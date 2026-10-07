/**
 * DECK MANAGER — mazo oficial de UNO de 108 cartas.
 *
 * Composición verificada contra el reglamento de Mattel:
 *   4 colores × 25 cartas = 100
 *     - 1 × "0"
 *     - 2 × cada número del 1 al 9        (18)
 *     - 2 × Skip, 2 × Reverse, 2 × DrawTwo (6)
 *   + 4 Wild
 *   + 4 Wild Draw Four
 *   = 108
 *
 * Las ediciones modernas traen 112 (4 cartas "extra" en blanco/comodín especial).
 * Jugamos con el mazo clásico de 108, que es el que usa el modo Clásico de UNO!.
 *
 * NOTA DE SEGURIDAD: el orden del mazo NUNCA sale del servidor. Cada carta lleva
 * un `id` opaco; el cliente sólo recibe el id de las cartas que legítimamente ve
 * (su mano y las ya jugadas). La mano del rival se transmite como un contador.
 */

import { mulberry32, nextInt } from './rng.js';

export const Color = Object.freeze({
  Red: 0,
  Yellow: 1,
  Green: 2,
  Blue: 3,
  Wild: 4, // comodines: sin color propio
});

export const COLOR_NAMES = Object.freeze(['Red', 'Yellow', 'Green', 'Blue', 'Wild']);

export const Value = Object.freeze({
  Zero: 0,
  One: 1,
  Two: 2,
  Three: 3,
  Four: 4,
  Five: 5,
  Six: 6,
  /** Alias legible para la house rule "7-0". */
  Seven: 7,
  Eight: 8,
  Nine: 9,
  Skip: 10,
  Reverse: 11,
  DrawTwo: 12,
  Wild: 13,
  WildDrawFour: 14,
});

export const VALUE_NAMES = Object.freeze([
  '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
  'Skip', 'Reverse', 'DrawTwo', 'Wild', 'WildDrawFour',
]);

export const COLORS = [Color.Red, Color.Yellow, Color.Green, Color.Blue];

export function isWildValue(value) {
  return value === Value.Wild || value === Value.WildDrawFour;
}

export function isNumberValue(value) {
  return value >= Value.Zero && value <= Value.Nine;
}

export function isActionValue(value) {
  return value === Value.Skip || value === Value.Reverse || value === Value.DrawTwo;
}

/** Puntuación oficial: número = valor facial, acción = 20, comodín = 50. */
export function pointValue(value) {
  if (isNumberValue(value)) return value;
  if (isActionValue(value)) return 20;
  return 50; // Wild y Wild Draw Four
}

/**
 * Construye el mazo en un orden canónico y estable.
 * El orden canónico importa: hace que `seed` produzca siempre el mismo reparto,
 * en cualquier máquina y en cualquier lenguaje.
 *
 * @returns {{id: string, color: number, value: number}[]}
 */
export function buildCanonicalDeck() {
  const deck = [];
  let n = 0;

  for (const color of COLORS) {
    deck.push({ id: `c${n++}`, color, value: Value.Zero });
    for (let v = Value.One; v <= Value.Nine; v++) {
      deck.push({ id: `c${n++}`, color, value: v });
      deck.push({ id: `c${n++}`, color, value: v });
    }
    for (let k = 0; k < 2; k++) deck.push({ id: `c${n++}`, color, value: Value.Skip });
    for (let k = 0; k < 2; k++) deck.push({ id: `c${n++}`, color, value: Value.Reverse });
    for (let k = 0; k < 2; k++) deck.push({ id: `c${n++}`, color, value: Value.DrawTwo });
  }
  for (let k = 0; k < 4; k++) deck.push({ id: `c${n++}`, color: Color.Wild, value: Value.Wild });
  for (let k = 0; k < 4; k++) {
    deck.push({ id: `c${n++}`, color: Color.Wild, value: Value.WildDrawFour });
  }
  return deck;
}

/**
 * Fisher–Yates determinista. Mismo algoritmo que DeckManager.cs.
 * @param {number} seed
 * @returns {{id: string, color: number, value: number}[]}
 */
export function buildShuffledDeck(seed) {
  const deck = buildCanonicalDeck();
  const rand = mulberry32(seed);
  for (let i = deck.length - 1; i > 0; i--) {
    const j = nextInt(rand, i + 1);
    const tmp = deck[i];
    deck[i] = deck[j];
    deck[j] = tmp;
  }
  return deck;
}

/**
 * Verifica la integridad de un mazo (108 cartas, conteos exactos).
 * Se usa en los tests y como aserción defensiva en el arranque de partida.
 */
export function validateDeck(deck) {
  const errors = [];
  if (deck.length !== 108) errors.push(`expected 108 cards, got ${deck.length}`);

  const ids = new Set(deck.map((c) => c.id));
  if (ids.size !== deck.length) errors.push('duplicate card ids');

  const counts = new Map();
  for (const c of deck) {
    const key = `${c.color}:${c.value}`;
    counts.set(key, (counts.get(key) ?? 0) + 1);
  }

  for (const color of COLORS) {
    const zero = counts.get(`${color}:${Value.Zero}`) ?? 0;
    if (zero !== 1) errors.push(`color ${color}: expected 1 Zero, got ${zero}`);
    for (let v = 1; v <= 9; v++) {
      const n = counts.get(`${color}:${v}`) ?? 0;
      if (n !== 2) errors.push(`color ${color}: expected 2 of ${v}, got ${n}`);
    }
    for (const v of [Value.Skip, Value.Reverse, Value.DrawTwo]) {
      const n = counts.get(`${color}:${v}`) ?? 0;
      if (n !== 2) errors.push(`color ${color}: expected 2 of ${VALUE_NAMES[v]}, got ${n}`);
    }
  }
  const wild = counts.get(`${Color.Wild}:${Value.Wild}`) ?? 0;
  const w4 = counts.get(`${Color.Wild}:${Value.WildDrawFour}`) ?? 0;
  if (wild !== 4) errors.push(`expected 4 Wild, got ${wild}`);
  if (w4 !== 4) errors.push(`expected 4 Wild Draw Four, got ${w4}`);

  return { ok: errors.length === 0, errors };
}

/** Descripción legible para logs y tests: "Red 7", "Blue Skip", "Wild Draw Four". */
export function cardLabel(card) {
  if (!card) return '<none>';
  if (card.value === Value.Wild) return 'Wild';
  if (card.value === Value.WildDrawFour) return 'Wild Draw Four';
  return `${COLOR_NAMES[card.color]} ${VALUE_NAMES[card.value]}`;
}
