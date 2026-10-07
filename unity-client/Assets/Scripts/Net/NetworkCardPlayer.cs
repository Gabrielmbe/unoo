// =============================================================================
//  NetworkCardPlayer.cs  ·  UnoX.Net
//  Fachada entre la UI y la red. Es el ÚNICO punto por el que salen las jugadas.
//
//  ASYNC/AWAIT CONTRA EL LAG
//  -------------------------
//  Cada acción pública es `async Task` y devuelve en cuanto el servidor responde.
//  Ninguna bloquea el hilo principal, así que la animación de la mano, el brillo
//  del botón UNO y el fondo siguen corriendo a 60 fps aunque el rival esté a
//  300 ms. La UI nunca hace `Result` ni `.Wait()`: eso es lo que congelaría el juego.
//
//  OPTIMISMO CONTROLADO
//  --------------------
//  La UI puede animar la carta saliendo de la mano en cuanto el usuario la suelta
//  (predicción local). Cuando llega `CardPlayed` del servidor se confirma; si el
//  servidor la rechaza, `Rejected` dispara la animación inversa y un feedback.
//  La autoridad es siempre del servidor: aquí sólo se decide CÓMO se ve.
//
//  SEGURIDAD
//  ---------
//  El cliente sólo envía `cardId` (opaco, asignado por el servidor) y el color
//  elegido. Nunca envía cartas, mazos ni su asiento: el servidor deriva el asiento
//  del token de conexión. Un cliente modificado no puede jugar cartas ajenas,
//  robar del mazo cuando no le toca, ni espiar la mano del rival (no viaja).
// =============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnoX.Core;

namespace UnoX.Net
{
    /// <summary>Resultado de una acción de red.</summary>
    public readonly struct NetResult
    {
        public readonly bool Ok;
        public readonly string ErrorCode;
        public readonly IReadOnlyList<GameEvent> Events;

        public NetResult(bool ok, string errorCode, IReadOnlyList<GameEvent> events)
        {
            Ok = ok;
            ErrorCode = errorCode;
            Events = events;
        }

        public static NetResult Success(IReadOnlyList<GameEvent> events) => new NetResult(true, null, events);
        public static NetResult Failure(string code) => new NetResult(false, code, Array.Empty<GameEvent>());
    }

    public sealed class NetworkCardPlayer : IDisposable
    {
        private readonly UnoWebSocketClient _client;
        private readonly Dictionary<long, TaskCompletionSource<NetResult>> _pending =
            new Dictionary<long, TaskCompletionSource<NetResult>>();
        private readonly System.Diagnostics.Stopwatch _clock = new System.Diagnostics.Stopwatch();
        private long _requestId;
        private int _lastSeq;
        private long _serverOffsetMs;   // serverTime - localTime
        private int _rttMs;

        public GameSnapshot Snapshot { get; private set; }
        public LobbyRoom Lobby { get; private set; }
        public string PlayerId { get; private set; }
        public string ReconnectToken { get; private set; }
        public int MySeat => Snapshot?.seat ?? -1;
        public bool IsConnected => _client.State == ConnectionState.Connected;
        public int RttMs => _rttMs;
        public ConnectionState State => _client.State;

        // ------------------------------------------------------- eventos UI ---

        public event Action<ConnectionState> ConnectionChanged;
        public event Action<LobbyRoom> LobbyUpdated;
        public event Action<GameSnapshot> GameStarted;
        public event Action<GameSnapshot> StateResynced;
        public event Action<GameEvent> GameEventReceived;
        public event Action<PeerStateMsg> PeerChanged;
        public event Action<EmoteMsg> EmoteReceived;
        public event Action<ErrorMsg> ServerError;
        public event Action<string> RoomClosedReason;

        /// <summary>Se lanza cuando el cliente detecta un hueco y pide resync.</summary>
        public event Action SequenceGapDetected;

        public NetworkCardPlayer(string serverUrl)
        {
            _clock.Start();
            _client = new UnoWebSocketClient(serverUrl);
            _client.OnMessage += HandleRawMessage;
            _client.OnStateChanged += s => ConnectionChanged?.Invoke(s);
        }

        // ------------------------------------------------------------ ciclo ---

        /// <summary>Llamar desde Update(). Mueve los mensajes al hilo principal.</summary>
        public void Tick() => _client.DrainIncoming();

