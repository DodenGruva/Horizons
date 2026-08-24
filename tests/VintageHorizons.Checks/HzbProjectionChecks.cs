namespace VintageHorizons.Checks;

/// <summary>
/// Projecting a section's box onto the depth pyramid, and the one inequality that decides
/// whether it is hidden.
///
/// Phase 4's gate is written almost entirely in the negative: near-plane, off-screen,
/// projection-change and invalid-value cases must FAIL OPEN. So most of what follows asserts
/// that the code declines to answer. That is not defensive padding - a wrong "hidden" is
/// invisible in code review and shows up as terrain missing from a view nobody thought to
/// stand in, which is the exact symptom this renderer has already lost two sessions to.
/// </summary>
public static class HzbProjectionChecks
{
    public static void Run(Check c)
    {
        SanityOfTheFixture(c);
        HiddenBehindNearerDepth(c);
        VisibleInFrontOfIt(c);
        NearPlaneFailsOpen(c);
        OffScreenFailsOpen(c);
        InvalidValuesFailOpen(c);
        LevelChoiceRoundsUp(c);
        CoarserLevelsNeverHideMore(c);
        SubCellsTileTheParentExactly(c);
        NeverHidesABoxThatPokesOut(c);
        WiderSamplingHidesMore(c);
    }

    /// <summary>
    /// A perspective projection looking down -Z, built the way the engine's own matrices
    /// are: column-major, m[col * 4 + row]. Everything below is expressed in
    /// camera-relative coordinates, exactly as the renderer's section boxes are.
    /// </summary>
    static float[] Perspective(float fovYRadians = 1.2f, float aspect = 16f / 9f,
        float near = 0.1f, float far = 40000f)
    {
        float f = 1f / MathF.Tan(fovYRadians / 2f);
        var m = new float[16];
        m[0] = f / aspect;
        m[5] = f;
        m[10] = (far + near) / (near - far);
        m[11] = -1f;
        m[14] = 2f * far * near / (near - far);
        return m;
    }

    /// <summary>A pyramid holding one depth everywhere, which is all most of these need.</summary>
    sealed class FlatPyramid : ILodHzbLevels
    {
        readonly float depth;
        readonly int width, height;

        public FlatPyramid(float depth, int width = 1920, int height = 1080)
        {
            this.depth = depth;
            this.width = width;
            this.height = height;
            Levels = LodHzbReference.LevelCount(width, height);
        }

        public int Levels { get; }
        public (int Width, int Height) Size(int level) =>
            LodHzbReference.LevelSize(width, height, level);
        public float Farthest(int level, int x, int y) => depth;
    }

    /// <summary>A pyramid that returns whatever the test tells it to, including nonsense.</summary>
    sealed class ScriptedPyramid : ILodHzbLevels
    {
        readonly Func<int, int, int, float> sample;
        public ScriptedPyramid(Func<int, int, int, float> sample, int levels = 11)
        {
            this.sample = sample;
            Levels = levels;
        }
        public int Levels { get; }
        public (int Width, int Height) Size(int level) =>
            LodHzbReference.LevelSize(1920, 1080, level);
        public float Farthest(int level, int x, int y) => sample(level, x, y);
    }

    /// <summary>
    /// The fixture has to actually put a box on screen before any claim about hiding it
    /// means anything. A test whose box projects off screen would "pass" every occlusion
    /// assertion by failing open, and prove nothing at all.
    /// </summary>
    static void SanityOfTheFixture(Check c)
    {
        LodHzbScreenBounds bounds = LodHzbProjection.Project(
            Perspective(), -40, -20, -1000, 40, 20, -900);

        c.True(bounds.Usable, "the reference box projects to a usable rectangle");
        c.True(bounds.MinU < bounds.MaxU, "and has width on screen");
        c.True(bounds.MinV < bounds.MaxV, "and height");
        c.True(bounds.NearestDepth is > 0f and < 1f, "and a depth inside the buffer's range");

        // Farther boxes must land nearer to 1. If this inverts, every comparison below is
        // backwards and would still pass with the inequality flipped.
        LodHzbScreenBounds far = LodHzbProjection.Project(
            Perspective(), -40, -20, -20000, 40, 20, -19900);
        c.True(far.NearestDepth > bounds.NearestDepth,
            "a more distant box has a larger depth value, so max means farthest");
    }

