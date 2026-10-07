// =============================================================================
//  RulesEngine.cs  ·  UnoX.Core
//  Espejo del motor de reglas del servidor (server/src/rules.js).
//
//  ¿Por qué duplicar la lógica si el servidor manda?
//   - Para poder GRISAR las cartas ilegales y bloquear el drag antes de enviar
//     nada (feedback instantáneo, sin viaje de red).
//   - Para el modo práctica offline.
//   - Para predecir de quién es el siguiente turno y arrancar la animación antes
//     de que llegue la confirmación.
//
//  La copia del servidor es la que DECIDE. Si este espejo se equivoca, lo peor
//  que pasa es que el cliente grisó mal una carta y el servidor la rechaza.
//  Nunca al revés: el servidor jamás acepta lo que este archivo rechace.
// =============================================================================

using System.Collections.Generic;

namespace UnoX.Core
{
    public enum PlayVerdict
    {
        Legal,
        NoSuchCard,
        NoMatch,
        Wild4Blocked,
        ColorRequired,
        BadSwapTarget,
        MustDraw
    }

    /// <summary>Resultado de una consulta de legalidad.</summary>
    public readonly struct PlayCheck
    {
        public readonly bool Legal;
        public readonly PlayVerdict Verdict;

        private PlayCheck(bool legal, PlayVerdict verdict)
        {
            Legal = legal;
            Verdict = verdict;
        }

        public static PlayCheck Ok() => new PlayCheck(true, PlayVerdict.Legal);
        public static PlayCheck Fail(PlayVerdict v) => new PlayCheck(false, v);
    }

    /// <summary>Quién juega tras aplicar el efecto de una carta.</summary>
    public readonly struct SeatResult
    {
        public readonly int NextSeat;
        public readonly int Direction;
        public readonly bool SamePlayerAgain;

        public SeatResult(int nextSeat, int direction, bool samePlayerAgain)
        {
            NextSeat = nextSeat;
            Direction = direction;
            SamePlayerAgain = samePlayerAgain;
        }
    }

    public static class RulesEngine
    {
        public const int Clockwise = 1;
        public const int CounterClockwise = -1;

        // ------------------------------------------------------------- match --

        /// <summary>¿Casa la carta con la superior, ignorando comodines?</summary>
        public static bool MatchesTop(Card card, Card topCard, CardColor topColor)
        {
            if (card.Color == topColor) return true;
            return card.Value == topCard.Value;
        }

        /// <summary>
        /// ¿Tiene el jugador alguna carta del color activo? (sin contar comodines)
        /// Es la condición de la restricción oficial del Wild Draw Four.
        /// </summary>
        public static bool HoldsColor(IReadOnlyList<Card> hand, CardColor color)
        {
            for (int i = 0; i < hand.Count; i++)
            {
                var c = hand[i];
                if (c.Color == color && !c.IsWild) return true;
            }
            return false;
        }

        /// <summary>Legalidad completa de una jugada.</summary>
        public static PlayCheck CanPlay(
            Card card, Card topCard, CardColor topColor,
            IReadOnlyList<Card> hand, RoomRules rules)
        {
            if (string.IsNullOrEmpty(card.Id)) return PlayCheck.Fail(PlayVerdict.NoSuchCard);

            if (card.Value == CardValue.Wild) return PlayCheck.Ok();

            if (card.Value == CardValue.WildDrawFour)
            {
                if (rules != null && rules.wild4Restriction && HoldsColor(hand, topColor))
                    return PlayCheck.Fail(PlayVerdict.Wild4Blocked);
                return PlayCheck.Ok();
            }

            if (MatchesTop(card, topCard, topColor)) return PlayCheck.Ok();
            return PlayCheck.Fail(PlayVerdict.NoMatch);
        }

        /// <summary>Todas las cartas jugables de la mano (para grisar la UI).</summary>
        public static List<Card> LegalPlays(
            IReadOnlyList<Card> hand, Card topCard, CardColor topColor, RoomRules rules)
        {
            var result = new List<Card>(hand.Count);
            for (int i = 0; i < hand.Count; i++)
                if (CanPlay(hand[i], topCard, topColor, hand, rules).Legal)
                    result.Add(hand[i]);
            return result;
        }

