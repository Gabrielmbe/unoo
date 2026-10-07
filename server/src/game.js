/**
 * GAME — máquina de estados autoritativa de una partida.
 *
 * Principios:
 *  1. El cliente NUNCA decide. Aquí se valida todo; cualquier jugada ilegal
 *     devuelve { ok:false, error } y el estado queda intacto.
 *  2. Cada mutación produce una lista ordenada de EVENTOS con `seq` global.
 *     El cliente aplica los eventos y, si detecta un hueco en `seq`, pide resync.
 *  3. Sin I/O ni timers propios: recibe `now()` inyectado y expone `tick(now)`,
 *     así el motor de reglas es 100% testeable de forma determinista.
 *  4. El mazo mezclado nunca se filtra: `snapshot(seat)` devuelve una vista
 *     recortada por jugador (su mano real, la del rival sólo como contador).
 */

import { buildShuffledDeck, cardLabel, Color, Value, isWildValue, pointValue } from './deck.js';
import { mulberry32 } from './rng.js';
import {
  DEFAULT_RULES,
  advanceSeat,
  canPlay,
  canStackOn,
  legalPlays,
  resolveSeatAfterPlay,
  scoreHand,
  settleRound,
  stackablePlays,
} from './rules.js';

export const Phase = Object.freeze({
  Setup: 'setup',
  Playing: 'playing',
  Challenge: 'challenge',
  RoundOver: 'roundOver',
  MatchOver: 'matchOver',
});

export const ErrorCode = Object.freeze({
  NotYourTurn: 'NOT_YOUR_TURN',
  NotInHand: 'NOT_IN_HAND',
  IllegalPlay: 'ILLEGAL_PLAY',
  Wild4Blocked: 'WILD4_BLOCKED',
  ColorRequired: 'COLOR_REQUIRED',
  NoPendingDraw: 'NO_PENDING_DRAW',
  CannotPass: 'CANNOT_PASS',
  BadPhase: 'BAD_PHASE',
  NoChallenge: 'NO_CHALLENGE',
  BadSwapTarget: 'BAD_SWAP_TARGET',
  GameOver: 'GAME_OVER',
});

export class Game {
  /**
   * @param {object} opts
   * @param {{id:string,name:string,avatarId?:string}[]} opts.players 2..4 en orden de asiento
   * @param {object} [opts.rules]
   * @param {number} [opts.seed]
   * @param {() => number} [opts.now] reloj inyectable (ms epoch)
   */
  constructor({ players, rules, seed, now = () => Date.now() }) {
    if (!Array.isArray(players) || players.length < 2 || players.length > 4) {
      throw new Error('UNO requiere de 2 a 4 jugadores');
    }
    this.rules = { ...DEFAULT_RULES, ...(rules ?? {}) };
    this.seed = seed ?? 0;
    this.now = now;
    this.seq = 0;
    this.phase = Phase.Setup;
    this.round = 0;
    this.turnIndex = 0;
    this.direction = 1; // +1 horario, -1 antihorario
    this.pending = null; // { amount, card, targetSeat, chainSeats:[], sourceSeat }
    this.drawnThisTurn = 0;
    this.turnDeadline = 0;
    this.log = [];

    this.players = players.map((p, seat) => ({
      seat,
      id: p.id,
      name: p.name ?? `P${seat + 1}`,
      avatarId: p.avatarId ?? 'default',
      hand: [],
      score: 0,
      unoDeclared: false,
      connected: true,
    }));

    /** @type {Map<string, object>} */
    this.cards = new Map();
    this.drawPile = [];
    this.discardPile = [];
    this.topColor = Color.Red;
    this.preWildColor = Color.Red;
    this.unoWindow = null; // { seat, deadline }
    this.pendingChallenge = null; // { targetSeat, playerSeat, deadline }
    this.lastEvent = null;
    this._dealPending = false;
  }

  // ---------------------------------------------------------------- setup ---

