// =============================================================================
//  GameManager.cs  ·  UnoX
//  Orquestador. Es el único MonoBehaviour que conoce a la vez la red, las reglas
//  y la presentación.
//
//  REGLA DE ORO DE ESTE ARCHIVO: nunca bloquear el hilo principal.
//  Toda espera es `await`. Nunca `.Result`, nunca `.Wait()`, nunca
//  `Thread.Sleep`. Un `await Task.Delay(...)` deja que Unity siga renderizando,
//  así que aunque el rival tarde 2 s en responder, el botón UNO sigue latiendo y
//  el fondo sigue animado.
//
//  FLUJO DE UNA JUGADA (lo que hace que no se note el lag):
//   1. El usuario suelta la carta -> la UI la despega de la mano AL INSTANTE
//      (predicción local, sin esperar red).
//   2. Se dispara PlayCardAsync en paralelo.
//   3a. Llega CardPlayed -> JuiceDirector reproduce el impacto completo.
//   3b. Llega error -> la carta vuelve a la mano con un "snap back" y un shake
//       corto. El usuario entiende que fue ilegal sin leer texto.
// =============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnoX.Core;
using UnoX.FX;
using UnoX.Net;
using UnoX.UI;

namespace UnoX
{
    public enum AppPhase
    {
        Boot,
        MainMenu,
        Lobby,
        InGame,
        RoundSummary,
        MatchSummary,
        Disconnected
    }

    [DisallowMultipleComponent]
    public sealed class GameManager : MonoBehaviour
    {
        [Header("Conexión")]
        [Tooltip("wss:// en producción (Render sirve TLS). ws:// sólo en local.")]
        [SerializeField] private string serverUrl = "wss://unox-server.onrender.com/ws";
        [SerializeField] private string playerName = "Jugador";
        [SerializeField] private float pingIntervalSeconds = 5f;

        [Header("Referencias")]
        [SerializeField] private CardHandLayout handLayout;
        [SerializeField] private JuiceDirector juice;
        [SerializeField] private UnoButtonView unoButton;

        public static GameManager Instance { get; private set; }

        public NetworkCardPlayer Net { get; private set; }
        public AppPhase Phase { get; private set; } = AppPhase.Boot;
        public int MySeat => Net?.MySeat ?? -1;
        public GameSnapshot State => Net?.Snapshot;
        public bool IsMyTurn => State != null && State.IsPlaying && State.currentSeat == MySeat;

        /// <summary>Reglas locales para el modo práctica offline.</summary>
        public RoomRules OfflineRules { get; private set; } = new RoomRules();

        // Eventos para que la UI se suscriba sin acoplarse a la red.
        public event Action<AppPhase> PhaseChanged;
        public event Action<GameEvent> OnGameEvent;
        public event Action<string> OnRejected;
        public event Action<LobbyRoom> OnLobbyUpdated;

        private CancellationTokenSource _sessionCts;
        private float _pingTimer;
        private TurnManager _offlineTurns;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            _sessionCts?.Cancel();
            Net?.Dispose();
            if (Instance == this) Instance = null;
        }

        // --------------------------------------------------------------- red --

        /// <summary>Conecta e identifica. Se llama desde el menú, con await.</summary>
        public async Task<bool> ConnectAsync()
        {
            _sessionCts?.Cancel();
            _sessionCts = new CancellationTokenSource();

            Net = new NetworkCardPlayer(serverUrl);
            Net.ConnectionChanged += HandleConnectionChanged;
            Net.LobbyUpdated += room => OnLobbyUpdated?.Invoke(room);
            Net.GameStarted += HandleGameStarted;
            Net.StateResynced += HandleResynced;
            Net.GameEventReceived += HandleGameEvent;
            Net.ServerError += err => OnRejected?.Invoke(err.code);
            Net.EmoteReceived += HandleEmote;

            var ok = await Net.ConnectAsync();
            if (!ok)
            {
                SetPhase(AppPhase.Disconnected);
                return false;
            }

            // Si ya teníamos token de una sesión anterior, intentamos recuperarla.
            var token = NetworkCardPlayer.LoadReconnectToken();
            if (!string.IsNullOrEmpty(token))
            {
                var restored = await Net.ReconnectAsync(token);
                if (restored != null && restored.reconnected)
                {
                    Debug.Log("[UNO-X] sesión recuperada tras el corte");
                    return true;
                }
            }

            await Net.HelloAsync(playerName);
            SetPhase(AppPhase.MainMenu);
            return true;
        }

        private void Update()
        {
            // Único punto donde los mensajes de red tocan la escena de Unity.
            Net?.Tick();

            if (Net != null && Net.IsConnected)
            {
                _pingTimer += Time.unscaledDeltaTime;
                if (_pingTimer >= pingIntervalSeconds)
                {
                    _pingTimer = 0f;
                    Net.SendPing();
                }
            }

            handLayout?.UpdateTimers(Time.unscaledDeltaTime);
            unoButton?.UpdatePulse(Time.unscaledDeltaTime);
        }

