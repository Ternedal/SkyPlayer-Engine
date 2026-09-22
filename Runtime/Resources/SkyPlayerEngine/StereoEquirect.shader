Shader "SkyPlayer/Engine/StereoEquirect"
{
    // Unified video material:
    //   _Mode   0 = flat (mesh UV)            1 = sphere (direction-based equirect)
    //   _Hfov   180 or 360                    (sphere only)
    //   _Stereo 0 = mono  1 = SBS (L|R)        2 = OU (top/bottom)
    // Per-eye selection uses unity_StereoEyeIndex (0 = left, 1 = right).
    Properties
    {
        _MainTex ("Video", 2D) = "black" {}
        _Mode   ("Mode (0 flat,1 sphere)", Float) = 1
        _Hfov   ("Horizontal FOV", Float) = 180
        _Stereo ("Stereo (0 mono,1 SBS,2 OU)", Float) = 0
        _Span   ("Angular span (zoom: 1=full immersive, <1=smaller/further)", Float) = 1
        // AR background key: make the video's solid backdrop transparent so passthrough shows.
        _Key         ("Key background (0/1)", Float) = 0
        _SatThresh   ("Neutral key (black/white/grey bg)", Range(0,1)) = 0.15
        _GreenThresh ("Green key strength", Range(0,1)) = 0.12
        // 0 = auto (all neutrals + green), 1 = dark only, 2 = light only, 3 = green only.
        _KeyMode     ("Key mode (0 auto,1 dark,2 light,3 green)", Float) = 1
        _HoleFill    ("Fill interior holes (0/1)", Float) = 1
        // Tighter than before: a wide ring smooths over hair entirely, a tight one follows it.
        _HoleR       ("Hole-fill ring radius (UV)", Range(0,0.05)) = 0.005
        _KeyColor    ("Picked backdrop colour (pipette mode)", Color) = (0.1,0.12,0.16,1)
        _Spill       ("Spill suppression strength", Range(0,1)) = 0.6
        _Soften      ("Matte softening (anti-blockiness)", Range(0,1)) = 0.45
        _Feather     ("Key edge feather", Range(0,0.2)) = 0.03
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Off            // sphere viewed from centre (one hit/dir) + flat quad both work
        ZWrite On
        Lighting Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            #define PI 3.14159265359

            sampler2D _MainTex;
            float _Mode;
            float _Hfov;
            float _Stereo;
            float _Span;
            float _Key;
            float _SatThresh;
            float _GreenThresh;
            float _KeyMode;
            float _HoleFill;
            float _HoleR;
            float _Feather;
            float4 _KeyColor;
            float _Spill;
            float _Soften;

            // How "background-like" a colour is: 1 = key it away (show the room), 0 = keep it.
            // Soft ramp (_Feather) instead of a hard cutoff → no paper-cut edges.
            float BgWeight(fixed3 c)
            {
                float mx = max(c.r, max(c.g, c.b));
                float mn = min(c.r, min(c.g, c.b));
                float chroma = mx - mn;                       // 0 = pure grey/black/white
                float lum    = (mx + mn) * 0.5;
                float f      = max(_Feather, 0.01);

                // Directional gate. Keying ALL neutrals eats dark eyes/hair AND pale hair/skin at the
                // same time; a backdrop is only ever dark OR light, so key only in that direction.
                // Dark/light modes key on BRIGHTNESS, not chroma: block-noise in near-black video has
                // plenty of chroma, so a chroma test leaves grey mush standing where the room should be.
                float dark  = 1.0 - smoothstep(_SatThresh, _SatThresh + f * 2.0, mx);
                float light = smoothstep(1.0 - _SatThresh - f * 2.0, 1.0 - _SatThresh, mn);

                if (_KeyMode < 0.5)                              // AUTO: both ends, still neutral-ish
                {
                    float neutralAll = 1.0 - smoothstep(_SatThresh - f, _SatThresh + f, chroma);
                    return max(dark, light) * max(neutralAll, 0.35);
                }
                if (_KeyMode < 1.5) return dark;                 // SORT
                if (_KeyMode < 2.5) return light;                // HVID
                if (_KeyMode < 3.5)                              // GRØN
                    return smoothstep(_GreenThresh - f, _GreenThresh + f, c.g - max(c.r, c.b));

                if (_KeyMode < 4.5)
                {
                    // GRÅ/BLÅ. Measured from the test captures: these backdrops are never neutral —
                    // blue is the dominant channel in 55–86% of backdrop pixels and chroma reaches
                    // ~0.21, so a strict grey test rejected most of them. Allow far more chroma when
                    // the cast is COOL, and stay strict on warm casts so skin is never touched.
                    float cool = saturate((c.b - min(c.r, c.g)) / max(chroma, 0.001));
                    float warm = saturate((c.r - max(c.g, c.b)) / max(chroma, 0.001));
                    float allow = 0.16 + 0.18 * cool;            // up to ~0.34 for a properly blue-grey wall
                    float chromaOk = 1.0 - smoothstep(allow * 0.7, allow, chroma);
                    chromaOk *= 1.0 - smoothstep(0.30, 0.60, warm);
                    // _SatThresh is the LEVEL being keyed here; KEY −/+ walks it up and down.
                    // The band is wide (±0.26): a lit wall has a big luma gradient, and measuring
                    // against the captures showed ±0.16 left ~20% of the backdrop standing while
                    // ±0.26 takes 93% of it at no measurable cost to skin.
                    return chromaOk * (1.0 - smoothstep(0.32 - f, 0.32 + f, abs(lum - _SatThresh)));
                }

                if (_KeyMode < 5.5)
                {
                    // RØD. Skin is warm too, so the bar sits well above skin's red bias and the pixel
                    // must actually be lit — a dark reddish shadow is not a red screen.
                    float redness = c.r - max(c.g, c.b);
                    float t = 0.22 + _GreenThresh;
                    return smoothstep(t - f, t + f, redness) * smoothstep(0.15, 0.30, mx);
                }

                if (_KeyMode < 6.5)
                    // LILLA / magenta: red AND blue both clearly above green. Skin has green above
                    // blue, so it scores negative here and is safe.
                    return smoothstep(_GreenThresh - f, _GreenThresh + f, min(c.r, c.b) - c.g);

                // PIPET — the backdrop's actual colour, picked from the video with the pointer.
                // This is why the green key always felt better: green is one specific colour, so it
                // can be matched exactly, while "grey-ish" is a guess about a whole family. With a
                // picked colour we match distance in the CHROMA PLANE (hue+saturation, brightness
                // removed), so shadows and hot spots on the same wall are the same colour here —
                // which is exactly what a luma band could never handle.
                float3 t3 = _KeyColor.rgb;
                float tl  = (max(t3.r, max(t3.g, t3.b)) + min(t3.r, min(t3.g, t3.b))) * 0.5;
                float2 tc = float2(t3.b - tl, t3.r - tl);
                float2 pc = float2(c.b - lum, c.r - lum);
                float2 diff = pc - tc;
                float tol = max(_SatThresh, 0.03);            // KEY −/+ = tolerance here
                float tlen = length(tc);

                float k;
                if (tlen > 0.02)
                {
                    // Split the error into HUE (across the colour direction) and SATURATION (along
                    // it). A lit screen varies a lot in saturation — bright and shadowed patches of
                    // the same green wall are the same hue but different purity — while the hue
                    // itself barely moves. That directional tolerance is exactly why the dedicated
                    // green key beat a plain distance match, so the pipette now does the same and
                    // works on a greenscreen too.
                    float2 dir = tc / tlen;
                    float  par = dot(diff, dir);
                    float  per = length(diff - dir * par);
                    k = (1.0 - smoothstep(tol * 0.6, tol, per))
                      * (1.0 - smoothstep(tol * 2.5, tol * 3.4, abs(par)));
                }
                else
                {
                    // Picked something essentially neutral: no hue direction to lean on.
                    k = 1.0 - smoothstep(tol * 0.6, tol, length(diff));
                }

                float dlAllow = 0.25 + tol * 1.5;             // generous: a lit wall has a gradient
                return k * (1.0 - smoothstep(dlAllow, dlAllow + 0.12, abs(lum - tl)));
            }

            // Background weight + luma of a tap, in one sample.
            float2 BgAndLuma(float2 uv)
            {
                fixed3 c = tex2D(_MainTex, uv).rgb;
                return float2(BgWeight(c), dot(c, float3(0.299, 0.587, 0.114)));
            }

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
                float3 dir : TEXCOORD1;   // object-space direction (sphere mode)
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.dir = normalize(v.vertex.xyz); // sphere centred at object origin
                return o;
            }

            // Apply per-eye stereo offset to a [0..1] UV.
            float2 stereoUV(float2 uv, int eye)
            {
                if (_Stereo == 1)        // SBS: left = [0,0.5], right = [0.5,1]
                {
                    uv.x = uv.x * 0.5 + (eye == 1 ? 0.5 : 0.0);
                }
                else if (_Stereo == 2)   // OU: top = left, bottom = right
                {
                    uv.y = uv.y * 0.5 + (eye == 1 ? 0.0 : 0.5);
                }
                return uv;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);   // korrekt eye-index i fragment (single-pass)
                int eye = (int)unity_StereoEyeIndex;
                float2 uv;

                if (_Mode < 0.5)
                {
                    // Flat: use mesh UV directly.
                    uv = i.uv;
                }
                else if (_Mode < 1.5)
                {
                    // Sphere: equirect from the interpolated direction.
                    float3 d = normalize(i.dir);
                    float lon = atan2(d.x, d.z);          // -PI..PI
                    float lat = asin(clamp(d.y, -1.0, 1.0)); // -PI/2..PI/2

                    // _Span shrinks the angular coverage: 1 = full immersive (±90° for 180),
                    // <1 packs the video into a smaller forward cap so the user can "sit back"
                    // and see the whole scene (rest of the sphere is black). Aspect preserved
                    // by scaling longitude and latitude by the same factor.
                    float s = max(_Span, 0.05);
                    float u;
                    if (_Hfov <= 180.0)
                        u = lon / (PI * s) + 0.5;          // front 180°, scaled
                    else
                        u = lon / (2.0 * PI * s) + 0.5;    // full 360°, scaled
                    float vtex = lat / (PI * s) + 0.5;
                    if (u < 0.0 || u > 1.0 || vtex < 0.0 || vtex > 1.0)
                        return fixed4(0,0,0,0);            // outside the cap = transparent → passthrough (room) shows here
                    uv = float2(u, vtex);
                }
                else
                {
                    // Theater / AR: reproject a rectilinear (undistorted) window into the equirect
                    // onto the flat quad — a video "screen" floating in the passthrough room. ~90° hFov.
                    float tx = (i.uv.x - 0.5) * 2.0 * tan(0.7854);              // 90° horizontal
                    float ty = (i.uv.y - 0.5) * 2.0 * tan(0.7854) * (9.0/16.0); // 16:9 screen
                    float3 dd = normalize(float3(tx, ty, 1.0));
                    float lon2 = atan2(dd.x, dd.z);
                    float lat2 = asin(clamp(dd.y, -1.0, 1.0));
                    float u2   = (_Hfov <= 180.0) ? (lon2 / PI + 0.5) : (lon2 / (2.0 * PI) + 0.5);
                    float v2   = lat2 / PI + 0.5;
                    if (u2 < 0.0 || u2 > 1.0 || v2 < 0.0 || v2 > 1.0)
                        return fixed4(0,0,0,1);            // outside content → black (inside the screen rect)
                    uv = float2(u2, v2);
                }

                uv = stereoUV(uv, eye);
                fixed4 col = tex2D(_MainTex, uv);
                col.a = 1.0;   // video content is opaque; the compositor shows passthrough where alpha == 0
                // AR background key: make the video's solid backdrop transparent so passthrough (the
                // room) shows through it. Luma-key (dark/black backdrop) OR chroma-key (green screen).
                if (_Key > 0.5)
                {
                    float bg = BgWeight(col.rgb);

                    // Hole-fill: eyes, brows, mouth and dark hair are colour-identical to a black
                    // backdrop, so a pure colour key punches holes in the face. Those pixels are
                    // *enclosed* by subject — sample a ring around them; if the ring isn't
                    // background either, the pixel is an interior hole → keep it opaque.
                    if (bg > 0.01 && _HoleFill > 0.5)
                    {
                        // 8 taps. SBS/OU pack two eyes into one texture, so a UV step is twice as
                        // wide on the packed axis — halve it there to keep the ring round.
                        float rx = (_Stereo == 1) ? _HoleR * 0.5 : _HoleR;
                        float ry = (_Stereo == 2) ? _HoleR * 0.5 : _HoleR;
                        float d = 0.7;
                        float2 t0 = BgAndLuma(uv + float2( rx, 0));
                        float2 t1 = BgAndLuma(uv + float2(-rx, 0));
                        float2 t2 = BgAndLuma(uv + float2( 0, ry));
                        float2 t3 = BgAndLuma(uv + float2( 0,-ry));
                        float2 t4 = BgAndLuma(uv + float2( rx*d, ry*d));
                        float2 t5 = BgAndLuma(uv + float2(-rx*d, ry*d));
                        float2 t6 = BgAndLuma(uv + float2( rx*d,-ry*d));
                        float2 t7 = BgAndLuma(uv + float2(-rx*d,-ry*d));

                        float ring = t0.x + t1.x + t2.x + t3.x + t4.x + t5.x + t6.x + t7.x;

                        // SOFTEN THE MATTE. Video compression works in macroblocks, so in dark or
                        // low-contrast areas whole blocks flip to the same value — a per-pixel
                        // yes/no decision then produces the stair-stepped, chewed-looking hair edge.
                        // Blending each pixel's own verdict with its neighbourhood turns those steps
                        // into a gradient, which is what fine strands should look like anyway.
                        bg = lerp(bg, ring / 8.0, _Soften);

                        // Gradual, not binary: fully enclosed (ring≈0) stays opaque — eyes, brows,
                        // mouth. Half-enclosed (hair strands, silhouette edge) keeps most of its
                        // opacity instead of being chopped off. Open background (ring≈8) keys fully.
                        bg *= smoothstep(1.5, 6.5, ring);

                        // Texture test — the one thing that separates BLACK HAIR from a BLACK
                        // BACKDROP: a backdrop is flat, hair is strands, so the local luma swings
                        // hard. Compression noise stays under the dead zone and is unaffected.
                        float lc = dot(col.rgb, float3(0.299, 0.587, 0.114));
                        float detail = abs(t0.y - lc) + abs(t1.y - lc) + abs(t2.y - lc) + abs(t3.y - lc)
                                     + abs(t4.y - lc) + abs(t5.y - lc) + abs(t6.y - lc) + abs(t7.y - lc);
                        // Dead zone raised and protection weakened: at a body's edge the luma also
                        // swings, so a generous setting kept a rim of the video's own lighting spill
                        // opaque — that was the glow outlining the performer. Only real strand-level
                        // texture (hair) should survive now.
                        bg *= 1.0 - smoothstep(0.35, 1.0, detail) * 0.60;

                        // BITE MARKS. The small ring only closes holes a few pixels across, but the
                        // ones eaten out of a head of hair are far bigger — the captures show them
                        // as ragged black patches in the middle of the hair while the body outline
                        // is clean. So in the edge zone only (where the small ring is mixed, i.e. a
                        // few percent of the frame) take four taps at 4x the radius: if that wide
                        // ring is mostly SUBJECT, this pixel sits inside the silhouette and must
                        // stay opaque no matter what its own colour says.
                        if (ring > 0.5 && ring < 7.5)
                        {
                            float wx = rx * 4.0, wy = ry * 4.0;
                            float wide = BgWeight(tex2D(_MainTex, uv + float2( wx, 0)).rgb)
                                       + BgWeight(tex2D(_MainTex, uv + float2(-wx, 0)).rgb)
                                       + BgWeight(tex2D(_MainTex, uv + float2( 0, wy)).rgb)
                                       + BgWeight(tex2D(_MainTex, uv + float2( 0,-wy)).rgb);
                            // Wide ramp: a narrow one turns the four-tap verdict into hard blocky
                            // boundaries of its own, which is part of what reads as pixelation.
                            bg *= smoothstep(0.6, 2.8, wide);
                        }
                    }

                    col.a = 1.0 - bg;                        // partial alpha = soft blend with the room
                    if (col.a <= 0.004) return fixed4(0,0,0,0);

                    // Un-mix the backdrop out of half-keyed pixels. A hair pixel that is 60% backdrop
                    // still *carries* the backdrop's colour, so without this it reads as grey smoke
                    // around the head. Subtract the backdrop's contribution and re-normalise.
                    // Boost is CLAMPED: dividing a near-black hair pixel by a tiny (1-bg) blew it up
                    // into white noise streaks, which is exactly what black hair looked like before.
                    // Only for the neutral-ish modes — for a coloured screen we don't know the exact
                    // backdrop colour, so spill suppression below does that job instead.
                    bool neutralMode = (_KeyMode < 2.5) || (_KeyMode > 3.5 && _KeyMode < 4.5)
                                    || (_KeyMode > 6.5);
                    if (bg > 0.01 && bg < 0.85 && neutralMode)
                    {
                        float mx2 = max(col.r, max(col.g, col.b));
                        float3 bgCol = float3(0,0,0);
                        if (_KeyMode < 0.5)       bgCol = (mx2 > 0.5) ? float3(1,1,1) : float3(0,0,0);
                        else if (_KeyMode > 1.5 && _KeyMode < 2.5) bgCol = float3(1,1,1);
                        else if (_KeyMode > 6.5)  bgCol = _KeyColor.rgb;   // exact — we picked it
                        else if (_KeyMode > 3.5)  bgCol = float3(_SatThresh, _SatThresh, _SatThresh);
                        col.rgb = saturate((col.rgb - bgCol * bg) / max(1.0 - bg, 0.85));
                    }

                    // SPILL. A coloured backdrop bounces its colour onto skin and hair — and that
                    // happens on FULLY OPAQUE pixels, not just the semi-transparent edge, which is
                    // why scaling this by the alpha (as it used to) missed the visible part of the
                    // problem entirely. Remove the component of the pixel's colour that points along
                    // the backdrop's hue, and leave everything perpendicular to it untouched, so a
                    // genuinely red dress under a red screen keeps its own colour.
                    if (_Spill > 0.01 && _KeyMode > 2.5)
                    {
                        float3 dir;
                        if (_KeyMode < 3.5)      dir = float3(-0.5,  1.0, -0.5);   // green
                        else if (_KeyMode < 4.5) dir = float3(-0.4, -0.2,  0.6);   // grey/blue (cool)
                        else if (_KeyMode < 5.5) dir = float3( 1.0, -0.5, -0.5);   // red
                        else if (_KeyMode < 6.5) dir = float3( 0.5, -1.0,  0.5);   // purple
                        else
                        {
                            float3 t4 = _KeyColor.rgb;
                            float  tl4 = (max(t4.r, max(t4.g, t4.b)) + min(t4.r, min(t4.g, t4.b))) * 0.5;
                            dir = t4 - tl4;                                        // pipette: picked hue
                        }
                        float dn = length(dir);
                        if (dn > 0.001)
                        {
                            dir /= dn;
                            float l2 = (max(col.r, max(col.g, col.b)) + min(col.r, min(col.g, col.b))) * 0.5;
                            float amt = max(dot(col.rgb - l2, dir), 0.0);
                            col.rgb = saturate(col.rgb - dir * amt * _Spill);
                        }
                    }
                }
                return col;
            }
            ENDCG
        }
    }
    Fallback "Unlit/Texture"
}
