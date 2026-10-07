// =============================================================================
//  CardView.cs  ·  UnoX.UI
//  La vista de UNA carta. Prefab con:
//    - MeshRenderer/Quad con el shader CardBend (flexión 3D al volar),
//    - cara frontal (sprite de la carta) y dorso,
//    - outline de "jugable",
//    - RibbonTrail para la estela.
//
//  Optimización móvil: las cartas usan un SOLO atlas. Cambiar de sprite dentro
//  del atlas no rompe las draw calls; cambiar de material sí. Por eso el brillo
//  de "jugable" va por propiedad de material con MaterialPropertyBlock, no por
//  un material distinto por carta.
// =============================================================================

using UnityEngine;
using UnityEngine.UI;
using UnoX.Core;
using UnoX.FX;

namespace UnoX.UI
{
    [DisallowMultipleComponent]
    public sealed class CardView : MonoBehaviour
    {
        [SerializeField] private RectTransform rect;
        [SerializeField] private Image faceImage;
        [SerializeField] private GameObject faceDownRoot;
        [SerializeField] private Image playableOutline;
        [SerializeField] private CardBendController bend;
        [SerializeField] private RibbonTrail trail;
        [SerializeField] private CanvasGroup canvasGroup;

        [Header("Feedback")]
        [SerializeField] private float dimAlpha = 0.42f;
        [SerializeField] private float nudgeAmount = 6f;

        public RectTransform Rect => rect != null ? rect : (RectTransform)transform;
        public Card Card { get; private set; }
        public bool IsDragging { get; set; }
        public bool FaceDown { get; private set; }

        private static readonly int HighlightAmount = Shader.PropertyToID("_Highlight");
        private MaterialPropertyBlock _mpb;
        private Renderer _renderer;

        public void Bind(Card card, bool faceDown)
        {
            Card = card;
            FaceDown = faceDown;
            if (faceDownRoot != null) faceDownRoot.SetActive(faceDown);
            if (faceImage != null)
            {
                faceImage.gameObject.SetActive(!faceDown);
                faceImage.sprite = CardArt.GetSprite(card);
            }
            SetInteractable(false);
        }

        public void SetInteractable(bool playable)
        {
            if (playableOutline != null) playableOutline.enabled = playable;
            if (canvasGroup != null) canvasGroup.alpha = playable ? 1f : dimAlpha;
            if (_mpb == null && TryGetComponent(out _renderer)) _mpb = new MaterialPropertyBlock();
            if (_mpb != null && _renderer != null)
            {
                _renderer.GetPropertyBlock(_mpb);
                _mpb.SetFloat(HighlightAmount, playable ? 1f : 0f);
                _renderer.SetPropertyBlock(_mpb);
            }
        }

        /// <summary>La carta sale volando hacia el descarte.</summary>
        public void PlayDetach()
        {
            if (bend != null) bend.Kick();
            if (trail != null) trail.Begin();
        }

        /// <summary>El servidor rechazó: la carta vuelve con un latigazo.</summary>
        public void PlaySnapBack()
        {
            if (bend != null) bend.Shake();
            if (trail != null) trail.End();
        }

        /// <summary>Micro-movimiento mientras se espera la respuesta del servidor.</summary>
        public void Nudge()
        {
            Rect.anchoredPosition += Vector2.up * nudgeAmount;
        }

        /// <summary>Flexión proporcional a la velocidad: cuanto más rápido, más doblada.</summary>
        public void SetFlightVelocity(Vector2 velocity)
        {
            if (bend != null) bend.SetVelocity(velocity);
        }

        private void OnDisable()
        {
            if (trail != null) trail.End();
        }
    }

    /// <summary>
    /// Mapa carta -&gt; sprite del atlas. Se resuelve una vez y se cachea: hacerlo
    /// por frame con Resources.Load sería un desastre en móvil.
    /// </summary>
    public static class CardArt
    {
        private static Sprite[] _cache;

        public static Sprite GetSprite(Card card)
        {
            if (_cache == null) Build();
            int index = ToIndex(card);
            if (index < 0 || index >= _cache.Length) return null;
            return _cache[index];
        }

        /// <summary>54 caras únicas: 4 colores × 13 valores + 2 comodines.</summary>
        private static int ToIndex(Card card)
        {
            if (card.Value == CardValue.Wild) return 52;
            if (card.Value == CardValue.WildDrawFour) return 53;
            return (int)card.Color * 13 + (int)card.Value;
        }

        private static void Build()
        {
            // En el proyecto real esto carga el atlas por dirección de assets y lo
            // cachea. Se deja como punto de integración para no acoplar el código
            // a una ruta concreta.
            _cache = new Sprite[54];
        }
    }
}
