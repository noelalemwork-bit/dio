Shader "Dio/MysteryBox"
{
    // Yellow base with a slow rainbow shimmer; reads as a Mario-Kart-style item box.
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.95, 0.78, 0.20, 1)
        _Shimmer   ("Shimmer", Range(0, 1)) = 0.45
        _Speed     ("Cycle Speed", Float) = 0.4
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

            float4 _BaseColor;
            float _Shimmer, _Speed;
            float _DioTime; // global from Dio.Common.NetworkTimeBinder

            float3 hsv2rgb(float3 c)
            {
                float4 K = float4(1.0, 2.0/3.0, 1.0/3.0, 3.0);
                float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
                return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
            }

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
                float t = _DioTime; if (t == 0.0) t = _Time.y;
                float h = frac(t * _Speed + (i.oPos.x + i.oPos.y + i.oPos.z) * 0.35);
                half3 shimmer = hsv2rgb(float3(h, 0.65, 1.0));
                half3 col = lerp(_BaseColor.rgb, shimmer, _Shimmer);
                float ndl = saturate(dot(normalize(i.wNormal), normalize(float3(0.4, 1.0, 0.2))));
                // Higher ambient floor so the box stays readable even from below.
                col *= 0.85 + 0.30 * ndl;
                return half4(col, 1);
            }
            ENDCG
        }
    }
}