  /** Reparte y arranca la ronda. Devuelve los eventos generados. */
  startRound() {
    const events = [];
    this.round += 1;
    this.phase = Phase.Playing;
    this.pending = null;
    this.pendingChallenge = null;
    this.unoWindow = null;
    this.drawnThisTurn = 0;
    this.direction = 1;

    const deck = buildShuffledDeck(this.seed + this.round * 7919);
    // RNG secundario para lo que no forma parte del orden del mazo
    // (reinserción de comodines, remezclado del descarte).
    this.rand01 = mulberry32((this.seed ^ 0x9e3779b9) + this.round * 104729);
    this.cards.clear();
    for (const c of deck) this.cards.set(c.id, c);

    this.drawPile = deck.slice();
    this.discardPile = [];
    for (const p of this.players) {
      p.hand = [];
      p.unoDeclared = false;
    }

    for (let i = 0; i < this.rules.handSize; i++) {
      for (const p of this.players) p.hand.push(this.drawPile.shift());
    }

    // Carta inicial: si sale comodín se devuelve al mazo y se saca otra.
    let starter = this.drawPile.shift();
    while (isWildValue(starter.value)) {
      // Se reinserta en una posición aleatoria del mazo restante.
      const at = Math.floor(this.rand01() * (this.drawPile.length + 1));
      this.drawPile.splice(at, 0, starter);
      starter = this.drawPile.shift();
    }
    this.discardPile.push(starter);
    this.topColor = starter.color;
    this.preWildColor = starter.color;

    // El repartidor rota cada ronda; el primer jugador es el siguiente.
    const dealerSeat = (this.round - 1) % this.players.length;
    this.currentSeat = advanceSeat(dealerSeat, 1, this.direction, this.players.length);

    events.push(
      this._ev('round_start', {
        round: this.round,
        handSize: this.rules.handSize,
        starter: this._pubCard(starter),
        firstSeat: this.currentSeat,
        direction: this.direction,
      }),
    );

    // Efecto de la carta inicial sobre el primer jugador.
    this._applyStarterEffect(starter, events);

    if (this.phase === Phase.Playing) this._openTurn(events);
    return events;
  }

  /** Si la carta inicial es de acción, se aplica al primer jugador. */
  _applyStarterEffect(card, events) {
    const first = this.currentSeat;
    if (card.value === Value.Skip) {
      events.push(this._ev('seat_skipped', { seat: first, by: 'starter' }));
      if (this.players.length > 2) {
        this.currentSeat = advanceSeat(first, 1, this.direction, this.players.length);
      } // con 2 jugadores el "saltado" juega igual (regla oficial 2P)
    } else if (card.value === Value.Reverse) {
      this.direction = -this.direction;
      events.push(this._ev('direction_changed', { direction: this.direction, by: 'starter' }));
    } else if (card.value === Value.DrawTwo) {
      events.push(
        this._ev('draw_penalty', { seat: first, amount: 2, sourceSeat: first, by: 'starter' }),
      );
      this._doDraw(first, 2, events);
      // El +2 siempre quita el turno, también a 2 jugadores (regla oficial).
      this.currentSeat = advanceSeat(first, 1, this.direction, this.players.length);
    }
  }

  // -------------------------------------------------------------- actions ---

