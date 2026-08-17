namespace VintageHorizons.Checks;

public static class DrainBudgetChecks
{
    public static void Run(Check c)
    {
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
    }
}
