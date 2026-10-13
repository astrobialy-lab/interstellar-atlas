Shader "Hidden/Dust/SpatialSprites"
{
    Properties
    {
        _CloudTexture ("Cloud Mask", 2D) = "white" {}
        _DustTint ("Dust Tint", Color) = (0.60, 0.48, 0.36, 1)
        _GlobalOpacity ("Global Opacity", Range(0, 3)) = 1.25
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
            CBUFFER_END

            struct DustSpriteData
            {
                float4 positionSize;
                float4 parameters;
            };

            StructuredBuffer<DustSpriteData> _DustSprites;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                // Explicit SV_InstanceID is required by Graphics.RenderMeshPrimitives.
                // Keep this field even in non-instancing shader variants.
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 localPosition : TEXCOORD1;
                float opacity : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                // Use Unity's decoded ID for single-pass-instanced XR, otherwise use
                // the direct SV_InstanceID supplied by RenderMeshPrimitives.
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
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float noise = SAMPLE_TEXTURE2D(
                    _CloudTexture, sampler_CloudTexture, input.uv).r;

                float cloudMask = smoothstep(0.16, 0.66, noise);
                float radiusSquared = dot(input.localPosition, input.localPosition);
                float edgeMask = pow(saturate(1.0 - radiusSquared), 0.95);
                float wispyMask = saturate(cloudMask * 1.10 + 0.12);

                // Exaggerated initial visibility; tune down after confirming it renders.
                float alpha = input.opacity * _GlobalOpacity * edgeMask * wispyMask * 2.0;
                alpha = saturate(alpha);

                if (alpha < 0.008)
                    discard;

                // Premultiplied-alpha blend.
                return half4(_DustTint.rgb * alpha, alpha);
            }
            ENDHLSL
        }
    }
}
