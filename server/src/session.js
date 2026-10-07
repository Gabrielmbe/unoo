/**
 * SESSION / GAME SERVER — capa de red independiente del transporte.
 *
 * Este módulo NO sabe nada de WebSockets: habla con una función `send(obj)`.
 * Eso permite:
 *   - testear toda la lógica de sala/turno con conexiones reales sin abrir puertos,
 *   - cambiar de transporte (ws hoy, Mirror/KCP mañana) sin tocar las reglas,
 *   - reutilizarlo tal cual en un servidor dedicado de Unity si se quisiera.
 *
 * SEGURIDAD (lo importante):
 *   - El servidor deriva el asiento desde el `playerId` de la conexión. El cliente
 *     nunca dice "soy el jugador 1"; si lo intenta, se ignora.
 *   - Las manos ajenas nunca salen del servidor (ver `_viewOf`).
 *   - Cada conexión tiene límite de mensajes/segundo y de tamaño de paquete.
 *   - Un mensaje inválido produce `error` y se descarta: nunca rompe la sala.
 */

import { randomBytes, randomUUID } from 'node:crypto';
import { Game, Phase } from './game.js';
import { Lobby, normalizeCode } from './lobby.js';
import {
  C2S,
  HEARTBEAT_MS,
  MAX_MESSAGES_PER_SEC,
  PROTOCOL_VERSION,
  RECONNECT_GRACE_MS,
  S2C,
  sanitizeRules,
  validate,
} from './protocol.js';
import { withRules } from './rules.js';

export class Connection {
  constructor({ id, send, server }) {
    this.id = id;
    this.send = send;
    this.server = server;
    this.playerId = null;
    this.reconnectToken = null;
    this.roomCode = null;
    this.name = 'Player';
    this.avatarId = 'default';
    this.alive = true;
    this.lastSeen = Date.now();
    this.lastPing = 0;
    this._recent = [];
  }

  /** Anti-spam: ventana deslizante de 1 s. */
  _allow() {
    const now = this.server.now();
    this._recent = this._recent.filter((t) => now - t < 1000);
    if (this._recent.length >= MAX_MESSAGES_PER_SEC) return false;
    this._recent.push(now);
    return true;
  }

  post(obj) {
    if (!this.alive) return;
    try {
      this.send(obj);
    } catch {
      this.alive = false;
    }
  }

  error(code, message, ref) {
    this.post({ t: S2C.Error, code, message, ref });
  }

  /** Punto de entrada único de la red. */
  handle(raw) {
    this.lastSeen = this.server.now();
    if (!this._allow()) {
      this.error('RATE_LIMITED', 'Demasiados mensajes');
      return;
    }
    let msg;
    try {
      msg = typeof raw === 'string' ? JSON.parse(raw) : raw;
    } catch {
      this.error('BAD_JSON', 'JSON inválido');
      return;
    }
    if (!msg || typeof msg !== 'object' || typeof msg.t !== 'string') {
      this.error('BAD_PAYLOAD', 'Falta el campo "t"');
      return;
    }
    const invalid = validate(msg.t, msg);
    if (invalid) {
      this.error(invalid, 'Mensaje inválido', msg.t);
      return;
    }
    try {
      this.server.route(this, msg);
    } catch (err) {
      // Un bug del servidor no puede tirar la sala entera.
      this.server.log?.('error', `route ${msg.t}: ${err?.stack ?? err}`);
      this.error('INTERNAL', 'Error interno', msg.t);
    }
  }
}

export class GameServer {
  /**
   * @param {object} [opts]
   * @param {Lobby} [opts.lobby]
   * @param {() => number} [opts.now]
   * @param {(level:string,msg:string)=>void} [opts.log]
   * @param {number} [opts.reconnectGraceMs]
   */
  constructor({ lobby, now = () => Date.now(), log, reconnectGraceMs = RECONNECT_GRACE_MS } = {}) {
    this.lobby = lobby ?? new Lobby({ now });
    this.now = now;
    this.log = log ?? (() => {});
    this.reconnectGraceMs = reconnectGraceMs;
    /** @type {Set<Connection>} */
    this.connections = new Set();
    /** @type {Map<string, Connection>} */
    this.byToken = new Map();
    /** @type {Map<string, Connection>} */
    this.byPlayerId = new Map();
    /** @type {Map<string, NodeJS.Timeout>} */
    this._graceTimers = new Map();
  }

