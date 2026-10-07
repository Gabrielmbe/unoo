// =============================================================================
//  TurnManager.cs  ·  UnoX.Core
//  Máquina de estados del turno: sentido horario/antihorario, Reverse, Skip y
//  ACUMULACIÓN DE CASTIGOS (+2 sobre +2, +4 sobre +4).
//
//  Doble uso, deliberado:
//   1. MODO PRÁCTICA (offline): es la autoridad local, no hay servidor.
//   2. MODO ONLINE: corre como PREDICCIÓN. El cliente aplica su jugada al vuelo
//      para animar sin esperar, y cuando llega el evento del servidor hace
//      `Reconcile()`. Si el servidor discrepa, gana el servidor y se corrige.
//      Esto es lo que hace que el juego no se sienta "congelado" con 200 ms de
//      ping entre el PC y el móvil.
//
//  Todo es síncrono y sin I/O: no hay nada que pueda bloquear el hilo principal.
//  Las esperas (animaciones, timeouts) viven en GameManager como async/await.
// =============================================================================

using System;
using System.Collections.Generic;

namespace UnoX.Core
{
    public enum TurnPhase
    {
        Idle,
        Playing,
        AwaitingStackDecision,
        AwaitingChallenge,
        RoundOver,
        MatchOver
    }

    /// <summary>Castigo acumulado que viaja al siguiente jugador.</summary>
    public sealed class PendingPenalty
    {
        public int Amount;
        public Card SourceCard;
        public int SourceSeat;
        public int TargetSeat;
        public readonly List<int> ChainSeats = new List<int>();

        public PendingPenalty(int amount, Card source, int sourceSeat, int targetSeat)
        {
            Amount = amount;
            SourceCard = source;
            SourceSeat = sourceSeat;
            TargetSeat = targetSeat;
            ChainSeats.Add(sourceSeat);
        }
    }

    /// <summary>Qué ocurrió tras una acción: el cliente lo traduce a animaciones.</summary>
    public enum TurnEventKind
    {
        CardPlayed,
        ColorChanged,
        DirectionChanged,
        SeatSkipped,
        DrawPenalty,
        CardsDrawn,
        StackOpened,
        Stacked,
        UnoRequired,
        UnoCalled,
        UnoCaught,
        ChallengeOffer,
        ChallengeResult,
        HandsSwapped,
        HandsRotated,
        DeckReshuffled,
        TurnStart,
        Pass,
        RoundEnd,
        MatchEnd
    }

    public sealed class TurnEvent
    {
        public TurnEventKind Kind;
        public int Seat;
        public int OtherSeat = -1;
        public int Amount;
        public Card Card;
        public CardColor Color;
        public int Direction;
        public List<Card> Cards;
        public bool Timeout;

        public override string ToString() => $"{Kind}(seat={Seat}, amount={Amount})";
    }

    /// <summary>
    /// Gestor de turno. Un único objeto por partida; no guarda referencias a
    /// UnityEngine, así que se puede instanciar en un test o en un worker.
    /// </summary>
    public sealed class TurnManager
    {
        private readonly RoomRules _rules;
        private readonly DeckManager _deck;
        private readonly List<List<Card>> _hands;
        private readonly List<int> _scores;
        private readonly List<bool> _unoDeclared;

        public TurnPhase Phase { get; private set; } = TurnPhase.Idle;
        public int PlayerCount => _hands.Count;
        public int CurrentSeat { get; private set; }
        public int Direction { get; private set; } = RulesEngine.Clockwise;
        public int TurnIndex { get; private set; }
        public int Round { get; private set; }
        public Card Top => _deck.Top;
        public CardColor TopColor { get; private set; } = CardColor.Red;
        public CardColor PreWildColor { get; private set; } = CardColor.Red;
        public PendingPenalty Pending { get; private set; }
        public int DrawnThisTurn { get; private set; }
        public RoomRules Rules => _rules;

