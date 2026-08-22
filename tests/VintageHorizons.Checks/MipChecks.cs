namespace VintageHorizons.Checks;

/// <summary>
/// Child to parent downsampling. Four child columns become one parent column via a
/// y-boundary slice sweep, with a slice surviving only when enough columns cover it.
///
/// The whole quadtree's appearance rests on this: every level above 0 is produced here
/// and nowhere else, so a fault shows up as distant terrain that is subtly wrong in a
/// way no one can trace back to a single block.
/// </summary>
public static class MipChecks
{
    const int Half = LodSection.GridSize / 2;

    public static void Run(Check c)
    {
        QuadrantPlacement(c);
        MajorityOccupancy(c);
        RunMerging(c);
        PaletteRemap(c);
        WorkerBoundary(c);
        RevisionValidation(c);
        NothingToDo(c);
    }

    /// <summary>A child section fills exactly one quadrant of its parent, and only that one.</summary>
    static void QuadrantPlacement(Check c)
    {
        for (int qz = 0; qz < 2; qz++)
        {
            for (int qx = 0; qx < 2; qx++)
            {
                LodSection child = Solid(0, 10, 0);
                var parent = new LodSection();

                c.True(LodMip.DownsampleIntoParent(child, parent, qx, qz),
                    $"quadrant ({qx},{qz}) reports the parent changed");

                // A full child section covers a half-by-half block of parent columns.
                c.Eq(Half * Half, parent.CapturedColumns, $"quadrant ({qx},{qz}) captures a quarter of the parent");

                int inside = LodSection.ColumnIndex(qx * Half, qz * Half);
                int alsoInside = LodSection.ColumnIndex(qx * Half + Half - 1, qz * Half + Half - 1);
                c.Eq(1, parent.ColumnRuns(inside).Length, $"quadrant ({qx},{qz}) fills its near corner");
                c.Eq(1, parent.ColumnRuns(alsoInside).Length, $"quadrant ({qx},{qz}) fills its far corner");

                // The other three quadrants must be untouched, or siblings would overwrite
                // each other and only the last one downsampled would survive.
                int outsideX = qx == 0 ? Half : 0;
                int outsideZ = qz == 0 ? Half : 0;
                c.False(parent.Captured[LodSection.ColumnIndex(outsideX, qz * Half)],
                    $"quadrant ({qx},{qz}) leaves the x-adjacent quadrant alone");
                c.False(parent.Captured[LodSection.ColumnIndex(qx * Half, outsideZ)],
                    $"quadrant ({qx},{qz}) leaves the z-adjacent quadrant alone");
            }
        }

        // All four siblings into one parent: together they cover it exactly once.
        var shared = new LodSection();
        for (int qz = 0; qz < 2; qz++)
        {
            for (int qx = 0; qx < 2; qx++) LodMip.DownsampleIntoParent(Solid(0, 10, 0), shared, qx, qz);
        }
        c.Eq(Fixtures.Total, shared.CapturedColumns, "four children cover the whole parent");
    }

    /// <summary>
    /// A slice survives when at least two of the four child columns cover it, so a lone
    /// spire does not become a full-width pillar at the coarser level. With only one
    /// column captured the threshold drops to one, or the frontier of explored terrain
    /// would silently lose its edge columns.
    /// </summary>
    static void MajorityOccupancy(Check c)
    {
        // One column reaches to 20, the other three stop at 10.
        var child = new LodSection();
        child.FindOrAddPaletteEntry(blockId: 1, color: 0x00445566, flags: 0);
        child.SetColumn(LodSection.ColumnIndex(0, 0), new[] { LodSection.PackRun(0, 20, 0) });
        child.SetColumn(LodSection.ColumnIndex(1, 0), new[] { LodSection.PackRun(0, 10, 0) });
        child.SetColumn(LodSection.ColumnIndex(0, 1), new[] { LodSection.PackRun(0, 10, 0) });
        child.SetColumn(LodSection.ColumnIndex(1, 1), new[] { LodSection.PackRun(0, 10, 0) });

        var parent = new LodSection();
        LodMip.DownsampleIntoParent(child, parent, 0, 0);

        ulong[] merged = parent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.Eq(1, merged.Length, "a minority spire does not survive as its own run");
        c.Eq(10, LodSection.RunYTop(merged[0]), "the parent takes the height the majority agreed on");
        c.Eq(0, LodSection.RunYBottom(merged[0]), "the merged run keeps the shared floor");

        // A single captured column has no majority to lose to.
        var lonely = new LodSection();
        lonely.FindOrAddPaletteEntry(blockId: 1, color: 0x00445566, flags: 0);
        lonely.SetColumn(LodSection.ColumnIndex(0, 0), new[] { LodSection.PackRun(0, 20, 0) });

        var lonelyParent = new LodSection();
        LodMip.DownsampleIntoParent(lonely, lonelyParent, 0, 0);

        ulong[] survived = lonelyParent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.Eq(1, survived.Length, "a lone captured column still produces a run");
        c.Eq(20, LodSection.RunYTop(survived[0]), "the lone column keeps its full height");
    }