    static void HiddenBehindNearerDepth(Check c)
    {
        var projection = Perspective();
        LodHzbScreenBounds bounds = LodHzbProjection.Project(
            projection, -40, -20, -20000, 40, 20, -19900);
        c.True(bounds.Usable, "the distant box projects");

        // Everything on screen was drawn much nearer than the box.
        var scene = new FlatPyramid(bounds.NearestDepth - 0.001f);
        c.True(LodHzbProjection.IsOccluded(bounds, scene, 1920, 1080, out int level),
            "a box entirely behind the drawn scene is hidden");
        c.True(level >= 0, "and reports which level answered");
    }

    static void VisibleInFrontOfIt(Check c)
    {
        var projection = Perspective();
        LodHzbScreenBounds bounds = LodHzbProjection.Project(
            projection, -40, -20, -20000, 40, 20, -19900);

        var scene = new FlatPyramid(bounds.NearestDepth + 0.001f);
        c.False(LodHzbProjection.IsOccluded(bounds, scene, 1920, 1080, out _),
            "a box nearer than the scene is drawn");

        // The coplanar case. Equality must not hide: the box's nearest point sits exactly
        // on the surface already drawn, and that surface is what it would replace.
        var coplanar = new FlatPyramid(bounds.NearestDepth);
        c.False(LodHzbProjection.IsOccluded(bounds, coplanar, 1920, 1080, out _),
            "a box exactly level with the scene is drawn");

        // One texel of the covered region being farther is enough to keep it: the box is
        // visible through that gap even if everything around it is nearer.
        var gap = new ScriptedPyramid((_, x, _) =>
            x % 2 == 0 ? bounds.NearestDepth - 0.001f : bounds.NearestDepth + 0.001f);
        c.False(LodHzbProjection.IsOccluded(bounds, gap, 1920, 1080, out _),
            "a single farther texel in the covered region keeps the box drawn");
    }

    /// <summary>
    /// The case that matters most. A box straddling the near plane has corners with w at or
    /// below zero, and the perspective divide sends those through infinity - producing a
    /// small rectangle in an arbitrary place, which is precisely the shape that hides
    /// terrain sitting right in front of the player.
    /// </summary>
    static void NearPlaneFailsOpen(Check c)
    {
        var projection = Perspective();

        // Straddling: the box spans the camera plane.
        LodHzbScreenBounds straddling = LodHzbProjection.Project(
            projection, -40, -20, -100, 40, 20, 100);
        c.False(straddling.Usable, "a box straddling the near plane fails open");

        // Entirely behind the camera.
        LodHzbScreenBounds behind = LodHzbProjection.Project(
            projection, -40, -20, 100, 40, 20, 200);
        c.False(behind.Usable, "a box behind the camera fails open");

        // Exactly on the camera plane, where w is zero rather than merely small.
        LodHzbScreenBounds onPlane = LodHzbProjection.Project(
            projection, -40, -20, 0, 40, 20, 0);
        c.False(onPlane.Usable, "a box on the camera plane fails open");

        // And an unusable projection can never be occluded, whatever pyramid it meets.
        c.False(LodHzbProjection.IsOccluded(straddling, new FlatPyramid(0.99f), 1920, 1080, out _),
            "an unusable projection is never reported hidden");
    }

    static void OffScreenFailsOpen(Check c)
    {
        var projection = Perspective();

        // Far off to the left, still in front of the camera.
        LodHzbScreenBounds left = LodHzbProjection.Project(
            projection, -100000, -20, -20000, -90000, 20, -19900);
        c.False(left.Usable, "a box entirely off the left of the screen fails open");

        // Far above.
        LodHzbScreenBounds above = LodHzbProjection.Project(
            projection, -40, 90000, -20000, 40, 100000, -19900);
        c.False(above.Usable, "a box entirely above the screen fails open");

        // Straddling the screen edge is NOT off screen, and must still be usable - this is
        // the mixed case where half the box is visible.
        LodHzbScreenBounds straddling = LodHzbProjection.Project(
            projection, -40000, -20, -20000, 40, 20, -19900);
        c.True(straddling.Usable, "a box crossing the screen edge still projects");
        c.True(straddling.MinU >= 0f && straddling.MaxU <= 1f,
            "and its rectangle is clamped into the screen");
    }