  // ------------------------------------------------------------ lifecycle ---

  /** Crea una conexión. `send` recibe objetos ya serializables. */
  connect(send) {
    const conn = new Connection({ id: randomUUID(), send, server: this });
    this.connections.add(conn);
    return conn;
  }

  disconnect(conn, { permanent = false } = {}) {
    if (!conn.alive) return;
    conn.alive = false;
    this.connections.delete(conn);

    const room = conn.roomCode ? this.lobby.getRoom(conn.roomCode) : null;
    if (!room || !conn.playerId) {
      this._forget(conn);
      return;
    }

    const entry = room.players.get(conn.playerId);
    if (entry) {
      entry.session = null;
      entry.connected = false;
    }
    this.byPlayerId.delete(conn.playerId);
    this._notifyPeers(room, conn.playerId, false);

    if (permanent || room.phase !== 'playing') {
      this._leaveRoom(room, conn.playerId);
      return;
    }

    // Partida en curso: damos un margen para reconectar (corte de wifi, cambio de app).
    this._log('info', `${entry?.name ?? conn.playerId} se desconectó; grace ${this.reconnectGraceMs}ms`);
    const timer = setTimeout(() => {
      this._graceTimers.delete(conn.playerId);
      const r = this.lobby.getRoom(room.code);
      if (r && r.players.has(conn.playerId) && !r.players.get(conn.playerId).session) {
        this._leaveRoom(r, conn.playerId);
      }
    }, this.reconnectGraceMs);
    if (typeof timer.unref === 'function') timer.unref();
    this._graceTimers.set(conn.playerId, timer);
  }

  _forget(conn) {
    if (conn.reconnectToken) this.byToken.delete(conn.reconnectToken);
    if (conn.playerId) this.byPlayerId.delete(conn.playerId);
  }

  _leaveRoom(room, playerId) {
    const timer = this._graceTimers.get(playerId);
    if (timer) {
      clearTimeout(timer);
      this._graceTimers.delete(playerId);
    }
    const entry = room.players.get(playerId);
    const conn = entry?.session;
    room.removePlayer(playerId);
    if (conn) {
      conn.roomCode = null;
      conn.post({ t: S2C.RoomClosed, code: room.code, reason: 'left' });
    }
    this._log('info', `sala ${room.code}: sale ${entry?.name ?? playerId}`);

    if (room.isEmpty) {
      this.lobby.deleteRoom(room.code);
      return;
    }
    this._broadcastLobby(room);
    if (room.phase === 'playing' && room.game) {
      this._broadcast(room, { t: S2C.PeerState, playerId, connected: false, left: true });
    }
  }

  _notifyPeers(room, playerId, connected) {
    for (const p of room.players.values()) {
      if (p.id === playerId) continue;
      p.session?.post({ t: S2C.PeerState, playerId, connected });
    }
  }

  // ------------------------------------------------------------- routing ---

  route(conn, msg) {
    switch (msg.t) {
      case C2S.Hello: return this._hello(conn, msg);
      case C2S.Reconnect: return this._reconnect(conn, msg);
      case C2S.Ping:
        return conn.post({ t: S2C.Pong, c: msg.c, serverTime: this.now() });
      case C2S.Resync: return this._resync(conn);
      default: break;
    }

    if (!conn.playerId) return conn.error('NOT_IDENTIFIED', 'Envía hello primero');

    switch (msg.t) {
      case C2S.CreateRoom: return this._createRoom(conn, msg);
      case C2S.JoinRoom: return this._joinRoom(conn, msg);
      case C2S.Leave: return this._leave(conn);
      case C2S.SetReady: return this._setReady(conn, msg);
      case C2S.StartGame: return this._startGame(conn);
      case C2S.PlayCard: return this._playCard(conn, msg);
      case C2S.Draw: return this._draw(conn);
      case C2S.Pass: return this._pass(conn);
      case C2S.CallUno: return this._callUno(conn);
      case C2S.CatchUno: return this._catchUno(conn);
      case C2S.Challenge: return this._challenge(conn, msg);
      case C2S.Emote: return this._emote(conn, msg);
      default: return conn.error('UNKNOWN_TYPE', `Tipo desconocido: ${msg.t}`);
    }
  }

