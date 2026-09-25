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

            StructuredBuffer<uint> _StarLuminance;
            StructuredBuffer<uint> _StarColorLuminance;

            int _Width;
            int _Height;

            float _StorageScaleInv;
            float _FixedScale;

            float _ColorMin;
            float _ColorMax;


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

                uint index =
                    GetPixelIndex(
                        pixel,
                        eye
                    );

                uint luminanceInteger =
                    _StarLuminance[index];

                if (luminanceInteger == 0)
                {
                    return 0;
                }

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
                        saturate(bpRpNormalized)
                    );

                float3 starColor =
                    GetStarColor(bpRp);

                return half4(
                    starColor * luminance,
                    1.0
                );
            }

            ENDHLSL
        }
    }
}