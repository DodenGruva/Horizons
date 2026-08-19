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
        TintSlotAgreement(c);
        AlphaPacking(c);
        LodFallbackAndStableColour(c);
        ReadinessShadowWiring(c);
        ReadinessTelemetryContract(c);
        VersionAgreement(c);
        AssistServeLoopDoesNotLogProgress(c);
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
        string shaders = Path.Combine(
            GameAssemblies.RepoRoot, "VintageHorizons", "assets", "vintagehorizons", "shaders");

        var found = new Dictionary<string, int>();
        foreach (string path in Directory.EnumerateFiles(shaders, "*.*sh"))
        {
            Match m = Regex.Match(File.ReadAllText(path), @"const\s+int\s+TINT_SLOTS\s*=\s*(\d+)\s*;");
            if (m.Success) found[Path.GetFileName(path)] = int.Parse(m.Groups[1].Value);
        }

        c.True(found.ContainsKey("lodterrain.vsh"), "lodterrain.vsh declares TINT_SLOTS");
        c.True(found.ContainsKey("lodterrain.fsh"), "lodterrain.fsh declares TINT_SLOTS");

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
        string shaders = Path.Combine(root, "VintageHorizons", "assets", "vintagehorizons", "shaders");
        string vertex = File.ReadAllText(Path.Combine(shaders, "lodterrain.vsh"));
        string fragment = File.ReadAllText(Path.Combine(shaders, "lodterrain.fsh"));
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
        c.True(renderer.Contains("ReadinessProbeMaxItemsPerFrame", StringComparison.Ordinal)
            && renderer.Contains("ReadinessProbeMaxMillisecondsPerFrame", StringComparison.Ordinal),
            "readiness probing has both item and elapsed-time ceilings");
        c.True(model.Contains("PromoteObserved", StringComparison.Ordinal),
            "first true observations wait in a deferred render-frame queue");
        // Phase 2a lets measured readiness drive the existing radial uniform. Per-cell
        // ownership - classification, whole-mesh skipping, and the GPU mask - is still
        // absent, so the handoff radius remains the single pixel path.
        c.False(renderer.Contains("readiness.Classify(", StringComparison.Ordinal),
            "per-section ownership classification still does not reach the draw path");
        c.True(renderer.Contains("nearHandoff.Update(", StringComparison.Ordinal)
            && renderer.Contains("readinessOwnsHandoff", StringComparison.Ordinal),
            "the near handoff is driven by measured readiness with an explicit fallback flag");
        c.True(renderer.Contains("LodNearHandoff.InnerDiscardRadius(viewDistance)", StringComparison.Ordinal),
            "the established radial constant remains available when readiness is unavailable");
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
}