  _hello(conn, msg) {
    conn.playerId = conn.playerId ?? randomUUID();
    conn.reconnectToken = randomBytes(16).toString('base64url');
    conn.name = msg.name.trim().slice(0, 24) || 'Player';
    conn.avatarId = typeof msg.avatarId === 'string' ? msg.avatarId.slice(0, 32) : 'default';
    this.byToken.set(conn.reconnectToken, conn);
    this.byPlayerId.set(conn.playerId, conn);
    conn.post({
      t: S2C.Welcome,
      protocol: PROTOCOL_VERSION,
      playerId: conn.playerId,
      reconnectToken: conn.reconnectToken,
      serverTime: this.now(),
      heartbeatMs: HEARTBEAT_MS,
    });
    this._log('info', `hola ${conn.name} (${conn.playerId.slice(0, 8)})`);
  }

  /**
   * Reconexión: el cliente guarda su `reconnectToken` en memoria persistente.
   * Si se cae el wifi del móvil, al volver recupera SU asiento y SU mano exacta.
   */
  _reconnect(conn, msg) {
    const prev = this.byToken.get(msg.reconnectToken);
    if (!prev) return conn.error('BAD_TOKEN', 'Sesión expirada, vuelve a entrar por código');

    conn.playerId = prev.playerId;
    conn.reconnectToken = prev.reconnectToken;
    conn.name = prev.name;
    conn.avatarId = prev.avatarId;
    this.byPlayerId.set(conn.playerId, conn);

    if (prev !== conn) {
      prev.alive = false;
      this.connections.delete(prev);
    }

    const room = prev.roomCode ? this.lobby.getRoom(prev.roomCode) : this.lobby.findByPlayer(conn.playerId);
    conn.post({
      t: S2C.Welcome,
      protocol: PROTOCOL_VERSION,
      playerId: conn.playerId,
      reconnectToken: conn.reconnectToken,
      serverTime: this.now(),
      heartbeatMs: HEARTBEAT_MS,
      reconnected: true,
    });

    if (!room) {
      conn.post({ t: S2C.RoomClosed, code: null, reason: 'expired' });
      return;
    }

    conn.roomCode = room.code;
    const entry = room.players.get(conn.playerId);
    if (entry) {
      entry.session = conn;
      entry.connected = true;
      entry.name = conn.name;
    }
    const timer = this._graceTimers.get(conn.playerId);
    if (timer) {
      clearTimeout(timer);
      this._graceTimers.delete(conn.playerId);
    }
    this._notifyPeers(room, conn.playerId, true);
    this._log('info', `${conn.name} reconectó a ${room.code}`);

    conn.post({ t: S2C.LobbyState, room: room.lobbyState() });
    if (room.phase === 'playing' && room.game) {
      conn.post({ t: S2C.Snapshot, game: room.game.snapshot(this._seatOf(room, conn.playerId)) });
    }
  }

  _createRoom(conn, msg) {
    if (conn.roomCode) return conn.error('ALREADY_IN_ROOM', 'Ya estás en una sala');
    const rules = withRules(sanitizeRules(msg.rules));
    const room = this.lobby.createRoom(conn.playerId, rules);
    conn.roomCode = room.code;
    room.addPlayer({
      id: conn.playerId,
      name: conn.name,
      avatarId: conn.avatarId,
      ready: false,
      session: conn,
    });
    this._log('info', `sala ${room.code} creada por ${conn.name}`);
    this._broadcastLobby(room);
  }

  _joinRoom(conn, msg) {
    if (conn.roomCode) return conn.error('ALREADY_IN_ROOM', 'Ya estás en una sala');
    const code = normalizeCode(msg.code);
    const room = this.lobby.getRoom(code);
    if (!room) return conn.error('NO_ROOM', 'Esa sala no existe o ya cerró');
    if (room.phase === 'playing') return conn.error('ROOM_BUSY', 'La partida ya empezó');
    if (room.isFull) return conn.error('ROOM_FULL', 'La sala está llena');
    if (room.players.has(conn.playerId)) return conn.error('ALREADY_IN_ROOM', 'Ya estás dentro');

    conn.roomCode = room.code;
    room.addPlayer({
      id: conn.playerId,
      name: conn.name,
      avatarId: conn.avatarId,
      ready: false,
      session: conn,
    });
    this._log('info', `${conn.name} entró en ${room.code}`);
    this._broadcastLobby(room);
  }