        /// <summary>Ventana abierta para cantar UNO (asiento + tiempo límite en ms).</summary>
        public int UnoWindowSeat { get; private set; } = -1;
        public long UnoWindowDeadline { get; private set; }
        public int ChallengePlayerSeat { get; private set; } = -1;
        public int ChallengeTargetSeat { get; private set; } = -1;
        public long ChallengeDeadline { get; private set; }

        /// <summary>Reloj inyectable para que los tests controlen el tiempo.</summary>
        public Func<long> Now { get; set; } = () => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        public TurnManager(int playerCount, RoomRules rules, DeckManager deck)
        {
            if (playerCount < 2 || playerCount > 4)
                throw new ArgumentOutOfRangeException(nameof(playerCount), "UNO admite de 2 a 4 jugadores");
            _rules = rules ?? new RoomRules();
            _deck = deck ?? throw new ArgumentNullException(nameof(deck));
            _hands = new List<List<Card>>(playerCount);
            _scores = new List<int>(playerCount);
            _unoDeclared = new List<bool>(playerCount);
            for (int i = 0; i < playerCount; i++)
            {
                _hands.Add(new List<Card>());
                _scores.Add(0);
                _unoDeclared.Add(false);
            }
        }

        public IReadOnlyList<Card> Hand(int seat) => _hands[seat];
        public int HandSize(int seat) => _hands[seat].Count;
        public int Score(int seat) => _scores[seat];
        public bool IsMyTurn(int seat) => Phase == TurnPhase.Playing && CurrentSeat == seat;

        // ------------------------------------------------------------- setup --

        /// <summary>Reparte y arranca la ronda.</summary>
        public List<TurnEvent> StartRound(int seed, int dealerSeat = 0)
        {
            var events = new List<TurnEvent>();
            Round++;
            Phase = TurnPhase.Playing;
            Pending = null;
            UnoWindowSeat = -1;
            ChallengePlayerSeat = -1;
            DrawnThisTurn = 0;
            Direction = RulesEngine.Clockwise;
            TurnIndex = 0;

            _deck.Reset(seed);
            foreach (var h in _hands) h.Clear();
            for (int i = 0; i < _unoDeclared.Count; i++) _unoDeclared[i] = false;

            for (int i = 0; i < _rules.handSize; i++)
                foreach (var h in _hands)
                {
                    var c = _deck.DrawOne();
                    if (c.HasValue) h.Add(c.Value);
                }

            var starter = _deck.FlipStarter();
            TopColor = starter.Color;
            PreWildColor = starter.Color;
            CurrentSeat = RulesEngine.AdvanceSeat(dealerSeat % PlayerCount, 1, Direction, PlayerCount);

            ApplyStarterEffect(starter, events);
            if (Phase == TurnPhase.Playing) OpenTurn(events);
            return events;
        }

        private void ApplyStarterEffect(Card card, List<TurnEvent> events)
        {
            int first = CurrentSeat;
            if (card.Value == CardValue.Skip)
            {
                events.Add(new TurnEvent { Kind = TurnEventKind.SeatSkipped, Seat = first });
                if (PlayerCount > 2)
                    CurrentSeat = RulesEngine.AdvanceSeat(first, 1, Direction, PlayerCount);
            }
            else if (card.Value == CardValue.Reverse)
            {
                Direction = -Direction;
                events.Add(new TurnEvent { Kind = TurnEventKind.DirectionChanged, Direction = Direction });
            }
            else if (card.Value == CardValue.DrawTwo)
            {
                events.Add(new TurnEvent { Kind = TurnEventKind.DrawPenalty, Seat = first, Amount = 2 });
                DoDraw(first, 2, events);
                CurrentSeat = RulesEngine.AdvanceSeat(first, 1, Direction, PlayerCount);
            }
        }

        // -------------------------------------------------------------- play --

