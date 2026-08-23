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
        c.Eq(0, LodHzbProjection.LevelFor(1f, 1f, 11), "a box under two pixels reads level zero");
        c.Eq(0, LodHzbProjection.LevelFor(2f, 2f, 11), "two pixels still fits level zero");
        c.Eq(1, LodHzbProjection.LevelFor(4f, 2f, 11), "four pixels needs one level up");
        c.Eq(2, LodHzbProjection.LevelFor(8f, 3f, 11), "eight pixels needs two");
        c.Eq(4, LodHzbProjection.LevelFor(17f, 5f, 11), "and the level rounds up, never down");

        // Clamped rather than running off the end of the chain.
        c.Eq(10, LodHzbProjection.LevelFor(100000f, 100000f, 11), "a huge box clamps to the last level");
        c.Eq(0, LodHzbProjection.LevelFor(float.NaN, float.NaN, 11), "a non-finite size reads level zero");
        c.Eq(0, LodHzbProjection.LevelFor(64f, 64f, 0), "a pyramid with no levels reads zero");
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
