// =============================================================================
//  UnoPulse.shader  ·  URP / Unlit aditivo para el botón ¡UNO!
//
//  Fuego Y electricidad en el mismo material, conmutables por _Energy:
//   - _Energy bajo:  llama lenta y anaranjada (ventana abierta, sin urgencia).
//   - _Energy alto:  arco eléctrico blanco-azulado y rápido (último segundo).
//  El mezclador lo lleva UnoButtonView con un MaterialPropertyBlock, así que un
//  solo material cubre ambos estados y no se rompe el batching.
//
//  Todo procedural (ruido de valor + fbm barato): CERO texturas, CERO samplers
//  extra. En móvil cada sampler adicional es ancho de banda que se paga caro.
// =============================================================================

Shader "UnoX/UnoPulse"
{
    Properties
    {
        _Energy ("Energía", Range(0, 1)) = 0.3
        _Speed ("Velocidad", Range(0.1, 6)) = 1.6
        _FireColor ("Color fuego", Color) = (1.0, 0.45, 0.08, 1)
        _ElectricColor ("Color eléctrico", Color) = (0.55, 0.85, 1.0, 1)
        _CoreColor ("Núcleo", Color) = (1, 1, 1, 1)
        _EdgePower ("Concentración al borde", Range(0.5, 6)) = 2.0
        _Intensity ("Intensidad aditiva", Range(0, 4)) = 1.6
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+10"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "UnoPulse"
            Blend One One          // aditivo: brilla de verdad sobre el fondo
            Cull Off
            ZWrite Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                half  _Energy;
                half  _Speed;
                half4 _FireColor;
                half4 _ElectricColor;
                half4 _CoreColor;
                half  _EdgePower;
                half  _Intensity;
            CBUFFER_END

            // Ruido de valor con hash: más barato que Perlin y suficiente aquí.
            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float vnoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = hash21(i);
                float b = hash21(i + float2(1, 0));
                float c = hash21(i + float2(0, 1));
                float d = hash21(i + float2(1, 1));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // Dos octavas: el detalle de la tercera no se percibe al tamaño del botón.
            float fbm2(float2 p)
            {
                float v = 0.0;
                v += 0.62 * vnoise(p);
                v += 0.31 * vnoise(p * 2.13 + 7.1);
                return v;
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 uv = IN.uv;
                float2 centered = uv * 2.0 - 1.0;
                float radius = length(centered);

                float t = _Time.y * _Speed * lerp(0.7, 3.0, _Energy);

                // Distorsión radial: las llamas suben desde el borde hacia fuera.
                float2 q = centered * 2.4;
                q.y -= t * 0.9;
                float n = fbm2(q + fbm2(q * 0.6 + t * 0.35));

                // Máscara de anillo: la energía vive en el borde del botón.
                float ring = smoothstep(1.0, 1.0 - 0.42 / _EdgePower, radius);
                float falloff = pow(saturate(1.0 - radius), _EdgePower * 0.5);

                float flame = saturate(n * 1.6 - 0.25) * falloff;

                // Rayos eléctricos: bandas finas de ruido umbralizadas.
                float bolt = smoothstep(0.62, 0.98, vnoise(float2(radius * 9.0, t * 2.2)));
                bolt *= falloff * _Energy;

                half3 fireCol = _FireColor.rgb * flame;
                half3 elecCol = _ElectricColor.rgb * bolt * 1.5;
                half3 core = _CoreColor.rgb * pow(saturate(1.0 - radius), 6.0) * _Energy;

                half3 rgb = (fireCol + elecCol + core) * _Intensity;

                // Latido: lo marca el script por _Energy, no por _Time, para que
                // el pulso sea idéntico en PC y móvil aunque el framerate difiera.
                rgb *= lerp(0.55, 1.35, _Energy);

                // Fuera del círculo, nada: evita un halo rectangular feo.
                half alpha = saturate((flame + bolt + core.r) * ring);
                alpha *= smoothstep(1.02, 0.94, radius);

                return half4(rgb, alpha);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
