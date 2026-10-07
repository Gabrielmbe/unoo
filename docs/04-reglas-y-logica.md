# Módulo 4 · Lógica del juego y reglas especiales

> Servidor (autoridad): [`server/src/deck.js`](../server/src/deck.js) · [`rules.js`](../server/src/rules.js) · [`game.js`](../server/src/game.js)
> Cliente (espejo): [`DeckManager.cs`](../unity-client/Assets/Scripts/Core/DeckManager.cs) · [`RulesEngine.cs`](../unity-client/Assets/Scripts/Core/RulesEngine.cs) · [`TurnManager.cs`](../unity-client/Assets/Scripts/Core/TurnManager.cs)

Reglas verificadas contra el reglamento oficial de Mattel.

---

## 4.1 DeckManager — las 108 cartas

```
4 colores × 25 cartas = 100
    1 × "0"
    2 × cada número del 1 al 9              = 18
    2 × Skip, 2 × Reverse, 2 × Draw Two     =  6
+ 4 Wild
+ 4 Wild Draw Four
= 108
```

> Las ediciones modernas traen **112** cartas (4 comodines especiales extra). Se
> juega con el mazo clásico de 108, que es el que usa el modo Clásico de UNO!.

### Orden canónico

El mazo se construye siempre en el mismo orden antes de barajar. No es
estética: es lo que hace que una semilla produzca **el mismo reparto en cualquier
máquina y en cualquier lenguaje**.

### Barajado determinista

Fisher–Yates + mulberry32, idéntico en C# y JavaScript:

```csharp
for (int i = list.Count - 1; i > 0; i--) {
    int j = NextInt(i + 1);
    (list[i], list[j]) = (list[j], list[i]);
}
```

Dos detalles que rompen la paridad si se tocan:

1. **La aritmética del RNG es de 32 bits sin signo con `unchecked`**, equivalente
   a `| 0` y `Math.imul` de JavaScript.
2. **La división final es en `double`, no en `float`.** Un `float` sólo tiene 24
   bits de mantisa y daría un `NextInt()` distinto en algunos valores.

Además, **se roba del frente de la lista** (`RemoveAt(0)`) para replicar el
`shift()` del servidor. Con 108 cartas el coste es despreciable y a cambio los
replays son bit a bit iguales en ambos lenguajes.

### Reciclado del descarte

Cuando el mazo se agota, el descarte (menos la superior) vuelve al mazo y se
remezcla. Si sólo queda la superior, la ronda termina (`deck_exhausted`).

### Validación de integridad

`DeckManager.Validate()` comprueba los 108 ids únicos y el conteo exacto de cada
combinación color×valor. Corre en los tests: si alguien toca la tabla de cartas,
revienta en CI en vez de producir una partida rara en producción.

---

## 4.2 TurnManager — flujo horario, antihorario y saltos

### Avance de asiento

```csharp
public static int AdvanceSeat(int index, int count, int direction, int playerCount) {
    int step = direction >= 0 ? 1 : -1;
    int raw = index + step * count;
    return ((raw % playerCount) + playerCount) % playerCount;   // doble módulo: el % de C# da negativos
}
```

### Resolución tras cada carta

```csharp
if (value == CardValue.Reverse) {
    int newDir = -direction;
    return twoPlayer
        ? new SeatResult(seat, newDir, true)                                  // 2P: juega otra vez el mismo
        : new SeatResult(AdvanceSeat(seat, 1, newDir, playerCount), newDir, false);
}
if (value == CardValue.Skip || value == CardValue.DrawTwo || value == CardValue.WildDrawFour) {
    return twoPlayer
        ? new SeatResult(seat, direction, true)
        : new SeatResult(AdvanceSeat(seat, 2, direction, playerCount), direction, false);  // +2 = lanzador → víctima saltada → siguiente
}
```

**La regla de 2 jugadores es el caso que más se rompe.** A 2, Skip / Reverse / +2
/ +4 hacen que **el mismo jugador vuelva a jugar**: la víctima es el rival y
pierde su turno. Es la regla oficial de UNO a 2 y la que aplica UNO! Mobile en los
duelos 1v1 — que es exactamente el modo de este proyecto.

| Carta | 4 jugadores | 2 jugadores |
|---|---|---|
| Número | siguiente | el otro |
| **Reverse** | invierte el sentido, juega el anterior | **el mismo** (y cambia el sentido interno) |
| **Skip** | salta al siguiente del siguiente | **el mismo** |
| **+2 / +4** | la víctima roba y pierde turno; juega el siguiente | **el mismo** |

