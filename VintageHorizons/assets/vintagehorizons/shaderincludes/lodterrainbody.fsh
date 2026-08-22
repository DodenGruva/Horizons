
// Fog application and far sky-fade structure adapted from Farseer's region.fsh
// (github.com/ViciousBadger/VSMod-Farseer, MIT, (c) Badgerson). Unlike Farseer we
// have real per-vertex surface colors, shaded with screen-space-derivative normals.

in vec4 worldPos;
in vec4 vertexColor;
in float yLevel;
in vec4 rgbaFog;
in float dist;
in float radialDistance;
in float fogAmount;
in float edgeFade;
in vec3 tint;
in vec3 terrainPos;
in vec3 sectionLocal;

uniform float fogDensityIn;
uniform float fogMinIn;
uniform float horizonFog;
uniform vec3 sunPosition;
uniform vec3 sunColor;
uniform float dayLight;
uniform float cacheHandoffDistance;

// Per-cell vanilla ownership. One texel per 32x32x32 vanilla render chunk, stored as Y
// slices stacked down a single 2D texture because the public client API binds 2D textures
// only. maskEnabled stays 0 until the chunk mask is both requested and uploaded, so the
// default path is exactly the distance handoff above. Addressing must match
// VanillaReadinessMask.TexelIndex; a static check holds the two together.
uniform sampler2D readinessMask;
uniform int maskEnabled;
uniform int maskMinX;
uniform int maskMinZ;
uniform int maskWidth;
uniform int maskDepth;
uniform int maskCapacity;
uniform int maskVerticalChunks;

// 1 paints hidden pixels red instead of hiding them. Diagnostic only.
uniform int maskDebug;

// 1 applies vanilla's own rule that an up-facing surface never darkens with the sun.
uniform int flatTopLight;

// The four terms below each close one measured gap between this shader and vanilla's
// chunkopaque pair, and each has its own switch so a single dusk-to-night sweep can
// attribute what it sees. See .vhlight. All default to on.
//
// 1 shades by the engine's own light vector, which follows the sun by day and swings
// to the moon once moonlight beats sunlight; 0 keeps shading by the sun all night.
// lightPosition is declared by fogandlight.fsh and uploaded by the engine on every
// Use(), so it costs nothing to read here.
uniform int lightMoonDir;

// 1 uses vanilla's ramp and floor, max(0.45, 0.5 + 0.5 * dot); 0 keeps the mod's
// 0.55 + 0.45 * max(0, dot), which is 22% too bright on every away-facing slope.
uniform int lightVanillaRamp;

// 1 lights by the engine's blended ambient colour, which is what vanilla's terrain
// actually multiplies by; 0 keeps sunColor * dayLight. This is the dominant term:
// vanilla builds its ambient from ReflectColor, which is floored at a blue night
// colour once the sun is down, while sunColor stays on the orange end of the sunlight
// ramp. Measured offline, the two hues invert after sundown - vanilla goes blue-grey
// and this shader goes orange.
uniform int lightAmbientColor;
uniform vec3 rgbaAmbientIn;

// 1 applies vanilla's daylight brightening, 1 + max(0, shadowIntensity * 2 - 1.66)/1.5.
// shadowIntensity is the engine's DropShadowIntensity, which is 1 while the sun is high
// and falls to 0 as it sets, so this is worth 22.7% extra brightness at midday and
// nothing at dusk. Also declared and uploaded by fogandlight.fsh.
uniform int lightDayBoost;

// 1 mixes the far dissolve band toward the sky the engine actually draws. Every engine
// shader that calls getSkyColorAt passes SkyDaylight, not DayLightStrength; the two
// differ by a factor of 1.25 and by a night floor, so the band this shader fades terrain
// into did not match the sky behind it, and the mismatch moved with the time of day.
// SkyDaylight is internal to the engine, but every input to it is public, so the renderer
// reconstructs it. See .vhlight sky.
uniform int lightSkyDayLight;
uniform float skyDayLight;

// ---- Per-section facts ------------------------------------------------------
//
// The two values below describe the section rather than the fragment. The established
// path sets each as a uniform before that section's draw; the indirect variant has no
// per-draw uniforms and receives them from the vertex stage as flat varyings, filled
// from LodGpuSectionRecord. The #defines keep the body below identical in both
// variants. See the matching block in lodterrainbody.vsh.
#ifdef VH_INDIRECT
flat in float vhColumnBlocks;
flat in ivec2 vhMaskSectionOrigin;

#define columnBlocks vhColumnBlocks
#define maskSectionOriginX vhMaskSectionOrigin.x
#define maskSectionOriginZ vhMaskSectionOrigin.y
#else
// This section's origin in whole vanilla chunks. Section origins are multiples of the
// chunk size, so this is exact, and adding a small local offset to it cannot round.
//
// Two scalars rather than one ivec2 on purpose. The client's only vector setter for a
// pair of integers is Uniform(name, Vec2i), which calls glUniform2f; against an integer
// uniform that is GL_INVALID_OPERATION and the value never arrives at all. See G42.
uniform int maskSectionOriginX;
uniform int maskSectionOriginZ;

