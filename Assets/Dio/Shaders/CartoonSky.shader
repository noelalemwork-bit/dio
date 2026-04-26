Shader "Dio/CartoonSky"
{
    // Multi-mode cartoony skybox. One shader, six visual presets selected
    // by `_SkyMode`. Every effect (gradient, sun, moon, stars, constellations,
    // aurora ribbons, nebula, lightning) is computed in *direction space* and
    // orientated relative to `_PlayerUp`, so it stays stable as the car drives
    // around the spherical world — no horizon basis flip, no pole singularity.
    //
    // Modes:
    //   0 — Sunset (existing palette, kept for backward compat)
    //   1 — Aurora Night (deep navy + dancing aurora ribbons + moon + stars)
    //   2 — Cosmic Galaxy (purple/teal nebula swirl + dense twinkling stars)
    //   3 — Stormy Daydream (slate-cyan, dense clouds, intermittent lightning)
    //   4 — Twilight Constellations (indigo+magenta, big moon, real-shape constellations)
    //   5 — Cotton Candy Dawn (pastel pink/lavender/mint, soft puffy clouds, chromatic sun)
    Properties
    {
        _SkyMode      ("Mode (0..5)", Range(0, 5)) = 0

        _ColorBottom  ("Bottom (horizon)", Color) = (1.0, 0.55, 0.20, 1)
        _ColorMid     ("Mid",              Color) = (0.95, 0.32, 0.30, 1)
        _ColorTop     ("Top (zenith)",     Color) = (0.30, 0.12, 0.45, 1)
        _MidPoint     ("Mid Stop", Range(0, 1)) = 0.45
        _HorizonGlow  ("Horizon Glow", Range(0, 2)) = 0.15

        _PlayerUp     ("Player Up", Vector) = (0, 1, 0, 0)

        _SunDir       ("Sun Direction (xyz)", Vector) = (-0.30, 0.18, 1.00, 0)
        _SunColor     ("Sun Color",  Color) = (1.0, 0.92, 0.70, 1)
        _SunSize      ("Sun Disc Size", Range(0, 0.2)) = 0.04
        _SunHaloPower ("Sun Halo Sharpness", Range(1, 256)) = 24
        _SunIntensity ("Sun Intensity", Range(0, 4)) = 1

        _MoonDir      ("Moon Direction (xyz)", Vector) = (0.50, 0.55, -0.30, 0)
        _MoonColor    ("Moon Color", Color) = (0.95, 0.95, 0.92, 1)
        _MoonSize     ("Moon Disc Size", Range(0, 0.3)) = 0.06
        _MoonPhase    ("Moon Phase Offset", Range(-1, 1)) = 0.45
        _MoonGlow     ("Moon Halo", Range(0, 1)) = 0.25

        _CloudColor    ("Cloud Color",    Color) = (1.0, 0.78, 0.58, 1)
        _CloudShadow   ("Cloud Shadow",   Color) = (0.55, 0.22, 0.30, 1)
        _CloudCoverage ("Cloud Coverage", Range(0, 1)) = 0.55
        _CloudScale    ("Cloud Scale", Float) = 4.0
        _CloudSpeed    ("Cloud Drift Speed", Float) = 0.04
        _CloudDensity  ("Cloud Strength", Range(0, 1)) = 0.85
        _CloudSoftness ("Cloud Edge Softness", Range(0.02, 0.5)) = 0.18

        _StarDensity      ("Star Density", Range(0, 1)) = 0.0
        _StarBrightness   ("Star Brightness", Range(0, 4)) = 1.4
        _StarTwinkleSpeed ("Star Twinkle Speed", Float) = 1.6
        _StarColor        ("Star Color", Color) = (1, 1, 1, 1)
        _StarStreakChance ("Shooting Star Chance", Range(0, 1)) = 0.0

        _AuroraColor1   ("Aurora Color 1", Color) = (0.20, 0.95, 0.55, 1)
        _AuroraColor2   ("Aurora Color 2", Color) = (0.70, 0.30, 0.95, 1)
        _AuroraIntensity("Aurora Intensity", Range(0, 3)) = 0.0
        _AuroraSpeed    ("Aurora Speed", Float) = 0.35
        _AuroraScale    ("Aurora Scale", Float) = 1.8

        _NebulaColor1   ("Nebula Color 1", Color) = (0.80, 0.30, 0.95, 1)
        _NebulaColor2   ("Nebula Color 2", Color) = (0.20, 0.55, 0.95, 1)
        _NebulaIntensity("Nebula Intensity", Range(0, 2)) = 0.0
        _NebulaSpeed    ("Nebula Speed", Float) = 0.06

        _ConstellationColor    ("Constellation Color", Color) = (0.85, 0.92, 1.00, 1)
        _ConstellationStrength ("Constellation Strength", Range(0, 2)) = 0.0

        _LightningChance ("Lightning Chance", Range(0, 1)) = 0.0
        _LightningColor  ("Lightning Color", Color) = (0.92, 0.95, 1.00, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Background"
            "Queue"="Background"
            "PreviewType"="Skybox"
        }
        Cull Off ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };
            struct v2f     { float4 vertex : SV_POSITION; float3 dir : TEXCOORD0; };

            float  _SkyMode;
            float4 _ColorBottom, _ColorMid, _ColorTop;
            float  _MidPoint, _HorizonGlow;
            float4 _PlayerUp;
            float4 _SunDir, _SunColor;
            float  _SunSize, _SunHaloPower, _SunIntensity;
            float4 _MoonDir, _MoonColor;
            float  _MoonSize, _MoonPhase, _MoonGlow;
            float4 _CloudColor, _CloudShadow;
            float  _CloudCoverage, _CloudScale, _CloudSpeed, _CloudDensity, _CloudSoftness;
            float  _StarDensity, _StarBrightness, _StarTwinkleSpeed, _StarStreakChance;
            float4 _StarColor;
            float4 _AuroraColor1, _AuroraColor2;
            float  _AuroraIntensity, _AuroraSpeed, _AuroraScale;
            float4 _NebulaColor1, _NebulaColor2;
            float  _NebulaIntensity, _NebulaSpeed;
            float4 _ConstellationColor;
            float  _ConstellationStrength;
            float  _LightningChance;
            float4 _LightningColor;
            float  _DioTime; // global from Dio.Common.NetworkTimeBinder

            // ---------- Cheap procedural noise. No textures. ----------
            float hash13(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float vnoise(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(
                    lerp(lerp(hash13(i + float3(0,0,0)), hash13(i + float3(1,0,0)), f.x),
                         lerp(hash13(i + float3(0,1,0)), hash13(i + float3(1,1,0)), f.x), f.y),
                    lerp(lerp(hash13(i + float3(0,0,1)), hash13(i + float3(1,0,1)), f.x),
                         lerp(hash13(i + float3(0,1,1)), hash13(i + float3(1,1,1)), f.x), f.y),
                    f.z);
            }

            float fbm(float3 p)
            {
                float v = 0.0;
                float a = 0.5;
                for (int i = 0; i < 5; i++)
                {
                    v += a * vnoise(p);
                    p = p * 2.02 + 19.19;
                    a *= 0.5;
                }
                return v;
            }

            // Distance from a unit direction `d` to the great-circle SEGMENT
            // between two unit vectors `a` and `b`. Used for constellation lines.
            // Returns angular distance in radians (~0 = on the line).
            float distToArc(float3 d, float3 a, float3 b)
            {
                float3 nrm = normalize(cross(a, b));
                float perp = abs(dot(d, nrm));
                // Clamp to the segment: project d onto plane(a,b), check if it
                // lies in the wedge between a and b. If outside, distance is
                // to the nearest endpoint.
                float3 dp = normalize(d - nrm * dot(d, nrm));
                float ab = acos(clamp(dot(a, b), -1.0, 1.0));
                float ad = acos(clamp(dot(a, dp), -1.0, 1.0));
                float bd = acos(clamp(dot(b, dp), -1.0, 1.0));
                float onSeg = step(ad + bd, ab + 0.001);
                float endpointMin = min(acos(clamp(dot(d, a), -1.0, 1.0)),
                                        acos(clamp(dot(d, b), -1.0, 1.0)));
                return lerp(endpointMin, asin(saturate(perp)), onSeg);
            }

            // Star field: hash per-cell, threshold to density, twinkle in time.
            // Direction-stable so stars don't slide as the player moves.
            float starField(float3 dir, float density, float twinkleSpeed, float t)
            {
                float3 p = floor(dir * 80.0);
                float h = hash13(p);
                float onOff = step(1.0 - density * 0.05, h);
                if (onOff < 0.5) return 0.0;
                // Sub-cell position to make stars feel like points, not blocks.
                float3 sub = frac(dir * 80.0) - 0.5;
                float radial = saturate(1.0 - length(sub) * 6.0);
                radial = pow(radial, 8.0);
                float twinkle = 0.6 + 0.4 * sin(t * twinkleSpeed + h * 31.4);
                return radial * twinkle;
            }

            // Three reference constellations baked into the shader. Each is a
            // small chain of (a,b) segments between unit-direction points. They
            // sit in the upper hemisphere of world-space, so the player will
            // see them rotate overhead as they orbit the planet.
            float constellations(float3 dir)
            {
                // "Big Dipper" style polyline.
                float3 c1a = normalize(float3( 0.30, 0.85, 0.40));
                float3 c1b = normalize(float3( 0.05, 0.92, 0.36));
                float3 c1c = normalize(float3(-0.18, 0.95, 0.20));
                float3 c1d = normalize(float3(-0.32, 0.90, 0.05));
                float3 c1e = normalize(float3(-0.42, 0.78, 0.10));
                float3 c1f = normalize(float3(-0.36, 0.72, 0.32));
                // Triangle.
                float3 c2a = normalize(float3( 0.65, 0.55, -0.20));
                float3 c2b = normalize(float3( 0.85, 0.40,  0.10));
                float3 c2c = normalize(float3( 0.72, 0.30,  0.30));
                // Cross.
                float3 c3a = normalize(float3(-0.60, 0.50, -0.30));
                float3 c3b = normalize(float3(-0.40, 0.55, -0.55));
                float3 c3c = normalize(float3(-0.55, 0.65, -0.45));
                float3 c3d = normalize(float3(-0.45, 0.40, -0.40));

                float dmin = 999.0;
                dmin = min(dmin, distToArc(dir, c1a, c1b));
                dmin = min(dmin, distToArc(dir, c1b, c1c));
                dmin = min(dmin, distToArc(dir, c1c, c1d));
                dmin = min(dmin, distToArc(dir, c1d, c1e));
                dmin = min(dmin, distToArc(dir, c1e, c1f));
                dmin = min(dmin, distToArc(dir, c2a, c2b));
                dmin = min(dmin, distToArc(dir, c2b, c2c));
                dmin = min(dmin, distToArc(dir, c2c, c2a));
                dmin = min(dmin, distToArc(dir, c3a, c3b));
                dmin = min(dmin, distToArc(dir, c3c, c3d));

                // `line` is reserved in HLSL (geometry-shader topology keyword) — use `arc`.
                float arc = 1.0 - smoothstep(0.0, 0.012, dmin);
                // Slightly brighter "knots" at the endpoints.
                float knot = 0.0;
                knot = max(knot, 1.0 - smoothstep(0.0, 0.025, acos(clamp(dot(dir, c1a),-1,1))));
                knot = max(knot, 1.0 - smoothstep(0.0, 0.025, acos(clamp(dot(dir, c1c),-1,1))));
                knot = max(knot, 1.0 - smoothstep(0.0, 0.025, acos(clamp(dot(dir, c2a),-1,1))));
                knot = max(knot, 1.0 - smoothstep(0.0, 0.025, acos(clamp(dot(dir, c3a),-1,1))));
                return saturate(arc + knot * 0.6);
            }

            // Aurora ribbons: vertical bands rotating slowly around the planet,
            // colour blending between two tones. Fades out at the horizon and
            // at the zenith so the bands sit roughly mid-sky.
            float3 aurora(float3 dir, float upDot, float t)
            {
                // Use direction projected onto a plane orthogonal to player-up
                // for the band parametrisation. Stable as player orbits.
                float3 up = normalize(_PlayerUp.xyz);
                if (dot(up, up) < 0.0001) up = float3(0,1,0);
                float3 horiz = normalize(dir - up * upDot + float3(1e-4, 0, 0));
                float a1 = atan2(horiz.z, horiz.x);
                float band = sin(a1 * 4.0 + t * _AuroraSpeed * 1.3 + sin(a1*2.0 + t*0.4)*1.2);
                band = pow(saturate(band * 0.5 + 0.5), 3.0);
                // Vertical "curtain" via fbm, scaled by aurora scale.
                float curtain = fbm(float3(a1 * _AuroraScale, upDot * 6.0 + t * 0.3, t * 0.2));
                curtain = pow(saturate(curtain - 0.35), 1.4);
                float verticalMask = smoothstep(0.05, 0.35, upDot) * (1.0 - smoothstep(0.7, 1.0, upDot));
                float intensity = band * curtain * verticalMask * _AuroraIntensity;
                float3 col = lerp(_AuroraColor1.rgb, _AuroraColor2.rgb, sin(a1*1.5 + t*0.4)*0.5+0.5);
                return col * intensity;
            }

            // Nebula swirl: rotating fbm in direction space, coloured between
            // two tones. Fills the whole sky at low intensity, peaks toward
            // zenith.
            float3 nebula(float3 dir, float upDot, float t)
            {
                // Slow rotation around player-up.
                float3 up = normalize(_PlayerUp.xyz);
                if (dot(up, up) < 0.0001) up = float3(0,1,0);
                float ang = t * _NebulaSpeed;
                float c = cos(ang), s = sin(ang);
                // Build a basis perpendicular to up.
                float3 axis1 = normalize(cross(up, float3(0.0, 0.0, 1.0) + float3(1e-3, 0, 0)));
                float3 axis2 = cross(up, axis1);
                float3 rot = dir + (axis1 * c + axis2 * s) * 0.05;
                float n1 = fbm(rot * 2.4);
                float n2 = fbm(rot * 4.8 + 7.0);
                float mask = pow(saturate(n1 * 1.4 - 0.25), 1.8);
                float blend = saturate(n2);
                float3 col = lerp(_NebulaColor1.rgb, _NebulaColor2.rgb, blend);
                // Brighter near the zenith so it doesn't smear into the horizon.
                float vmask = smoothstep(-0.1, 0.6, upDot);
                return col * mask * vmask * _NebulaIntensity;
            }

            // Discoidal sun/moon. For phase, subtract a smaller offset disc
            // (only for the moon). dir/c must both be unit length.
            float discDot(float3 d, float3 c, float size)
            {
                float ang = acos(clamp(dot(d, c), -1.0, 1.0));
                return 1.0 - smoothstep(size * 0.85, size, ang);
            }

            v2f vert(appdata IN)
            {
                v2f OUT;
                OUT.vertex = UnityObjectToClipPos(IN.vertex);
                OUT.dir = IN.vertex.xyz;
                return OUT;
            }

            half4 frag(v2f IN) : SV_Target
            {
                float3 dir = normalize(IN.dir);

                // Spherical-world horizon: gradient is along the player's surface
                // normal, not world-Y. Falls back to Y up if the binder hasn't
                // pushed yet.
                float3 up = _PlayerUp.xyz;
                if (dot(up, up) < 0.0001) up = float3(0, 1, 0);
                up = normalize(up);
                float upDot = dot(dir, up);

                // Three-stop gradient. upDot in [-1..1] -> h in [0..1].
                float h = saturate(upDot * 0.5 + 0.5);
                float lo = smoothstep(0.0, _MidPoint, h);
                float hi = smoothstep(_MidPoint, 1.0, h);
                half3 sky = lerp(_ColorBottom.rgb, _ColorMid.rgb, lo);
                sky = lerp(sky, _ColorTop.rgb, hi);

                // Soft horizon glow band (subtle warm/cool ring).
                float horizonBand = exp(-pow(upDot * 4.5, 2.0)) * _HorizonGlow;
                sky += _ColorMid.rgb * horizonBand;

                // _DioTime is the synced NetworkTime; falls back to _Time.y if
                // the binder isn't running (single-player editor preview).
                float dioT = _DioTime;
                if (dioT == 0.0) dioT = _Time.y;

                int mode = (int)floor(_SkyMode + 0.5);

                // ---- Stars (modes 1, 2, 4) ----
                if (_StarDensity > 0.001)
                {
                    float starMask = smoothstep(-0.05, 0.25, upDot);
                    float s = starField(dir, _StarDensity, _StarTwinkleSpeed, dioT);
                    sky += _StarColor.rgb * s * _StarBrightness * starMask;

                    // Shooting star: a transient streak in a hashed direction.
                    if (_StarStreakChance > 0.001)
                    {
                        float t = floor(dioT * 0.8); // a new "epoch" every ~1.25s
                        float spawn = step(1.0 - _StarStreakChance, hash13(float3(t, 11.0, 7.0)));
                        if (spawn > 0.5)
                        {
                            float3 origin = normalize(float3(
                                hash13(float3(t, 1.0, 2.0)) * 2.0 - 1.0,
                                abs(hash13(float3(t, 3.0, 4.0))) * 0.6 + 0.4,
                                hash13(float3(t, 5.0, 6.0)) * 2.0 - 1.0));
                            float3 toward = normalize(float3(
                                hash13(float3(t, 7.0, 8.0)) * 2.0 - 1.0,
                                hash13(float3(t, 9.0, 10.0)) * 2.0 - 1.0,
                                hash13(float3(t, 11.0,12.0)) * 2.0 - 1.0));
                            float3 head = normalize(origin + toward * (frac(dioT * 0.8) * 0.4));
                            float dStar = acos(clamp(dot(dir, head), -1, 1));
                            float streak = exp(-dStar * 80.0) * (1.0 - frac(dioT * 0.8));
                            sky += _StarColor.rgb * streak * 4.0 * starMask;
                        }
                    }
                }

                // ---- Constellations (mode 4) ----
                if (_ConstellationStrength > 0.001)
                {
                    float c = constellations(dir);
                    float starMask = smoothstep(-0.05, 0.25, upDot);
                    sky += _ConstellationColor.rgb * c * _ConstellationStrength * starMask;
                }

                // ---- Nebula swirl (mode 2) ----
                if (_NebulaIntensity > 0.001)
                    sky += nebula(dir, upDot, dioT);

                // ---- Aurora ribbons (mode 1) ----
                if (_AuroraIntensity > 0.001)
                    sky += aurora(dir, upDot, dioT);

                // ---- Sun (modes 0, 5; very dim or off in others) ----
                float3 sunDir = normalize(_SunDir.xyz);
                float sunDot = saturate(dot(dir, sunDir));
                float disc = smoothstep(1.0 - _SunSize, 1.0 - _SunSize * 0.5, sunDot);
                float halo = pow(sunDot, _SunHaloPower) * 0.55;
                sky += _SunColor.rgb * (disc + halo) * _SunIntensity;

                // ---- Moon (modes 1, 4) ----
                if (_MoonSize > 0.001)
                {
                    float3 mDir = normalize(_MoonDir.xyz);
                    float full = discDot(dir, mDir, _MoonSize);
                    // Phase: subtract a slightly offset disc to carve a crescent.
                    // The offset is in the plane perpendicular to the view-to-moon axis.
                    float3 right = normalize(cross(mDir, up));
                    float3 shadowCenter = normalize(mDir + right * _MoonPhase * _MoonSize * 6.0);
                    float shadow = discDot(dir, shadowCenter, _MoonSize * 0.95);
                    float crescent = saturate(full - shadow * 0.95);
                    // Soft glow ring around the moon for the cartoony look.
                    float mAng = acos(clamp(dot(dir, mDir), -1, 1));
                    float glow = exp(-mAng * 18.0) * _MoonGlow;
                    sky += _MoonColor.rgb * (crescent + glow);
                }

                // ---- Clouds (modes 0, 3, 5; tuned per-mode via material) ----
                if (_CloudDensity > 0.001)
                {
                    float skyMask = smoothstep(0.0, 0.18, upDot);
                    float3 cloudPos = dir * _CloudScale;
                    cloudPos += float3(dioT * _CloudSpeed, dioT * _CloudSpeed * 0.6, 0);
                    float n  = fbm(cloudPos);
                    float ns = fbm(cloudPos * 0.5 + 13.0);
                    float cmask = smoothstep(_CloudCoverage - _CloudSoftness * 0.5,
                                             _CloudCoverage + _CloudSoftness, n);
                    cmask *= skyMask;

                    half3 cloudCol = lerp(_CloudShadow.rgb, _CloudColor.rgb,
                                          smoothstep(0.4, 0.8, ns));
                    cloudCol += _SunColor.rgb * pow(sunDot, 4.0) * 0.18;

                    // Lightning (mode 3): hashed flashes that briefly light a
                    // patch of cloud + add a global brighten pulse. Direction-
                    // stable so all peers see the same flash.
                    if (_LightningChance > 0.001)
                    {
                        float epoch = floor(dioT * 1.2);
                        float boltSpawn = step(1.0 - _LightningChance, hash13(float3(epoch, 19.0, 5.0)));
                        if (boltSpawn > 0.5)
                        {
                            float3 boltDir = normalize(float3(
                                hash13(float3(epoch, 1.0, 7.0)) * 2.0 - 1.0,
                                abs(hash13(float3(epoch, 3.0, 11.0))) * 0.5 + 0.2,
                                hash13(float3(epoch, 5.0, 13.0)) * 2.0 - 1.0));
                            float bd = acos(clamp(dot(dir, boltDir), -1, 1));
                            float fade = saturate(1.0 - frac(dioT * 1.2) * 4.0);
                            float patch = exp(-bd * 6.0) * fade;
                            // Flicker within the flash for a more electric feel.
                            float flicker = 0.5 + 0.5 * sin(dioT * 80.0 + epoch * 17.0);
                            cloudCol += _LightningColor.rgb * patch * flicker * 2.5;
                            sky += _LightningColor.rgb * fade * 0.07; // global brighten
                        }
                    }

                    sky = lerp(sky, cloudCol, cmask * _CloudDensity);
                }

                return half4(sky, 1);
            }
            ENDCG
        }
    }

    Fallback Off
}
