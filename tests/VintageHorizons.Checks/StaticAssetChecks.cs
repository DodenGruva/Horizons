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
        OwnershipMaskWiring(c);
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
        c.Eq(2, CountOccurrences(renderer, "if (SkipVanillaOwnedSection(key)) continue;"),
            "opaque and water submission share the same skip decision");
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
        string fragment = File.ReadAllText(Path.Combine(GameAssemblies.RepoRoot,
            "VintageHorizons", "assets", "vintagehorizons", "shaders", "lodterrain.fsh"));

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
        c.True(fragment.Contains("maskSectionOrigin.x + int(floor(sectionLocal.x", StringComparison.Ordinal)
            && fragment.Contains("maskSectionOrigin.y + int(floor(sectionLocal.z", StringComparison.Ordinal),
            "ownership addressing uses an integer section origin plus the section-local offset");
        c.False(fragment.Contains("floor(terrainPos.x / 32.0)", StringComparison.Ordinal),
            "ownership is never derived from a summed world coordinate");
        c.True(renderer.Contains("prog.Uniform(\"maskSectionOrigin\"", StringComparison.Ordinal),
            "the renderer supplies that integer origin per draw");

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
            && handoffAssignment.Contains("0f", StringComparison.Ordinal),
            "a healthy mask drives the radial handoff to zero so it cannot also suppress cells");
        c.True(renderer.Contains("VINTAGEHORIZONS_CHUNK_MASK", StringComparison.Ordinal),
            "the mask stays behind an explicit opt-in gate");
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
}
