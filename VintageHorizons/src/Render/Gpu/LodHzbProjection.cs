namespace VintageHorizons;

/// <summary>
/// A section's box as it lands on screen: the rectangle it covers in [0,1] screen
/// coordinates, and the depth of its nearest corner.
///
/// <see cref="Usable"/> is the whole safety story. Every case this projection cannot answer
/// exactly - a box straddling the near plane, a corner behind the camera, a matrix carrying
/// an infinity - sets it false, and an unusable projection means DRAW THE SECTION. There is
/// no third answer, and no path that guesses.
/// </summary>
internal readonly record struct LodHzbScreenBounds(
    bool Usable,
    float MinU,
    float MinV,
    float MaxU,
    float MaxV,
    float NearestDepth,
    string Reason)
{
    public static LodHzbScreenBounds FailOpen(string reason) =>
        new(false, 0f, 0f, 0f, 0f, 0f, reason);

    public float WidthPixels(int screenWidth) => (MaxU - MinU) * screenWidth;
    public float HeightPixels(int screenHeight) => (MaxV - MinV) * screenHeight;
}

/// <summary>One pyramid, as the test reads it. Implemented by the GPU texture and by fakes.</summary>
internal interface ILodHzbLevels
{
    int Levels { get; }
    (int Width, int Height) Size(int level);

    /// <summary>The farthest depth in the patch this texel stands for.</summary>
    float Farthest(int level, int x, int y);
}

/// <summary>
/// Projects a camera-relative section box onto the depth pyramid and answers whether the
/// whole box sits behind what has already been drawn.
///
/// The test is one inequality: the box is hidden when its NEAREST point is farther away
/// than the FARTHEST thing already drawn anywhere in the screen region it covers. Both
/// extremes point the same way - nearest for the box, farthest for the scene - because any
/// other pairing would answer "hidden" about a box poking through a gap.
///
/// Everything here is deliberately pessimistic. A coarser pyramid level than strictly
/// needed, a rectangle rounded outward, a depth taken from the closest corner: each makes
/// the inequality harder to satisfy, so each can only ever cause a hidden section to be
/// drawn. None of them can hide a visible one.
/// </summary>
internal static class LodHzbProjection
{
    /// <summary>
    /// Corners must all sit strictly in front of the camera for a perspective divide to
    /// mean anything. A box crossing the near plane projects to coordinates that wrap
    /// through infinity, and the resulting rectangle is not merely inaccurate - it can be
    /// small and in the wrong place, which is the one failure that hides visible terrain.
    /// </summary>
    const float MinimumW = 1e-4f;

