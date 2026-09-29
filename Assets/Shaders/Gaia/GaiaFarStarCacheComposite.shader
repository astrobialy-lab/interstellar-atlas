Shader "Hidden/GaiaFarStarCacheComposite"
{
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "GaiaFarStarCacheToHDR"

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
            // Accumulated far-cache buffers
            // ========================================================

            StructuredBuffer<uint> _CacheLuminance;
            StructuredBuffer<uint> _CacheColorLuminance;


            // ========================================================
            // Cache dimensions
            // ========================================================

            int _CacheWidth;
            int _CacheHeight;


            // ========================================================
            // Fixed-point reconstruction
            // ========================================================

            float _StorageScaleInv;
            float _FixedScale;


            // ========================================================
            // Colour range
            // ========================================================

            float _ColorMin;
            float _ColorMax;


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

                return output;
            }


            // ========================================================
            // Star colour reconstruction
            //
            // Keep this identical to GaiaStarComposite.shader so
            // cached far stars have the same colour model as live
            // stars.
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
            // Fragment
            // ========================================================

            half4 Frag(
                Varyings input
            ) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                uint2 pixel =
                    uint2(
                        input.positionCS.xy
                    );


                if (pixel.x >= (uint)_CacheWidth ||
                    pixel.y >= (uint)_CacheHeight)
                {
                    return 0;
                }


                uint index =
                    pixel.y *
                    (uint)_CacheWidth +
                    pixel.x;


                uint luminanceInteger =
                    _CacheLuminance[index];


                if (luminanceInteger == 0)
                {
                    return 0;
                }


                uint colorInteger =
                    _CacheColorLuminance[index];


                // ====================================================
                // Decode fixed-point luminance
                // ====================================================

                float luminance =
                    (
                        (float)luminanceInteger /
                        _FixedScale
                    )
                    *
                    _StorageScaleInv;


                // ====================================================
                // Recover luminance-weighted BP-RP
                // ====================================================

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


                // ====================================================
                // Reconstruct colour
                // ====================================================

                float3 starColor =
                    GetStarColor(
                        bpRp
                    );


                return half4(
                    starColor * luminance,
                    1.0
                );
            }

            ENDHLSL
        }
    }
}