Shader "Hidden/Dust/Volume"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "DustVolume"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One Zero

            HLSLPROGRAM
            #pragma target 4.5
            #pragma require 2darray
            #pragma vertex DustVert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            struct DustAttributes
            {
                uint vertexID : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DustVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 texcoord : TEXCOORD0;
                float2 ndc : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            DustVaryings DustVert(DustAttributes input)
            {
                DustVaryings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float4 positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                float2 blitUV = GetFullScreenTriangleTexCoord(input.vertexID);

                output.positionCS = positionCS;
                output.texcoord = blitUV * _BlitScaleBias.xy + _BlitScaleBias.zw;
                output.ndc = positionCS.xy;
                return output;
            }

            // Camera depth
            TEXTURE2D_X(_CameraDepthTexture);
            SAMPLER(sampler_CameraDepthTexture);

            // Dust brick pool
            TEXTURE2D_ARRAY(_BrickPool);
            SAMPLER(sampler_BrickPool);
            StructuredBuffer<int> _PageTable;

            // Camera
            float4x4 _InvViewProj0;
            float4x4 _InvViewProj1;
            float4 _EyePosition0;
            float4 _EyePosition1;

            // Parameters
            int _LevelCount;
            int _FallbackLod;
            int _BrickSize;
            int _MaxRaySteps;
            float _StepMultiplier;

            // _ExtinctionScale is tau_V per (n_H [cm^-3] * path length [pc]).
            float _ExtinctionScale;
            float3 _ExtinctionRGB;

            float _ScatteringStrength;
            // Fraction of extinction due to scattering: alpha_sca / alpha_ext.
            float _ScatteringAlbedo;
            float4 _ScatteringColor;
            float4 _LightColor;
            // Direction from a dust sample toward the assumed directional light source.
            float3 _LightDirection;
            float _PhaseG;

            float3 _VolumeOrigin;
            float3 _BaseStep;
            float3 _VolumeBoundsMin;
            float3 _VolumeBoundsMax;

            float _PageOffsets[16];
            float _BrickDimsX[16];
            float _BrickDimsY[16];
            float _BrickDimsZ[16];
            float _VoxelSizes[16];
            float _LodSwitchDistances[16];

            int GetPageSlot(int level, int3 brick)
            {
                if (level < 0 || level >= _LevelCount)
                    return -1;

                if (brick.x < 0 || brick.y < 0 || brick.z < 0)
                    return -1;

                if (brick.x >= _BrickDimsX[level] ||
                    brick.y >= _BrickDimsY[level] ||
                    brick.z >= _BrickDimsZ[level])
                    return -1;

                int index = (int)_PageOffsets[level] + brick.x +
                    (int)_BrickDimsX[level] *
                    (brick.y + (int)_BrickDimsY[level] * brick.z);

                return _PageTable[index] - 1;
            }

            float SampleLevelDensity(float3 worldPosition, int level, out bool present)
            {
                present = false;
                int scale = 1 << level;

                float3 sourceIndex = (worldPosition - _VolumeOrigin) / _BaseStep;
                float3 coarseCell = (sourceIndex + 0.5) / scale - 0.5;

                int3 coarseDims = int3(
                    (int)_BrickDimsX[level] * _BrickSize,
                    (int)_BrickDimsY[level] * _BrickSize,
                    (int)_BrickDimsZ[level] * _BrickSize
                );

                float3 maxCell = float3(coarseDims.x - 1, coarseDims.y - 1, coarseDims.z - 1);
                coarseCell = clamp(coarseCell, 0.0, maxCell);

                int3 cellIndex = int3(floor(coarseCell));
                float3 localFraction = coarseCell - floor(coarseCell);
                int3 brick = cellIndex / _BrickSize;
                int3 localIndex = cellIndex - brick * _BrickSize;

                int slot = GetPageSlot(level, brick);
                if (slot < 0)
                    return 0.0;

                present = true;
                float2 uv = (float2(localIndex.x, localIndex.y) + localFraction.xy + 0.5) / _BrickSize;
                int slice = slot * _BrickSize + localIndex.z;

                return SAMPLE_TEXTURE2D_ARRAY_LOD(_BrickPool, sampler_BrickPool, uv, slice, 0).r;
            }

            int ChooseLOD(float distance)
            {
                int fallback = min(_FallbackLod, _LevelCount - 1);
                for (int level = 0; level < 16; level++)
                {
                    if (level >= fallback)
                        break;
                    if (distance < _LodSwitchDistances[level])
                        return level;
                }
                return fallback;
            }

            float SampleDensity(float3 worldPosition, int desiredLOD, out int usedLOD, out bool hasBrick)
            {
                usedLOD = desiredLOD;
                hasBrick = false;

                for (int level = desiredLOD; level < 16; level++)
                {
                    if (level >= _LevelCount)
                        break;

                    bool present;
                    float density = SampleLevelDensity(worldPosition, level, present);
                    if (present)
                    {
                        usedLOD = level;
                        hasBrick = true;
                        return density;
                    }
                }
                return 0.0;
            }

            bool IntersectBox(float3 origin, float3 direction, float3 boxMin, float3 boxMax,
                out float tEnter, out float tExit)
            {
                float3 signDirection = sign(direction);
                signDirection.x = signDirection.x == 0.0 ? 1.0 : signDirection.x;
                signDirection.y = signDirection.y == 0.0 ? 1.0 : signDirection.y;
                signDirection.z = signDirection.z == 0.0 ? 1.0 : signDirection.z;

                float3 safeDirection = max(abs(direction), 1e-6) * signDirection;
                float3 invDirection = 1.0 / safeDirection;
                float3 t0 = (boxMin - origin) * invDirection;
                float3 t1 = (boxMax - origin) * invDirection;
                float3 lo = min(t0, t1);
                float3 hi = max(t0, t1);

                tEnter = max(max(lo.x, lo.y), lo.z);
                tExit = min(min(hi.x, hi.y), hi.z);
                tEnter = max(tEnter, 0.0);
                return tExit > tEnter;
            }

            float3 ReconstructWorldRay(
                float2 ndc,
                float4x4 inverseViewProj,
                float3 eyePosition)
            {
                // Avoid unprojecting the near plane: at large world
                // coordinates, its tiny offset from the camera loses precision.
                #if UNITY_REVERSED_Z
                    const float rayDepth = 0.0001;
                #else
                    const float rayDepth = 0.9999;
                #endif

                float4 clipPosition = float4(
                    ndc.x, ndc.y, rayDepth, 1.0
                );

                float4 worldH = mul(inverseViewProj, clipPosition);

                if (abs(worldH.w) < 1e-6)
                    return float3(0.0, 0.0, 1.0);

                float3 worldPosition = worldH.xyz / worldH.w;

                return normalize(worldPosition - eyePosition);
            }

            float3 ReconstructWorldPosition(float2 ndc, float depth, float4x4 inverseViewProj)
            {
                float4 clipPosition = float4(ndc.x, ndc.y, depth, 1.0);
                float4 worldH = mul(inverseViewProj, clipPosition);
                if (abs(worldH.w) < 1e-6)
                    return 0.0;
                return worldH.xyz / worldH.w;
            }

            // Normalized Henyey-Greenstein phase function (integrates to 1 over 4*pi).
            float PhaseHG(float cosTheta, float g)
            {
                float g2 = g * g;
                float denominator = max(1.0 + g2 - 2.0 * g * cosTheta, 1e-4);
                return (1.0 - g2) / (4.0 * PI * pow(denominator, 1.5));
            }

            float4 Frag(DustVaryings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                uint eye = (uint)unity_StereoEyeIndex;

                float4 source = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.texcoord);

                float4x4 inverseViewProj = eye == 0 ? _InvViewProj0 : _InvViewProj1;
                float3 cameraPosition = eye == 0 ? _EyePosition0.xyz : _EyePosition1.xyz;

                float3 rayDirection = ReconstructWorldRay(input.ndc, inverseViewProj, cameraPosition);

                float tEnter;
                float tExit;
                if (!IntersectBox(cameraPosition, rayDirection, _VolumeBoundsMin, _VolumeBoundsMax, tEnter, tExit))
                    return source;

                float rawDepth = SAMPLE_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture, input.texcoord).r;
                bool hasSceneDepth;
                #if UNITY_REVERSED_Z
                    hasSceneDepth = rawDepth > 0.0001;
                #else
                    hasSceneDepth = rawDepth < 0.9999;
                #endif

                if (hasSceneDepth)
                {
                    float3 scenePosition = ReconstructWorldPosition(input.ndc, rawDepth, inverseViewProj);
                    float sceneT = dot(scenePosition - cameraPosition, rayDirection);
                    if (sceneT > 0.0)
                        tExit = min(tExit, sceneT);
                }

                if (tExit <= tEnter)
                    return source;

                float3 transmittance = 1.0;
                float3 inScatter = 0.0;
                float t = tEnter;
                int stepCount = 0;
                float3 viewDirection = -rayDirection;
                float3 lightDirection = normalize(_LightDirection);

                // _LightDirection points from the sample toward the light source.
                // The incident photon travels in the opposite direction (light -> sample).
                // viewDirection points from the sample toward the camera.
                float cosTheta = dot(viewDirection, -lightDirection);
                float phase = PhaseHG(cosTheta, _PhaseG) * 12.5663706;

                while (t < tExit && stepCount < _MaxRaySteps)
                {
                    float3 worldPosition = cameraPosition + rayDirection * t;
                    int desiredLOD = ChooseLOD(t);
                    int usedLOD;
                    bool hasBrick;
                    float density = SampleDensity(worldPosition, desiredLOD, usedLOD, hasBrick);

                    float voxelSize = _VoxelSizes[usedLOD];
                    float stepSize = max(voxelSize * _StepMultiplier, 0.25);
                    stepSize = min(stepSize, tExit - t);

                    if (hasBrick && density > 0.0)
                    {
                        // n_H is cm^-3 and stepSize is pc. _ExtinctionScale is
                        // calibrated optical depth per (cm^-3 * pc), in the V band.
                        float opticalDepth = max(density, 0.0) * stepSize * max(_ExtinctionScale, 0.0);
                        float3 tauStep = opticalDepth * max(_ExtinctionRGB, 0.0);
                        float3 stepTransmittance = exp(-tauStep);

                        // Approximate single-scattering source term. The global directional
                        // light is a placeholder; realistic Gaia-driven illumination requires
                        // a spatially varying incoming-light field.
                        float3 scatterStep = (1.0 - stepTransmittance)
                            * saturate(_ScatteringAlbedo)
                            * _ScatteringColor.rgb
                            * _LightColor.rgb
                            * phase
                            * max(_ScatteringStrength, 0.0);

                        inScatter += transmittance * scatterStep;
                        transmittance *= stepTransmittance;

                        float maxT = max(transmittance.r, max(transmittance.g, transmittance.b));
                        if (maxT < 0.001)
                            break;
                    }

                    t += stepSize;
                    stepCount++;
                }

                float3 finalColor = source.rgb * transmittance + inScatter;
                return float4(finalColor, source.a);
            }
            ENDHLSL
        }
    }
}
