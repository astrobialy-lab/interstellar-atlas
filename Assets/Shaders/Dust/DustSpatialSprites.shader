Shader "Hidden/Dust/SpatialSprites"
{
    Properties
    {
        _CloudTexture ("Cloud / Sprite Texture", 2D) = "white" {}
        _DustTint ("Sprite Tint", Color) = (1, 1, 1, 1)
        _GlobalOpacity ("Global Opacity", Range(0, 3)) = 1.25
        _UseCustomSpriteTexture ("Use Custom Sprite Texture", Float) = 0
        _PlayerPosition ("Player Position", Vector) = (0, 0, 0, 1)
        _ColorScheme ("Color Scheme", Float) = 0
        _SpriteSizeMultiplier ("Sprite Size Multiplier", Range(0.4, 2.0)) = 2.0
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
                float4 _PlayerPosition;
                float _ColorScheme;
                float _SpriteSizeMultiplier;
            CBUFFER_END

            struct DustSpriteData
            {
                float4 positionSize;
                // x = opacity, y = rotation, z = aspect, w = visual density.
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
                nointerpolation float3 densityColor : TEXCOORD3;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float3 MagmaReversed(float value)
            {
                float t = saturate(value);

                if (t < 0.125)
                    return lerp(float3(0.988, 0.992, 0.749),
                                float3(0.996, 0.788, 0.553), t / 0.125);

                if (t < 0.250)
                    return lerp(float3(0.996, 0.788, 0.553),
                                float3(0.992, 0.588, 0.408),
                                (t - 0.125) / 0.125);

                if (t < 0.375)
                    return lerp(float3(0.992, 0.588, 0.408),
                                float3(0.945, 0.376, 0.365),
                                (t - 0.250) / 0.125);

                if (t < 0.500)
                    return lerp(float3(0.945, 0.376, 0.365),
                                float3(0.804, 0.251, 0.443),
                                (t - 0.375) / 0.125);

                if (t < 0.625)
                    return lerp(float3(0.804, 0.251, 0.443),
                                float3(0.620, 0.184, 0.498),
                                (t - 0.500) / 0.125);

                if (t < 0.750)
                    return lerp(float3(0.620, 0.184, 0.498),
                                float3(0.447, 0.122, 0.506),
                                (t - 0.625) / 0.125);

                if (t < 0.875)
                    return lerp(float3(0.447, 0.122, 0.506),
                                float3(0.267, 0.059, 0.463),
                                (t - 0.750) / 0.125);

                return lerp(float3(0.267, 0.059, 0.463),
                            float3(0.000, 0.000, 0.016),
                            (t - 0.875) / 0.125);
            }

            float3 ThreeColorGradient(
                float t,
                float3 low,
                float3 mid,
                float3 high)
            {
                if (t < 0.5)
                    return lerp(low, mid, t * 2.0);

                return lerp(mid, high, (t - 0.5) * 2.0);
            }

            float3 ColorFromScheme(float value, int scheme)
            {
                float t = saturate(value);

                switch (scheme)
                {
                    // Preserve the shader's current appearance.
                    case 0:
                        return MagmaReversed(t);

                    // Yellow/orange -> red -> purple.
                    case 1:
                        return ThreeColorGradient(
                            t,
                            float3(0.98, 0.85, 0.35),
                            float3(0.85, 0.35, 0.35),
                            float3(0.20, 0.05, 0.35));

                    // Blue -> cyan -> yellow.
                    case 2:
                        return ThreeColorGradient(
                            t,
                            float3(0.05, 0.20, 0.90),
                            float3(0.10, 0.90, 1.00),
                            float3(1.00, 0.85, 0.25));

                    // Green -> yellow -> orange.
                    case 3:
                        return ThreeColorGradient(
                            t,
                            float3(0.10, 0.45, 0.25),
                            float3(0.75, 0.85, 0.35),
                            float3(1.00, 0.45, 0.10));

                    // Grayscale.
                    case 4:
                        return float3(t, t, t);

                    // Blue -> white -> red.
                    case 5:
                        return ThreeColorGradient(
                            t,
                            float3(0.10, 0.25, 1.00),
                            float3(0.95, 0.95, 0.95),
                            float3(1.00, 0.20, 0.10));

                    default:
                        return MagmaReversed(t);
                }
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
                float sizePc = sprite.positionSize.w * _SpriteSizeMultiplier;

                float spriteOpacity = sprite.parameters.x;
                float rotation = sprite.parameters.y;
                float aspect = sprite.parameters.z;
                float densityVisual = sprite.parameters.w;

                // Construct the sprite's quad in its own 2D plane.
                float2 corner = input.positionOS.xy * sizePc;
                corner.y *= aspect;

                float sineAngle;
                float cosineAngle;

                sincos(rotation, sineAngle, cosineAngle);

                corner = float2(
                    corner.x * cosineAngle - corner.y * sineAngle,
                    corner.x * sineAngle + corner.y * cosineAngle
                );

                // Face the player's POSITION, not the direction they are looking.
                // Each sprite has its own facing direction because each has a
                // different world-space position.
                float3 toPlayer = _PlayerPosition.xyz - centerWS;
                float distanceSquared = dot(toPlayer, toPlayer);

                // Avoid normalizing a zero-length vector.
                float3 forward;

                if (distanceSquared > 1e-8)
                    forward = toPlayer * rsqrt(distanceSquared);
                else
                    forward = float3(0.0, 0.0, 1.0);

                // Keep a stable upright reference where possible.
                float3 worldUp = float3(0.0, 1.0, 0.0);
                float3 right = cross(worldUp, forward);

                // When looking almost straight up or down, the first cross
                // product approaches zero. Use another reference direction.
                float rightLengthSquared = dot(right, right);

                if (rightLengthSquared < 1e-6)
                {
                    right = cross(float3(0.0, 0.0, 1.0), forward);
                    rightLengthSquared = dot(right, right);
                }

                right *= rsqrt(max(rightLengthSquared, 1e-8));

                // Complete the orthonormal basis.
                float3 up = cross(forward, right);

                float3 positionWS =
                    centerWS
                    + right * corner.x
                    + up * corner.y;

                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = input.uv;
                output.localPosition = input.positionOS.xy * 2.0;
                output.opacity = spriteOpacity;
                int scheme = (int)(_ColorScheme + 0.5);
                output.densityColor = ColorFromScheme(densityVisual, scheme);

                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float4 texel = SAMPLE_TEXTURE2D(
                    _CloudTexture,
                    sampler_CloudTexture,
                    input.uv
                );

                float shapeMask;
                float3 textureColor = 1.0;

                if (_UseCustomSpriteTexture > 0.5)
                {
                    shapeMask = texel.a;
                    textureColor = texel.rgb;
                }
                else
                {
                    float cloudMask = smoothstep(0.16, 0.66, texel.r);

                    float radiusSquared =
                        dot(input.localPosition, input.localPosition);

                    float edgeMask =
                        pow(saturate(1.0 - radiusSquared), 0.95);

                    float wispyMask =
                        saturate(cloudMask * 1.10 + 0.12);

                    shapeMask = edgeMask * wispyMask;
                }

                float alpha = saturate(
                    shapeMask
                    * input.opacity
                    * _GlobalOpacity
                    * _DustTint.a
                );

                if (alpha < 0.008)
                    discard;

                float3 rgb =
                    input.densityColor * textureColor * _DustTint.rgb;

                // Premultiplied-alpha blending.
                return half4(rgb * alpha, alpha);
            }
            ENDHLSL
        }
    }
}
