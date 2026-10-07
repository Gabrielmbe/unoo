import { buildCanonicalDeck, buildShuffledDeck, validateDeck, cardLabel } from '../src/deck.js';
import { advanceSeat, resolveSeatAfterPlay, scoreHand, settleRound, canPlay, withRules, DEFAULT_RULES } from '../src/rules.js';
import { mulberry32 } from '../src/rng.js';
import { writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const seeds = [0, 1, 42, 12345, 20260101, -7, 2147483647];

// 1) RNG: primeros 12 valores de cada semilla (doble precisión, como JS).
const rng = {};
for (const s of seeds) {
  const r = mulberry32(s);
  rng[String(s)] = Array.from({length: 12}, () => r());
}

// 2) Orden del mazo barajado: los primeros 20 ids + hash de todo el mazo.
function fnv1a(str) {
  let h = 0x811c9dc5;
  for (let i = 0; i < str.length; i++) { h ^= str.charCodeAt(i); h = Math.imul(h, 0x01000193) >>> 0; }
  return (h >>> 0).toString(16).padStart(8, '0');
}
const decks = {};
for (const s of seeds) {
  const d = buildShuffledDeck(s);
  decks[String(s)] = { first20: d.slice(0, 20).map(c => c.id), fnv1a: fnv1a(d.map(c => c.id).join(',')) };
}

// 3) Avance de asiento: tabla exhaustiva 2 y 4 jugadores, ambas direcciones.
const seatTable = [];
for (const n of [2, 3, 4]) {
  for (const dir of [1, -1]) {
    for (let seat = 0; seat < n; seat++) {
      for (const count of [1, 2]) {
        seatTable.push({ playerCount: n, direction: dir, seat, count, expected: advanceSeat(seat, count, dir, n) });
      }
    }
  }
}

// 4) Resolución de turno tras cada tipo de carta.
const resolveTable = [];
for (const value of [3, 7, 10, 11, 12, 13, 14]) {
  for (const n of [2, 4]) {
    for (const dir of [1, -1]) {
      const r = resolveSeatAfterPlay({ value, seat: 0, direction: dir, playerCount: n });
      resolveTable.push({ value, playerCount: n, direction: dir, nextSeat: r.nextSeat, newDirection: r.direction, samePlayerAgain: r.samePlayerAgain });
    }
  }
}

// 5) Puntuación de manos fijas.
const c = (color, value) => ({ color, value });
const scoring = [
  { hand: [c(0,8), c(3,3), c(1,10), c(4,13)], expected: 81 },
  { hand: [c(0,0)], expected: 0 },
  { hand: [c(2,11), c(2,12)], expected: 40 },
  { hand: [c(4,14), c(4,14)], expected: 100 },
  { hand: [c(1,9), c(1,9), c(3,5)], expected: 23 },
];

// 6) Legalidad de jugadas.
const rules = withRules();
const relaxed = withRules({ wild4Restriction: false });
const legalCases = [
  { name: 'mismo color', top: c(0,7), topColor: 0, card: c(0,3), hand: [c(0,3)], expected: true, rules: 'default' },
  { name: 'mismo número', top: c(0,7), topColor: 0, card: c(3,7), hand: [c(3,7)], expected: true, rules: 'default' },
  { name: 'ni color ni número', top: c(0,7), topColor: 0, card: c(3,3), hand: [c(3,3)], expected: false, rules: 'default' },
  { name: 'wild siempre', top: c(1,2), topColor: 1, card: c(4,13), hand: [c(4,13)], expected: true, rules: 'default' },
  { name: '+4 bloqueado con color', top: c(1,2), topColor: 1, card: c(4,14), hand: [c(1,8), c(4,14)], expected: false, rules: 'default' },
  { name: '+4 legal sin color', top: c(1,2), topColor: 1, card: c(4,14), hand: [c(0,8), c(4,14)], expected: true, rules: 'default' },
  { name: '+4 con restricción off', top: c(1,2), topColor: 1, card: c(4,14), hand: [c(1,8), c(4,14)], expected: true, rules: 'relaxed' },
  { name: 'color efectivo tras wild', top: c(4,13), topColor: 3, card: c(3,1), hand: [c(3,1)], expected: true, rules: 'default' },
  { name: 'skip del mismo color', top: c(2,5), topColor: 2, card: c(2,10), hand: [c(2,10)], expected: true, rules: 'default' },
];

const vectors = {
  $schema: 'Vectores dorados compartidos. Generados por server/src (implementación de referencia verificada por la suite de tests).',
  $generatedBy: 'scripts/generate-golden-vectors.mjs',
  $warning: 'NO EDITAR A MANO. Si cambian las reglas, se regeneran y se actualizan AMBAS implementaciones.',
  deckComposition: { total: 108, perColor: 25, wild: 4, wildDrawFour: 4 },
  rng, decks, seatTable, resolveTable, scoring, legalCases,
};
writeFileSync(fileURLToPath(new URL('../../shared/golden-vectors.json', import.meta.url)), JSON.stringify(vectors, null, 2) + '\n');
console.log('golden-vectors.json generado');
console.log('  semillas:', seeds.length, '| casos de asiento:', seatTable.length, '| casos de turno:', resolveTable.length, '| casos de legalidad:', legalCases.length);
console.log('  validación del mazo:', JSON.stringify(validateDeck(buildCanonicalDeck())));
console.log('  ejemplo deck seed=42 primeros 8:', decks['42'].first20.slice(0,8).join(','), 'fnv1a=', decks['42'].fnv1a);
