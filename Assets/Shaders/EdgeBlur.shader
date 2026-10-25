Shader "RocketWorld/EdgeBlur"
{
    // Full-screen pass: blurs the left/right "walls", the "ceiling" and the "floor" of the viewport, leaving the centre sharp.
    // Run by EdgeBlurFeature (after post-processing, before SMAA). Parameters are set from script every frame.
    Properties
    {
        _Strength ("Strength (0 = off)", Range(0, 1)) = 1
        _Inner ("Sharp area (0..1 of half-screen)", Range(0, 0.95)) = 0.55
        _Radius ("Max blur radius (fraction of screen height)", Range(0, 0.05)) = 0.012
        _Taps ("Samples", Range(4, 16)) = 10
        _Dims ("Target size (w, h, 1/w, 1/h)", Vector) = (1920, 1080, 0.00052, 0.00093)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off Blend Off

        Pass
        {
            Name "RW Edge Blur"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _Strength;
            float _Inner;
            float _Radius;
            float _Taps;
            float4 _Dims;

            // Interleaved gradient noise: rotates the sample pattern per pixel so few taps do not band.
            float Ign(float2 p) { return frac(52.9829189 * frac(dot(p, float2(0.06711056, 0.00583715)))); }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                half4 c0 = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0);

                // 0 in the middle .. 1 at the screen edge, per axis; x = side walls, y = ceiling / floor.
                float2 d = abs(uv - 0.5) * 2.0;
                float2 e = saturate((d - _Inner) / max(1e-3, 1.0 - _Inner));
                e = e * e * (3.0 - 2.0 * e);
                float w = saturate(e.x + e.y - e.x * e.y) * _Strength;
                if (w < 0.004) return c0;

                float radiusPx = w * _Radius * _Dims.y;      // blur radius in pixels, grows towards the edge
                float2 duv = _Dims.zw * radiusPx;            // pixels -> uv (keeps the blur round on any aspect ratio)
                float rot = Ign(uv * _Dims.xy) * 6.2831853;

                half3 sum = c0.rgb;
                float wsum = 1.0;
                int n = (int)_Taps;
                [loop] for (int k = 0; k < n; k++)
                {
                    float r = sqrt((k + 0.5) / n);           // even coverage of the disc
                    float a = k * 2.399963 + rot;            // golden angle
                    float2 o = float2(cos(a), sin(a)) * r;
                    sum += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + o * duv, 0).rgb;
                    wsum += 1.0;
                }
                return half4(sum / wsum, c0.a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
