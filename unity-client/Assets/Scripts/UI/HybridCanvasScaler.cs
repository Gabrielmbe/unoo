// =============================================================================
//  HybridCanvasScaler.cs  ·  UnoX.UI
//  Un solo prefab de UI que sirve en un iPhone SE en vertical, en un tablet en
//  horizontal y en un monitor 4K con ratón.
//
//  LA CLAVE: no escalar por resolución, escalar por ÁREA ÚTIL.
//  Un Canvas Scaler en "Scale With Screen Size" puro hace que las cartas sean
//  enanas en 4K y enormes en un móvil pequeño. Lo que de verdad tiene que ser
//  constante es el TAMAÑO FÍSICO de la carta (mm en pantalla) y el tamaño del
//  objetivo táctil (mínimo ~9-10 mm, ~44 px en iOS / 48 dp en Android).
//
//  Estrategia:
//   1. Match dinámico: 0 en vertical (manda el ancho), 1 en horizontal (manda el
//      alto). Así la mano de cartas siempre ocupa el ancho disponible.
//   2. Tope por DPI: se calcula el factor que hace que una carta mida X mm y se
//      limita el scale entre un mínimo y un máximo para que en 4K no se vea
//      ridículamente pequeño ni en un móvil diminuto se salga.
//   3. Reacción a cambios: orientación, plegado de un foldable, arrastrar la
//      ventana en PC, cambiar de monitor con distinto DPI.
// =============================================================================

using System;
using UnityEngine;
using UnityEngine.UI;

namespace UnoX.UI
{
    [RequireComponent(typeof(Canvas))]
    [RequireComponent(typeof(CanvasScaler))]
    [DisallowMultipleComponent]
    public sealed class HybridCanvasScaler : MonoBehaviour
    {
        [Header("Referencia de diseño")]
        [SerializeField] private Vector2 portraitReference = new Vector2(1080, 1920);
        [SerializeField] private Vector2 landscapeReference = new Vector2(1920, 1080);

        [Header("Tamaño físico objetivo")]
        [Tooltip("Ancho de carta deseado en milímetros. Es lo que se mantiene constante entre dispositivos.")]
        [SerializeField] private float targetCardWidthMm = 34f;
        [Tooltip("Ancho de carta en unidades de referencia del canvas.")]
        [SerializeField] private float cardWidthInReferenceUnits = 120f;

        [Header("Límites de escala")]
        [SerializeField] private float minScale = 0.55f;
        [SerializeField] private float maxScale = 3.2f;
        [Tooltip("Factor extra en pantallas muy grandes para que la UI no parezca de móvil.")]
        [SerializeField] private float largeScreenBias = 0.85f;

        [Header("Objetivos táctiles")]
        [Tooltip("Tamaño mínimo de un botón en mm (accesibilidad).")]
        [SerializeField] private float minTouchTargetMm = 9f;

        private Canvas _canvas;
        private CanvasScaler _scaler;
        private int _lastWidth;
        private int _lastHeight;
        private float _lastDpi;

        /// <summary>Escala aplicada ahora mismo. La leen la mano y los efectos.</summary>
        public float CurrentScale { get; private set; } = 1f;
        public bool IsPortrait { get; private set; }
        public float ScreenDiagonalInches { get; private set; }

        /// <summary>Se dispara al cambiar orientación o resolución.</summary>
        public event Action<bool> OrientationChanged;

        private void Awake()
        {
            _canvas = GetComponent<Canvas>();
            _scaler = GetComponent<CanvasScaler>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            Apply();
        }

        private void Update()
        {
            // Barato: sólo recalcula si de verdad cambió algo.
            if (Screen.width == _lastWidth && Screen.height == _lastHeight &&
                Mathf.Approximately(Screen.dpi, _lastDpi))
                return;
            Apply();
            OrientationChanged?.Invoke(IsPortrait);
        }

        private void Apply()
        {
            _lastWidth = Screen.width;
            _lastHeight = Screen.height;
            _lastDpi = Screen.dpi;

            IsPortrait = Screen.height >= Screen.width;
            var reference = IsPortrait ? portraitReference : landscapeReference;

            _scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _scaler.referenceResolution = reference;
            _scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            _scaler.matchWidthOrHeight = IsPortrait ? 0f : 1f;

            float dpi = Screen.dpi > 1f ? Screen.dpi : 96f;
            ScreenDiagonalInches = Mathf.Sqrt(Screen.width * Screen.width + Screen.height * Screen.height) / dpi;

            // Cuántas pulgadas mide físicamente la carta a escala 1.
            float unitsPerInch = dpi;
            float cardWidthInchesAtScale1 = cardWidthInReferenceUnits / reference.x * Screen.width / unitsPerInch;
            float targetInches = targetCardWidthMm / 25.4f;

            float scale = cardWidthInchesAtScale1 > 0.0001f ? targetInches / cardWidthInchesAtScale1 : 1f;

            // En monitores grandes el cálculo da cartas muy pequeñas; se compensa
            // parcialmente para mantener la estética de "mesa de cartas".
            if (ScreenDiagonalInches > 17f) scale = Mathf.Lerp(scale, 1f, 1f - largeScreenBias);

            CurrentScale = Mathf.Clamp(scale, minScale, maxScale);
            _scaler.scaleFactor = 1f;

            // El Canvas Scaler ya hace el trabajo; guardamos CurrentScale para que
            // CardHandLayout y los FX la usen como factor multiplicativo.
            _canvas.scaleFactor = 1f;
        }

        /// <summary>
        /// Convierte milímetros a píxeles de canvas. Úsalo para dimensionar
        /// cualquier cosa que el usuario toque con el dedo.
        /// </summary>
        public float MmToCanvasUnits(float mm)
        {
            float dpi = Screen.dpi > 1f ? Screen.dpi : 96f;
            float px = mm / 25.4f * dpi;
            var reference = IsPortrait ? portraitReference : landscapeReference;
            float unitsPerPx = reference.x / Mathf.Max(1, Screen.width);
            return px * unitsPerPx;
        }

        /// <summary>True si un rect dado cumple el mínimo táctil accesible.</summary>
        public bool MeetsTouchTarget(RectTransform rect)
        {
            if (rect == null) return false;
            float minUnits = MmToCanvasUnits(minTouchTargetMm);
            return rect.rect.width >= minUnits && rect.rect.height >= minUnits;
        }

        /// <summary>
        /// True si el dispositivo es táctil. En PC con pantalla táctil se activan
        /// ambos caminos: ratón Y dedos, que es lo que espera un usuario de Surface.
        /// </summary>
        public static bool HasTouchSupport =>
            SystemInfo.deviceType == DeviceType.Handheld ||
            Input.touchSupported;
    }
}
