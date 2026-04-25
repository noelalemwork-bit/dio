Shader "Dio/SunsetSky"
{
    Properties
    {
        _ColorBottom ("Bottom (horizon)", Color) = (1.0, 0.55, 0.20, 1)
        _ColorMid    ("Mid",              Color) = (0.95, 0.32, 0.30, 1)
        _ColorTop    ("Top (zenith)",     Color) = (0.30, 0.12, 0.45, 1)
        _MidPoint    ("Mid Stop", Range(0, 1)) = 0.45

        _SunDir   ("Sun Direction (xyz)", Vector) = (-0.30, 0.18, 1.00, 0)
        _SunColor ("Sun Color",  Color) = (1.0, 0.92, 0.70, 1)
        _SunSize  ("Sun Disc Size", Range(0, 0.2)) = 0.04
        _SunHaloPower ("Sun Halo Sharpness", Range(1, 256)) = 24

        _CloudColor    ("Cloud Color",    Color) = (1.0, 0.75, 0.60, 1)
        _CloudShadow   ("Cloud Shadow",   Color) = (0.55, 0.22, 0.30, 1)
        _CloudCoverage ("Cloud Coverage", Range(0, 1)) = 0.55
        _CloudScale    ("Cloud Scale", Float) = 4.0
        _CloudSpeed    ("Cloud Drift Speed", Float) = 0.04
        _CloudDensity  ("Cloud Strength", Range(0, 1)) = 0.85
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Background"
            "Queue"="Background"
            "PreviewType"="Skybox"
            "RenderPipeline"="UniversalPipeline"
        }
        Cull Off ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; float3 dir : TEXCOORD0; };

            CBUFFER_START(UnityPerMaterial)
                float4 _ColorBottom;
                float4 _ColorMid;
                float4 _ColorTop;
                float  _MidPoint;
                float4 _SunDir;
                float4 _SunColor;
                float  _SunSize;
                float  _SunHaloPower;
                float4 _CloudColor;
                float4 _CloudShadow;
                float  _CloudCoverage;
                float  _CloudScale;
                float  _CloudSpeed;
                float  _CloudDensity;
            CBUFFER_END

            // Hash + 3D value noise + fbm. Cheap, no textures.
            float hash13(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float vnoise(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(
                    lerp(lerp(hash13(i + float3(0, 0, 0)), hash13(i + float3(1, 0, 0)), f.x),
                         lerp(hash13(i + float3(0, 1, 0)), hash13(i + float3(1, 1, 0)), f.x), f.y),
                    lerp(lerp(hash13(i + float3(0, 0, 1)), hash13(i + float3(1, 0, 1)), f.x),
                         lerp(hash13(i + float3(0, 1, 1)), hash13(i + float3(1, 1, 1)), f.x), f.y),
                    f.z);
            }

            float fbm(float3 p)
            {
                float v = 0.0;
                float a = 0.5;
                [unroll] for (int i = 0; i < 5; i++)
                {
                    v += a * vnoise(p);
                    p = p * 2.02 + 19.19;
                    a *= 0.5;
                }
                return v;
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.dir = IN.positionOS.xyz;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 dir = normalize(IN.dir);

                // Three-stop vertical gradient. y in [-1..1], remap to [0..1].
                float h = saturate(dir.y * 0.5 + 0.5);
                float lo = smoothstep(0.0, _MidPoint, h);
                float hi = smoothstep(_MidPoint, 1.0, h);
                half3 sky = lerp(_ColorBottom.rgb, _ColorMid.rgb, lo);
                sky = lerp(sky, _ColorTop.rgb, hi);

                // Sun disc + soft halo.
                float3 sunDir = normalize(_SunDir.xyz);
                float sunDot = saturate(dot(dir, sunDir));
                float disc = smoothstep(1.0 - _SunSize, 1.0 - _SunSize * 0.5, sunDot);
                float halo = pow(sunDot, _SunHaloPower) * 0.55;
                sky += _SunColor.rgb * (disc + halo);

                // Procedural clouds. Sample fbm at "infinity-plane" uv = dir.xz / dir.y.
                // Only above the horizon; fade in over a small band so there's no hard line.
                float skyMask = smoothstep(0.0, 0.18, dir.y);
                float2 cloudUV = dir.xz / max(0.08, dir.y);
                cloudUV = cloudUV * _CloudScale + _Time.y * _CloudSpeed;
                float n  = fbm(float3(cloudUV, 0));
                float ns = fbm(float3(cloudUV * 0.5 + 13.0, 0)); // second octave for shadow inside clouds
                float cmask = smoothstep(_CloudCoverage - 0.08, _CloudCoverage + 0.18, n);
                cmask *= skyMask;

                half3 cloudCol = lerp(_CloudShadow.rgb, _CloudColor.rgb, smoothstep(0.4, 0.8, ns));
                // Sun-side clouds catch a warm rim.
                cloudCol += _SunColor.rgb * pow(saturate(dot(dir, sunDir)), 4.0) * 0.18;

                sky = lerp(sky, cloudCol, cmask * _CloudDensity);

                return half4(sky, 1);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