        /// <summary>
        /// Intenta jugar una carta. Devuelve los eventos generados; si la jugada
        /// es ilegal devuelve null y rellena <paramref name="error"/>.
        /// </summary>
        public List<TurnEvent> PlayCard(
            int seat, string cardId, CardColor? chosenColor, out PlayVerdict error, int swapTargetSeat = -1)
        {
            error = PlayVerdict.Legal;
            if (Phase != TurnPhase.Playing)
            {
                error = PlayVerdict.NoSuchCard;
                return null;
            }
            if (seat != CurrentSeat)
            {
                error = PlayVerdict.NoSuchCard;
                return null;
            }

            var hand = _hands[seat];
            int idx = hand.FindIndex(c => c.Id == cardId);
            if (idx < 0)
            {
                error = PlayVerdict.NoSuchCard;
                return null;
            }
            var card = hand[idx];

            bool isStack = Pending != null &&
                           Pending.TargetSeat == seat &&
                           _rules.stacking &&
                           RulesEngine.CanStackOn(card, Pending.SourceCard);

            if (!isStack)
            {
                if (Pending != null && Pending.TargetSeat == seat && !_rules.stacking)
                {
                    error = PlayVerdict.MustDraw;
                    return null;
                }
                var check = RulesEngine.CanPlay(card, Top, TopColor, hand, _rules);
                if (!check.Legal)
                {
                    error = check.Verdict;
                    return null;
                }
            }

            if (card.IsWild && !chosenColor.HasValue)
            {
                error = PlayVerdict.ColorRequired;
                return null;
            }
            if (card.IsWild && (chosenColor < CardColor.Red || chosenColor > CardColor.Blue))
            {
                error = PlayVerdict.ColorRequired;
                return null;
            }

            if (_rules.sevenZero && card.Value == CardValue.Seven)
            {
                if (swapTargetSeat < 0 || swapTargetSeat >= PlayerCount || swapTargetSeat == seat)
                {
                    error = PlayVerdict.BadSwapTarget;
                    return null;
                }
            }

            var events = new List<TurnEvent>();

            // --- mutación: la carta sale de la mano al descarte ---
            hand.RemoveAt(idx);
            _deck.Discard(card);
            PreWildColor = TopColor;
            TopColor = card.IsWild ? chosenColor.Value : card.Color;
            DrawnThisTurn = 0;

            events.Add(new TurnEvent { Kind = TurnEventKind.CardPlayed, Seat = seat, Card = card });
            if (card.IsWild)
                events.Add(new TurnEvent { Kind = TurnEventKind.ColorChanged, Seat = seat, Color = TopColor });

            CloseUnoWindow(seat, events);

            // --- fin de ronda ---
            if (hand.Count == 0)
            {
                FinishRound(seat, card, events);
                return events;
            }

            // --- aviso de UNO ---
            if (hand.Count == 1)
            {
                if (_unoDeclared[seat])
                {
                    _unoDeclared[seat] = false;
                    events.Add(new TurnEvent { Kind = TurnEventKind.UnoCalled, Seat = seat });
                }
                else
                {
                    UnoWindowSeat = seat;
                    UnoWindowDeadline = Now() + _rules.unoWindowMs;
                    events.Add(new TurnEvent { Kind = TurnEventKind.UnoRequired, Seat = seat });
                }
            }

            // --- apilado de castigo ---
            if (isStack)
            {
                Pending.Amount += card.DrawAmount;
                Pending.SourceCard = card;
                Pending.ChainSeats.Add(seat);
                Pending.TargetSeat = RulesEngine.AdvanceSeat(seat, 1, Direction, PlayerCount);
                CurrentSeat = Pending.TargetSeat;
                events.Add(new TurnEvent
                {
                    Kind = TurnEventKind.Stacked,
                    Seat = seat,
                    Card = card,
                    Amount = Pending.Amount,
                    OtherSeat = Pending.TargetSeat
                });
                OpenTurn(events);
                return events;
            }

            // --- house rule 7-0 ---
            if (_rules.sevenZero && card.Value == CardValue.Seven)
            {
                var tmp = _hands[seat];
                _hands[seat] = _hands[swapTargetSeat];
                _hands[swapTargetSeat] = tmp;
                events.Add(new TurnEvent
                {
                    Kind = TurnEventKind.HandsSwapped,
                    Seat = seat,
                    OtherSeat = swapTargetSeat
                });
            }
            else if (_rules.sevenZero && card.Value == CardValue.Zero)
            {
                RotateHands(Direction);
                events.Add(new TurnEvent { Kind = TurnEventKind.HandsRotated, Direction = Direction });
            }

            ApplyCardEffect(card, seat, events);
            // OpenTurn ya se autolimita a las fases en las que tiene sentido.
            OpenTurn(events);
            return events;
        }

