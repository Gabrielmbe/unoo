// =============================================================================
//  CardBend.shader  ·  URP / Unlit con flexión en el vertex shader
//
//  La carta se comba según _BendAmount a lo largo de _BendAxis. Todo ocurre en
//  GPU: el coste por carta es constante y no depende del número de cartas en
//  vuelo, que es exactamente lo que se necesita cuando caen 4 cartas de un +4.
//
//  Se incluye también el realce "_Highlight" que usa CardView para marcar las
//  cartas jugables con MaterialPropertyBlock (sin romper las draw calls: un
//  material distinto por carta sí las rompería).
//
//  Rendimiento:
//   - Un solo pass, sin sombras, sin profundidad extra.
//   - La deformación es una función cuadrática de la coordenada local, que es lo
//     más barato que da una curvatura creíble.
//   - Funciona igual en GLES3, Metal y Vulkan.
// =============================================================================

Shader "UnoX/CardBend"
{
    Properties
    {
        _MainTex ("Sprite (RGB)", 2D) = "white" {}
        _Color ("Tinte", Color) = (1,1,1,1)
        _BendAmount ("Flexión", Range(-1, 1)) = 0
        _BendAxis ("Eje de flexión", Vector) = (0, 1, 0, 0)
        _BendStiffness ("Rigidez", Range(0.1, 8)) = 2.2
        _Highlight ("Realce jugable", Range(0, 1)) = 0
        _HighlightColor ("Color del realce", Color) = (1, 0.85, 0.2, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "CardForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float  bend       : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4  _Color;
                float  _BendAmount;
                float4 _BendAxis;
                float  _BendStiffness;
                half   _Highlight;
                half4  _HighlightColor;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                float3 pos = IN.positionOS.xyz;

                // Proyección sobre el eje de flexión, normalizada al tamaño de la carta.
                float along = dot(pos.xy, normalize(_BendAxis.xy + float2(1e-5, 1e-5)));
                // Curvatura cuadrática: 0 en el centro de agarre, máxima en el extremo.
                float curve = along * abs(along) * _BendStiffness;
                pos.z += curve * _BendAmount;
                // Contracción lateral: al combarse, la carta se acorta un poco.
                pos.xy -= normalize(_BendAxis.xy + float2(1e-5, 1e-5)) * abs(curve * _BendAmount) * 0.06;

                OUT.positionCS = TransformObjectToHClip(pos);
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                OUT.bend = saturate(abs(curve * _BendAmount));
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                half4 col = tex * _Color;

                // Sombreado barato de la flexión: da volumen sin luces.
                col.rgb *= lerp(1.0h, 0.72h, IN.bend);

                // Realce de carta jugable: bordea sin tapar el arte.
                float2 edge = smoothstep(0.0, 0.06, IN.uv) * smoothstep(0.0, 0.06, 1.0 - IN.uv);
                float border = 1.0 - saturate(edge.x * edge.y);
                col.rgb += _HighlightColor.rgb * border * _Highlight * 1.4h;
                col.a = saturate(col.a + border * _Highlight * 0.5h);

                return col;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
