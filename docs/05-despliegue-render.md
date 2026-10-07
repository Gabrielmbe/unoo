# Módulo 5 · Despliegue en Render y puesta en marcha

> Archivos: [`render.yaml`](../render.yaml) · [`server/Dockerfile`](../server/Dockerfile) · [`.github/workflows/ci.yml`](../.github/workflows/ci.yml)

---

## 5.1 Lo que hay que saber de Render antes de tocar nada

| Hecho | Consecuencia para este proyecto |
|---|---|
| Render **inyecta `PORT`** (documenta 10000 por defecto) | Nunca hardcodear el puerto |
| Hay que bindear a **`0.0.0.0`** | `localhost` no es alcanzable desde fuera |
| Puertos **18012, 18013 y 19099 reservados** | `index.js` lanza si se intenta usarlos |
| `/health` para el health check | implementado, devuelve `rooms` y `connections` |
| 512 MB en el tier de entrada | de sobra: ~40 MB en reposo, ~2 KB por sala |
| **Free tier: duerme a los 15 min sin tráfico** y tarda ~1 min en despertar | inaceptable para jugar de verdad; el cliente lleva reconexión con backoff |
| WebSockets fiables sólo en planes de pago | para producción, plan `starter` (siempre encendido) |
| SIGTERM en cada deploy | cierre limpio que avisa a los clientes para que reconecten |

El `index.js` respeta todo esto explícitamente:

```js
const PORT = Number(process.env.PORT) > 0 ? Number(process.env.PORT) : 10000;
const HOST = process.env.HOST ?? '0.0.0.0';
const RESERVED_RENDER_PORTS = [18012, 18013, 19099];
```

---

## 5.2 Despliegue

### Opción A — Blueprint (recomendada)

1. Sube el repo a GitHub.
2. Render → **New +** → **Blueprint** → selecciona el repo.
3. Render lee [`render.yaml`](../render.yaml) y crea el servicio.
4. Copia la URL: `wss://<tu-servicio>.onrender.com/ws`.

### Opción B — Docker

```bash
cd server
docker build -t unox-server .
docker run --rm -p 10000:10000 -e PORT=10000 unox-server
```

### Opción C — Manual

| Campo | Valor |
|---|---|
| Root Directory | `server` |
| Runtime | Node 20 |
| Build Command | `npm ci --omit=dev` |
| Start Command | `npm start` |
| Health Check Path | `/health` |

> **No definas `PORT`** en las variables de entorno. Es el error de despliegue más
> común en Render.

### Verificación

```bash
curl https://<tu-servicio>.onrender.com/health
# {"ok":true,"protocol":1,"rooms":0,"connections":0,"uptime":12}
```

---

## 5.3 Desarrollo local

```bash
cd server
npm install
npm test           # suite completa: reglas, mazo, juego, red y vectores dorados
npm run dev        # node --watch en el puerto 10000
```

En Unity, apunta `GameManager.serverUrl` a `ws://localhost:10000/ws` (sin TLS en
local) y prueba con **Multiplayer Play Mode** o dos instancias del editor.

> Consejo que ahorra horas: prueba **siempre** como cliente remoto, nunca como
> host. Es la forma más rápida de descubrir que algo sólo funcionaba porque el
> host y el servidor compartían memoria.

---

## 5.4 Configuración del cliente

En el Inspector de `GameManager`:

| Campo | Valor |
|---|---|
| `serverUrl` | `wss://<tu-servicio>.onrender.com/ws` |
| `playerName` | el que elija el usuario |
| `pingIntervalSeconds` | `5` |

`wss://` en producción (Render sirve TLS en el edge). `ws://` sólo en local.

---

## 5.5 Qué hacer cuando el free tier se queda corto

El cuello de botella no es la RAM ni la CPU: es el **spin-down**. Opciones, en
orden de esfuerzo:

### 1. Plan de pago en Render (lo más simple)

Los servicios de pago están siempre encendidos. Un solo cambio en
`render.yaml`: `plan: starter`. Nada más.

### 2. Keep-alive externo (parche para desarrollo)

Un cron que haga `GET /health` cada 10 min evita el spin-down. Sirve para demos;
no lo uses como solución de producción, porque el free tier sigue sin dar
garantías de disponibilidad.

### 3. Redis para escalar a varias instancias

Hoy el estado vive en memoria de un proceso. Para escalar horizontalmente hace
falta mover el registro de salas a Redis:

```
Lobby (memoria) ──► Redis:  room:{code} → { hostId, players[], rules, phase }
                    pub/sub: canal por sala para enrutar eventos entre instancias
```

El diseño ya lo permite sin tocar las reglas: [`Lobby`](../server/src/lobby.js)
es una clase aislada y [`GameServer`](../server/src/session.js) habla con ella a
través de cuatro métodos (`createRoom`, `getRoom`, `findByPlayer`, `deleteRoom`).
Lo que **no** hace falta mover a Redis es el estado de la partida en sí: una sala
vive en una única instancia y el cliente siempre reconecta a la misma mediante el
`reconnectToken` + sticky sessions.

### 4. Elegir región cerca de los jugadores

En un juego por turnos, 150 ms de ping no se notan. Pero sí se notan en la
**carrera del botón ¡UNO!**, que se resuelve por orden de llegada al servidor. Si
tus jugadores están en Latinoamérica, `oregon` o `ohio` darán menos latencia que
`frankfurt`.

---

## 5.6 Observabilidad mínima

El servidor loguea con nivel y timestamp, y `/health` expone el número de salas y
conexiones. Para producción conviene añadir:

- **métrica de salas activas** → para saber si hace falta escalar.
- **contador de errores `ILLEGAL_PLAY`** por conexión → un pico sostenido en una
  sola conexión es un cliente modificado.
- **percentil 95 del RTT** → se calcula en el cliente (`_rttMs`) y se puede
  reportar con el emote de fin de partida.

No hace falta un APM: con esas tres señales se diagnostica prácticamente todo.

---

## 5.7 Checklist antes de publicar

- [ ] `npm test` en verde (63+ tests, incluida la integración con WebSockets reales)
- [ ] `shared/golden-vectors.json` commiteado y actualizado (`npm run vectors`)
- [ ] `dotnet test` del núcleo C# en verde en CI
- [ ] `serverUrl` apuntando a `wss://`
- [ ] Plan de Render sin spin-down
- [ ] Región elegida cerca de los jugadores
- [ ] Probado con **throttling de red** (Network Link Conditioner / Android emulator 3G)
- [ ] Probado cortando el wifi a mitad de partida y recuperando con el token
- [ ] Probado en vertical **y** horizontal en un móvil real
- [ ] Probado el botón ¡UNO! con 300 ms de latencia simulada