    /// <summary>
    /// Projects the camera-relative box through a column-major view-projection matrix
    /// (m[col * 4 + row], matching <see cref="LodFrustum"/> and the shader).
    /// </summary>
    public static LodHzbScreenBounds Project(
        float[] viewProjection,
        double minX, double minY, double minZ,
        double maxX, double maxY, double maxZ)
    {
        if (viewProjection == null || viewProjection.Length < 16)
            return LodHzbScreenBounds.FailOpen("no view-projection matrix");
        for (int i = 0; i < 16; i++)
        {
            if (!float.IsFinite(viewProjection[i]))
                return LodHzbScreenBounds.FailOpen("view-projection matrix is not finite");
        }
        if (maxX < minX || maxY < minY || maxZ < minZ)
            return LodHzbScreenBounds.FailOpen("inverted box");

        float minU = float.PositiveInfinity, minV = float.PositiveInfinity;
        float maxU = float.NegativeInfinity, maxV = float.NegativeInfinity;
        float nearestDepth = float.PositiveInfinity;

        for (int corner = 0; corner < 8; corner++)
        {
            double x = (corner & 1) == 0 ? minX : maxX;
            double y = (corner & 2) == 0 ? minY : maxY;
            double z = (corner & 4) == 0 ? minZ : maxZ;

            double clipX = viewProjection[0] * x + viewProjection[4] * y + viewProjection[8] * z + viewProjection[12];
            double clipY = viewProjection[1] * x + viewProjection[5] * y + viewProjection[9] * z + viewProjection[13];
            double clipZ = viewProjection[2] * x + viewProjection[6] * y + viewProjection[10] * z + viewProjection[14];
            double clipW = viewProjection[3] * x + viewProjection[7] * y + viewProjection[11] * z + viewProjection[15];

            if (!double.IsFinite(clipX) || !double.IsFinite(clipY)
                || !double.IsFinite(clipZ) || !double.IsFinite(clipW))
            {
                return LodHzbScreenBounds.FailOpen("a corner projected to a non-finite value");
            }
            if (clipW <= MinimumW)
                return LodHzbScreenBounds.FailOpen("the box crosses or sits behind the near plane");

            float u = (float)(clipX / clipW) * 0.5f + 0.5f;
            float v = (float)(clipY / clipW) * 0.5f + 0.5f;
            float depth = (float)(clipZ / clipW) * 0.5f + 0.5f;

            if (!float.IsFinite(u) || !float.IsFinite(v) || !float.IsFinite(depth))
                return LodHzbScreenBounds.FailOpen("a corner produced a non-finite screen coordinate");

            if (u < minU) minU = u;
            if (u > maxU) maxU = u;
            if (v < minV) minV = v;
            if (v > maxV) maxV = v;
            if (depth < nearestDepth) nearestDepth = depth;
        }

        // Wholly off screen. Not our decision to make - the frustum test owns that, and
        // answering "hidden" here would double up two culls whose disagreement nobody
        // would ever see.
        if (maxU < 0f || minU > 1f || maxV < 0f || minV > 1f)
            return LodHzbScreenBounds.FailOpen("the box projects entirely off screen");

        // A nearest depth at or before the near plane means part of the box is in front of
        // everything the depth buffer holds, so no comparison against it can be valid.
        if (nearestDepth <= 0f || nearestDepth >= 1f)
            return LodHzbScreenBounds.FailOpen("the box's nearest depth is outside the depth range");

        return new LodHzbScreenBounds(
            true,
            Math.Clamp(minU, 0f, 1f),
            Math.Clamp(minV, 0f, 1f),
            Math.Clamp(maxU, 0f, 1f),
            Math.Clamp(maxV, 0f, 1f),
            nearestDepth,
            "");
    }


    /// <summary>
    /// How many pieces a section's box is split into along each horizontal axis when
    /// measuring how much finer testing would buy. Four, so sixteen cells - two levels of
    /// quadtree subdivision, which is the granularity a cluster scheme would plausibly
    /// reach before the per-piece bookkeeping outweighs the saving.
    /// </summary>
    public const int SubdivisionsPerAxis = 4;

    public const int SubCellCount = SubdivisionsPerAxis * SubdivisionsPerAxis;

    /// <summary>
    /// One cell of the box, split horizontally only.
    ///
    /// Horizontally only, and that is the whole point of the exercise. A section is refused
    /// as unhideable because its rectangle overlaps sky, and it overlaps sky because it is
    /// 64 to 1,024 blocks WIDE - wide enough that part of it reaches past the ridge hiding
    /// the rest. Splitting the height would not address that; splitting the footprint does.
    ///
    /// The cells tile the parent exactly: no gaps, no overlap, and the union is the parent
    /// box. Anything else would make the measurement a different question from the one
    /// being asked, which is how much of this section a finer draw unit could skip.
    /// </summary>
    public static void SubCell(
        int index,
        double minX, double minY, double minZ,
        double maxX, double maxY, double maxZ,
        out double cellMinX, out double cellMinZ, out double cellMaxX, out double cellMaxZ)
    {
        int clamped = Math.Clamp(index, 0, SubCellCount - 1);
        int cx = clamped % SubdivisionsPerAxis;
        int cz = clamped / SubdivisionsPerAxis;

        double spanX = (maxX - minX) / SubdivisionsPerAxis;
        double spanZ = (maxZ - minZ) / SubdivisionsPerAxis;

        cellMinX = minX + spanX * cx;
        cellMinZ = minZ + spanZ * cz;

        // The far edge of the last cell is the parent's own edge, taken directly rather
        // than accumulated, so floating-point drift cannot leave a sliver uncovered.
        cellMaxX = cx == SubdivisionsPerAxis - 1 ? maxX : minX + spanX * (cx + 1);
        cellMaxZ = cz == SubdivisionsPerAxis - 1 ? maxZ : minZ + spanZ * (cz + 1);
    }

