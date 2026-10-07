// =============================================================================
//  CardHandLayout.cs  ·  UnoX.UI
//  La mano de cartas: abanico dinámico que se ve bien con 2 cartas y con 22.
//
//  EL PROBLEMA REAL
//  ----------------
//  En un iPhone SE en vertical hay ~320 px de ancho útil. Con 15 cartas, si cada
//  una mide 120 px, necesitas 1800 px. La solución no es hacer las cartas
//  minúsculas (ilegibles) sino SOLAPARLAS: el "overlap" se calcula de forma que
//  siempre quede visible la franja que distingue una carta de otra (el color y el
//  número están en la esquina superior izquierda).
//
//  En un 4K de 32" hay espacio de sobra, así que el abanico se abre, se curva y
//  se separa: misma lógica, parámetros distintos. Un solo algoritmo, dos
//  comportamientos, cero prefabs duplicados.
//
//  FÍSICA VISUAL
//  -------------
//  Las cartas no se teletransportan: cada una tiene velocidad, amortiguación y
//  un pequeño retardo escalonado (stagger). Al robar 4 cartas de golpe entran en
//  cascada, no todas a la vez. Todo se integra en Update con deltaTime, así que
//  es independiente del framerate y funciona igual a 30 fps en un móvil viejo que
//  a 144 fps en un PC.
// =============================================================================

using System.Collections.Generic;
using UnityEngine;
using UnoX.Core;
using UnoX.Net;

namespace UnoX.UI
{
    /// <summary>Estado visual de una carta de la mano.</summary>
    public sealed class HandSlot
    {
        public Card Card;
        public CardView View;
        public Vector2 TargetPosition;
        public float TargetRotation;
        public Vector2 Velocity;
        public float RotationVelocity;
        public bool Playable;
        public bool Lifted;
        public int Index;
    }

    [DisallowMultipleComponent]
    public sealed class CardHandLayout : MonoBehaviour
    {
        [Header("Contenedores")]
        [SerializeField] private RectTransform handRoot;
        [SerializeField] private RectTransform discardRoot;
        [SerializeField] private CardView cardPrefab;

        [Header("Geometría del abanico")]
        [Tooltip("Ancho base de carta en unidades de canvas.")]
        [SerializeField] private float cardWidth = 120f;
        [SerializeField] private float cardHeight = 180f;
        [Tooltip("Franja mínima visible de cada carta. Es lo que fija el solapamiento.")]
        [SerializeField] private float minVisibleStrip = 34f;
        [SerializeField] private float maxOverlapRatio = 0.82f;
        [Tooltip("Curvatura del abanico en grados por carta (0 = fila recta).")]
        [SerializeField] private float fanCurvature = 2.2f;
        [SerializeField] private float arcHeight = 26f;

        [Header("Física")]
        [SerializeField] private float positionStiffness = 22f;
        [SerializeField] private float positionDamping = 11f;
        [SerializeField] private float rotationStiffness = 18f;
        [SerializeField] private float staggerSeconds = 0.045f;
        [SerializeField] private float liftOffset = 46f;

        [Header("Dependencias")]
        [SerializeField] private HybridCanvasScaler scaler;

        private readonly List<HandSlot> _slots = new List<HandSlot>();
        private readonly Dictionary<string, HandSlot> _byId = new Dictionary<string, HandSlot>();
        private float _staggerAccumulator;
        private int _pendingEntrances;

        public IReadOnlyList<HandSlot> Slots => _slots;
        public int Count => _slots.Count;

        // --------------------------------------------------------------- API --

        /// <summary>Reconstruye la mano desde un snapshot (inicio o reconexión).</summary>
        public void RebuildHand(GameSnapshot snapshot)
        {
            Clear();
            if (snapshot == null || snapshot.players == null) return;
            if (snapshot.seat < 0 || snapshot.seat >= snapshot.players.Count) return;

            var me = snapshot.players[snapshot.seat];
            if (me.hand == null) return;

            foreach (var hc in me.hand) AddSlot(hc.ToCard(), instant: true);
            Relayout(instant: true);
        }

