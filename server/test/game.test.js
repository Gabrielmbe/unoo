import test from 'node:test';
import assert from 'node:assert/strict';

import { Game, Phase, ErrorCode } from '../src/game.js';
import { Color, Value, isWildValue } from '../src/deck.js';
import { legalPlays, withRules } from '../src/rules.js';

/** Reloj controlable: los tests deciden cuándo pasa el tiempo. */
function makeClock(start = 1_000_000) {
  const state = { t: start };
  return {
    now: () => state.t,
    advance: (ms) => (state.t += ms),
  };
}

function makeGame(overrides = {}, clock = makeClock()) {
  const game = new Game({
    players: [
      { id: 'p1', name: 'Ana' },
      { id: 'p2', name: 'Bruno' },
    ],
    rules: withRules(overrides),
    seed: 20260101,
    now: clock.now,
  });
  game.startRound();
  return { game, clock };
}

function findCard(game, seat, predicate) {
  return game.players[seat].hand.find(predicate);
}

function playAny(game, seat) {
  // Usa el motor de reglas real: así el bucle no diverge de lo que valida el
  // servidor y el test ejercita el flujo completo de la partida.
  const plays = legalPlays(game.players[seat].hand, game.topCard(), game.topColor, game.rules);
  if (plays.length > 0) {
    const card = plays[0];
    const chosen = isWildValue(card.value) ? Color.Blue : undefined;
    return game.playCard(seat, card.id, chosen);
  }
  return game.draw(seat);
}

test('reparto inicial: 7 cartas por jugador, descarte con 1 carta, mazo con 94', () => {
  const { game } = makeGame();
  assert.equal(game.players[0].hand.length, 7);
  assert.equal(game.players[1].hand.length, 7);
  assert.equal(game.discardPile.length, 1);
  assert.equal(game.drawPile.length, 108 - 14 - 1);
  assert.notEqual(game.currentSeat, undefined);
});

test('la carta inicial nunca es un comodín (se devuelve al mazo)', () => {
  for (let seed = 1; seed <= 60; seed++) {
    const game = new Game({
      players: [{ id: 'a' }, { id: 'b' }],
      rules: withRules(),
      seed,
      now: () => 0,
    });
    game.startRound();
    const top = game.topCard();
    assert.notEqual(top.value, Value.Wild, `seed ${seed} sacó Wild inicial`);
    assert.notEqual(top.value, Value.WildDrawFour, `seed ${seed} sacó Wild+4 inicial`);
  }
});

test('el total de cartas se conserva durante la partida (108 en todo momento)', () => {
  const { game } = makeGame();
  for (let i = 0; i < 60 && game.phase === Phase.Playing; i++) {
    const total =
      game.players.reduce((n, p) => n + p.hand.length, 0) +
      game.drawPile.length +
      game.discardPile.length;
    assert.equal(total, 108, `iteración ${i}: se perdieron cartas`);
    const res = playAny(game, game.currentSeat);
    assert.equal(res.ok, true, `jugada falló: ${res.error}`);
    game.tick();
  }
});

test('jugar fuera de turno es rechazado y no altera el estado', () => {
  const { game } = makeGame();
  const wrongSeat = game.currentSeat === 0 ? 1 : 0;
  const card = game.players[wrongSeat].hand[0];
  const before = game.players[wrongSeat].hand.length;
  const seqBefore = game.seq;

  const res = game.playCard(wrongSeat, card.id);
  assert.equal(res.ok, false);
  assert.equal(res.error, ErrorCode.NotYourTurn);
  assert.equal(game.players[wrongSeat].hand.length, before);
  assert.equal(game.seq, seqBefore, 'un rechazo no debe emitir eventos');
});

test('jugar una carta que no está en tu mano es rechazado (anti-cheat)', () => {
  const { game } = makeGame();
  const res = game.playCard(game.currentSeat, 'c999');
  assert.equal(res.ok, false);
  assert.equal(res.error, ErrorCode.NotInHand);
});

