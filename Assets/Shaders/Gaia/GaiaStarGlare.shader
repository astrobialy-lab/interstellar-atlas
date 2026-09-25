Shader "Hidden/GaiaStarGlare"
{
    Properties
    {
        _GlareThreshold("Glare Threshold", Float) = 1.0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
        }


        // ================================================================
        // PASS 0 - Prefilter
        // ================================================================

        Pass
        {
            Name "Prefilter"

            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment FragPrefilter

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _GlareThreshold;


            float4 FragPrefilter(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 color =
                    SAMPLE_TEXTURE2D_X(
                        _BlitTexture,
                        sampler_LinearClamp,
                        input.texcoord
                    ).rgb;

                float brightness =
                    max(
                        max(color.r, color.g),
                        color.b
                    );

                float contribution =
                    max(
                        brightness - _GlareThreshold,
                        0.0
                    );

                contribution =
                    contribution /
                    max(
                        brightness,
                        1e-6
                    );

                return float4(
                    color * contribution,
                    1.0
                );
            }

            ENDHLSL
        }


        // ================================================================
        // PASS 1 - Downsample
        // ================================================================

        Pass
        {
            Name "Downsample"

            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment FragDownsample

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"


            float4 FragDownsample(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 texel =
                    _BlitTexture_TexelSize.xy;

                float3 c0 =
                    SAMPLE_TEXTURE2D_X(
                        _BlitTexture,
                        sampler_LinearClamp,
                        input.texcoord +
                        texel * float2(-0.5, -0.5)
                    ).rgb;

                float3 c1 =
                    SAMPLE_TEXTURE2D_X(
                        _BlitTexture,
                        sampler_LinearClamp,
                        input.texcoord +
                        texel * float2(0.5, -0.5)
                    ).rgb;

                float3 c2 =
                    SAMPLE_TEXTURE2D_X(
                        _BlitTexture,
                        sampler_LinearClamp,
                        input.texcoord +
                        texel * float2(-0.5, 0.5)
                    ).rgb;

                float3 c3 =
                    SAMPLE_TEXTURE2D_X(
                        _BlitTexture,
                        sampler_LinearClamp,
                        input.texcoord +
                        texel * float2(0.5, 0.5)
                    ).rgb;

                return float4(
                    (c0 + c1 + c2 + c3) * 0.25,
                    1.0
                );
            }

            ENDHLSL
        }


        // ================================================================
        // PASS 2 - Horizontal perspective-correct Gaussian
        // ================================================================

        Pass
        {
            Name "HorizontalGaussian"

            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment FragHorizontalGaussian

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"


            float3 ViewRayFromUV(float2 uv)
            {
                float2 ndc =
                    uv * 2.0 - 1.0;

                float4 clipPosition =
                    float4(
                        ndc.x,
                        ndc.y,
                        1.0,
                        1.0
                    );

                float4 viewPosition =
                    mul(
                        UNITY_MATRIX_I_P,
                        clipPosition
                    );

                return normalize(
                    viewPosition.xyz /
                    viewPosition.w
                );
            }


            float2 ViewRayToUV(float3 ray)
            {
                float4 clipPosition =
                    mul(
                        UNITY_MATRIX_P,
                        float4(
                            ray,
                            1.0
                        )
                    );

                float2 ndc =
                    clipPosition.xy /
                    clipPosition.w;

                return ndc * 0.5 + 0.5;
            }


            float3 SampleViewSpaceGaussian(
                float2 uv,
                float2 axis,
                float offset
            )
            {
                float3 ray =
                    ViewRayFromUV(uv);


                float width =
                    max(
                        _BlitTexture_TexelSize.z,
                        1.0
                    );

                float height =
                    max(
                        _BlitTexture_TexelSize.w,
                        1.0
                    );


                float horizontalStep =
                    2.0 /
                    (
                        width *
                        abs(UNITY_MATRIX_P._m00)
                    );

                float verticalStep =
                    2.0 /
                    (
                        height *
                        abs(UNITY_MATRIX_P._m11)
                    );


                float2 step =
                    axis *
                    float2(
                        horizontalStep,
                        verticalStep
                    );


                float3 offsetRay =
                    normalize(
                        ray +
                        float3(
                            step.x * offset,
                            step.y * offset,
                            0.0
                        )
                    );


                float2 sampleUV =
                    ViewRayToUV(
                        offsetRay
                    );


                if (
                    sampleUV.x < 0.0 ||
                    sampleUV.x > 1.0 ||
                    sampleUV.y < 0.0 ||
                    sampleUV.y > 1.0
                )
                {
                    return 0.0;
                }


                return
                    SAMPLE_TEXTURE2D_X(
                        _BlitTexture,
                        sampler_LinearClamp,
                        sampleUV
                    ).rgb;
            }


            float4 FragHorizontalGaussian(
                Varyings input
            ) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv =
                    input.texcoord;


                float3 result =
                    SampleViewSpaceGaussian(
                        uv,
                        float2(1.0, 0.0),
                        0.0
                    )
                    * 0.2270270270;


                result +=
                    SampleViewSpaceGaussian(
                        uv,
                        float2(1.0, 0.0),
                        1.3846153846
                    )
                    * 0.3162162162;


                result +=
                    SampleViewSpaceGaussian(
                        uv,
                        float2(1.0, 0.0),
                        -1.3846153846
                    )
                    * 0.3162162162;


                result +=
                    SampleViewSpaceGaussian(
                        uv,
                        float2(1.0, 0.0),
                        3.2307692308
                    )
                    * 0.0702702703;


                result +=
                    SampleViewSpaceGaussian(
                        uv,
                        float2(1.0, 0.0),
                        -3.2307692308
                    )
                    * 0.0702702703;


                return float4(
                    result,
                    1.0
                );
            }

            ENDHLSL
        }


        // ================================================================
        // PASS 3 - Vertical perspective-correct Gaussian
        // ================================================================

        Pass
        {
            Name "VerticalGaussian"

            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment FragVerticalGaussian

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"


            float3 ViewRayFromUV(float2 uv)
            {
                float2 ndc =
                    uv * 2.0 - 1.0;

                float4 clipPosition =
                    float4(
                        ndc.x,
                        ndc.y,
                        1.0,
                        1.0
                    );

                float4 viewPosition =
                    mul(
                        UNITY_MATRIX_I_P,
                        clipPosition
                    );

                return normalize(
                    viewPosition.xyz /
                    viewPosition.w
                );
            }


            float2 ViewRayToUV(float3 ray)
            {
                float4 clipPosition =
                    mul(
                        UNITY_MATRIX_P,
                        float4(
                            ray,
                            1.0
                        )
                    );

                float2 ndc =
                    clipPosition.xy /
                    clipPosition.w;

                return ndc * 0.5 + 0.5;
            }


            float3 SampleViewSpaceGaussian(
                float2 uv,
                float2 axis,
                float offset
            )
            {
                float3 ray =
                    ViewRayFromUV(uv);


                float width =
                    max(
                        _BlitTexture_TexelSize.z,
                        1.0
                    );

                float height =
                    max(
                        _BlitTexture_TexelSize.w,
                        1.0
                    );


                float horizontalStep =
                    2.0 /
                    (
                        width *
                        abs(UNITY_MATRIX_P._m00)
                    );

                float verticalStep =
                    2.0 /
                    (
                        height *
                        abs(UNITY_MATRIX_P._m11)
                    );


                float2 step =
                    axis *
                    float2(
                        horizontalStep,
                        verticalStep
                    );


                float3 offsetRay =
                    normalize(
                        ray +
                        float3(
                            step.x * offset,
                            step.y * offset,
                            0.0
                        )
                    );


                float2 sampleUV =
                    ViewRayToUV(
                        offsetRay
                    );


                if (
                    sampleUV.x < 0.0 ||
                    sampleUV.x > 1.0 ||
                    sampleUV.y < 0.0 ||
                    sampleUV.y > 1.0
                )
                {
                    return 0.0;
                }


                return
                    SAMPLE_TEXTURE2D_X(
                        _BlitTexture,
                        sampler_LinearClamp,
                        sampleUV
                    ).rgb;
            }


            float4 FragVerticalGaussian(
                Varyings input
            ) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv =
                    input.texcoord;


                float3 result =
                    SampleViewSpaceGaussian(
                        uv,
                        float2(0.0, 1.0),
                        0.0
                    )
                    * 0.2270270270;


                result +=
                    SampleViewSpaceGaussian(
                        uv,
                        float2(0.0, 1.0),
                        1.3846153846
                    )
                    * 0.3162162162;


                result +=
                    SampleViewSpaceGaussian(
                        uv,
                        float2(0.0, 1.0),
                        -1.3846153846
                    )
                    * 0.3162162162;


                result +=
                    SampleViewSpaceGaussian(
                        uv,
                        float2(0.0, 1.0),
                        3.2307692308
                    )
                    * 0.0702702703;


                result +=
                    SampleViewSpaceGaussian(
                        uv,
                        float2(0.0, 1.0),
                        -3.2307692308
                    )
                    * 0.0702702703;


                return float4(
                    result,
                    1.0
                );
            }

            ENDHLSL
        }
    }
}