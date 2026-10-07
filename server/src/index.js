/**
 * BOOTSTRAP — servidor HTTP + WebSocket, listo para Render.
 *
 * Restricciones de Render que este archivo respeta:
 *  - Render inyecta `PORT` (documenta 10000 por defecto). Nunca se hardcodea.
 *  - Hay que bindear a 0.0.0.0, no a localhost.
 *  - Los puertos 18012, 18013 y 19099 están reservados: no se usan.
 *  - `/health` responde 200 para el health check del servicio.
 *  - SIGTERM se atiende con cierre limpio de sockets (los deploys no deben
 *    cortar partidas a mitad: se avisa a los clientes para que reconecten).
 *  - Free tier: el servicio duerme a los 15 min sin tráfico. El cliente Unity
 *    debe asumir reconexión con `reconnectToken` (ver session.js).
 */

import http from 'node:http';
import { pathToFileURL } from 'node:url';
import process from 'node:process';
import { WebSocketServer } from 'ws';

import { GameServer } from './session.js';
import { Lobby } from './lobby.js';
import { MAX_MESSAGE_BYTES, PROTOCOL_VERSION } from './protocol.js';

const PORT = Number(process.env.PORT) > 0 ? Number(process.env.PORT) : 10000;
const HOST = process.env.HOST ?? '0.0.0.0';
const TICK_MS = Number(process.env.TICK_MS) > 0 ? Number(process.env.TICK_MS) : 100;
const SWEEP_MS = 60_000;
const RESERVED_RENDER_PORTS = [18012, 18013, 19099];

function logger(level, msg) {
  const line = `${new Date().toISOString()} [${level}] ${msg}`;
  if (level === 'error') console.error(line);
  else console.log(line);
}

export function createServer({ now = () => Date.now(), log = logger } = {}) {
  const lobby = new Lobby({ now });
  const game = new GameServer({ lobby, now, log });

  const httpServer = http.createServer((req, res) => {
    const url = new URL(req.url ?? '/', 'http://localhost');

    if (url.pathname === '/health') {
      const body = JSON.stringify({
        ok: true,
        protocol: PROTOCOL_VERSION,
        rooms: lobby.size,
        connections: game.connections.size,
        uptime: Math.round(process.uptime()),
      });
      res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
      res.end(body);
      return;
    }

    if (url.pathname === '/') {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ name: 'unox-server', protocol: PROTOCOL_VERSION, ws: '/ws' }));
      return;
    }

    res.writeHead(404, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ error: 'NOT_FOUND' }));
  });

  const wss = new WebSocketServer({ noServer: true, maxPayload: MAX_MESSAGE_BYTES });

  httpServer.on('upgrade', (req, socket, head) => {
    const url = new URL(req.url ?? '/', 'http://localhost');
    if (url.pathname !== '/ws') {
      socket.write('HTTP/1.1 404 Not Found\r\n\r\n');
      socket.destroy();
      return;
    }
    wss.handleUpgrade(req, socket, head, (ws) => {
      ws.on('error', (err) => log('error', `ws: ${err.message}`));
      const conn = game.connect((obj) => {
        if (ws.readyState === ws.OPEN) ws.send(JSON.stringify(obj));
      });
      ws.on('message', (data) => conn.handle(data.toString('utf8')));
      ws.on('pong', () => (conn.lastSeen = Date.now()));
      ws.on('close', () => game.disconnect(conn));
    });
  });

  const tickTimer = setInterval(() => game.tick(now()), TICK_MS);
  const pingTimer = setInterval(
    () =>
      game.heartbeat(now(), {
        ping: (conn) => conn.lastPing++,
        terminate: (conn) => {
          log('info', 'latido perdido, cerrando conexión');
          game.disconnect(conn);
        },
      }),
    25_000,
  );
  const sweeper = lobby.startSweeper(SWEEP_MS);

  for (const t of [tickTimer, pingTimer]) if (typeof t.unref === 'function') t.unref();

  function listen(port = PORT, host = HOST) {
    if (RESERVED_RENDER_PORTS.includes(port)) {
      throw new Error(`El puerto ${port} está reservado por Render`);
    }
    return new Promise((resolve, reject) => {
      httpServer.once('error', reject);
      httpServer.listen(port, host, () => {
        const addr = httpServer.address();
        log('info', `UNO-X server escuchando en ${host}:${addr.port} (protocolo v${PROTOCOL_VERSION})`);
        resolve({ server: httpServer, wss, game, lobby, port: addr.port });
      });
    });
  }

  async function close() {
    clearInterval(tickTimer);
    clearInterval(pingTimer);
    lobby.stopSweeper();

    // Avisamos a los clientes para que guarden su reconnectToken y reintenten.
    for (const conn of game.connections) {
      try {
        conn.post({ t: 'room_closed', reason: 'shutdown' });
      } catch {
        /* socket ya cerrado */
      }
    }
    // httpServer.close() espera a que terminen las conexiones existentes, y los
    // sockets ya "upgraded" a WebSocket no se cierran solos: hay que terminarlos.
    for (const client of wss.clients) {
      try {
        client.terminate();
      } catch {
        /* ya cerrado */
      }
    }
    wss.close();
    if (typeof httpServer.closeAllConnections === 'function') httpServer.closeAllConnections();
    await new Promise((r) => httpServer.close(r));
  }

  return { httpServer, wss, game, lobby, listen, close };
}

// Arranque real (no se ejecuta al importar el módulo desde los tests).
const isMain = process.argv[1]
  ? import.meta.url === pathToFileURL(process.argv[1]).href
  : false;
if (isMain) {
  const app = createServer();
  app.listen().catch((err) => {
    logger('error', `no se pudo iniciar: ${err.message}`);
    process.exit(1);
  });
  for (const sig of ['SIGTERM', 'SIGINT']) {
    process.on(sig, async () => {
      logger('info', `${sig} recibido, cerrando…`);
      await app.close();
      process.exit(0);
    });
  }
}