        private void ApplyCardEffect(Card card, int seat, List<TurnEvent> events)
        {
            int nextSeat = RulesEngine.AdvanceSeat(seat, 1, Direction, PlayerCount);

            if (card.IsDrawCard)
            {
                bool canStackNow = _rules.stacking &&
                                   RulesEngine.StackablePlays(_hands[nextSeat], card).Count > 0;

                if (card.Value == CardValue.WildDrawFour && _rules.wild4Challenge)
                {
                    Pending = new PendingPenalty(card.DrawAmount, card, seat, nextSeat);
                    ChallengePlayerSeat = seat;
                    ChallengeTargetSeat = nextSeat;
                    ChallengeDeadline = Now() + _rules.challengeWindowMs;
                    Phase = TurnPhase.AwaitingChallenge;
                    events.Add(new TurnEvent
                    {
                        Kind = TurnEventKind.ChallengeOffer,
                        Seat = seat,
                        OtherSeat = nextSeat
                    });
                    return;
                }

                if (canStackNow)
                {
                    Pending = new PendingPenalty(card.DrawAmount, card, seat, nextSeat);
                    CurrentSeat = nextSeat;
                    Phase = TurnPhase.AwaitingStackDecision;
                    events.Add(new TurnEvent
                    {
                        Kind = TurnEventKind.StackOpened,
                        Seat = seat,
                        OtherSeat = nextSeat,
                        Amount = Pending.Amount,
                        Card = card
                    });
                    return;
                }

                events.Add(new TurnEvent
                {
                    Kind = TurnEventKind.DrawPenalty,
                    Seat = nextSeat,
                    OtherSeat = seat,
                    Amount = card.DrawAmount
                });
                DoDraw(nextSeat, card.DrawAmount, events);

                var r = RulesEngine.ResolveSeatAfterPlay(card.Value, seat, Direction, PlayerCount);
                Direction = r.Direction;
                CurrentSeat = r.NextSeat;
                return;
            }

            var res = RulesEngine.ResolveSeatAfterPlay(card.Value, seat, Direction, PlayerCount);
            if (card.Value == CardValue.Skip)
                events.Add(new TurnEvent
                {
                    Kind = TurnEventKind.SeatSkipped,
                    Seat = RulesEngine.AdvanceSeat(seat, 1, Direction, PlayerCount),
                    OtherSeat = seat
                });
            if (card.Value == CardValue.Reverse)
                events.Add(new TurnEvent { Kind = TurnEventKind.DirectionChanged, Seat = seat, Direction = res.Direction });

            Direction = res.Direction;
            CurrentSeat = res.NextSeat;
        }

        // -------------------------------------------------------------- draw --