        public Task<bool> ConnectAsync() => _client.ConnectAsync();

        /// <summary>
        /// Latido de reloj. Se manda cada ~5 s. Sirve para:
        ///  - estimar el offset del reloj del servidor (los countdowns de turno
        ///    deben coincidir en el PC y en el móvil aunque sus relojes difieran),
        ///  - estimar el RTT y ajustarlo al feedback de la UI,
        ///  - mantener vivo el socket frente a proxies y al spin-down de Render.
        /// </summary>
        public void SendPing()
        {
            long local = LocalNowMs();
            Send(new { t = C2S.Ping, c = local });
            _pendingPingLocal = local;
        }

        private long _pendingPingLocal;

        /// <summary>Epoch ms del servidor estimado desde el reloj local.</summary>
        public long ServerNowMs() => LocalNowMs() + _serverOffsetMs;

        private long LocalNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // -------------------------------------------------------- handshake ---

        public Task<WelcomeMsg> HelloAsync(string playerName, string avatarId = "default")
        {
            var tcs = new TaskCompletionSource<WelcomeMsg>(TaskCreationOptions.RunContinuationsAsynchronously);
            _welcomeWaiter = tcs;
            Send(new { t = C2S.Hello, name = playerName, avatarId });
            return tcs.Task;
        }

        private TaskCompletionSource<WelcomeMsg> _welcomeWaiter;

        /// <summary>Vuelve a entrar con el token tras un corte de red.</summary>
        public Task<WelcomeMsg> ReconnectAsync(string token)
        {
            var tcs = new TaskCompletionSource<WelcomeMsg>(TaskCreationOptions.RunContinuationsAsynchronously);
            _welcomeWaiter = tcs;
            Send(new { t = C2S.Reconnect, reconnectToken = token });
            return tcs.Task;
        }

        // ------------------------------------------------------------ lobby ---

        public void CreateRoom(RoomRules rules = null)
            => Send(new { t = C2S.CreateRoom, rules });

        public void JoinRoom(string code)
            => Send(new { t = C2S.JoinRoom, code = code?.Trim().ToUpperInvariant() });

        public void LeaveRoom() => Send(new { t = C2S.Leave });
        public void SetReady(bool ready) => Send(new { t = C2S.SetReady, ready });
        public void StartGame() => Send(new { t = C2S.StartGame });
        public void SendEmote(string emoteId) => Send(new { t = C2S.Emote, id = emoteId });

        // ---------------------------------------------------------- acciones --

        /// <summary>
        /// Juega una carta. El <paramref name="cardId"/> tiene que venir del
        /// snapshot: nunca se construye en el cliente.
        /// </summary>
        public Task<NetResult> PlayCardAsync(string cardId, CardColor? chosenColor = null, int swapTargetSeat = -1)
        {
            var payload = new Dictionary<string, object>
            {
                { "t", C2S.PlayCard },
                { "cardId", cardId }
            };
            if (chosenColor.HasValue) payload["chosenColor"] = (int)chosenColor.Value;
            if (swapTargetSeat >= 0) payload["swapTargetSeat"] = swapTargetSeat;
            return RequestAsync(payload);
        }

        public Task<NetResult> DrawAsync() => RequestAsync(new Dictionary<string, object> { { "t", C2S.Draw } });
        public Task<NetResult> PassAsync() => RequestAsync(new Dictionary<string, object> { { "t", C2S.Pass } });

        /// <summary>
        /// Gritar "¡UNO!". El servidor resuelve la carrera por orden de llegada:
        /// el paquete que entra primero gana. Por eso el botón debe ser reactivo y
        /// enviar en el mismo frame del toque, sin esperar a la animación.
        /// </summary>
        public Task<NetResult> CallUnoAsync() => RequestAsync(new Dictionary<string, object> { { "t", C2S.CallUno } });

        public Task<NetResult> CatchUnoAsync() => RequestAsync(new Dictionary<string, object> { { "t", C2S.CatchUno } });

        public Task<NetResult> ChallengeAsync(bool accept)
            => RequestAsync(new Dictionary<string, object> { { "t", C2S.Challenge }, { "accept", accept } });

        public void RequestResync() => Send(new { t = C2S.Resync });

        // -------------------------------------------------------- plumbing ---