  /**
   * Intenta jugar una carta. Es la única puerta de entrada para descartar.
   * @param {number} seat
   * @param {string} cardId
   * @param {number} [chosenColor] obligatorio si la carta es comodín
   * @param {number} [swapTargetSeat] para la house rule "7-0"
   */
  playCard(seat, cardId, chosenColor, swapTargetSeat) {
    if (this.phase !== Phase.Playing) return this._fail(ErrorCode.BadPhase);
    if (seat !== this.currentSeat) return this._fail(ErrorCode.NotYourTurn, { currentSeat: this.currentSeat });

    const player = this.players[seat];
    const idx = player.hand.findIndex((c) => c.id === cardId);
    if (idx < 0) return this._fail(ErrorCode.NotInHand);
    const card = player.hand[idx];

    const topCard = this.topCard();
    const pending = this.pending;

    // ¿Está apilando un castigo o haciendo una jugada normal?
    const isStack =
      !!pending && pending.targetSeat === seat && canStackOn(card, pending.card);

    if (!isStack) {
      // Con castigo pendiente y sin apilar, sólo puede jugar si el juego lo permite.
      if (pending && pending.targetSeat === seat && !this.rules.stacking) {
        // Debe robar: no puede escaquearse jugando otra cosa.
        return this._fail(ErrorCode.IllegalPlay, { mustDraw: true });
      }
      const verdict = canPlay(card, topCard, this.topColor, player.hand, this.rules);
      if (!verdict.legal) return this._fail(verdict.reason === 'WILD4_BLOCKED' ? ErrorCode.Wild4Blocked : ErrorCode.IllegalPlay, { reason: verdict.reason });
    }

    if (isWildValue(card.value) && (chosenColor === undefined || chosenColor === null)) {
      return this._fail(ErrorCode.ColorRequired);
    }
    if (isWildValue(card.value) && !(chosenColor >= Color.Red && chosenColor <= Color.Blue)) {
      return this._fail(ErrorCode.ColorRequired);
    }

    // "7-0": el 7 exige destino válido cuando la house rule está activa.
    if (this.rules.sevenZero && card.value === Value.Seven && !Number.isInteger(swapTargetSeat)) {
      return this._fail(ErrorCode.BadSwapTarget);
    }
    if (
      this.rules.sevenZero &&
      card.value === Value.Seven &&
      !(swapTargetSeat >= 0 && swapTargetSeat < this.players.length && swapTargetSeat !== seat)
    ) {
      return this._fail(ErrorCode.BadSwapTarget);
    }

    const events = [];

    // ---- mutación: la carta sale de la mano al descarte ----
    player.hand.splice(idx, 1);
    this.discardPile.push(card);
    this.preWildColor = this.topColor;

    const effectiveColor = isWildValue(card.value) ? chosenColor : card.color;
    this.topColor = effectiveColor;
    this.drawnThisTurn = 0;

    events.push(
      this._ev('card_played', {
        seat,
        card: this._pubCard(card),
        chosenColor: isWildValue(card.value) ? effectiveColor : null,
        stacked: isStack,
        handSizes: this._handSizes(),
      }),
    );
    if (isWildValue(card.value)) {
      events.push(this._ev('color_changed', { color: effectiveColor, bySeat: seat }));
    }
    this._note(`${player.name} juega ${cardLabel(card)}`);

    // Regla oficial: si no cantaste UNO y ya empezó otra acción, te pillan.
    this._closeUnoWindow(events, seat);

    // ---- fin de ronda: se quedó sin cartas ----
    if (player.hand.length === 0) {
      this._finishRound(seat, card, events);
      return { ok: true, events };
    }

    // ---- aviso de UNO ----
    if (player.hand.length === 1) {
      if (player.unoDeclared) {
        player.unoDeclared = false;
        events.push(this._ev('uno_called', { seat, preDeclared: true }));
      } else {
        this.unoWindow = { seat, deadline: this.now() + this.rules.unoWindowMs };
        events.push(this._ev('uno_required', { seat, deadline: this.unoWindow.deadline }));
      }
    }

    // ---- apilado ----
    if (isStack) {
      pending.amount += card.value === Value.DrawTwo ? 2 : 4;
      pending.card = card;
      pending.chainSeats.push(seat);
      const victimSeat = advanceSeat(seat, 1, this.direction, this.players.length);
      pending.targetSeat = victimSeat;
      // El castigo acumulado viaja: ahora le toca decidir al nuevo objetivo.
      this.currentSeat = victimSeat;
      events.push(
        this._ev('stacked', {
          seat,
          card: this._pubCard(card),
          amount: pending.amount,
          chainSeats: pending.chainSeats.slice(),
          targetSeat: victimSeat,
        }),
      );
      this._openTurn(events);
      return { ok: true, events };
    }

    // ---- "7-0" ----
    if (this.rules.sevenZero && card.value === Value.Seven) {
      const a = player.hand;
      const b = this.players[swapTargetSeat].hand;
      player.hand = b;
      this.players[swapTargetSeat].hand = a;
      events.push(this._ev('hands_swapped', { aSeat: seat, bSeat: swapTargetSeat, handSizes: this._handSizes() }));
    } else if (this.rules.sevenZero && card.value === Value.Zero) {
      const hands = this.players.map((p) => p.hand);
      const rotated = new Array(hands.length);
      const n = hands.length;
      for (let i = 0; i < n; i++) {
        const to = this.direction >= 0 ? (i + 1) % n : (i - 1 + n) % n;
        rotated[to] = hands[i];
      }
      this.players.forEach((p, i) => (p.hand = rotated[i]));
      events.push(this._ev('hands_rotated', { direction: this.direction, handSizes: this._handSizes() }));
    }

    // ---- efectos de carta ----
    this._applyCardEffect(card, effectiveColor, seat, events);

    if (this.phase === Phase.Playing) this._openTurn(events);
    return { ok: true, events };
  }

