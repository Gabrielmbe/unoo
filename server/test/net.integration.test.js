/**
 * TEST DE INTEGRACIÓN — abre un servidor REAL en un puerto efímero y juega una
 * partida completa con clientes WebSocket reales.
 *
 * Esto es lo que valida de verdad la ruta de red: upgrade HTTP->WS, protocolo
 * JSON, lobby por código, validación autoritativa de jugadas, filtrado por vista
 * (anti-cheat), límites de tasa, reconexión y /health.
 */

import test from 'node:test';
import assert from 'node:assert/strict';
import { setTimeout as delay } from 'node:timers/promises';
import WebSocket from 'ws';

import { createServer } from '../src/index.js';
import { C2S, S2C } from '../src/protocol.js';

let clockBase = 1_700_000_000_000;
function fakeNow() {
  return clockBase;
}

async function startApp() {
  const app = createServer({ now: fakeNow, log: () => {} });
  const { port } = await app.listen(0, '127.0.0.1');
  return { app, port, url: `ws://127.0.0.1:${port}/ws`, http: `http://127.0.0.1:${port}` };
}

class Client {
  constructor(url, name) {
    this.url = url;
    this.name = name;
    this.inbox = [];      // mensajes pendientes de consumir
    this.received = [];   // historial completo, para inspeccionar eventos
    this._waiters = [];
    this.ws = new WebSocket(url);
    this.ready = new Promise((resolve, reject) => {
      this.ws.on('open', resolve);
      this.ws.on('error', reject);
    });
    this.ws.on('message', (raw) => {
      const msg = JSON.parse(raw.toString('utf8'));
      this.received.push(msg);
      // Un waiter registrado tiene prioridad sobre la bandeja: así cada mensaje
      // se consume exactamente una vez y no se cuenta dos veces.
      for (let i = 0; i < this._waiters.length; i++) {
        if (this._waiters[i].test(msg)) {
          const [w] = this._waiters.splice(i, 1);
          w.resolve(msg);
          return;
        }
      }
      this.inbox.push(msg);
    });
  }

  async open() {
    await this.ready;
    return this;
  }

  send(t, extra = {}) {
    this.ws.send(JSON.stringify({ t, ...extra }));
  }

  /** Espera el siguiente mensaje que cumpla el predicado (o uno ya recibido). */
  expect(predicate, { timeout = 2000, label = '' } = {}) {
    const at = this.inbox.findIndex((m) => predicate(m));
    if (at >= 0) return Promise.resolve(this.inbox.splice(at, 1)[0]);

    return new Promise((resolve, reject) => {
      const entry = { test: predicate, resolve };
      const timer = setTimeout(() => {
        this._waiters = this._waiters.filter((w) => w !== entry);
        reject(
          new Error(
            `timeout esperando ${label || 'mensaje'}; recibidos: ${JSON.stringify(this.inbox.map((m) => m.t ?? m.code))}`,
          ),
        );
      }, timeout);
      entry.resolve = (msg) => {
        clearTimeout(timer);
        resolve(msg);
      };
      this._waiters.push(entry);
    });
  }

  /** Todos los eventos de juego recibidos desde el inicio (historial completo). */
  events() {
    const out = [];
    for (const m of this.received) {
      if (m.t === S2C.Event) out.push(m.e);
      else if (m.t === S2C.Batch) out.push(...m.e);
    }
    return out;
  }

  /** Espera un mensaje Event/Batch que contenga un evento concreto. */
  expectEvent(predicate, { timeout = 2000, label = '' } = {}) {
    const matches = (m) => {
      if (m.t === S2C.Event) return predicate(m.e);
      if (m.t === S2C.Batch) return m.e.some(predicate);
      return false;
    };
    return this.expect(matches, { timeout, label: label || 'evento de juego' });
  }

  close() {
    this.ws.close();
  }
}

test('GET /health responde ok con métricas', async () => {
  const { app, http } = await startApp();
  try {
    const res = await fetch(`${http}/health`);
    assert.equal(res.status, 200);
    const body = await res.json();
    assert.equal(body.ok, true);
    assert.equal(typeof body.rooms, 'number');
    assert.equal(typeof body.connections, 'number');
  } finally {
    await app.close();
  }
});

test('ruta desconocida responde 404', async () => {
  const { app, http } = await startApp();
  try {
    const res = await fetch(`${http}/nope`);
    assert.equal(res.status, 404);
  } finally {
    await app.close();
  }
});

