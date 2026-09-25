Shader "Hidden/GaiaStarComposite"
{
    Properties
    {
        [HideInInspector]
        _GlareStrength("Glare Strength", Float) = 0.20

        [HideInInspector]
        _GlareThreshold("Glare Threshold", Float) = 1.0

        [HideInInspector]
        _GlareSoftness("Glare Softness", Float) = 2.0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
        }

        Pass
        {
            Name "GaiaStarComposite"

            Blend One One
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM

            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // ------------------------------------------------------------
            // Buffers
            // ------------------------------------------------------------

            StructuredBuffer<uint> _StarLuminance;
            StructuredBuffer<uint> _StarColorLuminance;

            // ------------------------------------------------------------
            // Parameters
            // ------------------------------------------------------------

            int _Width;
            int _Height;

            float _StorageScaleInv;
            float _FixedScale;

            float _ColorMin;
            float _ColorMax;

            float _GlareStrength;
            float _GlareThreshold;
            float _GlareSoftness;

            // ------------------------------------------------------------
            // Fullscreen triangle
            // ------------------------------------------------------------

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

                float2 uv;

                uv.x = (input.vertexID == 1) ? 2.0 : 0.0;
                uv.y = (input.vertexID == 2) ? 2.0 : 0.0;

                output.positionCS =
                    float4(
                        uv * 2.0 - 1.0,
                        0.0,
                        1.0
                    );

                return output;
            }

            // ------------------------------------------------------------
            // Pixel indexing
            // ------------------------------------------------------------

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

            // ------------------------------------------------------------
            // Star colour
            // ------------------------------------------------------------

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

            // ------------------------------------------------------------
            // Read one star pixel from the accumulation buffers.
            // ------------------------------------------------------------

            void ReadStarPixel(
                int2 pixel,
                uint eye,
                out float luminance,
                out float bpRp,
                out float3 color
            )
            {
                luminance = 0.0;
                bpRp = 0.0;
                color = 0.0;

                if (pixel.x < 0 ||
                    pixel.x >= _Width ||
                    pixel.y < 0 ||
                    pixel.y >= _Height)
                {
                    return;
                }

                uint index =
                    GetPixelIndex(
                        uint2(pixel),
                        eye
                    );

                uint luminanceInteger =
                    _StarLuminance[index];

                if (luminanceInteger == 0)
                    return;

                uint colorInteger =
                    _StarColorLuminance[index];

                float storedLuminance =
                    (float)luminanceInteger /
                    _FixedScale;

                luminance =
                    storedLuminance *
                    _StorageScaleInv;

                float bpRpNormalized =
                    (float)colorInteger /
                    (float)luminanceInteger;

                bpRp =
                    lerp(
                        _ColorMin,
                        _ColorMax,
                        saturate(bpRpNormalized)
                    );

                color =
                    GetStarColor(bpRp);
            }

            // ------------------------------------------------------------
            // How strongly this pixel contributes to glare.
            //
            // 0 = no glare
            // 1 = maximum glare contribution
            // ------------------------------------------------------------

            float GetGlareFactor(
                float luminance
            )
            {
                return
                    saturate(
                        (luminance - _GlareThreshold) /
                        max(
                            _GlareSoftness,
                            0.0001
                        )
                    );
            }

            // ------------------------------------------------------------
            // Add one glare sample.
            // ------------------------------------------------------------

            void AddGlareSample(
                int2 pixel,
                uint eye,
                float weight,
                inout float3 glare
            )
            {
                float luminance;
                float bpRp;
                float3 color;

                ReadStarPixel(
                    pixel,
                    eye,
                    luminance,
                    bpRp,
                    color
                );

                if (luminance <= 0.0)
                    return;

                float glareFactor =
                    GetGlareFactor(
                        luminance
                    );

                if (glareFactor <= 0.0)
                    return;

                glare +=
                    color *
                    luminance *
                    glareFactor *
                    weight;
            }

            // ------------------------------------------------------------
            // One ring of the glare kernel.
            //
            // Eight directions:
            //
            //       7   0   1
            //         \ | /
            //       6 - + - 2
            //         / | \
            //       5   4   3
            //
            // This gives us an approximately circular screen-space halo.
            // ------------------------------------------------------------

            void AddGlareRing(
                int2 center,
                uint eye,
                float radius,
                float ringWeight,
                inout float3 glare
            )
            {
                float sampleWeight =
                    ringWeight / 8.0;

                float c =
                    0.70710678;

                // Cardinal directions

                AddGlareSample(
                    center + int2(
                        (int)round(radius),
                        0
                    ),
                    eye,
                    sampleWeight,
                    glare
                );

                AddGlareSample(
                    center + int2(
                        -(int)round(radius),
                        0
                    ),
                    eye,
                    sampleWeight,
                    glare
                );

                AddGlareSample(
                    center + int2(
                        0,
                        (int)round(radius)
                    ),
                    eye,
                    sampleWeight,
                    glare
                );

                AddGlareSample(
                    center + int2(
                        0,
                        -(int)round(radius)
                    ),
                    eye,
                    sampleWeight,
                    glare
                );

                // Diagonal directions

                int diagonal =
                    (int)round(
                        radius * c
                    );

                AddGlareSample(
                    center + int2(
                        diagonal,
                        diagonal
                    ),
                    eye,
                    sampleWeight,
                    glare
                );

                AddGlareSample(
                    center + int2(
                        -diagonal,
                        diagonal
                    ),
                    eye,
                    sampleWeight,
                    glare
                );

                AddGlareSample(
                    center + int2(
                        diagonal,
                        -diagonal
                    ),
                    eye,
                    sampleWeight,
                    glare
                );

                AddGlareSample(
                    center + int2(
                        -diagonal,
                        -diagonal
                    ),
                    eye,
                    sampleWeight,
                    glare
                );
            }

            // ------------------------------------------------------------
            // Fullscreen fragment
            // ------------------------------------------------------------

            half4 Frag(Varyings input) :
                SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                uint eye =
                    (uint)unity_StereoEyeIndex;

                uint2 pixel =
                    uint2(
                        input.positionCS.xy
                    );

                // --------------------------------------------------------
                // Read the actual star at this pixel.
                // --------------------------------------------------------

                float centerLuminance;
                float centerBpRp;
                float3 centerColor;

                ReadStarPixel(
                    int2(pixel),
                    eye,
                    centerLuminance,
                    centerBpRp,
                    centerColor
                );

                // --------------------------------------------------------
                // Sharp star component.
                //
                // Bright stars donate a fraction of their energy to
                // the glare. Faint stars remain unchanged.
                // --------------------------------------------------------

                float centerGlareFactor =
                    GetGlareFactor(
                        centerLuminance
                    );

                float centerGlareShare =
                    centerGlareFactor *
                    _GlareStrength;

                float3 starColor =
                    centerColor *
                    centerLuminance *
                    (
                        1.0 -
                        centerGlareShare
                    );

                // --------------------------------------------------------
                // Glare component.
                //
                // Ring weights:
                //
                // 1/2
                // 1/4
                // 1/8
                // 1/16
                //
                // Normalized so the total is 1.
                // --------------------------------------------------------

                float3 glare = 0.0;

                const float weightSum =
                    0.5 +
                    0.25 +
                    0.125 +
                    0.0625;

                float w0 =
                    0.5 /
                    weightSum;

                float w1 =
                    0.25 /
                    weightSum;

                float w2 =
                    0.125 /
                    weightSum;

                float w3 =
                    0.0625 /
                    weightSum;

                AddGlareRing(
                    int2(pixel),
                    eye,
                    2.0,
                    w0,
                    glare
                );

                AddGlareRing(
                    int2(pixel),
                    eye,
                    4.0,
                    w1,
                    glare
                );

                AddGlareRing(
                    int2(pixel),
                    eye,
                    8.0,
                    w2,
                    glare
                );

                AddGlareRing(
                    int2(pixel),
                    eye,
                    16.0,
                    w3,
                    glare
                );

                glare *=
                    _GlareStrength;

                // --------------------------------------------------------
                // Final star image.
                // --------------------------------------------------------

                float3 result =
                    starColor +
                    glare;

                return half4(
                    result,
                    0.0
                );
            }

            ENDHLSL
        }
    }
}