  _applyCardEffect(card, effectiveColor, seat, events) {
    const nextSeat = advanceSeat(seat, 1, this.direction, this.players.length);

    if (card.value === Value.DrawTwo || card.value === Value.WildDrawFour) {
      const amount = card.value === Value.DrawTwo ? 2 : 4;
      const canStackNow =
        this.rules.stacking && stackablePlays(this.players[nextSeat].hand, card).length > 0;

      if (card.value === Value.WildDrawFour && this.rules.wild4Challenge) {
        // Se congela el castigo: el penalizado puede retar.
        this.pending = { amount, card, targetSeat: nextSeat, chainSeats: [seat], sourceSeat: seat };
        this.pendingChallenge = {
          playerSeat: seat,
          targetSeat: nextSeat,
          deadline: this.now() + this.rules.challengeWindowMs,
        };
        this.phase = Phase.Challenge;
        events.push(this._ev('challenge_offer', { ...this.pendingChallenge }));
        return;
      }

      if (canStackNow) {
        this.pending = { amount, card, targetSeat: nextSeat, chainSeats: [seat], sourceSeat: seat };
        events.push(
          this._ev('stack_opened', { seat, amount, card: this._pubCard(card), targetSeat: nextSeat }),
        );
        this.currentSeat = nextSeat;
        return;
      }

      events.push(this._ev('draw_penalty', { seat: nextSeat, amount, sourceSeat: seat }));
      this._doDraw(nextSeat, amount, events);

      // Resuelve turno saltando a la víctima.
      const r = resolveSeatAfterPlay({
        value: card.value,
        seat,
        direction: this.direction,
        playerCount: this.players.length,
      });
      this.direction = r.direction;
      this.currentSeat = r.nextSeat;
      return;
    }

    const r = resolveSeatAfterPlay({
      value: card.value,
      seat,
      direction: this.direction,
      playerCount: this.players.length,
    });

    if (card.value === Value.Skip) {
      events.push(this._ev('seat_skipped', { seat: advanceSeat(seat, 1, this.direction, this.players.length), bySeat: seat }));
    }
    if (card.value === Value.Reverse) {
      events.push(this._ev('direction_changed', { direction: r.direction, bySeat: seat }));
    }

    this.direction = r.direction;
    this.currentSeat = r.nextSeat;
  }

  /**
   * Robar cartas.
   *  - Con castigo pendiente: robo forzoso del total acumulado y pierde el turno.
   *  - Sin castigo: puede robar mientras no tenga carta jugable (regla oficial
   *    "robar hasta poder jugar"). Si ya robó y ahora puede jugar, debe jugar o pasar.
   */
  draw(seat) {
    if (this.phase !== Phase.Playing) return this._fail(ErrorCode.BadPhase);
    if (seat !== this.currentSeat) return this._fail(ErrorCode.NotYourTurn, { currentSeat: this.currentSeat });

    const events = [];
    const pending = this.pending;

    if (pending && pending.targetSeat === seat) {
      const amount = pending.amount;
      events.push(this._ev('draw_penalty', { seat, amount, sourceSeat: pending.sourceSeat, accepted: true }));
      this._doDraw(seat, amount, events);
      this.pending = null;

      this.currentSeat = advanceSeat(seat, 1, this.direction, this.players.length);
      this.drawnThisTurn = 0;
      if (this.phase === Phase.Playing) this._openTurn(events);
      return { ok: true, events };
    }

    const player = this.players[seat];
    const playableNow = legalPlays(player.hand, this.topCard(), this.topColor, this.rules).length;
    if (this.drawnThisTurn > 0 && playableNow > 0) {
      return this._fail(ErrorCode.NoPendingDraw, { playable: playableNow });
    }

    this._doDraw(seat, 1, events);
    this.drawnThisTurn += 1;

    // Sin "robar hasta poder jugar": robar 1 carta consume el turno.
    if (!this.rules.drawUntilPlayable) {
      this.currentSeat = advanceSeat(seat, 1, this.direction, this.players.length);
      this.drawnThisTurn = 0;
      if (this.phase === Phase.Playing) this._openTurn(events);
      return { ok: true, events };
    }

    // Con "robar hasta poder jugar": si tras robar ya puede jugar, el turno sigue
    // siendo suyo (decide entre jugar o pasar). Si el mazo se agota sin poder
    // jugar, el turno pasa automáticamente para no bloquear la partida.
    const canNow = legalPlays(player.hand, this.topCard(), this.topColor, this.rules).length > 0;
    if (!canNow && this.drawPile.length === 0) {
      this.currentSeat = advanceSeat(seat, 1, this.direction, this.players.length);
      this.drawnThisTurn = 0;
      if (this.phase === Phase.Playing) this._openTurn(events);
    }
    return { ok: true, events };
  }

