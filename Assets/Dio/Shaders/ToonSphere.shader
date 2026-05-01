Shader "Dio/ToonSphere"
{
    // Flat-shaded sphere with a thin dark outline at grazing angles. Used
    // for burnout / impact / dust puffs. Hand-tuned to read well even at
    // tiny on-screen sizes, where standard PBR balls just look grey.
    Properties
    {
        _BaseColor   ("Base Color",   Color) = (1, 1, 1, 1)
        _Outline     ("Outline Color", Color) = (0.65, 0.65, 0.65, 1)
        _OutlineWidth("Outline Width", Range(0, 0.6)) = 0.18
        _ShadeStep   ("Shade Step",   Range(0, 1))   = 0.55
        _ShadeStrength("Shade Strength", Range(0, 1)) = 0.30
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Back
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2f { float4 pos : SV_POSITION; float3 wNormal : TEXCOORD0; float3 viewDir : TEXCOORD1; };

            float4 _BaseColor, _Outline;
            float _OutlineWidth, _ShadeStep, _ShadeStrength;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wNormal = UnityObjectToWorldNormal(v.normal);
                float3 wPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.viewDir = normalize(_WorldSpaceCameraPos - wPos);
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.wNormal);
                float fresnel = 1.0 - saturate(dot(n, normalize(i.viewDir)));
                // Two-tone toon shading via a hard step so the puffs read
                // as cell-shaded.
                float ndl = saturate(dot(n, normalize(float3(0.4, 1.0, 0.2))));
                float lit = step(_ShadeStep, ndl);
                half3 base = _BaseColor.rgb * (lit > 0.5 ? 1.0 : (1.0 - _ShadeStrength));

                // Thin outline at grazing angles.
                float outlineMask = smoothstep(1.0 - _OutlineWidth, 1.0, fresnel);
                base = lerp(base, _Outline.rgb, outlineMask);

                return half4(base, _BaseColor.a);
            }
            ENDCG
        }
    }
}