  _leave(conn) {
    const room = conn.roomCode ? this.lobby.getRoom(conn.roomCode) : null;
    if (!room) return;
    this._leaveRoom(room, conn.playerId);
  }

  _setReady(conn, msg) {
    const room = this._room(conn);
    if (!room) return;
    if (room.phase !== 'lobby') return conn.error('BAD_PHASE', 'La partida ya empezó');
    const entry = room.players.get(conn.playerId);
    if (!entry) return;
    entry.ready = msg.ready;
    this._broadcastLobby(room);
  }

  _startGame(conn) {
    const room = this._room(conn);
    if (!room) return;
    if (room.hostId !== conn.playerId) return conn.error('NOT_HOST', 'Sólo el anfitrión puede empezar');
    if (room.phase !== 'lobby') return conn.error('BAD_PHASE', 'La partida ya empezó');
    if (room.playerList.length < 2) return conn.error('NEED_PLAYERS', 'Faltan jugadores');
    if (room.playerList.some((p) => !p.ready && p.id !== room.hostId)) {
      return conn.error('NOT_READY', 'Hay jugadores sin estar listos');
    }

    const players = room.playerList.map((p) => ({ id: p.id, name: p.name, avatarId: p.avatarId }));
    const game = new Game({
      players,
      rules: room.rules,
      seed: this._seed(),
      now: this.now,
    });
    room.game = game;
    room.phase = 'playing';
    room.touch();

    const events = game.startRound();
    this._log('info', `sala ${room.code}: empieza la partida (${players.length} jugadores)`);

    // Cada cliente recibe su propio game_start con SU mano.
    for (const p of room.playerList) {
      p.session?.post({
        t: S2C.GameStart,
        game: game.snapshot(this._seatOf(room, p.id)),
      });
    }
    this._flush(room, events);
  }

  _playCard(conn, msg) {
    const ctx = this._gameCtx(conn);
    if (!ctx) return;
    const { room, game, seat } = ctx;
    const res = game.playCard(seat, msg.cardId, msg.chosenColor ?? undefined, msg.swapTargetSeat ?? undefined);
    this._resolve(conn, room, res);
  }

  _draw(conn) {
    const ctx = this._gameCtx(conn);
    if (!ctx) return;
    this._resolve(conn, ctx.room, ctx.game.draw(ctx.seat));
  }

  _pass(conn) {
    const ctx = this._gameCtx(conn);
    if (!ctx) return;
    this._resolve(conn, ctx.room, ctx.game.pass(ctx.seat));
  }

  _callUno(conn) {
    const ctx = this._gameCtx(conn, { allowChallenge: true });
    if (!ctx) return;
    this._resolve(conn, ctx.room, ctx.game.callUno(ctx.seat));
  }

  _catchUno(conn) {
    const ctx = this._gameCtx(conn, { allowChallenge: true });
    if (!ctx) return;
    this._resolve(conn, ctx.room, ctx.game.catchUno(ctx.seat));
  }

  _challenge(conn, msg) {
    const ctx = this._gameCtx(conn, { allowChallenge: true });
    if (!ctx) return;
    this._resolve(conn, ctx.room, ctx.game.resolveChallenge(ctx.seat, msg.accept));
  }

  _emote(conn, msg) {
    const room = this._room(conn);
    if (!room) return;
    for (const p of room.playerList) {
      if (p.id === conn.playerId) continue;
      p.session?.post({ t: 'emote', playerId: conn.playerId, id: msg.id });
    }
  }

  _resync(conn) {
    const room = this._room(conn);
    if (!room) return;
    if (room.phase === 'playing' && room.game) {
      conn.post({ t: S2C.Snapshot, game: room.game.snapshot(this._seatOf(room, conn.playerId)) });
    } else {
      this._broadcastLobby(room);
    }
  }