  /** Pasar turno tras robar (sólo si ya no puede jugar o si la casa lo permite). */
  pass(seat) {
    if (this.phase !== Phase.Playing) return this._fail(ErrorCode.BadPhase);
    if (seat !== this.currentSeat) return this._fail(ErrorCode.NotYourTurn, { currentSeat: this.currentSeat });
    if (this.pending && this.pending.targetSeat === seat) return this._fail(ErrorCode.CannotPass);
    if (this.drawnThisTurn <= 0) return this._fail(ErrorCode.CannotPass);

    const player = this.players[seat];
    const playable = legalPlays(player.hand, this.topCard(), this.topColor, this.rules);
    if (playable.length > 0 && !this.rules.allowPassWithPlay) {
      return this._fail(ErrorCode.CannotPass, { playable: playable.length });
    }

    const events = [];
    this.currentSeat = advanceSeat(seat, 1, this.direction, this.players.length);
    this.drawnThisTurn = 0;
    events.push(this._ev('pass', { seat }));
    this._openTurn(events);
    return { ok: true, events };
  }

  /** Gritar "¡UNO!". Gana el primer mensaje que llega al servidor. */
  callUno(seat) {
    if (this.phase !== Phase.Playing && this.phase !== Phase.Challenge) {
      return this._fail(ErrorCode.BadPhase);
    }
    const player = this.players[seat];
    const events = [];

    if (this.unoWindow && this.unoWindow.seat === seat && this.now() <= this.unoWindow.deadline) {
      this.unoWindow = null;
      events.push(this._ev('uno_called', { seat, preDeclared: false, justInTime: true }));
      this._note(`${player.name} canta ¡UNO!`);
      return { ok: true, events };
    }

    // Declaración anticipada: tienes 2 cartas y vas a bajar la penúltima.
    if (player.hand.length === 2 && seat === this.currentSeat) {
      player.unoDeclared = true;
      events.push(this._ev('uno_called', { seat, preDeclared: true }));
      return { ok: true, events };
    }

    return this._fail(ErrorCode.NoChallenge, { handSize: player.hand.length });
  }

  /** El rival "pilla" al que olvidó cantar UNO dentro de la ventana. */
  catchUno(seat) {
    if (!this.unoWindow) return this._fail(ErrorCode.NoChallenge);
    if (this.now() > this.unoWindow.deadline) return this._fail(ErrorCode.NoChallenge);
    if (this.unoWindow.seat === seat) return this._fail(ErrorCode.NoChallenge);

    const victim = this.unoWindow.seat;
    this.unoWindow = null;
    const events = [];
    events.push(this._ev('uno_caught', { seat: victim, bySeat: seat }));
    this._doDraw(victim, 2, events);
    return { ok: true, events };
  }

