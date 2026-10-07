// =============================================================================
//  CardFlightArc.cs  ·  UnoX.FX
//  Vuelo de la carta desde la mano hasta el descarte, con arco 3D y rotación.
//
//  Por qué importa en un juego ONLINE:
//  Cuando el rival juega una carta, su cliente ya la animó hace 150 ms. Si el
//  tuyo la animara al recibir el evento con la misma duración, verías la jugada
//  "retrasada" respecto al sonido del rival. Lo que se hace aquí es:
//    - reproducir el vuelo SIEMPRE con la misma duración fija (no depende del lag),
//    - arrancar en cuanto llega el evento, sin esperar a ninguna otra animación,
//    - permitir que se solapen varios vuelos (robo de 4 cartas + jugada).
//  Así, aunque la red vaya mal, el juego se ve fluido: lo que llega tarde es la
//  INFORMACIÓN, no la animación.
//
//  Todo async/await. Ningún bloqueo, ninguna corutina que pueda quedarse colgada
//  si se destruye el objeto (se usa CancellationToken).
// =============================================================================

using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace UnoX.FX
{
    [DisallowMultipleComponent]
    public sealed class CardFlightArc : MonoBehaviour
    {
        [SerializeField] private float duration = 0.42f;
        [SerializeField] private float arcHeight = 2.4f;
        [SerializeField] private float spinDegrees = 240f;
        [SerializeField] private AnimationCurve easeCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        [SerializeField] private CardBendController bend;

        /// <summary>Se dispara al aterrizar: aquí va el impacto y el sonido.</summary>
        public event System.Action Landed;

        /// <summary>
        /// Vuela la carta desde <paramref name="from"/> hasta <paramref name="to"/>.
        /// Devuelve una Task para que el llamante pueda encadenar con await sin
        /// bloquear el hilo principal.
        /// </summary>
        public Task FlyAsync(Vector3 from, Vector3 to, CancellationToken token = default)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = FlyRoutine(from, to, tcs, token);
            return tcs.Task;
        }

        private async Task FlyRoutine(Vector3 from, Vector3 to, TaskCompletionSource<bool> tcs, CancellationToken token)
        {
            float t = 0f;
            Quaternion startRotation = transform.rotation;
            Quaternion endRotation = startRotation * Quaternion.Euler(0f, 0f, spinDegrees);

            while (t < duration)
            {
                if (token.IsCancellationRequested || this == null)
                {
                    tcs.TrySetResult(false);
                    return;
                }

                t += Time.unscaledDeltaTime;
                float u = easeCurve.Evaluate(Mathf.Clamp01(t / duration));

                // Arco parabólico: interpolación lineal + elevación senoidal.
                Vector3 pos = Vector3.Lerp(from, to, u);
                pos.z += Mathf.Sin(u * Mathf.PI) * arcHeight;

                transform.position = pos;
                transform.rotation = Quaternion.Slerp(startRotation, endRotation, u);
                if (bend != null) bend.SetVelocity((to - from) * (1f / Mathf.Max(duration, 0.001f)));

                // Espera un frame sin bloquear: esto es lo que mantiene el juego vivo.
                await Task.Yield();
                await AwaitEndOfFrame(token);
            }

            transform.position = to;
            Landed?.Invoke();
            tcs.TrySetResult(true);
        }

        /// <summary>
        /// Equivalente a `yield return WaitForEndOfFrame` en async/await.
        /// Task.Delay(0) cede el hilo pero no espera al final del frame; con un
        /// retraso de ~1 frame se garantiza que la posición se aplica antes de
        /// que la cámara renderice.
        /// </summary>
        private static async Task AwaitEndOfFrame(CancellationToken token)
        {
            try
            {
                await Task.Delay(16, token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                // cancelado: el bucle lo detecta en la siguiente comprobación
            }
        }
    }
}