        /// <summary>Robar. Con castigo pendiente traga todo el acumulado y pierde el turno.</summary>
        public List<TurnEvent> Draw(int seat, out PlayVerdict error)
        {
            error = PlayVerdict.Legal;
            if (Phase != TurnPhase.Playing && Phase != TurnPhase.AwaitingStackDecision)
            {
                error = PlayVerdict.NoSuchCard;
                return null;
            }
            if (seat != CurrentSeat)
            {
                error = PlayVerdict.NoSuchCard;
                return null;
            }

            var events = new List<TurnEvent>();

            if (Pending != null && Pending.TargetSeat == seat)
            {
                int amount = Pending.Amount;
                events.Add(new TurnEvent
                {
                    Kind = TurnEventKind.DrawPenalty,
                    Seat = seat,
                    OtherSeat = Pending.SourceSeat,
                    Amount = amount
                });
                DoDraw(seat, amount, events);
                Pending = null;
                Phase = TurnPhase.Playing;
                CurrentSeat = RulesEngine.AdvanceSeat(seat, 1, Direction, PlayerCount);
                DrawnThisTurn = 0;
                OpenTurn(events);
                return events;
            }

            int playable = RulesEngine.LegalPlays(_hands[seat], Top, TopColor, _rules).Count;
            if (DrawnThisTurn > 0 && playable > 0)
            {
                // Ya robó y ahora puede jugar: debe jugar o pasar.
                error = PlayVerdict.MustDraw;
                return null;
            }

            DoDraw(seat, 1, events);
            DrawnThisTurn++;

            if (!_rules.drawUntilPlayable)
            {
                CurrentSeat = RulesEngine.AdvanceSeat(seat, 1, Direction, PlayerCount);
                DrawnThisTurn = 0;
                OpenTurn(events);
                return events;
            }

            bool canNow = RulesEngine.LegalPlays(_hands[seat], Top, TopColor, _rules).Count > 0;
            if (!canNow && _deck.DrawCount == 0)
            {
                CurrentSeat = RulesEngine.AdvanceSeat(seat, 1, Direction, PlayerCount);
                DrawnThisTurn = 0;
                OpenTurn(events);
            }
            return events;
        }

        /// <summary>Pasar tras robar (sólo si de verdad no puede jugar).</summary>
        public List<TurnEvent> Pass(int seat, out PlayVerdict error)
        {
            error = PlayVerdict.Legal;
            if (Phase != TurnPhase.Playing || seat != CurrentSeat || DrawnThisTurn <= 0)
            {
                error = PlayVerdict.MustDraw;
                return null;
            }
            if (Pending != null && Pending.TargetSeat == seat)
            {
                error = PlayVerdict.MustDraw;
                return null;
            }
            if (!_rules.allowPassWithPlay &&
                RulesEngine.LegalPlays(_hands[seat], Top, TopColor, _rules).Count > 0)
            {
                error = PlayVerdict.MustDraw;
                return null;
            }

            var events = new List<TurnEvent> { new TurnEvent { Kind = TurnEventKind.Pass, Seat = seat } };
            CurrentSeat = RulesEngine.AdvanceSeat(seat, 1, Direction, PlayerCount);
            DrawnThisTurn = 0;
            OpenTurn(events);
            return events;
        }

        // --------------------------------------------------------------- uno --

        /// <summary>Cantar "¡UNO!". Devuelve true si llegó dentro de la ventana.</summary>
        public List<TurnEvent> CallUno(int seat)
        {
            var events = new List<TurnEvent>();
            if (UnoWindowSeat == seat && Now() <= UnoWindowDeadline)
            {
                UnoWindowSeat = -1;
                events.Add(new TurnEvent { Kind = TurnEventKind.UnoCalled, Seat = seat });
                return events;
            }
            // Declaración anticipada: tienes 2 cartas y vas a bajar la penúltima.
            if (_hands[seat].Count == 2 && seat == CurrentSeat)
            {
                _unoDeclared[seat] = true;
                events.Add(new TurnEvent { Kind = TurnEventKind.UnoCalled, Seat = seat });
                return events;
            }
            return null;
        }

        /// <summary>El rival pilla al que olvidó cantar UNO.</summary>
        public List<TurnEvent> CatchUno(int seat)
        {
            if (UnoWindowSeat < 0 || UnoWindowSeat == seat || Now() > UnoWindowDeadline) return null;
            int victim = UnoWindowSeat;
            UnoWindowSeat = -1;
            var events = new List<TurnEvent>
            {
                new TurnEvent { Kind = TurnEventKind.UnoCaught, Seat = victim, OtherSeat = seat }
            };
            DoDraw(victim, RulesEngine.UnoPenaltyDraw, events);
            return events;
        }

        // --------------------------------------------------------- challenge --

