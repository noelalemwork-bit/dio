Shader "Dio/Tornado"
{
    // Translucent two-sided shader — vertex.color carries the wave-magnitude
    // gradient (white peaks, dark grey troughs) baked at mesh build time.
    // Tagged for both Built-in AND URP so the pipeline picks the right
    // SubShader. Floor alpha keeps the funnel visible even if downstream
    // material edits zero out the tint alpha.
    Properties
    {
        _Tint     ("Tint",       Color) = (0.85, 0.92, 1.00, 0.65)
        _MinAlpha ("Floor Alpha", Range(0, 1)) = 0.30
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "RenderPipeline"="UniversalPipeline" }
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
            float  _MinAlpha;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color * _Tint;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = i.color;
                // Floor alpha so the funnel never collapses to fully transparent.
                c.a = max(c.a, _MinAlpha);
                return c;
            }
            ENDCG
        }
    }
    // Fallback for the Built-in pipeline (same shader, just without the URP tag).
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
            float  _MinAlpha;
            v2f vert (appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.color = v.color * _Tint; return o; }
            fixed4 frag (v2f i) : SV_Target { fixed4 c = i.color; c.a = max(c.a, _MinAlpha); return c; }
            ENDCG
        }
    }
}
