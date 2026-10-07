/**
 * RULES ENGINE — funciones PURAS. Sin estado, sin red, sin I/O.
 *
 * Este archivo es el corazón anti-cheat: el cliente puede tener una copia
 * espejo para predicción y para habilitar/deshabilitar el drag de cartas
 * ilegales, pero la ÚNICA copia que decide es ésta. Un cliente modificado
 * que mande "play_card c007" cuando c007 no es legal recibe `error ILLEGAL_PLAY`
 * y el servidor ni siquiera toca el estado.
 *
 * Está escrito para traducirse 1:1 a C# (ver Core/RulesEngine.cs) y está
 * cubierto por vectores dorados compartidos (shared/golden-vectors.json).
 */

import {
  Color,
  Value,
  isWildValue,
  isNumberValue,
  isActionValue,
  pointValue,
} from './deck.js';

/** Reglas por defecto = modo Clásico de UNO! Mobile. */
export const DEFAULT_RULES = Object.freeze({
  handSize: 7,
  targetScore: 500,
  turnSeconds: 20,
  unoWindowMs: 4000,
  challengeWindowMs: 8000,

  // --- House rules (las mismas que expone UNO! Mobile en Room Mode) ---
  stacking: false,          // acumular +2 con +2 y +4 con +4
  wild4Restriction: true,   // +4 sólo si no tienes carta del color activo
  wild4Challenge: false,    // el penalizado puede retar y obligar a enseñar mano
  drawUntilPlayable: true,  // oficial: robar hasta poder jugar. false = robar 1 y pasar
  allowPassWithPlay: false, // pasar aun teniendo carta jugable (house rule permisiva)
  sevenZero: false,         // 7 = intercambiar mano, 0 = rotar manos
  jumpIn: false,            // carta idéntima (mismo color y número) fuera de turno
});

export function withRules(overrides = {}) {
  return Object.freeze({ ...DEFAULT_RULES, ...overrides });
}

/**
 * ¿La carta casa con la carta superior IGNORANDO comodines?
 * `topColor` es el color EFECTIVO (puede haberlo fijado un comodín).
 */
export function matchesTop(card, topCard, topColor) {
  if (card.color === topColor) return true;
  return card.value === topCard.value;
}

/**
 * ¿Tiene el jugador alguna carta del color activo? (excluyendo comodines)
 * Se usa para la restricción oficial del Wild Draw Four.
 */
export function holdsColor(hand, color) {
  for (const c of hand) {
    if (c.color === color && !isWildValue(c.value)) return true;
  }
  return false;
}

/**
 * Legalidad completa de una jugada.
 *
 * @param {object} card        carta a jugar
 * @param {object} topCard     carta superior del descarte
 * @param {number} topColor    color efectivo actual
 * @param {object[]} hand      mano completa del jugador (necesaria p/ restricción +4)
 * @param {object} rules
 * @returns {{legal: boolean, reason?: string}}
 */
export function canPlay(card, topCard, topColor, hand, rules = DEFAULT_RULES) {
  if (!card) return { legal: false, reason: 'NO_SUCH_CARD' };

  // Comodín puro: siempre legal.
  if (card.value === Value.Wild) return { legal: true };

  // Wild Draw Four: legal siempre que se respete la restricción oficial.
  if (card.value === Value.WildDrawFour) {
    if (rules.wild4Restriction && holdsColor(hand, topColor)) {
      return { legal: false, reason: 'WILD4_BLOCKED' };
    }
    return { legal: true };
  }

  if (matchesTop(card, topCard, topColor)) return { legal: true };
  return { legal: false, reason: 'NO_MATCH' };
}

/**
 * ¿Puede la carta apilarse sobre un castigo pendiente?
 * Oficialmente NO (Mattel: "Draw 2 stacking is illegal"), pero es la house rule
 * más jugada y UNO! Mobile la incluye. Va tras `rules.stacking`.
 */
