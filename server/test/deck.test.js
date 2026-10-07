import test from 'node:test';
import assert from 'node:assert/strict';

import {
  buildCanonicalDeck,
  buildShuffledDeck,
  cardLabel,
  Color,
  Value,
  pointValue,
  validateDeck,
} from '../src/deck.js';
import { mulberry32 } from '../src/rng.js';

test('el mazo canónico tiene exactamente 108 cartas y composición oficial', () => {
  const deck = buildCanonicalDeck();
  assert.equal(deck.length, 108);

  const check = validateDeck(deck);
  assert.deepEqual(check.errors, [], `errores de composición: ${check.errors.join('; ')}`);
  assert.equal(check.ok, true);
});

test('reparto por color: 25 cartas de cada color + 4 Wild + 4 Wild Draw Four', () => {
  const deck = buildCanonicalDeck();
  const byColor = [0, 0, 0, 0, 0];
  for (const c of deck) byColor[c.color]++;
  assert.deepEqual(byColor, [25, 25, 25, 25, 8]);
});

test('puntuación oficial: número=valor, acción=20, comodín=50', () => {
  assert.equal(pointValue(Value.Zero), 0);
  assert.equal(pointValue(Value.Nine), 9);
  assert.equal(pointValue(Value.Skip), 20);
  assert.equal(pointValue(Value.Reverse), 20);
  assert.equal(pointValue(Value.DrawTwo), 20);
  assert.equal(pointValue(Value.Wild), 50);
  assert.equal(pointValue(Value.WildDrawFour), 50);
});

test('el barajado es determinista para una misma semilla', () => {
  const a = buildShuffledDeck(12345);
  const b = buildShuffledDeck(12345);
  assert.deepEqual(a.map((c) => c.id), b.map((c) => c.id));
});

test('semillas distintas producen mazos distintos', () => {
  const a = buildShuffledDeck(1).map((c) => c.id).join(',');
  const b = buildShuffledDeck(2).map((c) => c.id).join(',');
  assert.notEqual(a, b);
});

test('barajar preserva la composición del mazo', () => {
  const check = validateDeck(buildShuffledDeck(987654321));
  assert.equal(check.ok, true, check.errors.join('; '));
});

test('mulberry32 es reproducible y queda dentro de [0,1)', () => {
  const r1 = mulberry32(42);
  const r2 = mulberry32(42);
  for (let i = 0; i < 1000; i++) {
    const v = r1();
    assert.ok(v >= 0 && v < 1, `fuera de rango: ${v}`);
    assert.equal(v, r2());
  }
});

test('cardLabel describe bien números y especiales', () => {
  assert.equal(cardLabel({ color: Color.Red, value: 7 }), 'Red 7');
  assert.equal(cardLabel({ color: Color.Blue, value: Value.Skip }), 'Blue Skip');
  assert.equal(cardLabel({ color: Color.Wild, value: Value.WildDrawFour }), 'Wild Draw Four');
});
