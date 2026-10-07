import test from 'node:test';
import assert from 'node:assert/strict';

import { Color, Value } from '../src/deck.js';
import {
  DEFAULT_RULES,
  advanceSeat,
  canPlay,
  canStackOn,
  legalPlays,
  matchesTop,
  resolveSeatAfterPlay,
  scoreHand,
  settleRound,
  stackablePlays,
  withRules,
} from '../src/rules.js';

const c = (color, value) => ({ id: `t${Math.random()}`, color, value });

test('coincidencia por color o por número', () => {
  const top = c(Color.Red, 7);
  assert.equal(matchesTop(c(Color.Red, 3), top, Color.Red), true, 'mismo color');
  assert.equal(matchesTop(c(Color.Blue, 7), top, Color.Red), true, 'mismo número');
  assert.equal(matchesTop(c(Color.Blue, 3), top, Color.Red), false);
});

test('el color EFECTIVO manda sobre un comodín ya jugado', () => {
  const top = c(Color.Wild, Value.Wild); // comodín que declaró Azul
  assert.equal(matchesTop(c(Color.Blue, 1), top, Color.Blue), true);
  assert.equal(matchesTop(c(Color.Red, 1), top, Color.Blue), false);
});

test('Wild puro es siempre legal', () => {
  const top = c(Color.Green, 5);
  assert.equal(canPlay(c(Color.Wild, Value.Wild), top, Color.Green, [], DEFAULT_RULES).legal, true);
});

test('Wild Draw Four se bloquea si tienes carta del color activo (regla oficial)', () => {
  const top = c(Color.Yellow, 2);
  const hand = [c(Color.Yellow, 8), c(Color.Wild, Value.WildDrawFour)];
  const withRestriction = withRules({ wild4Restriction: true });
  const relaxed = withRules({ wild4Restriction: false });

  assert.equal(canPlay(hand[1], top, Color.Yellow, hand, withRestriction).legal, false);
  assert.equal(canPlay(hand[1], top, Color.Yellow, hand, relaxed).legal, true);
});

test('Wild Draw Four es legal sin cartas del color activo', () => {
  const top = c(Color.Yellow, 2);
  const hand = [c(Color.Red, 8), c(Color.Blue, 1), c(Color.Wild, Value.WildDrawFour)];
  assert.equal(canPlay(hand[2], top, Color.Yellow, hand, withRules({ wild4Restriction: true })).legal, true);
});

test('carta que no casa devuelve NO_MATCH', () => {
  const top = c(Color.Red, 7);
  const v = canPlay(c(Color.Blue, 3), top, Color.Red, [], DEFAULT_RULES);
  assert.equal(v.legal, false);
  assert.equal(v.reason, 'NO_MATCH');
});

test('apilado: +2 sobre +2 sí, +2 sobre +4 no', () => {
  assert.equal(canStackOn(c(Color.Red, Value.DrawTwo), c(Color.Blue, Value.DrawTwo)), true);
  assert.equal(canStackOn(c(Color.Wild, Value.WildDrawFour), c(Color.Blue, Value.DrawTwo)), false);
  assert.equal(canStackOn(c(Color.Wild, Value.WildDrawFour), c(Color.Wild, Value.WildDrawFour)), true);
  assert.equal(canStackOn(c(Color.Red, 4), c(Color.Blue, Value.DrawTwo)), false);
});

test('advanceSeat envuelve bien en ambas direcciones', () => {
  assert.equal(advanceSeat(3, 1, 1, 4), 0);
  assert.equal(advanceSeat(0, 1, -1, 4), 3);
  assert.equal(advanceSeat(2, 2, -1, 4), 0);
  assert.equal(advanceSeat(0, 5, 1, 4), 1);
});

test('2 jugadores: Skip y Reverse devuelven el turno al mismo jugador', () => {
  const skip = resolveSeatAfterPlay({ value: Value.Skip, seat: 0, direction: 1, playerCount: 2 });
  assert.equal(skip.nextSeat, 0);
  assert.equal(skip.samePlayerAgain, true);

  const rev = resolveSeatAfterPlay({ value: Value.Reverse, seat: 0, direction: 1, playerCount: 2 });
  assert.equal(rev.nextSeat, 0);
  assert.equal(rev.direction, -1);
});

test('4 jugadores: Skip salta y Reverse invierte el sentido', () => {
  const skip = resolveSeatAfterPlay({ value: Value.Skip, seat: 0, direction: 1, playerCount: 4 });
  assert.equal(skip.nextSeat, 2);

  const rev = resolveSeatAfterPlay({ value: Value.Reverse, seat: 0, direction: 1, playerCount: 4 });
  assert.equal(rev.direction, -1);
  assert.equal(rev.nextSeat, 3);
});

test('puntuación de mano según tabla oficial', () => {
  const hand = [c(Color.Red, 8), c(Color.Blue, 3), c(Color.Green, Value.Skip), c(Color.Wild, Value.Wild)];
  assert.equal(scoreHand(hand), 8 + 3 + 20 + 50);
});

test('el ganador suma las manos de todos los rivales', () => {
  const hands = new Map([
    [0, [c(Color.Red, 0)]],
    [1, [c(Color.Blue, 9), c(Color.Wild, Value.WildDrawFour)]],
    [2, [c(Color.Yellow, Value.Reverse)]],
  ]);
  const s = settleRound(hands, 0);
  assert.equal(s.winnerSeat, 0);
  assert.equal(s.gained, 9 + 50 + 20);
  assert.equal(s.breakdown.length, 2);
});

test('legalPlays y stackablePlays filtran la mano correctamente', () => {
  const top = c(Color.Red, 7);
  const hand = [
    c(Color.Red, 1),
    c(Color.Blue, 7),
    c(Color.Green, 4),
    c(Color.Wild, Value.Wild),
  ];
  assert.equal(legalPlays(hand, top, Color.Red, DEFAULT_RULES).length, 3);

  const pending = c(Color.Blue, Value.DrawTwo);
  const hand2 = [c(Color.Yellow, Value.DrawTwo), c(Color.Red, 3)];
  assert.equal(stackablePlays(hand2, pending).length, 1);
});
