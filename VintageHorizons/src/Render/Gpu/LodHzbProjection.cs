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
    /// The pyramid level whose texels are big enough that the rectangle spans at most two
    /// of them on each axis, so four samples always cover it.
    ///
    /// Rounding UP a level is safe and rounding down is not: a coarser level pools more
    /// pixels into one texel, and pooling can only push the farthest depth farther, which
    /// makes the box harder to declare hidden.
    /// </summary>
    public static int LevelFor(float widthPixels, float heightPixels, int levels)
    {
        if (levels <= 0) return 0;
        float longest = Math.Max(widthPixels, heightPixels);
        if (!float.IsFinite(longest) || longest <= 2f) return 0;

        int level = (int)Math.Ceiling(Math.Log2(longest / 2.0));
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
        out int sampledLevel)
    {
        sampledLevel = -1;
        if (!bounds.Usable || pyramid == null || pyramid.Levels <= 0) return false;
        if (screenWidth <= 0 || screenHeight <= 0) return false;

        int level = LevelFor(
            bounds.WidthPixels(screenWidth), bounds.HeightPixels(screenHeight), pyramid.Levels);
        (int levelWidth, int levelHeight) = pyramid.Size(level);
        if (levelWidth <= 0 || levelHeight <= 0) return false;
        sampledLevel = level;

        // Outward rounding on both edges. A rectangle that covers even a sliver of a texel
        // must include that texel, or the box could be declared hidden on the strength of
        // pixels it does not actually sit behind.
        int x0 = (int)Math.Floor(bounds.MinU * levelWidth);
        int y0 = (int)Math.Floor(bounds.MinV * levelHeight);
        int x1 = (int)Math.Ceiling(bounds.MaxU * levelWidth) - 1;
        int y1 = (int)Math.Ceiling(bounds.MaxV * levelHeight) - 1;

        x0 = Math.Clamp(x0, 0, levelWidth - 1);
        y0 = Math.Clamp(y0, 0, levelHeight - 1);
        x1 = Math.Clamp(x1, x0, levelWidth - 1);
        y1 = Math.Clamp(y1, y0, levelHeight - 1);

        // A rectangle that somehow still spans more texels than the level choice promised
        // is a bug in that choice, not something to sample expensively around. Fail open.
        if ((x1 - x0) > 2 || (y1 - y0) > 2) return false;

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

        // Strictly greater. Equality means the box's nearest point is exactly on the
        // surface already drawn, which is the coplanar case, and drawing it is correct.
        return bounds.NearestDepth > farthest;
    }
}
