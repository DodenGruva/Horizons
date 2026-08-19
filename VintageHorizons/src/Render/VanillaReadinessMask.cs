namespace VintageHorizons;

/// <summary>
/// The CPU side of the per-cell ownership mask: one texel per 32x32x32 vanilla chunk, laid
/// out as Y slices stacked vertically in a single 2D texture. The public client API exposes
/// 2D texture binding and whole-texture upload only - there is no integer 3D texture and no
/// subregion update - so the atlas is the portable representation and every change costs a
/// full upload. That is cheap at ordinary view distances (32 KiB for a 256-block window)
/// and is why upload is coalesced to at most once per frame.
///
/// Addressing matches <see cref="VanillaRenderReadiness"/>'s tagged ring exactly, because
/// the shader reconstructs the same wrapped address from world coordinates. Any change to
/// one must change the other; <c>StaticAssetChecks</c> holds them together.
/// </summary>
internal sealed class VanillaReadinessMask
{
    /// <summary>Opaque white: vanilla owns this cell and cached fragments must not draw.</summary>
    public const int ReadyTexel = unchecked((int)0xFFFFFFFF);

    /// <summary>Zero: cache owns this cell. Every failure path leaves texels at this value.</summary>
    public const int CacheTexel = 0;

    readonly int capacity;
    readonly int capacityMask;
    readonly int verticalChunks;
    readonly int[] texels;

    public VanillaReadinessMask(int horizontalCapacity, int verticalChunkCount)
    {
        if (horizontalCapacity <= 0 || (horizontalCapacity & (horizontalCapacity - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(horizontalCapacity),
                "Mask capacity must be a positive power of two.");
        if (verticalChunkCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(verticalChunkCount));

        capacity = horizontalCapacity;
        capacityMask = horizontalCapacity - 1;
        verticalChunks = verticalChunkCount;
        texels = new int[checked(horizontalCapacity * horizontalCapacity * verticalChunkCount)];
        Dirty = true;
    }

    public int Width => capacity;
    public int Height => checked(capacity * verticalChunks);
    public int VerticalChunks => verticalChunks;
    public int[] Texels => texels;
    public long Bytes => texels.LongLength * sizeof(int);

    /// <summary>True when the buffer differs from what the GPU last accepted.</summary>
    public bool Dirty { get; private set; }

    public long Uploads { get; private set; }
    public long ReadyTexels { get; private set; }

    /// <summary>
    /// Wrapped atlas address for one ownership cell. The row is the ring's Z slot offset by
    /// whole capacity-sized blocks per Y level, so one 2D texture holds the full column
    /// stack without a 3D sampler.
    /// </summary>
    public static int TexelIndex(int chunkX, int chunkY, int chunkZ, int capacity, int verticalChunks)
    {
        int mask = capacity - 1;
        int row = (chunkZ & mask) + chunkY * capacity;
        return row * capacity + (chunkX & mask);
    }

    public bool Set(VanillaChunkCell cell, bool ready)
    {
        if (cell.Y < 0 || cell.Y >= verticalChunks) return false;

        int index = TexelIndex(cell.X, cell.Y, cell.Z, capacity, verticalChunks);
        int value = ready ? ReadyTexel : CacheTexel;
        if (texels[index] == value) return false;

        if (ready) ReadyTexels++;
        else ReadyTexels--;
        texels[index] = value;
        Dirty = true;
        return true;
    }

    public bool IsReady(VanillaChunkCell cell) =>
        cell.Y >= 0 && cell.Y < verticalChunks
        && texels[TexelIndex(cell.X, cell.Y, cell.Z, capacity, verticalChunks)] == ReadyTexel;

    /// <summary>
    /// Drops all ownership. Used for world teardown, window resize, and every failure path,
    /// because an emptied mask means cached terrain covers everything rather than nothing.
    /// </summary>
    public void Clear()
    {
        if (ReadyTexels == 0 && !Dirty) return;
        Array.Clear(texels);
        ReadyTexels = 0;
        Dirty = true;
    }

    /// <summary>
    /// Clears one wrapped column. A column leaving the active window must lose ownership
    /// before its ring slot is reused by different world coordinates, or the shader would
    /// read another place's readiness.
    /// </summary>
    public void ClearColumn(int chunkX, int chunkZ)
    {
        for (int y = 0; y < verticalChunks; y++)
        {
            int index = TexelIndex(chunkX, y, chunkZ, capacity, verticalChunks);
            if (texels[index] == CacheTexel) continue;
            texels[index] = CacheTexel;
            ReadyTexels--;
            Dirty = true;
        }
    }

    /// <summary>Records that the GPU accepted the current buffer.</summary>
    public void MarkUploaded()
    {
        Dirty = false;
        Uploads++;
    }

    public void ResetTelemetry() => Uploads = 0;
}
