namespace VintageHorizons.Checks;

/// <summary>
/// The machinery the offline field measurement adds on top of already-checked code: a near
/// plane clipper, a depth rasteriser, and the join between them and LodHzbProjection.
///
/// These exist because the measurement's output is a number nobody can eyeball. A pyramid
/// reduction that is wrong looks like terrain that hides slightly more or slightly less, and
/// the whole point of moving this offline was to stop shipping instruments whose faults are
/// only discoverable by a person running the game. The end-to-end pair at the bottom is the
/// important one: an occluder must hide a box behind it and must NOT hide a box that pokes
/// over it, and if that ever stops holding, every figure the tool prints is meaningless.
/// </summary>
public static class HzbFieldChecks
{
    const int Width = 320;
    const int Height = 180;
    const double Fov = 60;
    const double Far = 4000;

    public static void Run(Check c)
    {
        ClipsAwayGeometryBehindTheCamera(c);
        KeepsTheNearerSurface(c);
        ClippedTrianglesStayInRange(c);
        AnOccluderHidesWhatIsBehindIt(c);
        AnOccluderDoesNotHideWhatPokesOverIt(c);
    }

    // ---- clipper ----

    static void ClipsAwayGeometryBehindTheCamera(Check c)
    {
        // w is the fourth component; the clipper keeps only w >= 1e-4.
        Span<double> behind = stackalloc double[]
        {
            0, 0, 0, -5,
            1, 0, 0, -5,
            0, 1, 0, -5,
        };
        Span<double> result = stackalloc double[8 * 4];
        c.Eq(0, HzbField.ClipNear(behind, 3, result),
            "a triangle wholly behind the camera clips to nothing");

        Span<double> ahead = stackalloc double[]
        {
            0, 0, 0, 5,
            1, 0, 0, 5,
            0, 1, 0, 5,
        };
        c.Eq(3, HzbField.ClipNear(ahead, 3, result),
            "a triangle wholly in front of the camera passes through unchanged");

        // One corner behind: the triangle becomes a quad, because two edges are cut.
        Span<double> straddling = stackalloc double[]
        {
            0, 0, 0, 5,
            1, 0, 0, 5,
            0, 1, 0, -5,
        };
        c.Eq(4, HzbField.ClipNear(straddling, 3, result),
            "one corner behind the camera turns the triangle into a quad");
        for (int i = 0; i < 4; i++)
        {
            // A cut corner lands ON the plane by construction, so it is only ever a rounding
            // step away from it in either direction. What matters is that nothing survives at
            // a w the perspective divide cannot use, not that the arithmetic rounded upward.
            c.True(result[i * 4 + 3] >= 1e-4 * (1 - 1e-9),
                "every clipped corner ends up at or in front of the near plane");
        }
    }

    // ---- rasteriser ----

    static void KeepsTheNearerSurface(Check c)
    {
        var depth = new float[Width * Height];
        Array.Fill(depth, 1f);

        // Screen-space triangles covering the middle of the buffer, far one first.
        HzbField.RasterTriangle(depth, Width, Height, 40, 40, 0.9, 200, 40, 0.9, 40, 140, 0.9);
        c.Near(0.9, depth[80 * Width + 60], 1e-5, "the first surface lands in the depth buffer");
        c.Eq(1f, depth[5 * Width + 300], "a pixel the triangle does not cover is untouched");

        HzbField.RasterTriangle(depth, Width, Height, 40, 40, 0.4, 200, 40, 0.4, 40, 140, 0.4);
        c.Near(0.4, depth[80 * Width + 60], 1e-5, "a nearer surface replaces the one behind it");

        HzbField.RasterTriangle(depth, Width, Height, 40, 40, 0.8, 200, 40, 0.8, 40, 140, 0.8);
        c.Near(0.4, depth[80 * Width + 60], 1e-5, "a farther surface does not replace the nearer one");

        // Off-screen and degenerate triangles must be silent rather than throwing: the real
        // input is a whole cache of meshed terrain, and one bad triangle must not end a run.
        c.NoThrow(() => HzbField.RasterTriangle(depth, Width, Height,
            -500, -500, 0.5, -400, -500, 0.5, -500, -400, 0.5), "an off-screen triangle is skipped");
        c.NoThrow(() => HzbField.RasterTriangle(depth, Width, Height,
            10, 10, 0.5, 10, 10, 0.5, 10, 10, 0.5), "a degenerate triangle is skipped");
    }

    static void ClippedTrianglesStayInRange(Check c)
    {
        var depth = new float[Width * Height];
        Array.Fill(depth, 1f);

        // A ground plane the camera stands on, which is the case that forced a clipper: it
        // runs from behind the camera to well in front of it.
        float[] vp = HzbField.ViewProjection(0, Width, Height, Fov, Far);
        RasterQuad(depth, vp, -200, -2, -200, 200, -2, 400);

        // Counted rather than asserted per pixel: one assertion per texel would add six
        // figures to the suite total and say nothing more than "how many pixels are there".
        int written = 0, outOfRange = 0;
        foreach (float d in depth)
        {
            if (!float.IsFinite(d) || d < 0f || d > 1f) outOfRange++;
            if (d < 1f) written++;
        }

        c.Eq(0, outOfRange, "no rasterised depth is NaN, infinite or outside the depth range");
        c.True(written > 0, "a ground plane under the camera actually covers pixels");
    }

