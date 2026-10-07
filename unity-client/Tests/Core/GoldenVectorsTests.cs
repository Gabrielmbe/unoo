// =============================================================================
//  GoldenVectorsTests.cs  ·  UnoX.Core.Tests
//
//  Lee shared/golden-vectors.json —el MISMO archivo que consume la suite del
//  servidor— y verifica que la implementación C# produce resultados idénticos.
//
//  Es el cortafuegos contra el bug más caro de un juego con dos implementaciones:
//  que el cliente y el servidor discrepen sobre qué carta es legal, y que eso sólo
//  se descubra en producción cuando el móvil grisó una carta que el PC permitía.
//
//  Los vectores se generan desde server/scripts/generate-golden-vectors.mjs.
//  NO se editan a mano.
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using UnoX.Core;

namespace UnoX.Core.Tests
{
    public sealed class GoldenVectorsTests
    {
        private static readonly JsonDocument Vectors = LoadVectors();

        private static JsonDocument LoadVectors()
        {
            // Se busca junto al assembly (lo copia el csproj) y, como respaldo,
            // subiendo por el árbol hasta dar con shared/. Así funciona tanto en
            // `dotnet test` como abierto desde el IDE.
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 12; i++)
            {
                var candidate = Path.Combine(dir, "golden-vectors.json");
                if (File.Exists(candidate)) return JsonDocument.Parse(File.ReadAllText(candidate));
                var shared = Path.Combine(dir, "shared", "golden-vectors.json");
                if (File.Exists(shared)) return JsonDocument.Parse(File.ReadAllText(shared));
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            throw new FileNotFoundException("no se encontró golden-vectors.json");
        }

        // -------------------------------------------------------------- deck --

        [Fact]
        public void ElMazoCanónicoTiene108CartasYLaComposiciónOficial()
        {
            var deck = DeckManager.BuildCanonicalDeck();
            Assert.Equal(108, deck.Count);

            var expected = Vectors.RootElement.GetProperty("deckComposition");
            Assert.Equal(expected.GetProperty("total").GetInt32(), deck.Count);

            var perColor = new Dictionary<CardColor, int>();
            int wild = 0, wild4 = 0;
            foreach (var c in deck)
            {
                if (c.Color == CardColor.Wild)
                {
                    if (c.Value == CardValue.Wild) wild++;
                    else wild4++;
                }
                else
                {
                    perColor.TryGetValue(c.Color, out int n);
                    perColor[c.Color] = n + 1;
                }
            }

            Assert.Equal(expected.GetProperty("perColor").GetInt32(), perColor[CardColor.Red]);
            Assert.Equal(expected.GetProperty("perColor").GetInt32(), perColor[CardColor.Yellow]);
            Assert.Equal(expected.GetProperty("perColor").GetInt32(), perColor[CardColor.Green]);
            Assert.Equal(expected.GetProperty("perColor").GetInt32(), perColor[CardColor.Blue]);
            Assert.Equal(expected.GetProperty("wild").GetInt32(), wild);
            Assert.Equal(expected.GetProperty("wildDrawFour").GetInt32(), wild4);
        }

        [Fact]
        public void LaValidaciónDeIntegridadNoDevuelveErrores()
        {
            var errors = DeckManager.Validate(DeckManager.BuildCanonicalDeck());
            Assert.Empty(errors);
        }

        [Fact]
        public void BarajarConservaLas108Cartas()
        {
            var deck = DeckManager.BuildShuffledDeck(987654321);
            Assert.Empty(DeckManager.Validate(deck));
        }

        [Fact]
        public void ElOrdenDelMazoCoincideConElServidor()
        {
            foreach (var entry in Vectors.RootElement.GetProperty("decks").EnumerateObject())
            {
                int seed = int.Parse(entry.Name);
                var deck = DeckManager.BuildShuffledDeck(seed);

                var expectedFirst20 = entry.Value.GetProperty("first20")
                    .EnumerateArray().Select(e => e.GetString()).ToList();
                Assert.Equal(expectedFirst20, deck.Take(20).Select(c => c.Id).ToList());

                Assert.Equal(
                    entry.Value.GetProperty("fnv1a").GetString(),
                    Fnv1a(string.Join(",", deck.Select(c => c.Id))));
            }
        }

        // --------------------------------------------------------------- rng --

        [Fact]
        public void ElRngCoincideConJavaScriptParaTodasLasSemillas()
        {
            foreach (var entry in Vectors.RootElement.GetProperty("rng").EnumerateObject())
            {
                int seed = int.Parse(entry.Name);
                var rng = new DeterministicRng(seed);
                int i = 0;
                foreach (var v in entry.Value.EnumerateArray())
                {
                    // Comparación exacta en double: es lo que garantiza que
                    // NextInt() devuelva el mismo entero en C# y en JS.
                    Assert.Equal(v.GetDouble(), rng.NextDouble(), 15);
                    i++;
                }
                Assert.True(i > 0);
            }
        }

        // -------------------------------------------------------------- turn --