        // ---------------------------------------------------------- stacking --

        /// <summary>
        /// ¿Puede esta carta apilarse sobre un castigo pendiente?
        /// Oficialmente NO (Mattel prohíbe el stacking), pero es la house rule más
        /// jugada y UNO! Mobile la incluye. Va detrás de rules.stacking.
        /// </summary>
        public static bool CanStackOn(Card card, Card pendingCard)
        {
            if (pendingCard.Value == CardValue.DrawTwo) return card.Value == CardValue.DrawTwo;
            if (pendingCard.Value == CardValue.WildDrawFour) return card.Value == CardValue.WildDrawFour;
            return false;
        }

        public static List<Card> StackablePlays(IReadOnlyList<Card> hand, Card pendingCard)
        {
            var result = new List<Card>();
            for (int i = 0; i < hand.Count; i++)
                if (CanStackOn(hand[i], pendingCard)) result.Add(hand[i]);
            return result;
        }

        // -------------------------------------------------------------- turn --

        /// <summary>
        /// Avance de asiento para N jugadores, con envoltura correcta en ambas
        /// direcciones. El `%` de C# devuelve negativos con operandos negativos,
        /// por eso el doble módulo.
        /// </summary>
        public static int AdvanceSeat(int index, int count, int direction, int playerCount)
        {
            if (playerCount <= 0) return 0;
            int step = direction >= 0 ? 1 : -1;
            int raw = index + step * count;
            return ((raw % playerCount) + playerCount) % playerCount;
        }

        /// <summary>
        /// Decide quién juega tras el efecto de una carta.
        ///
        /// A 2 jugadores, Skip / Reverse / +2 / +4 hacen que el MISMO jugador
        /// vuelva a jugar: la víctima es el rival y pierde su turno. Es la regla
        /// oficial de UNO a 2 y la que aplica UNO! Mobile en los duelos 1v1.
        /// </summary>
        public static SeatResult ResolveSeatAfterPlay(CardValue value, int seat, int direction, int playerCount)
        {
            bool twoPlayer = playerCount == 2;

            if (value == CardValue.Reverse)
            {
                int newDir = -direction;
                return twoPlayer
                    ? new SeatResult(seat, newDir, true)
                    : new SeatResult(AdvanceSeat(seat, 1, newDir, playerCount), newDir, false);
            }

            if (value == CardValue.Skip || value == CardValue.DrawTwo || value == CardValue.WildDrawFour)
            {
                // Se avanza 2: lanzador -> víctima saltada -> siguiente.
                return twoPlayer
                    ? new SeatResult(seat, direction, true)
                    : new SeatResult(AdvanceSeat(seat, 2, direction, playerCount), direction, false);
            }

            return new SeatResult(AdvanceSeat(seat, 1, direction, playerCount), direction, false);
        }

        // ------------------------------------------------------------ scoring --

        public static int ScoreHand(IReadOnlyList<Card> hand)
        {
            int total = 0;
            for (int i = 0; i < hand.Count; i++) total += hand[i].Points;
            return total;
        }

        /// <summary>
        /// El ganador suma las cartas de todos los rivales (tabla oficial:
        /// número = valor facial, acción = 20, comodín = 50).
        /// </summary>
        public static int SettleRound(IReadOnlyList<IReadOnlyList<Card>> hands, int winnerSeat)
        {
            int gained = 0;
            for (int i = 0; i < hands.Count; i++)
            {
                if (i == winnerSeat) continue;
                gained += ScoreHand(hands[i]);
            }
            return gained;
        }

        // --------------------------------------------------------------- uno --

        /// <summary>Puntos de la penalización por no cantar UNO.</summary>
        public const int UnoPenaltyDraw = 2;

        /// <summary>Puntos extra que paga un reto fallido de Wild Draw Four.</summary>
        public const int FailedChallengeExtra = 2;
    }
}