        private void HandleConnectionChanged(ConnectionState state)
        {
            switch (state)
            {
                case ConnectionState.Reconnecting:
                    juice?.PlayReconnecting();
                    break;
                case ConnectionState.Failed:
                    SetPhase(AppPhase.Disconnected);
                    break;
                case ConnectionState.Connected:
                    juice?.PlayReconnected();
                    break;
            }
        }

        // ------------------------------------------------------------- lobby --

        public void CreateRoom(RoomRules rules = null)
        {
            Net?.CreateRoom(rules ?? new RoomRules());
            SetPhase(AppPhase.Lobby);
        }

        public void JoinRoom(string code)
        {
            Net?.JoinRoom(code);
            SetPhase(AppPhase.Lobby);
        }

        public void SetReady(bool ready) => Net?.SetReady(ready);

        public void StartGame() => Net?.StartGame();

        // ----------------------------------------------------------- jugadas --

        /// <summary>
        /// Juega una carta. La UI la llama en el frame en que el usuario la suelta.
        /// Devuelve cuando el servidor responde; mientras tanto el juego sigue vivo.
        /// </summary>
        public async Task<bool> PlayCardAsync(Card card, CardColor? chosenColor = null, int swapTargetSeat = -1)
        {
            if (Net == null || !IsMyTurn) return false;

            // 1) Predicción visual inmediata: la carta sale de la mano YA.
            handLayout?.DetachForPlay(card.Id);

            // 2) Petición asíncrona. No se espera para animar.
            var result = await Net.PlayCardAsync(card.Id, chosenColor, swapTargetSeat);

            if (!result.Ok)
            {
                // 3b) Rechazo: la carta vuelve a la mano con feedback.
                handLayout?.SnapBack(card.Id);
                juice?.PlayRejected(result.ErrorCode);
                OnRejected?.Invoke(result.ErrorCode);
                return false;
            }
            return true;
        }

        /// <summary>Robar. Si hay castigo acumulado, el servidor aplica el total.</summary>
        public async Task<bool> DrawAsync()
        {
            if (Net == null || !IsMyTurn) return false;
            handLayout?.AnimateDrawIntent();
            var result = await Net.DrawAsync();
            if (!result.Ok)
            {
                juice?.PlayRejected(result.ErrorCode);
                return false;
            }
            return true;
        }

        public async Task<bool> PassAsync()
        {
            if (Net == null || !IsMyTurn) return false;
            var result = await Net.PassAsync();
            if (!result.Ok) juice?.PlayRejected(result.ErrorCode);
            return result.Ok;
        }

        /// <summary>
        /// Gritar "¡UNO!". Se envía en el MISMO frame del toque: la carrera la
        /// resuelve el servidor por orden de llegada, así que cada milisegundo
        /// de animación previa es una desventaja real.
        /// </summary>
        public async void CallUno()
        {
            if (Net == null) return;
            juice?.PlayUnoPressed();
            var result = await Net.CallUnoAsync();
            if (!result.Ok) juice?.PlayRejected(result.ErrorCode);
        }

        /// <summary>Pillar al rival que olvidó cantar UNO.</summary>
        public async void CatchUno()
        {
            if (Net == null) return;
            var result = await Net.CatchUnoAsync();
            if (result.Ok) juice?.PlayUnoCatch();
        }

        public async Task<bool> ChallengeAsync(bool accept)
        {
            if (Net == null) return false;
            var result = await Net.ChallengeAsync(accept);
            if (!result.Ok) juice?.PlayRejected(result.ErrorCode);
            return result.Ok;
        }

        public void SendEmote(string emoteId) => Net?.SendEmote(emoteId);

        // --------------------------------------------------------- reacciones --

        private void HandleGameStarted(GameSnapshot snapshot)
        {
            SetPhase(AppPhase.InGame);
            handLayout?.RebuildHand(snapshot);
            juice?.PlayRoundStart();
        }

        private void HandleResynced(GameSnapshot snapshot)
        {
            // Tras una reconexión o un hueco de secuencia, se reconstruye todo.
            handLayout?.RebuildHand(snapshot);
            juice?.PlayResync();
        }

