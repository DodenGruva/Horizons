namespace VintageHorizons.Checks;

/// <summary>
/// The depth pyramid's shape and its reduction rule.
///
/// This is the one part of Phase 4 that can be proved without a GPU, and it is also the
/// part whose failure mode is worst: a pyramid that reports a depth even slightly nearer
/// than the true farthest sample lets the occlusion test conclude "hidden" about terrain
/// the player can see. That does not look like a bug in the pyramid. It looks like terrain
/// randomly missing in some views, which is precisely the symptom that took two sessions to
/// track down the last time this renderer lost ground.
///
/// So the checks below are one-directional throughout: every claim is that the reduction
/// never returns anything NEARER than the samples beneath it.
/// </summary>
public static class HzbChecks
{
    public static void Run(Check c)
    {
        LevelShape(c);
        FarthestWins(c);
        OddDimensionsKeepTheEdge(c);
        NeverNearerThanItsSources(c);
        DownToOneTexel(c);
        ResultPacking(c);
        ShaderMirrorsItsTwin(c);
        CullShaderCanOnlyStopADraw(c);
    }

    /// <summary>
    /// The card returns one word per section carrying two different things. A packing slip
    /// here does not fail loudly - it reports a plausible verdict with a plausible sub-cell
    /// count, both wrong, and the measurement they feed is a decision about what to build
    /// next.
    /// </summary>
    static void ResultPacking(Check c)
    {
        c.Eq(LodHzbClassifier.VerdictOccluded,
            LodHzbClassifier.VerdictOf(LodHzbClassifier.VerdictOccluded), "a bare verdict decodes");
        c.Eq(0, LodHzbClassifier.HiddenSubCellsOf(LodHzbClassifier.VerdictOccluded),
            "and carries no sub-cells");

        // Every verdict against every legal cell count, which is the only way to be sure the
        // two fields cannot bleed into each other at any value.
        for (uint verdict = 0; verdict <= 3; verdict++)
        {
            for (int cells = 0; cells <= LodHzbProjection.SubCellCount; cells++)
            {
                uint packed = verdict | ((uint)cells << 8);
                c.Eq(verdict, LodHzbClassifier.VerdictOf(packed),
                    $"verdict {verdict} survives {cells} packed cells");
                c.Eq(cells, LodHzbClassifier.HiddenSubCellsOf(packed),
                    $"{cells} cells survive verdict {verdict}");
                c.False(LodHzbClassifier.HiddenByWideSamplingOf(packed),
                    $"the wide flag stays clear for verdict {verdict} with {cells} cells");

                uint withWide = packed | (1u << 16);
                c.True(LodHzbClassifier.HiddenByWideSamplingOf(withWide),
                    $"the wide flag reads back for verdict {verdict} with {cells} cells");
                c.Eq(verdict, LodHzbClassifier.VerdictOf(withWide),
                    $"and does not disturb verdict {verdict}");
                c.Eq(cells, LodHzbClassifier.HiddenSubCellsOf(withWide),
                    $"nor the {cells} packed cells");
            }
        }

        // The four verdicts must be distinct, or two different outcomes count as one.
        var codes = new[]
        {
            LodHzbClassifier.VerdictVisible, LodHzbClassifier.VerdictOccluded,
            LodHzbClassifier.VerdictFailedOpen, LodHzbClassifier.VerdictBackground,
        };
        c.Eq(4, codes.Distinct().Count(), "the four verdicts are distinct");
        c.True(codes.All(code => code <= 0xF), "and all fit in the low nibble");

        // The fail-open family. Splitting one bucket into causes must not change what any
        // of them MEAN: each still says draw it, and a caller that treats one as a decision
        // would hide terrain on the strength of the test having failed.
        var undecided = new[]
        {
            LodHzbClassifier.VerdictFailedOpen, LodHzbClassifier.VerdictNearPlane,
            LodHzbClassifier.VerdictOffScreen, LodHzbClassifier.VerdictDegenerate,
        };
        c.Eq(4, undecided.Distinct().Count(), "the fail-open causes are distinct from each other");
        c.True(undecided.All(LodHzbClassifier.IsUndecided), "and all read as undecided");
        c.True(undecided.All(code => code <= 0xF), "and all fit in the low nibble");
        c.False(LodHzbClassifier.IsUndecided(LodHzbClassifier.VerdictOccluded),
            "a hidden verdict is a decision, not an undecided one");
        c.False(LodHzbClassifier.IsUndecided(LodHzbClassifier.VerdictVisible),
            "and so is a visible one");
        c.False(LodHzbClassifier.IsUndecided(LodHzbClassifier.VerdictBackground),
            "and a background refusal, which is a specific reason rather than a failure");
        c.Eq(0, undecided.Intersect(codes.Except(new[] { LodHzbClassifier.VerdictFailedOpen })).Count(),
            "no fail-open cause collides with a decided verdict");
    }

