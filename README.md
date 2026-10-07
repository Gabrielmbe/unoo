# unoo — UNO-X

Clon multijugador online de **UNO!™** para **Unity**, multiplataforma (PC y móvil
a la vez), con servidor autoritativo ligero desplegable en Render.

Diseñado para partidas **1v1 por invitación** entre dos personas a distancia
(aunque el motor soporta hasta 4 asientos y 2v2).

---

## Qué hay en el repo

```
server/                     Servidor autoritativo Node.js + WebSocket
  src/deck.js               108 cartas, orden canónico, barajado determinista
  src/rules.js              Motor de reglas PURO (sin estado, sin red)
  src/game.js               Máquina de estados de la partida
  src/lobby.js              Salas por código de invitación
  src/session.js            Capa de red, anti-cheat, reconexión
  src/index.js              HTTP + /health + upgrade a WS
  test/                     63+ tests, incluida integración con WS reales
  scripts/                  Generador de vectores dorados

unity-client/
  Assets/Scripts/Core/      Reglas en C#, SIN UnityEngine → testeables fuera del editor
  Assets/Scripts/Net/       Cliente WebSocket, fachada NetworkCardPlayer
  Assets/Scripts/UI/        UI híbrida PC/móvil, mano dinámica, drag & drop
  Assets/Scripts/FX/        Juice: shake, rayos, estelas, flexión, hit-stop
  Assets/Shaders/           CardBend (URP) y UnoPulse (URP)
  Tests/Core/               xunit contra los vectores dorados compartidos

shared/golden-vectors.json  Contrato entre servidor y cliente
docs/                       Los cuatro módulos de diseño
```

## Documentación

| Módulo | Contenido |
|---|---|
| [1 · Arquitectura Netcode](docs/01-arquitectura-netcode.md) | Por qué WebSocket propio y no Mirror/Photon · salas por código · validación autoritativa · tabla de ataques cerrados · reconexión |
| [2 · UI híbrida responsive](docs/02-ui-hibrida-responsive.md) | Canvas Scaler con match dinámico · escalado por tamaño físico · física de la mano · drag & drop ratón+dedos |
| [3 · Efectos ultra-juicy](docs/03-fx-ultra-juicy.md) | Hit-stop · shake por trauma · flexión 3D en vertex shader · rayos procedurales · botón ¡UNO! |
| [4 · Reglas y lógica](docs/04-reglas-y-logica.md) | DeckManager · TurnManager · acumulación de castigos · reto al +4 · async/await contra el lag |
| [5 · Despliegue en Render](docs/05-despliegue-render.md) | PORT, 0.0.0.0, health check, spin-down del free tier, escalado |

---

## Arranque rápido

### Servidor

```bash
cd server
npm install
npm test        # suite completa
npm run dev     # ws://localhost:10000/ws
```

```bash
curl http://localhost:10000/health
# {"ok":true,"protocol":1,"rooms":0,"connections":0,"uptime":3}
```

### Cliente Unity

1. Abre `unity-client/` con **Unity 2022.3 LTS o superior** (URP).
2. Instala `com.unity.nuget.newtonsoft-json` (ya viene con muchos paquetes).
3. Apunta `GameManager.serverUrl` a `ws://localhost:10000/ws`.
4. Prueba con dos instancias (Multiplayer Play Mode) o con un móvil y un PC.

### Despliegue

Render → **New + → Blueprint** → selecciona el repo. Lee [`render.yaml`](render.yaml).

---

## Verificación

### Lo que SÍ está ejecutado y en verde

```bash
cd server && npm test
```

**70 tests pasando**, cubriendo:

- **Mazo**: composición exacta de las 108 cartas, barajado determinista, reciclado
  del descarte.
- **Reglas**: coincidencia por color/número, restricción oficial del Wild Draw
  Four, apilado, avance de asiento a 2 y 4 jugadores, puntuación oficial.
- **Partida**: partida completa hasta 500 puntos sin excepciones, timeouts con
  auto-jugada, ¡UNO! y sus tres caminos, reto al +4, house rule 7-0, conservación
  de las 108 cartas en todo momento.
- **Red (integración con WebSockets reales)**: sala por código, sala llena,
  anfitrión que migra, reconexión con token recuperando la mano exacta, límite de
  tasa, JSON malformado, `NOT_IN_HAND` / `NOT_YOUR_TURN`, y que **la mano del
  rival nunca viaja**.
- **Vectores dorados**: el servidor coincide con el contrato compartido (7 tests).

Estabilidad: **30 ejecuciones consecutivas de la suite completa, 0 fallos**.
(Dos tests eran intermitentes; en ambos casos el bug estaba en la aserción del
test, no en el servidor — ver más abajo.)

