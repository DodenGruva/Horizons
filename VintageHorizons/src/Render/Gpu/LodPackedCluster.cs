namespace VintageHorizons;

/// <summary>
/// One contiguous run in the Phase 8 packed-cluster stream. Bounds are section-local
/// blocks and enclose the geometry in the run exactly; the renderer adds the section's
/// camera-relative origin when it builds the cull command.
/// </summary>
public readonly record struct LodPackedCluster(
    int Cell,
    int FirstQuad,
    int QuadCount,
    float MinX,
    float MinY,
    float MinZ,
    float MaxX,
    float MaxY,
    float MaxZ)
{
    public bool HasGeometry => QuadCount > 0
        && MaxX >= MinX && MaxY >= MinY && MaxZ >= MinZ;
}

/// <summary>
/// Splits exact packed quads across a moderate 4x4 section grid, then publishes each
/// cell as one contiguous range. The established whole-section packed stream stays
/// unchanged beside it so Phase 8 can compare cluster granularity without changing the
/// accepted Phase 7 representation on the control side.
/// </summary>
internal sealed class LodPackedClusterBuilder
{
    public const int SubdivisionsPerAxis = 4;
    public const int CellCount = SubdivisionsPerAxis * SubdivisionsPerAxis;
    public const int ColumnsPerCell = LodSection.GridSize / SubdivisionsPerAxis;

    readonly List<uint>[] cells = new List<uint>[CellCount];
    readonly Bounds[] bounds = new Bounds[CellCount];

    struct Bounds
    {
        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public bool HasGeometry;

        public void Include(float minX, float minY, float minZ,
            float maxX, float maxY, float maxZ)
        {
            if (!HasGeometry)
            {
                MinX = minX; MinY = minY; MinZ = minZ;
                MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
                HasGeometry = true;
                return;
            }

            MinX = Math.Min(MinX, minX);
            MinY = Math.Min(MinY, minY);
            MinZ = Math.Min(MinZ, minZ);
            MaxX = Math.Max(MaxX, maxX);
            MaxY = Math.Max(MaxY, maxY);
            MaxZ = Math.Max(MaxZ, maxZ);
        }
    }

    public LodPackedClusterBuilder()
    {
        if (LodSection.GridSize % SubdivisionsPerAxis != 0)
            throw new InvalidOperationException("the packed cluster grid must tile a section exactly");
        for (int i = 0; i < cells.Length; i++) cells[i] = new List<uint>(192);
    }

    public void Clear()
    {
        for (int i = 0; i < cells.Length; i++) cells[i].Clear();
        Array.Clear(bounds);
    }

    public void Append(
        LodPackedFace face,
        int x0,
        int x1,
        float y0,
        float y1,
        int z0,
        int z1,
        int columnBlocks,
        int color,
        byte alpha)
    {
        if (columnBlocks <= 0) throw new ArgumentOutOfRangeException(nameof(columnBlocks));
        if (x0 < 0 || x1 < x0 || x1 > LodSection.GridSize
            || z0 < 0 || z1 < z0 || z1 > LodSection.GridSize)
            throw new ArgumentOutOfRangeException(nameof(x0), "clustered quad lies outside its section");

        (int firstX, int lastX) = CellRange(x0, x1, OwnsPositiveX(face));
        (int firstZ, int lastZ) = CellRange(z0, z1, OwnsPositiveZ(face));

        for (int cellZ = firstZ; cellZ <= lastZ; cellZ++)
        {
            int cellMinZ = cellZ * ColumnsPerCell;
            int cellMaxZ = cellMinZ + ColumnsPerCell;
            int splitZ0 = z0 == z1 ? z0 : Math.Max(z0, cellMinZ);
            int splitZ1 = z0 == z1 ? z1 : Math.Min(z1, cellMaxZ);

            for (int cellX = firstX; cellX <= lastX; cellX++)
            {
                int cellMinX = cellX * ColumnsPerCell;
                int cellMaxX = cellMinX + ColumnsPerCell;
                int splitX0 = x0 == x1 ? x0 : Math.Max(x0, cellMinX);
                int splitX1 = x0 == x1 ? x1 : Math.Min(x1, cellMaxX);
                if ((x0 != x1 && splitX1 <= splitX0)
                    || (z0 != z1 && splitZ1 <= splitZ0)) continue;

                int cell = cellZ * SubdivisionsPerAxis + cellX;
                LodPackedQuadFormat.Append(
                    cells[cell], face,
                    splitX0, splitX1, y0, y1, splitZ0, splitZ1,
                    color, alpha);
                bounds[cell].Include(
                    splitX0 * columnBlocks, y0, splitZ0 * columnBlocks,
                    splitX1 * columnBlocks, y1, splitZ1 * columnBlocks);
            }
        }
    }

    public void Finish(out uint[] words, out LodPackedCluster[] clusters)
    {
        int wordCount = 0;
        int populated = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            wordCount = checked(wordCount + cells[i].Count);
            if (cells[i].Count > 0) populated++;
        }

        words = new uint[wordCount];
        clusters = new LodPackedCluster[populated];
        int wordOffset = 0;
        int clusterIndex = 0;
        for (int cell = 0; cell < cells.Length; cell++)
        {
            List<uint> source = cells[cell];
            if (source.Count == 0) continue;
            source.CopyTo(words, wordOffset);
            Bounds b = bounds[cell];
            clusters[clusterIndex++] = new LodPackedCluster(
                cell,
                wordOffset / LodPackedQuadFormat.WordsPerQuad,
                source.Count / LodPackedQuadFormat.WordsPerQuad,
                b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ);
            wordOffset += source.Count;
        }
    }

    static (int first, int last) CellRange(int min, int max, bool ownsPositive)
    {
        if (min < max)
        {
            int first = Math.Clamp(min / ColumnsPerCell, 0, SubdivisionsPerAxis - 1);
            int last = Math.Clamp((max - 1) / ColumnsPerCell, 0, SubdivisionsPerAxis - 1);
            return (first, last);
        }

        int ownedColumn = ownsPositive ? min : min - 1;
        int cell = Math.Clamp(ownedColumn / ColumnsPerCell, 0, SubdivisionsPerAxis - 1);
        return (cell, cell);
    }

    static bool OwnsPositiveX(LodPackedFace face) => face == LodPackedFace.West;
    static bool OwnsPositiveZ(LodPackedFace face) => face == LodPackedFace.North;
}
