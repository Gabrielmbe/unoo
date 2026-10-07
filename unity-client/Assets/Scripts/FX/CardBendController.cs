// =============================================================================
//  CardBendController.cs  ·  UnoX.FX
//  Flexión 3D de la carta al volar.
//
//  Una carta de cartón NO se mueve como un quad rígido: al lanzarla se comba por
//  la resistencia del aire y recupera la forma al frenar. Ese detalle es el que
//  hace que "se sienta" una carta y no un sprite.
//
//  Se hace en el VERTEX SHADER (CardBend.shader), no con física:
//   - coste constante, independiente del número de cartas,
//   - funciona igual en PC y en un móvil de gama baja,
//   - no necesita Rigidbody ni colisionadores.
//  El script sólo alimenta dos floats: cuánto se dobla y hacia dónde.
// =============================================================================

using UnityEngine;

namespace UnoX.FX
{
    [DisallowMultipleComponent]
    public sealed class CardBendController : MonoBehaviour
    {
        [SerializeField] private Renderer targetRenderer;
        [SerializeField] private float bendResponse = 9f;
        [SerializeField] private float bendDamping = 6.5f;
        [SerializeField] private float maxBend = 0.55f;
        [SerializeField] private float velocityToBend = 0.0016f;
        [SerializeField] private float kickImpulse = 0.42f;
        [SerializeField] private float shakeImpulse = 0.3f;

        private static readonly int BendAmount = Shader.PropertyToID("_BendAmount");
        private static readonly int BendAxis = Shader.PropertyToID("_BendAxis");

        private MaterialPropertyBlock _mpb;
        private float _bend;
        private float _bendVelocity;
        private Vector2 _axis = Vector2.up;
        private Vector3 _lastPosition;

        private void Awake()
        {
            if (targetRenderer == null) TryGetComponent(out targetRenderer);
            _mpb = new MaterialPropertyBlock();
            _lastPosition = transform.position;
        }

        /// <summary>Flexión proporcional a la velocidad de vuelo.</summary>
        public void SetVelocity(Vector2 velocity)
        {
            float magnitude = velocity.magnitude * velocityToBend;
            _bendVelocity += Mathf.Clamp(magnitude, 0f, maxBend) * Time.unscaledDeltaTime * 60f;
            if (velocity.sqrMagnitude > 0.001f) _axis = velocity.normalized;
        }

        /// <summary>Impulso al soltar la carta: se comba de golpe y recupera.</summary>
        public void Kick()
        {
            _bendVelocity += kickImpulse;
            _axis = Vector2.up;
        }

        /// <summary>Sacudida lateral cuando el servidor rechaza la jugada.</summary>
        public void Shake()
        {
            _bendVelocity += shakeImpulse;
            _axis = Vector2.right;
        }

        private void LateUpdate()
        {
            // Aporte adicional por movimiento real del transform (drag con el dedo).
            Vector3 delta = transform.position - _lastPosition;
            _lastPosition = transform.position;
            if (delta.sqrMagnitude > 0.000001f)
                _bendVelocity += delta.magnitude * bendResponse * 0.35f * Time.unscaledDeltaTime;

            // Resorte amortiguado hacia 0: la carta siempre vuelve a estar plana.
            float accel = -_bend * bendResponse * bendResponse - _bendVelocity * bendDamping * bendResponse;
            _bendVelocity += accel * Time.unscaledDeltaTime;
            _bend = Mathf.Clamp(_bend + _bendVelocity * Time.unscaledDeltaTime, -maxBend, maxBend);

            if (targetRenderer == null) return;
            targetRenderer.GetPropertyBlock(_mpb);
            _mpb.SetFloat(BendAmount, _bend);
            _mpb.SetVector(BendAxis, new Vector4(_axis.x, _axis.y, 0f, 0f));
            targetRenderer.SetPropertyBlock(_mpb);
        }
    }
}