    static void InvalidValuesFailOpen(Check c)
    {
        c.False(LodHzbProjection.Project(null!, -1, -1, -10, 1, 1, -9).Usable,
            "a missing matrix fails open");
        c.False(LodHzbProjection.Project(new float[4], -1, -1, -10, 1, 1, -9).Usable,
            "a short matrix fails open");

        var nan = Perspective();
        nan[5] = float.NaN;
        c.False(LodHzbProjection.Project(nan, -1, -1, -10, 1, 1, -9).Usable,
            "a matrix carrying NaN fails open");

        var infinite = Perspective();
        infinite[0] = float.PositiveInfinity;
        c.False(LodHzbProjection.Project(infinite, -1, -1, -10, 1, 1, -9).Usable,
            "a matrix carrying infinity fails open");

        // A projection-change mid-frame shows up as an inverted or degenerate box.
        c.False(LodHzbProjection.Project(Perspective(), 40, -20, -1000, -40, 20, -900).Usable,
            "an inverted box fails open");

        // A pyramid that returns nonsense must not be believed.
        LodHzbScreenBounds bounds = LodHzbProjection.Project(
            Perspective(), -40, -20, -20000, 40, 20, -19900);
        c.False(LodHzbProjection.IsOccluded(
                bounds, new ScriptedPyramid((_, _, _) => float.NaN), 1920, 1080, out _),
            "a NaN sample keeps the box drawn");
        c.False(LodHzbProjection.IsOccluded(
                bounds, new ScriptedPyramid((_, _, _) => float.NegativeInfinity), 1920, 1080, out _),
            "an infinite sample keeps the box drawn");
        c.False(LodHzbProjection.IsOccluded(bounds, null!, 1920, 1080, out _),
            "no pyramid at all keeps the box drawn");
        c.False(LodHzbProjection.IsOccluded(bounds, new FlatPyramid(0.1f), 0, 0, out _),
            "a zero-sized screen keeps the box drawn");
    }

    static void LevelChoiceRoundsUp(Check c)
    {
        // The width is stated rather than taken from the default. These cases describe the
        // rounding rule - pick the level where the box spans at most this many texels - and
        // that rule is what must not drift; writing them against whatever the default happens
        // to be made three of them fail the day the default was widened, which was a check
        // reporting a settings change as a defect.
        const int narrow = LodHzbProjection.NarrowTexelsPerAxis;
        c.Eq(0, LodHzbProjection.LevelFor(1f, 1f, 11, narrow), "a box under two pixels reads level zero");
        c.Eq(0, LodHzbProjection.LevelFor(2f, 2f, 11, narrow), "two pixels still fits level zero");
        c.Eq(1, LodHzbProjection.LevelFor(4f, 2f, 11, narrow), "four pixels needs one level up");
        c.Eq(2, LodHzbProjection.LevelFor(8f, 3f, 11, narrow), "eight pixels needs two");
        c.Eq(4, LodHzbProjection.LevelFor(17f, 5f, 11, narrow), "and the level rounds up, never down");

        // The same rule at the shipped width, which is the one the renderer actually uses.
        const int wide = LodHzbProjection.DefaultTexelsPerAxis;
        c.Eq(0, LodHzbProjection.LevelFor(8f, 8f, 11, wide), "eight pixels fits level zero at eight texels");
        c.Eq(1, LodHzbProjection.LevelFor(16f, 4f, 11, wide), "sixteen needs one level up");
        c.Eq(2, LodHzbProjection.LevelFor(32f, 9f, 11, wide), "thirty-two needs two");
        c.Eq(4, LodHzbProjection.LevelFor(65f, 20f, 11, wide), "and it still rounds up, never down");

        // The overload with no width must agree with naming the default explicitly, or the
        // shader and the C# twin can be reading different levels for the same box.
        foreach (float size in new[] { 1f, 4f, 17f, 64f, 300f, 4000f })
        {
            c.Eq(LodHzbProjection.LevelFor(size, size, 11, wide),
                LodHzbProjection.LevelFor(size, size, 11),
                $"the default overload picks the default width's level at {size} pixels");
        }

        // A wider footprint never picks a coarser level - that is the whole reason widening
        // hides more, and it holds one-directionally across the range.
        for (float size = 1f; size < 8192f; size *= 1.7f)
        {
            c.True(LodHzbProjection.LevelFor(size, size, 11, wide)
                    <= LodHzbProjection.LevelFor(size, size, 11, narrow),
                $"eight texels never reads a coarser level than two at {size:0} pixels");
        }

        // Clamped rather than running off the end of the chain.
        c.Eq(10, LodHzbProjection.LevelFor(100000f, 100000f, 11), "a huge box clamps to the last level");
        c.Eq(0, LodHzbProjection.LevelFor(float.NaN, float.NaN, 11), "a non-finite size reads level zero");
        c.Eq(0, LodHzbProjection.LevelFor(64f, 64f, 0), "a pyramid with no levels reads zero");
    }