Adicionalmente, los 23 archivos C# pasan un chequeo de balance de llaves y
paréntesis, y un chequeo de enlaces que confirma que **todos** los métodos que
`GameManager` llama sobre `CardHandLayout`, `UnoButtonView`, `JuiceDirector`,
`LightningBeam` y `ScreenShakeRig` existen con esos nombres. Eso **no sustituye a
un compilador**: no detecta errores de tipos, de sobrecargas ni de firmas.

También se verificó el servidor **como proceso real**: arranque en `0.0.0.0`,
`/health` respondiendo, creación de sala por código y arranque de partida con dos
clientes WebSocket, confirmando que la mano del rival no se filtra.

### Lo que NO se ha podido compilar aquí

**El código C# no se ha compilado en este entorno.** El sandbox no tiene SDK de
.NET ni acceso a NuGet, y Unity no está disponible. Lo que sí se hizo:

- **Los archivos de `Core/` no tienen ninguna referencia a `UnityEngine`**
  (verificable con `grep -hn "^using" unity-client/Assets/Scripts/Core/*.cs`: sólo
  aparecen `System` y `System.Collections.Generic`), así que compilan con `dotnet`
  sin Unity. El proyecto de tests
  [`unity-client/Tests/Core/Core.Tests.csproj`](unity-client/Tests/Core/Core.Tests.csproj)
  está listo para `dotnet test` y lo ejecuta el workflow de CI.
- **La aritmética del puerto C#↔JS se verificó numéricamente**: se reprodujeron
  las semánticas exactas de `uint`/`unchecked`/`>>` de C# y se compararon contra
  el mulberry32 real de JavaScript — **32.000 valores, 0 divergencias**.
- **El algoritmo de barajado de `DeckManager.cs` se verificó igual**: espejo de la
  semántica C# comparado contra los vectores dorados — **7 semillas, hash FNV-1a
  idéntico, 0 fallos**.
- Los scripts de **UI, FX y Net sí usan `UnityEngine`** y sólo pueden validarse con
  el compilador del editor. **No se han compilado.**

### Bugs que encontraron los tests (y se corrigieron)

Merece la pena listarlos porque tres habrían llegado a producción:

1. **`this.challenge` (propiedad) sobreescribía al método `challenge()`** en
   `game.js`. Un reto al +4 habría lanzado `TypeError: this.challenge is not a
   function` y tirado la sala. Renombrado a `pendingChallenge` /
   `resolveChallenge`.
2. **`ResolveSeatAfterPlay` avanzaba 1 asiento en vez de 2 para +2/+4**, así que
   la víctima robaba y *además* jugaba. Corregido y cubierto por la tabla de
   vectores dorados.
3. **Al apilar, el turno no viajaba al nuevo objetivo**, dejando
   `currentSeat` y `pending.targetSeat` desincronizados.
4. **`Value.Seven` no existía** y **`this.rand01` no estaba definido**.
5. **Dividir por 2³² en `float`** en el RNG de C# habría divergido de JavaScript
   en algunos valores. Cambiado a `double`.
6. **`app.close()` colgaba para siempre** porque `httpServer.close()` espera a los
   sockets WebSocket vivos. Ahora se terminan explícitamente.
7. **Dos aserciones de test eran incorrectas**, no el servidor: asumían mano de 7
   (si la carta inicial es un +2, son 9) y que el asiento cambia tras un timeout
   (a 2 jugadores, un Skip o un Reverse lo deja en el mismo jugador).

---

## Reglas implementadas

Reglamento oficial de Mattel: 108 cartas, 7 por jugador, coincidencia por color o
número, puntuación oficial (número = valor facial, acción = 20, comodín = 50),
primero a 500 puntos, restricción del Wild Draw Four, reto al +4, y la regla de 2
jugadores (Skip/Reverse/+2/+4 devuelven el turno al mismo jugador).

House rules opcionales, como en el *Room Mode* de UNO! Mobile:

| Rule | Flag | Por defecto |
|---|---|---|
| Apilar +2 con +2 y +4 con +4 | `stacking` | no (oficial lo prohíbe) |
| Reto al Wild Draw Four | `wild4Challenge` | no |
| Restricción del +4 | `wild4Restriction` | **sí** |
| Robar hasta poder jugar | `drawUntilPlayable` | **sí** |
| 7-0 (7 intercambia, 0 rota) | `sevenZero` | no |

---

## Licencia

MIT. **UNO™ es una marca registrada de Mattel.** Este es un proyecto técnico y
educativo: no uses el nombre, el logotipo ni el arte de UNO en nada que publiques.