    /// <summary>
    /// Slices are walked one boundary at a time, so a uniform column arrives as several
    /// abutting slices and must come back out as one run. Without the merge every parent
    /// column would carry a run per distinct y in its children - the run arrays would grow
    /// with depth instead of shrinking, which is the entire point of the pyramid.
    /// </summary>
    static void RunMerging(Check c)
    {
        var child = new LodSection();
        child.FindOrAddPaletteEntry(blockId: 1, color: 0x00112233, flags: 0);
        child.FindOrAddPaletteEntry(blockId: 2, color: 0x00445566, flags: 0);

        // Two stacked runs of the SAME block, split at y=10.
        ulong[] sameBlock = { LodSection.PackRun(0, 20, 10), LodSection.PackRun(0, 10, 0) };
        for (int dz = 0; dz < 2; dz++)
        {
            for (int dx = 0; dx < 2; dx++) child.SetColumn(LodSection.ColumnIndex(dx, dz), sameBlock);
        }

        var parent = new LodSection();
        LodMip.DownsampleIntoParent(child, parent, 0, 0);

        ulong[] merged = parent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.Eq(1, merged.Length, "abutting slices of one block fuse into a single run");
        c.Eq(20, LodSection.RunYTop(merged[0]), "the fused run keeps the top");
        c.Eq(0, LodSection.RunYBottom(merged[0]), "the fused run keeps the bottom");

        // Different blocks must NOT fuse, or stone would swallow the soil above it.
        var layered = new LodSection();
        layered.FindOrAddPaletteEntry(blockId: 1, color: 0x00112233, flags: 0);
        layered.FindOrAddPaletteEntry(blockId: 2, color: 0x00445566, flags: 0);
        ulong[] twoBlocks = { LodSection.PackRun(1, 20, 10), LodSection.PackRun(0, 10, 0) };
        for (int dz = 0; dz < 2; dz++)
        {
            for (int dx = 0; dx < 2; dx++) layered.SetColumn(LodSection.ColumnIndex(dx, dz), twoBlocks);
        }

        var layeredParent = new LodSection();
        LodMip.DownsampleIntoParent(layered, layeredParent, 0, 0);

        ulong[] kept = layeredParent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.Eq(2, kept.Length, "runs of different blocks stay separate");
        c.Eq(20, LodSection.RunYTop(kept[0]), "the upper layer keeps its top");
        c.Eq(10, LodSection.RunYBottom(kept[0]), "the layers meet where they should");
        c.Eq(0, LodSection.RunYBottom(kept[1]), "the lower layer keeps its floor");
    }

    /// <summary>
    /// Parent and child hold independent palettes, so ids must be translated rather than
    /// copied. A raw copy would silently reinterpret every run as whatever block happened
    /// to occupy that index in the parent.
    /// </summary>
    static void PaletteRemap(Check c)
    {
        var child = new LodSection();
        // Deliberately not index 0: an identity mapping would hide the bug.
        child.FindOrAddPaletteEntry(blockId: 100, color: 0x00AAAAAA, flags: 0);
        child.FindOrAddPaletteEntry(blockId: 200, color: 0x00BBCCDD,
            flags: LodPaletteEntry.FlagWater, tintSlot: 7);

        ulong[] runs = { LodSection.PackRun(1, 10, 0) };
        for (int dz = 0; dz < 2; dz++)
        {
            for (int dx = 0; dx < 2; dx++) child.SetColumn(LodSection.ColumnIndex(dx, dz), runs);
        }

        // Give the parent an unrelated entry first, so the child's id 1 cannot coincide.
        var parent = new LodSection();
        parent.FindOrAddPaletteEntry(blockId: 999, color: 0x00000001, flags: 0);

        LodMip.DownsampleIntoParent(child, parent, 0, 0);

        ulong[] merged = parent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.Eq(1, merged.Length, "the remapped column has one run");

        int pid = LodSection.RunPaletteId(merged[0]);
        c.True(pid < parent.Palette.Count, "the remapped id is inside the parent palette");
        c.Eq(200, parent.Palette[pid].BlockId, "the run still names the child's block");
        c.Eq(0x00BBCCDD, parent.Palette[pid].Color, "colour survives the remap");
        c.Eq(LodPaletteEntry.FlagWater, parent.Palette[pid].Flags, "flags survive the remap");
        c.Eq((byte)7, parent.Palette[pid].TintSlot, "tint slot survives the remap");
        c.Eq(999, parent.Palette[0].BlockId, "the parent's existing palette entry is undisturbed");
    }