    /// <summary>
    /// The compute shader cannot be compiled without a GPU, so the constants it shares with
    /// the C# side are pinned here instead. Both of these have already cost a run: a
    /// reserved word stopped the shader compiling at all, and a mismatched constant would be
    /// worse, because it would compile and quietly measure a different thing.
    /// </summary>
    static void ShaderMirrorsItsTwin(Check c)
    {
        string source = LodHzbClassifier.ComputeSource;

        c.True(source.Contains("#version 430"), "the shader declares the version compute needs");
        c.True(source.Contains($"SUBDIVISIONS_PER_AXIS = {LodHzbProjection.SubdivisionsPerAxis}"),
            "the shader subdivides by the same factor the C# side reports against");
        c.True(source.Contains($"VERDICT_OCCLUDED = {LodHzbClassifier.VerdictOccluded}u"),
            "the occluded verdict matches");
        c.True(source.Contains($"VERDICT_FAILED_OPEN = {LodHzbClassifier.VerdictFailedOpen}u"),
            "the fail-open verdict matches");
        c.True(source.Contains($"VERDICT_BACKGROUND = {LodHzbClassifier.VerdictBackground}u"),
            "the background verdict matches");
        c.True(source.Contains($"VERDICT_NEAR_PLANE = {LodHzbClassifier.VerdictNearPlane}u"),
            "the near-plane cause matches");
        c.True(source.Contains($"VERDICT_OFF_SCREEN = {LodHzbClassifier.VerdictOffScreen}u"),
            "the off-screen cause matches");
        c.True(source.Contains($"VERDICT_DEGENERATE = {LodHzbClassifier.VerdictDegenerate}u"),
            "the degenerate cause matches");
        c.True(source.Contains("hiddenCells << 8"), "and the sub-cell count is packed where C# reads it");
        c.True(source.Contains("hiddenWide << 16"), "and the wide-sampling flag where C# reads that");
        c.True(source.Contains($"TEXELS_PRIMARY = {LodHzbProjection.DefaultTexelsPerAxis}"),
            "the shader takes its verdict at the same width as the C# default");
        c.True(source.Contains($"TEXELS_NARROW = {LodHzbProjection.NarrowTexelsPerAxis}"),
            "and its comparison baseline is the width the test used before it was widened");
        c.True(source.Contains("uniform float occlusionDepthBias"),
            "the shader receives the selected 24-bit depth safety band explicitly");
        c.Near(4.0 / 16777215.0, LodHzbProjection.OcclusionDepthBias, 1e-12,
            "the C# safety band remains exactly four normalized 24-bit depth steps");
        c.True(source.Contains("nearestDepth > farthest + occlusionDepthBias"),
            "the shader fails open inside the depth safety band");
        // The 0.3.99 silhouette guard is deliberately gone as of 0.3.103, and these assert
        // that it stays gone. It refused to hide a box whose projected rectangle had exact
        // clear sky within one texel OUTSIDE it - a rule written for a sky theory that the
        // 0.3.101 texel-mapping fix superseded (G96). Measured in 0.3.102, it drew 169,994
        // already-proved-hidden far commands out of 2,495,527 sampled.
        c.False(source.Contains("backgroundGuardTexels"),
            "the retired silhouette guard has no remaining uniform");
        c.False(source.Contains("guardDepth"),
            "and no remaining perimeter sampling");
        c.True(source.Contains("if (nearestDepth > farthest + occlusionDepthBias) return VERDICT_OCCLUDED;"),
            "a box past the safety band is hidden on its own rectangle alone");
        c.True(LodHzbClassifier.CullSource.Contains(
                "binding = 2) buffer LiveCullStats", StringComparison.Ordinal),
            "the live cull writes diagnostics beside the exact indirect command buffer");
        c.True(LodHzbClassifier.CullSource.Contains(
                $"STATS_BAND_BASE = {LodGpuCullTelemetryLayout.BandBase}u",
                StringComparison.Ordinal),
            "the shader and CPU agree where per-distance live counters begin");
        c.True(LodHzbClassifier.CullSource.Contains(
                $"STATS_BAND_STRIDE = {LodGpuCullTelemetryLayout.BandStride}u",
                StringComparison.Ordinal),
            "the shader and CPU agree on command, geometry, background, and refusal words");
        // The pixel anchoring reproduces the pyramid's own halving only while the uniforms
        // ARE the pyramid's base size. Nothing else in the frame would notice if that ever
        // stopped being true, and the symptom would be G96 again - hidden visible terrain.
        c.True(LodHzbClassifier.SharedSource.Contains(
                "if (textureSize(hzb, 0) != ivec2(screenWidth, screenHeight)) return VERDICT_DEGENERATE;",
                StringComparison.Ordinal),
            "the box test fails open when its screen size is not the pyramid's own base size");

        c.True(LodHzbClassifier.CullSource.Contains(
                "commands[commandWord + INSTANCE_COUNT_WORD] = 0u", StringComparison.Ordinal),
            "the instrumented verdict still zeroes the exact command the driver will read");
        c.True(LodHzbClassifier.CullSource.Contains(
                "telemetryEnabled != 0", StringComparison.Ordinal),
            "ordinary frames avoid counter atomics between asynchronous samples");
        c.True(LodHzbClassifier.SharedSource.Contains(
                "uint TestBoxDetailed(", StringComparison.Ordinal)
            && LodHzbClassifier.CullSource.Contains(
                "uint verdict = TestBoxDetailed(lo, hi, TEXELS_PRIMARY", StringComparison.Ordinal),
            "the captured verdict and its depth details come from one box test");

        // The verdict and the sub-cells must be taken at the same width. They were both
        // narrow before the switch and both primary after it; one moving without the other
        // would make the headroom figure a comparison between two rules rather than a
        // measurement of a finer draw unit.
        c.True(source.Contains("TestBox(lo, hi, TEXELS_PRIMARY)"),
            "the section verdict is taken at the primary width");
        c.True(source.Contains("z1), TEXELS_PRIMARY)"),
            "and each sub-cell is judged at that same width");

        // GLSL reserved qualifiers used as identifiers. `sample` reached hardware once and
        // failed the compile with a message naming SAMPLE; the C# twin could never catch it
        // because C# is perfectly happy with the name.
        foreach (string reserved in new[] { "sample", "filter", "active", "partition", "resource" })
        {
            c.False(System.Text.RegularExpressions.Regex.IsMatch(
                    source, @"\b(float|int|uint|vec[234]|bool)\s+" + reserved + @"\b"),
                $"the shader does not declare a variable named '{reserved}'");
        }

        // G10: shader bytes stay ASCII, or OpenTK marshaling can truncate the source.
        c.True(source.All(ch => ch < 128), "the shader source is pure ASCII");

        // The uniforms C# sets by name must exist, or they silently bind to nothing.
        foreach (string uniform in new[]
            { "viewProjection", "screenWidth", "screenHeight", "levelCount", "sectionCount", "hzb" })
        {
            c.True(source.Contains("uniform ") && source.Contains(uniform),
                $"the shader declares the '{uniform}' uniform C# sets");
        }

        // Scalar sizes, never an ivec2: the engine's vector overload reaches glUniform2f and
        // an integer uniform rejects it, leaving zero. G42.
        c.False(source.Contains("uniform ivec2 screenSize"),
            "screen size is two scalar ints rather than an ivec2");
    }