        /// <summary>
        /// Envía y espera la respuesta del servidor (eventos o error) sin bloquear.
        /// El timeout evita que un paquete perdido deje una Task colgada para siempre.
        /// </summary>
        private Task<NetResult> RequestAsync(object payload)
        {
            var tcs = new TaskCompletionSource<NetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            long id = Interlocked.Increment(ref _requestId);
            _pending[id] = tcs;
            Send(payload);

            _ = TimeoutAsync(id, 8000);
            return tcs.Task;
        }

        private async Task TimeoutAsync(long id, int ms)
        {
            await Task.Delay(ms).ConfigureAwait(false);
            if (_pending.TryGetValue(id, out var tcs))
            {
                _pending.Remove(id);
                tcs.TrySetResult(NetResult.Failure("TIMEOUT"));
            }
        }

        private void Send(object payload) => _client.Send(Json.ToJson(payload));

        private void HandleRawMessage(string raw)
        {
            Envelope env;
            try
            {
                env = Json.FromJson<Envelope>(raw);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UNO-X] JSON no parseable: {ex.Message}");
                return;
            }
            if (env == null || string.IsNullOrEmpty(env.t)) return;

            switch (env.t)
            {
                case S2C.Welcome: OnWelcome(Json.FromJson<WelcomeMsg>(raw)); break;
                case S2C.LobbyState: OnLobby(Json.FromJson<LobbyStateMsg>(raw)); break;
                case S2C.GameStart: OnGameStart(Json.FromJson<GameStartMsg>(raw)); break;
                case S2C.Snapshot: OnSnapshot(Json.FromJson<SnapshotMsg>(raw)); break;
                case S2C.Event: OnEvents(new[] { Json.FromJson<EventMsg>(raw).e }); break;
                case S2C.Batch: OnEvents(Json.FromJson<BatchMsg>(raw).e); break;
                case S2C.Error: OnError(Json.FromJson<ErrorMsg>(raw)); break;
                case S2C.Pong: OnPong(Json.FromJson<PongMsg>(raw)); break;
                case S2C.PeerState: PeerChanged?.Invoke(Json.FromJson<PeerStateMsg>(raw)); break;
                case S2C.Emote: EmoteReceived?.Invoke(Json.FromJson<EmoteMsg>(raw)); break;
                case S2C.RoomClosed: RoomClosedReason?.Invoke(Json.FromJson<RoomClosedMsg>(raw)?.reason); break;
                default: Debug.Log($"[UNO-X] mensaje sin handler: {env.t}"); break;
            }
        }

        private void OnWelcome(WelcomeMsg msg)
        {
            if (msg == null) return;
            PlayerId = msg.playerId;
            ReconnectToken = msg.reconnectToken;
            // Estimación simple: offset = serverTime - localTime (ignorando el RTT/2,
            // que se refina con cada pong).
            _serverOffsetMs = msg.serverTime - LocalNowMs();
            PersistReconnectToken(msg.reconnectToken);
            _welcomeWaiter?.TrySetResult(msg);
            _welcomeWaiter = null;
        }

        private void OnPong(PongMsg msg)
        {
            if (msg == null) return;
            long local = LocalNowMs();
            _rttMs = (int)Math.Max(0, local - msg.c);
            // Ajuste NTP simplificado: asumimos simetría de ida y vuelta.
            _serverOffsetMs = msg.serverTime + _rttMs / 2 - local;
        }

        private void OnLobby(LobbyStateMsg msg)
        {
            if (msg?.room == null) return;
            Lobby = msg.room;
            LobbyUpdated?.Invoke(msg.room);
        }

        private void OnGameStart(GameStartMsg msg)
        {
            if (msg?.game == null) return;
            Snapshot = msg.game;
            _lastSeq = msg.game.lastSeq;
            GameStarted?.Invoke(msg.game);
        }

        private void OnSnapshot(SnapshotMsg msg)
        {
            if (msg?.game == null) return;
            Snapshot = msg.game;
            _lastSeq = msg.game.lastSeq;
            StateResynced?.Invoke(msg.game);
        }