    // ---- the join, which is what the measurement rests on ----

    static void AnOccluderHidesWhatIsBehindIt(Check c)
    {
        var depth = new float[Width * Height];
        Array.Fill(depth, 1f);

        float[] vp = HzbField.ViewProjection(0, Width, Height, Fov, Far);

        // A wall 100 blocks ahead, tall and wide enough to bury what is behind it. At yaw 0
        // forward is +Z - the view matrix negates it into the camera's own -Z - so "ahead" is
        // positive here. Getting that backwards put every fixture behind the camera.
        RasterQuad(depth, vp, -400, -200, 100, 400, 200, 100);

        var pyramid = HzbField.ChainPyramid.Build(depth, Width, Height);

        // A box far behind the wall and well inside its silhouette.
        LodHzbScreenBounds bounds = LodHzbProjection.Project(
            vp, -32, -20, 540, 32, 20, 600);

        c.True(bounds.Usable, "the hidden box projects usably");
        c.True(LodHzbProjection.IsOccluded(bounds, pyramid, Width, Height,
            LodHzbProjection.DefaultTexelsPerAxis, out _),
            "a box behind a wall that covers the screen is hidden");
    }

    static void AnOccluderDoesNotHideWhatPokesOverIt(Check c)
    {
        var depth = new float[Width * Height];
        Array.Fill(depth, 1f);

        float[] vp = HzbField.ViewProjection(0, Width, Height, Fov, Far);

        // The same wall, but only up to y = 10.
        RasterQuad(depth, vp, -400, -200, 100, 400, 10, 100);

        var pyramid = HzbField.ChainPyramid.Build(depth, Width, Height);

        // A box behind it whose top rises well above the wall, so part of it is against sky.
        // This is the one-directional case: hiding it would delete terrain the player sees.
        LodHzbScreenBounds bounds = LodHzbProjection.Project(
            vp, -32, -20, 540, 32, 300, 600);

        c.True(bounds.Usable, "the poking box projects usably");
        c.False(LodHzbProjection.IsOccluded(bounds, pyramid, Width, Height,
            LodHzbProjection.DefaultTexelsPerAxis, out _),
            "a box rising above the wall is never hidden");

        // And it must stay unhidden under the wider sampling the measurement compares, or the
        // lever being measured is one that loses terrain.
        foreach (int texels in new[] { 4, 8, 16 })
        {
            c.False(LodHzbProjection.IsOccluded(bounds, pyramid, Width, Height, texels, out _),
                "wider sampling at " + texels + " texels still does not hide it");
        }
    }

    // ---- helpers ----

    /// <summary>An axis-aligned quad in camera-relative space, pushed through the real path.</summary>
    static void RasterQuad(float[] depth, float[] vp,
        double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
    {
        // Two triangles over the four corners, spanning X and Y at fixed Z when the box is
        // flat in Z, and X/Z when it is flat in Y.
        bool flatInZ = Math.Abs(maxZ - minZ) < 1e-9;

        (double X, double Y, double Z)[] corners = flatInZ
            ? new[]
            {
                (minX, minY, minZ), (maxX, minY, minZ), (maxX, maxY, minZ), (minX, maxY, minZ),
            }
            : new[]
            {
                (minX, minY, minZ), (maxX, minY, minZ), (maxX, maxY, maxZ), (minX, maxY, maxZ),
            };

        RasterWorldTriangle(depth, vp, corners[0], corners[1], corners[2]);
        RasterWorldTriangle(depth, vp, corners[0], corners[2], corners[3]);
    }

    static void RasterWorldTriangle(float[] depth, float[] vp,
        (double X, double Y, double Z) a, (double X, double Y, double Z) b,
        (double X, double Y, double Z) cc)
    {
        Span<double> tri = stackalloc double[3 * 4];
        (double X, double Y, double Z)[] corners = { a, b, cc };

        for (int i = 0; i < 3; i++)
        {
            (double x, double y, double z) = corners[i];
            tri[i * 4 + 0] = vp[0] * x + vp[4] * y + vp[8] * z + vp[12];
            tri[i * 4 + 1] = vp[1] * x + vp[5] * y + vp[9] * z + vp[13];
            tri[i * 4 + 2] = vp[2] * x + vp[6] * y + vp[10] * z + vp[14];
            tri[i * 4 + 3] = vp[3] * x + vp[7] * y + vp[11] * z + vp[15];
        }

        Span<double> clipped = stackalloc double[8 * 4];
        int count = HzbField.ClipNear(tri, 3, clipped);
        if (count < 3) return;

        for (int t = 1; t + 1 < count; t++)
        {
            Screen(clipped, 0, out double ax, out double ay, out double az);
            Screen(clipped, t, out double bx, out double by, out double bz);
            Screen(clipped, t + 1, out double cx, out double cy, out double cz);
            HzbField.RasterTriangle(depth, Width, Height, ax, ay, az, bx, by, bz, cx, cy, cz);
        }
    }

    static void Screen(Span<double> poly, int index, out double x, out double y, out double z)
    {
        double w = poly[index * 4 + 3];
        x = (poly[index * 4 + 0] / w * 0.5 + 0.5) * Width;
        y = (poly[index * 4 + 1] / w * 0.5 + 0.5) * Height;
        z = poly[index * 4 + 2] / w * 0.5 + 0.5;
    }
}
