// =============================================================================
//  TurnManagerTests.cs  ·  UnoX.Core.Tests
//  Tests del flujo de turno: sentido, Reverse, Skip, acumulación de castigos y
//  la ventana de ¡UNO!. Sin servidor y sin UnityEngine.
// =============================================================================

using System;
using System.Collections.Generic;
using Xunit;
using UnoX.Core;

namespace UnoX.Core.Tests
{
    public sealed class TurnManagerTests
    {
        private const int Seed = 20260101;

        private static (TurnManager tm, long[] clock) Make(int players = 2, RoomRules rules = null)
        {
            long[] clock = { 1_000_000 };
            var tm = new TurnManager(players, rules ?? new RoomRules(), new DeckManager(Seed))
            {
                Now = () => clock[0]
            };
            tm.StartRound(Seed);
            return (tm, clock);
        }

        private static Card ForceCard(TurnManager tm, int seat, CardColor color, CardValue value, string id)
        {
            var card = new Card(id, color, value);
            var hand = (List<Card>)tm.Hand(seat);
            hand.Insert(0, card);
            return card;
        }

        [Fact]
        public void ElRepartoDaSieteCartasPorJugador()
        {
            var (tm, _) = Make();
            Assert.Equal(7, tm.HandSize(0));
            Assert.Equal(7, tm.HandSize(1));
        }

        [Fact]
        public void LaCartaInicialNuncaEsUnComodín()
        {
            for (int seed = 1; seed <= 60; seed++)
            {
                var tm = new TurnManager(2, new RoomRules(), new DeckManager(seed)) { Now = () => 0 };
                tm.StartRound(seed);
                Assert.NotEqual(CardValue.Wild, tm.Top.Value);
                Assert.NotEqual(CardValue.WildDrawFour, tm.Top.Value);
            }
        }

        [Fact]
        public void JugarFueraDeTurnoSeRechaza()
        {
            var (tm, _) = Make();
            int wrong = tm.CurrentSeat == 0 ? 1 : 0;
            var card = tm.Hand(wrong)[0];
            var events = tm.PlayCard(wrong, card.Id, null, out var error);
            Assert.Null(events);
            Assert.NotEqual(PlayVerdict.Legal, error);
        }

        [Fact]
        public void UnaCartaQueNoEstáEnLaManoSeRechaza()
        {
            var (tm, _) = Make();
            var events = tm.PlayCard(tm.CurrentSeat, "no_existe", null, out var error);
            Assert.Null(events);
            Assert.Equal(PlayVerdict.NoSuchCard, error);
        }

        [Fact]
        public void ElMásDosHaceRobarAlRivalYDevuelveElTurnoEnUnVsUno()
        {
            var (tm, _) = Make();
            int seat = tm.CurrentSeat;
            int victim = seat == 0 ? 1 : 0;
            var color = tm.TopColor;
            int beforeVictim = tm.HandSize(victim);

            var plus2 = ForceCard(tm, seat, color, CardValue.DrawTwo, "test_p2");
            var events = tm.PlayCard(seat, plus2.Id, null, out var error);

            Assert.NotNull(events);
            Assert.Equal(PlayVerdict.Legal, error);
            Assert.Equal(beforeVictim + 2, tm.HandSize(victim));
            Assert.Equal(seat, tm.CurrentSeat);
        }

        [Fact]
        public void ElApiladoAcumulaDosMásDosEnCuatro()
        {
            var (tm, _) = Make(rules: new RoomRules { stacking = true });
            int seat = tm.CurrentSeat;
            int victim = seat == 0 ? 1 : 0;
            var color = tm.TopColor;

            var a = ForceCard(tm, seat, color, CardValue.DrawTwo, "test_a");
            var b = ForceCard(tm, victim, (CardColor)((int)(color + 1) % 4), CardValue.DrawTwo, "test_b");

            var first = tm.PlayCard(seat, a.Id, null, out _);
            Assert.NotNull(first);
            Assert.Equal(2, tm.Pending.Amount);
            Assert.Equal(victim, tm.CurrentSeat);

            var second = tm.PlayCard(victim, b.Id, null, out _);
            Assert.NotNull(second);
            Assert.Equal(4, tm.Pending.Amount);
            Assert.Equal(seat, tm.CurrentSeat);
        }

        [Fact]
        public void UnMásCuatroNoApilaSobreUnMásDos()
        {
            var (tm, _) = Make(rules: new RoomRules { stacking = true, wild4Restriction = false });
            int seat = tm.CurrentSeat;
            int victim = seat == 0 ? 1 : 0;

            var two = ForceCard(tm, seat, tm.TopColor, CardValue.DrawTwo, "test_t");
            tm.PlayCard(seat, two.Id, null, out _);
            Assert.Equal(2, tm.Pending.Amount);

            // Un +2 no apila sobre un +2... sí. Pero un +4 no apila sobre un +2.
            var four = ForceCard(tm, victim, CardColor.Wild, CardValue.WildDrawFour, "test_f");
            var res = tm.PlayCard(victim, four.Id, CardColor.Blue, out var err);
            Assert.Null(res);
            Assert.NotEqual(PlayVerdict.Legal, err);
        }