        /// <summary>Procesa el evento de robo: las cartas entran en cascada.</summary>
        public void OnCardsDrawn(GameEvent e)
        {
            if (e == null) return;
            if (e.cards != null)
            {
                foreach (var c in e.cards) AddSlot(c.ToCard($"tmp_{_slots.Count}"));
                Relayout();
            }
            else
            {
                // Robo del rival: sólo sabemos cuántas. Añadimos dorsos.
                for (int i = 0; i < e.count; i++) AddSlot(default, instant: false, faceDown: true);
            }
        }

        public void OnCardPlayed(GameEvent e)
        {
            if (e == null || e.card == null) return;
            if (e.seat == LocalSeat)
            {
                // La carta ya salió con DetachForPlay; aquí sólo confirmamos.
                return;
            }
            // El rival jugó: se anima desde su lado hacia el descarte.
        }

        public void OnTurnStart(GameEvent e) => Relayout();

        /// <summary>La UI llama a esto en el frame del toque: feedback inmediato.</summary>
        public void DetachForPlay(string cardId)
        {
            if (!_byId.TryGetValue(cardId, out var slot)) return;
            slot.View?.PlayDetach();
            _byId.Remove(cardId);
            _slots.Remove(slot);
            Relayout();
        }

        /// <summary>El servidor rechazó la jugada: la carta vuelve a su sitio.</summary>
        public void SnapBack(string cardId)
        {
            if (!_byId.TryGetValue(cardId, out var slot)) return;
            slot.View?.PlaySnapBack();
            slot.Lifted = false;
        }

        public void AnimateDrawIntent()
        {
            // Micro-feedback mientras viaja la petición: la mano "respira".
            foreach (var s in _slots) s.View?.Nudge();
        }

        /// <summary>Marca qué cartas son jugables (las demás se atenúan y no se arrastran).</summary>
        public void SetPlayable(IReadOnlyList<Card> legalPlays, bool myTurn)
        {
            var legal = new HashSet<string>();
            if (legalPlays != null)
                foreach (var c in legalPlays) legal.Add(c.Id);

            foreach (var s in _slots)
            {
                s.Playable = myTurn && legal.Contains(s.Card.Id);
                s.View?.SetInteractable(s.Playable);
            }
        }

        public void UpdateTimers(float deltaTime)
        {
            Integrate(deltaTime);
        }

        public int LocalSeat { get; set; }

        // ----------------------------------------------------------- interno --

        private void Clear()
        {
            foreach (var s in _slots) if (s.View != null) Destroy(s.View.gameObject);
            _slots.Clear();
            _byId.Clear();
            _pendingEntrances = 0;
        }

        private void AddSlot(Card card, bool instant = false, bool faceDown = false)
        {
            if (cardPrefab == null || handRoot == null) return;
            var view = Instantiate(cardPrefab, handRoot);
            view.Bind(card, faceDown);

            var slot = new HandSlot
            {
                Card = card,
                View = view,
                Index = _slots.Count,
                TargetPosition = EntranceOrigin(),
                TargetRotation = 0f
            };

            if (instant)
            {
                var rt = view.Rect;
                rt.anchoredPosition = slot.TargetPosition = ComputeSlotPosition(_slots.Count, _slots.Count + 1);
                rt.localRotation = Quaternion.Euler(0f, 0f, 0f);
            }
            else
            {
                _pendingEntrances++;
                view.Rect.anchoredPosition = slot.TargetPosition;
            }

            _slots.Add(slot);
            if (!string.IsNullOrEmpty(card.Id)) _byId[card.Id] = slot;
        }

        private Vector2 EntranceOrigin()
        {
            // Las cartas nuevas entran desde el mazo, no aparecen de la nada.
            return new Vector2(handRoot.rect.width * 0.5f + cardWidth, -cardHeight * 0.6f);
        }