        private void OnEvents(IReadOnlyList<GameEvent> events)
        {
            if (events == null) return;
            foreach (var e in events)
            {
                if (e == null) continue;
                // Detección de huecos: si nos perdimos algo, el estado local ya no
                // es fiable y hay que pedir el snapshot completo.
                if (_lastSeq > 0 && e.seq > _lastSeq + 1)
                {
                    Debug.LogWarning($"[UNO-X] hueco en la secuencia: {_lastSeq} -> {e.seq}. Pidiendo resync.");
                    SequenceGapDetected?.Invoke();
                    RequestResync();
                }
                _lastSeq = Math.Max(_lastSeq, e.seq);

                ApplyToSnapshot(e);
                GameEventReceived?.Invoke(e);
            }
            CompletePending(events);
        }

        private void OnError(ErrorMsg msg)
        {
            if (msg == null) return;
            ServerError?.Invoke(msg);
            // Un error cierra la petición en vuelo más antigua.
            CompletePendingWithError(msg.code);
        }

        /// <summary>
        /// Aplica un evento al snapshot local. No es la autoridad: es lo justo para
        /// que la UI pueda reaccionar antes de que llegue el snapshot del servidor.
        /// </summary>
        private void ApplyToSnapshot(GameEvent e)
        {
            if (Snapshot == null) return;

            switch (e.type)
            {
                case "card_played":
                    if (e.handSizes != null) ApplyHandSizes(e.handSizes);
                    if (e.card.HasValue)
                    {
                        Snapshot.topCard = e.card;
                        if (e.chosenColor.HasValue) Snapshot.topColor = e.chosenColor.Value;
                        else if (e.card.Value.color != CardColor.Wild) Snapshot.topColor = e.card.Value.color;
                    }
                    break;
                case "cards_drawn":
                    if (e.handSizes != null) ApplyHandSizes(e.handSizes);
                    if (e.handSize.HasValue && e.seat == MySeat && e.cards != null)
                    {
                        var me = Snapshot.players[MySeat];
                        if (me.hand == null) me.hand = new List<HandCard>();
                        foreach (var c in e.cards)
                            me.hand.Add(new HandCard { id = "", color = c.color, value = c.value });
                    }
                    break;
                case "turn_start":
                    Snapshot.currentSeat = e.seat;
                    Snapshot.turnIndex = e.turnIndex;
                    Snapshot.direction = e.direction;
                    Snapshot.turnDeadline = e.deadline;
                    break;
                case "direction_changed":
                    Snapshot.direction = e.direction;
                    break;
                case "round_end":
                    if (e.scores != null)
                        foreach (var s in e.scores)
                            if (s.seat < Snapshot.players.Count) Snapshot.players[s.seat].score = s.score;
                    break;
            }
        }

        private void ApplyHandSizes(List<int> sizes)
        {
            for (int i = 0; i < sizes.Count && i < Snapshot.players.Count; i++)
                Snapshot.players[i].handSize = sizes[i];
        }

        private void CompletePending(IReadOnlyList<GameEvent> events)
        {
            if (_pending.Count == 0) return;
            // FIFO: en un juego por turnos sólo hay una petición en vuelo por cliente.
            long oldest = long.MaxValue;
            TaskCompletionSource<NetResult> tcs = null;
            foreach (var kv in _pending)
                if (kv.Key < oldest) { oldest = kv.Key; tcs = kv.Value; }
            if (tcs == null) return;
            _pending.Remove(oldest);
            tcs.TrySetResult(NetResult.Success(events));
        }

        private void CompletePendingWithError(string code)
        {
            if (_pending.Count == 0) return;
            long oldest = long.MaxValue;
            TaskCompletionSource<NetResult> tcs = null;
            foreach (var kv in _pending)
                if (kv.Key < oldest) { oldest = kv.Key; tcs = kv.Value; }
            if (tcs == null) return;
            _pending.Remove(oldest);
            tcs.TrySetResult(NetResult.Failure(code));
        }

        /// <summary>
        /// Persistencia del token. Con PlayerPrefs basta: si se pierde, el jugador
        /// vuelve a entrar con el código de sala, que es un coste asumible.
        /// </summary>
        private static void PersistReconnectToken(string token)
        {
            try
            {
                PlayerPrefs.SetString("unox.reconnectToken", token);
                PlayerPrefs.Save();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UNO-X] no se pudo guardar el token: {ex.Message}");
            }
        }

        public static string LoadReconnectToken()
        {
            try
            {
                return PlayerPrefs.GetString("unox.reconnectToken", null);
            }
            catch
            {
                return null;
            }
        }

        public void Dispose() => _client.Dispose();
    }
}
