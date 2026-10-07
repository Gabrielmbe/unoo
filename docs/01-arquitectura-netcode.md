# Módulo 1 · Arquitectura Netcode (multijugador a distancia)

> Código de referencia: [`server/src/`](../server/src/) · [`unity-client/Assets/Scripts/Net/`](../unity-client/Assets/Scripts/Net/)

---

## 1.1 Decisión de stack: por qué WebSocket propio y no Mirror / Photon / NGO

Antes de escribir una línea, la pregunta es qué necesita *este* juego. UNO mueve
**~2 mensajes por turno**. No hay interpolación de posiciones, no hay hit
registration, no hay rollback. Lo que sí necesita es:

| Requisito | Peso en la decisión |
|---|---|
| Servidor autoritativo real (anti-hackeo) | Crítico |
| Reconexión limpia (el móvil pierde cobertura siempre) | Crítico |
| Backend que quepa en un servicio barato de Render (512 MB) | Crítico |
| Sincronización de posiciones a 30 Hz | **Irrelevante** |
| Predicción + rollback de física | **Irrelevante** |

Mirror y Netcode for GameObjects están diseñados para sincronizar
`NetworkTransform` de cientos de GameObjects. Usarlos aquí significa:

- mantener un **runtime de Unity en el servidor** (segundo build, segundo ciclo de compilación),
- acoplar las reglas al ciclo de vida de los `GameObject`,
- y seguir escribiendo a mano lobby, códigos de invitación, reconnect y timers.

Es exactamente el patrón que la gente que ha hecho juegos de cartas con Mirror
acaba abandonando: para un juego por turnos, un servidor de estado + WebSocket
es más simple, más auditable y más barato. Photon añadiría dependencia de CCU y
un coste recurrente por algo que no usamos.

**Decisión:** servidor Node.js autoritativo sobre WebSocket (`ws`), JSON como
formato, cliente Unity con `ClientWebSocket`. El protocolo está aislado en
[`protocol.js`](../server/src/protocol.js) y
[`Messages.cs`](../unity-client/Assets/Scripts/Net/Messages.cs), así que cambiar
el transporte (a Mirror, a binario, a WebTransport) no toca las reglas.

**Ancho de banda real:** una jugada típica pesa <200 bytes. Una partida completa
de 15 rondas son ~40 KB. Cero problema.

---

## 1.2 Salas por código de invitación privado

Implementado en [`lobby.js`](../server/src/lobby.js).

```
Código: 6 caracteres · alfabeto ABCDEFGHJKMNPQRSTVWXYZ23456789 (32 símbolos)
        → sin 0/O, sin 1/I/L, sin U
Espacio: 32^6 = 1.073.741.824 combinaciones
```

Se quitan los caracteres ambiguos a propósito: el código se dicta por teléfono
(«entra en **K7M3PQ**»), y un `0` frente a una `O` cuesta una partida.

### Ciclo de vida de una sala

```
create_room ──► Room(code, hostId, rules)
                    │
join_room ─────────►│  (rechaza: ROOM_FULL, ROOM_BUSY, NO_ROOM)
                    │
set_ready ─────────►│  broadcast lobby_state a ambos
                    │
start_game ────────►│  sólo el host; exige 2 jugadores y todos "ready"
                    │
                Game(seed) ──► fase 'playing'
                    │
   desconexión ─────┤  partida en curso → GRACE de 120 s
                    │  lobby             → el jugador sale ya
                    │
   reconnect ──────►│  recupera asiento + mano exacta
                    │
   vacío / 30 min ──► sala liberada
```

### Migración de host

Si el creador se va, **el que queda hereda la sala** en lugar de dejarla
huérfana. Está probado en
[`net.integration.test.js`](../server/test/net.integration.test.js) (*«el
anfitrión migra si se va y la sala se limpia al quedarse vacía»*).

### Por qué no hay base de datos

