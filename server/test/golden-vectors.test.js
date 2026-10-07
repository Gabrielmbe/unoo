/**
 * GOLDEN VECTORS — contrato compartido entre el servidor y el cliente Unity.
 *
 * shared/golden-vectors.json se genera desde esta implementación (que es la que
 * está cubierta por toda la suite) y lo consume TAMBIÉN el proyecto de tests C#
 * (unity-client/Tests/Core/GoldenVectorsTests.cs) en CI.
 *
 * Sirve para exactamente una cosa: si alguien cambia DeckManager.cs o
 * RulesEngine.cs y los aleja del servidor, el build de Unity falla. Sin esto, la
 * divergencia sólo aparecería en producción como "el móvil grisó una carta que el
 * PC sí dejaba jugar".
 */

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import { buildCanonicalDeck, buildShuffledDeck, validateDeck } from '../src/deck.js';
import { advanceSeat, canPlay, resolveSeatAfterPlay, scoreHand, withRules } from '../src/rules.js';
import { mulberry32 } from '../src/rng.js';

const path = fileURLToPath(new URL('../../shared/golden-vectors.json', import.meta.url));
const vectors = JSON.parse(readFileSync(path, 'utf8'));

function fnv1a(str) {
  let h = 0x811c9dc5;
  for (let i = 0; i < str.length; i++) {
    h ^= str.charCodeAt(i);
    h = Math.imul(h, 0x01000193) >>> 0;
  }
  return (h >>> 0).toString(16).padStart(8, '0');
}

test('la composición del mazo coincide con el contrato', () => {
  const deck = buildCanonicalDeck();
  assert.equal(deck.length, vectors.deckComposition.total);
  const perColor = [0, 0, 0, 0];
  let wild = 0;
  let w4 = 0;
  for (const c of deck) {
    if (c.color === 4) {
      if (c.value === 13) wild++;
      else w4++;
    } else perColor[c.color]++;
  }
  assert.deepEqual(perColor, [25, 25, 25, 25]);
  for (const color of [0, 1, 2, 3]) assert.equal(perColor[color], vectors.deckComposition.perColor);
  assert.equal(wild, vectors.deckComposition.wild);
  assert.equal(w4, vectors.deckComposition.wildDrawFour);
  assert.equal(validateDeck(deck).ok, true);
});

test('el RNG coincide con los vectores para todas las semillas', () => {
  for (const [seed, expected] of Object.entries(vectors.rng)) {
    const r = mulberry32(Number(seed));
    for (let i = 0; i < expected.length; i++) {
      assert.equal(r(), expected[i], `seed ${seed} valor ${i}`);
    }
  }
});

test('el orden del mazo barajado coincide con los vectores', () => {
  for (const [seed, expected] of Object.entries(vectors.decks)) {
    const deck = buildShuffledDeck(Number(seed));
    assert.deepEqual(
      deck.slice(0, 20).map((c) => c.id),
      expected.first20,
      `seed ${seed}: primeros 20 ids`,
    );
    assert.equal(
      fnv1a(deck.map((c) => c.id).join(',')),
      expected.fnv1a,
      `seed ${seed}: hash del mazo completo`,
    );
  }
});

test('el avance de asiento coincide con la tabla exhaustiva', () => {
  for (const c of vectors.seatTable) {
    assert.equal(
      advanceSeat(c.seat, c.count, c.direction, c.playerCount),
      c.expected,
      JSON.stringify(c),
    );
  }
});

test('la resolución de turno coincide para cada tipo de carta', () => {
  for (const c of vectors.resolveTable) {
    const r = resolveSeatAfterPlay({
      value: c.value,
      seat: 0,
      direction: c.direction,
      playerCount: c.playerCount,
    });
    assert.equal(r.nextSeat, c.nextSeat, `nextSeat ${JSON.stringify(c)}`);
    assert.equal(r.direction, c.newDirection, `direction ${JSON.stringify(c)}`);
    assert.equal(r.samePlayerAgain, c.samePlayerAgain, `samePlayerAgain ${JSON.stringify(c)}`);
  }
});

test('la puntuación de manos coincide con los vectores', () => {
  for (const c of vectors.scoring) {
    assert.equal(scoreHand(c.hand), c.expected, JSON.stringify(c.hand));
  }
});

test('la legalidad de jugadas coincide con los vectores', () => {
  const defaultRules = withRules();
  const relaxed = withRules({ wild4Restriction: false });
  for (const c of vectors.legalCases) {
    const rules = c.rules === 'relaxed' ? relaxed : defaultRules;
    const verdict = canPlay(c.card, c.top, c.topColor, c.hand, rules);
    assert.equal(verdict.legal, c.expected, `${c.name}: esperado ${c.expected}, obtenido ${verdict.legal} (${verdict.reason ?? 'ok'})`);
  }
});