    static void WorkerBoundary(Check c)
    {
        LodSection child = Solid(0, 12, 2);
        var parent = new LodSection();
        MipJob job = LodMip.CreateJob(epoch: 4, childKey: 99, childRevision: 7, child, qx: 1, qz: 0);
        MipResult result = LodMip.BuildResult(job);

        c.Eq(0, parent.CapturedColumns, "worker construction does not mutate the live parent");
        c.Eq(4L, result.Epoch, "worker result preserves its world epoch");
        c.Eq(99L, result.ChildKey, "worker result preserves its child identity");
        c.Eq(7L, result.ChildRevision, "worker result preserves its content revision");
        c.True(LodMip.ApplyToParent(result, parent) != LodSection.EdgeNone,
            "owning-thread publication changes the parent");
        c.Eq(Half * Half, parent.CapturedColumns, "published worker result fills only its quadrant");
    }

    static void RevisionValidation(Check c)
    {
        var world = new LodWorld();
        long childKey = LodWorld.SectionKey(0, 8, 10);
        LodSection child = world.GetOrCreateSection(childKey);
        child.FindOrAddPaletteEntry(blockId: 1, color: 0x00112233, flags: 0);
        int col = LodSection.ColumnIndex(0, 0);
        child.SetColumn(col, new[] { LodSection.PackRun(0, 10, 0) });
        world.MarkChanged(childKey);

        List<MipJob> first = world.CreatePropagationJobs(epoch: 12, maxSections: 1);
        c.Eq(1, first.Count, "a dirty child schedules one mip job");
        c.Eq(1, world.MipInFlightCount, "scheduled mip work is tracked in flight");

        // Change the child after the immutable snapshot was handed away. The old result
        // must release its slot without clearing the durable rerun obligation.
        child.SetColumn(col, new[] { LodSection.PackRun(0, 20, 0) });
        world.MarkChanged(childKey);
        c.False(world.CompletePropagation(LodMip.BuildResult(first[0])),
            "a stale result cannot commit over newer child content");
        c.True(world.MipDirty.Contains(childKey), "stale work leaves the child retryable");
        c.Eq(0, world.MipInFlightCount, "stale completion releases its in-flight slot");

        List<MipJob> retry = world.CreatePropagationJobs(epoch: 12, maxSections: 1);
        c.Eq(1, retry.Count, "the newest revision reschedules");
        c.True(world.CompletePropagation(LodMip.BuildResult(retry[0])),
            "the matching revision commits");
        c.False(world.MipDirty.Contains(childKey), "valid commit clears the child's propagation flag");

        long parentKey = LodWorld.ParentKey(childKey);
        c.True(world.Sections.TryGetValue(parentKey, out LodSection? parent), "valid commit creates the parent");
        ulong[] merged = parent!.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.Eq(20, LodSection.RunYTop(merged[0]), "the parent receives the newest child revision");
    }

    static void NothingToDo(Check c)
    {
        var parent = new LodSection();
        c.False(LodMip.DownsampleIntoParent(new LodSection(), parent, 0, 0),
            "an empty child leaves the parent unchanged");
        c.Eq(0, parent.CapturedColumns, "an empty child captures nothing");

        // Re-running an identical downsample must be a no-op, or every mip pass would
        // mark the parent dirty and re-queue a save and a re-mesh forever.
        LodSection child = Solid(0, 10, 0);
        var target = new LodSection();
        c.True(LodMip.DownsampleIntoParent(child, target, 0, 0), "the first downsample changes the parent");
        c.False(LodMip.DownsampleIntoParent(child, target, 0, 0), "an identical re-run changes nothing");
    }

    /// <summary>A fully captured child section, every column one run of palette id 0.</summary>
    static LodSection Solid(int paletteId, int yTop, int yBottom)
    {
        var s = new LodSection();
        s.FindOrAddPaletteEntry(blockId: paletteId + 1, color: 0x00708090, flags: 0);
        ulong[] run = { LodSection.PackRun(paletteId, yTop, yBottom) };
        for (int col = 0; col < Fixtures.Total; col++) s.SetColumn(col, run);
        return s;
    }
}