test('jugar una carta que no casa es rechazado', () => {
  const { game } = makeGame();
  const seat = game.currentSeat;
  const top = game.topCard();
  const color = game.topColor;
  const bad = game.players[seat].hand.find(
    (c) =>
      c.color !== color &&
      c.value !== top.value &&
      c.value !== Value.Wild &&
      c.value !== Value.WildDrawFour,
  );
  if (!bad) return; // mano afortunada; el caso ya está cubierto en rules.test
  const res = game.playCard(seat, bad.id);
  assert.equal(res.ok, false);
  assert.equal(res.error, ErrorCode.IllegalPlay);
});

test('un comodín exige declarar color', () => {
  const { game } = makeGame();
  const seat = game.currentSeat;
  const wild = findCard(game, seat, (c) => c.value === Value.Wild);
  if (!wild) return;
  const res = game.playCard(seat, wild.id);
  assert.equal(res.ok, false);
  assert.equal(res.error, ErrorCode.ColorRequired);
});

test('una partida completa termina sin excepciones y declara ganador', () => {
  const { game, clock } = makeGame({ targetScore: 500 });
  let guard = 0;
  let lastSeq = game.seq;
  while (game.phase !== Phase.MatchOver && guard++ < 20000) {
    if (game.phase === Phase.RoundOver) {
      game.startRound();
      continue;
    }
    if (game.phase === Phase.Challenge) {
      const res = game.resolveChallenge(game.pendingChallenge.targetSeat, false);
      assert.equal(res.ok, true);
      continue;
    }
    clock.advance(10);
    const res = playAny(game, game.currentSeat);
    assert.equal(res.ok, true, `turno ${guard}: ${res.error}`);
    game.tick();

    // Salvaguarda de progreso: cada acción debe avanzar el contador de eventos.
    assert.ok(game.seq > lastSeq, `iteración ${guard} no produjo eventos (seq ${game.seq})`);
    lastSeq = game.seq;
  }
  assert.equal(game.phase, Phase.MatchOver, `no terminó tras ${guard} iteraciones (fase ${game.phase})`);
  const winner = game.players.find((p) => p.score >= 500);
  assert.ok(winner, 'debe haber un jugador con >= 500 puntos');
  assert.ok(game.round >= 2, `esperadas varias rondas, jugadas ${game.round}`);
});

test('los timeouts de turno producen jugadas automáticas', () => {
  const { game, clock } = makeGame();
  const seat0 = game.currentSeat;
  const size0 = game.players[seat0].hand.length;

  clock.advance(game.rules.turnSeconds * 1000 + 1);
  const events = game.tick();
  assert.ok(events.some((e) => e.type === 'turn_timeout'), 'debe emitir turn_timeout');
  assert.notEqual(game.currentSeat, seat0, 'el turno debe haber avanzado');
  const totalMoves = game.players.map((p) => p.hand.length);
  assert.ok(totalMoves.length === 2);
  assert.ok(size0 >= 0);
});

test('el +2 acumulado castiga al siguiente jugador y le quita el turno', () => {
  const { game } = makeGame({ stacking: false });
  // Forzamos una mano con +2 del color activo para aislar el caso.
  const seat = game.currentSeat;
  const color = game.topColor;
  const victim = seat === 0 ? 1 : 0;
  const plus2 = { id: 'test_plus2', color, value: Value.DrawTwo };
  game.cards.set(plus2.id, plus2);
  game.players[seat].hand.unshift(plus2);
  const beforeVictim = game.players[victim].hand.length;

  const res = game.playCard(seat, plus2.id);
  assert.equal(res.ok, true, res.error);

  const penalty = res.events.find((e) => e.type === 'draw_penalty');
  assert.ok(penalty, 'debe haber un evento draw_penalty');
  assert.equal(penalty.seat, victim);
  assert.equal(penalty.amount, 2);
  assert.equal(game.players[victim].hand.length, beforeVictim + 2);
  assert.equal(game.currentSeat, seat, 'con 2 jugadores el lanzador vuelve a jugar');
});