        private void HandleGameEvent(GameEvent e)
        {
            OnGameEvent?.Invoke(e);

            switch (e.type)
            {
                case "card_played":
                    juice?.PlayCardPlayed(e.seat, e.card, e.seat == MySeat);
                    handLayout?.OnCardPlayed(e);
                    break;
                case "cards_drawn":
                    juice?.PlayCardsDrawn(e.seat, e.count);
                    handLayout?.OnCardsDrawn(e);
                    break;
                case "draw_penalty":
                    // Aquí es donde se ve el "3x": rayos hacia el penalizado.
                    juice?.PlayDrawPenalty(e.seat, e.amount, e.sourceSeat);
                    break;
                case "stacked":
                    juice?.PlayStacked(e.seat, e.amount);
                    break;
                case "color_changed":
                    juice?.PlayColorBurst(e.color ?? CardColor.Red);
                    break;
                case "direction_changed":
                    juice?.PlayDirectionFlip(e.direction);
                    break;
                case "uno_required":
                    unoButton?.Arm(e.seat == MySeat, e.deadline, Net?.ServerNowMs() ?? 0);
                    juice?.PlayUnoWindowOpened(e.seat == MySeat);
                    break;
                case "uno_called":
                    unoButton?.Disarm();
                    juice?.PlayUnoConfirmed(e.seat == MySeat);
                    break;
                case "uno_caught":
                    unoButton?.Disarm();
                    juice?.PlayUnoCaught(e.seat, e.timeout);
                    break;
                case "challenge_offer":
                    juice?.PlayChallengeOffered(e.targetSeat == MySeat);
                    break;
                case "challenge_result":
                    juice?.PlayChallengeResolved(e.bluffed, e.playerSeat);
                    break;
                case "turn_start":
                    handLayout?.OnTurnStart(e);
                    juice?.PlayTurnStart(e.seat == MySeat);
                    break;
                case "round_end":
                    SetPhase(AppPhase.RoundSummary);
                    juice?.PlayRoundEnd(e.winnerSeat == MySeat, e.gained);
                    break;
                case "match_end":
                    SetPhase(AppPhase.MatchSummary);
                    juice?.PlayMatchEnd(e.winnerSeat == MySeat);
                    break;
            }
        }

        private void HandleEmote(EmoteMsg msg) => juice?.PlayEmote(msg?.id);

        private void SetPhase(AppPhase next)
        {
            if (Phase == next) return;
            Phase = next;
            PhaseChanged?.Invoke(next);
        }

        // --------------------------------------------------- modo práctica --

        /// <summary>
        /// Modo offline contra la máquina. Usa TurnManager como autoridad local y
        /// una corutina para el ritmo: demuestra que la misma lógica de reglas
        /// funciona con y sin servidor.
        /// </summary>
        public IEnumerator StartOfflineMatch(int botCount = 1)
        {
            var deck = new DeckManager(Environment.TickCount);
            _offlineTurns = new TurnManager(botCount + 1, OfflineRules, deck)
            {
                Now = () => (long)(Time.realtimeSinceStartup * 1000f)
            };

            var events = _offlineTurns.StartRound(Environment.TickCount);
            foreach (var e in events) yield return StartCoroutine(AnimateOfflineEvent(e));

            while (_offlineTurns.Phase == TurnPhase.Playing ||
                   _offlineTurns.Phase == TurnPhase.AwaitingStackDecision)
            {
                if (_offlineTurns.CurrentSeat == 0)
                {
                    // Espera al humano sin bloquear.
                    yield return new WaitUntil(() => IsMyTurn || _offlineTurns.Phase != TurnPhase.Playing);
                }
                else
                {
                    yield return new WaitForSeconds(0.6f);
                    yield return StartCoroutine(BotPlay());
                }
            }
        }

        private IEnumerator BotPlay()
        {
            int seat = _offlineTurns.CurrentSeat;
            var hand = _offlineTurns.Hand(seat);
            var legal = RulesEngine.LegalPlays(hand, _offlineTurns.Top, _offlineTurns.TopColor, OfflineRules);

            List<TurnEvent> events = null;
            if (legal.Count > 0)
            {
                var card = legal[0];
                CardColor? chosen = card.IsWild ? CardColor.Blue : (CardColor?)null;
                events = _offlineTurns.PlayCard(seat, card.Id, chosen, out _);
            }
            if (events == null) events = _offlineTurns.Draw(seat, out _);
            if (events == null) yield break;

            foreach (var e in events) yield return StartCoroutine(AnimateOfflineEvent(e));
        }

        private IEnumerator AnimateOfflineEvent(TurnEvent e)
        {
            // Un frame por evento: suficiente para que la animación respire.
            yield return null;
            OnGameEvent?.Invoke(ToNetworkEvent(e));
        }

        /// <summary>Adapta un evento offline al formato de red para reusar la UI.</summary>
        private static GameEvent ToNetworkEvent(TurnEvent e)
        {
            var g = new GameEvent
            {
                seat = e.Seat,
                amount = e.Amount,
                direction = e.Direction,
                timeout = e.Timeout
            };
            switch (e.Kind)
            {
                case TurnEventKind.CardPlayed: g.type = "card_played"; g.card = new PublicCard { color = e.Card.Color, value = e.Card.Value }; break;
                case TurnEventKind.CardsDrawn: g.type = "cards_drawn"; g.count = e.Amount; break;
                case TurnEventKind.DrawPenalty: g.type = "draw_penalty"; break;
                case TurnEventKind.Stacked: g.type = "stacked"; break;
                case TurnEventKind.UnoRequired: g.type = "uno_required"; break;
                case TurnEventKind.UnoCalled: g.type = "uno_called"; break;
                case TurnEventKind.UnoCaught: g.type = "uno_caught"; break;
                case TurnEventKind.DirectionChanged: g.type = "direction_changed"; break;
                case TurnEventKind.TurnStart: g.type = "turn_start"; break;
                case TurnEventKind.RoundEnd: g.type = "round_end"; break;
                case TurnEventKind.MatchEnd: g.type = "match_end"; break;
                default: g.type = e.Kind.ToString().ToLowerInvariant(); break;
            }
            return g;
        }
    }
}
