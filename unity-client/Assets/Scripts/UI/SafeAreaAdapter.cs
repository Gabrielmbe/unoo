// =============================================================================
//  SafeAreaAdapter.cs  ·  UnoX.UI
//  Recorta la UI a la zona segura: notch de iPhone, agujero de cámara Android,
//  barra de navegación por gestos, Dynamic Island.
//
//  En un juego de cartas esto NO es cosmético: si la mano de cartas queda bajo el
//  notch, el jugador no puede arrastrar su última carta y pierde la partida.
// =============================================================================

using UnityEngine;

namespace UnoX.UI
{
    [RequireComponent(typeof(RectTransform))]
    [DisallowMultipleComponent]
    public sealed class SafeAreaAdapter : MonoBehaviour
    {
        [SerializeField] private bool applyTop = true;
        [SerializeField] private bool applyBottom = true;
        [SerializeField] private bool applyLeft = true;
        [SerializeField] private bool applyRight = true;

        [Tooltip("Margen extra en píxeles, por si el fabricante reporta mal la zona segura.")]
        [SerializeField] private float paddingPx = 0f;

        private RectTransform _rt;
        private Rect _lastSafe;
        private Vector2 _lastResolution;
        private ScreenOrientation _lastOrientation;

        private void Awake() => _rt = (RectTransform)transform;

        private void OnEnable() => Apply();

        private void Update()
        {
            var safe = Screen.safeArea;
            if (safe == _lastSafe &&
                Screen.orientation == _lastOrientation &&
                (Vector2)new Vector2(Screen.width, Screen.height) == _lastResolution)
                return;
            Apply();
        }

        private void Apply()
        {
            var safe = Screen.safeArea;
            _lastSafe = safe;
            _lastOrientation = Screen.orientation;
            _lastResolution = new Vector2(Screen.width, Screen.height);

            int w = Mathf.Max(1, Screen.width);
            int h = Mathf.Max(1, Screen.height);

            var anchorMin = new Vector2(safe.x / w, safe.y / h);
            var anchorMax = new Vector2((safe.x + safe.width) / w, (safe.y + safe.height) / h);

            if (!applyLeft) anchorMin.x = 0f;
            if (!applyRight) anchorMax.x = 1f;
            if (!applyBottom) anchorMin.y = 0f;
            if (!applyTop) anchorMax.y = 1f;

            _rt.anchorMin = anchorMin;
            _rt.anchorMax = anchorMax;
            _rt.offsetMin = new Vector2(paddingPx, paddingPx);
            _rt.offsetMax = new Vector2(-paddingPx, -paddingPx);
        }
    }
}