  /** Resolver el reto del Wild Draw Four. */
  resolveChallenge(seat, accept) {
    if (this.phase !== Phase.Challenge || !this.pendingChallenge) return this._fail(ErrorCode.NoChallenge);
    if (seat !== this.pendingChallenge.targetSeat) return this._fail(ErrorCode.NotYourTurn);

    const events = [];
    const { playerSeat, targetSeat } = this.pendingChallenge;
    const culprit = this.players[playerSeat];
    const victim = this.players[targetSeat];
    const amount = this.pending ? this.pending.amount : 4;

    // ¿Tenía el lanzador alguna carta del color que estaba activo ANTES del +4?
    const heldColor = culprit.hand.some(
      (c) => c.color === this.preWildColor && !isWildValue(c.value),
    );
    const bluff = heldColor;

    this.phase = Phase.Playing;
    this.pendingChallenge = null;

    events.push(
      this._ev('challenge_result', {
        accepted: !!accept,
        challenged: !!accept,
        bluffed: bluff,
        playerSeat,
        targetSeat,
        revealed: culprit.hand.map((c) => this._pubCard(c)),
      }),
    );

    if (accept && bluff) {
      // Pillado mintiendo: roba él las 4, el retador no roba.
      events.push(this._ev('draw_penalty', { seat: playerSeat, amount, sourceSeat: targetSeat, fromChallenge: true }));
      this._doDraw(playerSeat, amount, events);
      this.pending = null;
      this.currentSeat = targetSeat;
    } else if (accept) {
      // Reto fallido: el retador roba +2 de castigo adicional.
      events.push(this._ev('draw_penalty', { seat: targetSeat, amount: amount + 2, sourceSeat: playerSeat, fromChallenge: true }));
      this._doDraw(targetSeat, amount + 2, events);
      this.pending = null;
      this.currentSeat = advanceSeat(targetSeat, 1, this.direction, this.players.length);
    } else {
      this._doDraw(targetSeat, amount, events);
      this.pending = null;
      this.currentSeat = advanceSeat(targetSeat, 1, this.direction, this.players.length);
    }

    this._openTurn(events);
    return { ok: true, events };
  }

  // ------------------------------------------------------------------ tick ---

  /**
   * Reloj del servidor: ventanas de UNO, retos y timeout de turno.
   * El cliente NO puede acelerar nada: todo esto ocurre en el servidor.
   */
  tick(now = this.now()) {
    const events = [];
    if (this.phase === Phase.MatchOver || this.phase === Phase.RoundOver) return events;

    if (this.unoWindow && now > this.unoWindow.deadline) {
      const victim = this.unoWindow.seat;
      this.unoWindow = null;
      events.push(this._ev('uno_caught', { seat: victim, bySeat: null, timeout: true }));
      this._doDraw(victim, 2, events);
    }

    if (this.pendingChallenge && now > this.pendingChallenge.deadline) {
      const res = this.resolveChallenge(this.pendingChallenge.targetSeat, false);
      if (res.events) events.push(...res.events);
    }

    if (this.phase === Phase.Playing && this.turnDeadline && now > this.turnDeadline) {
      events.push(...this._autoPlay(this.currentSeat, now));
    }

    return events;
  }

  /** Jugada automática por inactividad (comportamiento de UNO! Mobile). */
  _autoPlay(seat, now) {
    const events = [this._ev('turn_timeout', { seat })];
    const player = this.players[seat];
    const topCard = this.topCard();

    // Si tiene que apilar un castigo, apila la de menor valor o roba.
    if (this.pending && this.pending.targetSeat === seat) {
      const stacks = stackablePlays(player.hand, this.pending.card);
      if (stacks.length > 0) {
        stacks.sort((a, b) => pointValue(a.value) - pointValue(b.value));
        const r = this.playCard(seat, stacks[0].id);
        if (r.events) events.push(...r.events);
        return events;
      }
      const r = this.draw(seat);
      if (r.events) events.push(...r.events);
      return events;
    }

    const plays = legalPlays(player.hand, topCard, this.topColor, this.rules);
    if (plays.length > 0) {
      // Heurística: la de menor valor; evita regalar comodines.
      const nonWild = plays.filter((c) => !isWildValue(c.value));
      const pool = nonWild.length > 0 ? nonWild : plays;
      pool.sort((a, b) => pointValue(a.value) - pointValue(b.value));
      const pick = pool[0];
      const chosen = isWildValue(pick.value) ? this._bestColorFor(player) : undefined;
      const r = this.playCard(seat, pick.id, chosen);
      if (r.events) events.push(...r.events);
      return events;
    }

    // Nada que jugar: robar hasta poder jugar o hasta agotar el mazo.
    let guard = 0;
    let res = this.draw(seat);
    while (res.events && res.events.length) events.push(...res.events);
    while (
      this.phase === Phase.Playing &&
      this.currentSeat === seat &&
      this.drawnThisTurn > 0 &&
      legalPlays(this.players[seat].hand, this.topCard(), this.topColor, this.rules).length === 0 &&
      this.drawPile.length > 0 &&
      guard++ < 40
    ) {
      res = this.draw(seat);
      if (res.events && res.events.length) events.push(...res.events);
    }
    if (this.phase === Phase.Playing && this.currentSeat === seat) {
      const r2 = this.pass(seat);
      if (r2.events && r2.events.length) events.push(...r2.events);
    }
    return events;
  }

