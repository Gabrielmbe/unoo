// =============================================================================
//  ScreenShakeRig.cs  ·  UnoX.FX
//  Sacudida de cámara por "trauma" (técnica de Vlambeer / Game Feel de Swink).
//
//  Por qué trauma y no animaciones sueltas:
//   - Los shaks se ACUMULAN. Un +4 seguido de un UNO pillado no reinicia el
//     anterior: se suma y decae. Es lo que hace que una racha se sienta como tal.
//   - El decaimiento es cuadrático, así que el golpe fuerte cae rápido y la cola
//     es suave. Un decaimiento lineal se siente mecánico.
//   - Se usa ruido Perlin, no Random: el Random da un temblor "digital" y
//     molesto; el ruido da un movimiento orgánico que no marea.
//
//  Sin dependencia de Cinemachine: funciona con una Camera normal y añade
//  Cinemachine Impulse si está presente (opcional, por si ya lo usas).
// =============================================================================

using UnityEngine;

namespace UnoX.FX
{
    [DisallowMultipleComponent]
    public sealed class ScreenShakeRig : MonoBehaviour
    {
        [SerializeField] private Transform cameraTransform;
        [SerializeField] private bool affectRotation = true;

        [Header("Respuesta")]
        [Tooltip("Cuánto trauma añade cada punto de intensidad.")]
        [SerializeField] private float traumaGain = 0.9f;
        [Tooltip("Velocidad de decaimiento por segundo.")]
        [SerializeField] private float traumaDecay = 1.7f;
        [SerializeField] private float maxDisplacement = 0.55f;
        [SerializeField] private float maxRotationDegrees = 3.5f;
        [SerializeField] private float noiseFrequency = 17f;
        [SerializeField] private float rollAmount = 0f;

        private float _trauma;
        private float _seedX;
        private float _seedY;
        private float _seedZ;
        private Vector3 _basePosition;
        private Quaternion _baseRotation;
        private bool _captured;
        private float _roll;

        private void Awake()
        {
            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
            _seedX = Random.value * 100f;
            _seedY = Random.value * 100f;
            _seedZ = Random.value * 100f;
        }

        private void CaptureBase()
        {
            if (_captured || cameraTransform == null) return;
            _basePosition = cameraTransform.localPosition;
            _baseRotation = cameraTransform.localRotation;
            _captured = true;
        }

        /// <summary>Añade trauma. Llamar desde JuiceDirector, no desde los efectos sueltos.</summary>
        public void AddTrauma(float amount)
        {
            _trauma = Mathf.Clamp01(_trauma + amount * traumaGain);
            CaptureBase();
        }

        /// <summary>Barrido de cámara (cambio de sentido, victoria).</summary>
        public void AddRoll(float degrees)
        {
            _roll += degrees;
            CaptureBase();
        }

        private void LateUpdate()
        {
            if (cameraTransform == null) return;
            CaptureBase();

            if (_trauma <= 0.0001f && Mathf.Abs(_roll) < 0.001f)
            {
                cameraTransform.localPosition = _basePosition;
                cameraTransform.localRotation = _baseRotation;
                return;
            }

            // Decaimiento cuadrático: el impacto cae rápido, la cola es suave.
            float shake = _trauma * _trauma;
            _trauma = Mathf.Max(0f, _trauma - traumaDecay * Time.unscaledDeltaTime);
            _roll = Mathf.Lerp(_roll, 0f, Time.unscaledDeltaTime * 6f);

            float t = Time.unscaledTime * noiseFrequency;
            float x = (Mathf.PerlinNoise(_seedX, t) * 2f - 1f) * maxDisplacement * shake;
            float y = (Mathf.PerlinNoise(_seedY, t) * 2f - 1f) * maxDisplacement * shake;
            float z = (Mathf.PerlinNoise(_seedZ, t) * 2f - 1f) * maxRotationDegrees * shake;

            cameraTransform.localPosition = _basePosition + new Vector3(x, y, 0f);

            if (affectRotation)
            {
                cameraTransform.localRotation = _baseRotation * Quaternion.Euler(0f, 0f, z + _roll);
            }
            else
            {
                cameraTransform.localRotation = _baseRotation * Quaternion.Euler(0f, 0f, _roll);
            }
        }

        /// <summary>Corte limpio: útil al cambiar de escena para no arrastrar trauma.</summary>
        public void Reset()
        {
            _trauma = 0f;
            _roll = 0f;
            if (_captured && cameraTransform != null)
            {
                cameraTransform.localPosition = _basePosition;
                cameraTransform.localRotation = _baseRotation;
            }
        }
    }
}