Todo vive en memoria. Una sala son ~2 KB; 5.000 salas son ~10 MB dentro de los
512 MB de Render. Para un juego **por invitación entre dos personas**, perder las
salas al reiniciar es aceptable y evita depender de Redis o Postgres. Cuándo
cambiar esto y cómo: ver [Módulo 5](05-despliegue-render.md).

---

## 1.3 Sincronización de turnos y validación anti-hackeo

### El principio: el cliente sólo manda *intenciones*

El cliente **nunca** envía cartas, mazos, su asiento ni el estado. Envía:

```jsonc
{ "t": "play_card", "cardId": "c073", "chosenColor": 2 }
```

`cardId` es un identificador opaco que **el servidor asignó**. El servidor:

1. Deriva el asiento del `playerId` de la conexión — no del mensaje.
2. Comprueba que es su turno.
3. Comprueba que `cardId` está en su mano.
4. Comprueba la legalidad contra la carta superior y el color efectivo.
5. Sólo entonces muta el estado.

Cualquier fallo devuelve `{t:"error", code:"..."}` y **el estado no se toca**.
Ni siquiera se emite un evento (el contador `seq` no avanza).

### Qué ataques quedan cerrados

| Ataque | Defensa | Test que lo cubre |
|---|---|---|
| Jugar una carta de la mano del rival | El `cardId` se busca sólo en *tu* mano | `NOT_IN_HAND` (integración) |
| Jugar una carta inventada | Id desconocido → rechazo | `carta fantasma` |
| Jugar fuera de turno | Asiento derivado de la conexión | `NOT_YOUR_TURN` |
| Jugar una carta que no casa | `canPlay()` autoritativo | `IllegalPlay` |
| Usar +4 teniendo el color | Restricción oficial del W+4 | `Wild4Blocked` |
| **Espiar la mano del rival** | **La mano ajena nunca sale del servidor** | `snapshot nunca revela la mano del rival` |
| **Saber la carta que vas a robar** | **El mazo mezclado no viaja** | `snapshot()` sólo envía `deckCount` |
| Acelerar el reloj para ganar timeouts | Los timers corren en el servidor | `turn_timeout` |
| Spam / DoS ligero | 20 msg/s + payload de 8 KB máx. | `RATE_LIMITED` |
| Romper la sala con JSON malformado | Validación + try/catch por mensaje | `BAD_JSON`, `BAD_PAYLOAD` |

El filtrado por vista está en un único sitio,
[`GameServer._viewOf`](../server/src/session.js):

```js
_viewOf(event, seat) {
  if (event.type === 'cards_drawn' && event.seat !== seat) {
    const { cards, ...rest } = event;   // las cartas robadas son privadas
    return rest;
  }
  return event;
}
```

Excepción deliberada: `challenge_result.revealed` **sí** es público, porque
enseñar la mano del que retó es la mecánica oficial del reto al Wild Draw Four.

### Semilla privada

El servidor genera una semilla con `crypto` y mezcla con ella. **No se envía a
los clientes** durante la partida: conocerla sería conocer el mazo entero. Sólo
tiene sentido revelarla al terminar, para replays y para que cualquiera pueda
verificar que el reparto fue honesto.

---

## 1.4 Modelo de sincronización: eventos + snapshot, no estado completo

Cada mutación produce una lista ordenada de **eventos** con un `seq` global
monótono. El servidor los agrupa en un solo frame de red:

```jsonc
{ "t": "batch", "e": [
  { "seq": 41, "type": "card_played",   "seat": 1, "card": {"color":0,"value":12} },
  { "seq": 42, "type": "draw_penalty",  "seat": 0, "amount": 2 },
  { "seq": 43, "type": "cards_drawn",   "seat": 0, "count": 2, "cards": [...] },
  { "seq": 44, "type": "turn_start",    "seat": 1, "deadline": 1712345678901 }
]}
```

El cliente detecta huecos y pide el snapshot completo:

