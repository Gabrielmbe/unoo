# Módulo 3 · Animaciones y efectos ultra-juicy (3x)

> Código: [`unity-client/Assets/Scripts/FX/`](../unity-client/Assets/Scripts/FX/) · Shaders: [`unity-client/Assets/Shaders/`](../unity-client/Assets/Shaders/)

El objetivo no es «muchos efectos». Es que cada efecto **comunique estado de
juego** y que el conjunto se sienta pesado. Cuatro principios sostienen todo lo
demás.

---

## 3.0 Los cuatro principios

### 1. Un solo momento estrella por jugada

Si todo tiembla a la vez, nada impacta. [`JuiceDirector`](../unity-client/Assets/Scripts/FX/JuiceDirector.cs)
asigna una **intensidad** a cada evento y acumula un *presupuesto* de shake que
decae, en lugar de lanzar un shake independiente por evento:

```csharp
private void AddShake(float intensity) {
    _shakeBudget = Mathf.Min(1.2f, _shakeBudget + intensity);
    shakeRig?.AddTrauma(_shakeBudget * 0.55f * TierMultiplier());
}
```

Un +4 seguido de un ¡UNO! pillado **se suma**: la racha se siente como racha.

| Evento | Intensidad |
|---|---|
| Número normal | 0.18 |
| Skip | 0.40 |
| Reverse | 0.45 |
| Comodín | 0.60 |
| **+2** | **0.70** |
| **+4 / Wild Draw Four** | **1.00** |

### 2. Hit-stop

Al jugar un +4, `Time.timeScale` baja a **0.15 durante ~90 ms**. El cerebro lo
lee como «esto ha sido importante». Es el truco más rentable de todo el documento
y cuesta cuatro líneas:

```csharp
private IEnumerator HitStopRoutine(float seconds) {
    float previous = Time.timeScale;
    Time.timeScale = hitStopScale;
    Time.fixedDeltaTime = 0.02f * Time.timeScale;
    yield return new WaitForSecondsRealtime(seconds);   // ← Realtime, no WaitForSeconds
    Time.timeScale = previous;
    Time.fixedDeltaTime = 0.02f;
}
```

El detalle que lo rompe si se olvida: las rutinas de efectos usan
`WaitForSecondsRealtime`. Con `WaitForSeconds`, el hit-stop ralentizaría su
propia animación y el resultado sería un tartamudeo.

### 3. Presupuesto por plataforma

```csharp
private static QualityTier DetectTier() {
    if (Application.isEditor) return QualityTier.Ultra;
    if (SystemInfo.deviceType == DeviceType.Handheld) {
        if (SystemInfo.systemMemorySize < 3500 || SystemInfo.processorCount <= 4) return QualityTier.Low;
        if (SystemInfo.systemMemorySize < 6000) return QualityTier.Medium;
        return QualityTier.High;
    }
    return SystemInfo.systemMemorySize >= 16000 ? QualityTier.Ultra : QualityTier.High;
}
```

**Regla innegociable:** se recorta el *espectáculo*, nunca la *información*. Un
móvil de gama baja ve menos partículas y sin hit-stop, pero siempre ve **quién
roba, cuánto y de qué color es el turno**. Los emotes, que son cosméticos
puros, se ignoran por completo en `Low`.

### 4. Nunca bloquear

Todos los efectos son corutinas o `async`. Si el servidor tarda, la animación
sigue y el siguiente evento se encola. `OnDisable()` restaura `Time.timeScale = 1`
para que un cambio de escena no deje el juego a cámara lenta.

---

## 3.1 Robo y descarte: flexión 3D + estela

### Flexión en el vertex shader

[`CardBend.shader`](../unity-client/Assets/Shaders/CardBend.shader) +
[`CardBendController.cs`](../unity-client/Assets/Scripts/FX/CardBendController.cs).

Una carta de cartón no se mueve como un quad rígido: al lanzarla se comba y
recupera la forma al frenar. Ese detalle es lo que hace que *se sienta* una carta
y no un sprite.

```hlsl
float along = dot(pos.xy, normalize(_BendAxis.xy + float2(1e-5, 1e-5)));
float curve = along * abs(along) * _BendStiffness;   // cuadrática: creíble y barata
pos.z += curve * _BendAmount;
pos.xy -= normalize(_BendAxis.xy) * abs(curve * _BendAmount) * 0.06;  // se acorta al combarse
```

