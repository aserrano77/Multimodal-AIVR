Shader "Custom/BoxEdgeHighlight_URP"
{
    Properties
    {
        // Color base del cubo (relleno principal)
        _BaseColor ("Base Color", Color) = (0, 0.678, 1, 1)

        // Color del borde (normalmente negro para resaltar aristas)
        _EdgeColor ("Edge Color", Color) = (0, 0, 0, 1)

        // Grosor del borde medido en coordenadas locales del cubo
        _EdgeWidth ("Edge Width", Range(0.001, 0.2)) = 0.08

        // Suavizado del borde para evitar transiciones bruscas (antialiasing)
        _EdgeSoftness ("Edge Softness", Range(0.001, 0.1)) = 0.02
    }

    SubShader
    {
        Tags
        {
            // Shader opaco para URP
            "RenderType"="Opaque"
            "RenderPipeline"="UniversalPipeline"
            "Queue"="Geometry"
        }

        Pass
        {
            Name "ForwardUnlit"

            // Se usa el pass forward de URP pero sin iluminación (unlit)
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM

            // Funciones principales del shader
            #pragma vertex vert
            #pragma fragment frag

            // Librería base de URP (transformaciones, matrices, etc.)
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // =========================
            // ENTRADA DEL VÉRTICE
            // =========================
            struct Attributes
            {
                float4 positionOS : POSITION; // Posición en espacio local (Object Space)
            };

            // =========================
            // DATOS INTERPOLADOS AL FRAGMENTO
            // =========================
            struct Varyings
            {
                float4 positionHCS : SV_POSITION; // Posición en espacio clip (pantalla)
                float3 positionOS : TEXCOORD0;    // Posición local interpolada
            };

            // =========================
            // PARÁMETROS DEL MATERIAL
            // =========================
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _EdgeColor;
                float _EdgeWidth;
                float _EdgeSoftness;
            CBUFFER_END

            // =========================
            // VERTEX SHADER
            // =========================
            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                // Transformación de espacio local a clip space
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);

                // Se pasa la posición local al fragment shader
                OUT.positionOS = IN.positionOS.xyz;

                return OUT;
            }

            // =========================
            // FUNCIÓN AUXILIAR DE BORDES
            // =========================
            // Calcula cuánto de "borde" hay en un eje concreto
            float EdgeMaskAxis(float axisAbs, float edgeWidth, float edgeSoftness)
            {
                // Distancia al borde del cubo (asumiendo cubo centrado en origen [-0.5, 0.5])
                float d = 0.5 - axisAbs;

                // Genera transición suave del borde hacia el interior
                return 1.0 - smoothstep(edgeWidth, edgeWidth + edgeSoftness, d);
            }

            // =========================
            // FRAGMENT SHADER
            // =========================
            half4 frag(Varyings IN) : SV_Target
            {
                // Valor absoluto de la posición local
                // Permite tratar todas las caras del cubo de forma uniforme
                float3 p = abs(IN.positionOS);

                // Máscara de borde por eje
                float edgeX = EdgeMaskAxis(p.x, _EdgeWidth, _EdgeSoftness);
                float edgeY = EdgeMaskAxis(p.y, _EdgeWidth, _EdgeSoftness);
                float edgeZ = EdgeMaskAxis(p.z, _EdgeWidth, _EdgeSoftness);

                // Combinación de ejes para detectar aristas (intersección de caras)
                float xy = edgeX * edgeY;
                float xz = edgeX * edgeZ;
                float yz = edgeY * edgeZ;

                // Máscara final: se toma el máximo para cubrir todas las aristas
                float edgeMask = max(xy, max(xz, yz));

                // Mezcla entre color base y color de borde
                float4 finalColor = lerp(_BaseColor, _EdgeColor, edgeMask);

                return finalColor;
            }

            ENDHLSL
        }
    }
}