test('upgrade a WS fuera de /ws se rechaza', async () => {
  const { app, port } = await startApp();
  try {
    const bad = new WebSocket(`ws://127.0.0.1:${port}/otracosa`);
    const result = await new Promise((resolve) => {
      bad.on('error', () => resolve('error'));
      bad.on('open', () => resolve('open'));
    });
    assert.equal(result, 'error');
  } finally {
    await app.close();
  }
});

test('flujo completo: crear sala, entrar por código y jugar una partida', async () => {
  const { app, url } = await startApp();
  let a;
  let b;
  try {
    a = await new Client(url, 'Ana').open();
    b = await new Client(url, 'Bruno').open();

    a.send(C2S.Hello, { name: 'Ana' });
    const wa = await a.expect((m) => m.t === S2C.Welcome, { label: 'welcome A' });
    assert.equal(wa.protocol, 1);
    assert.ok(wa.reconnectToken, 'debe dar token de reconexión');

    b.send(C2S.Hello, { name: 'Bruno' });
    await b.expect((m) => m.t === S2C.Welcome, { label: 'welcome B' });

    // Acciones antes de identificarse no deberían pasar, pero hello ya está hecho.
    a.send(C2S.CreateRoom, { rules: { stacking: true, turnSeconds: 30 } });
    const lobbyA = await a.expect((m) => m.t === S2C.LobbyState, { label: 'lobby_state A' });
    const code = lobbyA.room.code;
    assert.match(code, /^[A-Z2-9]{6}$/, 'código de 6 chars sin caracteres ambiguos');
    assert.equal(lobbyA.room.rules.stacking, true);
    assert.equal(lobbyA.room.rules.turnSeconds, 30);

    b.send(C2S.JoinRoom, { code });
    const lobbyB = await b.expect((m) => m.t === S2C.LobbyState, { label: 'lobby_state B' });
    assert.equal(lobbyB.room.players.length, 2);

    // Entrar con un código inexistente falla de forma controlada.
    const c = await new Client(url, 'Carla').open();
    c.send(C2S.Hello, { name: 'Carla' });
    await c.expect((m) => m.t === S2C.Welcome);
    c.send(C2S.JoinRoom, { code: 'ZZZZZZ' });
    const err = await c.expect((m) => m.t === S2C.Error, { label: 'error sala' });
    assert.equal(err.code, 'NO_ROOM');

    // Sala llena: un tercero no entra.
    const d = await new Client(url, 'Dario').open();
    d.send(C2S.Hello, { name: 'Dario' });
    await d.expect((m) => m.t === S2C.Welcome);
    d.send(C2S.JoinRoom, { code });
    const full = await d.expect((m) => m.t === S2C.Error, { label: 'sala llena' });
    assert.equal(full.code, 'ROOM_FULL');

    // Empezar sin estar listos falla.
    a.send(C2S.StartGame);
    const notReady = await a.expect((m) => m.t === S2C.Error, { label: 'not ready' });
    assert.equal(notReady.code, 'NOT_READY');

    b.send(C2S.SetReady, { ready: true });
    await b.expect((m) => m.t === S2C.LobbyState && m.room.players.every((p) => p.ready || p.id === lobbyA.room.hostId));

    a.send(C2S.StartGame);
    const startA = await a.expect((m) => m.t === S2C.GameStart, { label: 'game_start A' });
    const startB = await b.expect((m) => m.t === S2C.GameStart, { label: 'game_start B' });

    // OJO: la mano puede NO ser de 7. Si la carta inicial es un +2, el primer
    // jugador roba 2 y se queda con 9 (regla oficial). Lo que sí es invariante es
    // que ambos clientes coincidan en los conteos y que sólo el dueño vea su mano.
    const aSeat = startA.game.seat;
    const bSeat = startB.game.seat;
    assert.notEqual(aSeat, bSeat, 'cada cliente tiene su propio asiento');

    assert.ok(Array.isArray(startA.game.players[aSeat].hand), 'Ana ve su propia mano');
    assert.equal(startA.game.players[bSeat].hand, undefined, 'Ana NO ve la mano de Bruno');
    assert.equal(startB.game.players[aSeat].hand, undefined, 'Bruno NO ve la mano de Ana');
    assert.ok(Array.isArray(startB.game.players[bSeat].hand), 'Bruno ve la suya');

    // La longitud de la mano propia coincide con el handSize publicado.
    assert.equal(startA.game.players[aSeat].hand.length, startA.game.players[aSeat].handSize);
    assert.equal(startB.game.players[bSeat].hand.length, startB.game.players[bSeat].handSize);

    // Y ambos clientes coinciden en el recuento ajeno (consistencia de estado).
    assert.equal(startA.game.players[bSeat].handSize, startB.game.players[bSeat].handSize);
    assert.equal(startB.game.players[aSeat].handSize, startA.game.players[aSeat].handSize);

    // El total de cartas se conserva: manos + mazo + descarte = 108.
    const totalA =
      startA.game.players.reduce((n, p) => n + p.handSize, 0) + startA.game.deckCount + 1;
    assert.equal(totalA, 108, 'deben seguir en juego las 108 cartas');

    assert.ok(startA.game.turnDeadline > clockBase, 'hay deadline de turno');

    // Jugar una carta ilegal se rechaza y el estado no cambia.
    const game = app.lobby.getRoom(code).game;
    const turnSeat = game.currentSeat;
    const turnPlayer = turnSeat === startA.game.seat ? a : b;
    const other = turnSeat === startA.game.seat ? b : a;
    const otherSeat = other === a ? startA.game.seat : startB.game.seat;

    const otherHandCard = game.players[otherSeat].hand[0];
    turnPlayer.send(C2S.PlayCard, { cardId: otherHandCard.id });
    const cheat = await turnPlayer.expect((m) => m.t === S2C.Error, { label: 'anti-cheat' });
    assert.equal(cheat.code, 'NOT_IN_HAND', 'no puedes jugar una carta que no está en tu mano');

    // Carta inventada también se rechaza.
    turnPlayer.send(C2S.PlayCard, { cardId: 'c999' });
    const ghost = await turnPlayer.expect((m) => m.t === S2C.Error, { label: 'carta fantasma' });
    assert.equal(ghost.code, 'NOT_IN_HAND');

    // Jugar fuera de turno se rechaza.
    other.send(C2S.PlayCard, { cardId: game.players[otherSeat].hand[0].id });
    const oot = await other.expect((m) => m.t === S2C.Error, { label: 'fuera de turno' });
    assert.equal(oot.code, 'NOT_YOUR_TURN');

    // Una jugada legal sí pasa y genera eventos para ambos.
    // Se elige con la MISMA lógica que el servidor: primero una carta que case por
    // color o número, después un Wild puro. NO se usa un Wild Draw Four a ciegas:
    // si la mano contiene el color activo, el servidor lo rechaza (regla oficial)
    // y el test fallaría por algo que en realidad funciona bien.
    const hand = game.players[turnSeat].hand;
    const color = game.topColor;
    const top = game.topCard();
    const isPlainMatch = (c) => (c.color === color || c.value === top.value) && c.color !== 4;
    let pick = hand.find(isPlainMatch);
    if (!pick) pick = hand.find((c) => c.value === 13); // Wild puro: siempre legal
    if (!pick) {
      const holdsColor = hand.some((c) => c.color === color && c.color !== 4);
      if (!holdsColor) pick = hand.find((c) => c.value === 14);
    }

    if (!pick) {
      turnPlayer.send(C2S.Draw);
      await turnPlayer.expect((m) => m.t === S2C.Event || m.t === S2C.Batch, { label: 'robo' });
    } else {
      const chosen = pick.color === 4 ? 0 : undefined;
      turnPlayer.send(C2S.PlayCard, { cardId: pick.id, chosenColor: chosen });
      await turnPlayer.expect((m) => m.t === S2C.Event || m.t === S2C.Batch, { label: 'jugada' });
    }

    const evA = a.events();
    const evB = b.events();
    assert.ok(evA.length > 0, 'Ana recibe eventos');
    assert.ok(evB.length > 0, 'Bruno recibe eventos');

    // Filtrado por vista: el robo de uno no filtra sus cartas al otro.
    const drawsA = evA.filter((e) => e.type === 'cards_drawn' && e.seat === startB.game.seat);
    for (const d of drawsA) {
      assert.equal(d.cards, undefined, 'las cartas robadas por el rival son privadas');
    }

    c.close();
    d.close();
  } finally {
    a?.close();
    b?.close();
    await app.close();
  }
});