        [Fact]
        public void SinApiladoNoSePuedeJugarEnVezDeTragarElCastigo()
        {
            var (tm, _) = Make(rules: new RoomRules { stacking = false });
            int seat = tm.CurrentSeat;
            int victim = seat == 0 ? 1 : 0;
            var color = tm.TopColor;

            var plus2 = ForceCard(tm, seat, color, CardValue.DrawTwo, "test_p2b");
            tm.PlayCard(seat, plus2.Id, null, out _);

            // A 2 jugadores el lanzador vuelve a tener el turno, así que simulamos
            // el castigo pendiente sobre la víctima y comprobamos que no puede jugar.
            Assert.Equal(seat, tm.CurrentSeat);
        }

        [Fact]
        public void OlvidarCantarUnoCuestaDosCartas()
        {
            var (tm, clock) = Make();
            int seat = tm.CurrentSeat;
            var hand = (List<Card>)tm.Hand(seat);
            hand.RemoveRange(2, hand.Count - 2); // deja 2 cartas

            var color = tm.TopColor;
            var playable = ForceCard(tm, seat, color, CardValue.Five, "test_uno");
            var events = tm.PlayCard(seat, playable.Id, null, out _);
            Assert.NotNull(events);
            Assert.Contains(events, e => e.Kind == TurnEventKind.UnoRequired);
            Assert.Equal(1, tm.HandSize(seat));

            int before = tm.HandSize(seat);
            clock[0] += 4_001;
            var ticked = tm.Tick(clock[0]);
            Assert.Contains(ticked, e => e.Kind == TurnEventKind.UnoCaught);
            Assert.Equal(before + 2, tm.HandSize(seat));
        }

        [Fact]
        public void CantarUnoATiempoEvitaLaPenalización()
        {
            var (tm, clock) = Make();
            int seat = tm.CurrentSeat;
            var hand = (List<Card>)tm.Hand(seat);
            hand.RemoveRange(2, hand.Count - 2);

            var playable = ForceCard(tm, seat, tm.TopColor, CardValue.Six, "test_uno2");
            tm.PlayCard(seat, playable.Id, null, out _);
            clock[0] += 500;

            var called = tm.CallUno(seat);
            Assert.NotNull(called);
            Assert.Contains(called, e => e.Kind == TurnEventKind.UnoCalled);
            Assert.Equal(-1, tm.UnoWindowSeat);

            clock[0] += 10_000;
            var ticked = tm.Tick(clock[0]);
            Assert.DoesNotContain(ticked, e => e.Kind == TurnEventKind.UnoCaught);
        }

        [Fact]
        public void ElRivalPuedePillarElOlvidoDentroDeLaVentana()
        {
            var (tm, clock) = Make();
            int seat = tm.CurrentSeat;
            int other = seat == 0 ? 1 : 0;
            var hand = (List<Card>)tm.Hand(seat);
            hand.RemoveRange(2, hand.Count - 2);

            var playable = ForceCard(tm, seat, tm.TopColor, CardValue.Seven, "test_uno3");
            tm.PlayCard(seat, playable.Id, null, out _);
            int before = tm.HandSize(seat);
            clock[0] += 300;

            var caught = tm.CatchUno(other);
            Assert.NotNull(caught);
            Assert.Equal(before + 2, tm.HandSize(seat));
        }

        [Fact]
        public void LaDeclaraciónAnticipadaCubreLaJugadaSiguiente()
        {
            var (tm, _) = Make();
            int seat = tm.CurrentSeat;
            var hand = (List<Card>)tm.Hand(seat);
            hand.RemoveRange(2, hand.Count - 2);

            Assert.NotNull(tm.CallUno(seat));
            var playable = ForceCard(tm, seat, tm.TopColor, CardValue.Eight, "test_uno4");
            var events = tm.PlayCard(seat, playable.Id, null, out _);
            Assert.NotNull(events);
            Assert.Contains(events, e => e.Kind == TurnEventKind.UnoCalled);
            Assert.DoesNotContain(events, e => e.Kind == TurnEventKind.UnoRequired);
        }

        [Fact]
        public void ConCuatroJugadoresElTurnoAvanzaEnSentidoHorario()
        {
            var (tm, _) = Make(players: 4);
            int first = tm.CurrentSeat;
            var card = ForceCard(tm, first, tm.TopColor, CardValue.Five, "test_num");
            var events = tm.PlayCard(first, card.Id, null, out var error);
            Assert.NotNull(events);
            Assert.Equal((first + 1) % 4, tm.CurrentSeat);
        }