  _bestColorFor(player) {
    const tally = [0, 0, 0, 0];
    for (const c of player.hand) if (!isWildValue(c.value)) tally[c.color]++;
    let best = 0;
    for (let i = 1; i < 4; i++) if (tally[i] > tally[best]) best = i;
    return best;
  }

  // --------------------------------------------------------------- helpers ---

  _openTurn(events) {
    if (this.phase !== Phase.Playing) return;
    this.turnIndex = (this.turnIndex ?? 0) + 1;
    this.turnDeadline = this.now() + this.rules.turnSeconds * 1000;
    this.drawnThisTurn = this.pending && this.pending.targetSeat === this.currentSeat ? this.drawnThisTurn : 0;
    const p = this.players[this.currentSeat];
    const playable = legalPlays(p.hand, this.topCard(), this.topColor, this.rules).length;
    const canStack = this.pending && this.pending.targetSeat === this.currentSeat
      ? stackablePlays(p.hand, this.pending.card).length
      : 0;
    events.push(
      this._ev('turn_start', {
        seat: this.currentSeat,
        turnIndex: this.turnIndex,
        deadline: this.turnDeadline,
        direction: this.direction,
        playableCount: playable,
        stackableCount: canStack,
        pendingAmount: this.pending ? this.pending.amount : 0,
      }),
    );
  }

  /**
   * Cierra la ventana de "¡UNO!". Si el dueño de la ventana no la cantó antes de
   * que ocurriera otra acción en la mesa, roba 2 de penalización.
   * `actingSeat` evita castigarse a sí mismo cuando encadena Skip/Reverse a 2P.
   */
  _closeUnoWindow(events, actingSeat = -1) {
    if (!this.unoWindow) return;
    const victim = this.unoWindow.seat;
    if (victim === actingSeat) return;
    this.unoWindow = null;
    events.push(this._ev('uno_caught', { seat: victim, bySeat: actingSeat, late: true }));
    this._doDraw(victim, 2, events);
  }

  _doDraw(seat, amount, events) {
    const player = this.players[seat];
    const drawn = [];
    for (let i = 0; i < amount; i++) {
      if (this.drawPile.length === 0) this._reshuffle(events);
      if (this.drawPile.length === 0) break;
      drawn.push(this.drawPile.shift());
    }
    player.hand.push(...drawn);
    player.unoDeclared = false;
    events.push(
      this._ev('cards_drawn', {
        seat,
        count: drawn.length,
        cards: drawn.map((c) => this._pubCard(c)),
        handSize: player.hand.length,
        handSizes: this._handSizes(),
      }),
    );
    return drawn;
  }

  /** El descarte (menos la superior) vuelve al mazo y se remezcla. */
  _reshuffle(events) {
    if (this.discardPile.length <= 1) {
      // Sin cartas que reciclar: la partida no puede continuar.
      this.phase = Phase.RoundOver;
      events.push(this._ev('deck_exhausted', { handSizes: this._handSizes() }));
      return;
    }
    const top = this.discardPile.pop();
    const recycled = this.discardPile.splice(0, this.discardPile.length);
    this.discardPile.push(top);

    const rand = this.rand01;
    for (let i = recycled.length - 1; i > 0; i--) {
      const j = Math.floor(rand() * (i + 1));
      const t = recycled[i];
      recycled[i] = recycled[j];
      recycled[j] = t;
    }
    this.drawPile.push(...recycled);
    // Los comodines reciclados pierden el color declarado.
    events.push(this._ev('deck_reshuffled', { count: recycled.length }));
  }