export function canStackOn(card, pendingCard) {
  if (!pendingCard) return false;
  if (pendingCard.value === Value.DrawTwo) return card.value === Value.DrawTwo;
  if (pendingCard.value === Value.WildDrawFour) return card.value === Value.WildDrawFour;
  return false;
}

/** Toda carta legal de la mano — para auto-jugada en timeout y para la UI. */
export function legalPlays(hand, topCard, topColor, rules = DEFAULT_RULES) {
  return hand.filter((c) => canPlay(c, topCard, topColor, hand, rules).legal);
}

/** ¿Puede apilar algo? (para saber si ofrecer la opción en lugar del robo) */
export function stackablePlays(hand, pendingCard) {
  if (!pendingCard) return [];
  return hand.filter((c) => canStackOn(c, pendingCard));
}

/**
 * Suma de puntos de una mano según la tabla oficial.
 */
export function scoreHand(hand) {
  let total = 0;
  for (const c of hand) total += pointValue(c.value);
  return total;
}

/**
 * Avance de turno genérico para N jugadores.
 *
 * Con 2 jugadores, Skip y Reverse hacen que el MISMO jugador vuelva a jugar:
 *   - Skip: obvio.
 *   - Reverse: con 2 asientos invertir el sentido devuelve el turno al mismo.
 * Esa es la regla oficial para 2 jugadores y la que aplica UNO! Mobile en 1v1.
 *
 * @param {number} index        asiento actual
 * @param {number} count        cuántos asientos avanzar
 * @param {number} direction    +1 horario, -1 antihorario
 * @param {number} playerCount
 */
export function advanceSeat(index, count, direction, playerCount) {
  if (playerCount <= 0) return 0;
  const step = direction >= 0 ? 1 : -1;
  return (((index + step * count) % playerCount) + playerCount) % playerCount;
}

/**
 * Decide quién juega después de aplicar el efecto de una carta.
 * Devuelve también si el jugador actual conserva el turno.
 *
 * Con 2 jugadores, Skip / Reverse / +2 / +4 hacen que el MISMO jugador vuelva a
 * jugar: la víctima es el rival y pierde su turno, así que el siguiente en actuar
 * vuelve a ser el lanzador. Es la regla oficial de UNO a 2 y la que aplica UNO!
 * Mobile en los duelos 1v1.
 */
export function resolveSeatAfterPlay({ value, seat, direction, playerCount }) {
  const twoPlayer = playerCount === 2;

  if (value === Value.Reverse) {
    const newDir = -direction;
    return twoPlayer
      ? { nextSeat: seat, direction: newDir, samePlayerAgain: true }
      : { nextSeat: advanceSeat(seat, 1, newDir, playerCount), direction: newDir, samePlayerAgain: false };
  }

  // Skip, Draw Two y Wild Draw Four roban el turno a la víctima:
  // se avanza 2 asientos (lanzador -> víctima saltada -> siguiente).
  if (value === Value.Skip || value === Value.DrawTwo || value === Value.WildDrawFour) {
    return twoPlayer
      ? { nextSeat: seat, direction, samePlayerAgain: true }
      : { nextSeat: advanceSeat(seat, 2, direction, playerCount), direction, samePlayerAgain: false };
  }

  return { nextSeat: advanceSeat(seat, 1, direction, playerCount), direction, samePlayerAgain: false };
}

/**
 * Cálculo de fin de ronda: el ganador suma las cartas de TODOS los demás.
 *
 * @param {Map<number, object[]>} hands asiento -> mano
 * @param {number} winnerSeat
 * @returns {{winnerSeat:number, gained:number, breakdown: object[]}}
 */
export function settleRound(hands, winnerSeat) {
  const breakdown = [];
  let gained = 0;
  for (const [seat, hand] of hands) {
    if (seat === winnerSeat) continue;
    const pts = scoreHand(hand);
    gained += pts;
    breakdown.push({ seat, points: pts, cards: hand.map((c) => ({ color: c.color, value: c.value })) });
  }
  return { winnerSeat, gained, breakdown };
}

export { Color, Value, isWildValue, isNumberValue, isActionValue, pointValue };
