// =============================================================================
//  UnoWebSocketClient.cs  ·  UnoX.Net
//  Transporte WebSocket asíncrono sobre System.Net.WebSockets.ClientWebSocket.
//
//  POR QUÉ NO MIRROR NI PHOTON AQUÍ:
//  UNO es un juego por turnos. Mueve ~2 mensajes por turno, no 30 paquetes por
//  segundo. Lo que de verdad importa es:
//    - validación autoritativa en servidor (anti-hackeo),
//    - reconexión limpia (el móvil pierde cobertura constantemente),
//    - un backend que quepa en el free/lowest tier de Render (512 MB).
//  Un WebSocket propio con JSON cumple las tres cosas y cabe en un servicio de
//  7 $/mes. Mirror/NGO añadirían un runtime de Unity en el servidor, dos builds
//  y una superficie de bugs enorme sin aportar nada a un juego por turnos.
//  (La comunidad que ha hecho juegos de cartas con Mirror coincide en esto.)
//
//  DISEÑO ASÍNCRONO:
//  - El bucle de recepción corre en un Task propio, NUNCA en el hilo principal.
//  - Los mensajes se encolan en una ConcurrentQueue y se drenan en Update(),
//    porque tocar GameObjects desde otro hilo rompe Unity.
//  - Ningún await bloquea el hilo principal: `async Task` de punta a punta.
// =============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace UnoX.Net
{
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting,
        Failed
    }

    /// <summary>
    /// Cliente WebSocket. No conoce las reglas del juego: sólo mueve texto JSON
    /// y notifica estados. Toda la semántica vive en NetworkCardPlayer.
    /// </summary>
    public sealed class UnoWebSocketClient : IDisposable
    {
        private readonly string _url;
        private readonly ConcurrentQueue<string> _incoming = new ConcurrentQueue<string>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private ClientWebSocket _socket;
        private Task _receiveLoop;
        private int _reconnectAttempt;

        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public string LastError { get; private set; }

        /// <summary>Se invoca en el HILO PRINCIPAL, durante DrainIncoming().</summary>
        public event Action<string> OnMessage;
        public event Action<ConnectionState> OnStateChanged;
        public event Action<string> OnRawError;

        public int ReconnectDelayMs { get; set; } = 1000;
        public int MaxReconnectDelayMs { get; set; } = 15000;
        public int MaxReconnectAttempts { get; set; } = 12;

        public UnoWebSocketClient(string url)
        {
            if (string.IsNullOrEmpty(url)) throw new ArgumentException("url vacía", nameof(url));
            // wss:// en producción (Render sirve TLS en el edge). ws:// sólo en local.
            _url = url;
        }

        // ---------------------------------------------------------- conexión --

        /// <summary>Conecta de forma asíncrona. No bloquea el hilo principal.</summary>
        public async Task<bool> ConnectAsync()
        {
            if (State == ConnectionState.Connecting || State == ConnectionState.Connected) return true;

            SetState(ConnectionState.Connecting);
            try
            {
                _socket?.Dispose();
                _socket = new ClientWebSocket();
                // El keep-alive del protocolo WS evita que los proxies intermedios
                // corten la conexión en los momentos de "nadie juega nada".
                _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

                await _socket.ConnectAsync(new Uri(_url), _cts.Token).ConfigureAwait(false);
                _reconnectAttempt = 0;
                SetState(ConnectionState.Connected);
                _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                OnRawError?.Invoke(ex.Message);
                SetState(ConnectionState.Failed);
                return false;
            }
        }

        private void SetState(ConnectionState next)
        {
            if (State == next) return;
            State = next;
            // El callback se lanza en el hilo principal vía la cola de drenado.
            _pendingState = next;
        }

        private ConnectionState? _pendingState;

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            var buffer = new byte[16 * 1024];
            var builder = new StringBuilder();

            try
            {
                while (!token.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    builder.Clear();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), token)
                            .ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", token)
                                .ConfigureAwait(false);
                            _incoming.Enqueue("__CLOSED__");
                            return;
                        }
                        builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    } while (!result.EndOfMessage);

                    _incoming.Enqueue(builder.ToString());
                }
            }
            catch (OperationCanceledException)
            {
                // cierre limpio solicitado
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _incoming.Enqueue("__CLOSED__");
            }
        }

        // ------------------------------------------------------------- envío --

        /// <summary>
        /// Envía sin esperar confirmación. Fire-and-forget deliberado: en un juego
        /// por turnos, esperar el ACK antes de dejar jugar al usuario sería lo que
        /// provocaría exactamente la sensación de congelación que queremos evitar.
        /// </summary>
        public void Send(string json)
        {
            if (_socket == null || _socket.State != WebSocketState.Open)
            {
                Debug.LogWarning("[UNO-X] Send ignorado: socket cerrado");
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(json);
            _ = SendInternalAsync(bytes);
        }

        private async Task SendInternalAsync(byte[] bytes)
        {
            try
            {
                await _socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _incoming.Enqueue("__CLOSED__");
            }
        }

        // ---------------------------------------------------------- drenado --

        /// <summary>
        /// Drena la cola de mensajes en el hilo principal. Llamar desde Update().
        /// Con tope por frame para que un aluvión de eventos no congele un frame.
        /// </summary>
        public void DrainIncoming(int maxPerFrame = 32)
        {
            if (_pendingState.HasValue)
            {
                var s = _pendingState.Value;
                _pendingState = null;
                OnStateChanged?.Invoke(s);
            }

            int processed = 0;
            while (processed < maxPerFrame && _incoming.TryDequeue(out var raw))
            {
                processed++;
                if (raw == "__CLOSED__")
                {
                    HandleClosed();
                    continue;
                }
                OnMessage?.Invoke(raw);
            }
        }

        private void HandleClosed()
        {
            if (State == ConnectionState.Disconnected) return;
            SetState(ConnectionState.Reconnecting);
            _ = ReconnectLoopAsync();
        }

        private async Task ReconnectLoopAsync()
        {
            while (_reconnectAttempt < MaxReconnectAttempts && !_cts.IsCancellationRequested)
            {
                _reconnectAttempt++;
                // Backoff exponencial con tope: no machacar un servidor que se está
                // despertando del spin-down de Render (tarda ~1 min en arrancar).
                int delay = Mathf.Min(MaxReconnectDelayMs, ReconnectDelayMs * (1 << Math.Min(_reconnectAttempt - 1, 5)));
                try
                {
                    await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                var ok = await ConnectAsync().ConfigureAwait(false);
                if (ok) return;
            }
            SetState(ConnectionState.Failed);
        }

        // ----------------------------------------------------------- cierre --

        public void Dispose()
        {
            try
            {
                _cts.Cancel();
                if (_socket != null && _socket.State == WebSocketState.Open)
                {
                    _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "dispose", CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
            }
            catch
            {
                // Dispose nunca debe lanzar
            }
            finally
            {
                _socket?.Dispose();
                _cts.Dispose();
            }
        }
    }
}