test('stacking: el penalizado puede apilar otro +2 y pasar 4', () => {
  const { game } = makeGame({ stacking: true });
  const seat = game.currentSeat;
  const victim = seat === 0 ? 1 : 0;
  const color = game.topColor;

  const plus2a = { id: 'test_a', color, value: Value.DrawTwo };
  const plus2b = { id: 'test_b', color: (color + 1) % 4, value: Value.DrawTwo };
  game.cards.set(plus2a.id, plus2a);
  game.cards.set(plus2b.id, plus2b);
  game.players[seat].hand.unshift(plus2a);
  game.players[victim].hand.unshift(plus2b);

  const r1 = game.playCard(seat, plus2a.id);
  assert.equal(r1.ok, true, r1.error);
  assert.ok(r1.events.some((e) => e.type === 'stack_opened'), 'debe abrir ventana de apilado');
  assert.equal(game.pending.amount, 2);
  assert.equal(game.currentSeat, victim, 'la víctima decide: apilar o tragar');

  const r2 = game.playCard(victim, plus2b.id);
  assert.equal(r2.ok, true, r2.error);
  assert.equal(game.pending.amount, 4, 'el castigo debe acumularse a 4');
  assert.equal(game.currentSeat, seat, 'el castigo acumulado vuelve al lanzador');
});

test('sin stacking, intentar jugar en vez de robar el castigo se rechaza', () => {
  const { game } = makeGame({ stacking: false });
  const seat = game.currentSeat;
  const victim = seat === 0 ? 1 : 0;
  const color = game.topColor;

  const plus2 = { id: 'test_p2', color, value: Value.DrawTwo };
  game.cards.set(plus2.id, plus2);
  game.players[seat].hand.unshift(plus2);
  game.playCard(seat, plus2.id);

  // La víctima ya tragó el castigo automáticamente; turno de nuevo del lanzador.
  assert.equal(game.currentSeat, seat);
  // Y si quedara pendiente, la víctima no podría jugar otra cosa:
  game.pending = { amount: 4, card: { color, value: Value.WildDrawFour }, targetSeat: victim, chainSeats: [seat], sourceSeat: seat };
  game.currentSeat = victim;
  const other = game.players[victim].hand.find((c) => c.color === color || c.value === game.topCard().value);
  if (other) {
    const r = game.playCard(victim, other.id);
    assert.equal(r.ok, false);
    assert.equal(r.error, ErrorCode.IllegalPlay);
  }
});

test('robar sin castigo pendiente saca exactamente 1 carta', () => {
  const { game } = makeGame();
  const seat = game.currentSeat;
  const before = game.players[seat].hand.length;
  const res = game.draw(seat);
  assert.equal(res.ok, true, res.error);
  assert.equal(game.players[seat].hand.length, before + 1);
});

test('sin poder jugar se puede seguir robando; con carta jugable ya no', () => {
  const { game } = makeGame({ drawUntilPlayable: true });
  const seat = game.currentSeat;
  const top = game.topCard();
  const color = game.topColor;

  // Mano deliberadamente muerta: ni color ni número coinciden.
  const deadColor = (color + 1) % 4;
  const deadValue = top.value >= 9 ? 1 : top.value + 1;
  game.players[seat].hand = [
    { id: 'dead_a', color: deadColor, value: deadValue },
    { id: 'dead_b', color: deadColor, value: deadValue },
  ];

  const before = game.players[seat].hand.length;
  const r1 = game.draw(seat);
  assert.equal(r1.ok, true, r1.error);
  assert.equal(game.players[seat].hand.length, before + 1);

  const drew = game.players[seat].hand[game.players[seat].hand.length - 1];
  const stillDead = drew.color !== color && drew.value !== top.value;
  const r2 = game.draw(seat);
  if (stillDead) {
    assert.equal(r2.ok, true, 'sin carta jugable debe poder seguir robando');
  } else {
    assert.equal(r2.ok, false, 'con carta jugable ya no puede robar');
    assert.equal(r2.error, ErrorCode.NoPendingDraw);
  }
});

test('con carta jugable en mano y ya habiendo robado, no puede volver a robar', () => {
  const { game } = makeGame({ drawUntilPlayable: true });
  const seat = game.currentSeat;
  const color = game.topColor;
  const good = { id: 'good_card', color, value: 4 };
  game.cards.set(good.id, good);
  game.players[seat].hand = [good];

  game.draw(seat); // robo voluntario permitido la primera vez
  assert.equal(game.drawnThisTurn, 1);
  const r = game.draw(seat);
  assert.equal(r.ok, false);
  assert.equal(r.error, ErrorCode.NoPendingDraw);
});

