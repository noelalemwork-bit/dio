Shader "Dio/Tornado"
{
    // Translucent two-sided shader that multiplies vertex.color by a tint.
    // Used by the procedurally-generated tornado mesh; vertex colors are
    // baked at mesh-build time to encode the wave magnitude (white peaks,
    // dark grey troughs).
    //
    // Two SubShaders: the first is URP-tagged (UniversalForward LightMode +
    // RenderPipeline=UniversalPipeline) so URP picks it up natively, the
    // second is the legacy CG path so the same .shader keeps working if the
    // project ever drops back to the built-in pipeline. Without the URP
    // SubShader, URP's frame setup wouldn't render the tornado at all —
    // legacy CG transparent shaders aren't reliably picked up by the URP
    // forward pass, which is why the tornado was firing but invisible.
    Properties
    {
        _Tint ("Tint", Color) = (1, 1, 1, 0.55)
    }

    // ---------- URP SubShader ----------
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue"          = "Transparent"
            "RenderType"     = "Transparent"
            "IgnoreProjector"= "True"
        }

        Pass
        {
            Name "TornadoForward"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; half4 color : COLOR; };
            struct Varyings   { float4 positionHCS : SV_POSITION; half4 color : COLOR; };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.color = IN.color * _Tint;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target { return IN.color; }
            ENDHLSL
        }
    }

    // ---------- Built-in pipeline fallback ----------
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