    /// <summary>
    /// The sub-cell split used to measure how much a finer draw unit would buy.
    ///
    /// It has to tile the parent exactly. A gap would let a cell claim ground nothing tests,
    /// an overlap would count the same ground twice, and either turns "how much could a
    /// finer unit skip" into a different question with a plausible-looking answer. Since the
    /// whole point of the measurement is to inform whether cluster subdivision is worth
    /// building, a quietly wrong denominator would be expensive.
    /// </summary>
    static void SubCellsTileTheParentExactly(Check c)
    {
        const double minX = -100, minZ = 40, maxX = 156, maxZ = 296;
        int n = LodHzbProjection.SubdivisionsPerAxis;
        c.Eq(n * n, LodHzbProjection.SubCellCount, "the cell count is the square of the axis count");

        double area = 0;
        var seen = new List<(double, double, double, double)>();

        for (int i = 0; i < LodHzbProjection.SubCellCount; i++)
        {
            LodHzbProjection.SubCell(i, minX, 0, minZ, maxX, 10, maxZ,
                out double cx0, out double cz0, out double cx1, out double cz1);

            c.True(cx1 > cx0, $"cell {i} has width");
            c.True(cz1 > cz0, $"cell {i} has depth");
            c.True(cx0 >= minX && cx1 <= maxX, $"cell {i} stays inside the parent in x");
            c.True(cz0 >= minZ && cz1 <= maxZ, $"cell {i} stays inside the parent in z");

            area += (cx1 - cx0) * (cz1 - cz0);
            seen.Add((cx0, cz0, cx1, cz1));
        }

        // Areas summing to the parent's, with every cell inside it, is only possible if the
        // cells neither overlap nor leave a gap.
        c.Near((maxX - minX) * (maxZ - minZ), area, 0.0001, "the cells sum to the parent's area");

        // The extremes are the parent's own edges, not an accumulated approximation of them.
        c.Near(minX, seen.Min(cell => cell.Item1), 0.0000001, "the cells start at the parent's near x edge");
        c.Near(minZ, seen.Min(cell => cell.Item2), 0.0000001, "and its near z edge");
        c.Near(maxX, seen.Max(cell => cell.Item3), 0.0000001, "and reach its far x edge exactly");
        c.Near(maxZ, seen.Max(cell => cell.Item4), 0.0000001, "and its far z edge exactly");

        c.Eq(LodHzbProjection.SubCellCount, seen.Distinct().Count(), "every cell is distinct");

        // Out-of-range indices clamp rather than producing a box outside the parent, which
        // the shader relies on: it loops a fixed count and must never test empty space.
        LodHzbProjection.SubCell(-5, minX, 0, minZ, maxX, 10, maxZ,
            out double lowX, out _, out _, out _);
        c.Near(minX, lowX, 0.0000001, "a negative index clamps to the first cell");
        LodHzbProjection.SubCell(9999, minX, 0, minZ, maxX, 10, maxZ,
            out _, out _, out double highX, out _);
        c.Near(maxX, highX, 0.0000001, "an oversized index clamps to the last");

        // A degenerate parent must not produce inverted cells - the projection would refuse
        // them, but the measurement would then read as "nothing hideable" for a bad reason.
        LodHzbProjection.SubCell(0, 10, 0, 10, 10, 10, 10,
            out double dx0, out double dz0, out double dx1, out double dz1);
        c.True(dx1 >= dx0 && dz1 >= dz0, "a zero-area parent yields non-inverted cells");
    }