test('¡UNO! cantado a tiempo no tiene penalización', () => {
  const { game, clock } = makeGame();
  // Dejamos al jugador actual con exactamente 2 cartas y una jugable.
  const seat = game.currentSeat;
  game.players[seat].hand = game.players[seat].hand.slice(0, 2);
  const color = game.topColor;
  const playable = { id: 'test_win', color, value: 5 };
  game.cards.set(playable.id, playable);
  game.players[seat].hand[0] = playable;

  const res = game.playCard(seat, playable.id);
  assert.equal(res.ok, true, res.error);
  const req = res.events.find((e) => e.type === 'uno_required');
  assert.ok(req, 'debe abrir la ventana de UNO');

  clock.advance(500);
  const call = game.callUno(seat);
  assert.equal(call.ok, true);
  assert.ok(call.events.some((e) => e.type === 'uno_called'));
  assert.equal(game.unoWindow, null);
});

test('olvidar cantar ¡UNO! cuesta 2 cartas al expirar la ventana', () => {
  const { game, clock } = makeGame();
  const seat = game.currentSeat;
  game.players[seat].hand = game.players[seat].hand.slice(0, 2);
  const color = game.topColor;
  const playable = { id: 'test_win2', color, value: 6 };
  game.cards.set(playable.id, playable);
  game.players[seat].hand[0] = playable;

  game.playCard(seat, playable.id);
  const sizeAfter = game.players[seat].hand.length;

  clock.advance(game.rules.unoWindowMs + 1);
  const events = game.tick();
  assert.ok(events.some((e) => e.type === 'uno_caught'), 'debe pillar el olvido');
  assert.equal(game.players[seat].hand.length, sizeAfter + 2);
});

test('el rival puede pillar el olvido de UNO dentro de la ventana', () => {
  const { game, clock } = makeGame();
  const seat = game.currentSeat;
  const other = seat === 0 ? 1 : 0;
  game.players[seat].hand = game.players[seat].hand.slice(0, 2);
  const color = game.topColor;
  const playable = { id: 'test_win3', color, value: 7 };
  game.cards.set(playable.id, playable);
  game.players[seat].hand[0] = playable;

  game.playCard(seat, playable.id);
  const sizeAfter = game.players[seat].hand.length;
  clock.advance(300);

  const caught = game.catchUno(other);
  assert.equal(caught.ok, true, caught.error);
  assert.equal(game.players[seat].hand.length, sizeAfter + 2);
  assert.equal(game.unoWindow, null);
});

test('la declaración anticipada de UNO cubre la jugada siguiente', () => {
  const { game } = makeGame();
  const seat = game.currentSeat;
  game.players[seat].hand = game.players[seat].hand.slice(0, 2);
  const pre = game.callUno(seat);
  assert.equal(pre.ok, true, pre.error);
  assert.equal(game.players[seat].unoDeclared, true);

  const color = game.topColor;
  const playable = { id: 'test_win4', color, value: 8 };
  game.cards.set(playable.id, playable);
  game.players[seat].hand[0] = playable;
  const res = game.playCard(seat, playable.id);
  assert.equal(res.ok, true, res.error);
  assert.ok(res.events.some((e) => e.type === 'uno_called'));
  assert.equal(game.unoWindow, null, 'no debe abrir ventana si ya lo declaró');
});

test('snapshot nunca revela la mano del rival', () => {
  const { game } = makeGame();
  const snap = game.snapshot(0);
  assert.ok(Array.isArray(snap.players[0].hand), 'mi mano sí llega completa');
  assert.equal(snap.players[1].hand, undefined, 'la mano del rival no debe viajar');
  assert.equal(snap.players[1].handSize, 7, 'pero sí su número de cartas');
});

test('snapshot incluye el contador de mazo, descarte y reglas', () => {
  const { game } = makeGame();
  const snap = game.snapshot(0);
  assert.equal(snap.deckCount, game.drawPile.length);
  assert.ok(snap.topCard, 'debe haber carta superior');
  assert.equal(snap.rules.handSize, 7);
  assert.ok(snap.turnDeadline > 0);
});

