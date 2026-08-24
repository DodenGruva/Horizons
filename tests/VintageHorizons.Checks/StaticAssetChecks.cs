using System.Text;
using System.Text.RegularExpressions;

namespace VintageHorizons.Checks;

/// <summary>
/// Invariants that live across file boundaries, where no compiler or runtime check can
/// reach. Everything here reads committed files off disk and touches no game type at all.
/// </summary>
public static class StaticAssetChecks
{
    public static void Run(Check c)
    {
        AsciiOnly(c);
        TextFilesAreUtf8(c);
        TintSlotAgreement(c);
        AlphaPacking(c);
        LodFallbackAndStableColour(c);
        VanillaLightingWiring(c);
        ReadinessShadowWiring(c);
        ReadinessTelemetryContract(c);
        OwnershipMaskWiring(c);
        OcclusionCullingWiring(c);
        PersistenceCadenceWiring(c);
        VersionAgreement(c);
        AssistServeLoopDoesNotLogProgress(c);
        ChatCommandNamesAreUnique(c);
        ConfigDialogWiring(c);
        NoIntegerVectorUniforms(c);
        SourceHasNoControlCharacters(c);
        IndirectShaderVariant(c);
    }

    /// <summary>
    /// The indirect path's acceptance gate is that it draws pixels identical to the
    /// established path. That is only a real gate while the two cannot drift: they share
    /// one body file, and the only difference between the programs is one define.
    ///
    /// This holds four things a silent edit could break. That the wrappers differ by the
    /// define alone, so nobody can start "just tweaking" the fast variant. That the
    /// per-section attributes sit at the locations the draw backend points them at, since
    /// a mismatch there addresses the wrong value for every section at once and looks
    /// like a geometry bug. That both stages agree about the two values the vertex stage
    /// forwards. And that the vertex body no longer declares the per-section uniforms
    /// outside their variant block, which would shadow the defines and fail to compile
    /// only in the variant nobody builds by default.
    /// </summary>
    static void IndirectShaderVariant(Check c)
    {
        string root = GameAssemblies.RepoRoot;
        string renderer = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src",
            "Render", "LodTerrainRenderer.cs"));
        string mod = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src",
            "VintageHorizonsModSystem.cs"));
        string bench = File.ReadAllText(Path.Combine(root, "scripts", "bench-windows.ps1"));
        string packedBackend = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src",
            "Render", "Gpu", "LodGpuPackedDrawBackend.cs"));

        // Default off, with an explicit on override, and reachable from a scripted run.
        // The phase gate is a controlled A/B; a switch that can only be typed in game
        // cannot be pinned for a route, and a comparison whose switches were set by hand
        // is how session 40 lost a run.
        c.True(renderer.Contains("Environment.GetEnvironmentVariable(\"VINTAGEHORIZONS_GPU_INDIRECT\") == \"1\"",
                StringComparison.Ordinal),
            "batched drawing is off unless the environment turns it on");
        c.True(mod.Contains("ChatCommands.Create(\"vhindirect\")", StringComparison.Ordinal),
            "and can still be flipped live for a side-by-side look");
        c.True(bench.Contains("VINTAGEHORIZONS_GPU_INDIRECT", StringComparison.Ordinal)
            && bench.Contains("$GpuIndirect", StringComparison.Ordinal),
            "the benchmark runner can pin either side of the comparison for a whole run");
        c.True(renderer.Contains("VINTAGEHORIZONS_GPU_PACKED", StringComparison.Ordinal)
            && mod.Contains("ChatCommands.Create(\"vhpacked\")", StringComparison.Ordinal)
            && bench.Contains("$GpuPacked", StringComparison.Ordinal),
            "packed drawing has live and whole-run comparison controls");
        c.True(packedBackend.Contains("GL.MultiDrawElementsIndirect(", StringComparison.Ordinal)
            && packedBackend.Contains(
                "GL.BindBuffer(BufferTarget.ElementArrayBuffer, sharedIndexBuffer);",
                StringComparison.Ordinal)
            && !packedBackend.Contains("GL.MultiDrawArraysIndirect(", StringComparison.Ordinal),
            "the packed backend shades four indexed corners rather than six array vertices");

        // Delayed occlusion cannot run under batching, so the pair must stay comparable
        // only when it is off on both sides. The renderer enforces that itself.
        c.True(renderer.Contains("&& !indirectDrawingThisFrame", StringComparison.Ordinal),
            "delayed occlusion is suspended while batched drawing is on, in one place");

        string shaders = Path.Combine(
            GameAssemblies.RepoRoot, "VintageHorizons", "assets", "vintagehorizons", "shaders");

        foreach (string stage in new[] { "vsh", "fsh" })
        {
            string plain = File.ReadAllText(Path.Combine(shaders, "lodterrain." + stage));
            string indirect = File.ReadAllText(Path.Combine(shaders, "lodterrainindirect." + stage));
            string packed = File.ReadAllText(Path.Combine(shaders, "lodterrainpacked." + stage));

            c.True(plain.Contains("#include lodterrainbody." + stage, StringComparison.Ordinal),
                $"the established {stage} program includes the shared body");
            c.True(indirect.Contains("#include lodterrainbody." + stage, StringComparison.Ordinal),
                $"the indirect {stage} program includes the same shared body");
            c.False(plain.Contains("VH_INDIRECT", StringComparison.Ordinal),
                $"the established {stage} program does not define the indirect switch");
            c.True(indirect.Contains("#define VH_INDIRECT 1", StringComparison.Ordinal),
                $"the indirect {stage} program defines the indirect switch");
            c.True(packed.Contains("#include lodterrainbody." + stage, StringComparison.Ordinal)
                && packed.Contains("#define VH_INDIRECT 1", StringComparison.Ordinal)
                && packed.Contains("#define VH_PACKED 1", StringComparison.Ordinal),
                $"the packed {stage} program changes only both shared-body switches");

            // A wrapper is a version line, an extension line, comments, and an include.
            // Anything else in one is shader code that exists in only one variant.
            c.Eq(3, CodeLines(plain),
                $"the established {stage} wrapper is only a version, an extension and an include");
            c.Eq(4, CodeLines(indirect),
                $"the indirect {stage} wrapper adds only the define");
            c.Eq(4, CodeLines(packed),
                $"the packed {stage} wrapper is version, two defines, and the shared include");
        }

        string vertex = ShaderBody("vsh");
        string fragment = ShaderBody("fsh");

        // Attribute locations, against the offsets the draw backend binds them to.
        foreach ((int location, string name) in new[]
        {
            (2, "vhRecordOrigin"), (3, "vhRecordNoise"),
            (4, "vhRecordMask"), (5, "vhRecordOpenEdges"),
        })
        {
            c.True(vertex.Contains($"layout(location = {location}) in ", StringComparison.Ordinal)
                && vertex.Contains(name + ";", StringComparison.Ordinal),
                $"the indirect vertex body reads {name} from attribute {location}");
        }

        c.True(vertex.Contains("flat out float vhColumnBlocks;", StringComparison.Ordinal)
            && fragment.Contains("flat in float vhColumnBlocks;", StringComparison.Ordinal),
            "the column size is handed to the fragment stage as a flat varying");
        c.True(vertex.Contains("flat out ivec2 vhMaskSectionOrigin;", StringComparison.Ordinal)
            && fragment.Contains("flat in ivec2 vhMaskSectionOrigin;", StringComparison.Ordinal),
            "the chunk origin is handed over as flat integers, never interpolated floats");

        // Every per-section uniform must sit inside the variant block. A stray declaration
        // outside it collides with the define and only breaks the indirect build.
        foreach (string declaration in new[]
        {
            "uniform mat4 modelMatrix;", "uniform vec4 noiseOrigin;",
            "uniform vec4 openEdges;", "uniform float sectionSize;",
        })
        {
            c.Eq(1, Occurrences(vertex, declaration),
                $"the vertex body declares {declaration} exactly once, inside the variant block");
        }
        foreach (string declaration in new[]
        {
            "uniform int maskSectionOriginX;", "uniform int maskSectionOriginZ;",
            "uniform float columnBlocks;",
        })
        {
            c.Eq(1, Occurrences(fragment, declaration),
                $"the fragment body declares {declaration} exactly once, inside the variant block");
        }

        // The vertex body branches twice - once to choose uniforms or attributes, once at
        // the end of main to fill the varyings - and the fragment body once. A body that
        // branched in five places would be two shaders again, written in one file.
        c.Eq(2, Occurrences(vertex, "#ifdef VH_INDIRECT"),
            "the vertex body branches on the variant exactly twice");
        c.Eq(1, Occurrences(fragment, "#ifdef VH_INDIRECT"),
            "the fragment body branches on the variant exactly once");
        c.Eq(1, Occurrences(vertex, "#ifndef VH_PACKED"),
            "only the vertex inputs change for packed pulling");
        c.Eq(1, Occurrences(vertex, "#ifdef VH_PACKED"),
            "and one main block decodes the pulled quad");
        c.True(vertex.Contains("uint vhQuad = uint(gl_VertexID) / 4u;", StringComparison.Ordinal)
            && vertex.Contains("uint vhLogicalCorner = uint(gl_VertexID) % 4u;", StringComparison.Ordinal)
            && vertex.Contains("uint vhBase = vhQuad * 3u;", StringComparison.Ordinal)
            && vertex.Contains("layout(std430, binding = 0) readonly buffer VhPackedQuadBuffer",
                StringComparison.Ordinal),
            "the packed variant pulls three scalar words for four reusable corners");

        // One #else per stage: the declaration block. The second vertex branch only fills
        // the varyings and has no established-path half, and the SSAO branches the engine
        // includes bring in are #if, not #ifdef, so they are not counted here.
        c.Eq(2, Occurrences(vertex, "#else"),
            "only the input and per-section declaration blocks have two halves");
        c.Eq(1, Occurrences(fragment, "#else"), "and the same in the fragment body");
    }

    /// <summary>Non-blank, non-comment lines: what a wrapper actually compiles.</summary>
    static int CodeLines(string source) => source
        .Split('\n')
        .Count(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("//", StringComparison.Ordinal));

    static int Occurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>
    /// The shader body, which is where every rule below actually lives. The two programs
    /// under assets/.../shaders are three-line wrappers that include it: one plain, one
    /// with VH_INDIRECT defined. Checks read the body rather than a wrapper, because a
    /// rule that held in only one variant would be worse than no rule at all.
    /// </summary>
    static string ShaderBody(string stage) => File.ReadAllText(Path.Combine(
        GameAssemblies.RepoRoot, "VintageHorizons", "assets", "vintagehorizons",
        "shaderincludes", "lodterrainbody." + stage));

    static void PersistenceCadenceWiring(Check c)
    {
        string src = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src");
        string pipeline = File.ReadAllText(Path.Combine(src, "Lod", "LodPipeline.cs"));
        string storage = File.ReadAllText(Path.Combine(src, "Storage", "LodStorageThread.cs"));
        string renderer = File.ReadAllText(Path.Combine(src, "Render", "LodTerrainRenderer.cs"));
        string assist = File.ReadAllText(Path.Combine(src, "Net", "LodAssistServerSystem.cs"));
        string modSystem = File.ReadAllText(Path.Combine(src, "VintageHorizonsModSystem.cs"));

        c.True(pipeline.Contains("PersistenceCheckpointIntervalMs = 30_000", StringComparison.Ordinal),
            "LOD persistence uses a 30-second checkpoint interval");
        c.True(pipeline.Contains("autoCommitSaves: false", StringComparison.Ordinal),
            "the live pipeline keeps snapshots in RAM until checkpoint publication");
        c.True(storage.Contains("store.SaveBatch(batch)", StringComparison.Ordinal),
            "a published checkpoint uses the batched SQLite path");
        c.True(renderer.Contains("SeasonalRefreshIntervalMs = 30_000", StringComparison.Ordinal),
            "seasonal tint refresh uses a 30-second cadence");
        c.True(renderer.Contains("MeshEvictionChecksPerFrame", StringComparison.Ordinal),
            "mesh eviction is a rolling per-frame queue");
        c.False(renderer.Contains("EvictSweepInterval", StringComparison.Ordinal),
            "the renderer no longer performs periodic full mesh sweeps");
        c.True(renderer.Contains("TemporalOcclusionQueryIssuesPerFrame", StringComparison.Ordinal)
            && renderer.Contains("TemporalOcclusionResultChecksPerFrame", StringComparison.Ordinal),
            "render-context occlusion queries have fixed per-frame issue and result budgets");
        c.True(assist.Contains("MeasureOfferNewKeys(), 30000", StringComparison.Ordinal),
            "server manifest follow-up scans align with the 30-second checkpoint");
        c.True(modSystem.Contains("localOffers.RequestBlob", StringComparison.Ordinal)
            && modSystem.Contains("localOffers.TryTakeBlobResult", StringComparison.Ordinal),
            "singleplayer sibling-cache blob reads use the background I/O worker");
        c.False(modSystem.Contains("localOffers.Blob(", StringComparison.Ordinal),
            "the game tick never performs a synchronous sibling-cache SQLite read");
    }

    /// <summary>
    /// Shader source must be pure ASCII. OpenTK passes managed strings to GL by handing
    /// over a char count where the driver reads utf8 bytes, so a single non-ASCII
    /// character silently truncates the source by (utf8 bytes - chars) characters. The
    /// tail of the shader just disappears; there is no error, only wrong output.
    ///
    /// Scans the whole asset tree rather than a list of known shaders, so a file added
    /// later is covered without anyone remembering to add it here.
    /// </summary>
    /// <summary>
    /// Every tracked text file must decode as UTF-8. Windows tooling defaults to cp1252,
    /// so an editing pass that reads and rewrites a file without naming an encoding turns
    /// one em dash into a lone 0x97 byte. The round trip is byte-preserving for content it
    /// did not touch, which is exactly why it goes unnoticed: the file still opens, the
    /// documentation checks still pass, and only the characters that pass actually
    /// change. Introduced twice in one session before this check existed.
    /// </summary>
    static void TextFilesAreUtf8(Check c)
    {
        string root = GameAssemblies.RepoRoot;
        string[] extensions = [".md", ".cs", ".json", ".ps1", ".sh", ".py", ".vsh", ".fsh", ".ash", ".txt"];
        var offenders = new List<string>();
        int scanned = 0;

        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
            string relative = Path.GetRelativePath(root, path);
            if (relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || relative.StartsWith(".testdata", StringComparison.Ordinal)
                || relative.StartsWith(".git", StringComparison.Ordinal)
                || relative.StartsWith("dist", StringComparison.Ordinal)
                || relative.StartsWith("NuGetScratch", StringComparison.Ordinal)) continue;

            scanned++;
            byte[] bytes = File.ReadAllBytes(path);
            try
            {
                new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (ArgumentException)
            {
                int at = FirstInvalidByte(bytes);
                offenders.Add($"{relative} byte {at} = 0x{bytes[at]:X2}");
            }
        }

        c.SeqEq(Array.Empty<string>(), offenders, $"all {scanned} tracked text files decode as UTF-8");
    }

    static int FirstInvalidByte(byte[] bytes)
    {
        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] < 0x80) continue;
            try { strict.GetString(bytes, i, Math.Min(4, bytes.Length - i)); }
            catch (ArgumentException) { return i; }
        }
        return 0;
    }

    static void AsciiOnly(Check c)
    {
        string assets = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "assets");
        c.True(Directory.Exists(assets), "asset directory exists");

        var offenders = new List<string>();
        int scanned = 0;

        foreach (string path in Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories))
        {
            // Binary assets are exempt: a PNG is full of high bytes by definition.
            if (Path.GetExtension(path) is ".png" or ".jpg" or ".ogg" or ".wav") continue;

            scanned++;
            byte[] bytes = File.ReadAllBytes(path);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] >= 0x80)
                {
                    offenders.Add($"{Path.GetRelativePath(GameAssemblies.RepoRoot, path)} byte {i} = 0x{bytes[i]:X2}");
                    break;
                }
            }
        }

        c.True(scanned > 0, "found asset files to scan");
        c.SeqEq(Array.Empty<string>(), offenders, $"all {scanned} text assets are pure ASCII");
    }

    /// <summary>
    /// The shaders carry their own `const int TINT_SLOTS` because this game version offers
    /// no way to inject a #define, and a mismatch decodes water as opaque and thin plants
    /// as water with no compile error.
    ///
    /// This used to be guarded at shader load by comparing MaxSlots against a second C#
    /// constant that mirrored the shader's value by hand - two constants in the same file,
    /// which cannot detect a shader being edited at all. The compiler said as much: that
    /// branch raised CS0162, unreachable code. Both the mirror and the dead guard are gone.
    ///
    /// Reading the shader files is the only check that can actually close it, and it also
    /// catches the .vsh and .fsh disagreeing with each other, which nothing did before.
    /// </summary>
    static void TintSlotAgreement(Check c)
    {
        string assets = Path.Combine(
            GameAssemblies.RepoRoot, "VintageHorizons", "assets", "vintagehorizons");

        var found = new Dictionary<string, int>();
        foreach (string path in Directory.EnumerateFiles(assets, "*.*sh", SearchOption.AllDirectories))
        {
            Match m = Regex.Match(File.ReadAllText(path), @"const\s+int\s+TINT_SLOTS\s*=\s*(\d+)\s*;");
            if (m.Success) found[Path.GetFileName(path)] = int.Parse(m.Groups[1].Value);
        }

        c.True(found.ContainsKey("lodterrainbody.vsh"), "the vertex body declares TINT_SLOTS");
        c.True(found.ContainsKey("lodterrainbody.fsh"), "the fragment body declares TINT_SLOTS");

        foreach ((string file, int value) in found)
        {
            c.Eq(LodTintRegistry.MaxSlots, value, $"{file} TINT_SLOTS matches LodTintRegistry.MaxSlots");
        }

        c.Eq(1, found.Values.Distinct().Count(), "the vertex and fragment shaders agree with each other");
    }

    /// <summary>
    /// LodMesher packs the tint slot into a vertex alpha byte in three bands: opaque at
    /// slot, water at MaxSlots + slot, thin at MaxSlots * 2 + slot. Alpha is a byte, so the
    /// largest encodable value is MaxSlots * 3 - 1. Raise MaxSlots past 85 and the thin band
    /// wraps into the opaque band with no error anywhere - thin plants would render as solid
    /// terrain of an arbitrary tint.
    /// </summary>
    static void AlphaPacking(Check c)
    {
        c.True(LodTintRegistry.MaxSlots * 3 <= 256,
            $"tint bands fit in a byte (MaxSlots {LodTintRegistry.MaxSlots} * 3 <= 256)");
    }

    /// <summary>
    /// Three rendering regressions are expressible only across the renderer and its two
    /// shader stages: the near handoff must retain cached fallback, the transition must
    /// not deform geometry, and cosmetic noise must use a world-stable coordinate rather
    /// than the camera-relative position required by projection and fog.
    /// </summary>
    static void LodFallbackAndStableColour(Check c)
    {
        string root = GameAssemblies.RepoRoot;
        string vertex = ShaderBody("vsh");
        string fragment = ShaderBody("fsh");
        string renderer = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src", "Render",
            "LodTerrainRenderer.cs"));

        c.False(fragment.Contains("dist < 0.0", StringComparison.Ordinal),
            "cached terrain is not discarded at the old outer handoff boundary");
        c.True(fragment.Contains("radialDistance < cacheHandoffDistance", StringComparison.Ordinal),
            "cached terrain hands the conservative close field back to vanilla terrain");
        c.True(renderer.Contains("prog.Uniform(\"cacheHandoffDistance\"", StringComparison.Ordinal),
            "the renderer uploads the conservative near handoff radius");
        c.False(vertex.Contains("worldPos.y -=", StringComparison.Ordinal),
            "the near transition does not push cached terrain downward");

        c.True(vertex.Contains("uniform vec4 noiseOrigin", StringComparison.Ordinal),
            "the vertex shader declares a stable section noise origin");
        c.True(renderer.Contains("prog.Uniform(\"noiseOrigin\"", StringComparison.Ordinal),
            "the renderer uploads the stable section noise origin");
        c.True(fragment.Contains("valuenoise(terrainPos / period)", StringComparison.Ordinal),
            "terrain colour noise uses the stable terrain coordinate");
        c.False(fragment.Contains("valuenoise(worldPos", StringComparison.Ordinal),
            "terrain colour noise never follows the camera-relative render position");
    }

    /// <summary>
    /// The four lighting corrections are each an `int` uniform read by the fragment shader
    /// and written by the renderer. A missing upload is silent: GLSL leaves the uniform at
    /// zero, so the switch reads permanently off and the correction simply never happens,
    /// with no compile error and nothing in the log. Same failure shape as G42.
    ///
    /// The formulas are pinned as literals because they are transcriptions of the engine's
    /// own `fogandlight.fsh` and `chunkopaque.vsh`, not values of ours to tune. If a game
    /// update changes vanilla's numbers, this check will not notice - but a silent edit on
    /// our side, which is the likelier accident, fails here.
    /// </summary>
    static void VanillaLightingWiring(Check c)
    {
        string root = GameAssemblies.RepoRoot;
        string fragment = ShaderBody("fsh");
        string renderer = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src", "Render",
            "LodTerrainRenderer.cs"));

        foreach (string name in new[] { "flatTopLight", "lightMoonDir", "lightVanillaRamp",
                                        "lightAmbientColor", "lightDayBoost", "lightSkyDayLight" })
        {
            c.True(fragment.Contains($"uniform int {name};", StringComparison.Ordinal),
                $"the fragment shader declares the {name} switch");
            c.True(renderer.Contains($"prog.Uniform(\"{name}\"", StringComparison.Ordinal),
                $"the renderer uploads the {name} switch");
        }

        // The ambient colour is the only one of vanilla's lighting inputs the engine does
        // not already push into any program including fogandlight.fsh.
        c.True(fragment.Contains("uniform vec3 rgbaAmbientIn;", StringComparison.Ordinal),
            "the fragment shader declares vanilla's ambient colour");
        c.True(renderer.Contains("prog.Uniform(\"rgbaAmbientIn\", capi.Ambient.BlendedAmbientColor)",
                StringComparison.Ordinal),
            "the renderer uploads the engine's blended ambient colour unaltered");

        // chunkopaque.vsh: nb = max(max(intensity, 0.5 + 0.5 * dot(normal, lightPosition)),
        // normal.y * 0.95), with intensity 0.45 when shadows are off - which is the case a
        // LOD section is always in, having no shadow map.
        c.True(fragment.Contains("max(0.45, 0.5 + 0.5 * sunAngle)", StringComparison.Ordinal),
            "the shade ramp and floor are vanilla's");
        c.True(fragment.Contains("clamp(normal.y, 0.0, 1.0) * 0.95", StringComparison.Ordinal),
            "the up-facing floor is vanilla's");

        // fogandlight.vsh applyLight, reduced for full sky light and no block light: every
        // scale cancels against the bMax renormalisation except the contrast constant.
        c.True(fragment.Contains("1.05 * rgbaAmbientIn", StringComparison.Ordinal),
            "the light colour is vanilla's ambient times its contrast constant");

        // fogandlight.fsh applyFogAndShadowFromBrightness, verbatim.
        c.True(fragment.Contains("1.0 + max(0.0, shadowIntensity * 2.0 - 1.66) / 1.5",
                StringComparison.Ordinal),
            "the daylight brightening matches vanilla's");

        // SystemRenderSky.OnRenderFrame3D, transcribed. DefaultShaderUniforms.SkyDaylight is
        // internal, so the value the engine computed cannot be read; only rebuilt. Every
        // engine shader that calls getSkyColorAt passes this, never DayLightStrength.
        c.True(renderer.Contains("1.25f * Math.Max(calendar.DayLightStrength - calendar.MoonLightStrength / 2f, 0.05f)",
                StringComparison.Ordinal),
            "the sky daylight reconstruction matches the engine's formula");
        c.True(renderer.Contains("capi.World.Player.Entity.Pos.Y - capi.World.SeaLevel - 1000.0) / 30000.0",
                StringComparison.Ordinal),
            "the sky daylight reconstruction keeps the engine's high-altitude attenuation");
        c.True(fragment.Contains("clamp(skyLight, 0.0, 1.0), horizonFog", StringComparison.Ordinal),
            "the far sky fade uses the corrected sky daylight");
        // The mod's own night glow dimming is calibrated against DayLightStrength, which
        // reaches zero; sky daylight floors at 0.0625 and would leave a glow burning.
        c.True(fragment.Contains("skyGlow.y *= clamp((dayLight - 0.05)", StringComparison.Ordinal),
            "the glow clamp keeps the daylight strength it was calibrated against");

        // These two arrive free with the fogandlight.fsh include; uploading them again
        // would be harmless but would hide the fact that the engine already owns them.
        c.False(renderer.Contains("prog.Uniform(\"lightPosition\"", StringComparison.Ordinal)
            || renderer.Contains("prog.Uniform(\"shadowIntensity\"", StringComparison.Ordinal),
            "the light vector and shadow intensity are left to the engine's own upload");
    }

    static void ReadinessShadowWiring(Check c)
    {
        string renderDir = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src", "Render");
        string renderer = File.ReadAllText(Path.Combine(renderDir, "LodTerrainRenderer.cs"));
        string model = File.ReadAllText(Path.Combine(renderDir, "VanillaRenderReadiness.cs"));

        c.True(renderer.Contains("capi.Event.ChunkDirty += OnReadinessChunkDirty", StringComparison.Ordinal),
            "the shadow tracker receives event-fed client chunk candidates");
        c.True(renderer.Contains("capi.Event.ChunkDirty -= OnReadinessChunkDirty", StringComparison.Ordinal),
            "renderer teardown unsubscribes the readiness event");
        c.True(renderer.Contains("capi.IsChunkRendered(readinessProbePos)", StringComparison.Ordinal),
            "readiness probes use the supported public engine query");
        // Emptiness may be counted and must never decide ownership: a column counts as
        // owned only when every one of its chunks does, and every column has sky, so
        // refusing air its cell removes ownership from the entire world at once (G40).
        // Returning void is the structural half of that guarantee - a measurement with no
        // result cannot be branched on, whatever a later edit intends.
        c.True(Regex.IsMatch(renderer, @"void MeasureDrawnChunk\(VanillaChunkCell cell"),
            "the empty-chunk question is measured rather than acted on");
        c.True(Regex.IsMatch(renderer, @"if \(rendered && atOwnershipDecision\) MeasureDrawnChunk\(cell"),
            "the measurement runs only for chunks the engine claims it drew");
        // The chunk lookup takes the client's chunk lock. Doing it for every probe doubles
        // that traffic on the render thread while a world is loading, which is when the
        // probe queue is longest and the loader threads want the same lock.
        c.True(renderer.Contains("bool atOwnershipDecision = decisionState is VanillaReadinessState.ObservedRendered",
            StringComparison.Ordinal),
            "the chunk lookup happens only where an observation can change ownership");
        c.False(renderer.Contains("IsVanillaChunkEmpty(cell)) rendered = false", StringComparison.Ordinal),
            "an empty-chunk reading cannot decide ownership");
        c.True(renderer.Contains("ReadinessProbeMaxItemsPerFrame", StringComparison.Ordinal)
            && renderer.Contains("ReadinessProbeMaxMillisecondsPerFrame", StringComparison.Ordinal),
            "readiness probing has both item and elapsed-time ceilings");
        c.True(renderer.Contains("ReadinessProbeCatchUpItemsPerFrame", StringComparison.Ordinal)
            && renderer.Contains("ReadinessProbeCatchUpMillisecondsPerFrame", StringComparison.Ordinal),
            "a deep queue raises both ceilings rather than only the item count");
        c.True(renderer.Contains("readiness.PendingCandidates >= ReadinessProbeCatchUpQueueDepth", StringComparison.Ordinal),
            "the raised budget is entered only on measured backlog");
        c.True(model.Contains("PromoteObserved", StringComparison.Ordinal),
            "first true observations wait in a deferred render-frame queue");
        // Classification now reaches the draw path, but only to skip a section the mask
        // would have discarded entirely, and only while the mask is live. A skip decided
        // without the texture behind it would remove terrain the GPU still had to draw.
        c.True(renderer.Contains("readiness.Classify(key) != VanillaSectionOwnership.VanillaOnly", StringComparison.Ordinal),
            "only a wholly vanilla-owned section is skipped");
        int skipMethod = renderer.IndexOf("bool SkipVanillaOwnedSection(long key)", StringComparison.Ordinal);
        c.True(skipMethod > 0, "the whole-mesh skip has its own guard");
        string skipBody = skipMethod > 0
            ? renderer.Substring(skipMethod, Math.Min(420, renderer.Length - skipMethod))
            : string.Empty;
        c.True(skipBody.Contains("if (!readinessMaskActive", StringComparison.Ordinal),
            "a section is never skipped unless the GPU mask is live");
        c.Eq(3, CountOccurrences(renderer, "if (SkipVanillaOwnedSection(key)) continue;"),
            "both opaque comparison paths and water submission share the same skip decision");
        c.False(skipBody.Contains("Evict", StringComparison.Ordinal)
            || skipBody.Contains("RenderDirty", StringComparison.Ordinal)
            || skipBody.Contains("Remove", StringComparison.Ordinal),
            "skipping a draw does not touch residency or dirty obligations");
        c.True(renderer.Contains("nearHandoff.Update(", StringComparison.Ordinal)
            && renderer.Contains("readinessOwnsHandoff", StringComparison.Ordinal),
            "the near handoff is driven by measured readiness with an explicit fallback flag");
        c.True(renderer.Contains("LodNearHandoff.InnerDiscardRadius(viewDistance)", StringComparison.Ordinal),
            "the established radial constant remains available when readiness is unavailable");
    }

    /// <summary>
    /// The shader reconstructs the same wrapped atlas address that C# writes. Nothing at
    /// runtime can catch a divergence - it would simply read another chunk's ownership and
    /// hide the wrong ground - so the two addressings are held together here, along with
    /// the ordering and fallback rules the mask depends on.
    /// </summary>
    static void OwnershipMaskWiring(Check c)
    {
        string renderDir = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src", "Render");
        string renderer = File.ReadAllText(Path.Combine(renderDir, "LodTerrainRenderer.cs"));
        string maskSource = File.ReadAllText(Path.Combine(renderDir, "VanillaReadinessMask.cs"));
        string fragment = ShaderBody("fsh");

        // Row = wrapped Z plus a whole capacity-sized block per Y level; column = wrapped X.
        c.True(maskSource.Contains("int row = (chunkZ & mask) + chunkY * capacity;", StringComparison.Ordinal)
            && maskSource.Contains("return row * capacity + (chunkX & mask);", StringComparison.Ordinal),
            "the mask addresses a cell as wrapped X across a Y-stacked wrapped Z row");
        c.True(fragment.Contains("ivec2(cellX & wrap, (cellZ & wrap) + cellY * maskCapacity)", StringComparison.Ordinal),
            "the shader reconstructs that same wrapped address");
        c.True(fragment.Contains("int wrap = maskCapacity - 1;", StringComparison.Ordinal),
            "the shader wraps with the same power-of-two mask the ring uses");

        // Ownership must come from an integer section origin plus a small local offset. A
        // summed world coordinate is not exact in float32: at 512k blocks a fragment 0.03
        // blocks below a chunk edge rounds onto the next chunk and takes its ownership.
        c.True(fragment.Contains("maskSectionOriginX + int(floor(sectionLocal.x", StringComparison.Ordinal)
            && fragment.Contains("maskSectionOriginZ + int(floor(sectionLocal.z", StringComparison.Ordinal),
            "ownership addressing uses an integer section origin plus the section-local offset");
        c.False(fragment.Contains("floor(terrainPos.x / 32.0)", StringComparison.Ordinal),
            "ownership is never derived from a summed world coordinate");
        // Two scalars, and never the Vec2i overload: that one reaches glUniform2f, which an
        // integer uniform rejects outright, so the origin never leaves the CPU. G42.
        // Matched against the source with its whitespace collapsed, so wrapping the call
        // over two lines stays legal while dropping the cast still fails.
        string flattened = Regex.Replace(renderer, @"\s+", " ");
        c.True(flattened.Contains("prog.Uniform(\"maskSectionOriginX\", (int)", StringComparison.Ordinal)
            && flattened.Contains("prog.Uniform(\"maskSectionOriginZ\", (int)", StringComparison.Ordinal),
            "the renderer supplies that integer origin per draw");
        c.False(renderer.Contains("new Vec2i(", StringComparison.Ordinal),
            "no uniform is set through the integer-vector overload the driver rejects");

        int maskCheck = fragment.IndexOf("maskEnabled == 1", StringComparison.Ordinal);
        int shading = fragment.IndexOf("normalize(cross(", StringComparison.Ordinal);
        c.True(maskCheck > 0 && shading > 0 && maskCheck < shading,
            "ownership is decided before normals, lighting, noise, fog and output work");
        c.Eq(1, CountOccurrences(fragment, "texelFetch(readinessMask"),
            "opaque and water pass through exactly one shared ownership lookup");
        c.True(fragment.Contains("if (maskEnabled == 1)", StringComparison.Ordinal),
            "a disabled mask executes no readiness sample at all");

        c.True(renderer.Contains("prog.Uniform(\"maskEnabled\"", StringComparison.Ordinal)
            && renderer.Contains("prog.Uniform(\"maskCapacity\"", StringComparison.Ordinal)
            && renderer.Contains("prog.BindTexture2D(\"readinessMask\"", StringComparison.Ordinal),
            "the renderer uploads the ring uniforms and binds the mask itself");
        // A healthy mask must be the only suppressor. If the radius stayed live it would
        // hide cached terrain in cells the mask still assigns to the cache.
        int handoffUniform = renderer.IndexOf("prog.Uniform(\"cacheHandoffDistance\"", StringComparison.Ordinal);
        c.True(handoffUniform > 0, "the renderer still sets the handoff distance uniform");
        string handoffAssignment = handoffUniform > 0
            ? renderer.Substring(handoffUniform, Math.Min(260, renderer.Length - handoffUniform))
            : string.Empty;
        c.True(handoffAssignment.Contains("maskOwnsPixels", StringComparison.Ordinal)
            && handoffAssignment.Contains("MaskNearFloor()", StringComparison.Ordinal),
            "a healthy mask replaces the radial handoff with its own near-field floor");

        // The floor exists to keep coarse cached geometry out of the player's face, but it
        // must never suppress terrain where vanilla has not proven it draws.
        int floorMethod = renderer.IndexOf("float MaskNearFloor()", StringComparison.Ordinal);
        c.True(floorMethod > 0, "the near floor is computed in one place");
        string floorBody = floorMethod > 0
            ? renderer.Substring(floorMethod, Math.Min(520, renderer.Length - floorMethod))
            : string.Empty;
        c.True(floorBody.Contains("VanillaReadinessState.VanillaReady", StringComparison.Ordinal)
            && floorBody.Contains("MaskNearFloorBlocks", StringComparison.Ordinal)
            && floorBody.Contains("0f", StringComparison.Ordinal),
            "the floor applies only while the camera's own cell is committed ready");
        // The mask became the default in 0.3.17, so the old opt-in gate is gone. What has to
        // survive is the override that lets the benchmark harness pin either path: without a
        // way to force the mask off, the radial path it is measured against is unreachable
        // and every "off" run would silently measure the mask instead.
        c.True(renderer.Contains("VINTAGEHORIZONS_CHUNK_MASK", StringComparison.Ordinal)
            && renderer.Contains("!= \"0\"", StringComparison.Ordinal),
            "the mask can still be forced off for a controlled comparison");
        c.True(renderer.Contains("DisposeReadinessMaskTexture();", StringComparison.Ordinal),
            "the mask texture is released on teardown");
    }

    static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>
    /// The benchmark runner's readiness gate reads one log line that the renderer formats.
    /// A rename on either side would leave the gate matching nothing, which is only
    /// discovered after a ten-minute game run, so the two are matched here instead: the
    /// renderer's interpolated literal is rendered with sample values and must satisfy the
    /// runner's own pattern. PowerShell uses this exact regex engine.
    /// </summary>
    static void ReadinessTelemetryContract(Check c)
    {
        string renderer = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "VintageHorizons", "src", "Render", "LodTerrainRenderer.cs"));
        string runner = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "scripts", "bench-windows.ps1"));

        c.True(runner.Contains("RequireReadinessConvergence", StringComparison.Ordinal),
            "the benchmark runner exposes the readiness convergence gate");

        Match describe = Regex.Match(renderer,
            @"public string DescribeReadiness\(\).*?(?<statement>return \$""shadow .*?;)",
            RegexOptions.Singleline);
        c.True(describe.Success, "the renderer still formats a readiness line to parse");
        if (!describe.Success) return;

        var line = new StringBuilder();
        foreach (Match part in Regex.Matches(describe.Groups["statement"].Value, @"\$""(?<text>[^""]*)"""))
            line.Append(part.Groups["text"].Value);
        // Holes formatted to one decimal print a fractional number; the rest are counts.
        string sample = Regex.Replace(line.ToString(), @"\{[^{}]*\}",
            m => m.Value.Contains(":0.0", StringComparison.Ordinal) ? "342.3" : "7");
        c.False(sample.Contains('{'), "every readiness hole was substituted with a sample value");

        Match pattern = Regex.Match(runner,
            @"\$shadowPattern =(?<parts>(?:\s*'[^']*'\s*\+?)+)");
        c.True(pattern.Success, "the runner still declares its readiness pattern");
        if (!pattern.Success) return;

        var expression = new StringBuilder();
        foreach (Match part in Regex.Matches(pattern.Groups["parts"].Value, @"'(?<text>[^']*)'"))
            expression.Append(part.Groups["text"].Value);

        Match parsed = Regex.Match("  vanilla readiness: " + sample, expression.ToString());
        c.True(parsed.Success, "the runner's readiness pattern matches the renderer's own line");
        if (!parsed.Success) return;
        foreach (string group in new[] { "ready", "unknown", "age", "probes", "errors", "dropped" })
            c.True(parsed.Groups[group].Success && parsed.Groups[group].Value.Length > 0,
                $"the readiness gate can read its {group} value");
    }

    /// <summary>
    /// scripts/package.sh names the release zip from modinfo.json, while the assembly
    /// identity comes from the csproj. Drift between them ships an artifact whose filename
    /// disagrees with the version the game reports.
    /// </summary>
    static void VersionAgreement(Check c)
    {
        CheckPair(c, "VintageHorizons", Path.Combine("VintageHorizons", "VintageHorizons.csproj"),
            Path.Combine("VintageHorizons", "modinfo.json"));
        CheckPair(c, "VintageHorizonsBench",
            Path.Combine("bench", "VintageHorizonsBench", "VintageHorizonsBench.csproj"),
            Path.Combine("bench", "VintageHorizonsBench", "modinfo.json"));
    }

    static void CheckPair(Check c, string label, string csprojRel, string modinfoRel)
    {
        string csproj = Path.Combine(GameAssemblies.RepoRoot, csprojRel);
        string modinfo = Path.Combine(GameAssemblies.RepoRoot, modinfoRel);

        if (!File.Exists(csproj) || !File.Exists(modinfo))
        {
            c.True(false, $"{label}: both csproj and modinfo.json exist");
            return;
        }

        Match fromCsproj = Regex.Match(File.ReadAllText(csproj), @"<Version>([^<]+)</Version>");
        Match fromModinfo = Regex.Match(File.ReadAllText(modinfo), @"""version""\s*:\s*""([^""]+)""");

        c.True(fromCsproj.Success, $"{label}: csproj declares a Version");
        c.True(fromModinfo.Success, $"{label}: modinfo.json declares a version");

        if (fromCsproj.Success && fromModinfo.Success)
        {
            c.Eq(fromCsproj.Groups[1].Value, fromModinfo.Groups[1].Value,
                $"{label}: csproj Version matches modinfo.json version");
        }
    }

    /// <summary>
    /// The old every-200-sections progress notification ran inside the 50 ms owning-thread
    /// assist callback. The synchronous logger reproduced multi-millisecond service tails;
    /// cumulative progress already belongs to the stats callback and /vhserver.
    /// </summary>
    static void AssistServeLoopDoesNotLogProgress(Check c)
    {
        string path = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src", "Net",
            "LodAssistServerSystem.cs");
        string source = File.ReadAllText(path);
        int start = source.IndexOf("void AdmitPending(", StringComparison.Ordinal);
        int end = start < 0 ? -1 : source.IndexOf("LodAssistBlobReader? EnsureBlobReader(",
            start, StringComparison.Ordinal);

        c.True(start >= 0 && end > start, "the assist admission method is found for static inspection");
        if (start >= 0 && end > start)
        {
            string admission = source[start..end];
            c.False(admission.Contains("Mod.Logger.", StringComparison.Ordinal),
                "the 50 ms assist admission loop contains no synchronous logger call");
        }
    }

    /// <summary>
    /// Two commands registered under one name throw at the second registration, and the
    /// throw aborts StartClientSide part-way: every command declared after the collision
    /// silently does not exist, and the mod reports as a failed system while still
    /// running. The compiler cannot see it because the names are strings, and the game
    /// only says so in a log line nobody reads during a playtest.
    /// </summary>
    static void ChatCommandNamesAreUnique(Check c)
    {
        string src = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src");
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var duplicates = new List<string>();

        foreach (string path in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(GameAssemblies.RepoRoot, path);
            foreach (Match m in Regex.Matches(File.ReadAllText(path),
                @"ChatCommands\s*\.\s*Create\s*\(\s*""(?<name>[^""]+)"""))
            {
                string name = m.Groups["name"].Value;
                if (seen.TryGetValue(name, out string? first)) duplicates.Add($"{name} in {first} and {rel}");
                else seen[name] = rel;
            }
        }

        c.True(seen.Count > 0, "found chat command registrations to scan");
        c.SeqEq(Array.Empty<string>(), duplicates,
            $"all {seen.Count} chat command names are registered exactly once");
    }

    static void ConfigDialogWiring(Check c)
    {
        string src = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src");
        string mod = File.ReadAllText(Path.Combine(src, "VintageHorizonsModSystem.cs"));
        string dialog = File.ReadAllText(Path.Combine(src, "Gui", "VintageHorizonsConfigDialog.cs"));
        string scale = File.ReadAllText(Path.Combine(src, "Gui", "LodThresholdScaleElement.cs"));

        c.True(mod.Contains("ChatCommands.Create(\"vhconfig\")", StringComparison.Ordinal),
            "the player-facing config command remains registered");
        c.True(dialog.Contains("new LodThresholdScaleElement", StringComparison.Ordinal),
            "the config window uses one shared multi-marker LOD scale");
        c.True(dialog.Contains("AddButton(\"Defaults\"", StringComparison.Ordinal)
            && dialog.Contains("AddButton(\"Save\"", StringComparison.Ordinal)
            && dialog.Contains("AddButton(\"Cancel\"", StringComparison.Ordinal),
            "the config window retains Defaults, Save and Cancel actions");
        c.True(scale.Contains("values[dragging - 1] + LodWorld.ThresholdStepBlocks", StringComparison.Ordinal)
            && scale.Contains("values[dragging + 1] - LodWorld.ThresholdStepBlocks", StringComparison.Ordinal),
            "each LOD marker remains constrained by both neighbours");
        c.True(scale.Contains("$\"L{i + 1}\"", StringComparison.Ordinal)
            && scale.Contains("Render2DLoadedTexture(markerLabels[i]", StringComparison.Ordinal),
            "the moving LOD handles render their L1-L6 names inside the boxes");
        c.True(dialog.Contains("const int MaxDrawDistance = 32768", StringComparison.Ordinal)
            && dialog.Contains("const int DrawDistanceStep = 512", StringComparison.Ordinal),
            "the cached draw-distance slider stops at 32,768 in 512-block increments");
        c.True(dialog.Contains("ToString(\"N0\", CultureInfo.InvariantCulture)", StringComparison.Ordinal),
            "the config window displays full block values instead of abbreviated thousands");
    }

    /// <summary>
    /// Our shaders may not declare an integer vector uniform, because the client cannot
    /// set one. Its only pair-of-integers setter is
    /// <c>ShaderProgramBase.Uniform(string, Vec2i)</c>, which reaches
    /// <c>GL.Uniform2(location, float, float)</c>; against an <c>ivec</c> uniform the
    /// driver answers GL_INVALID_OPERATION and the uniform keeps its previous value.
    /// Nothing throws, the shader compiles, the check tier's own re-implementation of the
    /// arithmetic still agrees - and the value never arrives. That shipped: the ownership
    /// mask addressed chunk (0,0) for every section in the world while its error was
    /// buried in a per-frame log line. Scalar int uniforms are set correctly, so split the
    /// vector.
    /// </summary>
    static void NoIntegerVectorUniforms(Check c)
    {
        string shaders = Path.Combine(
            GameAssemblies.RepoRoot, "VintageHorizons", "assets", "vintagehorizons", "shaders");
        var offenders = new List<string>();
        int scanned = 0;

        foreach (string path in Directory.EnumerateFiles(shaders, "*.*sh"))
        {
            scanned++;
            foreach (Match m in Regex.Matches(File.ReadAllText(path),
                @"^\s*uniform\s+(?<type>[iu]vec[234])\s+(?<name>\w+)", RegexOptions.Multiline))
            {
                offenders.Add($"{Path.GetFileName(path)}: {m.Groups["type"].Value} {m.Groups["name"].Value}");
            }
        }

        c.True(scanned > 0, "found shader files to scan for integer vector uniforms");
        c.SeqEq(Array.Empty<string>(), offenders,
            "no shader declares an integer vector uniform the client cannot set");
    }

    /// <summary>
    /// The experimental culler relies only on render order: vanilla terrain populates the
    /// depth buffer at 0.37, then cached terrain follows at 0.38. The engine sorts a
    /// renderer only when it is registered, so a live toggle must unregister and register
    /// again rather than merely change the property returned by RenderOrder.
    /// </summary>
    static void OcclusionCullingWiring(Check c)
    {
        string root = GameAssemblies.RepoRoot;
        string renderer = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src", "Render",
            "LodTerrainRenderer.cs"));
        string mod = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src",
            "VintageHorizonsModSystem.cs"));

        c.True(mod.Contains("ChatCommands.Create(\"vhocclusion\")", StringComparison.Ordinal),
            "the live occlusion toggle is registered");
        c.True(mod.Contains("VINTAGEHORIZONS_OCCLUSION_CULLING\") != \"0\"", StringComparison.Ordinal),
            "post-vanilla depth rejection is default-on with an explicit off override");
        c.True(renderer.Contains("bool postVanillaDepthCulling = true", StringComparison.Ordinal),
            "direct renderer construction also starts at the accepted post-vanilla order");
        c.True(renderer.Contains("PreVanillaRenderOrder = 0.36", StringComparison.Ordinal)
            && renderer.Contains("PostVanillaRenderOrder = 0.38", StringComparison.Ordinal),
            "the experiment brackets vanilla terrain's established 0.37 order");
        c.True(renderer.Contains("capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque)",
                StringComparison.Ordinal)
            && CountOccurrences(renderer,
                "capi.Event.RegisterRenderer(this, EnumRenderStage.Opaque") == 1,
            "changing order re-registers the renderer through one shared registration path");
        c.False(renderer.Contains("BeginConditionalRender", StringComparison.Ordinal)
            || renderer.Contains("lodocclusion", StringComparison.Ordinal)
            || renderer.Contains("LodOcclusionProbe", StringComparison.Ordinal),
            "the rejected proxy/conditional-render path is absent");
        c.True(mod.Contains("ChatCommands.Create(\"vhtemporal\")", StringComparison.Ordinal)
            && mod.Contains("ChatCommands.Create(\"vhtemporalprofile\")", StringComparison.Ordinal)
            && renderer.Contains("RenderOpaqueMesh(key, mesh)", StringComparison.Ordinal)
            && renderer.Contains("QueryTarget.AnySamplesPassed", StringComparison.Ordinal)
            && renderer.Contains("readiness.Classify(key) == VanillaSectionOwnership.Mixed", StringComparison.Ordinal),
            "the accepted delayed exact-geometry path is wired behind its own live toggle");
        c.True(renderer.Contains("VINTAGEHORIZONS_TEMPORAL_OCCLUSION\") != \"0\"", StringComparison.Ordinal)
            && renderer.Contains("TemporalOcclusionProfileName { get; private set; } = \"aggressive\"", StringComparison.Ordinal)
            && renderer.Contains("float temporalOcclusionRotationMatrixLimit = float.PositiveInfinity;", StringComparison.Ordinal),
            "accepted aggressive temporal occlusion, including turn persistence, is default-on with an explicit off override");
        // A fourth reason since 0.3.53: switching batched drawing on or off. Under batching
        // no per-section queries are issued at all, so every stored answer is from the
        // other path and from an older camera; carrying them across the switch would show
        // as terrain missing after switching back.
        c.Eq(4, CountOccurrences(renderer, "InvalidateTemporalOcclusionScene();"),
            "only profile, render-order, camera/projection and draw-path changes globally "
            + "invalidate temporal results");
        c.True(renderer.Contains("query.State.Invalidate(temporalOcclusionEpoch)", StringComparison.Ordinal),
            "a replaced cached mesh invalidates its own query rather than every hidden section");
    }

    /// <summary>
    /// No source file may contain a control character. Shader assets are already covered by
    /// the ASCII rule, but C#, PowerShell and Markdown were not, and an editing pipeline
    /// that re-parses backslash escapes turns a regex `` into a literal backspace. The
    /// result is invisible in a terminal, compiles without complaint, and silently changes
    /// what a pattern matches. It has happened twice in this repository.
    /// </summary>
    static void SourceHasNoControlCharacters(Check c)
    {
        string root = GameAssemblies.RepoRoot;
        var offenders = new List<string>();
        int scanned = 0;

        foreach (string dir in new[] { "VintageHorizons", "tests", "scripts", "dev", "bench" })
        {
            string path = Path.Combine(root, dir);
            if (!Directory.Exists(path)) continue;

            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                if (Path.GetExtension(file) is not (".cs" or ".ps1" or ".sh" or ".md" or ".json" or ".txt" or ".py")) continue;

                scanned++;
                string text = File.ReadAllText(file);
                for (int i = 0; i < text.Length; i++)
                {
                    char ch = text[i];
                    // Tab, CR and LF by code point: writing them as escapes is how the
                    // stray characters this check exists to catch got here in the first place.
                    if (ch >= ' ' || ch == (char)9 || ch == (char)13 || ch == (char)10) continue;
                    offenders.Add($"{Path.GetRelativePath(root, file)} offset {i} = 0x{(int)ch:X2}");
                    break;
                }
            }
        }

        c.True(scanned > 0, "found source files to scan for control characters");
        c.SeqEq(Array.Empty<string>(), offenders, $"none of {scanned} source files contain a control character");
    }
}
