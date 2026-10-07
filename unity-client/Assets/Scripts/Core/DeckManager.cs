// =============================================================================
//  DeckManager.cs  ·  UnoX.Core
//  El mazo oficial de 108 cartas y todo lo que lo rodea: construcción, barajado,
//  reciclado del descarte y reparto.
//
//  Composición (reglamento de Mattel):
//     4 colores × 25 cartas = 100
//         1 × "0"  +  2 × (1..9) = 18  +  2 × Skip + 2 × Reverse + 2 × DrawTwo = 6
//     + 4 Wild
//     + 4 Wild Draw Four
//     = 108
//
//  IMPORTANTE sobre autoridad:
//  En una partida online el servidor es quien mezcla y quien sabe el orden del
//  mazo. Este DeckManager se usa para:
//     - modo práctica offline (sin servidor),
//     - la animación de reparto (el cliente baraja visualmente con la semilla
//       pública de la ronda, que NO revela el orden real),
//     - los tests de reglas.
//  Nunca se usa para decidir qué carta toca en una partida online.
// =============================================================================

using System;
using System.Collections.Generic;

namespace UnoX.Core
{
    public sealed class DeckManager
    {
        public const int DeckSize = 108;
        public const int DefaultHandSize = 7;

        private static readonly CardColor[] Colors =
        {
            CardColor.Red, CardColor.Yellow, CardColor.Green, CardColor.Blue
        };

        private readonly List<Card> _drawPile = new List<Card>(DeckSize);
        private readonly List<Card> _discardPile = new List<Card>(DeckSize);
        private readonly DeterministicRng _rng;

        public IReadOnlyList<Card> DrawPile => _drawPile;
        public IReadOnlyList<Card> DiscardPile => _discardPile;
        public int DrawCount => _drawPile.Count;
        public int DiscardCount => _discardPile.Count;
        public Card Top => _discardPile.Count > 0 ? _discardPile[_discardPile.Count - 1] : default;
        public bool HasTop => _discardPile.Count > 0;

        public DeckManager(int seed)
        {
            _rng = new DeterministicRng(seed);
        }

        // ------------------------------------------------------------- build --

        /// <summary>
        /// Construye el mazo en orden canónico. El orden importa: con la misma
        /// semilla, cualquier plataforma obtiene exactamente el mismo mazo.
        /// </summary>
        public static List<Card> BuildCanonicalDeck()
        {
            var deck = new List<Card>(DeckSize);
            int n = 0;

            foreach (var color in Colors)
            {
                deck.Add(new Card($"c{n++}", color, CardValue.Zero));
                for (int v = 1; v <= 9; v++)
                {
                    deck.Add(new Card($"c{n++}", color, (CardValue)v));
                    deck.Add(new Card($"c{n++}", color, (CardValue)v));
                }
                for (int k = 0; k < 2; k++) deck.Add(new Card($"c{n++}", color, CardValue.Skip));
                for (int k = 0; k < 2; k++) deck.Add(new Card($"c{n++}", color, CardValue.Reverse));
                for (int k = 0; k < 2; k++) deck.Add(new Card($"c{n++}", color, CardValue.DrawTwo));
            }
            for (int k = 0; k < 4; k++) deck.Add(new Card($"c{n++}", CardColor.Wild, CardValue.Wild));
            for (int k = 0; k < 4; k++) deck.Add(new Card($"c{n++}", CardColor.Wild, CardValue.WildDrawFour));

            return deck;
        }

        /// <summary>Mazo barajado de forma determinista (Fisher–Yates + mulberry32).</summary>
        public static List<Card> BuildShuffledDeck(int seed)
        {
            var deck = BuildCanonicalDeck();
            new DeterministicRng(seed).Shuffle(deck);
            return deck;
        }

