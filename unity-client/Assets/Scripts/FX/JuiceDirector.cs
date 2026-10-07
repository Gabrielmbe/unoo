// =============================================================================
//  JuiceDirector.cs  ·  UnoX.FX
//  Director de efectos. Convierte eventos de red en espectáculo coordinado.
//
//  PRINCIPIOS (lo que separa "efectos bonitos" de "se siente brutal"):
//
//  1. UN SOLO MOMENTO ESTRELLA POR JUGADA. Si todo tiembla a la vez, nada impacta.
//     El director asigna una INTENSIDAD a cada evento y sólo la más alta del frame
//     dispara el shake grande.
//
//  2. HIT-STOP (parón de impacto). Al jugar un +4 se baja Time.timeScale a 0.15
//     durante ~90 ms. El cerebro lo lee como "esto ha sido importante". Es barato
//     y es el truco más rentable de todo el documento.
//
//  3. PRESUPUESTO POR PLATAFORMA. El mismo código corre en PC y móvil, pero el
//     QualityGate recorta partículas, bloom y shake en gama baja. El jugador de
//     móvil ve menos partículas, no menos juego: la INFORMACIÓN (quién roba, de
//     qué color es el turno) nunca se recorta.
//
//  4. NUNCA BLOQUEAR. Los efectos se lanzan con async/await o corutinas; si el
//     servidor tarda, la animación sigue y el siguiente evento se encola.
// =============================================================================

using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnoX.Core;
using UnoX.Net;

namespace UnoX.FX
{
    public enum QualityTier { Low, Medium, High, Ultra }

    [DisallowMultipleComponent]
    public sealed class JuiceDirector : MonoBehaviour
    {
        [Header("Cámara")]
        [SerializeField] private ScreenShakeRig shakeRig;

        [Header("Impacto")]
        [SerializeField] private Volume postFxVolume;
        [SerializeField] private CanvasGroup flashLayer;
        [SerializeField] private Gradient flashGradient;

        [Header("Rayos y estelas")]
        [SerializeField] private LightningBeam lightning;
        [SerializeField] private Transform localAnchor;
        [SerializeField] private Transform[] opponentAnchors;

        [Header("Hit-stop")]
        [SerializeField] private float hitStopScale = 0.15f;
        [SerializeField] private float hitStopSeconds = 0.09f;

        [Header("Presupuesto")]
        [SerializeField] private QualityTier tier = QualityTier.High;

        private readonly Queue<IEnumerator> _queue = new Queue<IEnumerator>();
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private float _flashAmount;
        private Color _flashColor = Color.white;
        private float _shakeBudget;

        public QualityTier Tier => tier;

        private void Awake()
        {
            tier = DetectTier();
        }

        /// <summary>
        /// Detección de gama. No es ciencia exacta, pero evita que un móvil de 2018
        /// intente mover bloom + 6 sistemas de partículas + shake a 30 fps.
        /// </summary>
        private static QualityTier DetectTier()
        {
            if (Application.isEditor) return QualityTier.Ultra;
            int cores = SystemInfo.processorCount;
            long memMb = SystemInfo.systemMemorySize;
            if (SystemInfo.deviceType == DeviceType.Handheld)
            {
                if (memMb < 3500 || cores <= 4) return QualityTier.Low;
                if (memMb < 6000) return QualityTier.Medium;
                return QualityTier.High;
            }
            return memMb >= 16000 ? QualityTier.Ultra : QualityTier.High;
        }

        private void Update()
        {
            if (flashLayer != null && _flashAmount > 0.001f)
            {
                _flashAmount = Mathf.MoveTowards(_flashAmount, 0f, Time.unscaledDeltaTime * 5f);
                flashLayer.alpha = _flashAmount;
                // El flash se tiñe del color del evento: un +4 azul no debe
                // destellar en blanco genérico.
                if (flashLayer.gameObject.activeSelf)
                    SetFlashTint(_flashColor);
            }

            // Se consume el presupuesto de shake cada frame.
            _shakeBudget = Mathf.MoveTowards(_shakeBudget, 0f, Time.unscaledDeltaTime * 3f);
        }

        // ---------------------------------------------------------- eventos --

        public void PlayCardPlayed(int seat, PublicCard? card, bool isLocal)
        {
            float intensity = card.HasValue ? IntensityOf(card.Value.value) : 0.2f;
            AddShake(intensity * (isLocal ? 0.6f : 1f));
            if (card.HasValue && card.Value.value >= CardValue.DrawTwo) Flash(ColorFor(card.Value.color), 0.35f);
        }

        public void PlayCardsDrawn(int seat, int count)
        {
            AddShake(Mathf.Min(0.35f, 0.08f * count));
        }