  // --------------------------------------------------------------- helpers ---

  _seed() {
    return randomBytes(4).readInt32BE(0);
  }

  _room(conn) {
    if (!conn.roomCode) {
      conn.error('NO_ROOM', 'No estás en ninguna sala');
      return null;
    }
    const room = this.lobby.getRoom(conn.roomCode);
    if (!room) {
      conn.roomCode = null;
      conn.error('NO_ROOM', 'La sala ya no existe');
      return null;
    }
    return room;
  }

  _seatOf(room, playerId) {
    return room.game ? room.game.players.findIndex((p) => p.id === playerId) : -1;
  }

  _gameCtx(conn, { allowChallenge = false } = {}) {
    const room = this._room(conn);
    if (!room) return null;
    if (!room.game) {
      conn.error('BAD_PHASE', 'La partida no ha empezado');
      return null;
    }
    const game = room.game;
    if (!allowChallenge && game.phase !== Phase.Playing) {
      conn.error('BAD_PHASE', `Fase ${game.phase}`);
      return null;
    }
    const seat = this._seatOf(room, conn.playerId);
    if (seat < 0) {
      conn.error('NOT_IN_GAME', 'No estás en esta partida');
      return null;
    }
    return { room, game, seat };
  }

  /** Aplica el resultado de una acción y emite los eventos resultantes. */
  _resolve(conn, room, res) {
    room.touch();
    if (!res.ok) {
      conn.error(res.error, `Jugada rechazada: ${res.error}`);
      return;
    }
    this._flush(room, res.events);
    if (room.game.phase === Phase.MatchOver) {
      // La sala vuelve al lobby para la revancha.
      room.phase = 'lobby';
      room.game = null;
      for (const p of room.playerList) p.ready = p.id === room.hostId;
      this._broadcastLobby(room);
    }
  }

  /**
   * Reparte un lote de eventos. Aquí ocurre el filtrado por vista:
   * la información privada de un jugador NUNCA viaja al otro.
   */
  _flush(room, events) {
    if (!events || events.length === 0) return;
    for (const p of room.playerList) {
      const seat = this._seatOf(room, p.id);
      if (seat < 0) continue;
      const views = events.map((e) => this._viewOf(e, seat)).filter(Boolean);
      if (views.length === 0) continue;
      p.session?.post(views.length === 1 ? { t: S2C.Event, e: views[0] } : { t: S2C.Batch, e: views });
    }
  }

  /**
   * Filtra un evento para el asiento `seat`.
   * - `cards_drawn.cards` sólo lo ve quien roba.
   * - `challenge_result.revealed` es público a propósito (es la mecánica del reto).
   */
  _viewOf(event, seat) {
    if (event.type === 'cards_drawn' && event.seat !== seat) {
      const { cards, ...rest } = event;
      return rest;
    }
    return event;
  }

  _broadcastLobby(room) {
    const state = { t: S2C.LobbyState, room: room.lobbyState() };
    for (const p of room.playerList) p.session?.post(state);
  }

  _broadcast(room, msg) {
    for (const p of room.playerList) p.session?.post(msg);
  }

  _log(level, msg) {
    try {
      this.log?.(level, msg);
    } catch {
      /* el logging nunca debe romper una sala */
    }
  }

  /** Reloj del servidor: timeouts de turno y ventanas de UNO. */
  tick(now = this.now()) {
    for (const room of this.lobby.rooms.values()) {
      if (room.phase !== 'playing' || !room.game) continue;
      const events = room.game.tick(now);
      if (events.length) {
        room.touch();
        this._flush(room, events);
      }
      if (room.game.phase === Phase.MatchOver) {
        room.phase = 'lobby';
        room.game = null;
        for (const p of room.playerList) p.ready = p.id === room.hostId;
        this._broadcastLobby(room);
      }
    }
  }

  /** Latido: detecta móviles que se fueron a segundo plano sin cerrar el socket. */
  heartbeat(now = this.now(), { ping, terminate }) {
    for (const conn of this.connections) {
      if (now - conn.lastSeen > HEARTBEAT_MS * 2) {
        terminate?.(conn);
        continue;
      }
      ping?.(conn);
    }
  }
}