```csharp
if (_lastSeq > 0 && e.seq > _lastSeq + 1) {
    SequenceGapDetected?.Invoke();
    RequestResync();          // → el servidor manda snapshot(seat)
}
```

Es el mismo patrón que event sourcing + snapshot, y es lo que permite que una
reconexión cueste **un mensaje** en vez de reconstruir la partida evento a evento.

### Reloj compartido

Los countdowns de turno deben agotarse en el mismo instante en el PC y en el
móvil aunque sus relojes difieran en segundos. Se resuelve con un ping cada 5 s
y un ajuste tipo NTP simplificado:

```csharp
_rttMs = (int)(local - msg.c);
_serverOffsetMs = msg.serverTime + _rttMs / 2 - local;
```

La UI cronometra con `Net.ServerNowMs()`, nunca con `Time.time`.

---

## 1.5 Reconexión: el caso que más importa en móvil

```
1. welcome  → el servidor da un reconnectToken (16 bytes, base64url)
2. el cliente lo guarda en PlayerPrefs
3. se cae el wifi / el móvil va a segundo plano
4. el servidor marca connected=false y avisa al rival (peer_state)
5. GRACE de 120 s
6. reconnect {reconnectToken} → recupera asiento Y mano exacta
7. el servidor manda lobby_state + snapshot
```

El rival ve un indicador de «reconectando» durante esos 120 s, no una derrota
automática. Cubierto por el test *«reconexión con token recupera asiento y
mano»*, que verifica que la mano recuperada es **idéntica** a la original.

El backoff del cliente es exponencial con tope de 15 s — pensado para no
machacar un servicio de Render que se está despertando del spin-down (~1 min).

---

## 1.6 Backend ligero: el presupuesto real

| Concepto | Coste |
|---|---|
| Dependencias de producción | **1** (`ws`) |
| RAM en reposo | ~40 MB |
| RAM por sala activa | ~2 KB |
| CPU por sala | un `tick()` cada 100 ms, sólo salas en partida |
| Tiempo de arranque | < 200 ms |

Decisiones concretas que lo mantienen así:

- **Sin framework.** `node:http` + `ws`. Express no aporta nada a un endpoint
  de salud y a un upgrade.
- **Tick centralizado.** Un `setInterval` recorre las salas en partida; no hay un
  timer por sala ni por jugador.
- **Timers con `unref()`** para que el proceso pueda morir limpio.
- **`maxPayload: 8 KB`** en el handshake: corta un ataque de paquete gigante
  antes de parsear nada.
- **Límite de 20 msg/s por conexión** con ventana deslizante.
- **Sin `async` innecesario en el hot path**: las reglas son síncronas.

Con 512 MB y ~40 MB de base, el techo no es la RAM: es el número de sockets
abiertos. Un proceso Node aguanta miles sin problema para este tráfico.

---

## 1.7 Diagrama del flujo de una jugada

```
 UNITY (móvil)                    RENDER (Node)                  UNITY (PC)
      │                                 │                             │
      │ suelta la carta                 │                             │
      ├─► predicción local:             │                             │
      │   la carta sale YA              │                             │
      │                                 │                             │
      ├──{play_card c073}──────────────►│                             │
      │                                 ├─ ¿su turno?                 │
      │                                 ├─ ¿en su mano?               │
      │                                 ├─ ¿legal?  ← RulesEngine     │
      │                                 ├─ muta estado                │
      │                                 ├─ emite eventos seq 41..44   │
      │◄──────────{batch [...]}─────────┼──────{batch [...]}─────────►│
      │                                 │        (sin `cards`)        │
      ├─ confirma la animación          │                 anima la jugada
      │                                 │                             │
      │  ...o bien...                   │                             │
      │◄──────────{error ILLEGAL}───────┤                             │
      ├─ snap-back + shake              │                             │
```

La clave de que no se note el lag: **la animación empieza antes de la red**, y la
red sólo la confirma o la revierte.