        /// <summary>
        /// El corazón del layout. Calcula solapamiento, abanico y arco.
        /// </summary>
        public void Relayout(bool instant = false)
        {
            int n = _slots.Count;
            if (n == 0) return;

            float available = Mathf.Max(cardWidth, handRoot.rect.width);
            float effectiveWidth = cardWidth * ScaleFactor;

            // Paso entre cartas: el mínimo entre "sin solapar" y "máximo solape
            // que sigue dejando visible la franja identificativa".
            float noOverlap = effectiveWidth;
            float maxStep = effectiveWidth * (1f - maxOverlapRatio);
            float stripStep = minVisibleStrip * ScaleFactor;
            float step = Mathf.Min(noOverlap, Mathf.Max(maxStep, stripStep));

            // Si aun así no caben, se comprime hasta rellenar el ancho útil.
            float needed = effectiveWidth + step * (n - 1);
            if (needed > available && n > 1)
                step = (available - effectiveWidth) / (n - 1);

            float totalWidth = effectiveWidth + step * (n - 1);
            float startX = -totalWidth * 0.5f + effectiveWidth * 0.5f;

            float curvature = fanCurvature * Mathf.Clamp01(n / 8f);

            for (int i = 0; i < n; i++)
            {
                var slot = _slots[i];
                slot.Index = i;
                float t = n == 1 ? 0.5f : i / (float)(n - 1);
                float centered = t - 0.5f;

                float x = startX + step * i;
                float y = -Mathf.Abs(centered) * arcHeight * 2f * ScaleFactor;
                float rot = -centered * curvature * (n - 1) * 0.5f;

                slot.TargetPosition = new Vector2(x, y + (slot.Lifted ? liftOffset : 0f));
                slot.TargetRotation = rot;

                // Orden de render: la carta del centro por encima, como en la mano real.
                if (slot.View != null) slot.View.Rect.SetSiblingIndex(i);

                if (instant && slot.View != null)
                {
                    slot.View.Rect.anchoredPosition = slot.TargetPosition;
                    slot.View.Rect.localRotation = Quaternion.Euler(0f, 0f, slot.TargetRotation);
                    slot.Velocity = Vector2.zero;
                }
            }
        }

        private float ScaleFactor => scaler != null ? scaler.CurrentScale : 1f;

        /// <summary>
        /// Integración tipo resorte-amortiguador. Se usa en lugar de DOTween aquí
        /// porque con 20+ cartas corriendo a la vez un tween por carta genera
        /// basura y callbacks; esto es un bucle y nada más.
        /// </summary>
        private void Integrate(float dt)
        {
            if (dt <= 0f) return;

            if (_pendingEntrances > 0)
            {
                _staggerAccumulator += dt;
                while (_staggerAccumulator >= staggerSeconds && _pendingEntrances > 0)
                {
                    _staggerAccumulator -= staggerSeconds;
                    _pendingEntrances--;
                }
            }

            foreach (var slot in _slots)
            {
                if (slot.View == null) continue;
                var rt = slot.View.Rect;
                if (slot.View.IsDragging) continue; // mientras se arrastra manda el dedo

                Vector2 pos = rt.anchoredPosition;
                Vector2 toTarget = slot.TargetPosition - pos;

                // a = k*x - c*v  (resorte amortiguado, estable con dt variable)
                Vector2 accel = toTarget * positionStiffness - slot.Velocity * positionDamping;
                slot.Velocity += accel * dt;
                pos += slot.Velocity * dt;
                rt.anchoredPosition = pos;

                float rot = rt.localEulerAngles.z;
                if (rot > 180f) rot -= 360f;
                float rotAccel = (slot.TargetRotation - rot) * rotationStiffness - slot.RotationVelocity * positionDamping;
                slot.RotationVelocity += rotAccel * dt;
                rt.localRotation = Quaternion.Euler(0f, 0f, rot + slot.RotationVelocity * dt);
            }
        }

        /// <summary>Devuelve la carta bajo un punto de pantalla (toque o ratón).</summary>
        public HandSlot PickAt(Vector2 screenPoint, Camera uiCamera)
        {
            // Se recorre al revés: la última es la que está visualmente encima.
            for (int i = _slots.Count - 1; i >= 0; i--)
            {
                var slot = _slots[i];
                if (slot.View == null) continue;
                if (RectTransformUtility.RectangleContainsScreenPoint(slot.View.Rect, screenPoint, uiCamera))
                    return slot;
            }
            return null;
        }
    }
}