test('el anfitrión migra si se va y la sala se limpia al quedarse vacía', async () => {
  const { app, url } = await startApp();
  let a;
  let b;
  try {
    a = await new Client(url, 'Ana').open();
    b = await new Client(url, 'Bruno').open();
    a.send(C2S.Hello, { name: 'Ana' });
    const wa = await a.expect((m) => m.t === S2C.Welcome);
    b.send(C2S.Hello, { name: 'Bruno' });
    await b.expect((m) => m.t === S2C.Welcome);

    a.send(C2S.CreateRoom);
    const lobby = await a.expect((m) => m.t === S2C.LobbyState);
    assert.equal(lobby.room.hostId, wa.playerId);

    b.send(C2S.JoinRoom, { code: lobby.room.code });
    await b.expect((m) => m.t === S2C.LobbyState && m.room.players.length === 2);

    a.close();
    await delay(80);

    const afterLeave = await b.expect(
      (m) => m.t === S2C.LobbyState && m.room.players.length === 1,
      { label: 'lobby tras salida' },
    );
    assert.equal(afterLeave.room.hostId, afterLeave.room.players[0].id, 'el anfitrión migra al que queda');
    assert.notEqual(app.lobby.getRoom(lobby.room.code), null);

    b.close();
    await delay(80);
    assert.equal(app.lobby.getRoom(lobby.room.code), null, 'la sala se libera al vaciarse');
  } finally {
    a?.close();
    b?.close();
    await app.close();
  }
});

