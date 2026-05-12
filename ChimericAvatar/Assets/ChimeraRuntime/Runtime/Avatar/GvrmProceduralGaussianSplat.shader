Shader "Chimera/GVRM Procedural Gaussian Splat"
{
    Properties
    {
        _OpacityScale ("Opacity Scale", Range(0, 4)) = 1
        _SplatScale ("Splat Scale", Range(0, 4)) = 1
        _AlphaClip ("Alpha Clip", Range(0, 0.25)) = 0
        _MinScreenRadiusPixels ("Min Screen Radius Pixels", Range(0, 8)) = 0
        _MaxScreenRadiusPixels ("Max Screen Radius Pixels", Range(1, 1024)) = 1024
        _Kernel2DSize ("2D Kernel Size", Range(0, 2)) = 0.3
        _AntialiasOpacityCompensation ("Antialias Opacity Compensation", Range(0, 1)) = 0
        _EigenTermFloor ("GS3D Eigen Term Floor", Range(0, 1)) = 0.1
        _LinearizeColor ("Linearize PLY Color In Linear Projects", Range(0, 1)) = 0
        _OpacityPower ("Opacity Power", Range(0.25, 2)) = 1
        _OpacityDensity ("Opacity Density", Range(0.25, 4)) = 1
        _SphericalHarmonicsScale ("Spherical Harmonics Scale", Range(0, 1.5)) = 1
        _SurfaceContinuity ("Surface Continuity", Range(0, 1)) = 0.3
        _SurfaceContinuityKernelPixels ("Surface Continuity Kernel Pixels", Range(0, 2)) = 0.45
        _SurfaceContinuityShStability ("Surface Continuity SH Stability", Range(0, 1)) = 0.15
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct ProceduralSplat
            {
                float4 centerWS;
                float4 axis0WS;
                float4 axis1WS;
                float4 axis2WS;
                float4 color;
                float4 meta;
            };

            struct SphericalHarmonicsSplat
            {
                float4 sh1;
                float4 sh2;
                float4 sh3;
                float4 sh4;
                float4 sh5;
                float4 sh6;
                float4 sh7;
                float4 sh8;
                float4 sh9;
                float4 sh10;
                float4 sh11;
                float4 sh12;
                float4 sh13;
                float4 sh14;
                float4 sh15;
            };

            struct ProjectedSplat
            {
                float4 clipCenter;
                float4 axis0Ndc;
                float4 axis1Ndc;
                float4 color;
                float4 meta;
            };

            StructuredBuffer<ProceduralSplat> _Splats;
            StructuredBuffer<uint> _DrawOrder;
            StructuredBuffer<SphericalHarmonicsSplat> _SphericalHarmonics;
            StructuredBuffer<ProjectedSplat> _ProjectedSplats;

            CBUFFER_START(UnityPerMaterial)
                float _OpacityScale;
                float _SplatScale;
                float _AlphaClip;
                float _MinScreenRadiusPixels;
                float _MaxScreenRadiusPixels;
                float _Kernel2DSize;
                float _AntialiasOpacityCompensation;
                float _EigenTermFloor;
                float _LinearizeColor;
                float _OpacityPower;
                float _OpacityDensity;
                float _UseSphericalHarmonics;
                float _SphericalHarmonicsDegree;
                float _SphericalHarmonicsScale;
                float _SurfaceContinuity;
                float _SurfaceContinuityKernelPixels;
                float _SurfaceContinuityShStability;
                float _UseProceduralInstances;
                float _UseProjectedSplatCache;
                int _ProjectedSplatCacheEyeStride;
                int _ProjectedSplatCacheEyeCount;
                float4x4 _WorldToGsLocal;
            CBUFFER_END

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 gaussianUV : TEXCOORD0;
                float4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            struct Attributes
            {
                uint vertexID : SV_VertexID;
                uint instanceID : SV_InstanceID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            static const float kGaussianExtent = 2.8284271247461903;
            static const float kShC1 = 0.4886025119029199;
            static const float kShC2_0 = 1.0925484305920792;
            static const float kShC2_1 = -1.0925484305920792;
            static const float kShC2_2 = 0.31539156525252005;
            static const float kShC2_3 = -1.0925484305920792;
            static const float kShC2_4 = 0.5462742152960396;
            static const float kShC3_0 = -0.5900435899266435;
            static const float kShC3_1 = 2.890611442640554;
            static const float kShC3_2 = -0.4570457994644658;
            static const float kShC3_3 = 0.3731763325901154;
            static const float kShC3_4 = -0.4570457994644658;
            static const float kShC3_5 = 1.445305721320277;
            static const float kShC3_6 = -0.5900435899266435;

            float2 QuadCorner(uint vertexID)
            {
                if (vertexID == 0) return float2(-1.0, -1.0);
                if (vertexID == 1) return float2( 1.0, -1.0);
                if (vertexID == 2) return float2( 1.0,  1.0);
                if (vertexID == 3) return float2(-1.0, -1.0);
                if (vertexID == 4) return float2( 1.0,  1.0);
                return float2(-1.0,  1.0);
            }

            void SolveEigen2(float c00, float c01, float c11, out float lambda0, out float lambda1, out float2 dir0)
            {
                float determinant = c00 * c11 - c01 * c01;
                float traceOver2 = 0.5 * (c00 + c11);
                float term2 = sqrt(max(_EigenTermFloor, traceOver2 * traceOver2 - determinant));
                lambda0 = traceOver2 + term2;
                lambda1 = traceOver2 - term2;

                if (abs(c01) > 1e-8)
                {
                    dir0 = normalize(float2(c01, lambda0 - c00));
                }
                else
                {
                    dir0 = c00 >= c11 ? float2(1.0, 0.0) : float2(0.0, 1.0);
                }
            }

            float3 EvaluateSphericalHarmonics(SphericalHarmonicsSplat sh, float3 direction, float degree)
            {
                float x = direction.x;
                float y = direction.y;
                float z = direction.z;
                float xx = x * x;
                float yy = y * y;
                float zz = z * z;
                float xy = x * y;
                float yz = y * z;
                float xz = x * z;

                float3 color = kShC1 * (-sh.sh1.xyz * y + sh.sh2.xyz * z - sh.sh3.xyz * x);
                if (degree >= 2.0)
                {
                    color +=
                        (kShC2_0 * xy) * sh.sh4.xyz +
                        (kShC2_1 * yz) * sh.sh5.xyz +
                        (kShC2_2 * (2.0 * zz - xx - yy)) * sh.sh6.xyz +
                        (kShC2_3 * xz) * sh.sh7.xyz +
                        (kShC2_4 * (xx - yy)) * sh.sh8.xyz;
                }

                if (degree >= 3.0)
                {
                    color +=
                        (kShC3_0 * y * (3.0 * xx - yy)) * sh.sh9.xyz +
                        (kShC3_1 * xy * z) * sh.sh10.xyz +
                        (kShC3_2 * y * (4.0 * zz - xx - yy)) * sh.sh11.xyz +
                        (kShC3_3 * z * (2.0 * zz - 3.0 * xx - 3.0 * yy)) * sh.sh12.xyz +
                        (kShC3_4 * x * (4.0 * zz - xx - yy)) * sh.sh13.xyz +
                        (kShC3_5 * z * (xx - yy)) * sh.sh14.xyz +
                        (kShC3_6 * x * (xx - 3.0 * yy)) * sh.sh15.xyz;
                }

                return color;
            }

            float3 SrgbToLinearApprox(float3 value)
            {
                value = saturate(value);
                float3 low = value / 12.92;
                float3 high = pow((value + 0.055) / 1.055, 2.4);
                return lerp(low, high, step(float3(0.04045, 0.04045, 0.04045), value));
            }

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                uint drawIndex;
                uint cornerIndex;
                if (_UseProceduralInstances > 0.5)
                {
                    drawIndex = input.instanceID;
                    cornerIndex = input.vertexID % 6;
                }
                else
                {
                    drawIndex = input.vertexID / 6;
                    cornerIndex = input.vertexID - drawIndex * 6;
                }

                uint splatIndex = _DrawOrder[drawIndex];
                if (_UseProjectedSplatCache > 0.5)
                {
                    uint projectedEyeCount = (uint)max(1, _ProjectedSplatCacheEyeCount);
                    uint projectedEyeStride = (uint)max(0, _ProjectedSplatCacheEyeStride);
                    uint projectedEyeIndex = 0;
                    #if defined(UNITY_SINGLE_PASS_STEREO)
                    projectedEyeIndex = min((uint)unity_StereoEyeIndex, projectedEyeCount - 1);
                    #endif

                    uint projectedIndex = projectedEyeIndex * projectedEyeStride + splatIndex;
                    ProjectedSplat projected = _ProjectedSplats[projectedIndex];
                    if (projected.meta.z < 0.5)
                    {
                        output.positionHCS = float4(0.0, 0.0, 2.0, 1.0);
                        output.gaussianUV = float2(99.0, 99.0);
                        output.color = float4(0.0, 0.0, 0.0, 0.0);
                        return output;
                    }

                    float2 unitCornerCached = QuadCorner(cornerIndex);
                    float4 clipCached = projected.clipCenter;
                    clipCached.xy += (projected.axis0Ndc.xy * unitCornerCached.x
                        + projected.axis1Ndc.xy * unitCornerCached.y) * clipCached.w;
                    output.positionHCS = clipCached;
                    output.gaussianUV = unitCornerCached * kGaussianExtent;
                    output.color = projected.color;
                    return output;
                }

                ProceduralSplat splat = _Splats[splatIndex];

                float3 centerWS = splat.centerWS.xyz;
                float3 centerVS = mul(UNITY_MATRIX_V, float4(centerWS, 1.0)).xyz;
                float4 clipCenter = mul(UNITY_MATRIX_VP, float4(centerWS, 1.0));
                float clipLimit = abs(clipCenter.w) * 1.2;
                if (centerVS.z >= -1e-4 || abs(clipCenter.x) > clipLimit || abs(clipCenter.y) > clipLimit)
                {
                    output.positionHCS = float4(0.0, 0.0, 2.0, 1.0);
                    output.gaussianUV = float2(99.0, 99.0);
                    output.color = float4(0.0, 0.0, 0.0, 0.0);
                    return output;
                }

                float depth = max(1e-4, -centerVS.z);

                float3 axis0VS = mul((float3x3)UNITY_MATRIX_V, splat.axis0WS.xyz);
                float3 axis1VS = mul((float3x3)UNITY_MATRIX_V, splat.axis1WS.xyz);
                float3 axis2VS = mul((float3x3)UNITY_MATRIX_V, splat.axis2WS.xyz);

                float covXX = dot(float3(axis0VS.x, axis1VS.x, axis2VS.x), float3(axis0VS.x, axis1VS.x, axis2VS.x));
                float covXY = dot(float3(axis0VS.x, axis1VS.x, axis2VS.x), float3(axis0VS.y, axis1VS.y, axis2VS.y));
                float covXZ = dot(float3(axis0VS.x, axis1VS.x, axis2VS.x), float3(axis0VS.z, axis1VS.z, axis2VS.z));
                float covYY = dot(float3(axis0VS.y, axis1VS.y, axis2VS.y), float3(axis0VS.y, axis1VS.y, axis2VS.y));
                float covYZ = dot(float3(axis0VS.y, axis1VS.y, axis2VS.y), float3(axis0VS.z, axis1VS.z, axis2VS.z));
                float covZZ = dot(float3(axis0VS.z, axis1VS.z, axis2VS.z), float3(axis0VS.z, axis1VS.z, axis2VS.z));

                float focalX = abs(UNITY_MATRIX_P._m00) * _ScreenParams.x * 0.5;
                float focalY = abs(UNITY_MATRIX_P._m11) * _ScreenParams.y * 0.5;
                float invDepth = 1.0 / depth;
                float invDepthSq = invDepth * invDepth;
                float j00 = focalX * invDepth;
                float j02 = focalX * centerVS.x * invDepthSq;
                float j11 = focalY * invDepth;
                float j12 = focalY * centerVS.y * invDepthSq;

                float c00Raw = j00 * j00 * covXX + 2.0 * j00 * j02 * covXZ + j02 * j02 * covZZ;
                float c01 = j00 * j11 * covXY + j00 * j12 * covXZ + j02 * j11 * covYZ + j02 * j12 * covZZ;
                float c11Raw = j11 * j11 * covYY + 2.0 * j11 * j12 * covYZ + j12 * j12 * covZZ;
                float detRaw = max(0.0, c00Raw * c11Raw - c01 * c01);
                float surfaceContinuity = saturate(_SurfaceContinuity);
                float kernel2D = max(0.0, _Kernel2DSize + surfaceContinuity * _SurfaceContinuityKernelPixels);
                float c00 = c00Raw + kernel2D;
                float c11 = c11Raw + kernel2D;
                float detBlur = max(1e-8, c00 * c11 - c01 * c01);
                float antialiasCompensation = sqrt(max(0.0, detRaw / detBlur));

                float lambda0;
                float lambda1;
                float2 dir0;
                SolveEigen2(c00, c01, c11, lambda0, lambda1, dir0);
                if (lambda1 <= 0.0)
                {
                    output.positionHCS = float4(0.0, 0.0, 2.0, 1.0);
                    output.gaussianUV = float2(99.0, 99.0);
                    output.color = float4(0.0, 0.0, 0.0, 0.0);
                    return output;
                }

                float2 dir1 = float2(dir0.y, -dir0.x);

                float maxRadiusPixels = max(_MinScreenRadiusPixels, _MaxScreenRadiusPixels);
                float radius0Pixels = max(_MinScreenRadiusPixels, _SplatScale * min(kGaussianExtent * sqrt(lambda0), maxRadiusPixels));
                float radius1Pixels = max(_MinScreenRadiusPixels, _SplatScale * min(kGaussianExtent * sqrt(lambda1), maxRadiusPixels));
                float2 unitCorner = QuadCorner(cornerIndex);
                float2 gaussianUV = unitCorner * kGaussianExtent;
                float2 pixelOffset = dir0 * (radius0Pixels * unitCorner.x) + dir1 * (radius1Pixels * unitCorner.y);

                float2 ndcOffset = 2.0 * pixelOffset / max(float2(1.0, 1.0), _ScreenParams.xy);
                clipCenter.xy += ndcOffset * clipCenter.w;

                float4 color = splat.color;
                color.a *= lerp(1.0, antialiasCompensation, saturate(_AntialiasOpacityCompensation));
                if (_UseSphericalHarmonics > 0.5 && splat.meta.x >= 0.0)
                {
                    uint shIndex = (uint)(splat.meta.x + 0.5);
                    float3 shCenter = mul(_WorldToGsLocal, float4(centerWS, 1.0)).xyz;
                    float3 shCamera = mul(_WorldToGsLocal, float4(_WorldSpaceCameraPos.xyz, 1.0)).xyz;
                    float3 shViewDir = normalize(shCenter - shCamera);
                    float alphaConfidence = saturate(color.a * _OpacityScale * 2.0);
                    float shScale = _SphericalHarmonicsScale
                        * lerp(1.0, alphaConfidence, surfaceContinuity * saturate(_SurfaceContinuityShStability));
                    color.rgb = saturate(color.rgb + shScale * EvaluateSphericalHarmonics(
                        _SphericalHarmonics[shIndex],
                        shViewDir,
                        _SphericalHarmonicsDegree));
                }
                else
                {
                    color.rgb = saturate(color.rgb);
                }

                if (_LinearizeColor > 0.5)
                {
                    color.rgb = SrgbToLinearApprox(color.rgb);
                }

                output.positionHCS = clipCenter;
                output.gaussianUV = gaussianUV;
                output.color = color;
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float radius2 = dot(input.gaussianUV, input.gaussianUV);
                clip(8.0 - radius2);
                float continuityRadius2 = lerp(radius2, radius2 * 0.94, saturate(_SurfaceContinuity));
                float baseAlpha = exp(-0.5 * continuityRadius2) * saturate(input.color.a * _OpacityScale);
                float shapedAlpha = pow(saturate(baseAlpha), max(0.25, _OpacityPower));
                float alpha = 1.0 - pow(saturate(1.0 - shapedAlpha), max(0.25, _OpacityDensity));
                clip(alpha - _AlphaClip);
                return float4(input.color.rgb, alpha);
            }
            ENDHLSL
        }
    }
}