        public List<TurnEvent> ResolveChallenge(int seat, bool accept)
        {
            if (Phase != TurnPhase.AwaitingChallenge || seat != ChallengeTargetSeat) return null;

            int culprit = ChallengePlayerSeat;
            int victim = ChallengeTargetSeat;
            int amount = Pending?.Amount ?? 4;
            bool bluffed = RulesEngine.HoldsColor(_hands[culprit], PreWildColor);

            Phase = TurnPhase.Playing;
            ChallengePlayerSeat = -1;
            ChallengeTargetSeat = -1;

            var events = new List<TurnEvent>
            {
                new TurnEvent { Kind = TurnEventKind.ChallengeResult, Seat = culprit, OtherSeat = victim, Amount = accept && bluffed ? amount : (accept ? amount + RulesEngine.FailedChallengeExtra : amount) }
            };

            if (accept && bluffed)
            {
                DoDraw(culprit, amount, events);
                Pending = null;
                CurrentSeat = victim;
            }
            else if (accept)
            {
                DoDraw(victim, amount + RulesEngine.FailedChallengeExtra, events);
                Pending = null;
                CurrentSeat = RulesEngine.AdvanceSeat(victim, 1, Direction, PlayerCount);
            }
            else
            {
                DoDraw(victim, amount, events);
                Pending = null;
                CurrentSeat = RulesEngine.AdvanceSeat(victim, 1, Direction, PlayerCount);
            }

            OpenTurn(events);
            return events;
        }

        // -------------------------------------------------------------- tick --

        /// <summary>Reloj: cierra la ventana de UNO y expira el reto. Sin I/O.</summary>
        public List<TurnEvent> Tick(long now)
        {
            var events = new List<TurnEvent>();
            if (Phase == TurnPhase.RoundOver || Phase == TurnPhase.MatchOver) return events;

            if (UnoWindowSeat >= 0 && now > UnoWindowDeadline)
            {
                int victim = UnoWindowSeat;
                UnoWindowSeat = -1;
                events.Add(new TurnEvent { Kind = TurnEventKind.UnoCaught, Seat = victim, Timeout = true });
                DoDraw(victim, RulesEngine.UnoPenaltyDraw, events);
            }

            if (Phase == TurnPhase.AwaitingChallenge && now > ChallengeDeadline)
            {
                var resolved = ResolveChallenge(ChallengeTargetSeat, false);
                if (resolved != null) events.AddRange(resolved);
            }

            return events;
        }

        // ----------------------------------------------------------- helpers --

        private void OpenTurn(List<TurnEvent> events)
        {
            if (Phase != TurnPhase.Playing && Phase != TurnPhase.AwaitingStackDecision) return;
            TurnIndex++;
            events.Add(new TurnEvent
            {
                Kind = TurnEventKind.TurnStart,
                Seat = CurrentSeat,
                Direction = Direction,
                Amount = Pending?.Amount ?? 0
            });
        }

        private void CloseUnoWindow(int actingSeat, List<TurnEvent> events)
        {
            if (UnoWindowSeat < 0) return;
            int victim = UnoWindowSeat;
            if (victim == actingSeat) return; // no se castiga a sí mismo
            UnoWindowSeat = -1;
            events.Add(new TurnEvent { Kind = TurnEventKind.UnoCaught, Seat = victim, OtherSeat = actingSeat });
            DoDraw(victim, RulesEngine.UnoPenaltyDraw, events);
        }

        private void DoDraw(int seat, int amount, List<TurnEvent> events)
        {
            var drawn = _deck.Draw(amount);
            _hands[seat].AddRange(drawn);
            _unoDeclared[seat] = false;
            if (drawn.Count < amount)
                events.Add(new TurnEvent { Kind = TurnEventKind.DeckReshuffled });
            events.Add(new TurnEvent
            {
                Kind = TurnEventKind.CardsDrawn,
                Seat = seat,
                Amount = drawn.Count,
                Cards = drawn
            });
        }

        private void RotateHands(int direction)
        {
            int n = _hands.Count;
            var rotated = new List<Card>[n];
            for (int i = 0; i < n; i++)
            {
                int to = direction >= 0 ? (i + 1) % n : (i - 1 + n) % n;
                rotated[to] = _hands[i];
            }
            for (int i = 0; i < n; i++) _hands[i] = rotated[i];
        }

