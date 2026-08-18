namespace VintageHorizons.Checks;

public static class DrainBudgetChecks
{
    public static void Run(Check c)
    {
        c.True(typeof(LodDrainBudget).IsValueType,
            "per-frame drain budgets do not allocate a heap object");

        long now = 100;
        var bytes = new LodDrainBudget(100, now, 10, () => now);
        c.True(bytes.TryStart(80), "the first ordinary item starts");
        c.False(bytes.TryStart(30), "an item that crosses the byte ceiling waits");
        c.True(bytes.TryStart(20), "an item that exactly fills the byte ceiling starts");
        c.Eq(2, bytes.Items, "only admitted items are counted");
        c.Eq(100L, bytes.Bytes, "admitted estimates accumulate exactly");

        now = 111;
        c.False(bytes.TryStart(0), "elapsed time stops later items even when they are empty");

        var oversized = new LodDrainBudget(100, 0, 10, () => 1000);
        c.True(oversized.TryStart(500), "an oversized oldest item still makes progress");
        c.False(oversized.TryStart(0), "nothing follows an oversized item in the same drain");

        var learnedLate = new LodDrainBudget(100, 0, 10, () => 0);
        c.True(learnedLate.TryStart(0), "work with an unknown size can start");
        learnedLate.AddBytes(150);
        c.Eq(150L, learnedLate.Bytes, "size learned during work is recorded");
        c.False(learnedLate.TryStart(0), "late-discovered oversize stops the next item");

        var negative = new LodDrainBudget(100, 0, 10, () => 0);
        c.True(negative.TryStart(-1), "a defensive negative estimate is treated as zero");
        negative.AddBytes(-1);
        c.Eq(0L, negative.Bytes, "a defensive negative measured size is ignored");

        var section = new LodSection
        {
            Runs = new ulong[2],
            PendingPaletteCodes = new string[] { "stone", null! },
        };
        section.Palette.Add(default);
        long expected = section.ColumnStart.LongLength * sizeof(int)
            + section.Runs.LongLength * sizeof(ulong)
            + section.Captured.LongLength
            + 12
            + (24 + "stone".Length * sizeof(char))
            + 24;
        c.Eq(expected, section.EstimatedContentBytes,
            "section byte estimates include arrays, palette entries, and pending codes");

        section.Runs = new ulong[3];
        section.FindOrAddPaletteEntry(blockId: 1, color: 2, flags: 3);
        long expectedSnapshot = section.Runs.LongLength * sizeof(ulong)
            + section.ColumnStart.LongLength * sizeof(int)
            + section.Captured.LongLength
            + section.Palette.Count * (sizeof(int) + 2L);
        c.Eq(expectedSnapshot, SectionSnapshot.EstimateRetainedBytes(section),
            "mesh snapshot estimates include shared and copied array payloads");
        c.Eq(expectedSnapshot, SectionSnapshot.Of(section).EstimatedRetainedBytes,
            "pre-snapshot and completed-snapshot estimates agree");

        var meshResult = new MeshResult
        {
            Xyz = Array.Empty<float>(),
            Rgba = Array.Empty<byte>(),
            Indices = Array.Empty<int>(),
            VertexCount = 10,
            IndexCount = 20,
            WaterVertexCount = 30,
            WaterIndexCount = 40,
        };
        c.Eq(880L, meshResult.EstimatedUploadBytes,
            "mesh upload estimates use live vertex and index counts for both passes");

        c.Eq(long.MaxValue, SectionSnapshot.SaturatingAdd(long.MaxValue - 1, 2),
            "mesh byte accounting saturates instead of wrapping");
        c.True(LodTerrainRenderer.MeshSnapshotMaxBytesPerFrame > 0
            && LodTerrainRenderer.MeshSnapshotMaxMillisecondsPerFrame > 0,
            "mesh snapshot production has byte and elapsed-time ceilings");
        c.True(LodTerrainRenderer.MeshUploadMaxBytesPerFrame > 0
            && LodTerrainRenderer.MeshUploadMaxMillisecondsPerFrame > 0,
            "mesh result upload has byte and elapsed-time ceilings");
    }
}