    /// <summary>
    /// Level sizes must match what `TexStorage2D` allocates, because the framebuffer
    /// attachment and the viewport are both derived from them. A disagreement here writes
    /// part of a level and leaves the rest holding the previous frame.
    /// </summary>
    static void LevelShape(Check c)
    {
        c.Eq(11, LodHzbReference.LevelCount(1920, 1080), "1920x1080 reduces in eleven levels");
        c.Eq(1, LodHzbReference.LevelCount(1, 1), "a single texel is already the whole pyramid");
        c.Eq(0, LodHzbReference.LevelCount(0, 0), "an empty target has no pyramid");

        c.Eq((1920, 1080), LodHzbReference.LevelSize(1920, 1080, 0), "level zero is full size");
        c.Eq((960, 540), LodHzbReference.LevelSize(1920, 1080, 1), "each level halves");
        c.Eq((15, 8), LodHzbReference.LevelSize(1920, 1080, 7), "and keeps halving with floor");

        // The short side bottoms out first and then stays at one while the long side keeps
        // halving. A level size of zero would make an empty viewport and a silent no-op.
        c.Eq((1, 1), LodHzbReference.LevelSize(1920, 1080, 10), "the last level is one texel");
        for (int level = 0; level < LodHzbReference.LevelCount(1920, 1080); level++)
        {
            (int w, int h) = LodHzbReference.LevelSize(1920, 1080, level);
            c.True(w >= 1 && h >= 1, $"level {level} has at least one texel in each axis");
        }
    }

