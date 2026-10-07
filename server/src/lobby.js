/**
 * LOBBY — salas privadas por código de invitación.
 *
 * Decisiones de diseño para un host barato (Render, 512 MB, sin BD):
 *  - TODO vive en memoria. Una sala son ~2 KB; 5.000 salas son ~10 MB.
 *  - Los códigos son de 6 caracteres con un alfabeto sin caracteres ambiguos
 *    (sin 0/O/1/I/L/U), que es lo que hace falta para leerlo por teléfono:
 *    "oye, entra en K7M3PQ".
 *  - TTL por inactividad: si nadie toca la sala en 30 min, se libera.
 *  - Migración de host: si el creador se va, el otro jugador hereda la sala
 *    en lugar de dejarla huérfana.
 *
 * Lo que NO hace (y por qué): persistencia. Un reinicio del servicio pierde las
 * salas. Para un juego por invitación entre dos personas eso es aceptable y
 * evita depender de Redis/Postgres. El doc de despliegue explica cómo añadir
 * Redis cuando se necesite escalar a varias instancias.
 */

import { randomBytes } from 'node:crypto';
import { ROOM_IDLE_TTL_MS } from './protocol.js';

/** Alfabeto sin 0/O/1/I/L/U para evitar confusiones al dictar el código. */
const CODE_ALPHABET = 'ABCDEFGHJKMNPQRSTVWXYZ23456789';
const CODE_LENGTH = 6;

export function generateCode(rand = randomBytes) {
  const bytes = rand(CODE_LENGTH);
  let out = '';
  for (let i = 0; i < CODE_LENGTH; i++) out += CODE_ALPHABET[bytes[i] % CODE_ALPHABET.length];
  return out;
}

export function normalizeCode(raw) {
  return String(raw ?? '')
    .toUpperCase()
    .replace(/[^A-Z0-9]/g, '')
    .slice(0, 8);
}

export class Room {
  constructor(code, hostId, rules) {
    this.code = code;
    this.hostId = hostId;
    this.rules = rules;
    /** @type {Map<string, {id:string,name:string,avatarId?:string,ready:boolean,session:any}>} */
    this.players = new Map();
    this.game = null;
    this.phase = 'lobby'; // 'lobby' | 'playing'
    this.createdAt = Date.now();
    this.lastActivity = Date.now();
    this.maxPlayers = 2;
  }

  touch() {
    this.lastActivity = Date.now();
  }

  get playerList() {
    return [...this.players.values()];
  }

  get isFull() {
    return this.players.size >= this.maxPlayers;
  }

  get isEmpty() {
    return this.players.size === 0;
  }

  addPlayer(player) {
    this.players.set(player.id, player);
    this.touch();
  }

  removePlayer(id) {
    this.players.delete(id);
    if (this.hostId === id) {
      const next = this.playerList[0];
      this.hostId = next ? next.id : null;
    }
    this.touch();
  }

  /** Estado público de la sala (sin manos ni mazo). */
  lobbyState() {
    return {
      code: this.code,
      hostId: this.hostId,
      phase: this.phase,
      maxPlayers: this.maxPlayers,
      rules: this.rules,
      players: this.playerList.map((p) => ({
        id: p.id,
        name: p.name,
        avatarId: p.avatarId ?? 'default',
        ready: !!p.ready,
        connected: !!(p.session && p.session.alive),
      })),
    };
  }
}

export class Lobby {
  constructor({ now = () => Date.now(), codeLength = CODE_LENGTH } = {}) {
    /** @type {Map<string, Room>} */
    this.rooms = new Map();
    this.now = now;
    this.codeLength = codeLength;
    this._timer = null;
  }

  get size() {
    return this.rooms.size;
  }

  _uniqueCode() {
    for (let attempt = 0; attempt < 32; attempt++) {
      const code = generateCode();
      if (!this.rooms.has(code)) return code;
    }
    // Colisión persistente: alargar el código en vez de fallar.
    let code = generateCode();
    while (this.rooms.has(code)) code += CODE_ALPHABET[Math.floor(Math.random() * CODE_ALPHABET.length)];
    return code;
  }

  createRoom(hostId, rules) {
    const code = this._uniqueCode();
    const room = new Room(code, hostId, rules);
    this.rooms.set(code, room);
    return room;
  }

  getRoom(code) {
    return this.rooms.get(normalizeCode(code)) ?? null;
  }

  /** Encuentra la sala en la que está un jugador (para reconexiones). */
  findByPlayer(playerId) {
    for (const room of this.rooms.values()) {
      if (room.players.has(playerId)) return room;
    }
    return null;
  }

  deleteRoom(code) {
    this.rooms.delete(code);
  }

  /** Limpieza periódica de salas abandonadas. */
  sweep(maxIdleMs = ROOM_IDLE_TTL_MS) {
    const cutoff = this.now() - maxIdleMs;
    const removed = [];
    for (const [code, room] of this.rooms) {
      const stale = room.lastActivity < cutoff;
      const orphan = room.isEmpty;
      if (stale || orphan) {
        removed.push(code);
        this.rooms.delete(code);
      }
    }
    return removed;
  }

  /** Arranca el barrido. `unref` para que no mantenga vivo el proceso en tests. */
  startSweeper(intervalMs = 60_000, maxIdleMs = ROOM_IDLE_TTL_MS) {
    this.stopSweeper();
    this._timer = setInterval(() => this.sweep(maxIdleMs), intervalMs);
    if (typeof this._timer.unref === 'function') this._timer.unref();
    return this._timer;
  }

  stopSweeper() {
    if (this._timer) {
      clearInterval(this._timer);
      this._timer = null;
    }
  }
}