// Blocks per column in the section being drawn (1 at level 0, doubling per level).
// Coarse sections merge whole neighbourhoods into one colour, and greedy meshing
// then fuses them into large single-colour quads; a little world-space variation
// scaled to the column size breaks those plates up without inventing detail.
// Scaling by column size is what keeps the pattern roughly constant on screen
// instead of aliasing into shimmer at distance.
uniform float columnBlocks;
#endif
// -----------------------------------------------------------------------------

// Live tint table. The alpha byte carries a tint SLOT plus a blend band:
//   0..63    opaque,     slot = alpha
//   64..127  water,      slot = alpha - 64
//   128..191 thin plant, slot = alpha - 128
// Slot 0 is the identity tint. One slot per distinct (climate map, season map) pair,
// because leaves pick a seasonal map per species and water has its own -- a single
// shared foliage tint left every tree the same colour and water untinted grey.
// Sampled at two heights and blended by vertex height: the climate maps are indexed
// by temperature, which drops with altitude, so one sample at the player's feet gave
// mountaintops the same lush green as the valley floor.
// Must equal LodTintRegistry.MaxSlots; LoadShader logs an error if it does not.
const int TINT_SLOTS = 64;
uniform float snowLineY;

// Blend factors per band, now that alpha carries the slot instead of an opacity.
// Flowers are crossed quads in vanilla; as a solid cube they read as a grey blob, so
// they are drawn mostly see-through and the ground shows through them.
const float WATER_ALPHA = 0.66;
const float THIN_ALPHA = 0.50;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

#include noise3d.ash
#include dither.fsh
#include fogandlight.fsh
#include skycolor.fsh
#include underwatereffects.fsh