  _finishRound(winnerSeat, lastCard, events) {
    const nextSeat = advanceSeat(winnerSeat, 1, this.direction, this.players.length);

    // Regla oficial: si la última carta es +2/+4, la víctima roba ANTES de puntuar.
    if (lastCard.value === Value.DrawTwo || lastCard.value === Value.WildDrawFour) {
      const amount = lastCard.value === Value.DrawTwo ? 2 : 4;
      events.push(this._ev('draw_penalty', { seat: nextSeat, amount, sourceSeat: winnerSeat, final: true }));
      this._doDraw(nextSeat, amount, events);
    }

    const hands = new Map(this.players.map((p) => [p.seat, p.hand]));
    const settlement = settleRound(hands, winnerSeat);
    this.players[winnerSeat].score += settlement.gained;

    this.phase = Phase.RoundOver;
    this.unoWindow = null;
    this.pendingChallenge = null;
    this.turnDeadline = 0;

    events.push(
      this._ev('round_end', {
        winnerSeat,
        winnerId: this.players[winnerSeat].id,
        gained: settlement.gained,
        breakdown: settlement.breakdown,
        scores: this.players.map((p) => ({ seat: p.seat, id: p.id, score: p.score })),
        targetScore: this.rules.targetScore,
      }),
    );

    const matchWinner = this.players.find((p) => p.score >= this.rules.targetScore);
    if (matchWinner) {
      this.phase = Phase.MatchOver;
      events.push(
        this._ev('match_end', {
          winnerSeat: matchWinner.seat,
          winnerId: matchWinner.id,
          rounds: this.round,
          scores: this.players.map((p) => ({ seat: p.seat, id: p.id, score: p.score })),
        }),
      );
    }
  }

  // ------------------------------------------------------------- snapshots ---

  topCard() {
    return this.discardPile[this.discardPile.length - 1] ?? null;
  }

  _handSizes() {
    return this.players.map((p) => p.hand.length);
  }

  /** Cartas "públicas": sin el id interno, para que el rival no pueda referenciarlas. */
  _pubCard(c) {
    return { color: c.color, value: c.value };
  }

  _ev(type, data) {
    const ev = { seq: ++this.seq, type, at: this.now(), ...data };
    this.lastEvent = ev;
    return ev;
  }

  _fail(code, extra) {
    return { ok: false, error: code, ...(extra ?? {}), events: [] };
  }

  _note(text) {
    this.log.push(text);
    if (this.log.length > 200) this.log.shift();
  }

  /**
   * Vista recortada por jugador. Aquí es donde se corta el cheat por inspección:
   * el rival jamás recibe identificadores ni valores de cartas ajenas.
   */
  snapshot(forSeat) {
    const me = this.players[forSeat];
    return {
      phase: this.phase,
      round: this.round,
      turnIndex: this.turnIndex,
      seat: forSeat,
      direction: this.direction,
      currentSeat: this.phase === Phase.Playing || this.phase === Phase.Challenge ? this.currentSeat : null,
      turnDeadline: this.turnDeadline,
      topCard: this.topCard() ? this._pubCard(this.topCard()) : null,
      topColor: this.topColor,
      pending: this.pending
        ? { amount: this.pending.amount, targetSeat: this.pending.targetSeat, card: this._pubCard(this.pending.card) }
        : null,
      challenge: this.pendingChallenge ? { ...this.pendingChallenge } : null,
      unoWindow: this.unoWindow ? { ...this.unoWindow } : null,
      deckCount: this.drawPile.length,
      players: this.players.map((p) => ({
        seat: p.seat,
        id: p.id,
        name: p.name,
        avatarId: p.avatarId,
        score: p.score,
        connected: p.connected,
        handSize: p.hand.length,
        hand: p.seat === forSeat ? p.hand.map((c) => ({ id: c.id, color: c.color, value: c.value })) : undefined,
      })),
      rules: this.rules,
      lastSeq: this.seq,
    };
  }
}