### Carta inicial

Si la primera carta del descarte es un comodín, se reinserta en el mazo y se saca
otra. Si es de acción, se aplica al primer jugador — y si es un **+2, el primer
jugador roba 2 y se queda con 9 cartas**. (Este caso rompió dos tests que asumían
mano de 7.)

---

## 4.3 Acumulación de castigos (+2 y +4 consecutivos)

El estado viaja en un objeto:

```csharp
public sealed class PendingPenalty {
    public int Amount;             // total acumulado
    public Card SourceCard;        // qué tipo de castigo es
    public int SourceSeat;
    public int TargetSeat;
    public readonly List<int> ChainSeats;   // quién apiló qué
}
```

### Dos comportamientos, una house rule

`stacking` está **desactivado por defecto**, porque las reglas oficiales de Mattel
prohiben apilar (*«Draw 2 stacking is illegal»*). Pero es la house rule más
jugada y UNO! Mobile la incluye, así que va detrás de un flag.

**Sin `stacking` (oficial):**

```
A juega +2  →  B roba 2 y pierde el turno  →  juega A
```

**Con `stacking`:**

```
A juega +2      → pending = 2, le toca B decidir
B apila +2      → pending = 4, le toca A decidir
A apila +2      → pending = 6, le toca B
B no tiene +2   → B roba 6 y pierde el turno
```

Reglas del apilado tal y como están implementadas:

- **sólo apila el mismo tipo**: +2 sobre +2, +4 sobre +4. Un +4 **no** apila
  sobre un +2 (hay un test que lo verifica explícitamente).
- al apilar, el castigo **viaja**: `TargetSeat` pasa al siguiente y el turno también.
- con castigo pendiente y `stacking` desactivado, intentar jugar otra cosa
  devuelve `MustDraw`: no se puede escaquear.

### El reto al Wild Draw Four

Regla oficial, detrás de `wild4Challenge`:

```
A juega +4 declarando Azul
  → se congela el castigo, fase 'challenge', ventana de 8 s
  → B puede retar
     A enseña la mano:
       ¿tenía carta del color que estaba ACTIVO ANTES del +4?
         SÍ (mintió)  → A roba 4, B no roba, juega B
         NO (legítimo)→ B roba 4 + 2 de castigo = 6, juega el siguiente
  → si expira la ventana, equivale a no retar
```

El detalle que más se implementa mal: se comprueba la mano contra
`PreWildColor` — el color que estaba activo **antes** de que el comodín lo
cambiara. Comprobarlo contra el color declarado haría que el reto nunca
funcionara.

### Restricción del +4

Regla oficial, activa por defecto (`wild4Restriction`):

```csharp
if (card.value === Value.WildDrawFour) {
    if (rules.wild4Restriction && holdsColor(hand, topColor))
        return { legal: false, reason: 'WILD4_BLOCKED' };
    return { legal: true };
}
```

`holdsColor` **excluye los comodines**: tener otro Wild no te impide jugar un +4.

---

## 4.4 ¡UNO! y su ventana

Tres caminos, todos cubiertos por tests:

| Situación | Resultado |
|---|---|
| Cantas dentro de la ventana | `uno_called`, sin penalización |
| **Declaración anticipada** con 2 cartas | `unoDeclared = true`, cubre la jugada siguiente |
| Expira la ventana | `uno_caught` + **roba 2** |
| El rival pulsa `catch_uno` a tiempo | `uno_caught` + **roba 2** (atribuido al que pilló) |
| Encadenas Skip/Reverse a 2P | **no** te castigas a ti mismo |

```csharp
private void CloseUnoWindow(int actingSeat, List<TurnEvent> events) {
    if (UnoWindowSeat < 0) return;
    int victim = UnoWindowSeat;
    if (victim == actingSeat) return;      // ← sin esto, te penalizas solo
    ...
}
```

---

## 4.5 Puntuación y fin de partida

Tabla oficial:

| Carta | Puntos |
|---|---|
| Número 0–9 | valor facial |
| Skip / Reverse / Draw Two | 20 |
| Wild / Wild Draw Four | 50 |

El ganador de la ronda suma **las cartas de todos los rivales**. La partida acaba
a los **500 puntos** (configurable).