    /// <summary>
    /// The pyramid level whose texels are big enough that the rectangle spans at most two
    /// of them on each axis, so four samples always cover it.
    ///
    /// Rounding UP a level is safe and rounding down is not: a coarser level pools more
    /// pixels into one texel, and pooling can only push the farthest depth farther, which
    /// makes the box harder to declare hidden.
    /// </summary>
    /// <summary>
    /// How many texels per axis the test is willing to sample.
    ///
    /// The level is picked so the rectangle spans at most this many texels, so allowing more
    /// texels picks a FINER level, and a finer texel pools fewer screen pixels. That matters
    /// because a box can only be hidden if it is inside the occluder by at least one texel -
    /// a texel straddling the edge pulls in the background beyond it and refuses. At two
    /// texels a 384-pixel-wide box is tested at 256-pixel texels and needs a quarter of the
    /// screen of clearance; at eight it is tested at 64-pixel texels and needs an eighth of
    /// that. The cost is the sample count, which grows as the square.
    ///
    /// EIGHT, changed from two on 2026-08-23. Two is the classic choice and it is why big
    /// boxes hid so rarely - the sky problem. Measured offline over the owner's own cache at
    /// his 350-block vanilla view distance, 128 views, two seeds: of the sections two texels
    /// could not hide, eight hides 25.0%. The alternative answer, splitting each section into
    /// a 4x4 grid, hides 14.7% and costs a rewrite of how terrain is stored, meshed and
    /// drawn. Sixteen adds only about 2.5 points over eight, so the curve has flattened by
    /// then and the extra samples are not worth paying for.
    ///
    /// This widens what the test can PROVE hidden. It cannot make it hide something visible:
    /// every sample still comes from a max-reduced pyramid, and more samples can only push
    /// the farthest depth farther. See the checks over widening in HzbProjectionChecks.
    /// </summary>
    public const int DefaultTexelsPerAxis = 8;

    /// <summary>
    /// The width the test used before <see cref="DefaultTexelsPerAxis"/> was widened, kept so
    /// the classifier can still report what the change bought and so the offline measurement
    /// has a fixed baseline to quote against. Not a fallback: nothing selects it at runtime.
    /// </summary>
    public const int NarrowTexelsPerAxis = 2;

    /// <summary>
    /// Four normalized steps of a 24-bit depth buffer. A projected box and the rasterized
    /// surface it coincides with reach depth through different arithmetic, so strict
    /// greater-than alone can call the box hidden when the two differ only by rounding.
    /// That verdict flickers at a precise camera angle and cluster subdivision multiplies
    /// how many independently visible pieces can hit it. Treat this band as undecidable.
    /// </summary>
    public const int OcclusionDepthBiasSteps = 4;
    public const int MaximumDiagnosticDepthBiasSteps = 4096;
    public const float OcclusionDepthBias = OcclusionDepthBiasSteps / 16777215f;

    /// <summary>
    /// Converts a player-selected 24-bit depth-step margin into normalized depth. The
    /// ordinary verdict remains fixed at four steps; only the same-frame cached-on-cached
    /// diagnostic uses larger values while the precise-angle flicker is localized.
    /// </summary>
    public static float DepthBiasForSteps(int steps) =>
        Math.Clamp(steps, OcclusionDepthBiasSteps, MaximumDiagnosticDepthBiasSteps)
        / 16777215f;

    public static int LevelFor(float widthPixels, float heightPixels, int levels) =>
        LevelFor(widthPixels, heightPixels, levels, DefaultTexelsPerAxis);

    public static int LevelFor(float widthPixels, float heightPixels, int levels, int texelsPerAxis)
    {
        if (levels <= 0) return 0;
        int perAxis = Math.Max(2, texelsPerAxis);
        float longest = Math.Max(widthPixels, heightPixels);
        if (!float.IsFinite(longest) || longest <= perAxis) return 0;

        int level = (int)Math.Ceiling(Math.Log2(longest / (double)perAxis));
        return Math.Clamp(level, 0, levels - 1);
    }

    /// <summary>
    /// True when every pixel the box covers already holds something nearer than the box's
    /// closest point.
    ///
    /// Returns false - draw it - for anything it cannot establish. The caller never has to
    /// interpret a failure, because there is no failure value distinct from "visible".
    /// </summary>
    public static bool IsOccluded(
        in LodHzbScreenBounds bounds,
        ILodHzbLevels pyramid,
        int screenWidth,
        int screenHeight,
        out int sampledLevel) =>
        IsOccluded(bounds, pyramid, screenWidth, screenHeight, DefaultTexelsPerAxis, out sampledLevel);

