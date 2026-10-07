// =============================================================================
//  DeterministicRng.cs  ·  UnoX.Core
//  Puerto exacto de mulberry32 (server/src/rng.js).
//
//  ¿Para qué lo necesita el cliente si el servidor es el que mezcla?
//   1. Para el modo "offline / práctica" sin servidor.
//   2. Para reproducir una partida a partir de la semilla que el servidor revela
//      al terminar (replays y verificación de que el servidor no hizo trampas).
//   3. Para que el barajado visual del reparto coincida en PC y móvil sin
//      depender del RNG de cada plataforma.
//
//  DOS DETALLES QUE ROMPEN LA PARIDAD SI SE TOCAN:
//   - La aritmética es de 32 bits sin signo con `unchecked` (equivale a `| 0` y
//     a `Math.imul` de JavaScript).
//   - La división final se hace en DOUBLE, no en float. Un float sólo tiene 24
//     bits de mantisa y daría un NextInt() distinto al de JavaScript en algunos
//     valores. JavaScript devuelve un double; aquí igual.
// =============================================================================

using System.Collections.Generic;

namespace UnoX.Core
{
    public sealed class DeterministicRng
    {
        private const double TwoPow32 = 4294967296.0;

        private uint _state;

        public DeterministicRng(int seed)
        {
            unchecked
            {
                _state = (uint)seed;
            }
        }

        /// <summary>Devuelve un double en [0, 1), idéntico al mulberry32 de JavaScript.</summary>
        public double NextDouble()
        {
            unchecked
            {
                _state += 0x6D2B79F5u;
                uint t = _state;
                t = Imul(t ^ (t >> 15), 1u | t);
                t = (t + Imul(t ^ (t >> 7), 61u | t)) ^ t;
                uint result = t ^ (t >> 14);
                return result / TwoPow32;
            }
        }

        /// <summary>Versión float para uso exclusivamente visual (nunca para lógica).</summary>
        public float NextFloat() => (float)NextDouble();

        /// <summary>Entero en [0, max). Equivalente a Math.floor(rand() * max).</summary>
        public int NextInt(int max)
        {
            if (max <= 0) return 0;
            return (int)(NextDouble() * max);
        }

        /// <summary>
        /// Math.imul de JavaScript: multiplicación de 32 bits con truncado.
        /// En C# uint*uint dentro de `unchecked` ya trunca; se deja explícito
        /// para que la intención quede documentada y revisable.
        /// </summary>
        private static uint Imul(uint a, uint b)
        {
            unchecked
            {
                return a * b;
            }
        }

        /// <summary>Barajado Fisher–Yates: mismo algoritmo y mismo orden que el servidor.</summary>
        public void Shuffle<T>(IList<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = NextInt(i + 1);
                T tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }
    }
}