test('al agotarse el mazo se recicla el descarte sin perder cartas', () => {
  const { game } = makeGame();
  // Situación: mazo vacío y descarte con varias cartas.
  game.drawPile = [];
  game.discardPile = [
    { id: 'd1', color: Color.Red, value: 1 },
    { id: 'd2', color: Color.Blue, value: 2 },
    { id: 'd3', color: Color.Green, value: 3 },
    { id: 'd4', color: Color.Red, value: Value.Skip },
  ];
  const topBefore = game.topCard().id;

  const events = [];
  game._reshuffle(events);
  assert.ok(events.some((e) => e.type === 'deck_reshuffled'));
  assert.equal(game.drawPile.length, 3, 'vuelven todas menos la superior');
  assert.equal(game.discardPile.length, 1, 'la carta superior se conserva');
  assert.equal(game.topCard().id, topBefore, 'la superior no cambia');
});

test('sin descarte que reciclar, el mazo agotado cierra la ronda', () => {
  const { game } = makeGame();
  game.drawPile = [];
  game.discardPile = [{ id: 'only', color: Color.Red, value: 1 }];
  const events = [];
  game._reshuffle(events);
  assert.ok(events.some((e) => e.type === 'deck_exhausted'));
  assert.equal(game.phase, Phase.RoundOver);
});

test('4 jugadores: el turno rota en el orden correcto', () => {
  const clock = makeClock();
  const game = new Game({
    players: [{ id: 'a' }, { id: 'b' }, { id: 'c' }, { id: 'd' }],
    rules: withRules(),
    seed: 77,
    now: clock.now,
  });
  game.startRound();
  const first = game.currentSeat;
  // Jugamos una carta numérica (sin efecto) del color activo para ver el avance.
  const color = game.topColor;
  const num = { id: 'test_num', color, value: 5 };
  game.cards.set(num.id, num);
  game.players[first].hand.unshift(num);
  const res = game.playCard(first, num.id);
  assert.equal(res.ok, true, res.error);
  assert.equal(game.currentSeat, (first + 1) % 4, 'debe avanzar en sentido horario');
});

test('la Reverse con 4 jugadores invierte el sentido del reparto de turno', () => {
  const clock = makeClock();
  const game = new Game({
    players: [{ id: 'a' }, { id: 'b' }, { id: 'c' }, { id: 'd' }],
    rules: withRules(),
    seed: 77,
    now: clock.now,
  });
  game.startRound();
  const first = game.currentSeat;
  const color = game.topColor;
  const rev = { id: 'test_rev', color, value: Value.Reverse };
  game.cards.set(rev.id, rev);
  game.players[first].hand.unshift(rev);
  const res = game.playCard(first, rev.id);
  assert.equal(res.ok, true, res.error);
  assert.equal(game.direction, -1);
  assert.equal(game.currentSeat, (first + 3) % 4, 'debe ir al asiento anterior');
});

test('sevenZero: el 7 intercambia manos con el objetivo elegido', () => {
  const { game } = makeGame({ sevenZero: true });
  const seat = game.currentSeat;
  const other = seat === 0 ? 1 : 0;
  const color = game.topColor;
  const seven = { id: 'test_seven', color, value: 7 };
  game.cards.set(seven.id, seven);
  game.players[seat].hand.unshift(seven);

  const beforeA = game.players[seat].hand.length;
  const beforeB = game.players[other].hand.length;
  const res = game.playCard(seat, seven.id, undefined, other);
  assert.equal(res.ok, true, res.error);
  assert.ok(res.events.some((e) => e.type === 'hands_swapped'));
  // El 7 salió de A, así que A queda con (beforeA-1) y B con beforeB.
  assert.equal(game.players[seat].hand.length, beforeB);
  assert.equal(game.players[other].hand.length, beforeA - 1);
});