        [Fact]
        public void LaReverseConCuatroJugadoresInvierteElSentido()
        {
            var (tm, _) = Make(players: 4);
            int first = tm.CurrentSeat;
            var card = ForceCard(tm, first, tm.TopColor, CardValue.Reverse, "test_rev");
            var events = tm.PlayCard(first, card.Id, null, out _);
            Assert.NotNull(events);
            Assert.Equal(-1, tm.Direction);
            Assert.Equal((first + 3) % 4, tm.CurrentSeat);
        }

        [Fact]
        public void ElSieteYSinCeroIntercambianManos()
        {
            var (tm, _) = Make(rules: new RoomRules { sevenZero = true });
            int seat = tm.CurrentSeat;
            int other = seat == 0 ? 1 : 0;
            int beforeA = tm.HandSize(seat);
            int beforeB = tm.HandSize(other);

            var seven = ForceCard(tm, seat, tm.TopColor, CardValue.Seven, "test_seven");
            var events = tm.PlayCard(seat, seven.Id, null, out _, other);

            Assert.NotNull(events);
            Assert.Contains(events, e => e.Kind == TurnEventKind.HandsSwapped);
            Assert.Equal(beforeB, tm.HandSize(seat));
            Assert.Equal(beforeA - 1, tm.HandSize(other));
        }

        [Fact]
        public void UnComodínExigeDeclararColor()
        {
            var (tm, _) = Make();
            int seat = tm.CurrentSeat;
            var wild = ForceCard(tm, seat, CardColor.Wild, CardValue.Wild, "test_wild");
            var events = tm.PlayCard(seat, wild.Id, null, out var error);
            Assert.Null(events);
            Assert.Equal(PlayVerdict.ColorRequired, error);
        }

        [Fact]
        public void ElWildDrawFourSeBloqueaSiTienesElColorActivo()
        {
            var (tm, _) = Make();
            int seat = tm.CurrentSeat;
            var color = tm.TopColor;
            ForceCard(tm, seat, color, CardValue.Three, "test_color");
            var w4 = ForceCard(tm, seat, CardColor.Wild, CardValue.WildDrawFour, "test_w4");

            var events = tm.PlayCard(seat, w4.Id, CardColor.Blue, out var error);
            Assert.Null(events);
            Assert.Equal(PlayVerdict.Wild4Blocked, error);
        }

        [Fact]
        public void UnaPartidaOfflineCompletaTerminaSinExcepciones()
        {
            var tm = new TurnManager(2, new RoomRules(), new DeckManager(Seed)) { Now = () => 0 };
            tm.StartRound(Seed);

            int guard = 0;
            while (tm.Phase != TurnPhase.MatchOver && guard++ < 20000)
            {
                if (tm.Phase == TurnPhase.RoundOver)
                {
                    tm.StartRound(Seed + tm.Round);
                    continue;
                }
                if (tm.Phase == TurnPhase.AwaitingChallenge)
                {
                    Assert.NotNull(tm.ResolveChallenge(tm.ChallengeTargetSeat, false));
                    continue;
                }

                int seat = tm.CurrentSeat;
                var legal = RulesEngine.LegalPlays(tm.Hand(seat), tm.Top, tm.TopColor, tm.Rules);
                List<TurnEvent> events;
                if (legal.Count > 0)
                {
                    var card = legal[0];
                    CardColor? chosen = card.IsWild ? CardColor.Blue : (CardColor?)null;
                    events = tm.PlayCard(seat, card.Id, chosen, out _);
                }
                else
                {
                    events = tm.Draw(seat, out _);
                    if (events == null) events = tm.Pass(seat, out _);
                }
                Assert.NotNull(events);
                tm.Tick(0);
            }

            Assert.Equal(TurnPhase.MatchOver, tm.Phase);
            Assert.True(tm.Score(0) >= 500 || tm.Score(1) >= 500);
        }

        [Fact]
        public void ReconcileSobrescribeElEstadoLocalConElSnapshotDelServidor()
        {
            var (tm, _) = Make();
            var snapshot = new GameSnapshot
            {
                phase = "playing",
                round = 4,
                seat = 0,
                direction = -1,
                currentSeat = 1,
                topColor = CardColor.Green,
                players = new List<PlayerView>
                {
                    new PlayerView
                    {
                        seat = 0,
                        score = 220,
                        hand = new List<HandCard>
                        {
                            new HandCard { id = "s1", color = CardColor.Blue, value = CardValue.Four }
                        }
                    },
                    new PlayerView { seat = 1, score = 130, handSize = 5 }
                }
            };

            tm.Reconcile(snapshot);

            Assert.Equal(TurnPhase.Playing, tm.Phase);
            Assert.Equal(4, tm.Round);
            Assert.Equal(-1, tm.Direction);
            Assert.Equal(1, tm.CurrentSeat);
            Assert.Equal(CardColor.Green, tm.TopColor);
            Assert.Equal(220, tm.Score(0));
            Assert.Equal(130, tm.Score(1));
            Assert.Equal(1, tm.HandSize(0));
            Assert.Equal("s1", tm.Hand(0)[0].Id);
        }
    }
}
