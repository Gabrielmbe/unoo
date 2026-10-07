/**
 * PROTOCOLO — contrato único entre el servidor y el cliente Unity.
 *
 * Formato: un mensaje = un objeto JSON con `t` (tipo) y campos planos.
 * JSON en vez de binario porque:
 *   - UNO mueve ~2 mensajes por turno. El ancho de banda es irrelevante
 *     (una jugada típica pesa <200 bytes).
 *   - Un servidor de Render con 512 MB y cientos de salas no se va a ahogar.
 *   - Se depura con un `console.log` y se prueba con wscat desde el móvil.
 * Si algún día se necesitara binario, basta con sustituir el codec aquí:
 * el resto del código habla con estos nombres, no con bytes.
 */

export const PROTOCOL_VERSION = 1;

/** Cliente -> Servidor */
export const C2S = Object.freeze({
  Hello: 'hello',
  CreateRoom: 'create_room',
  JoinRoom: 'join_room',
  Leave: 'leave',
  SetReady: 'set_ready',
  Kick: 'kick',
  StartGame: 'start_game',
  Reconnect: 'reconnect',
  PlayCard: 'play_card',
  Draw: 'draw',
  Pass: 'pass',
  CallUno: 'call_uno',
  CatchUno: 'catch_uno',
  Challenge: 'challenge',
  Emote: 'emote',
  Ping: 'ping',
  Resync: 'resync',
});

/** Servidor -> Cliente */
export const S2C = Object.freeze({
  Welcome: 'welcome',
  LobbyState: 'lobby_state',
  GameStart: 'game_start',
  Event: 'ev',           // evento de juego (card_played, turn_start, ...)
  Batch: 'batch',        // varios eventos juntos (un solo frame de red)
  Snapshot: 'snapshot',  // estado completo tras reconexión
  Error: 'error',
  Pong: 'pong',
  PeerState: 'peer_state',
  RoomClosed: 'room_closed',
});

/** Tipos de evento de juego que el servidor emite desde Game. */
export const GameEvents = Object.freeze([
  'round_start',
  'card_played',
  'color_changed',
  'direction_changed',
  'seat_skipped',
  'draw_penalty',
  'cards_drawn',
  'stack_opened',
  'stacked',
  'uno_required',
  'uno_called',
  'uno_caught',
  'uno_window_closed',
  'challenge_offer',
  'challenge_result',
  'hands_swapped',
  'hands_rotated',
  'deck_reshuffled',
  'deck_exhausted',
  'turn_timeout',
  'turn_start',
  'pass',
  'round_end',
  'match_end',
]);

export const MAX_MESSAGE_BYTES = 8 * 1024;      // una jugada cabe en <200 B
export const MAX_MESSAGES_PER_SEC = 20;          // anti-spam / anti-DoS ligero
export const HEARTBEAT_MS = 25_000;
export const RECONNECT_GRACE_MS = 120_000;       // 2 min para volver tras un corte
export const ROOM_IDLE_TTL_MS = 30 * 60_000;     // 30 min sin actividad: fuera

const PLAYER_NAME_MAX = 24;

/**
 * Validación mínima y barata. Devuelve un string con el error o null si es válido.
 * Nunca se lanza excepción: un paquete malformado se descarta y se responde `error`.
 */
export function validate(type, msg) {
  if (!msg || typeof msg !== 'object') return 'BAD_PAYLOAD';
  switch (type) {
    case C2S.Hello:
      if (typeof msg.name !== 'string' || msg.name.length === 0) return 'BAD_NAME';
      if (msg.name.length > PLAYER_NAME_MAX) return 'NAME_TOO_LONG';
      return null;
    case C2S.CreateRoom:
      if (msg.rules && typeof msg.rules !== 'object') return 'BAD_RULES';
      return null;
    case C2S.JoinRoom:
      return typeof msg.code === 'string' && msg.code.length >= 4 && msg.code.length <= 8
        ? null
        : 'BAD_CODE';
    case C2S.PlayCard:
      if (typeof msg.cardId !== 'string') return 'BAD_CARD';
      if (msg.chosenColor !== undefined && msg.chosenColor !== null) {
        const c = msg.chosenColor;
        if (!Number.isInteger(c) || c < 0 || c > 3) return 'BAD_COLOR';
      }
      if (msg.swapTargetSeat !== undefined && msg.swapTargetSeat !== null) {
        if (!Number.isInteger(msg.swapTargetSeat) || msg.swapTargetSeat < 0 || msg.swapTargetSeat > 3) {
          return 'BAD_SWAP_TARGET';
        }
      }
      return null;
    case C2S.Challenge:
      return typeof msg.accept === 'boolean' ? null : 'BAD_CHALLENGE';
    case C2S.Emote:
      return typeof msg.id === 'string' && msg.id.length <= 32 ? null : 'BAD_EMOTE';
    case C2S.SetReady:
      return typeof msg.ready === 'boolean' ? null : 'BAD_READY';
    default:
      return null;
  }
}

/** Sanea las reglas recibidas del host: sólo claves conocidas y rangos válidos. */
export function sanitizeRules(input = {}) {
  const out = {};
  const bools = [
    'stacking',
    'wild4Restriction',
    'wild4Challenge',
    'drawUntilPlayable',
    'allowPassWithPlay',
    'sevenZero',
    'jumpIn',
  ];
  for (const k of bools) if (typeof input[k] === 'boolean') out[k] = input[k];

  const clamp = (v, lo, hi, dflt) =>
    Number.isFinite(v) ? Math.min(hi, Math.max(lo, Math.trunc(v))) : dflt;

  if (input.turnSeconds !== undefined) out.turnSeconds = clamp(input.turnSeconds, 8, 120, 20);
  if (input.targetScore !== undefined) out.targetScore = clamp(input.targetScore, 50, 5000, 500);
  if (input.handSize !== undefined) out.handSize = clamp(input.handSize, 3, 15, 7);
  if (input.unoWindowMs !== undefined) out.unoWindowMs = clamp(input.unoWindowMs, 1500, 15000, 4000);
  if (input.challengeWindowMs !== undefined) {
    out.challengeWindowMs = clamp(input.challengeWindowMs, 3000, 30000, 8000);
  }
  return out;
}
