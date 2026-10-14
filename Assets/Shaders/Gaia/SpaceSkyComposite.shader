Shader "Hidden/SpaceSkyComposite"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "FinalComposite"
            ZTest Always
            ZWrite Off
            Cull Off
            Blend One Zero

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D_X(_StarHDR);
            TEXTURE2D_X(_Glare0);
            TEXTURE2D_X(_Glare1);
            TEXTURE2D_X(_Glare2);
            TEXTURE2D_X(_Glare3);
            TEXTURE2D_X(_Glare4);

            float _GlareStrength;

            float2 GetTexelSize(TEXTURE2D_X_PARAM(tex, samp))
            {
                float width;
                float height;

                #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
                    float slices;
                    tex.GetDimensions(width, height, slices);
                #else
                    tex.GetDimensions(width, height);
                #endif

                return 1.0 / float2(width, height);
            }

            // Four bilinear samples efficiently approximate a bicubic reconstruction.
            float3 SampleBicubic(TEXTURE2D_X_PARAM(tex, samp), float2 uv)
            {
                float2 texelSize = GetTexelSize(TEXTURE2D_X_ARGS(tex, samp));
                float2 texSize = 1.0 / texelSize;
                float2 xy = uv * texSize + 0.5;
                float2 ic = floor(xy);
                float2 fc = frac(xy);

                float2 f2 = fc * fc;
                float2 f3 = f2 * fc;

                float2 w0 = -0.5 * f3 + f2 - 0.5 * fc;
                float2 w1 = 1.5 * f3 - 2.5 * f2 + 1.0;
                float2 w2 = -1.5 * f3 + 2.0 * f2 + 0.5 * fc;
                float2 w3 = 0.5 * f3 - 0.5 * f2;

                float2 g0 = max(w0 + w1, 1e-5);
                float2 g1 = max(w2 + w3, 1e-5);
                float2 h0 = w1 / g0 - 0.5;
                float2 h1 = w3 / g1 + 1.5;

                float2 uv00 = (ic + float2(h0.x, h0.y) - 0.5) * texelSize;
                float2 uv10 = (ic + float2(h1.x, h0.y) - 0.5) * texelSize;
                float2 uv01 = (ic + float2(h0.x, h1.y) - 0.5) * texelSize;
                float2 uv11 = (ic + float2(h1.x, h1.y) - 0.5) * texelSize;

                float3 c00 = SAMPLE_TEXTURE2D_X(tex, samp, uv00).rgb;
                float3 c10 = SAMPLE_TEXTURE2D_X(tex, samp, uv10).rgb;
                float3 c01 = SAMPLE_TEXTURE2D_X(tex, samp, uv01).rgb;
                float3 c11 = SAMPLE_TEXTURE2D_X(tex, samp, uv11).rgb;

                return c00 * g0.x * g0.y
                     + c10 * g1.x * g0.y
                     + c01 * g0.x * g1.y
                     + c11 * g1.x * g1.y;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 camera = SAMPLE_TEXTURE2D_X(
                    _BlitTexture, sampler_LinearClamp, input.texcoord).rgb;

                float3 stars = SAMPLE_TEXTURE2D_X(
                    _StarHDR, sampler_LinearClamp, input.texcoord).rgb;

                float3 glare = 0.0;
                glare += SampleBicubic(TEXTURE2D_X_ARGS(_Glare0, sampler_LinearClamp), input.texcoord) * 1.0;
                glare += SampleBicubic(TEXTURE2D_X_ARGS(_Glare1, sampler_LinearClamp), input.texcoord) * 0.5;
                glare += SampleBicubic(TEXTURE2D_X_ARGS(_Glare2, sampler_LinearClamp), input.texcoord) * 0.25;
                glare += SampleBicubic(TEXTURE2D_X_ARGS(_Glare3, sampler_LinearClamp), input.texcoord) * 0.125;
                glare += SampleBicubic(TEXTURE2D_X_ARGS(_Glare4, sampler_LinearClamp), input.texcoord) * 0.0625;

                return float4(camera + stars + glare * _GlareStrength, 1.0);
            }
            ENDHLSL
        }
    }
}
