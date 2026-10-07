// =============================================================================
//  RibbonTrail.cs  ·  UnoX.FX
//  Estela brillante que sigue a la carta.
//
//  Por qué no el TrailRenderer de Unity:
//   - TrailRenderer genera geometría en el hilo principal y la reconstruye entera
//     cuando cambia el número de puntos. Con 4 cartas volando a la vez (un +4) se
//     nota el tirón.
//   - No permite control fino del ancho por edad del punto.
//  Esto es un mesh propio con un buffer de longitud fija: CERO asignaciones en
//  régimen estable, que es lo que se necesita para 60 fps sostenidos en móvil.
// =============================================================================

using UnityEngine;

namespace UnoX.FX
{
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    [DisallowMultipleComponent]
    public sealed class RibbonTrail : MonoBehaviour
    {
        [SerializeField] private int maxPoints = 26;
        [SerializeField] private float lifetime = 0.42f;
        [SerializeField] private float startWidth = 0.22f;
        [SerializeField] private float endWidth = 0.0f;
        [SerializeField] private Material ribbonMaterial;
        [SerializeField] private Gradient colorGradient;
        [SerializeField] private float minDistanceBetweenPoints = 0.02f;

        private Mesh _mesh;
        private Vector3[] _positions;
        private float[] _ages;
        private int[] _triangles;
        private Vector3[] _vertices;
        private Vector2[] _uvs;
        private Color[] _colors;
        private int _count;
        private bool _emitting;
        private Vector3 _lastEmit;

        private void Awake()
        {
            _mesh = new Mesh { name = "RibbonTrail" };
            _mesh.MarkDynamic();
            GetComponent<MeshFilter>().sharedMesh = _mesh;

            var mr = GetComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            if (ribbonMaterial != null) mr.sharedMaterial = ribbonMaterial;

            // Dos vértices por punto del ribbon.
            _vertices = new Vector3[maxPoints * 2];
            _uvs = new Vector2[maxPoints * 2];
            _colors = new Color[maxPoints * 2];
            _triangles = new int[Mathf.Max(0, maxPoints - 1) * 6];
            _positions = new Vector3[maxPoints];
            _ages = new float[maxPoints];

            if (colorGradient == null)
            {
                colorGradient = new Gradient();
                colorGradient.SetKeys(
                    new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(0.6f, 0.8f, 1f), 1f) },
                    new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            }

            _mesh.vertices = _vertices;
            _mesh.triangles = _triangles;
            SetActive(false);
        }

        public void Begin()
        {
            _emitting = true;
            _count = 0;
            _lastEmit = transform.position;
            SetActive(true);
        }

        public void End() => _emitting = false;

        private void SetActive(bool on)
        {
            var mr = GetComponent<MeshRenderer>();
            if (mr != null) mr.enabled = on;
        }

        private void LateUpdate()
        {
            float dt = Time.unscaledDeltaTime;

            if (_emitting && (transform.position - _lastEmit).sqrMagnitude > minDistanceBetweenPoints * minDistanceBetweenPoints)
            {
                AddPoint(transform.position);
                _lastEmit = transform.position;
            }

            if (_count == 0)
            {
                if (!_emitting) SetActive(false);
                return;
            }

            AgePoints(dt);
            RebuildMesh();
        }

        private void AddPoint(Vector3 p)
        {
            if (_count < maxPoints)
            {
                _positions[_count] = p;
                _ages[_count] = 0f;
                _count++;
                return;
            }
            // Buffer circular: se desplaza una posición en vez de allocar.
            System.Array.Copy(_positions, 1, _positions, 0, maxPoints - 1);
            System.Array.Copy(_ages, 1, _ages, 0, maxPoints - 1);
            _positions[maxPoints - 1] = p;
            _ages[maxPoints - 1] = 0f;
        }

        private void AgePoints(float dt)
        {
            int write = 0;
            for (int i = 0; i < _count; i++)
            {
                _ages[i] += dt;
                if (_ages[i] >= lifetime) continue; // caducado: se descarta
                if (write != i)
                {
                    _positions[write] = _positions[i];
                    _ages[write] = _ages[i];
                }
                write++;
            }
            _count = write;
        }

        private void RebuildMesh()
        {
            if (_count < 2)
            {
                _mesh.Clear();
                return;
            }

            var cam = Camera.main;
            Vector3 camForward = cam != null ? cam.transform.forward : Vector3.forward;

            int tri = 0;
            for (int i = 0; i < _count; i++)
            {
                float age01 = Mathf.Clamp01(_ages[i] / lifetime);
                float width = Mathf.Lerp(startWidth, endWidth, age01);

                // Dirección de la cinta: tangente entre vecinos.
                Vector3 prev = _positions[Mathf.Max(0, i - 1)];
                Vector3 next = _positions[Mathf.Min(_count - 1, i + 1)];
                Vector3 tangent = (next - prev).normalized;
                if (tangent.sqrMagnitude < 0.0001f) tangent = Vector3.up;

                Vector3 side = Vector3.Cross(tangent, camForward).normalized * (width * 0.5f);

                _vertices[i * 2] = _positions[i] + side;
                _vertices[i * 2 + 1] = _positions[i] - side;
                _uvs[i * 2] = new Vector2(0f, age01);
                _uvs[i * 2 + 1] = new Vector2(1f, age01);

                Color c = colorGradient.Evaluate(age01);
                _colors[i * 2] = c;
                _colors[i * 2 + 1] = c;

                if (i < _count - 1)
                {
                    int a = i * 2;
                    _triangles[tri++] = a;
                    _triangles[tri++] = a + 1;
                    _triangles[tri++] = a + 2;
                    _triangles[tri++] = a + 1;
                    _triangles[tri++] = a + 3;
                    _triangles[tri++] = a + 2;
                }
            }

            _mesh.Clear();
            _mesh.vertices = _vertices;
            _mesh.uv = _uvs;
            _mesh.colors = _colors;
            _mesh.triangles = _triangles;
            _mesh.RecalculateBounds();
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
        }
    }
}