Detalle oficial fácil de olvidar: **si la última carta jugada es un +2 o un +4, la
víctima roba esas cartas *antes* de puntuar**, y se incluyen en el recuento.

---

## 4.6 House rules (paridad con UNO! Mobile)

| Rule | Flag | Comportamiento |
|---|---|---|
| Apilar castigos | `stacking` | +2 sobre +2, +4 sobre +4 |
| Reto al +4 | `wild4Challenge` | el penalizado puede obligar a enseñar mano |
| Restricción del +4 | `wild4Restriction` | sólo si no tienes el color activo |
| Robar hasta poder jugar | `drawUntilPlayable` | oficial; `false` = robar 1 y pasar |
| **7-0** | `sevenZero` | 7 = intercambiar manos, 0 = rotar manos en el sentido del juego |
| Pasar con carta jugable | `allowPassWithPlay` | house rule permisiva |
| Jump-In | `jumpIn` | reservado |

`7-0` es la que más cambia la partida y está implementada y testeada (el 7 exige
destino válido o devuelve `BadSwapTarget`).

---

## 4.7 Async/await: por qué el juego no se congela con lag

El principio es único: **la animación empieza antes que la red**.

```csharp
public async Task<bool> PlayCardAsync(Card card, CardColor? chosenColor = null, int swapTargetSeat = -1) {
    if (Net == null || !IsMyTurn) return false;

    handLayout?.DetachForPlay(card.Id);          // 1) la carta sale de la mano YA

    var result = await Net.PlayCardAsync(card.Id, chosenColor, swapTargetSeat);  // 2) red

    if (!result.Ok) {
        handLayout?.SnapBack(card.Id);           // 3b) vuelve con feedback
        juice?.PlayRejected(result.ErrorCode);
        return false;
    }
    return true;                                 // 3a) el evento confirma la animación
}
```

Reglas que se respetan en todo el código del cliente:

| Regla | Motivo |
|---|---|
| Nunca `.Result`, nunca `.Wait()` | bloquean el hilo principal → congelación literal |
| Nunca `Thread.Sleep` | igual |
| Los mensajes de red se drenan en `Update()` | tocar `GameObject` desde otro hilo rompe Unity |
| `DrainIncoming(maxPerFrame: 32)` | un aluvión de eventos no puede comerse un frame entero |
| `TaskCompletionSource` con `RunContinuationsAsynchronously` | evita que la continuación se ejecute en el hilo del socket |
| Timeout de 8 s por petición | un paquete perdido no puede dejar una `Task` colgada para siempre |
| El servidor nunca espera al cliente | los timers corren en el servidor; si no respondes, juega solo |

Y en el servidor: el motor de reglas es **100 % síncrono**. No hay nada que
bloquear, porque no hay I/O en el camino crítico de una jugada.

---

## 4.8 Los vectores dorados: cómo se impide la divergencia

Tener dos implementaciones de las mismas reglas (servidor y cliente) es el riesgo
número uno de este proyecto. Se controla con
[`shared/golden-vectors.json`](../shared/golden-vectors.json):

```
server/src  ──(implementación de referencia, cubierta por 63+ tests)──►  generate-golden-vectors.mjs
                                                                                    │
                                                          shared/golden-vectors.json
                                                                    │               │
                                                        server/test/golden-vectors.test.js
                                                                    │
                                            unity-client/Tests/Core/GoldenVectorsTests.cs  (CI)
```

Contiene, generado desde el servidor:

| Sección | Casos |
|---|---|
| `rng` | 7 semillas × 12 valores en doble precisión |
| `decks` | 7 semillas: primeros 20 ids + hash FNV-1a del mazo completo |
| `seatTable` | 36 combinaciones de asiento/jugadores/sentido |
| `resolveTable` | 28 combinaciones de carta/jugadores/sentido |
| `scoring` | 5 manos con su puntuación oficial |
| `legalCases` | 9 casos de legalidad, incluida la restricción del +4 |

Si alguien cambia `DeckManager.cs` o `RulesEngine.cs` y los aleja del servidor,
**el build de Unity falla**. Sin esto, la divergencia aparecería en producción
como «el móvil grisó una carta que el PC sí dejaba jugar».

El workflow de CI además verifica que el archivo no esté desactualizado:

```yaml
node scripts/generate-golden-vectors.mjs
if ! git diff --quiet -- ../shared/golden-vectors.json; then
  echo "::error::shared/golden-vectors.json está desactualizado"
  exit 1
fi
```
