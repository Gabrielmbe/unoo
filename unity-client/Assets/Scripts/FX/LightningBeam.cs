// =============================================================================
//  LightningBeam.cs  ·  UnoX.FX
//  Rayos de luz hacia el jugador penalizado. Procedurales, sin texturas: se
//  generan los vértices de un LineRenderer con ruido y se desvanecen.
//
//  Por qué procedural y no un VFX Graph:
//   - Un VFX Graph por rayo en móvil es caro de instanciar. Esto son N
//    LineRenderers de un pool con ~24 puntos cada uno.
//   - El trayecto se calcula hacia un punto concreto (el avatar del rival), lo
//    que en un juego de cartas importa más que el aspecto: el jugador tiene que
//    ver A QUIÉN le cae el castigo.
//
//  POOL: los rayos se reutilizan. Instanciar y destruir GameObjects cada vez que
//  alguien juega un +2 es la forma más rápida de provocar tirones en Android.
// =============================================================================

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UnoX.FX
{
    [DisallowMultipleComponent]
    public sealed class LightningBeam : MonoBehaviour
    {
        [SerializeField] private int poolSize = 12;
        [SerializeField] private int segments = 24;
        [SerializeField] private float jaggedness = 0.28f;
        [SerializeField] private float widthStart = 0.09f;
        [SerializeField] private float widthEnd = 0.01f;
        [SerializeField] private Material beamMaterial;
        [SerializeField] private AudioSource crackSfx;

        private readonly List<LineRenderer> _pool = new List<LineRenderer>();
        private Vector3[] _scratch;
        private int _cursor;

        private void Awake()
        {
            for (int i = 0; i < poolSize; i++)
            {
                var go = new GameObject($"beam_{i}");
                go.transform.SetParent(transform, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.positionCount = segments;
                lr.useWorldSpace = true;
                lr.numCapVertices = 4;
                lr.numCornerVertices = 4;
                lr.textureMode = LineTextureMode.Stretch;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                if (beamMaterial != null) lr.sharedMaterial = beamMaterial;
                go.SetActive(false);
                _pool.Add(lr);
            }
        }

        /// <summary>Dispara un haz de rayos desde <paramref name="from"/> a <paramref name="to"/>.</summary>
        public void Fire(Vector3 from, Vector3 to, int bolts = 3, float duration = 0.5f, Color? color = null)
        {
            var c = color ?? new Color(0.9f, 0.4f, 1f);
            for (int i = 0; i < bolts; i++) StartCoroutine(BoltRoutine(from, to, duration, c, i * 0.035f));
            if (crackSfx != null) crackSfx.Play();
        }

        /// <summary>Explosión de rayos alrededor de un punto (UNO pillado, victoria).</summary>
        public void Burst(Vector3 center, int bolts = 6, float duration = 0.6f, Color? color = null)
        {
            var c = color ?? Color.white;
            for (int i = 0; i < bolts; i++)
            {
                float angle = i / (float)bolts * Mathf.PI * 2f;
                var target = center + new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * 2.2f;
                StartCoroutine(BoltRoutine(center, target, duration, c, i * 0.02f));
            }
            if (crackSfx != null) crackSfx.Play();
        }

        private IEnumerator BoltRoutine(Vector3 from, Vector3 to, float duration, Color color, float delay)
        {
            if (delay > 0f) yield return new WaitForSecondsRealtime(delay);

            var lr = Next();
            if (lr == null) yield break;

            lr.gameObject.SetActive(true);
            lr.startWidth = widthStart;
            lr.endWidth = widthEnd;
            lr.startColor = color;
            lr.endColor = new Color(color.r, color.g, color.b, 0f);

            // El rayo "parpadea": se recalcula la forma varias veces durante su vida.
            float t = 0f;
            while (t < duration)
            {
                Reshape(lr, from, to, t / duration);
                t += Time.unscaledDeltaTime;
                yield return null;
            }

            lr.gameObject.SetActive(false);
        }

        private void Reshape(LineRenderer lr, Vector3 from, Vector3 to, float progress)
        {
            // La amplitud del zigzag cae con el progreso: el rayo nace violento y
            // muere recto, que es como se percibe un arco eléctrico real.
            float amp = jaggedness * (1f - progress);
            var dir = to - from;
            var perp = Vector3.Cross(dir, Vector3.forward);
            if (perp.sqrMagnitude < 0.0001f) perp = Vector3.up;
            perp.Normalize();

            // Buffer reutilizado: allocar un array por frame y por rayo es basura
            // para el GC, y el GC es la causa número uno de tirones en Android.
            if (_scratch == null || _scratch.Length != segments) _scratch = new Vector3[segments];

            for (int i = 0; i < segments; i++)
            {
                float u = i / (float)(segments - 1);
                // Envelope: 0 en los extremos, 1 en el centro. Sin esto el rayo
                // se despega del origen y se ve falso.
                float envelope = Mathf.Sin(u * Mathf.PI);
                float noise = Mathf.PerlinNoise(u * 6f + Time.unscaledTime * 9f, progress * 3f) * 2f - 1f;
                _scratch[i] = Vector3.Lerp(from, to, u) + perp * (noise * amp * envelope);
            }
            lr.positionCount = segments;
            lr.SetPositions(_scratch);
        }

        private LineRenderer Next()
        {
            if (_pool.Count == 0) return null;
            _cursor = (_cursor + 1) % _pool.Count;
            return _pool[_cursor];
        }

        private void OnDisable()
        {
            foreach (var lr in _pool) lr.gameObject.SetActive(false);
        }
    }
}
