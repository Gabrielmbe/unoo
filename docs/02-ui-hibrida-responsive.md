# Módulo 2 · Interfaz híbrida responsiva (PC y móvil a la vez)

> Código de referencia: [`unity-client/Assets/Scripts/UI/`](../unity-client/Assets/Scripts/UI/)

El objetivo es **un solo prefab de UI** que sirva en un iPhone SE en vertical, en
un tablet en horizontal y en un monitor 4K con ratón. Sin variantes por
plataforma, sin prefabs duplicados.

---

## 2.1 El error habitual: escalar por resolución

Un `Canvas Scaler` en *Scale With Screen Size* con un `match` fijo produce:

- en 4K → cartas enanas en un monitor de 32",
- en un móvil pequeño → cartas que se salen de la pantalla.

El motivo es que se está escalando por **píxeles** cuando lo que el usuario
percibe es **tamaño físico**. Lo que tiene que ser constante entre dispositivos
es:

1. el tamaño de la carta en milímetros,
2. el tamaño del objetivo táctil (≥ 9 mm; 44 px en iOS, 48 dp en Android).

## 2.2 La solución: match dinámico + tope por DPI

En [`HybridCanvasScaler.cs`](../unity-client/Assets/Scripts/UI/HybridCanvasScaler.cs):

```csharp
_scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
_scaler.matchWidthOrHeight = IsPortrait ? 0f : 1f;
```

| Orientación | `match` | Manda | Por qué |
|---|---|---|---|
| Vertical (móvil) | `0` | el **ancho** | la mano de cartas es horizontal: el ancho es el recurso escaso |
| Horizontal (tablet / PC) | `1` | el **alto** | el ancho sobra; lo que falta es alto para la mano + el descarte + los avatares |

Encima, un factor calculado desde el DPI real mantiene la carta en ~34 mm
físicos, limitado entre `0.55×` y `3.2×` para no desbordar en los extremos:

```csharp
float cardWidthInchesAtScale1 = cardWidthInReferenceUnits / reference.x * Screen.width / dpi;
float scale = targetInches / cardWidthInchesAtScale1;
if (ScreenDiagonalInches > 17f)                      // monitor de sobremesa
    scale = Mathf.Lerp(scale, 1f, 1f - largeScreenBias);
CurrentScale = Mathf.Clamp(scale, minScale, maxScale);
```

Y una utilidad que debería usarse para **todo** lo tocable:

```csharp
public float MmToCanvasUnits(float mm);          // mm → unidades de canvas
public bool  MeetsTouchTarget(RectTransform r);  // ¿cumple los 9 mm mínimos?
```

### Reacción a cambios

`Update()` sólo recalcula si cambió `Screen.width`, `Screen.height` o
`Screen.dpi`. Cubre: rotar el móvil, plegar un foldable, arrastrar la ventana en
PC, y moverla a un monitor con otro DPI. Al recalcular dispara
`OrientationChanged` para que el layout de la mesa se reorganice.

### Zona segura

[`SafeAreaAdapter.cs`](../unity-client/Assets/Scripts/UI/SafeAreaAdapter.cs)
recorta al `Screen.safeArea` con anclas (no con `sizeDelta`, que se rompe al
rotar). En un juego de cartas **no es cosmético**: si la mano queda bajo el notch,
el jugador no puede arrastrar su última carta y pierde la partida.

---

## 2.3 La mano de cartas: física visual que escala

Implementada en [`CardHandLayout.cs`](../unity-client/Assets/Scripts/UI/CardHandLayout.cs).

### El problema aritmético

En un iPhone SE en vertical hay ~320 px útiles. Con 15 cartas de 120 px hacen
falta 1.800 px. Las opciones malas son hacerlas minúsculas (ilegibles) o
recortarlas. La buena es **solaparlas**, calculando el paso entre cartas:

```csharp
float noOverlap  = effectiveWidth;                        // sin solapar
float maxStep    = effectiveWidth * (1f - maxOverlapRatio); // solape tope: 18 %
float stripStep  = minVisibleStrip * ScaleFactor;          // franja legible: 34 px
float step = Mathf.Min(noOverlap, Mathf.Max(maxStep, stripStep));

// Si aun así no caben, se comprime hasta rellenar el ancho útil.
float needed = effectiveWidth + step * (n - 1);
if (needed > available && n > 1)
    step = (available - effectiveWidth) / (n - 1);
```

El invariante es `minVisibleStrip`: **siempre queda visible la franja que
distingue una carta de otra** (color y número, que en UNO están en la esquina
superior izquierda). Con 7 cartas la mano se abre; con 22 se comprime; la franja
legible no se sacrifica nunca.

En 4K la misma fórmula da un paso grande, así que el abanico se abre, se curva y
se separa. Mismo algoritmo, parámetros distintos, cero prefabs extra.

### Abanico y arco

```csharp
float t = n == 1 ? 0.5f : i / (float)(n - 1);
float centered = t - 0.5f;
float x   = startX + step * i;
float y   = -Mathf.Abs(centered) * arcHeight * 2f * ScaleFactor;  // arco
float rot = -centered * curvature * (n - 1) * 0.5f;               // abanico
```

La curvatura se amortigua con `Clamp01(n / 8f)`: con 3 cartas una fila recta se
ve mejor que un abanico exagerado.