Y en el fragment, un sombreado barato que da volumen sin luces:

```hlsl
col.rgb *= lerp(1.0h, 0.72h, IN.bend);
```

**Por qué en GPU y no con física:** el coste por carta es constante e
independiente del número de cartas en vuelo, que es exactamente lo que se
necesita cuando caen 4 cartas de un +4. Sin `Rigidbody`, sin colisionadores, sin
`FixedUpdate`.

El controller alimenta dos floats con un resorte amortiguado, así la carta
**siempre vuelve a estar plana**:

```csharp
float accel = -_bend * bendResponse * bendResponse - _bendVelocity * bendDamping * bendResponse;
```

### Estela: mesh propio, no `TrailRenderer`

[`RibbonTrail.cs`](../unity-client/Assets/Scripts/FX/RibbonTrail.cs).

El `TrailRenderer` de Unity genera geometría en el hilo principal y la reconstruye
entera cuando cambia el número de puntos. Con 4 cartas volando a la vez se nota
el tirón. Este ribbon usa un **buffer de longitud fija** con desplazamiento:

```csharp
System.Array.Copy(_positions, 1, _positions, 0, maxPoints - 1);
_positions[maxPoints - 1] = p;
```

**Cero asignaciones en régimen estable.** El ancho y el color van por edad del
punto (`colorGradient.Evaluate(age01)`), y la orientación se calcula con la
tangente entre vecinos cruzada con la dirección de cámara.

### Vuelo de duración fija

[`CardFlightArc.cs`](../unity-client/Assets/Scripts/FX/CardFlightArc.cs) — y aquí
hay una decisión específica de juego **online**:

> Cuando el rival juega una carta, su cliente ya la animó hace 150 ms. Si el tuyo
> la animara con una duración dependiente de cualquier cosa variable, verías la
> jugada desincronizada respecto a su sonido.

El vuelo dura **siempre lo mismo** y arranca en cuanto llega el evento. Lo que
llega tarde con mal ping es la *información*, nunca la *animación*.

---

## 3.2 Cartas especiales: temblor + destello + rayos

```csharp
public void PlayDrawPenalty(int victimSeat, int amount, int sourceSeat) {
    StartCoroutine(DrawPenaltyRoutine(victimSeat, amount, amount >= 4 ? 1f : 0.6f));
}

private IEnumerator DrawPenaltyRoutine(int victimSeat, int amount, float intensity) {
    HitStop(hitStopSeconds * (amount >= 4 ? 1.6f : 1f));
    AddShake(intensity);

    var victim = AnchorFor(victimSeat);
    var source = AnchorFor(sourceSeat);
    Color c = amount >= 4 ? new Color(0.85f, 0.2f, 1f)     // +4: violeta
                          : new Color(1f, 0.35f, 0.15f);    // +2: naranja
    Flash(c, intensity * 0.8f);
    lightning.Fire(source.position, victim.position,
                   bolts: amount >= 4 ? 5 : 3, duration: 0.55f, color: c);

    yield return new WaitForSecondsRealtime(0.12f);
    AddShake(intensity * 0.5f);          // segundo golpe: el impacto "rebota"
}
```

La secuencia es **hit-stop → shake → destello → rayos → segundo golpe a los
120 ms**. El doble golpe es lo que convierte un efecto en un *impacto*.

### Rayos procedurales

[`LightningBeam.cs`](../unity-client/Assets/Scripts/FX/LightningBeam.cs) — sin
VFX Graph ni texturas. `LineRenderer` con ruido Perlin:

```csharp
float amp = jaggedness * (1f - progress);          // nace violento, muere recto
float envelope = Mathf.Sin(u * Mathf.PI);          // 0 en extremos, 1 en el centro
float noise = Mathf.PerlinNoise(u * 6f + Time.unscaledTime * 9f, progress * 3f) * 2f - 1f;
_scratch[i] = Vector3.Lerp(from, to, u) + perp * (noise * amp * envelope);
```

Dos detalles que separan un rayo creíble de uno falso:

