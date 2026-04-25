Shader "Dio/SunsetSky"
{
    Properties
    {
        _ColorBottom ("Bottom (horizon)", Color) = (1.0, 0.55, 0.20, 1)
        _ColorMid    ("Mid",              Color) = (0.95, 0.32, 0.30, 1)
        _ColorTop    ("Top (zenith)",     Color) = (0.30, 0.12, 0.45, 1)
        _MidPoint    ("Mid Stop", Range(0, 1)) = 0.45

        // The "up" direction the gradient and cloud horizon are aligned to.
        // On a flat world this is (0,1,0); on the spherical world it's the
        // unit vector from planet origin to the player. SkyboxPlayerUpBinder
        // pushes this every frame.
        _PlayerUp ("Player Up", Vector) = (0, 1, 0, 0)

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

    // Skybox shaders only need vertex transform + per-pixel direction. Using the
    // legacy `UnityCG.cginc` keeps us compatible with both URP and Built-in
    // without needing pipeline-specific shader libraries.
    SubShader
    {
        Tags
        {
            "RenderType"="Background"
            "Queue"="Background"
            "PreviewType"="Skybox"
        }
        Cull Off ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };
            struct v2f     { float4 vertex : SV_POSITION; float3 dir : TEXCOORD0; };

            float4 _ColorBottom;
            float4 _ColorMid;
            float4 _ColorTop;
            float  _MidPoint;
            float4 _PlayerUp;
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
            float  _DioTime; // global from Dio.Common.NetworkTimeBinder

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
                for (int i = 0; i < 5; i++)
                {
                    v += a * vnoise(p);
                    p = p * 2.02 + 19.19;
                    a *= 0.5;
                }
                return v;
            }

            v2f vert(appdata IN)
            {
                v2f OUT;
                OUT.vertex = UnityObjectToClipPos(IN.vertex);
                OUT.dir = IN.vertex.xyz;
                return OUT;
            }

            half4 frag(v2f IN) : SV_Target
            {
                float3 dir = normalize(IN.dir);

                // Spherical-world horizon: gradient is along the player's surface
                // normal, not world-Y. _PlayerUp is set at runtime from
                // (playerPos - planetOrigin).normalized; falls back to Y up if
                // the binder hasn't pushed yet.
                float3 up = _PlayerUp.xyz;
                if (dot(up, up) < 0.0001) up = float3(0, 1, 0);
                up = normalize(up);
                float upDot = dot(dir, up);

                // Three-stop gradient. upDot in [-1..1] -> h in [0..1].
                float h = saturate(upDot * 0.5 + 0.5);
                float lo = smoothstep(0.0, _MidPoint, h);
                float hi = smoothstep(_MidPoint, 1.0, h);
                half3 sky = lerp(_ColorBottom.rgb, _ColorMid.rgb, lo);
                sky = lerp(sky, _ColorTop.rgb, hi);

                // Sun disc + soft halo. Sun is a fixed world-space direction --
                // it appears in different parts of the player's sky depending on
                // where they are on the planet, which is the desired behaviour.
                float3 sunDir = normalize(_SunDir.xyz);
                float sunDot = saturate(dot(dir, sunDir));
                float disc = smoothstep(1.0 - _SunSize, 1.0 - _SunSize * 0.5, sunDot);
                float halo = pow(sunDot, _SunHaloPower) * 0.55;
                sky += _SunColor.rgb * (disc + halo);

                // Clouds -- sample fbm in 3D *direction* space rather than projecting
                // onto a horizontal plane. World-stable, no singularity at the
                // pole, no basis-flip glitches when the player crosses an axis.
                float skyMask = smoothstep(0.0, 0.18, upDot);
                float3 cloudPos = dir * _CloudScale;
                // _DioTime is the synced NetworkTime; falls back to _Time.y if the
                // NetworkTimeBinder isn't running (single-player editor preview).
                float dioT = _DioTime;
                if (dioT == 0.0) dioT = _Time.y;
                cloudPos += float3(dioT * _CloudSpeed, dioT * _CloudSpeed * 0.6, 0);
                float n  = fbm(cloudPos);
                float ns = fbm(cloudPos * 0.5 + 13.0); // second octave for inside-cloud shadow
                float cmask = smoothstep(_CloudCoverage - 0.08, _CloudCoverage + 0.18, n);
                cmask *= skyMask;

                half3 cloudCol = lerp(_CloudShadow.rgb, _CloudColor.rgb, smoothstep(0.4, 0.8, ns));
                cloudCol += _SunColor.rgb * pow(sunDot, 4.0) * 0.18; // warm sun-side rim

                sky = lerp(sky, cloudCol, cmask * _CloudDensity);

                return half4(sky, 1);
            }
            ENDCG
        }
    }

    Fallback Off
}