### Física: resorte amortiguado, no tweens

```csharp
Vector2 accel = toTarget * positionStiffness - slot.Velocity * positionDamping;
slot.Velocity += accel * dt;
rt.anchoredPosition += slot.Velocity * dt;
```

Por qué no DOTween aquí: con 20+ cartas moviéndose a la vez, un tween por carta
genera callbacks y basura. Un bucle de integración no genera nada y se integra
con `deltaTime`, así que **se ve igual a 30 fps en un móvil viejo que a 144 fps
en un PC**.

El `staggerSeconds` (45 ms) hace que al robar 4 cartas de un +4 entren **en
cascada**, no todas a la vez. Ese detalle es la mitad de la sensación de peso.

### Orden de render

`slot.View.Rect.SetSiblingIndex(i)` — la carta del centro queda por encima, como
en una mano real. Sin esto, al solaparse se ve un mosaico plano.

---

## 2.4 Drag & Drop unificado: ratón y dedos en el mismo bucle

En [`CardDragController.cs`](../unity-client/Assets/Scripts/UI/CardDragController.cs).

### Por qué no `IBeginDragHandler` de Unity UI

- sólo responde a **un** puntero;
- pelea con el scroll del layout;
- no da el «levantar la carta» con inercia que se siente bien con ratón;
- **no permite multitáctil**, y aquí es una mecánica real: cantar ¡UNO! con un
  dedo mientras arrastras una carta con el otro.

### Lo que hace este controlador

```csharp
// ratón (PC)
if (Input.GetMouseButtonDown(0) && !IsPointerOverUiButton(Input.mousePosition))
    TryBegin(Input.mousePosition, PointerId.Mouse);

// dedos (móvil): reserva UNO para arrastrar y deja los demás libres para la UI
for (int i = 0; i < Input.touchCount; i++) { ... }
```

**Umbral de arrastre en milímetros, no en píxeles.** 4 mm en un móvil son muchos
más píxeles que en un 4K, y el umbral tiene que *sentirse* igual:

```csharp
float ThresholdPx => dragThresholdMm / 25.4f * (Screen.dpi > 1f ? Screen.dpi : 96f);
```

Detalles de sensación:

| Detalle | Por qué |
|---|---|
| `followLerp = 0.55` | el seguimiento 1:1 se siente rígido y "de sprite" |
| `tiltPerVelocity` | la carta se inclina según la velocidad del puntero: parece que pesa |
| `scaleWhileDragging = 1.12` | la carta levantada se acerca a la cámara |
| `SetFlightVelocity` | alimenta la flexión 3D del shader (Módulo 3) |
| `IsPointerOverUiButton` | evita robar el gesto a los botones ¡UNO!, emotes y pasar |

---

## 2.5 La carta como vista

[`CardView.cs`](../unity-client/Assets/Scripts/UI/CardView.cs) — un prefab con
cara, dorso, outline de «jugable», flexión y estela.

Dos decisiones de rendimiento móvil que importan de verdad:

**1. Un solo atlas.** Cambiar de sprite dentro del atlas **no rompe las draw
calls**; cambiar de material sí. Con 15 cartas en mano, esto es la diferencia
entre 1 y 15 draw calls.

**2. `MaterialPropertyBlock` para el realce.** El brillo de «carta jugable» va
por propiedad de material, no por material distinto:

```csharp
_renderer.GetPropertyBlock(_mpb);
_mpb.SetFloat(HighlightAmount, playable ? 1f : 0f);
_renderer.SetPropertyBlock(_mpb);
```

Las cartas no jugables se atenúan a `alpha 0.42` **antes** de que el usuario
intente arrastrarlas. Es feedback instantáneo y sin viaje de red: la copia local
del motor de reglas (Módulo 4) ya sabe qué es legal.

---

## 2.6 El botón ¡UNO! en ambas pantallas

[`UnoButtonView.cs`](../unity-client/Assets/Scripts/UI/UnoButtonView.cs). Tres
decisiones que no son cosméticas:

1. **Envío en el mismo frame del toque.** La animación arranca *después* de
   mandar el mensaje. Cada milisegundo de animación previa es desventaja real en
   la carrera que resuelve el servidor.
2. **Visible en los dos jugadores**, con dos modos (`MustCall` / `CanCatch`): es
   información de juego, no decoración. El que puede pillar tiene que saber que
   puede.
3. **Countdown con el reloj del servidor** (`Net.ServerNowMs()`), no con
   `Time.time`: la barra se agota en el mismo instante en el PC y en el móvil.

---

## 2.7 Matriz de dispositivos objetivo

| Dispositivo | Orientación | Comportamiento resultante |
|---|---|---|
| iPhone SE (4.7") | vertical | `match=0`, cartas comprimidas, franja de 34 px garantizada |
| Pixel 8 | vertical | igual, con zona segura por agujero de cámara |
| iPad (11") | horizontal | `match=1`, abanico abierto, cartas grandes |
| Foldable plegado | vertical | `Update()` detecta el cambio de resolución y recalcula |
| Portátil 1080p | horizontal | escala ~1.0, ratón + drag con tilt |
| Monitor 4K 32" | horizontal | `largeScreenBias` compensa; la UI no parece de móvil |
| Surface (táctil + ratón) | horizontal | **ambas** entradas activas a la vez |