        private void FinishRound(int winnerSeat, Card lastCard, List<TurnEvent> events)
        {
            int nextSeat = RulesEngine.AdvanceSeat(winnerSeat, 1, Direction, PlayerCount);

            // Regla oficial: si la última carta es +2/+4, la víctima roba ANTES de puntuar.
            if (lastCard.IsDrawCard)
            {
                events.Add(new TurnEvent
                {
                    Kind = TurnEventKind.DrawPenalty,
                    Seat = nextSeat,
                    OtherSeat = winnerSeat,
                    Amount = lastCard.DrawAmount
                });
                DoDraw(nextSeat, lastCard.DrawAmount, events);
            }

            var hands = new List<IReadOnlyList<Card>>(_hands.Count);
            foreach (var h in _hands) hands.Add(h);
            int gained = RulesEngine.SettleRound(hands, winnerSeat);
            _scores[winnerSeat] += gained;

            Phase = TurnPhase.RoundOver;
            UnoWindowSeat = -1;
            events.Add(new TurnEvent { Kind = TurnEventKind.RoundEnd, Seat = winnerSeat, Amount = gained });

            if (_scores[winnerSeat] >= _rules.targetScore)
            {
                Phase = TurnPhase.MatchOver;
                events.Add(new TurnEvent { Kind = TurnEventKind.MatchEnd, Seat = winnerSeat, Amount = _scores[winnerSeat] });
            }
        }

        // -------------------------------------------------------- reconcile --

        /// <summary>
        /// Reconciliación con el servidor. El cliente predice para animar sin lag;
        /// cuando llega el snapshot autoritativo, esto pisa el estado local.
        /// Nunca se hace "merge": en un juego con servidor autoritativo, el que
        /// manda es el servidor y punto.
        /// </summary>
        public void Reconcile(GameSnapshot snapshot)
        {
            if (snapshot == null) return;
            Phase = ParsePhase(snapshot.phase);
            Round = snapshot.round;
            TurnIndex = snapshot.turnIndex;
            Direction = snapshot.direction;
            CurrentSeat = snapshot.currentSeat;
            TopColor = snapshot.topColor;
            DrawnThisTurn = 0;

            if (snapshot.pending != null)
            {
                Pending = new PendingPenalty(
                    snapshot.pending.amount,
                    snapshot.pending.card.ToCard(),
                    -1,
                    snapshot.pending.targetSeat);
            }
            else
            {
                Pending = null;
            }

            if (snapshot.unoWindow != null)
            {
                UnoWindowSeat = snapshot.unoWindow.seat;
                UnoWindowDeadline = snapshot.unoWindow.deadline;
            }
            else
            {
                UnoWindowSeat = -1;
            }

            if (snapshot.challenge != null)
            {
                ChallengePlayerSeat = snapshot.challenge.playerSeat;
                ChallengeTargetSeat = snapshot.challenge.targetSeat;
                ChallengeDeadline = snapshot.challenge.deadline;
            }
            else
            {
                ChallengePlayerSeat = -1;
                ChallengeTargetSeat = -1;
            }

            if (snapshot.players != null)
            {
                foreach (var p in snapshot.players)
                {
                    if (p.seat < 0 || p.seat >= _hands.Count) continue;
                    _scores[p.seat] = p.score;
                    if (p.hand != null)
                    {
                        var hand = _hands[p.seat];
                        hand.Clear();
                        foreach (var hc in p.hand) hand.Add(hc.ToCard());
                    }
                }
            }
        }

        private static TurnPhase ParsePhase(string phase)
        {
            switch (phase)
            {
                case "playing": return TurnPhase.Playing;
                case "challenge": return TurnPhase.AwaitingChallenge;
                case "roundOver": return TurnPhase.RoundOver;
                case "matchOver": return TurnPhase.MatchOver;
                default: return TurnPhase.Idle;
            }
        }
    }
}
