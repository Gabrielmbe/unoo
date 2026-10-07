// =============================================================================
//  UnoButtonView.cs  ·  UnoX.UI
//  El botón "¡UNO!". Es la mecánica más competitiva del juego y la que más se
//  juega con la latencia en contra, así que está diseñada alrededor de eso:
//
//   1. ENVÍO EN EL MISMO FRAME DEL TOQUE. La animación de pulsado arranca DESPUÉS
//      de mandar el mensaje, no antes. Cada milisegundo de animación previa es
//      desventaja real en la carrera que resuelve el servidor.
//
//   2. PULSO VISIBLE EN AMBOS JUGADORES. El botón late con fuego/electricidad en
//      la pantalla de los dos: el que tiene que cantar y el que puede pillar.
//      Es información de juego, no decoración.
//
//   3. VENTANA CRONOMETRADA CON EL RELOJ DEL SERVIDOR. El countdown usa el offset
//      de reloj estimado, no Time.time, para que la barra se agote en el mismo
//      instante en el PC y en el móvil aunque sus relojes difieran en segundos.
// =============================================================================

using UnityEngine;
using UnityEngine.UI;
using UnoX.Core;

namespace UnoX.UI
{
    public enum UnoButtonMode
    {
        Idle,
        MustCall,     // yo tengo que cantar
        CanCatch,     // el rival tiene que cantar y yo puedo pillarlo
        Locked
    }

    [DisallowMultipleComponent]
    public sealed class UnoButtonView : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private CanvasGroup canvasGroup;
        [SerializeField] private Image fillRing;
        [SerializeField] private Renderer pulseRenderer;
        [SerializeField] private ParticleSystem fireParticles;
        [SerializeField] private ParticleSystem electricParticles;
        [SerializeField] private AudioSource pressSfx;
        [SerializeField] private Transform shakeTarget;

        [Header("Pulso")]
        [SerializeField] private float idlePulseHz = 2.4f;
        [SerializeField] private float urgentPulseHz = 6.5f;
        [SerializeField] private float pulseScale = 0.09f;
        [SerializeField] private float urgencyRamp = 1f;

        [Header("Sacudida al pulsar")]
        [SerializeField] private float pressShake = 9f;
        [SerializeField] private float pressShakeDecay = 14f;

        public UnoButtonMode Mode { get; private set; } = UnoButtonMode.Idle;
        public bool Armed => Mode == UnoButtonMode.MustCall || Mode == UnoButtonMode.CanCatch;

        private long _deadline;
        private long _serverNow;
        private float _phase;
        private float _shake;
        private float _pressAnim;
        private static readonly int Energy = Shader.PropertyToID("_Energy");
        private MaterialPropertyBlock _mpb;

        /// <summary>Se lanza cuando el usuario pulsa. El que escucha es GameManager.</summary>
        public event System.Action Pressed;

        private void Awake()
        {
            if (button != null)
            {
                button.onClick.AddListener(HandleClick);
                button.interactable = false;
            }
            if (canvasGroup != null) canvasGroup.alpha = 0.35f;
        }

        /// <summary>El servidor abre la ventana de UNO.</summary>
        public void Arm(bool isMine, long deadline, long serverNow)
        {
            Mode = isMine ? UnoButtonMode.MustCall : UnoButtonMode.CanCatch;
            _deadline = deadline;
            _serverNow = serverNow;
            if (button != null) button.interactable = true;
            if (canvasGroup != null) canvasGroup.alpha = 1f;
            if (fireParticles != null && isMine) fireParticles.Play();
            if (electricParticles != null && !isMine) electricParticles.Play();
        }

        public void Disarm()
        {
            Mode = UnoButtonMode.Idle;
            if (button != null) button.interactable = false;
            if (canvasGroup != null) canvasGroup.alpha = 0.35f;
            if (fireParticles != null) fireParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            if (electricParticles != null) electricParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            if (fillRing != null) fillRing.fillAmount = 0f;
        }

        private void HandleClick()
        {
            // 1) PRIMERO se manda. Nada de animaciones antes.
            Pressed?.Invoke();

            // 2) DESPUÉS, el espectáculo.
            _pressAnim = 1f;
            _shake = pressShake;
            if (pressSfx != null) pressSfx.Play();
        }

        /// <summary>Lo llama GameManager.Update con deltaTime.</summary>
        public void UpdatePulse(float deltaTime)
        {
            float urgency = 0f;
            if (Armed && _deadline > 0)
            {
                long remaining = _deadline - _serverNow;
                urgency = 1f - Mathf.Clamp01(remaining / 4000f);
                if (fillRing != null) fillRing.fillAmount = Mathf.Clamp01(remaining / 4000f);
            }

            float hz = Mathf.Lerp(idlePulseHz, urgentPulseHz, urgency * urgencyRamp);
            _phase += deltaTime * hz * Mathf.PI * 2f;

            float pulse = 1f + Mathf.Sin(_phase) * pulseScale * (0.4f + urgency);
            transform.localScale = Vector3.one * (pulse + _pressAnim * 0.25f);

            _pressAnim = Mathf.MoveTowards(_pressAnim, 0f, deltaTime * 4f);
            _shake = Mathf.MoveTowards(_shake, 0f, deltaTime * pressShakeDecay * 10f);
            if (shakeTarget != null)
            {
                shakeTarget.localPosition = new Vector3(
                    Random.Range(-_shake, _shake),
                    Random.Range(-_shake, _shake),
                    0f);
            }

            if (pulseRenderer != null)
            {
                if (_mpb == null) _mpb = new MaterialPropertyBlock();
                pulseRenderer.GetPropertyBlock(_mpb);
                _mpb.SetFloat(Energy, Armed ? 0.55f + urgency * 0.45f + _pressAnim * 0.6f : 0.15f);
                pulseRenderer.SetPropertyBlock(_mpb);
            }
        }

        /// <summary>Feedback cuando el rival te pilla sin cantar.</summary>
        public void PlayCaught()
        {
            _shake = pressShake * 1.6f;
            Disarm();
        }
    }
}