- el **envelope** senoidal: sin él el rayo se despega del origen y se ve pegado;
- la amplitud **decae** con el progreso: un arco eléctrico real nace violento y
  muere recto.

**Pool de 12 rayos reutilizados.** Instanciar y destruir GameObjects cada vez que
alguien juega un +2 es la forma más rápida de provocar tirones en Android.

**Corrección aplicada:** `Reshape()` asignaba `new Vector3[segments]` cada frame
y por rayo. Es basura para el GC, y el GC es la causa número uno de tirones en
móvil. Ahora usa un buffer `_scratch` reutilizado.

### Sacudida por trauma con ruido Perlin

[`ScreenShakeRig.cs`](../unity-client/Assets/Scripts/FX/ScreenShakeRig.cs). Sin
dependencia de Cinemachine.

```csharp
float shake = _trauma * _trauma;    // decaimiento CUADRÁTICO
_trauma = Mathf.Max(0f, _trauma - traumaDecay * Time.unscaledDeltaTime);
float x = (Mathf.PerlinNoise(_seedX, t) * 2f - 1f) * maxDisplacement * shake;
```

- **Cuadrático**, no lineal: el golpe fuerte cae rápido y la cola es suave. Un
  decaimiento lineal se siente mecánico.
- **Perlin, no `Random`**: el `Random` da un temblor digital que marea.
- Captura `_basePosition` / `_baseRotation` y siempre suma sobre ellas, así el
  shake nunca se acumula en la posición real de la cámara.

---

## 3.3 El botón ¡UNO!: fuego y electricidad

[`UnoPulse.shader`](../unity-client/Assets/Shaders/UnoPulse.shader) +
[`UnoButtonView.cs`](../unity-client/Assets/Scripts/UI/UnoButtonView.cs).

Un solo material cubre **los dos estados**, mezclado por `_Energy` con un
`MaterialPropertyBlock` (un material por estado rompería el batching):

| `_Energy` | Aspecto |
|---|---|
| bajo | llama lenta y anaranjada — ventana abierta, sin urgencia |
| alto | arco eléctrico blanco-azulado y rápido — último segundo |

```hlsl
float t = _Time.y * _Speed * lerp(0.7, 3.0, _Energy);   // acelera con la urgencia
float flame = saturate(fbm2(q) * 1.6 - 0.25) * falloff; // fuego
float bolt  = smoothstep(0.62, 0.98, vnoise(float2(radius * 9.0, t * 2.2))) * falloff * _Energy; // rayos
```

Todo procedural: ruido de valor con hash + 2 octavas de fbm. **Cero texturas,
cero samplers extra** — en móvil cada sampler adicional es ancho de banda que se
paga caro.

### La carrera por latencia

El botón late en la pantalla de **ambos** jugadores, con dos modos:

```csharp
Mode = isMine ? UnoButtonMode.MustCall : UnoButtonMode.CanCatch;
```

El que tiene que cantar ve fuego; el que puede pillar ve electricidad. Y el envío
es lo primero que ocurre:

```csharp
private void HandleClick() {
    Pressed?.Invoke();      // 1) PRIMERO se manda a la red
    _pressAnim = 1f;        // 2) DESPUÉS, el espectáculo
    _shake = pressShake;
}
```

El servidor resuelve por **orden de llegada** (`uno_caught`, `uno_called`). El
primero que entra gana; la latencia es inherentemente justa porque se mide en el
servidor, no en el cliente.

---

## 3.4 Presupuesto móvil concreto

| Técnica | Coste ahorrado |
|---|---|
| Flexión en vertex shader | 0 `Rigidbody`, 0 `FixedUpdate` por carta |
| Ribbon con buffer fijo | 0 asignaciones en régimen estable |
| Pool de rayos | 0 `Instantiate`/`Destroy` por jugada |
| `MaterialPropertyBlock` | 1 draw call en vez de N |
| Un atlas de cartas | 1 draw call en vez de 54 |
| `_scratch` reutilizado en rayos | 0 basura para el GC por frame |
| Shaders de 1 pass, sin sombras | ~40 % menos de coste de fragment |
| `WaitForSecondsRealtime` en efectos | el hit-stop no se ralentiza a sí mismo |

Regla general que se aplica en todo el proyecto: **si se puede precomputar o
reutilizar, no se asigna en el bucle**.
