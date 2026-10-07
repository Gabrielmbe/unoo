// =============================================================================
//  CardModel.cs  ·  UnoX.Core
//  Modelo de datos puro. CERO referencias a UnityEngine: así puede compilarse y
//  testearse fuera del editor (xunit/NUnit) y compartirse con un servidor .NET.
//
//  Los valores numéricos de CardColor y CardValue son IDÉNTICOS a los del
//  servidor (server/src/deck.js). Cambiarlos rompería el protocolo.
// =============================================================================

using System;
using System.Collections.Generic;

namespace UnoX.Core
{
    /// <summary>Colores del mazo. Wild = comodín sin color propio.</summary>
    public enum CardColor : byte
    {
        Red = 0,
        Yellow = 1,
        Green = 2,
        Blue = 3,
        Wild = 4
    }

    /// <summary>Valores de carta. 0-9 números, 10-12 acciones, 13-14 comodines.</summary>
    public enum CardValue : byte
    {
        Zero = 0,
        One = 1,
        Two = 2,
        Three = 3,
        Four = 4,
        Five = 5,
        Six = 6,
        Seven = 7,
        Eight = 8,
        Nine = 9,
        Skip = 10,
        Reverse = 11,
        DrawTwo = 12,
        Wild = 13,
        WildDrawFour = 14
    }

    /// <summary>
    /// Una carta. Es un <c>struct</c> a propósito: una mano de 30 cartas no debe
    /// generar 30 objetos en el heap ni 30 entradas para el GC. En móvil eso se
    /// nota directamente en los tirones.
    /// </summary>
    [Serializable]
    public readonly struct Card : IEquatable<Card>
    {
        /// <summary>Identificador opaco asignado por el servidor. Nunca se inventa.</summary>
        public readonly string Id;
        public readonly CardColor Color;
        public readonly CardValue Value;

        public Card(string id, CardColor color, CardValue value)
        {
            Id = id;
            Color = color;
            Value = value;
        }

        /// <summary>Copia con otro id (se usa al reciclar el descarte).</summary>
        public Card WithId(string newId) => new Card(newId, Color, Value);

        public bool IsWild => Value == CardValue.Wild || Value == CardValue.WildDrawFour;
        public bool IsNumber => Value <= CardValue.Nine;
        public bool IsAction => Value == CardValue.Skip || Value == CardValue.Reverse || Value == CardValue.DrawTwo;
        public bool IsDrawCard => Value == CardValue.DrawTwo || Value == CardValue.WildDrawFour;

        /// <summary>Puntos oficiales: número = valor facial, acción = 20, comodín = 50.</summary>
        public int Points
        {
            get
            {
                if (IsNumber) return (int)Value;
                if (IsAction) return 20;
                return 50;
            }
        }

        /// <summary>Cantidad que hace robar al rival (0 si no es de castigo).</summary>
        public int DrawAmount
        {
            get
            {
                if (Value == CardValue.DrawTwo) return 2;
                if (Value == CardValue.WildDrawFour) return 4;
                return 0;
            }
        }

        public bool Equals(Card other) => Id == other.Id && Color == other.Color && Value == other.Value;
        public override bool Equals(object obj) => obj is Card c && Equals(c);
        public override int GetHashCode() => Id?.GetHashCode() ?? 0;
        public static bool operator ==(Card a, Card b) => a.Equals(b);
        public static bool operator !=(Card a, Card b) => !a.Equals(b);

        public override string ToString()
        {
            if (Value == CardValue.Wild) return "Wild";
            if (Value == CardValue.WildDrawFour) return "Wild Draw Four";
            return $"{Color} {Value}";
        }
    }

    /// <summary>Carta tal y como la envía el servidor: sin id (información pública).</summary>
    [Serializable]
    public struct PublicCard
    {
        public CardColor color;
        public CardValue value;

        public Card ToCard(string id = "") => new Card(id, color, value);
    }

    /// <summary>Carta de la propia mano: sí lleva id, porque hay que poder referenciarla.</summary>
    [Serializable]
    public struct HandCard
    {
        public string id;
        public CardColor color;
        public CardValue value;

        public Card ToCard() => new Card(id, color, value);
    }

    /// <summary>Reglas de la sala. Debe coincidir campo por campo con sanitizeRules() del servidor.</summary>
    [Serializable]
    public class RoomRules
    {
        public int handSize = 7;
        public int targetScore = 500;
        public int turnSeconds = 20;
        public int unoWindowMs = 4000;
        public int challengeWindowMs = 8000;

        // House rules (las mismas que expone UNO! Mobile en Room Mode).
        public bool stacking;          // acumular +2 con +2 y +4 con +4
        public bool wild4Restriction = true;
        public bool wild4Challenge;
        public bool drawUntilPlayable = true;
        public bool allowPassWithPlay;
        public bool sevenZero;
        public bool jumpIn;
    }

    /// <summary>Estado de un jugador tal y como lo ve ESTE cliente.</summary>
    [Serializable]
    public class PlayerView
    {
        public int seat;
        public string id;
        public string name;
        public string avatarId;
        public int score;
        public bool connected;
        public int handSize;

        /// <summary>Sólo viene relleno para el jugador local. Para el rival es null.</summary>
        public List<HandCard> hand;

        public bool IsLocal(int localSeat) => seat == localSeat;
    }

    /// <summary>
    /// Snapshot completo de la partida. Es lo que se aplica tras una reconexión
    /// o cuando el cliente detecta un hueco en la secuencia de eventos.
    /// </summary>
    [Serializable]
    public class GameSnapshot
    {
        public string phase;
        public int round;
        public int turnIndex;
        public int seat;                 // MI asiento
        public int direction;            // +1 horario, -1 antihorario
        public int currentSeat;
        public long turnDeadline;        // epoch ms del servidor
        public PublicCard? topCard;
        public CardColor topColor;
        public PendingDrawView pending;
        public ChallengeView challenge;
        public UnoWindowView unoWindow;
        public int deckCount;
        public List<PlayerView> players;
        public RoomRules rules;
        public int lastSeq;

        public bool IsPlaying => phase == "playing";
    }

    [Serializable]
    public class PendingDrawView
    {
        public int amount;
        public int targetSeat;
        public PublicCard card;
    }

    [Serializable]
    public class ChallengeView
    {
        public int playerSeat;
        public int targetSeat;
        public long deadline;
    }

    [Serializable]
    public class UnoWindowView
    {
        public int seat;
        public long deadline;
    }
}
