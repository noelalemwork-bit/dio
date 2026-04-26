Shader "Dio/Tornado"
{
    // Translucent two-sided shader that multiplies vertex.color by a tint.
    // Used by the procedurally-generated tornado mesh; vertex colors are
    // baked at mesh-build time to encode the wave magnitude (white peaks,
    // dark grey troughs).
    Properties
    {
        _Tint ("Tint", Color) = (1, 1, 1, 0.55)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off
        Lighting Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; };

            fixed4 _Tint;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color * _Tint;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target { return i.color; }
            ENDCG
        }
    }
}