    public static bool IsOccluded(
        in LodHzbScreenBounds bounds,
        ILodHzbLevels pyramid,
        int screenWidth,
        int screenHeight,
        int texelsPerAxis,
        out int sampledLevel)
    {
        sampledLevel = -1;
        int perAxis = Math.Max(2, texelsPerAxis);
        if (!bounds.Usable || pyramid == null || pyramid.Levels <= 0) return false;
        if (screenWidth <= 0 || screenHeight <= 0) return false;

        int level = LevelFor(
            bounds.WidthPixels(screenWidth), bounds.HeightPixels(screenHeight),
            pyramid.Levels, perAxis);
        (int levelWidth, int levelHeight) = pyramid.Size(level);
        if (levelWidth <= 0 || levelHeight <= 0) return false;
        sampledLevel = level;

        // Anchored in screen PIXELS, then divided down by the level's own halving. A texel at
        // level L stands for exactly 2^L pixels, because that is how the pyramid was built -
        // it is not 1/count of the screen, and the two stop agreeing the moment a dimension
        // halves to something odd. At 1440 rows the chain reaches 45, so from level 6 up a
        // texel covers 64 rows while 1/count is only about 65.5, and scaling by the count
        // lands one texel SHORT of the texel the box's top edge really sits in. The texel
        // skipped that way is the one holding the sky beyond an occluder's silhouette, so a
        // box peeking over a ridge gets declared hidden - the one verdict never allowed here.
        //
        // Outward rounding on both edges is kept. A rectangle that covers even a sliver of a
        // texel must include that texel, or the box could be declared hidden on the strength
        // of pixels it does not actually sit behind.
        int pixelMinX = Math.Clamp((int)Math.Floor(bounds.MinU * screenWidth), 0, screenWidth - 1);
        int pixelMinY = Math.Clamp((int)Math.Floor(bounds.MinV * screenHeight), 0, screenHeight - 1);
        int pixelMaxX = Math.Clamp((int)Math.Ceiling(bounds.MaxU * screenWidth) - 1, 0, screenWidth - 1);
        int pixelMaxY = Math.Clamp((int)Math.Ceiling(bounds.MaxV * screenHeight) - 1, 0, screenHeight - 1);

        int x0 = pixelMinX >> level;
        int y0 = pixelMinY >> level;
        int x1 = pixelMaxX >> level;
        int y1 = pixelMaxY >> level;

        // Clamping the far edge to the last texel is what honours the odd-dimension fold:
        // the reduction folds the leftover row and column into that texel, so it is where
        // the remaining pixels genuinely live rather than an index being trimmed away.
        x0 = Math.Clamp(x0, 0, levelWidth - 1);
        y0 = Math.Clamp(y0, 0, levelHeight - 1);
        x1 = Math.Clamp(x1, x0, levelWidth - 1);
        y1 = Math.Clamp(y1, y0, levelHeight - 1);

        // A rectangle that somehow still spans more texels than the level choice promised
        // is a bug in that choice, not something to sample expensively around. Fail open.
        //
        // Strictly greater than, and that has to stay. A rectangle N texels WIDE can start
        // part way into a texel and so touch N+1 of them, which is a difference of N - the
        // level choice sizes the span, not the count. Making this an inequality on the count
        // would fail open on ordinary boxes rather than on broken ones.
        if ((x1 - x0) > perAxis || (y1 - y0) > perAxis) return false;

        float farthest = float.NegativeInfinity;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float sample = pyramid.Farthest(level, x, y);
                if (!float.IsFinite(sample)) return false;
                if (sample > farthest) farthest = sample;
            }
        }

        if (!float.IsFinite(farthest)) return false;

        // A narrow fail-open band around equality covers fixed-point depth quantization and
        // the different arithmetic used by box projection and triangle rasterization. The
        // old strict comparison flickered whole commands at precise camera angles; clusters
        // made it more obvious by creating up to sixteen independent verdicts per section.
        return bounds.NearestDepth > farthest + OcclusionDepthBias;
    }
}
