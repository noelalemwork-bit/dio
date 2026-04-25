Shader "Dio/CarWheel"
{
    // Procedural wheel: black tire on the cylindrical "side", silver radial
    // spokes on the round "cap" faces. Sampling in object space means rotation
    // visibly spins the spokes (cap angles change as the cylinder rotates
    // about its axle).
    Properties
    {
        _TireColor   ("Tire Color",   Color) = (0.05, 0.05, 0.06, 1)
        _SpokeBase   ("Spoke Base",   Color) = (0.34, 0.36, 0.40, 1)
        _SpokeStripe ("Spoke Stripe", Color) = (0.78, 0.80, 0.85, 1)
        _HubColor    ("Hub Color",    Color) = (0.55, 0.57, 0.62, 1)
        _SpokeCount  ("Spoke Count",  Range(2, 16)) = 6
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

            float4 _TireColor, _SpokeBase, _SpokeStripe, _HubColor;
            float _SpokeCount;
            float4 _PlayerUp; // global from SkyboxPlayerUpBinder

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
                // Cylinder primitive is 2u tall (Y) and 1u diameter (XZ).
                // Cap = abs(y) near 1; side = elsewhere. Radial dist 0..0.5.
                float onCap = step(0.95, abs(i.oPos.y));
                float radial = length(i.oPos.xz) * 2.0; // 0 axle .. 1 rim
                float angle  = atan2(i.oPos.z, i.oPos.x); // -pi..pi
                float ang01  = (angle + 3.14159265) / 6.28318530;

                // SIDE (the rolling surface) — tire.
                half3 sideCol = _TireColor.rgb;

                // CAP — hub at center, spokes mid, tire-coloured rim at edge.
                half3 capCol;
                float stripe = step(0.5, frac(ang01 * _SpokeCount));
                half3 spokeCol = lerp(_SpokeBase.rgb, _SpokeStripe.rgb, stripe);

                // Blend regions smoothly: rim → spokes → hub.
                if (radial > 0.92)      capCol = _TireColor.rgb;
                else if (radial > 0.18) capCol = spokeCol;
                else                    capCol = _HubColor.rgb;

                half3 col = lerp(sideCol, capCol, onCap);

                // Use the global player-up (set by SkyboxPlayerUpBinder) as the
                // key-light direction so all peers shade the wheel the same way
                // regardless of where they are on the planet.
                float3 keyDir = _PlayerUp.xyz;
                if (dot(keyDir, keyDir) < 0.001) keyDir = float3(0, 1, 0);
                keyDir = normalize(keyDir);
                float ndl = saturate(dot(normalize(i.wNormal), keyDir));
                // Bright ambient floor so the tire is dark-grey, not full black.
                col *= 0.80 + 0.35 * ndl;

                return half4(col, 1);
            }
            ENDCG
        }
    }
}