        [Theory]
        [InlineData(0, 1, 1, 4, 1)]
        [InlineData(3, 1, 1, 4, 0)]
        [InlineData(0, 1, -1, 4, 3)]
        [InlineData(2, 2, -1, 4, 0)]
        public void ElAvanceDeAsientoEnvuelveBien(int seat, int count, int dir, int n, int expected)
        {
            Assert.Equal(expected, RulesEngine.AdvanceSeat(seat, count, dir, n));
        }

        [Fact]
        public void LaTablaDeAvanceCoincideConElServidor()
        {
            foreach (var c in Vectors.RootElement.GetProperty("seatTable").EnumerateArray())
            {
                int actual = RulesEngine.AdvanceSeat(
                    c.GetProperty("seat").GetInt32(),
                    c.GetProperty("count").GetInt32(),
                    c.GetProperty("direction").GetInt32(),
                    c.GetProperty("playerCount").GetInt32());
                Assert.Equal(c.GetProperty("expected").GetInt32(), actual);
            }
        }

        [Fact]
        public void LaResoluciónDeTurnoCoincideParaCadaTipoDeCarta()
        {
            foreach (var c in Vectors.RootElement.GetProperty("resolveTable").EnumerateArray())
            {
                var r = RulesEngine.ResolveSeatAfterPlay(
                    (CardValue)c.GetProperty("value").GetInt32(),
                    0,
                    c.GetProperty("direction").GetInt32(),
                    c.GetProperty("playerCount").GetInt32());

                Assert.Equal(c.GetProperty("nextSeat").GetInt32(), r.NextSeat);
                Assert.Equal(c.GetProperty("newDirection").GetInt32(), r.Direction);
                Assert.Equal(c.GetProperty("samePlayerAgain").GetBoolean(), r.SamePlayerAgain);
            }
        }

        [Fact]
        public void SkipYReverseDejanElTurnoAlMismoJugadorCuandoSonDos()
        {
            var skip = RulesEngine.ResolveSeatAfterPlay(CardValue.Skip, 0, 1, 2);
            Assert.Equal(0, skip.NextSeat);
            Assert.True(skip.SamePlayerAgain);

            var rev = RulesEngine.ResolveSeatAfterPlay(CardValue.Reverse, 0, 1, 2);
            Assert.Equal(0, rev.NextSeat);
            Assert.Equal(-1, rev.Direction);
        }

        // ----------------------------------------------------------- scoring --

        [Fact]
        public void LaPuntuaciónDeManosCoincideConElServidor()
        {
            foreach (var c in Vectors.RootElement.GetProperty("scoring").EnumerateArray())
            {
                var hand = ReadCards(c.GetProperty("hand"));
                Assert.Equal(c.GetProperty("expected").GetInt32(), RulesEngine.ScoreHand(hand));
            }
        }

        [Theory]
        [InlineData(CardValue.Zero, 0)]
        [InlineData(CardValue.Nine, 9)]
        [InlineData(CardValue.Skip, 20)]
        [InlineData(CardValue.Reverse, 20)]
        [InlineData(CardValue.DrawTwo, 20)]
        [InlineData(CardValue.Wild, 50)]
        [InlineData(CardValue.WildDrawFour, 50)]
        public void LaTablaDePuntosEsLaOficial(CardValue value, int points)
        {
            Assert.Equal(points, new Card("x", CardColor.Red, value).Points);
        }

        // ------------------------------------------------------------ legal --

        [Fact]
        public void LaLegalidadDeJugadasCoincideConElServidor()
        {
            foreach (var c in Vectors.RootElement.GetProperty("legalCases").EnumerateArray())
            {
                var rules = c.GetProperty("rules").GetString() == "relaxed"
                    ? new RoomRules { wild4Restriction = false }
                    : new RoomRules();

                var top = ReadCard(c.GetProperty("top"));
                var card = ReadCard(c.GetProperty("card"));
                var hand = ReadCards(c.GetProperty("hand"));
                var topColor = (CardColor)c.GetProperty("topColor").GetInt32();

                var verdict = RulesEngine.CanPlay(card, top, topColor, hand, rules);
                Assert.Equal(
                    c.GetProperty("expected").GetBoolean(),
                    verdict.Legal);
            }
        }

        // ----------------------------------------------------------- helpers --

        private static List<Card> ReadCards(JsonElement array) =>
            array.EnumerateArray().Select(ReadCard).ToList();

        private static Card ReadCard(JsonElement el) => new Card(
            "g" + el.GetProperty("color").GetInt32() + "_" + el.GetProperty("value").GetInt32(),
            (CardColor)el.GetProperty("color").GetInt32(),
            (CardValue)el.GetProperty("value").GetInt32());

        /// <summary>FNV-1a de 32 bits, idéntico al del generador de vectores.</summary>
        private static string Fnv1a(string s)
        {
            uint h = 0x811c9dc5;
            foreach (char ch in s)
            {
                h ^= ch;
                unchecked { h *= 0x01000193; }
            }
            return h.ToString("x8");
        }
    }
}