test('un cliente sin hello no puede crear sala', async () => {
  const { app, url } = await startApp();
  let a;
  try {
    a = await new Client(url, 'Ana').open();
    a.send(C2S.CreateRoom);
    const err = await a.expect((m) => m.t === S2C.Error);
    assert.equal(err.code, 'NOT_IDENTIFIED');
  } finally {
    a?.close();
    await app.close();
  }
});

test('JSON malformado y tipos desconocidos se descartan sin tirar la conexión', async () => {
  const { app, url } = await startApp();
  let a;
  try {
    a = await new Client(url, 'Ana').open();
    a.ws.send('{esto no es json');
    const bad = await a.expect((m) => m.t === S2C.Error);
    assert.equal(bad.code, 'BAD_JSON');

    a.ws.send(JSON.stringify({ foo: 'bar' }));
    const noType = await a.expect((m) => m.t === S2C.Error);
    assert.equal(noType.code, 'BAD_PAYLOAD');

    a.send('invento_raro');
    const unknown = await a.expect((m) => m.t === S2C.Error);
    assert.equal(unknown.code, 'NOT_IDENTIFIED');

    // La conexión sigue viva.
    a.send(C2S.Hello, { name: 'Ana' });
    const w = await a.expect((m) => m.t === S2C.Welcome);
    assert.ok(w.playerId);
  } finally {
    a?.close();
    await app.close();
  }
});

test('el límite de mensajes por segundo corta el spam', async () => {
  const { app, url } = await startApp();
  let a;
  try {
    a = await new Client(url, 'Ana').open();
    for (let i = 0; i < 40; i++) a.send(C2S.Ping, { c: i });
    const limited = await a.expect((m) => m.t === S2C.Error && m.code === 'RATE_LIMITED', {
      label: 'rate limit',
    });
    assert.equal(limited.code, 'RATE_LIMITED');
  } finally {
    a?.close();
    await app.close();
  }
});

test('reconexión con token recupera asiento y mano', async () => {
  const { app, url } = await startApp();
  let a;
  let b;
  let a2;
  try {
    a = await new Client(url, 'Ana').open();
    b = await new Client(url, 'Bruno').open();
    a.send(C2S.Hello, { name: 'Ana' });
    const wa = await a.expect((m) => m.t === S2C.Welcome);
    b.send(C2S.Hello, { name: 'Bruno' });
    await b.expect((m) => m.t === S2C.Welcome);

    a.send(C2S.CreateRoom);
    const lobby = await a.expect((m) => m.t === S2C.LobbyState);
    b.send(C2S.JoinRoom, { code: lobby.room.code });
    await b.expect((m) => m.t === S2C.LobbyState && m.room.players.length === 2);

    b.send(C2S.SetReady, { ready: true });
    await delay(30);
    a.send(C2S.StartGame);
    const start = await a.expect((m) => m.t === S2C.GameStart);
    const myHand = start.game.players.find((p) => p.hand).hand.map((c) => c.id);

    // Ana "pierde el wifi" (corte brusco, sin close limpio).
    a.ws.terminate();
    await delay(60);
    const peerSeen = await b.expect((m) => m.t === S2C.PeerState && m.connected === false, {
      label: 'peer_state desconectado',
    });
    assert.ok(peerSeen.playerId);

    // Vuelve con su token.
    a2 = await new Client(url, 'Ana').open();
    a2.send(C2S.Reconnect, { reconnectToken: wa.reconnectToken });
    const w2 = await a2.expect((m) => m.t === S2C.Welcome && m.reconnected === true, {
      label: 'welcome reconectado',
    });
    assert.equal(w2.playerId, wa.playerId);

    const snap = await a2.expect((m) => m.t === S2C.Snapshot, { label: 'snapshot' });
    const recovered = snap.game.players.find((p) => p.hand).hand.map((c) => c.id);
    assert.deepEqual(recovered, myHand, 'debe recuperar exactamente su mano');

    const revived = await b.expect((m) => m.t === S2C.PeerState && m.connected === true, {
      label: 'peer_state reconectado',
    });
    assert.ok(revived.playerId);

    // Token falso rechazado.
    const impostor = await new Client(url, 'Malo').open();
    impostor.send(C2S.Reconnect, { reconnectToken: 'token-falso' });
    const bad = await impostor.expect((m) => m.t === S2C.Error);
    assert.equal(bad.code, 'BAD_TOKEN');
    impostor.close();
  } finally {
    a?.close();
    a2?.close();
    b?.close();
    await app.close();
  }
});