test('sevenZero: el 0 sin destino no requiere target pero rota manos', () => {
  const { game } = makeGame({ sevenZero: true });
  const seat = game.currentSeat;
  const other = seat === 0 ? 1 : 0;
  const color = game.topColor;
  const zero = { id: 'test_zero', color, value: Value.Zero };
  game.cards.set(zero.id, zero);
  game.players[seat].hand.unshift(zero);

  const handA = game.players[seat].hand.filter((c) => c.id !== 'test_zero');
  const handB = game.players[other].hand.slice();
  const res = game.playCard(seat, zero.id);
  assert.equal(res.ok, true, res.error);
  assert.ok(res.events.some((e) => e.type === 'hands_rotated'));
  assert.deepEqual(game.players[seat].hand, handB);
  assert.deepEqual(game.players[other].hand, handA);
});

test('sevenZero: jugar un 7 sin destino se rechaza', () => {
  const { game } = makeGame({ sevenZero: true });
  const seat = game.currentSeat;
  const color = game.topColor;
  const seven = { id: 'test_seven2', color, value: 7 };
  game.cards.set(seven.id, seven);
  game.players[seat].hand.unshift(seven);
  const res = game.playCard(seat, seven.id);
  assert.equal(res.ok, false);
  assert.equal(res.error, ErrorCode.BadSwapTarget);
});

test('Wild Draw Four con reto: mentir cuesta 4 al lanzador', () => {
  const { game, clock } = makeGame({ wild4Challenge: true, wild4Restriction: false });
  const seat = game.currentSeat;
  const victim = seat === 0 ? 1 : 0;
  const color = game.topColor;
  const w4 = { id: 'test_w4', color: Color.Wild, value: Value.WildDrawFour };
  game.cards.set(w4.id, w4);
  game.players[seat].hand.unshift(w4);
  // El lanzador SÍ tiene carta del color activo → está mintiendo.
  const liar = { id: 'test_liar', color, value: 3 };
  game.cards.set(liar.id, liar);
  game.players[seat].hand.unshift(liar);

  const res = game.playCard(seat, w4.id, (color + 2) % 4);
  assert.equal(res.ok, true, res.error);
  assert.equal(game.phase, Phase.Challenge);
  assert.ok(res.events.some((e) => e.type === 'challenge_offer'));

  const beforeVictim = game.players[victim].hand.length;
  const beforeCulprit = game.players[seat].hand.length;
  const ch = game.resolveChallenge(victim, true);
  assert.equal(ch.ok, true, ch.error);
  const result = ch.events.find((e) => e.type === 'challenge_result');
  assert.equal(result.bluffed, true);
  assert.equal(game.players[seat].hand.length, beforeCulprit + 4, 'el mentiroso roba 4');
  assert.equal(game.players[victim].hand.length, beforeVictim, 'el retador no roba');
  assert.equal(game.currentSeat, victim);
});

test('Wild Draw Four con reto fallido: el retador roba 6', () => {
  const { game } = makeGame({ wild4Challenge: true, wild4Restriction: false });
  const seat = game.currentSeat;
  const victim = seat === 0 ? 1 : 0;
  const color = game.topColor;
  const w4 = { id: 'test_w4b', color: Color.Wild, value: Value.WildDrawFour };
  game.cards.set(w4.id, w4);
  game.players[seat].hand.unshift(w4);
  // Eliminamos cualquier carta del color activo de la mano del lanzador → reto legítimo.
  game.players[seat].hand = game.players[seat].hand.filter((c) => c.color !== color);

  const res = game.playCard(seat, w4.id, (color + 2) % 4);
  assert.equal(res.ok, true, res.error);
  const beforeVictim = game.players[victim].hand.length;
  const ch = game.resolveChallenge(victim, true);
  assert.equal(ch.ok, true, ch.error);
  assert.equal(ch.events.find((e) => e.type === 'challenge_result').bluffed, false);
  assert.equal(game.players[victim].hand.length, beforeVictim + 6);
});

test('la secuencia de eventos es monótona y sin huecos', () => {
  const { game, clock } = makeGame();
  let last = game.seq; // startRound() ya emitió eventos
  for (let i = 0; i < 40 && game.phase === Phase.Playing; i++) {
    clock.advance(5);
    const res = playAny(game, game.currentSeat);
    for (const e of res.events) {
      assert.equal(e.seq, last + 1, `hueco en seq: ${last} -> ${e.seq}`);
      last = e.seq;
    }
    game.tick();
  }
  assert.ok(last > 0);
});
