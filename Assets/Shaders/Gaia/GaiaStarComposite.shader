Shader "Hidden/GaiaStarComposite"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One One

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            StructuredBuffer<uint> _StarLuminance;
            StructuredBuffer<uint> _StarColorLuminance;

            uint _Width;
            uint _Height;

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

            Varyings Vert(Attributes input)
            {
                Varyings output;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float2 position;

                position.x = input.vertexID == 2 ? 3.0 : -1.0;
                position.y = input.vertexID == 1 ? 3.0 : -1.0;

                output.positionCS = float4(position, 0.0, 1.0);

                return output;
            }

            float3 GetStarColor(float bpRp)
            {
                float3 blue = float3(0.55, 0.70, 1.00);
                float3 white = float3(1.00, 1.00, 1.00);
                float3 orange = float3(1.00, 0.55, 0.25);

                if (bpRp < 0.8)
                {
                    float t = saturate((bpRp + 0.5) / 1.3);
                    return lerp(blue, white, t);
                }

                float t = saturate((bpRp - 0.8) / 2.0);
                return lerp(white, orange, t);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                uint eye = unity_StereoEyeIndex;
                uint2 pixel = uint2(input.positionCS.xy);

                if (pixel.x >= _Width || pixel.y >= _Height)
                    return 0;

                uint pixelIndex = (eye * _Height + pixel.y) * _Width + pixel.x;

                uint luminanceInteger = _StarLuminance[pixelIndex];

                if (luminanceInteger == 0)
                    return 0;

                uint colorInteger = _StarColorLuminance[pixelIndex];

                if (colorInteger == 0)
                    return 0;

                float storedLuminance = (float)luminanceInteger / _FixedScale;
                float luminance = storedLuminance * _StorageScaleInv;

                float bpRpNormalized = (float)colorInteger / (float)luminanceInteger;

                float bpRp = lerp(_ColorMin, _ColorMax, saturate(bpRpNormalized));

                float3 starColor = GetStarColor(bpRp);

                return half4(starColor * luminance, 0.0);
            }

            ENDHLSL
        }
    }
}