void main()
{
    // Retain a broad fallback overlap for chunks that are still streaming, then give
    // the close field entirely to ready vanilla terrain so the approximate surfaces do
    // not mix. This boundary is deliberately much nearer than the old 78.5% cutoff.
    if (radialDistance < cacheHandoffDistance || dist > 1.0) discard;

    // Ownership is decided before normals, noise, tint, lighting, fog, sky and water work,
    // so an owned fragment costs one point fetch rather than a whole shaded pixel. Every
    // address outside the tracked window, above or below the world, or reading a
    // cache-owned texel falls through to normal cached drawing: the mask can only take
    // ground away from the cache where vanilla has proven it draws there.
    if (maskEnabled == 1)
    {
        int cellX = maskSectionOriginX + int(floor(sectionLocal.x / 32.0));
        int cellY = int(floor(sectionLocal.y / 32.0));
        int cellZ = maskSectionOriginZ + int(floor(sectionLocal.z / 32.0));
        if (cellY >= 0 && cellY < maskVerticalChunks
            && cellX >= maskMinX && cellX < maskMinX + maskWidth
            && cellZ >= maskMinZ && cellZ < maskMinZ + maskDepth)
        {
            int wrap = maskCapacity - 1;
            ivec2 maskTexel = ivec2(cellX & wrap, (cellZ & wrap) + cellY * maskCapacity);
            if (texelFetch(readinessMask, maskTexel, 0).r > 0.5)
            {
                // Debug mode paints instead of discarding, which turns an ambiguous gap
                // into a yes/no question: red means the mask hid this pixel, and a gap
                // that stays empty in this mode was never the mask's doing.
                if (maskDebug == 1)
                {
                    outColor = vec4(1.0, 0.0, 0.0, 1.0);
                    outGlow = vec4(0.0);
                    outGNormal = vec4(0.0);
                    outGPosition = vec4(0.0);
                    return;
                }
                discard;
            }
        }
    }

    // Flat-shaded facet normal from position derivatives - no normals in the mesh.
    vec3 normal = normalize(cross(dFdx(worldPos.xyz), dFdy(worldPos.xyz)));

    // sunPosition arrives as Calendar.SunPositionNormalized, so it is already a unit
    // vector. The call below passes it to getSkyColorAt unnormalized for the same reason.
    //
    // lightPosition is the engine's own shading vector: the sun position lerped toward
    // the moon as moonlight overtakes sunlight. Vanilla terrain shades by that, not by
    // the sun, so at night vanilla lights slopes from the moon while this shader was
    // still lighting them from a sun below the horizon.
    vec3 lightVec = lightMoonDir == 1 ? lightPosition : sunPosition;
    float sunAngle = dot(normal, lightVec);
    float shade = lightVanillaRamp == 1
        ? max(0.45, 0.5 + 0.5 * sunAngle)
        : 0.55 + 0.45 * max(0.0, sunAngle);

    // Vanilla never lets an up-facing surface darken as the sun drops. Its
    // getBrightnessFromNormal floors the shade at normal.y * 0.95, with a comment in the
    // engine's own source saying that block tops coming out darker than block sides looks
    // uncanny; its liquid shader does not shade by normal at all. Without the same floor,
    // cached ground fell to 0.55 at dawn and dusk while the vanilla ground beside it stayed
    // near 0.95, and the two only matched around midday - reported as the colour matching
    // well at some times of day and not others, after the albedo itself was already exact.
    //
    // A max, so this can only ever brighten: cliffs and side faces keep the shading they
    // had, and only surfaces that actually face upwards are affected.
    //
    // Behind a switch because it is a judgement call about how distant ground should look at
    // dawn and dusk, and the only way to settle that is to flip it while looking at the
    // ground. See .vhtoplight.
    if (flatTopLight == 1) shade = max(shade, clamp(normal.y, 0.0, 1.0) * 0.95);

    // Decode the tint slot, then snow line on up-facing terrain.
    // Only the blend band is needed here; the tint itself arrives interpolated.
    int band = int(vertexColor.a * 255.0 + 0.5) / TINT_SLOTS;  // 0 opaque, 1 water, 2 thin
    bool translucent = band > 0;

    vec3 albedo = vertexColor.rgb * tint;
    float outAlpha = band == 2 ? THIN_ALPHA : (band == 1 ? WATER_ALPHA : 1.0);

    if (!translucent) {
        float upness = clamp(normal.y, 0.0, 1.0);
        float snowMix = smoothstep(snowLineY, snowLineY + 24.0, yLevel) * upness;
        albedo = mix(albedo, vec3(0.93, 0.94, 0.97), snowMix);
    }

    // Water is a smooth surface; only break up land.
    if (!translucent) {
        float period = max(4.0, columnBlocks * 6.0);
        float n = valuenoise(terrainPos / period);
        albedo *= 1.0 + 0.10 * (n - 0.5);
    }

    // Vanilla's light term, derived by reading applyLight in fogandlight.vsh for the
    // case a LOD section is in: full sky light, no baked block light, no glow. Every
    // scale factor there cancels except the contrast constant, leaving the pixel
    // multiplied by 1.05 * ambientColor. The ambient colour already carries the sunset
    // hue, the night blue, and the scene-brightness dimming, so nothing here has to
    // re-derive any of them.
    vec3 light = lightAmbientColor == 1
        ? 1.05 * rgbaAmbientIn
        : clamp(sunColor * dayLight, 0.02, 1.0);
    if (lightDayBoost == 1) light *= 1.0 + max(0.0, shadowIntensity * 2.0 - 1.66) / 1.5;

    vec4 terraColor = vec4(albedo, outAlpha);
    terraColor.rgb *= shade * light;

    terraColor = applyFog(terraColor, fogAmount);
    terraColor = applySpheresFog(terraColor, fogAmount, worldPos.xyz);

    // Dissolve both the far edge of the cache and the edges of the explored area
    // into the sky, so neither ends in a visible wall.
    float fade = max(smoothstep(0.75, 1.0, dist), edgeFade);

    // Only work out the sky where it is actually mixed in. mix(x, y, 0.0) is x, so
    // skipping this where fade is zero cannot change a pixel, and fade is zero across
    // the inner part of the band, which is most of the terrain on screen.
    //
    // It is worth skipping. getSkyColorAt costs several texture fetches, three
    // normalize calls, a pow, and a noise chain of eight sin calls, per fragment,
    // and all of it was being multiplied by zero.
    //
    // The branch is coherent: fade is a function of distance, so the fragments that
    // take it are a ring near the horizon rather than a speckle across the screen.
    if (fade > 0.0) {
        vec4 skyColor = vec4(1.0);
        vec4 skyGlow = vec4(1.0);
        vec3 worldPosInSky = normalize(worldPos.xyz) * 250.0;
        // Only the sky colour takes the corrected value. The glow clamp below is this
        // mod's own night dimming, not a transcription of vanilla, and its 0.05 constant
        // is calibrated against DayLightStrength - SkyDaylight never falls below 0.0625,
        // so feeding it here would leave a faint glow burning all night.
        float skyLight = lightSkyDayLight == 1 ? skyDayLight : dayLight;
        getSkyColorAt(worldPosInSky, sunPosition, 0.25, clamp(skyLight, 0.0, 1.0), horizonFog, skyColor, skyGlow);
        float murkiness = max(0.0, getSkyMurkiness() - 14.0 * fogDensityIn);
        skyColor.rgb = applyUnderwaterEffects(skyColor.rgb, murkiness);
        skyGlow.y *= clamp((dayLight - 0.05) * 2.0 - 50.0 * murkiness, 0.0, 1.0);

        outColor = mix(terraColor, skyColor, fade);
        outGlow = mix(vec4(0.0), skyGlow, fade);
    } else {
        outColor = terraColor;
        outGlow = vec4(0.0);
    }

#if SSAOLEVEL > 0
    outGPosition = vec4(0.0);
    outGNormal = vec4(0.0);
#endif
}