        /// <summary>
        /// Validación de integridad. Se usa en los tests y como aserción de
        /// arranque: si algún día alguien toca la tabla de cartas, esto revienta
        /// en el test en lugar de producir una partida rara en producción.
        /// </summary>
        public static List<string> Validate(List<Card> deck)
        {
            var errors = new List<string>();
            if (deck.Count != DeckSize) errors.Add($"esperadas 108 cartas, hay {deck.Count}");

            var ids = new HashSet<string>();
            foreach (var c in deck)
                if (!ids.Add(c.Id)) errors.Add($"id duplicado: {c.Id}");

            var counts = new Dictionary<(CardColor, CardValue), int>();
            foreach (var c in deck)
            {
                var key = (c.Color, c.Value);
                counts.TryGetValue(key, out int n);
                counts[key] = n + 1;
            }

            foreach (var color in Colors)
            {
                Expect(counts, color, CardValue.Zero, 1, errors);
                for (int v = 1; v <= 9; v++) Expect(counts, color, (CardValue)v, 2, errors);
                Expect(counts, color, CardValue.Skip, 2, errors);
                Expect(counts, color, CardValue.Reverse, 2, errors);
                Expect(counts, color, CardValue.DrawTwo, 2, errors);
            }
            Expect(counts, CardColor.Wild, CardValue.Wild, 4, errors);
            Expect(counts, CardColor.Wild, CardValue.WildDrawFour, 4, errors);
            return errors;
        }

        private static void Expect(
            Dictionary<(CardColor, CardValue), int> counts,
            CardColor color, CardValue value, int expected, List<string> errors)
        {
            counts.TryGetValue((color, value), out int n);
            if (n != expected) errors.Add($"{color} {value}: esperadas {expected}, hay {n}");
        }

        // ------------------------------------------------------------- piles --

        /// <summary>Prepara una ronda nueva: baraja y coloca todo en el mazo de robo.</summary>
        public void Reset(int seed)
        {
            _drawPile.Clear();
            _discardPile.Clear();
            _drawPile.AddRange(BuildShuffledDeck(seed));
        }

        /// <summary>
        /// Roba <paramref name="count"/> cartas. Devuelve las que pudo dar.
        ///
        /// Se roba del FRENTE de la lista para replicar el `shift()` del servidor.
        /// Con 108 cartas el coste de RemoveAt(0) es despreciable y a cambio los
        /// replays son bit a bit iguales en C# y en JavaScript.
        /// </summary>
        public List<Card> Draw(int count)
        {
            var drawn = new List<Card>(count);
            for (int i = 0; i < count; i++)
            {
                if (_drawPile.Count == 0 && !RecycleDiscard()) break;
                if (_drawPile.Count == 0) break;
                var card = _drawPile[0];
                _drawPile.RemoveAt(0);
                drawn.Add(card);
            }
            return drawn;
        }

        public Card? DrawOne()
        {
            var list = Draw(1);
            return list.Count > 0 ? list[0] : (Card?)null;
        }

        public void Discard(Card card) => _discardPile.Add(card);

        /// <summary>
        /// Devuelve el descarte (menos la carta superior) al mazo de robo y lo
        /// remezcla. Devuelve false si no queda nada que reciclar.
        /// </summary>
        public bool RecycleDiscard()
        {
            if (_discardPile.Count <= 1) return false;

            var top = _discardPile[_discardPile.Count - 1];
            var recycled = new List<Card>(_discardPile.Count - 1);
            for (int i = 0; i < _discardPile.Count - 1; i++) recycled.Add(_discardPile[i]);

            _discardPile.Clear();
            _discardPile.Add(top);

            _rng.Shuffle(recycled);
            _drawPile.AddRange(recycled);
            return true;
        }

        /// <summary>
        /// Reparte una mano. En online esto NO se usa: el servidor ya repartió y
        /// envía sólo la mano de cada uno. Sirve para el modo práctica.
        /// </summary>
        public List<Card> DealHand(int playerCount, int handSize = DefaultHandSize)
        {
            var hand = new List<Card>(handSize);
            for (int i = 0; i < handSize; i++)
            {
                var c = DrawOne();
                if (c.HasValue) hand.Add(c.Value);
            }
            return hand;
        }

        /// <summary>
        /// Saca la carta inicial del descarte. Si sale comodín se reinserta en el
        /// mazo y se saca otra, como manda el reglamento.
        /// </summary>
        public Card FlipStarter()
        {
            while (_drawPile.Count > 0)
            {
                var card = _drawPile[0];
                _drawPile.RemoveAt(0);
                if (!card.IsWild)
                {
                    _discardPile.Add(card);
                    return card;
                }
                int at = _rng.NextInt(_drawPile.Count + 1);
                _drawPile.Insert(at, card);
            }
            throw new InvalidOperationException("Mazo vacío al sacar la carta inicial");
        }

        /// <summary>Total de cartas en juego. Debe ser siempre 108.</summary>
        public int TotalInPlay(IEnumerable<Card> hands)
        {
            int n = _drawPile.Count + _discardPile.Count;
            foreach (var h in hands) n++;
            return n;
        }
    }
}
