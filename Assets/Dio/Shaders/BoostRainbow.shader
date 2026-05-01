Shader "Dio/BoostRainbow"
{
    // Time-driven swirling rainbow gradient. Hue at any point comes from
    // a curl-noise-style angular field over the object surface — NOT a
    // straight horizontal/vertical ramp — so the shader reads as a
    // turbulent flow even on a flat patch. Used by boost trails / boost
    // ground markers / star-mode body tint.
    //
    // Palette is biased through warm reds → oranges → magentas → purples
    // before wrapping, so the loop reads as "warm party" rather than the
    // full chroma circle.
    Properties
    {
        _Speed     ("Cycle Speed",  Float)  = 0.7
        _SwirlScale("Swirl Scale",  Float)  = 2.0
        _SwirlBend ("Swirl Bend",   Range(0, 6)) = 2.4
        _Saturation("Saturation",   Range(0, 1)) = 0.95
        _Brightness("Brightness",   Range(0.5, 1.5)) = 1.05
        _AlphaFloor("Alpha Floor",  Range(0, 1)) = 0.85
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float3 oPos : TEXCOORD1; };

            float _Speed, _SwirlScale, _SwirlBend, _Saturation, _Brightness, _AlphaFloor;

            float hash13(float3 p)
            {
                p = frac(p * float3(443.897, 441.423, 437.195));
                p += dot(p, p.yzx + 19.19);
                return frac((p.x + p.y) * p.z);
            }

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

            float fbm(float3 p)
            {
                float v = 0, a = 0.5;
                [unroll] for (int i = 0; i < 4; i++) { v += a * vnoise(p); p = p * 2.0 + 11.1; a *= 0.5; }
                return v;
            }

            float3 hsv2rgb(float3 c)
            {
                float4 K = float4(1.0, 2.0/3.0, 1.0/3.0, 3.0);
                float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
                return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
            }

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.oPos = v.vertex.xyz;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                float t = _Time.y * _Speed;
                // Two layers of noise sampled at different scales — adding
                // their gradients mixes into a curl-flow direction so the
                // hue field looks like it's swirling rather than bleeding
                // along one axis.
                float n1 = fbm(float3(i.oPos.x * _SwirlScale, i.oPos.y * _SwirlScale, t));
                float n2 = fbm(float3(i.oPos.z * _SwirlScale + 17.3, i.oPos.x * _SwirlScale - 9.7, t * 1.13));
                float swirl = n1 + n2 - 1.0;
                // Warm-biased palette: bias the hue input through a quadratic
                // curve that lingers in the red→orange→magenta arc and
                // skips most of the green range. Final hue cycles every
                // ~1/Speed seconds.
                float hueRaw = frac(t * 0.25 + swirl * (_SwirlBend / 6.283));
                // Quartic bias — hue spends more time near 0 (red) and 0.85 (purple).
                float hue = lerp(hueRaw * 0.10, 0.10 + hueRaw * 0.85,
                                 smoothstep(0.0, 1.0, abs(hueRaw - 0.5) * 2.0));
                // Pump it slightly back into a 0..1 range so we always wrap.
                hue = frac(hue + 0.92);

                half3 col = hsv2rgb(float3(hue, _Saturation, _Brightness));
                return half4(col, _AlphaFloor);
            }
            ENDCG
        }
    }
}