test('el timeout de turno juega automáticamente por el jugador ausente', async () => {
  const { app, url } = await startApp();
  let a;
  let b;
  try {
    a = await new Client(url, 'Ana').open();
    b = await new Client(url, 'Bruno').open();
    a.send(C2S.Hello, { name: 'Ana' });
    await a.expect((m) => m.t === S2C.Welcome);
    b.send(C2S.Hello, { name: 'Bruno' });
    await b.expect((m) => m.t === S2C.Welcome);

    a.send(C2S.CreateRoom, { rules: { turnSeconds: 8 } });
    const lobby = await a.expect((m) => m.t === S2C.LobbyState);
    b.send(C2S.JoinRoom, { code: lobby.room.code });
    await b.expect((m) => m.t === S2C.LobbyState && m.room.players.length === 2);
    b.send(C2S.SetReady, { ready: true });
    await delay(30);
    a.send(C2S.StartGame);
    await a.expect((m) => m.t === S2C.GameStart);
    await b.expect((m) => m.t === S2C.GameStart);

    const game = app.lobby.getRoom(lobby.room.code).game;
    const seqBefore = game.seq;
    const turnBefore = game.turnIndex;
    const handSizesBefore = game.players.map((p) => p.hand.length);

    // Avanzamos el reloj por encima del deadline y damos cuerda al tick.
    clockBase += 8_001;
    app.game.tick();

    assert.ok(game.seq > seqBefore, 'el tick debe producir eventos');
    // OJO: no se puede afirmar que cambie el asiento. A 2 jugadores, si la
    // auto-jugada es un Skip o un Reverse, el turno vuelve al mismo jugador
    // (regla oficial de UNO a 2). La señal fiable de progreso es turnIndex.
    assert.ok(game.turnIndex > turnBefore || game.phase !== 'playing', 'debe abrirse un turno nuevo');
    const moved = game.players.some((p, i) => p.hand.length !== handSizesBefore[i]);
    assert.ok(moved || game.discardPile.length > 1, 'la auto-jugada debe jugar o robar');

    // Los eventos viajan por WebSocket: hay que esperar a que lleguen de verdad,
    // no basta con consultar la bandeja (sería una condición de carrera).
    const isTimeout = (e) => e.type === 'turn_timeout';
    await a.expectEvent(isTimeout, { label: 'turn_timeout en A' });
    await b.expectEvent(isTimeout, { label: 'turn_timeout en B' });

    assert.ok(a.events().some(isTimeout), 'Ana ve el timeout');
    assert.ok(b.events().some(isTimeout), 'Bruno ve el timeout');
    assert.ok(
      a.events().some((e) => e.type === 'turn_start'),
      'tras el timeout se abre un turno nuevo',
    );
  } finally {
    a?.close();
    b?.close();
    await app.close();
  }
});

test('sanitizeRules ignora basura y limita rangos', async () => {
  const { sanitizeRules } = await import('../src/protocol.js');
  const out = sanitizeRules({
    stacking: 'sí por favor',
    wild4Challenge: true,
    turnSeconds: 99999,
    targetScore: -5,
    handSize: 'muchas',
    inyeccion: '<script>',
  });
  assert.equal(out.stacking, undefined, 'los no-booleanos se descartan');
  assert.equal(out.wild4Challenge, true);
  assert.equal(out.turnSeconds, 120, 'se recorta al máximo');
  assert.equal(out.targetScore, 50, 'se recorta al mínimo');
  assert.equal(out.handSize, 7, 'una entrada no numérica cae al valor por defecto');
  assert.equal(out.inyeccion, undefined, 'las claves desconocidas no pasan');
});