    /// <summary>
    /// The rule itself. The driver's own mipmap generation would average these four and
    /// return 0.4, which is nearer than three of the four samples under it - and a test
    /// against 0.4 hides anything between 0.4 and 0.9.
    /// </summary>
    static void FarthestWins(Check c)
    {
        float[] source = { 0.1f, 0.2f, 0.3f, 0.9f };
        float[] reduced = LodHzbReference.Reduce(source, 2, 2);

        c.Eq(1, reduced.Length, "a 2x2 source reduces to one texel");
        c.Eq(0.9f, reduced[0], "the texel holds the farthest of its four, not their average");

        // Order must not matter: the same four samples in any arrangement give the same
        // answer, or the pyramid would depend on where geometry happened to land.
        c.Eq(0.9f, LodHzbReference.Reduce(new[] { 0.9f, 0.1f, 0.2f, 0.3f }, 2, 2)[0],
            "the farthest sample wins wherever it sits");
    }

    /// <summary>
    /// Halving an odd width leaves a column that belongs to no target texel. Dropped, it is
    /// a depth the level never learned about - and it is always the column at the screen
    /// edge, so the pyramid would stop being conservative exactly where terrain runs off the
    /// side of the view.
    /// </summary>
    static void OddDimensionsKeepTheEdge(Check c)
    {
        // 3x1: two source texels fold into one target, and the third is the leftover. It
        // holds the farthest depth in the row, so a reduction that drops it is unsafe.
        float[] reduced = LodHzbReference.Reduce(new[] { 0.1f, 0.2f, 0.95f }, 3, 1);
        c.Eq(1, reduced.Length, "three texels reduce to one");
        c.Eq(0.95f, reduced[0], "the dropped odd column is folded in, not lost");

        // Same on the other axis.
        c.Eq(0.95f, LodHzbReference.Reduce(new[] { 0.1f, 0.2f, 0.95f }, 1, 3)[0],
            "and the dropped odd row is folded in too");

        // Both axes odd: the corner texel belongs to neither fold and needs its own.
        float[] corner = { 0.1f, 0.1f, 0.1f,
                           0.1f, 0.1f, 0.1f,
                           0.1f, 0.1f, 0.99f };
        c.Eq(0.99f, LodHzbReference.Reduce(corner, 3, 3)[0],
            "the far corner of an odd-by-odd source survives the fold");
    }

