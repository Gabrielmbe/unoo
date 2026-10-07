// =============================================================================
//  CardDragController.cs  ·  UnoX.UI
//  Drag & Drop unificado para RATÓN (PC) y DEDO (móvil).
//
//  Por qué no usar IBeginDragHandler de Unity UI:
//   - Sólo responde a un puntero y pelea con el scroll del layout.
//   - En PC no da el "levantar la carta" con inercia que se siente bien con ratón.
//   - No permite multitáctil (arrastrar con un dedo y pulsar ¡UNO! con otro), que
//     en este juego es literalmente una mecánica: cantar UNO mientras juegas.
//
//  Este controlador:
//   - Lee Input.GetMouseButton (ratón) y Input.touches (dedos) en el mismo bucle.
//   - Reserva un dedo para el arrastre y deja los demás libres para la UI.
//   - Aplica un umbral de arrastre en mm (no en px): 4 mm en un móvil son muchos
//     más píxeles que en un 4K, y el umbral tiene que sentirse igual.
// =============================================================================

using UnityEngine;
using UnoX.Core;

namespace UnoX.UI
{
    [DisallowMultipleComponent]
    public sealed class CardDragController : MonoBehaviour
    {
        [SerializeField] private CardHandLayout hand;
        [SerializeField] private RectTransform playZone;
        [SerializeField] private RectTransform discardTarget;
        [SerializeField] private Camera uiCamera;
        [SerializeField] private HybridCanvasScaler scaler;

        [Header("Sensación")]
        [Tooltip("Umbral de arrastre en milímetros. Se traduce a px según el DPI.")]
        [SerializeField] private float dragThresholdMm = 4f;
        [SerializeField] private float followLerp = 0.55f;
        [SerializeField] private float tiltPerVelocity = 0.05f;
        [SerializeField] private float maxTilt = 22f;
        [SerializeField] private float scaleWhileDragging = 1.12f;

        /// <summary>Se lanza al soltar la carta sobre la zona de juego.</summary>
        public event System.Action<Card, Vector2> CardDropped;
        /// <summary>Se lanza al levantar una carta (para el sonido y el highlight).</summary>
        public event System.Action<Card> CardPicked;
        /// <summary>Se lanza al soltar fuera de la zona (la carta vuelve a la mano).</summary>
        public event System.Action<Card> CardCancelled;

        private HandSlot _active;
        private Vector2 _grabScreenPoint;
        private Vector2 _grabLocalPoint;
        private Vector2 _lastScreenPoint;
        private Vector2 _screenVelocity;
        private int _pointerId = int.MinValue;
        private bool _dragging;

        private float ThresholdPx
        {
            get
            {
                float dpi = Screen.dpi > 1f ? Screen.dpi : 96f;
                return dragThresholdMm / 25.4f * dpi;
            }
        }

        private void Update()
        {
            if (hand == null) return;

            // ---- entrada de ratón (PC) ----
            if (Input.GetMouseButtonDown(0) && !IsPointerOverUiButton(Input.mousePosition))
                TryBegin(Input.mousePosition, PointerId.Mouse);

            if (Input.GetMouseButton(0) && _pointerId == PointerId.Mouse)
            {
                UpdateDrag(Input.mousePosition);
                if (Input.GetMouseButtonUp(0)) EndDrag(Input.mousePosition);
            }

            // ---- entrada táctil (móvil) ----
            for (int i = 0; i < Input.touchCount; i++)
            {
                var touch = Input.GetTouch(i);
                int id = touch.fingerId;

                if (touch.phase == TouchPhase.Began && _pointerId == int.MinValue &&
                    !IsPointerOverUiButton(touch.position))
                {
                    TryBegin(touch.position, id);
                }
                else if (_pointerId == id)
                {
                    if (touch.phase == TouchPhase.Moved || touch.phase == TouchPhase.Stationary)
                        UpdateDrag(touch.position);
                    else if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
                        EndDrag(touch.position);
                }
            }

            if (_active != null) AnimateDraggedCard();
        }

