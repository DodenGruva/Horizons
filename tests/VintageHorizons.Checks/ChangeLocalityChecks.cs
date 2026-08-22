namespace VintageHorizons.Checks;

/// <summary>
/// How far one content change spreads.
///
/// A neighbouring section's mesh hides its faces against our edge columns, so a change on
/// a shared edge really does invalidate that neighbour. A change in the interior does not,
/// and MarkChanged used to claim all four regardless - "conservatively refresh all four
/// (change locality tracking can come later)". On a stationary six-minute run that turned
/// 64 genuinely-changed sections into 5,057 mesh rebuilds, because the same fan-out
/// repeats at every level of the mip pyramid.
///
/// These checks pin the fan-out as a number rather than an estimate, including the part
/// that is NOT saved: a capture patch is exactly one quadrant of a section, so a
/// first-time capture always lands on two of the four edges and can never fall to one
/// section. The hoped-for "one instead of five" is only reachable for a re-capture whose
/// differing columns are interior.
/// </summary>
public static class ChangeLocalityChecks
{
    public static void Run(Check c)
    {
        InteriorChangeStopsAtTheSection(c);
        EdgeChangeReachesOnlyThatNeighbour(c);
        ChunkAlignedCaptureAlwaysLandsOnTwoEdges(c);
        UnknownEdgesStayConservative(c);
    }

    /// <summary>A world with the section and all four of its neighbours resident.</summary>
    static LodWorld WorldWithNeighbours(long key)
    {
        var world = new LodWorld();
        world.GetOrCreateSection(key);
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                world.GetOrCreateSection(LodWorld.NeighborKey(key, dx, dz));
        world.RenderDirty.Clear();
        return world;
    }

    static void InteriorChangeStopsAtTheSection(Check c)
    {
        long key = LodWorld.SectionKey(0, 10, 10);
        LodWorld world = WorldWithNeighbours(key);

        world.MarkChanged(key, LodSection.EdgeNone);

        c.Eq(1, world.RenderDirty.Count, "an interior change rebuilds one section, not five");
        c.True(world.RenderDirty.Contains(key), "and it is the section that actually changed");
        c.Eq(4L, world.MarkChangedNeighborsSkipped, "all four neighbour rebuilds were skipped");
        c.Eq(1L, world.MarkChangedRenderDirtied, "one mesh was made stale");
    }

    static void EdgeChangeReachesOnlyThatNeighbour(Check c)
    {
        long key = LodWorld.SectionKey(0, 10, 10);
        LodWorld world = WorldWithNeighbours(key);

        world.MarkChanged(key, LodSection.EdgePlusX);

        c.Eq(2, world.RenderDirty.Count, "a one-edge change rebuilds the section and one neighbour");
        c.True(world.RenderDirty.Contains(LodWorld.NeighborKey(key, 1, 0)),
            "the neighbour across the changed edge is rebuilt");
        c.False(world.RenderDirty.Contains(LodWorld.NeighborKey(key, -1, 0)),
            "the neighbour on the opposite side is not");
        c.False(world.RenderDirty.Contains(LodWorld.NeighborKey(key, 0, 1)),
            "nor is either neighbour along the other axis");
        c.Eq(3L, world.MarkChangedNeighborsSkipped, "three of the four were skipped");
    }

    /// <summary>
    /// The limit of this optimisation, stated so nobody re-derives the wrong expectation.
    /// A vanilla chunk is 32 blocks and a level-0 section is 64, so a capture patch fills
    /// exactly one 32x32 quadrant - which always includes one whole column at cx 0 or 63
    /// and one at cz 0 or 63. A first-time capture, where every column in the patch is new,
    /// therefore always reports two edges. Fan-out falls from five sections to three, not
    /// to one.
    /// </summary>
    static void ChunkAlignedCaptureAlwaysLandsOnTwoEdges(Check c)
    {
        int half = LodSection.GridSize / 2;
        ulong[] runs = { LodSection.PackRun(0, 10, 0) };

        var expected = new[]
        {
            (0, 0, LodSection.EdgeMinusX | LodSection.EdgeMinusZ),
            (1, 0, LodSection.EdgePlusX | LodSection.EdgeMinusZ),
            (0, 1, LodSection.EdgeMinusX | LodSection.EdgePlusZ),
            (1, 1, LodSection.EdgePlusX | LodSection.EdgePlusZ),
        };

        foreach ((int qx, int qz, int edges) in expected)
        {
            var section = new LodSection();
            var batch = new ulong[]?[LodSection.GridSize * LodSection.GridSize];
            for (int cz = 0; cz < half; cz++)
                for (int cx = 0; cx < half; cx++)
                    batch[LodSection.ColumnIndex(qx * half + cx, qz * half + cz)] = (ulong[])runs.Clone();

            c.Eq(edges, section.ReplaceColumns(batch),
                $"a first-time capture into quadrant ({qx},{qz}) reports its two outer edges");
        }

        // The case that does fall to one: a later capture of the same chunk where only
        // interior columns differ. This is what a settled world actually produces.
        var settled = new LodSection();
        var full = new ulong[]?[LodSection.GridSize * LodSection.GridSize];
        for (int cz = 0; cz < half; cz++)
            for (int cx = 0; cx < half; cx++)
                full[LodSection.ColumnIndex(cx, cz)] = (ulong[])runs.Clone();
        settled.ReplaceColumns(full);

        var recapture = new ulong[]?[LodSection.GridSize * LodSection.GridSize];
        for (int cz = 0; cz < half; cz++)
            for (int cx = 0; cx < half; cx++)
                recapture[LodSection.ColumnIndex(cx, cz)] = (ulong[])runs.Clone();
        recapture[LodSection.ColumnIndex(5, 5)] = new[] { LodSection.PackRun(0, 20, 0) };

        c.Eq(LodSection.EdgeNone, settled.ReplaceColumns(recapture),
            "a re-capture differing only in an interior column reports no edges");
    }

    /// <summary>
    /// A whole-section install or a palette repair has no column-level answer to give, so
    /// it says nothing and keeps the old behaviour. Silence must stay conservative: the
    /// alternative is a missing rebuild, which is a visible seam.
    /// </summary>
    static void UnknownEdgesStayConservative(Check c)
    {
        long key = LodWorld.SectionKey(0, 10, 10);
        LodWorld world = WorldWithNeighbours(key);

        world.MarkChanged(key);

        c.Eq(5, world.RenderDirty.Count, "a caller that does not know still refreshes all four");
        c.Eq(0L, world.MarkChangedNeighborsSkipped, "and skips nothing");
        c.Eq(1L, world.MarkChangedCalls, "the change itself is counted once");
    }
}
