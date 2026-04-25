Shader "Dio/EarthPlanet"
{
    // Cartoon earth: low-frequency noise in object space gives stable continents
    // (they don't move when the planet's transform spins). Three thresholds
    // → ocean / coast / land / mountain.
    Properties
    {
        _Ocean    ("Ocean",    Color) = (0.08, 0.36, 0.55, 1)
        _Coast    ("Shallow",  Color) = (0.32, 0.62, 0.70, 1)
        _Lowland  ("Lowland",  Color) = (0.30, 0.55, 0.28, 1)
        _Highland ("Highland", Color) = (0.45, 0.42, 0.22, 1)
        _Peak     ("Peak",     Color) = (0.85, 0.80, 0.75, 1)

        _NoiseScale  ("Noise Scale", Float) = 1.4
        _SeaLevel    ("Sea Level",   Range(0, 1)) = 0.46
        _CoastWidth  ("Coast Width", Range(0, 0.2)) = 0.04
        _MountainStart ("Mountain Start", Range(0, 1)) = 0.72
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2f { float4 vertex : SV_POSITION; float3 oPos : TEXCOORD0; float3 wNormal : TEXCOORD1; };

            float4 _Ocean, _Coast, _Lowland, _Highland, _Peak;
            float _NoiseScale, _SeaLevel, _CoastWidth, _MountainStart;
            float4 _PlayerUp; // global from SkyboxPlayerUpBinder (unused for the planet itself but defined for consistency)

            float hash13(float3 p) { p = frac(p * 0.3183099 + 0.1); p *= 17.0; return frac(p.x*p.y*p.z*(p.x+p.y+p.z)); }
            float vnoise(float3 p)
            {
                float3 i = floor(p), f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(
                    lerp(lerp(hash13(i+float3(0,0,0)), hash13(i+float3(1,0,0)), f.x),
                         lerp(hash13(i+float3(0,1,0)), hash13(i+float3(1,1,0)), f.x), f.y),
                    lerp(lerp(hash13(i+float3(0,0,1)), hash13(i+float3(1,0,1)), f.x),
                         lerp(hash13(i+float3(0,1,1)), hash13(i+float3(1,1,1)), f.x), f.y),
                    f.z);
            }
            float fbm(float3 p) { float v=0,a=0.5; for(int k=0;k<4;k++){v+=a*vnoise(p);p*=2.05;a*=0.5;} return v; }

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.oPos = v.vertex.xyz;
                o.wNormal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                // Sample on the unit sphere direction (object-space coords on
                // a primitive Sphere are -0.5..0.5 — normalize to direction).
                float3 dir = normalize(i.oPos);
                float h = fbm(dir * _NoiseScale + 7.13);

                half3 col;
                if (h < _SeaLevel - _CoastWidth) col = _Ocean.rgb;
                else if (h < _SeaLevel)          col = lerp(_Ocean.rgb, _Coast.rgb,
                                                            (h - (_SeaLevel - _CoastWidth)) / max(_CoastWidth, 1e-4));
                else if (h < _MountainStart)     col = lerp(_Lowland.rgb, _Highland.rgb,
                                                            (h - _SeaLevel) / max(_MountainStart - _SeaLevel, 1e-4));
                else                              col = lerp(_Highland.rgb, _Peak.rgb,
                                                            saturate((h - _MountainStart) / 0.18));

                // Lit by the planet's own surface normal — gives the cartoon
                // "everywhere is lit" look so the player can see the track from any side.
                float ndl = saturate(dot(normalize(i.wNormal), normalize(i.oPos)));
                col *= 0.75 + 0.40 * ndl;

                return half4(col, 1);
            }
            ENDCG
        }
    }
}
