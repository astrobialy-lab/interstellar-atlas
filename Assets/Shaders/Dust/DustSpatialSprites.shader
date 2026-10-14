Shader "Hidden/Dust/SpatialSprites"
{
    Properties
    {
        _CloudTexture ("Cloud / Sprite Texture", 2D) = "white" {}
        _DustTint ("Sprite Tint", Color) = (1, 1, 1, 1)
        _GlobalOpacity ("Global Opacity", Range(0, 3)) = 1.25
        _UseCustomSpriteTexture ("Use Custom Sprite Texture", Float) = 0
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
            Name "SpatialDustSprites"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_CloudTexture);
            SAMPLER(sampler_CloudTexture);

            CBUFFER_START(UnityPerMaterial)
                float4 _DustTint;
                float _GlobalOpacity;
                float _UseCustomSpriteTexture;
            CBUFFER_END

            struct DustSpriteData
            {
                float4 positionSize;
                // x = opacity, y = rotation, z = aspect, w = Gaussian-remapped visual density.
                float4 parameters;
            };

            StructuredBuffer<DustSpriteData> _DustSprites;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 localPosition : TEXCOORD1;
                float opacity : TEXCOORD2;
                float densityVisual : TEXCOORD3;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // Approximation of Matplotlib's magma_r palette, sampled from pale yellow
            // at density_visual=0 through orange/pink/purple to near-black at 1.
            float3 MagmaReversed(float value)
            {
                float t = saturate(value);
                if (t < 0.125)
                    return lerp(float3(0.988, 0.992, 0.749), float3(0.996, 0.788, 0.553), t / 0.125);
                if (t < 0.250)
                    return lerp(float3(0.996, 0.788, 0.553), float3(0.992, 0.588, 0.408), (t - 0.125) / 0.125);
                if (t < 0.375)
                    return lerp(float3(0.992, 0.588, 0.408), float3(0.945, 0.376, 0.365), (t - 0.250) / 0.125);
                if (t < 0.500)
                    return lerp(float3(0.945, 0.376, 0.365), float3(0.804, 0.251, 0.443), (t - 0.375) / 0.125);
                if (t < 0.625)
                    return lerp(float3(0.804, 0.251, 0.443), float3(0.620, 0.184, 0.498), (t - 0.500) / 0.125);
                if (t < 0.750)
                    return lerp(float3(0.620, 0.184, 0.498), float3(0.447, 0.122, 0.506), (t - 0.625) / 0.125);
                if (t < 0.875)
                    return lerp(float3(0.447, 0.122, 0.506), float3(0.267, 0.059, 0.463), (t - 0.750) / 0.125);
                return lerp(float3(0.267, 0.059, 0.463), float3(0.000, 0.000, 0.016), (t - 0.875) / 0.125);
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                uint spriteIndex = input.instanceID;
                #if defined(UNITY_INSTANCING_ENABLED)
                    spriteIndex = unity_InstanceID;
                #endif

                DustSpriteData sprite = _DustSprites[spriteIndex];
                float3 centerWS = sprite.positionSize.xyz;
                float sizePc = sprite.positionSize.w;
                float spriteOpacity = sprite.parameters.x;
                float rotation = sprite.parameters.y;
                float aspect = sprite.parameters.z;
                float densityVisual = sprite.parameters.w;

                float2 corner = input.positionOS.xy * sizePc;
                corner.y *= aspect;

                float sineAngle;
                float cosineAngle;
                sincos(rotation, sineAngle, cosineAngle);
                corner = float2(
                    corner.x * cosineAngle - corner.y * sineAngle,
                    corner.x * sineAngle + corner.y * cosineAngle);

                float3 cameraRight = normalize(float3(
                    UNITY_MATRIX_I_V._m00,
                    UNITY_MATRIX_I_V._m10,
                    UNITY_MATRIX_I_V._m20));

                float3 cameraUp = normalize(float3(
                    UNITY_MATRIX_I_V._m01,
                    UNITY_MATRIX_I_V._m11,
                    UNITY_MATRIX_I_V._m21));

                float3 positionWS = centerWS
                    + cameraRight * corner.x
                    + cameraUp * corner.y;

                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = input.uv;
                output.localPosition = input.positionOS.xy * 2.0;
                output.opacity = spriteOpacity;
                output.densityVisual = densityVisual;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float4 texel = SAMPLE_TEXTURE2D(
                    _CloudTexture, sampler_CloudTexture, input.uv);

                float shapeMask;
                float3 textureColor = 1.0;

                if (_UseCustomSpriteTexture > 0.5)
                {
                    // A custom PNG supplies the silhouette through alpha. Its RGB gently
                    // modulates the density palette, so a white sprite shows the palette
                    // unchanged while a colored sprite can tint it.
                    shapeMask = texel.a;
                    textureColor = texel.rgb;
                }
                else
                {
                    // Preserve the procedural cloud mask used by the original renderer.
                    float cloudMask = smoothstep(0.16, 0.66, texel.r);
                    float radiusSquared = dot(input.localPosition, input.localPosition);
                    float edgeMask = pow(saturate(1.0 - radiusSquared), 0.95);
                    float wispyMask = saturate(cloudMask * 1.10 + 0.12);
                    shapeMask = edgeMask * wispyMask;
                }

                float alpha = saturate(
                    shapeMask * input.opacity * _GlobalOpacity * _DustTint.a);
                if (alpha < 0.008)
                    discard;

                float3 densityColor = MagmaReversed(input.densityVisual);
                float3 rgb = densityColor * textureColor * _DustTint.rgb;

                // Premultiplied-alpha blend (Blend One OneMinusSrcAlpha).
                return half4(rgb * alpha, alpha);
            }
            ENDHLSL
        }
    }
}