        /// <summary>
        /// El efecto firma del juego: rayos de luz hacia el jugador penalizado,
        /// destello del color de la carta y hit-stop.
        /// </summary>
        public void PlayDrawPenalty(int victimSeat, int amount, int sourceSeat)
        {
            float intensity = amount >= 4 ? 1f : 0.6f;
            StartCoroutine(DrawPenaltyRoutine(victimSeat, amount, intensity));
        }

        private IEnumerator DrawPenaltyRoutine(int victimSeat, int amount, float intensity)
        {
            HitStop(hitStopSeconds * (amount >= 4 ? 1.6f : 1f));
            AddShake(intensity);

            var victim = AnchorFor(victimSeat);
            var source = AnchorFor(sourceSeat);
            if (victim != null && lightning != null)
            {
                Color c = amount >= 4 ? new Color(0.85f, 0.2f, 1f) : new Color(1f, 0.35f, 0.15f);
                Flash(c, intensity * 0.8f);
                lightning.Fire(source != null ? source.position : Vector3.up * 3f, victim.position,
                    bolts: amount >= 4 ? 5 : 3, duration: 0.55f, color: c);
            }

            yield return new WaitForSecondsRealtime(0.12f);
            AddShake(intensity * 0.5f);
        }

        public void PlayStacked(int seat, int amount)
        {
            HitStop(hitStopSeconds * 1.4f);
            AddShake(Mathf.Min(1f, 0.25f + 0.12f * amount));
            Flash(new Color(1f, 0.75f, 0.1f), 0.5f);
            lightning?.Burst(AnchorFor(seat)?.position ?? Vector3.zero, 6, 0.4f, new Color(1f, 0.8f, 0.2f));
        }

        public void PlayColorBurst(CardColor color)
        {
            Flash(ColorFor(color), 0.45f);
            AddShake(0.3f);
        }

        public void PlayDirectionFlip(int direction)
        {
            AddShake(0.25f);
            StartCoroutine(FlipRoutine(direction));
        }

        private IEnumerator FlipRoutine(int direction)
        {
            // Barrido de cámara: la mesa "gira" para que el cambio de sentido se
            // entienda sin leer texto.
            float t = 0f;
            while (t < 0.35f)
            {
                t += Time.unscaledDeltaTime;
                shakeRig?.AddRoll(direction * Mathf.Sin(t / 0.35f * Mathf.PI) * 2.5f);
                yield return null;
            }
        }

        public void PlayTurnStart(bool isMine)
        {
            if (isMine) AddShake(0.12f);
        }

        public void PlayUnoWindowOpened(bool isMine)
        {
            Flash(isMine ? new Color(1f, 0.85f, 0.2f) : new Color(0.3f, 0.7f, 1f), 0.3f);
        }

        public void PlayUnoPressed()
        {
            HitStop(0.06f);
            AddShake(0.35f);
            Flash(Color.white, 0.5f);
        }

        public void PlayUnoConfirmed(bool isMine)
        {
            Flash(new Color(0.4f, 1f, 0.5f), 0.4f);
            AddShake(0.2f);
        }

        public void PlayUnoCaught(int victimSeat, bool timeout)
        {
            StartCoroutine(UnoCaughtRoutine(victimSeat, timeout));
        }

        private IEnumerator UnoCaughtRoutine(int victimSeat, bool timeout)
        {
            HitStop(0.11f);
            AddShake(0.85f);
            Flash(new Color(1f, 0.15f, 0.2f), 0.7f);
            var victim = AnchorFor(victimSeat);
            if (victim != null && lightning != null)
                lightning.Burst(victim.position, 8, 0.6f, new Color(1f, 0.2f, 0.25f));
            yield return new WaitForSecondsRealtime(0.2f);
            AddShake(0.4f);
        }

        public void PlayUnoCatch() => PlayUnoPressed();

        public void PlayChallengeOffered(bool iAmTarget)
        {
            Flash(new Color(0.9f, 0.3f, 1f), 0.4f);
            AddShake(0.4f);
        }

        public void PlayChallengeResolved(bool bluffed, int culpritSeat)
        {
            HitStop(bluffed ? 0.14f : 0.07f);
            AddShake(bluffed ? 1f : 0.5f);
            Flash(bluffed ? new Color(1f, 0.9f, 0.2f) : new Color(0.5f, 0.6f, 1f), 0.6f);
            var anchor = AnchorFor(culpritSeat);
            if (anchor != null && lightning != null)
                lightning.Burst(anchor.position, bluffed ? 10 : 4, 0.7f,
                    bluffed ? new Color(1f, 0.9f, 0.3f) : new Color(0.5f, 0.6f, 1f));
        }

