namespace VintageHorizons;

/// <summary>
/// The pyramid's shape and its reduction rule, expressed on the CPU so both can be tested
/// without a GPU. `hzbreduce.fsh` implements exactly this; if the two ever disagree, the
/// checks over this reference are what say so, because a wrong pyramid does not look wrong
/// - it silently hides terrain, in views nobody thought to stand in.
/// </summary>
internal static class LodHzbReference
{
    /// <summary>
    /// Dimensions of one level. Each is the previous halved and floored, never below one,
    /// which is what `TexStorage2D` allocates for a mip chain and therefore what the
    /// framebuffer attachment will actually be.
    /// </summary>
    public static (int Width, int Height) LevelSize(int width, int height, int level)
    {
        int w = Math.Max(1, width >> level);
        int h = Math.Max(1, height >> level);
        return (w, h);
    }

    /// <summary>Levels from full size down to and including 1x1.</summary>
    public static int LevelCount(int width, int height)
    {
        if (width <= 0 || height <= 0) return 0;
        int largest = Math.Max(width, height);
        int levels = 1;
        while (largest > 1)
        {
            largest >>= 1;
            levels++;
        }
        return levels;
    }

    /// <summary>
    /// One reduction step: the farthest of the texels each target texel stands for.
    ///
    /// Farthest is the whole safety argument. A pyramid built with the average - which is
    /// what the driver's own mipmap generation would give - reports a depth nearer than
    /// some of the samples under it, and a test against that concludes "hidden" about
    /// terrain visible through a gap. Rounding to the far side can only fail to hide
    /// something that was hidden, which costs a draw call.
    ///
    /// The odd-dimension fold is the other half. Halving an odd width leaves a column with
    /// no home, and a dropped column is a depth the level never learned about - so the
    /// last target texel absorbs it rather than the pyramid quietly losing the edge of the
    /// screen.
    /// </summary>
    public static float[] Reduce(float[] source, int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return Array.Empty<float>();
        if (source.Length < sourceWidth * sourceHeight)
            throw new ArgumentException("source is smaller than its stated size", nameof(source));

        int targetWidth = Math.Max(1, sourceWidth >> 1);
        int targetHeight = Math.Max(1, sourceHeight >> 1);
        var target = new float[targetWidth * targetHeight];

        bool oddWidth = (sourceWidth & 1) != 0;
        bool oddHeight = (sourceHeight & 1) != 0;

        for (int y = 0; y < targetHeight; y++)
        {
            for (int x = 0; x < targetWidth; x++)
            {
                int sx = x * 2;
                int sy = y * 2;

                float farthest = At(source, sourceWidth, sourceHeight, sx, sy);
                farthest = Math.Max(farthest, At(source, sourceWidth, sourceHeight, sx + 1, sy));
                farthest = Math.Max(farthest, At(source, sourceWidth, sourceHeight, sx, sy + 1));
                farthest = Math.Max(farthest, At(source, sourceWidth, sourceHeight, sx + 1, sy + 1));

                bool foldX = oddWidth && x == targetWidth - 1;
                bool foldY = oddHeight && y == targetHeight - 1;

                if (foldX)
                {
                    farthest = Math.Max(farthest,
                        At(source, sourceWidth, sourceHeight, sourceWidth - 1, sy));
                    farthest = Math.Max(farthest,
                        At(source, sourceWidth, sourceHeight, sourceWidth - 1, sy + 1));
                }
                if (foldY)
                {
                    farthest = Math.Max(farthest,
                        At(source, sourceWidth, sourceHeight, sx, sourceHeight - 1));
                    farthest = Math.Max(farthest,
                        At(source, sourceWidth, sourceHeight, sx + 1, sourceHeight - 1));
                }
                if (foldX && foldY)
                {
                    farthest = Math.Max(farthest,
                        At(source, sourceWidth, sourceHeight, sourceWidth - 1, sourceHeight - 1));
                }

                target[y * targetWidth + x] = farthest;
            }
        }

        return target;
    }

    /// <summary>
    /// Clamped fetch, matching the shader's `clamp` rather than wrapping or returning a
    /// default. A sample off the edge repeats the edge texel, which is already accounted
    /// for in the reduction and can never introduce a depth nearer than a real one.
    /// </summary>
    static float At(float[] source, int width, int height, int x, int y)
    {
        int cx = Math.Clamp(x, 0, width - 1);
        int cy = Math.Clamp(y, 0, height - 1);
        return source[cy * width + cx];
    }
}