    /// <summary>
    /// The general form of the same claim, over shapes chosen to hit every combination of
    /// odd and even. One arrangement could be folded correctly by a rule that happens to
    /// suit it; nothing satisfies all of these except actually taking the maximum.
    /// </summary>
    static void NeverNearerThanItsSources(Check c)
    {
        foreach ((int w, int h) in new[] { (2, 2), (3, 2), (2, 3), (3, 3), (7, 5), (16, 9), (1, 5) })
        {
            var rnd = new Random(w * 31 + h);
            var source = new float[w * h];
            for (int i = 0; i < source.Length; i++) source[i] = (float)rnd.NextDouble();

            float[] reduced = LodHzbReference.Reduce(source, w, h);
            (int tw, int th) = (Math.Max(1, w >> 1), Math.Max(1, h >> 1));
            c.Eq(tw * th, reduced.Length, $"{w}x{h} reduces to {tw}x{th}");

            // Nothing in the level may be nearer than the farthest thing it stands for.
            // Checked against the whole source rather than per-texel neighbourhoods: the
            // maximum of the level can never exceed the maximum of what produced it, and
            // every source texel must be represented by at least one target texel that is
            // at least as far.
            float sourceMax = source.Max();
            float reducedMax = reduced.Max();
            c.Eq(sourceMax, reducedMax, $"{w}x{h}: the farthest sample survives the reduction");
            c.True(reduced.All(value => value <= sourceMax),
                $"{w}x{h}: no texel invents a depth farther than anything beneath it");

            // Per texel, the conservative claim in full: every target is at least as far as
            // each of the four samples it nominally covers.
            for (int y = 0; y < th; y++)
            {
                for (int x = 0; x < tw; x++)
                {
                    float target = reduced[y * tw + x];
                    for (int dy = 0; dy <= 1; dy++)
                    {
                        for (int dx = 0; dx <= 1; dx++)
                        {
                            int sx = Math.Min(x * 2 + dx, w - 1);
                            int sy = Math.Min(y * 2 + dy, h - 1);
                            c.True(target >= source[sy * w + sx],
                                $"{w}x{h}: target ({x},{y}) is never nearer than source ({sx},{sy})");
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Reducing repeatedly must terminate at one texel holding the farthest depth in the
    /// whole image. That final texel is what a test against a section covering the entire
    /// screen would read, and if the chain lost anything on the way down it would read as
    /// nearer than the true scene and hide everything.
    /// </summary>
    static void DownToOneTexel(Check c)
    {
        const int width = 37;
        const int height = 21;
        var rnd = new Random(4242);
        var level = new float[width * height];
        for (int i = 0; i < level.Length; i++) level[i] = (float)rnd.NextDouble();
        // A deliberate farthest sample in an awkward place: the last texel of an odd row of
        // an odd image, which is the one every naive fold drops.
        level[^1] = 0.999f;
        float farthestAnywhere = level.Max();

        int w = width, h = height;
        int steps = 0;
        while (w > 1 || h > 1)
        {
            level = LodHzbReference.Reduce(level, w, h);
            (w, h) = (Math.Max(1, w >> 1), Math.Max(1, h >> 1));
            steps++;
            c.True(steps <= 32, "the reduction terminates rather than looping");
        }

        c.Eq(1, level.Length, "the chain ends at a single texel");
        c.Eq(farthestAnywhere, level[0], "which holds the farthest depth anywhere in the image");
        c.True(level[0] >= 0.999f,
            "including the sample planted in the corner every naive fold drops");
        c.Eq(LodHzbReference.LevelCount(width, height) - 1, steps,
            "and takes exactly the number of steps the level count promised");
    }

    /// <summary>
    /// The cull shader is the first thing in this mod whose output a player can see the
    /// absence of, so what matters is not that it hides the right things but that it cannot
    /// hide the wrong ones.
    ///
    /// The safety argument is structural: it writes only the literal zero, only into the
    /// instance-count word, and only under VERDICT_OCCLUDED. Nothing in it can raise an
    /// instance count. That is what stops it resurrecting a section the CPU suppressed for a
    /// reason the GPU knows nothing about - vanilla ownership, a mixed seam, the distance cap -
    /// none of which appear in a depth test. These assertions hold that shape, because the
    /// alternative is a GLSL review every time the file is touched.
    /// </summary>
    static void CullShaderCanOnlyStopADraw(Check c)
    {
        string source = LodHzbClassifier.CullSource;

        c.True(source.Contains("#version 430"), "the cull shader declares the version compute needs");
        c.True(source.Contains("buffer Commands"), "it binds the command buffer");
        c.True(source.Contains("readonly buffer Boxes"), "and reads the boxes without writing them");

        // The command layout it addresses has to be the one the driver reads.
        c.True(source.Contains($"COMMAND_WORDS = {LodGpuIndirectCommand.StrideBytes / 4}u"),
            "it strides by the real command size");
        c.True(source.Contains(
                $"INSTANCE_COUNT_WORD = {LodGpuIndirectCommand.InstanceCountOffset / 4}u"),
            "and writes at the instance-count word the command format defines");

        // The one write in the whole shader, and what guards it.
        // Assignments INTO the array, which is "...] =". Matching a bare "=" also catches the
        // buffer declaration, because "binding = 1" lives on that line.
        string[] writes = source.Split((char)10)
            .Where(line => System.Text.RegularExpressions.Regex.IsMatch(
                line, @"commands\[[^]]+\]\s*=(?!=)\s*"))
            .Select(line => line.Trim())
            .ToArray();
        c.Eq(1, writes.Length, "the cull shader writes to the command buffer exactly once");
        c.True(writes[0].EndsWith("= 0u;", StringComparison.Ordinal),
            "and the only value it ever writes is zero");
        c.True(source.Contains("if (verdict == VERDICT_OCCLUDED)"),
            "guarded by the occluded verdict alone, so every fail-open cause still draws");

        // The widening counter's precondition, pinned where it can be read next to the code
        // that counts it. The classifier sets this bit only for a section it HID, so any
        // caller that counts it under "not hidden" makes the two mutually exclusive and the
        // figure structurally zero. That shipped, and reported "widening buys nothing" for a
        // whole playtest before the log gave it away.
        c.True(LodHzbClassifier.ComputeSource.Contains(
                "if (verdict == VERDICT_OCCLUDED && TestBox(lo, hi, TEXELS_NARROW) != VERDICT_OCCLUDED)"),
            "the widening flag is set only for sections the primary width hid");

        // Both passes must judge at the same width, or the measurement and the culling are
        // answering different questions and the log stops describing what is on screen.
        c.True(source.Contains("TEXELS_PRIMARY"), "the cull pass tests at the primary width");
        c.True(LodHzbClassifier.SharedSource.Contains("uint TestBox(vec3 lo, vec3 hi, int texelsPerAxis)"),
            "and the box test itself lives in the source both passes include");
        c.False(source.Replace(LodHzbClassifier.SharedSource, "").Contains("uint TestBox("),
            "the cull shader does not carry a second copy of the box test");

        // Group sizing has to cover every command; a rounding-down bug would leave the tail
        // of the draw list untested, which reads as culling that mysteriously stops working
        // on busy frames.
        c.Eq(0, LodGpuCullPass.GroupsFor(0), "no commands need no workgroups");
        c.Eq(1, LodGpuCullPass.GroupsFor(1), "one command still needs a whole workgroup");
        c.Eq(1, LodGpuCullPass.GroupsFor(64), "a full workgroup is one workgroup");
        c.Eq(2, LodGpuCullPass.GroupsFor(65), "and one command past it needs another");
        c.Eq(157, LodGpuCullPass.GroupsFor(10000), "ten thousand commands round up, never down");
    }
}