        public void PlayRoundStart() => Flash(Color.white, 0.25f);
        public void PlayResync() => Flash(new Color(0.4f, 0.8f, 1f), 0.2f);
        public void PlayReconnecting() { }
        public void PlayReconnected() => Flash(new Color(0.4f, 1f, 0.6f), 0.3f);
        public void PlayRejected(string code)
        {
            AddShake(0.3f);
            Flash(new Color(1f, 0.2f, 0.2f), 0.25f);
        }

        public void PlayRoundEnd(bool iWon, int gained)
        {
            HitStop(0.12f);
            AddShake(iWon ? 1f : 0.5f);
            Flash(iWon ? new Color(1f, 0.85f, 0.25f) : new Color(0.4f, 0.5f, 0.7f), 0.7f);
            if (iWon && lightning != null) lightning.Burst(Vector3.zero, 12, 1.2f, new Color(1f, 0.85f, 0.3f));
        }

        public void PlayMatchEnd(bool iWon)
        {
            HitStop(0.18f);
            AddShake(iWon ? 1f : 0.6f);
            Flash(iWon ? Color.white : new Color(0.3f, 0.35f, 0.5f), 0.9f);
        }

        public void PlayEmote(string emoteId)
        {
            // Los emotes son cosméticos: en gama baja se ignoran para no robar
            // presupuesto a los efectos que sí comunican estado de juego.
            if (tier == QualityTier.Low) return;
            AddShake(0.08f);
        }

        // --------------------------------------------------------- primitivas --

        private void AddShake(float intensity)
        {
            // Se suma al presupuesto en vez de lanzar un shake por evento: evita
            // que una cadena de eventos convierta la pantalla en un terremoto.
            _shakeBudget = Mathf.Min(1.2f, _shakeBudget + intensity);
            shakeRig?.AddTrauma(_shakeBudget * 0.55f * TierMultiplier());
        }

        private float TierMultiplier()
        {
            switch (tier)
            {
                case QualityTier.Low: return 0.55f;
                case QualityTier.Medium: return 0.8f;
                case QualityTier.High: return 1f;
                default: return 1.15f;
            }
        }

        private void Flash(Color color, float amount)
        {
            _flashColor = color;
            _flashAmount = Mathf.Max(_flashAmount, Mathf.Clamp01(amount));
            if (flashLayer != null)
            {
                flashLayer.gameObject.SetActive(true);
                SetFlashTint(color);
            }
        }

        private void SetFlashTint(Color color)
        {
            if (flashLayer == null) return;
            var img = flashLayer.GetComponent<UnityEngine.UI.Image>();
            if (img != null) img.color = new Color(color.r, color.g, color.b, 1f);
        }

        /// <summary>
        /// Hit-stop: se baja la escala de tiempo un instante. OJO: se usa
        /// WaitForSecondsRealtime en las rutinas para que el efecto no se
        /// ralentice a sí mismo.
        /// </summary>
        private void HitStop(float seconds)
        {
            if (tier == QualityTier.Low) return; // en gama baja marea
            StartCoroutine(HitStopRoutine(seconds));
        }

        private IEnumerator HitStopRoutine(float seconds)
        {
            float previous = Time.timeScale;
            Time.timeScale = hitStopScale;
            Time.fixedDeltaTime = 0.02f * Time.timeScale;
            yield return new WaitForSecondsRealtime(seconds);
            Time.timeScale = previous;
            Time.fixedDeltaTime = 0.02f;
        }

        private Transform AnchorFor(int seat)
        {
            if (seat < 0) return null;
            if (GameManager.Instance != null && seat == GameManager.Instance.MySeat) return localAnchor;
            if (opponentAnchors != null && seat < opponentAnchors.Length) return opponentAnchors[seat];
            return localAnchor;
        }

        private static float IntensityOf(CardValue value)
        {
            switch (value)
            {
                case CardValue.WildDrawFour: return 1f;
                case CardValue.DrawTwo: return 0.7f;
                case CardValue.Wild: return 0.6f;
                case CardValue.Skip: return 0.4f;
                case CardValue.Reverse: return 0.45f;
                default: return 0.18f;
            }
        }

        private static Color ColorFor(CardColor color)
        {
            switch (color)
            {
                case CardColor.Red: return new Color(1f, 0.18f, 0.22f);
                case CardColor.Yellow: return new Color(1f, 0.82f, 0.15f);
                case CardColor.Green: return new Color(0.2f, 0.9f, 0.35f);
                case CardColor.Blue: return new Color(0.2f, 0.5f, 1f);
                default: return new Color(0.85f, 0.3f, 1f);
            }
        }

        private void OnDisable()
        {
            Time.timeScale = 1f;
            Time.fixedDeltaTime = 0.02f;
        }
    }
}
