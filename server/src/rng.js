/**
 * RNG determinista y PORTABLE.
 *
 * Implementa mulberry32, un generador de 32 bits que produce la MISMA secuencia
 * en JavaScript y en C# (ver Unity-client/Assets/Scripts/Core/DeterministicRng.cs).
 *
 * Es crítico: el servidor mezcla el mazo con una semilla privada. Si algún día
 * queremos replays o "spectator" determinista, basta con compartir la semilla al
 * final de la partida y cualquier cliente reproduce el orden exacto.
 *
 * Nunca se envía la semilla a los clientes mientras la partida está viva:
 * conocerla equivaldría a conocer el mazo entero (cheat trivial).
 */

const TWO_POW_32 = 4294967296;

/**
 * @param {number} seed entero de 32 bits
 * @returns {() => number} función que devuelve un float en [0, 1)
 */
export function mulberry32(seed) {
  let a = seed | 0;
  return function next() {
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / TWO_POW_32;
  };
}

/** Entero en [0, max) — equivalente a (int)(next() * max) en C#. */
export function nextInt(rand, max) {
  if (max <= 0) return 0;
  return Math.floor(rand() * max);
}

/** Semilla criptográficamente decente para arranque de partida. */
export function randomSeed() {
  // crypto.getRandomValues es síncrono en Node >= 19 (globalThis.crypto)
  const buf = new Uint32Array(1);
  globalThis.crypto.getRandomValues(buf);
  return buf[0] | 0;
}