        private void TryBegin(Vector2 screenPoint, int pointerId)
        {
            var slot = hand.PickAt(screenPoint, uiCamera);
            if (slot == null || !slot.Playable) return;

            _active = slot;
            _pointerId = pointerId;
            _grabScreenPoint = screenPoint;
            _lastScreenPoint = screenPoint;
            _screenVelocity = Vector2.zero;
            _dragging = false;

            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                slot.View.Rect.parent as RectTransform, screenPoint, uiCamera, out _grabLocalPoint);
        }

        private void UpdateDrag(Vector2 screenPoint)
        {
            if (_active == null) return;

            float dt = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
            _screenVelocity = (screenPoint - _lastScreenPoint) / dt;
            _lastScreenPoint = screenPoint;

            if (!_dragging && (screenPoint - _grabScreenPoint).sqrMagnitude > ThresholdPx * ThresholdPx)
            {
                _dragging = true;
                _active.View.IsDragging = true;
                _active.Lifted = true;
                CardPicked?.Invoke(_active.Card);
            }
        }

        private void AnimateDraggedCard()
        {
            if (!_dragging || _active?.View == null) return;

            var rt = _active.View.Rect;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rt.parent as RectTransform, _lastScreenPoint, uiCamera, out var local);

            // Seguimiento con suavizado: el seguimiento 1:1 se siente "rígido".
            rt.anchoredPosition = Vector2.Lerp(rt.anchoredPosition, local, followLerp);
            rt.localScale = Vector3.Lerp(rt.localScale, Vector3.one * scaleWhileDragging, 0.25f);

            // Inclinación según la velocidad del puntero: la carta "se arrastra" por el aire.
            float tilt = Mathf.Clamp(-_screenVelocity.x * tiltPerVelocity * Time.unscaledDeltaTime, -maxTilt, maxTilt);
            rt.localRotation = Quaternion.Euler(0f, 0f, tilt);

            _active.View.SetFlightVelocity(_screenVelocity);
            PlayZoneHighlight(IsOverPlayZone(_lastScreenPoint));
        }

        private void EndDrag(Vector2 screenPoint)
        {
            if (_active == null) return;

            var slot = _active;
            var card = slot.Card;
            _active.View.IsDragging = false;
            slot.Lifted = false;
            _active = null;
            _pointerId = int.MinValue;
            _dragging = false;

            PlayZoneHighlight(false);

            if (IsOverPlayZone(screenPoint))
            {
                slot.View.Rect.localScale = Vector3.one;
                CardDropped?.Invoke(card, screenPoint);
            }
            else
            {
                slot.View.Rect.localScale = Vector3.one;
                slot.Velocity = Vector2.zero;
                CardCancelled?.Invoke(card);
            }
        }

        private bool IsOverPlayZone(Vector2 screenPoint)
        {
            if (playZone == null) return false;
            return RectTransformUtility.RectangleContainsScreenPoint(playZone, screenPoint, uiCamera);
        }

        private void PlayZoneHighlight(bool on)
        {
            if (playZone == null) return;
            var cg = playZone.GetComponent<CanvasGroup>();
            if (cg != null) cg.alpha = on ? 1f : 0.35f;
        }

        /// <summary>
        /// Evita robar el gesto a los botones (¡UNO!, emotes, pasar). Sin esto,
        /// pulsar ¡UNO! con el dedo mientras arrastras se lo comería la mano.
        /// </summary>
        private static bool IsPointerOverUiButton(Vector2 screenPoint)
        {
            if (Application.isMobilePlatform)
            {
                for (int i = 0; i < Input.touchCount; i++)
                    if (Input.GetTouch(i).phase == TouchPhase.Began &&
                        UnityEngine.EventSystems.EventSystem.current != null &&
                        UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject(Input.GetTouch(i).fingerId))
                        return true;
                return false;
            }
            return UnityEngine.EventSystems.EventSystem.current != null &&
                   UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();
        }

        private static class PointerId
        {
            public const int Mouse = int.MinValue + 1;
        }
    }
}
