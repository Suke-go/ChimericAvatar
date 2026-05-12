Shader "Chimera/SplatPack Gaussian Splat"
{
    Properties
    {
        _OpacityScale ("Opacity Scale", Range(0, 4)) = 1
        _SplatScale ("Splat Scale", Range(0, 4)) = 1
        _AlphaClip ("Alpha Clip", Range(0, 0.25)) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ForwardUnlit"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            struct SplatPackSplat
            {
                float4 centerWS;
                float4 axis0WS;
                float4 axis1WS;
                float4 axis2WS;
                float4 color;
                float4 meta;
            };

            struct ProjectedSplat
            {
                float4 clipCenter;
                float4 axis0Ndc;
                float4 axis1Ndc;
                float4 color;
                float4 meta;
            };

            StructuredBuffer<SplatPackSplat> _Splats;
            StructuredBuffer<uint> _DrawOrder;
            StructuredBuffer<ProjectedSplat> _ProjectedSplats;

            static const float kGaussianExtent = 2.8284271247461903;

            CBUFFER_START(UnityPerMaterial)
                float _OpacityScale;
                float _SplatScale;
                float _AlphaClip;
                float _UseProjectedSplatCache;
                int _ProjectedSplatCacheEyeStride;
                int _ProjectedSplatCacheEyeCount;
            CBUFFER_END

            struct Attributes
            {
                uint vertexID : SV_VertexID;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 gaussianUV : TEXCOORD0;
                float radiusPixels : TEXCOORD1;
                float tailExtent : TEXCOORD2;
                float4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float2 QuadCorner(uint vertexID)
            {
                if (vertexID == 0) return float2(-1.0, -1.0);
                if (vertexID == 1) return float2( 1.0, -1.0);
                if (vertexID == 2) return float2( 1.0,  1.0);
                if (vertexID == 3) return float2(-1.0, -1.0);
                if (vertexID == 4) return float2( 1.0,  1.0);
                return float2(-1.0,  1.0);
            }

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                uint drawIndex = input.vertexID / 6;
                uint cornerIndex = input.vertexID - drawIndex * 6;
                uint splatIndex = _DrawOrder[drawIndex];

                uint projectedEyeCount = (uint)max(1, _ProjectedSplatCacheEyeCount);
                uint projectedEyeStride = (uint)max(0, _ProjectedSplatCacheEyeStride);
                uint projectedEyeIndex = 0;
                #if defined(UNITY_SINGLE_PASS_STEREO) || defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
                projectedEyeIndex = min((uint)unity_StereoEyeIndex, projectedEyeCount - 1);
                #endif

                ProjectedSplat projected = _ProjectedSplats[projectedEyeIndex * projectedEyeStride + splatIndex];
                if (projected.meta.z < 0.5)
                {
                    output.positionHCS = float4(0.0, 0.0, 2.0, 1.0);
                    output.gaussianUV = float2(99.0, 99.0);
                    output.radiusPixels = 0.0;
                    output.tailExtent = 0.0;
                    output.color = 0.0;
                    return output;
                }

                float2 corner = QuadCorner(cornerIndex);
                float tailExtent = projected.meta.w > 0.0 ? projected.meta.w : kGaussianExtent;
                float4 clip = projected.clipCenter;
                clip.xy += (projected.axis0Ndc.xy * corner.x + projected.axis1Ndc.xy * corner.y) * clip.w;

                output.positionHCS = clip;
                output.gaussianUV = corner * tailExtent;
                output.radiusPixels = projected.meta.y;
                output.tailExtent = tailExtent;
                output.color = projected.color;
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float r2 = dot(input.gaussianUV, input.gaussianUV);
                float tailExtent = max(0.001, input.tailExtent);
                float edgeDistance = tailExtent * tailExtent - r2;
                clip(edgeDistance);
                float alpha = exp(-0.5 * r2) * input.color.a * _OpacityScale;
                float smallRadiusFade = saturate((2.0 - input.radiusPixels) * 0.5);
                float edgeScale = max(0.25, tailExtent * 0.5);
                alpha *= saturate(edgeDistance / edgeScale * lerp(1.0, 0.625, smallRadiusFade));
                clip(alpha - _AlphaClip);
                return float4(input.color.rgb, alpha);
            }
            ENDHLSL
        }
    }
}
