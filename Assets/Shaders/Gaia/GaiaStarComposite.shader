Shader "Hidden/GaiaStarComposite"
{
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "GaiaStarBufferToHDR"

            Blend One Zero
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM

            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"


            // ========================================================
            // Live star accumulation
            // ========================================================

            StructuredBuffer<uint> _StarLuminance;
            StructuredBuffer<uint> _StarColorLuminance;


            int _Width;
            int _Height;


            float _StorageScaleInv;
            float _FixedScale;


            float _ColorMin;
            float _ColorMax;


            // ========================================================
            // Far-star panorama
            // ========================================================

            TEXTURE2D(_FarStarCache);
            SAMPLER(sampler_FarStarCache);

            float _FarStarCacheValid;

            float _FarStarCacheOnly;


            // ========================================================
            // Per-eye camera reconstruction
            // ========================================================

            float4x4 _InvViewProj0;
            float4x4 _InvViewProj1;

            float4 _EyePosition0;
            float4 _EyePosition1;


            // ========================================================
            // Fullscreen triangle
            // ========================================================

            struct Attributes
            {
                uint vertexID : SV_VertexID;

                UNITY_VERTEX_INPUT_INSTANCE_ID
            };


            struct Varyings
            {
                float4 positionCS : SV_POSITION;

                float2 ndc : TEXCOORD0;

                UNITY_VERTEX_OUTPUT_STEREO
            };


            Varyings Vert(
                Attributes input
            )
            {
                Varyings output;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);


                float2 uv;

                uv.x =
                    input.vertexID == 1
                    ? 2.0
                    : 0.0;

                uv.y =
                    input.vertexID == 2
                    ? 2.0
                    : 0.0;


                output.positionCS =
                    float4(
                        uv * 2.0 - 1.0,
                        0.0,
                        1.0
                    );


                output.ndc =
                    output.positionCS.xy;


                return output;
            }


            // ========================================================
            // Live star pixel indexing
            // ========================================================

            uint GetPixelIndex(
                uint2 pixel,
                uint eye
            )
            {
                return
                    (eye * (uint)_Height + pixel.y)
                    * (uint)_Width
                    + pixel.x;
            }


            // ========================================================
            // Star colour
            // ========================================================

            float3 GetStarColor(
                float bpRp
            )
            {
                float3 blue =
                    float3(
                        0.55,
                        0.70,
                        1.00
                    );


                float3 white =
                    float3(
                        1.00,
                        1.00,
                        1.00
                    );


                float3 orange =
                    float3(
                        1.00,
                        0.55,
                        0.25
                    );


                if (bpRp < 0.8)
                {
                    float t =
                        saturate(
                            (bpRp + 0.5) / 1.3
                        );


                    return lerp(
                        blue,
                        white,
                        t
                    );
                }


                float t =
                    saturate(
                        (bpRp - 0.8) / 2.0
                    );


                return lerp(
                    white,
                    orange,
                    t
                );
            }


            // ========================================================
            // World ray reconstruction
            // ========================================================

            float3 ReconstructWorldRay(
                float2 ndc,
                float4x4 inverseViewProj,
                float3 eyePosition
            )
            {
                float4 clip =
                    float4(
                        ndc.x,
                        ndc.y,
                        1.0,
                        1.0
                    );


                float4 worldH =
                    mul(
                        inverseViewProj,
                        clip
                    );


                if (abs(worldH.w) < 1e-6)
                {
                    return float3(
                        0.0,
                        0.0,
                        1.0
                    );
                }


                float3 worldPosition =
                    worldH.xyz /
                    worldH.w;


                return normalize(
                    worldPosition -
                    eyePosition
                );
            }


            // ========================================================
            // Equirectangular lookup
            // ========================================================

            float3 SampleFarStarCache(
                float3 worldDirection
            )
            {
                float longitude =
                    atan2(
                        worldDirection.x,
                        worldDirection.z
                    );


                float latitude =
                    asin(
                        clamp(
                            worldDirection.y,
                            -1.0,
                            1.0
                        )
                    );


                float2 uv;


                uv.x =
                    longitude /
                    (2.0 * PI)
                    + 0.5;


                uv.y =
                    latitude /
                    PI
                    + 0.5;


                uv.x =
                    frac(
                        uv.x
                    );


                uv.y =
                    saturate(
                        uv.y
                    );


                return
                    SAMPLE_TEXTURE2D(
                        _FarStarCache,
                        sampler_FarStarCache,
                        uv
                    ).rgb;
            }


            // ========================================================
            // Fragment
            // ========================================================

            half4 Frag(
                Varyings input
            ) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);


                uint eye =
                    (uint)unity_StereoEyeIndex;


                uint2 pixel =
                    uint2(
                        input.positionCS.xy
                    );


                if (pixel.x >= (uint)_Width ||
                    pixel.y >= (uint)_Height)
                {
                    return 0;
                }


                // ====================================================
                // Live stars
                //
                // Completely disabled in far-cache-only mode.
                // ====================================================

                float3 liveHDR =
                    float3(
                        0.0,
                        0.0,
                        0.0
                    );


                if (_FarStarCacheOnly < 0.5)
                {
                    uint index =
                        GetPixelIndex(
                            pixel,
                            eye
                        );


                    uint luminanceInteger =
                        _StarLuminance[index];


                    if (luminanceInteger > 0)
                    {
                        uint colorInteger =
                            _StarColorLuminance[index];


                        float luminance =
                            (
                                (float)luminanceInteger /
                                _FixedScale
                            )
                            *
                            _StorageScaleInv;


                        float bpRpNormalized =
                            (float)colorInteger /
                            (float)luminanceInteger;


                        float bpRp =
                            lerp(
                                _ColorMin,
                                _ColorMax,
                                saturate(
                                    bpRpNormalized
                                )
                            );


                        float3 starColor =
                            GetStarColor(
                                bpRp
                            );


                        liveHDR =
                            starColor *
                            luminance;
                    }
                }


                // ====================================================
                // Far panorama
                // ====================================================

                float3 farHDR =
                    float3(
                        0.0,
                        0.0,
                        0.0
                    );


                if (_FarStarCacheValid > 0.5)
                {
                    float4 eyePosition4 =
                        eye == 0
                        ? _EyePosition0
                        : _EyePosition1;


                    float4x4 inverseViewProj =
                        eye == 0
                        ? _InvViewProj0
                        : _InvViewProj1;


                    float3 worldDirection =
                        ReconstructWorldRay(
                            input.ndc,
                            inverseViewProj,
                            eyePosition4.xyz
                        );


                    farHDR =
                        SampleFarStarCache(
                            worldDirection
                        );
                }


                // ====================================================
                // Final star HDR
                // ====================================================

                return half4(
                    liveHDR +
                    farHDR,
                    1.0
                );
            }

            ENDHLSL
        }
    }
}