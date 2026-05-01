Shader "Dio/PenaltyShot"
{
    // Procedural soccer-ball: voronoi cells in object-space direction space,
    // shaded black or white based on a hashed cell ID. Cell SEAMS are drawn
    // as thin dark lines so the panels read crisply at any distance. Polar
    // coordinates fall out naturally from normalising object-space position
    // on a sphere mesh; voronoi over that direction gives roughly-uniform
    // panels without the seam pinching you'd get from a (theta, phi) grid.
    Properties
    {
        _Light       ("Light Panel",  Color) = (0.95, 0.96, 0.96, 1)
        _Dark        ("Dark Panel",   Color) = (0.08, 0.08, 0.10, 1)
        _Seam        ("Seam Color",   Color) = (0.02, 0.02, 0.03, 1)
        _CellScale   ("Cell Scale",   Range(2, 12)) = 6.0
        _SeamWidth   ("Seam Width",   Range(0.005, 0.10)) = 0.035
        _DarkRatio   ("Dark Ratio",   Range(0, 1)) = 0.40
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2f { float4 pos : SV_POSITION; float3 oPos : TEXCOORD0; float3 wNormal : TEXCOORD1; };

            float4 _Light, _Dark, _Seam;
            float _CellScale, _SeamWidth, _DarkRatio;

            // Cheap 3D hash → [0,1)
            float hash13(float3 p)
            {
                p = frac(p * float3(443.897, 441.423, 437.195));
                p += dot(p, p.yzx + 19.19);
                return frac((p.x + p.y) * p.z);
            }

            // Voronoi over a 3D direction. Returns:
            //   x = distance to closest seed
            //   y = distance to second-closest seed (so y - x is "seam distance")
            //   z = scalar id of the closest cell  in [0,1)
            float3 voronoi3(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                float d1 = 8, d2 = 8;
                float3 idClosest = 0;
                [unroll]
                for (int z = -1; z <= 1; z++)
                [unroll]
                for (int y = -1; y <= 1; y++)
                [unroll]
                for (int x = -1; x <= 1; x++)
                {
                    float3 cell = float3(x, y, z);
                    float3 seed = cell + float3(
                        hash13(i + cell + 0.13),
                        hash13(i + cell + 7.31),
                        hash13(i + cell + 13.7));
                    float3 r = seed - f;
                    float d = dot(r, r);
                    if (d < d1) { d2 = d1; d1 = d; idClosest = i + cell; }
                    else if (d < d2) { d2 = d; }
                }
                return float3(sqrt(d1), sqrt(d2), hash13(idClosest));
            }

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.oPos = v.vertex.xyz;
                o.wNormal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                // Sample voronoi over the unit sphere direction. Multiplying
                // dir by _CellScale before the voronoi gives roughly that many
                // panels around the equator.
                float3 dir = normalize(i.oPos);
                float3 v = voronoi3(dir * _CellScale);
                // Black panels = lowest-id portion of the cells, by ratio.
                float panel = step(_DarkRatio, v.z);
                half3 panelCol = panel > 0.5 ? _Light.rgb : _Dark.rgb;

                // Seam: blend toward seam color when (d2 - d1) is small.
                float seam = smoothstep(_SeamWidth, 0.0, (v.y - v.x));
                panelCol = lerp(panelCol, _Seam.rgb, seam);

                // Cheap shading off the world normal, biased so the unlit side
                // never goes fully black — keeps the ball legible from below.
                float ndl = saturate(dot(normalize(i.wNormal), normalize(float3(0.4, 1.0, 0.2))));
                panelCol *= 0.65 + 0.45 * ndl;

                return half4(panelCol, 1);
            }
            ENDCG
        }
    }
}