    /// <summary>
    /// A synthetic scene with a known occluder, and boxes that stick out of it by a hair.
    ///
    /// This replaces what the in-game comparison against occlusion queries was being asked
    /// to do. That comparison can never read zero: the pyramid judges last frame's depth
    /// buffer and the query reports an actual draw some frames earlier, so a moving camera
    /// makes them disagree while both are correct. Chasing it to zero was chasing the
    /// instrument. The question underneath - can this test hide a box that is partly in
    /// front of the scene - is deterministic, and belongs here.
    ///
    /// The occluder is a rectangle of near depth over part of the screen, with the rest at
    /// the depth-clear value, exactly as sky arrives in a real buffer.
    /// </summary>
    static void NeverHidesABoxThatPokesOut(Check c)
    {
        const int width = 1920, height = 1080;
        int levels = LodHzbReference.LevelCount(width, height);

        // Level 0 of a scene: near depth inside the occluder, background outside it.
        const float occluderDepth = 0.90f;
        var scene = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Background on the left, right and top, occluder filling the rest - the
                // shape a ridge actually makes. The margins have to be well clear of the
                // test boxes: a coarse pyramid level pools 128 or 256 screen pixels into
                // one texel, so a box "inside" the occluder by less than a texel still
                // samples background and correctly refuses to hide. The first version of
                // this fixture failed for exactly that reason, which is the same effect
                // that stops real sections hiding.
                bool insideOccluder = x >= 300 && x < 1500 && y >= 200;
                scene[y * width + x] = insideOccluder ? occluderDepth : 1.0f;
            }
        }

        // Reduced with the same rule the shader uses, so the pyramid under test is the one
        // the reduction actually produces rather than a hand-written stand-in.
        var chain = new List<(float[] Data, int W, int H)> { (scene, width, height) };
        for (int level = 1; level < levels; level++)
        {
            (float[] previous, int pw, int ph) = chain[level - 1];
            chain.Add((LodHzbReference.Reduce(previous, pw, ph),
                Math.Max(1, pw >> 1), Math.Max(1, ph >> 1)));
        }

        var pyramid = new BuiltPyramid(chain);

        // A box well inside the occluder and behind it: hideable, and the fixture is
        // worthless if this one does not hide.
        c.True(LodHzbProjection.IsOccluded(
                Bounds(0.25f, 0.40f, 0.35f, 0.50f, occluderDepth + 0.01f),
                pyramid, width, height, out _),
            "a box behind the occluder and well inside it is hidden");

        // Now the cases that must NOT hide. Each is behind the occluder in depth - so the
        // depth comparison alone would hide it - and each overlaps background somewhere.
        (string Name, float MinU, float MinV, float MaxU, float MaxV)[] pokingOut =
        {
            ("past the left edge", 0.05f, 0.40f, 0.18f, 0.50f),
            ("past the right edge", 0.72f, 0.40f, 0.85f, 0.50f),
            ("above the top edge", 0.25f, 0.05f, 0.35f, 0.20f),
            ("straddling the top-left corner", 0.05f, 0.05f, 0.18f, 0.20f),
            ("spanning the whole screen", 0.0f, 0.0f, 1.0f, 1.0f),
        };

        foreach ((string name, float u0, float v0, float u1, float v1) in pokingOut)
        {
            c.False(LodHzbProjection.IsOccluded(
                    Bounds(u0, v0, u1, v1, occluderDepth + 0.01f),
                    pyramid, width, height, out _),
                $"a box reaching {name} of the occluder is drawn");
        }

        // And the boundary itself, walked one screen-percent at a time. Somewhere along this
        // sweep the box stops being fully inside the occluder, and from that point on it
        // must never be hidden again - a single hidden verdict after the first visible one
        // would mean the rounding lets a box escape at some widths but not others.
        bool leftTheOccluder = false;
        for (int step = 0; step <= 40; step++)
        {
            float right = 0.35f + step * 0.015f;
            bool hidden = LodHzbProjection.IsOccluded(
                Bounds(0.25f, 0.40f, right, 0.50f, occluderDepth + 0.01f),
                pyramid, width, height, out _);

            if (!hidden) leftTheOccluder = true;
            else if (leftTheOccluder)
            {
                c.True(false, $"a box widened to {right:0.000} hid again after escaping the occluder");
                break;
            }
        }
        c.True(leftTheOccluder, "widening the box past the occluder eventually stops hiding it");

        // Depth, the other axis: a box nearer than the occluder is never hidden however
        // snugly it sits inside it.
        c.False(LodHzbProjection.IsOccluded(
                Bounds(0.25f, 0.40f, 0.35f, 0.50f, occluderDepth - 0.01f),
                pyramid, width, height, out _),
            "a box in front of the occluder is drawn");
    }

    /// <summary>
    /// How much the sampling width is worth, measured on a synthetic ridge.
    ///
    /// A box is only hidden if it is inside the occluder by at least one texel of the level
    /// it is tested at, and the level is chosen so the box spans at most N texels. So a
    /// wider sampling footprint picks a finer level, shrinks the clearance a box needs, and
    /// should hide strictly more - at a sample cost growing as the square of N.
    ///
    /// This is the alternative to cluster subdivision for the same problem, and unlike
    /// subdivision it changes only the test, not what gets drawn or how terrain is stored.
    /// The numbers below are asserted rather than printed so a regression shows up as a
    /// failure rather than as a line nobody reads.
    /// </summary>
    static void WiderSamplingHidesMore(Check c)
    {
        const int width = 1920, height = 1080;
        int levels = LodHzbReference.LevelCount(width, height);
        const float occluderDepth = 0.90f;

        var scene = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                scene[y * width + x] = x >= 300 && x < 1500 && y >= 200 ? occluderDepth : 1.0f;
            }
        }

        var chain = new List<(float[] Data, int W, int H)> { (scene, width, height) };
        for (int level = 1; level < levels; level++)
        {
            (float[] previous, int pw, int ph) = chain[level - 1];
            chain.Add((LodHzbReference.Reduce(previous, pw, ph),
                Math.Max(1, pw >> 1), Math.Max(1, ph >> 1)));
        }
        var pyramid = new BuiltPyramid(chain);

        // A grid of candidate boxes of several sizes, all behind the occluder, spread over
        // the screen. Some genuinely overlap background and must never hide at any width.
        var boxes = new List<LodHzbScreenBounds>();
        foreach (float size in new[] { 0.05f, 0.10f, 0.20f, 0.40f })
        {
            for (float u = 0f; u + size <= 1f; u += 0.05f)
            {
                for (float v = 0f; v + size <= 1f; v += 0.05f)
                {
                    boxes.Add(Bounds(u, v, u + size, v + size, occluderDepth + 0.01f));
                }
            }
        }
        c.True(boxes.Count > 500, "the sweep covers a useful number of boxes");

        int Hidden(int perAxis) => boxes.Count(box =>
            LodHzbProjection.IsOccluded(box, pyramid, width, height, perAxis, out _));

        int at2 = Hidden(2);
        int at4 = Hidden(4);
        int at8 = Hidden(8);
        int at16 = Hidden(16);

        // Monotone: a finer level can only ever pool fewer foreign pixels into a texel, so
        // widening the footprint can never take a hidden verdict away.
        c.True(at4 >= at2, "sampling four texels per axis hides at least as much as two");
        c.True(at8 >= at4, "eight at least as much as four");
        c.True(at16 >= at8, "sixteen at least as much as eight");

        // And it is a real gain, not a rounding artifact. If this ever stops holding, the
        // sampling width has stopped being a lever and the sky refusals need another answer.
        c.True(at8 > at2, "widening the footprint hides materially more than the default two");

        // Measured on this fixture, 2026-08-23: of 1,085 boxes behind the occluder, two
        // texels per axis hides 326, four hides 368, eight hides 404 and sixteen hides 421.
        // So the default leaves about a fifth of the achievable hiding on the table, and the
        // curve flattens after eight - which is the shape that decides how wide is worth
        // paying for. Pinned loosely, as a regression guard rather than a golden value.
        c.True(at8 >= at2 + at2 / 5,
            "eight texels per axis hides at least a fifth more than two");
        c.True(at16 - at8 < at8 - at4,
            "and the gain flattens: sixteen adds less over eight than eight added over four");

        // The ceiling: no width may hide a box that genuinely overlaps background, because
        // the reduction is still max and background is still the farthest thing there.
        foreach (int perAxis in new[] { 2, 4, 8, 16 })
        {
            c.False(LodHzbProjection.IsOccluded(
                    Bounds(0.0f, 0.0f, 1.0f, 1.0f, occluderDepth + 0.01f),
                    pyramid, width, height, perAxis, out _),
                $"a full-screen box still never hides at {perAxis} texels per axis");
            c.False(LodHzbProjection.IsOccluded(
                    Bounds(0.05f, 0.40f, 0.18f, 0.50f, occluderDepth + 0.01f),
                    pyramid, width, height, perAxis, out _),
                $"a box over the left background still never hides at {perAxis} texels per axis");
        }
    }

    /// <summary>A screen-space rectangle and depth, bypassing projection to test the sampler.</summary>
    static LodHzbScreenBounds Bounds(float minU, float minV, float maxU, float maxV, float depth) =>
        new(true, minU, minV, maxU, maxV, depth, "");

    /// <summary>A pyramid backed by real reduced levels rather than a constant.</summary>
    sealed class BuiltPyramid : ILodHzbLevels
    {
        readonly List<(float[] Data, int W, int H)> chain;
        public BuiltPyramid(List<(float[] Data, int W, int H)> chain) => this.chain = chain;
        public int Levels => chain.Count;
        public (int Width, int Height) Size(int level) => (chain[level].W, chain[level].H);
        public float Farthest(int level, int x, int y) =>
            chain[level].Data[y * chain[level].W + x];
    }

    /// <summary>
    /// The conservativeness argument, stated as a property. Sampling a coarser level pools
    /// more pixels into one texel, and pooling with max can only push the farthest depth
    /// farther away - so a coarser level can never declare hidden something a finer level
    /// would have drawn. If that ever inverts, the level choice becomes a source of missing
    /// terrain rather than a performance knob.
    /// </summary>
    static void CoarserLevelsNeverHideMore(Check c)
    {
        var projection = Perspective();
        LodHzbScreenBounds bounds = LodHzbProjection.Project(
            projection, -40, -20, -20000, 40, 20, -19900);
        c.True(bounds.Usable, "the property fixture projects");

        // A pyramid whose coarse levels are strictly farther, as a real max-reduction makes
        // them: level 0 near, each level up pooling in something farther.
        var rnd = new Random(90210);
        float baseDepth = bounds.NearestDepth - 0.01f;
        var pooling = new ScriptedPyramid((level, x, y) =>
            baseDepth + level * 0.004f + (float)rnd.NextDouble() * 0.0001f);

        bool occludedAtSomeLevel = false;
        for (int level = 0; level < 11; level++)
        {
            // Sampling by hand at each level, rather than trusting the chooser, so the
            // claim is about the pyramid rather than about which level got picked.
            float sample = pooling.Farthest(level, 0, 0);
            bool wouldHide = bounds.NearestDepth > sample;
            if (level == 0) occludedAtSomeLevel = wouldHide;
            if (occludedAtSomeLevel && !wouldHide) continue;
            c.True(!wouldHide || sample < bounds.NearestDepth,
                $"level {level}: hiding requires the scene to be strictly nearer");
        }

        // And the real chooser agrees with a hand-picked coarser level on a scene that is
        // uniformly nearer: both hide it, because uniformity removes the level's influence.
        var uniform = new FlatPyramid(bounds.NearestDepth - 0.001f);
        c.True(LodHzbProjection.IsOccluded(bounds, uniform, 1920, 1080, out int chosen),
            "a uniformly nearer scene hides the box");
        c.True(chosen >= 0 && chosen < uniform.Levels, "at a level inside the chain");
    }
}
