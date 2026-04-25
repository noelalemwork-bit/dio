Shader "Dio/CarBodyCamo"
{
    Properties
    {
        _ColorA   ("Color A", Color) = (0.05, 0.06, 0.08, 1)
        _ColorB   ("Color B", Color) = (0.30, 0.55, 0.30, 1)
        _NoiseScale  ("Noise Scale", Float) = 2.5
        _Threshold   ("Threshold", Range(0, 1)) = 0.5
        _Edge        ("Edge Softness", Range(0, 0.4)) = 0.08
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

            float4 _ColorA, _ColorB;
            float _NoiseScale, _Threshold, _Edge;

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
            float fbm(float3 p) { float v=0,a=0.5; for(int i=0;i<3;i++){v+=a*vnoise(p);p*=2.03;a*=0.5;} return v; }

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
                float n = fbm(i.oPos * _NoiseScale);
                float mask = smoothstep(_Threshold - _Edge, _Threshold + _Edge, n);
                half3 base = lerp(_ColorA.rgb, _ColorB.rgb, mask);

                // Cheap directional shading so the car has form even without a light.
                float ndl = saturate(dot(normalize(i.wNormal), normalize(float3(0.4, 1.0, 0.2))));
                base *= 0.55 + 0.55 * ndl;

                return half4(base, 1);
            }
            ENDCG
        }
    }
}
