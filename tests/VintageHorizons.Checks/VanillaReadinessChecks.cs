using System.Reflection;
using Vintagestory.API.Client;

namespace VintageHorizons.Checks;

public static class VanillaReadinessChecks
{
    public static void Run(Check c)
    {
        CoordinateMapping(c);
        WindowCalculation(c);
        CandidateWindowAndRing(c);
        MovingWindowQueuesFrontier(c);
        CommittedOwnershipIsRevalidatedWholesale(c);
        SectionCountsSurviveEveryWindowOperation(c);
        StabilizationAndPublication(c);
        DeferredObservationQueue(c);
        AggregateClassification(c);
        VerticalColumnReadiness(c);
        NearestIncompleteColumn(c);
        OwnershipMask(c);
        ShaderAddressAgreement(c);
        BoundaryRevalidation(c);
        WindowInvalidationAndTeardown(c);
        SteadyStateAllocation(c);
        ChunkGeometryBinding(c);
        MaskForgetsEvictedColumns(c);
        MaskExcludedCellsOwnGroundWithoutATexel(c);
        OwnershipStopsAtTheEnginesDrawRange(c);
    }

    static void CoordinateMapping(Check c)
    {
        c.Eq(0, VanillaRenderReadiness.ChunkCoordinate(0.0), "world zero maps to chunk zero");
        c.Eq(0, VanillaRenderReadiness.ChunkCoordinate(31.999), "the upper side of a chunk stays half-open");
        c.Eq(1, VanillaRenderReadiness.ChunkCoordinate(32.0), "an exact 32-block boundary enters the next chunk");
        c.Eq(1, VanillaRenderReadiness.ChunkCoordinate(63.999), "the second chunk keeps its upper interior");
        c.Eq(2, VanillaRenderReadiness.ChunkCoordinate(64.0), "an exact 64-block boundary enters chunk two");
        c.Eq(-1, VanillaRenderReadiness.ChunkCoordinate(-0.001), "coordinate conversion uses floor rather than truncation");

        const int largeOrigin = 1_000_000;
        c.Eq(largeOrigin, VanillaRenderReadiness.ChunkCoordinate(largeOrigin, 31.999f),
            "section-local addressing is stable at a large chunk origin");
        c.Eq(largeOrigin + 1, VanillaRenderReadiness.ChunkCoordinate(largeOrigin, 32f),
            "section-local addressing crosses an exact boundary at a large origin");
    }

    static void CandidateWindowAndRing(Check c)
    {
        var model = new VanillaRenderReadiness(4, 2);
        model.SetWindow(8, 12, 4, 4);
        DrainQueue(model);
        var first = new VanillaChunkCell(8, 0, 12);
        c.True(model.EnqueueCandidate(first), "an active cell enters the bounded candidate queue");
        c.Eq(VanillaReadinessState.Pending, model.State(first), "an event-fed candidate remains cache-owned while pending");
        c.False(model.EnqueueCandidate(first, engineEvent: true), "a duplicate candidate is coalesced");
        c.Eq(1L, model.CandidateEventsCoalesced, "candidate coalescing is counted");
        c.False(model.EnqueueCandidate(new VanillaChunkCell(7, 0, 12), engineEvent: true),
            "a candidate outside the active window fails toward cached coverage");
        c.Eq(1L, model.CandidateEventsDropped, "out-of-window candidates are counted as dropped");
        c.True(model.TryDequeueCandidate(out VanillaChunkCell dequeued), "the accepted candidate can be drained");
        c.Eq(first, dequeued, "candidate coordinates survive the fixed ring queue");

        int cursor = 0;
        c.Eq(3, model.SeedUnknownCells(ref cursor, 3), "initial discovery obeys its item budget");
        c.Eq(3, model.PendingCandidates, "budgeted discovery leaves exactly its admitted work queued");

        model.SetWindow(12, 12, 4, 4); // Same physical X slots, different world tags.

        // The move queues the columns it gained. What must never come back out is a cell
        // from the window that was left behind: those slots are now aliased to different
        // world coordinates, and publishing one would attribute ownership to the wrong place.
        int drained = 0;
        while (model.TryDequeueCandidate(out VanillaChunkCell swept))
        {
            drained++;
            c.True(swept.X >= 12 && swept.X < 16 && swept.Z >= 12 && swept.Z < 16,
                "ring-alias candidates from the old window are rejected as stale");
        }
        c.True(drained > 0, "the columns the window gained are queued for probing");

        var aliased = new VanillaChunkCell(12, 0, 12);
        c.True(model.EnqueueCandidate(aliased), "the aliased slot accepts its new world coordinate");
        c.True(model.TryDequeueCandidate(out dequeued), "the new ring generation drains normally");
        c.Eq(aliased, dequeued, "ring wrapping cannot publish the old coordinate as the new cell");
    }

    /// <summary>
    /// While the camera moves, the columns the window just gained are the ground the player
    /// is heading into, and they are the ones showing cached terrain over real terrain
    /// until they are probed. They must be queued when the window moves. A discovery cursor
    /// alone cannot do this: at flight speed the window moves again long before a sweep
    /// reaches the far edge, so the frontier would be the last thing ever probed.
    /// </summary>
    static void MovingWindowQueuesFrontier(Check c)
    {
        var model = new VanillaRenderReadiness(8, 2);
        model.SetWindow(0, 0, 4, 4);
        DrainQueue(model);

        // One step east: the window gains exactly the column strip at x = 4.
        model.SetWindow(1, 0, 4, 4);
        var gained = new List<VanillaChunkCell>();
        while (model.TryDequeueCandidate(out VanillaChunkCell cell)) gained.Add(cell);

        c.Eq(4 * 2, gained.Count, "every cell of the gained column strip is queued");
        c.True(gained.TrueForAll(cell => cell.X == 4),
            "only the newly covered column is queued, not the whole window again");
        c.True(gained.Exists(cell => cell.Y == 0) && gained.Exists(cell => cell.Y == 1),
            "the strip is queued through the full world height, not one surface layer");

        // Standing still queues nothing: an unchanged window is not new ground.
        c.False(model.SetWindow(1, 0, 4, 4), "an unchanged window reports no movement");
        c.Eq(0, model.PendingCandidates, "a stationary window queues no repeat work");
    }

    /// <summary>
    /// Ownership that is never revisited is a hole that never closes. Three cursor-based
    /// sweeps each failed to reach ground the camera had moved away from, and each failure
    /// was reported from play as terrain that simply stayed missing. Re-confirming the whole
    /// committed set bounds that by construction, so this asserts the set is complete: every
    /// committed cell, wherever it sits in the ring, and nothing that is not committed.
    /// </summary>
    static void CommittedOwnershipIsRevalidatedWholesale(Check c)
    {
        var model = new VanillaRenderReadiness(8, 2);
        model.SetWindow(0, 0, 8, 8);
        DrainQueue(model);

        var owned = new[]
        {
            new VanillaChunkCell(0, 0, 0),
            new VanillaChunkCell(7, 1, 7),
            new VanillaChunkCell(3, 0, 5),
        };
        int frame = 1;
        foreach (VanillaChunkCell cell in owned) { CommitReady(model, cell, frame); frame += 2; }
        DrainQueue(model);

        c.Eq(owned.Length, model.QueueAllReadyCells(frame),
            "every committed cell is queued for re-confirmation");

        var queued = new List<VanillaChunkCell>();
        while (model.TryDequeueCandidate(out VanillaChunkCell cell)) queued.Add(cell);
        foreach (VanillaChunkCell cell in owned)
            c.True(queued.Contains(cell), $"committed cell {cell.X},{cell.Y},{cell.Z} is revisited");
        c.Eq(owned.Length, queued.Count, "cells that own nothing are not re-probed");

        // A cell that has lost ownership drops out of the set, so the pass cannot grow
        // without bound as terrain streams.
        model.EnqueueCandidate(owned[0], frame);
        model.TryDequeueCandidate(out _);
        c.True(model.Observe(owned[0], rendered: false, frame, out VanillaReadinessPublication drop),
            "a committed cell that stops rendering proposes its loss");
        model.ResolvePublication(drop, accepted: true);
        DrainQueue(model);
        c.Eq(owned.Length - 1, model.QueueAllReadyCells(frame + 1),
            "a cell that lost ownership is no longer re-confirmed");
    }

    /// <summary>
    /// The per-section ready counts are what the whole-mesh skip trusts, and nothing
    /// re-derives them: a count that drifts high hides its section permanently, which is
    /// what a hole that survives standing still looks like. Cell states are re-probed every
    /// second and repair themselves; these counts cannot. So every operation that can touch
    /// them is run here and the counts are audited against the cell states afterwards.
    /// </summary>
    static void SectionCountsSurviveEveryWindowOperation(Check c)
    {
        var model = new VanillaRenderReadiness(8, 2);
        model.SetWindow(0, 0, 8, 8);
        DrainQueue(model);
        c.Eq(0, model.AuditReadyCounts(), "an empty model has consistent section counts");

        int frame = 1;
        foreach (var cell in new[]
        {
            new VanillaChunkCell(0, 0, 0), new VanillaChunkCell(1, 1, 1),
            new VanillaChunkCell(6, 0, 6), new VanillaChunkCell(7, 1, 7),
        })
        {
            CommitReady(model, cell, frame);
            frame += 2;
        }
        DrainQueue(model);
        c.Eq(0, model.AuditReadyCounts(), "committing ownership keeps the counts consistent");

        var lost = new VanillaChunkCell(1, 1, 1);
        model.EnqueueCandidate(lost, frame);
        model.TryDequeueCandidate(out _);
        model.Observe(lost, rendered: false, frame, out VanillaReadinessPublication drop);
        model.ResolvePublication(drop, accepted: true);
        c.Eq(0, model.AuditReadyCounts(), "losing ownership keeps the counts consistent");

        // Sliding the window drops columns; the counts must lose exactly their cells.
        model.SetWindow(2, 0, 8, 8);
        DrainQueue(model);
        c.Eq(0, model.AuditReadyCounts(), "sliding the window keeps the counts consistent");

        // Far enough that ring slots alias to different world coordinates, which is where a
        // stale count would hide.
        model.SetWindow(64, 64, 8, 8);
        DrainQueue(model);
        c.Eq(0, model.AuditReadyCounts(), "ring aliasing keeps the counts consistent");

        CommitReady(model, new VanillaChunkCell(64, 0, 64), frame + 40);
        DrainQueue(model);
        c.Eq(0, model.AuditReadyCounts(), "committing in an aliased slot keeps the counts consistent");

        model.SetWindow(0, 0, 8, 8);
        DrainQueue(model);
        c.Eq(0, model.AuditReadyCounts(), "returning to the original window keeps the counts consistent");
        c.Eq(VanillaSectionOwnership.CacheOnly, model.Classify(LodWorld.SectionKey(0, 32, 32)),
            "ownership committed in a window that has been left behind does not survive");

        model.Clear();
        c.Eq(0, model.AuditReadyCounts(), "clearing keeps the counts consistent");
    }

    static void WindowCalculation(Check c)
    {
        VanillaReadinessWindow centered = VanillaRenderReadiness.CalculateWindow(
            cameraX: 512, cameraZ: 512, approvedViewDistance: 256, mapSizeX: 2048, mapSizeZ: 2048);
        c.Eq(16, centered.CenterX, "camera block coordinates map to the expected window chunk");
        c.Eq(8, centered.VanillaRadius, "approved block distance rounds outward to vanilla chunks");
        c.Eq(10, centered.OuterRadius, "the active window includes the conservative guard band");
        c.Eq(21, centered.Width, "a centred window covers both radial sides and its centre");
        c.Eq(32, centered.RequiredCapacity, "ring capacity rounds up to a power of two");

        VanillaReadinessWindow corner = VanillaRenderReadiness.CalculateWindow(
            cameraX: 1, cameraZ: 1, approvedViewDistance: 256, mapSizeX: 320, mapSizeZ: 320);
        c.Eq(0, corner.MinX, "world-edge windows clamp rather than producing negative chunk coordinates");
        c.Eq(10, corner.Width, "world-edge windows clamp to the finite map width");
        c.Eq(16, corner.RequiredCapacity, "clamped dimensions still receive power-of-two storage");
    }

    static void StabilizationAndPublication(Check c)
    {
        var model = new VanillaRenderReadiness(4, 2);
        model.SetWindow(0, 0, 4, 4);
        DrainQueue(model);
        DrainQueue(model);
        var cell = new VanillaChunkCell(0, 0, 0);
        model.EnqueueCandidate(cell);
        model.TryDequeueCandidate(out _);

        c.False(model.Observe(cell, rendered: true, 10, out _),
            "the first true observation keeps cached coverage");
        c.Eq(VanillaReadinessState.ObservedRendered, model.State(cell),
            "the first true observation records stabilization state");
        c.False(model.Observe(cell, rendered: true, 10, out _),
            "a second observation in the same render frame cannot transfer ownership");
        c.True(model.Observe(cell, rendered: true, 11, out VanillaReadinessPublication ready),
            "a later render frame proposes vanilla ownership");
        c.True(ready.Ready, "the stabilized proposal sets the readiness bit");
        c.Eq(0, model.CommittedReadyCells, "CPU ownership cannot advance before GPU publication acceptance");

        c.False(model.ResolvePublication(ready, accepted: false), "a failed texture publication is rejected");
        c.Eq(0, model.CommittedReadyCells, "failed publication leaves cache ownership intact");
        c.True(model.Observe(cell, rendered: true, 12, out ready), "a rejected ready publication remains retryable");
        c.True(model.ResolvePublication(ready, accepted: true), "an accepted texture update commits ownership");
        c.Eq(VanillaReadinessState.VanillaReady, model.State(cell), "accepted publication commits the ready state");
        c.Eq(1, model.CommittedReadyCells, "accepted publication advances the CPU aggregate once");

        c.True(model.Observe(cell, rendered: false, 12, out VanillaReadinessPublication lost),
            "the first false observation proposes immediate cached restoration");
        c.False(lost.Ready, "loss publication clears rather than sets the GPU bit");
        c.Eq(1, model.CommittedReadyCells, "CPU clearing also waits for matching GPU publication");
        c.True(model.ResolvePublication(lost, accepted: true), "an accepted clear commits cached ownership");
        c.Eq(0, model.CommittedReadyCells, "the committed clear removes the ready aggregate");
        c.Eq(VanillaReadinessState.Pending, model.State(cell), "a lost cell must stabilize again before readiness");
    }

    static void AggregateClassification(Check c)
    {
        var model = new VanillaRenderReadiness(4, 2);
        model.SetWindow(0, 0, 4, 4);
        DrainQueue(model);
        DrainQueue(model);
        long l0 = LodWorld.SectionKey(0, 0, 0);
        long l1 = LodWorld.SectionKey(1, 0, 0);
        c.Eq(VanillaSectionOwnership.CacheOnly, model.Classify(l0), "a zero-count section keeps the unmasked cache path");

        CommitReady(model, new VanillaChunkCell(0, 0, 0), 1);
        c.Eq(1, model.ReadyCount(l0), "a ready cell increments its L0 ancestor");
        c.Eq(1, model.ReadyCount(l1), "the same transition increments its coarser ancestor");
        c.Eq(VanillaSectionOwnership.Mixed, model.Classify(l0), "a partial L0 section selects the mixed mask path");
        c.Eq(VanillaSectionOwnership.Mixed, model.Classify(l1), "a partial coarse section also selects the mask path");

        int frame = 2;
        for (int z = 0; z < 2; z++)
        for (int x = 0; x < 2; x++)
        for (int y = 0; y < 2; y++)
        {
            if (x == 0 && y == 0 && z == 0) continue;
            CommitReady(model, new VanillaChunkCell(x, y, z), frame++);
        }
        c.Eq(8, model.ReadyCount(l0), "all 2x2xY cells contribute to the aligned L0 total");
        c.Eq(VanillaSectionOwnership.VanillaOnly, model.Classify(l0),
            "a complete section can skip both cached draw submissions");
        c.Eq(VanillaSectionOwnership.Mixed, model.Classify(l1),
            "a complete child remains only partial coverage of its L1 parent");

        for (int level = 0; level <= LodWorld.MaxLevel; level++)
        {
            long ancestor = LodWorld.SectionKey(level, 0, 0);
            c.True(model.ReadyCount(ancestor) > 0, $"one point transition updates the fixed L{level} ancestor");
        }
    }

    /// <summary>
    /// Whole-section vanilla ownership needs every vertical chunk of a column, so this
    /// distribution is what decides whether the planned CPU whole-mesh skip is reachable
    /// against the real engine. The counters must separate a column nobody has probed
    /// from a column that is genuinely only partly ready.
    /// </summary>
    static void VerticalColumnReadiness(Check c)
    {
        var model = new VanillaRenderReadiness(4, 3);
        model.SetWindow(0, 0, 4, 4);
        DrainQueue(model);
        int[] readyByY = new int[3];

        // Entering the window queues every column, so all of them are tracked and pending
        // before anything is probed. Ownership, not tracking, is what the counts are for.
        model.GetColumnReadiness(readyByY, out int tracked, out int full, out int partial, out int deepest);
        c.Eq(16, tracked, "the columns the window covers are tracked once it is set");
        c.Eq(0, full, "an unprobed window reports no complete columns");
        c.Eq(0, partial, "an unprobed window reports no partially ready columns");
        c.Eq(0, deepest, "an unprobed window has no ready cell in any column");

        var fresh = new VanillaRenderReadiness(4, 3);
        fresh.GetColumnReadiness(new int[3], out int freshTracked, out _, out _, out _);
        c.Eq(0, freshTracked, "a model with no window tracks nothing at all");

        CommitReady(model, new VanillaChunkCell(1, 0, 1), 1);
        CommitReady(model, new VanillaChunkCell(1, 1, 1), 3);
        model.GetColumnReadiness(readyByY, out tracked, out full, out partial, out deepest);
        c.Eq(16, tracked, "each column is counted once regardless of its cell count");
        c.Eq(1, partial, "a column missing one vertical chunk counts as partial, not complete");
        c.Eq(0, full, "a column missing its top chunk cannot report complete ownership");
        c.Eq(2, deepest, "the deepest column reports how many vertical chunks reached ready");
        c.Eq(1, readyByY[0], "the histogram attributes a ready cell to its own Y band");
        c.Eq(1, readyByY[1], "the second Y band is counted separately");
        c.Eq(0, readyByY[2], "a Y band that never reports rendered stays visibly empty");

        CommitReady(model, new VanillaChunkCell(1, 2, 1), 5);
        model.GetColumnReadiness(readyByY, out tracked, out full, out partial, out deepest);
        c.Eq(1, full, "a column whose every vertical chunk is ready reports complete");
        c.Eq(0, partial, "completing the last chunk moves the column out of the partial count");
        c.Eq(3, deepest, "a complete column reports the full vertical extent");

        c.Throws<ArgumentException>(() => model.GetColumnReadiness(new int[2], out _, out _, out _, out _),
            "a histogram shorter than the world is refused rather than silently truncated");
    }

    /// <summary>
    /// A single radial handoff can only suppress cached terrain inside the nearest column
    /// that is not completely owned. These bounds decide whether one global radius is worth
    /// anything, so they must never read further than the truth: an untracked window edge,
    /// a partially ready column, and an unknown column all have to pull the radius in.
    /// </summary>
    static void NearestIncompleteColumn(Check c)
    {
        var model = new VanillaRenderReadiness(8, 2);
        model.SetWindow(0, 0, 8, 8);
        DrainQueue(model);
        double centreX = 4 * 32 + 16;
        double centreZ = 4 * 32 + 16;

        // Nothing is ready, so the camera's own column bounds the radius at zero.
        c.Eq(0d, model.NearestIncompleteColumnBlocks(centreX, centreZ, out double unready),
            "an unowned camera column allows no suppression at all");
        c.Eq(0d, unready, "the same column is also the nearest wholly unready one");

        for (int z = 0; z < 8; z++)
        for (int x = 0; x < 8; x++)
        for (int y = 0; y < 2; y++)
            CommitReady(model, new VanillaChunkCell(x, y, z), 1 + (x + z * 8) * 2 + y);

        // Every tracked column is complete, so only the window edge bounds the radius.
        // The window spans blocks [0, 256); its far edge at 112 blocks is nearer than its
        // near edge at 144, and the bound must take the nearest of the four.
        double edge = model.NearestIncompleteColumnBlocks(centreX, centreZ, out unready);
        c.Near(112d, edge, 0.001, "a fully owned window is bounded by its nearest edge");

        // One column losing a single vertical chunk pulls the bound back to that column.
        var lost = new VanillaChunkCell(6, 1, 4);
        model.EnqueueCandidate(lost, 500);
        model.TryDequeueCandidate(out _);
        c.True(model.Observe(lost, rendered: false, 500, out VanillaReadinessPublication drop),
            "a committed cell that stops rendering proposes its loss");
        model.ResolvePublication(drop, accepted: true);

        // Column x=6 begins 48 blocks from the camera and shares its z band, so the bound
        // is the horizontal gap to that column rather than to a chunk corner.
        double partial = model.NearestIncompleteColumnBlocks(centreX, centreZ, out unready);
        c.Near(48d, partial, 0.001, "a column missing one vertical chunk bounds the radius at its own edge");
        c.True(unready > partial, "a partly ready column is not counted as wholly unready");
    }

    /// <summary>
    /// The mask is the only thing the shader reads, so its addressing, its accounting, and
    /// above all its failure direction matter more than its speed. Every uncertain path has
    /// to leave a cache-owned texel: a stale owned texel hides terrain vanilla is not
    /// drawing, which is the hole this whole design exists to prevent.
    /// </summary>
    static void OwnershipMask(Check c)
    {
        var mask = new VanillaReadinessMask(4, 2);
        c.Eq(4, mask.Width, "the atlas is one texel per chunk across the ring");
        c.Eq(8, mask.Height, "Y slices stack down a single 2D texture");
        c.Eq(0L, mask.ReadyTexels, "a new mask owns nothing, so cached terrain covers everything");

        var cell = new VanillaChunkCell(1, 1, 2);
        c.True(mask.Set(cell, ready: true), "committing ownership changes the texel");
        c.True(mask.IsReady(cell), "the committed cell reads back as vanilla-owned");
        c.Eq(1L, mask.ReadyTexels, "owned texels are counted");
        c.False(mask.Set(cell, ready: true), "an unchanged texel is not reported as a change");

        c.Eq(VanillaReadinessMask.ReadyTexel, mask.Texels[
            VanillaReadinessMask.TexelIndex(1, 1, 2, 4, 2)], "the texel lands at its computed address");
        c.Eq(VanillaReadinessMask.TexelIndex(1, 1, 2, 4, 2),
            VanillaReadinessMask.TexelIndex(1 + 4, 1, 2 + 4, 4, 2),
            "ring wrapping maps a coordinate one full ring away onto the same texel");

        c.False(mask.Set(new VanillaChunkCell(0, 2, 0), ready: true),
            "a cell above the world cannot be marked owned");
        c.False(mask.Set(new VanillaChunkCell(0, -1, 0), ready: true),
            "a cell below the world cannot be marked owned");

        mask.MarkUploaded();
        c.False(mask.Dirty, "an accepted upload clears the dirty flag");
        c.Eq(1L, mask.Uploads, "uploads are counted for telemetry");
        mask.Set(cell, ready: false);
        c.True(mask.Dirty, "losing ownership dirties the buffer again");
        c.Eq(0L, mask.ReadyTexels, "the owned count follows the loss");

        mask.Set(new VanillaChunkCell(3, 0, 3), ready: true);
        mask.Set(new VanillaChunkCell(3, 1, 3), ready: true);
        mask.ClearColumn(3, 3);
        c.Eq(0L, mask.ReadyTexels,
            "a column leaving the window releases every vertical cell before its slot is reused");

        mask.Set(cell, ready: true);
        mask.Clear();
        c.Eq(0L, mask.ReadyTexels, "clearing returns every cell to the cache");
        c.True(mask.Dirty, "a cleared mask must reach the GPU before it is trusted");

        c.Throws<ArgumentOutOfRangeException>(() => new VanillaReadinessMask(3, 1),
            "a non-power-of-two ring cannot be addressed by masking and is refused");

        // The tracker owns the window, so a rebuild is what keeps a reused ring slot from
        // carrying another place's ownership into the texture.
        var model = new VanillaRenderReadiness(4, 2);
        model.SetWindow(0, 0, 4, 4);
        DrainQueue(model);
        DrainQueue(model);
        var owned = new VanillaChunkCell(1, 0, 1);
        var excluded = new VanillaChunkCell(1, 1, 1);
        CommitReady(model, owned, 1);
        CommitReady(model, excluded, 3);
        // The rebuild must reproduce what the incremental path produces, exclusions and all.
        // Marking every committed cell ready instead is what put air ownership back into the
        // atlas on every window change - which is every 32 blocks of travel.
        model.SetMaskExcluded(excluded, true);
        var rebuilt = new VanillaReadinessMask(4, 2);
        model.WriteMask(rebuilt);
        c.True(rebuilt.IsReady(owned), "a rebuild reproduces committed ownership");
        c.False(rebuilt.IsReady(excluded), "a rebuild honours the stored mask exclusion");
        c.Eq(1L, rebuilt.ReadyTexels, "a rebuild reproduces exactly the committed, non-excluded cells");

        model.SetMaskExcluded(excluded, false);
        model.WriteMask(rebuilt);
        c.Eq(2L, rebuilt.ReadyTexels, "clearing the exclusion returns the cell to the rebuild");
        model.SetMaskExcluded(excluded, true);

        model.SetWindow(8, 8, 4, 4);
        model.WriteMask(rebuilt);
        c.Eq(0L, rebuilt.ReadyTexels,
            "moving the window past the owned column leaves no ownership behind in the ring");
    }

    /// <summary>
    /// The fragment shader recomputes the atlas address from world coordinates in GLSL. A
    /// substring check proves the two expressions look alike; this evaluates the shader's
    /// arithmetic against the real one over a grid that crosses chunk boundaries, ring
    /// wraps and world extremes. A divergence here would not crash - it would silently
    /// read another chunk's ownership and hide ground that vanilla is not drawing.
    /// </summary>
    static void ShaderAddressAgreement(Check c)
    {
        const int capacity = 32;
        const int verticalChunks = 8;
        int mismatches = 0;
        int compared = 0;

        // Exactly what lodterrain.fsh does: an integer section origin in chunks plus the
        // floor of the section-local offset. The local offset is small and therefore exact
        // in float32, which is the whole point - a summed world coordinate is not. Passing
        // 512031.99 as one float rounds to 512032.0 and takes the next chunk's ownership.
        static int ShaderIndex(int originChunkX, int originChunkZ, float localX, float localY, float localZ)
        {
            int cellX = originChunkX + (int)MathF.Floor(localX / 32.0f);
            int cellY = (int)MathF.Floor(localY / 32.0f);
            int cellZ = originChunkZ + (int)MathF.Floor(localZ / 32.0f);
            int wrap = capacity - 1;
            return ((cellZ & wrap) + cellY * capacity) * capacity + (cellX & wrap);
        }

        // Section origins are multiples of the section footprint, which is itself a
        // multiple of the chunk size, so the origin in chunks is always exact.
        foreach (double originX in new[] { 0d, 1024d, 512_000d, 1_024_000d })
        foreach (double originZ in new[] { 0d, 64d, 512_064d })
        foreach (float localX in new[] { 0f, 31.999f, 32f, 63.999f })
        foreach (int y in new[] { 0, 1, 7 })
        {
            float localY = y * 32f + 1f;
            const float localZ = 1f;
            int originChunkX = (int)(originX / VanillaReadinessMask.ChunkBlocks);
            int originChunkZ = (int)(originZ / VanillaReadinessMask.ChunkBlocks);

            int expected = VanillaReadinessMask.TexelIndex(
                VanillaRenderReadiness.ChunkCoordinate(originChunkX, localX),
                y,
                VanillaRenderReadiness.ChunkCoordinate(originChunkZ, localZ),
                capacity, verticalChunks);
            int actual = ShaderIndex(originChunkX, originChunkZ, localX, localY, localZ);
            compared++;
            if (expected != actual) mismatches++;
        }

        c.Eq(0, mismatches, $"the shader address matches the mask address at all {compared} sampled points");
        c.True(compared >= 60, "the agreement sample crosses boundaries, wraps and large world coordinates");

        // Half-open boundaries: the last block of a chunk and the first of the next must
        // land in different cells, or a surface exactly on a boundary flips ownership.
        const int farOrigin = 512_000 / 32;
        c.True(ShaderIndex(farOrigin, 0, 31.999f, 1f, 1f) != ShaderIndex(farOrigin, 0, 32f, 1f, 1f),
            "an exact 32-block boundary crosses into the next ownership cell");
        c.Eq(ShaderIndex(farOrigin, 0, 0f, 1f, 1f), ShaderIndex(farOrigin, 0, 31.999f, 1f, 1f),
            "a whole chunk of ground shares one ownership cell even at a large world origin");
    }

    static void DeferredObservationQueue(Check c)
    {
        var model = new VanillaRenderReadiness(4, 1);
        model.SetWindow(0, 0, 4, 4);
        DrainQueue(model);
        var cell = new VanillaChunkCell(1, 0, 1);
        model.EnqueueCandidate(cell, renderFrame: 19);
        c.Eq(19L, model.OldestCandidateFrame(), "candidate age telemetry retains the oldest queued frame");
        model.TryDequeueCandidate(out _);
        c.Eq(-1L, model.OldestCandidateFrame(), "draining the queue clears candidate age telemetry");
        model.Observe(cell, rendered: true, 20, out _);
        c.Eq(20L, model.OldestObservedFrame(), "stabilization age starts at the first true observation");
        c.Eq(1, model.PendingObservations, "a first true observation enters the fixed deferred queue");
        c.Eq(0, model.PromoteObserved(20, 4), "an observation cannot re-probe in its first render frame");
        c.False(model.TryDequeueCandidate(out _), "same-frame stabilization cannot spin through the probe budget");
        c.Eq(1, model.PromoteObserved(21, 4), "a complete render boundary promotes the observation");
        c.True(model.TryDequeueCandidate(out VanillaChunkCell promoted), "the promoted observation becomes probe work");
        c.Eq(cell, promoted, "deferred stabilization preserves the exact cell identity");
        c.True(model.Observe(promoted, rendered: true, 21, out VanillaReadinessPublication publication),
            "the promoted second true observation proposes readiness");
        c.True(model.ResolvePublication(publication, accepted: true),
            "the deferred production path commits through the publication handshake");
    }

    static void BoundaryRevalidation(Check c)
    {
        var model = new VanillaRenderReadiness(16, 1);
        model.SetWindow(0, 0, 9, 9);
        DrainQueue(model);
        var interior = new VanillaChunkCell(4, 0, 4);
        var boundary = new VanillaChunkCell(6, 0, 4);
        CommitReady(model, interior, 1);
        CommitReady(model, boundary, 3);

        int cursor = 0;
        c.Eq(1, model.QueueReadyShell(4, 4, innerRadius: 2, outerRadius: 2,
            ref cursor, maxCells: 16), "boundary-first revalidation queues the ready frontier cell");
        c.True(model.TryDequeueCandidate(out VanillaChunkCell queued),
            "the frontier revalidation candidate can be probed");
        c.Eq(boundary, queued, "the shell scan does not spend its queue slot on an interior ready cell");
        c.False(model.TryDequeueCandidate(out _), "the interior ready cell was not queued by a radius-two shell");
        c.True(model.TrackedArrayBytes > model.ActiveCells,
            "readiness telemetry reports the fixed state and queue arrays, not only readiness bytes");

        // A ready-only interior sweep can lose ownership but never gain it. A route on
        // 2026-08-18 measured a full 15-second interval spending 432,744 probes on
        // already-ready cells and none on 1,801 pending ones, so maintenance must revisit
        // a cell whose chunk was not yet rendered when its dirty event was probed.
        var missed = new VanillaChunkCell(1, 0, 1);
        model.EnqueueCandidate(missed, 5, engineEvent: true);
        model.TryDequeueCandidate(out _);
        model.Observe(missed, rendered: false, 5, out _);
        c.Eq(VanillaReadinessState.Pending, model.State(missed),
            "a chunk that is not rendered yet leaves its cell cache-owned and pending");
        while (model.TryDequeueCandidate(out _)) { }

        int maintenance = 0;
        bool requeued = false;
        for (int pass = 0; pass < model.ActiveCells && !requeued; pass++)
        {
            model.QueueMaintenanceCells(ref maintenance, 1, 7);
            while (model.TryDequeueCandidate(out VanillaChunkCell swept))
                requeued |= swept == missed;
        }
        c.True(requeued, "interior maintenance revisits a pending cell rather than only ready ones");

        long eventsBefore = model.CandidateEventsAccepted;
        long sweepsBefore = model.ScheduledCandidatesAccepted;
        model.EnqueueCandidate(new VanillaChunkCell(2, 0, 2), 8, engineEvent: true);
        model.EnqueueCandidate(new VanillaChunkCell(3, 0, 3), 8);
        c.Eq(eventsBefore + 1, model.CandidateEventsAccepted,
            "engine-announced candidates are counted apart from the tracker's own sweeps");
        c.Eq(sweepsBefore + 1, model.ScheduledCandidatesAccepted,
            "scheduled sweep candidates have their own counter");
    }

    static void WindowInvalidationAndTeardown(Check c)
    {
        var model = new VanillaRenderReadiness(4, 1);
        model.SetWindow(0, 0, 4, 4);
        DrainQueue(model);
        var cell = new VanillaChunkCell(0, 0, 0);
        CommitReady(model, cell, 1);
        long section = LodWorld.SectionKey(0, 0, 0);
        c.Eq(1, model.ReadyCount(section), "precondition: the old window owns one ready cell");

        model.SetWindow(1, 0, 4, 4);
        c.Eq(VanillaReadinessState.Unknown, model.State(cell), "a cell leaving the active window becomes cache-owned");
        c.Eq(0, model.ReadyCount(section), "window movement removes stale CPU aggregate ownership");
        c.Eq(0, model.CommittedReadyCells, "window invalidation updates the global committed count");

        var current = new VanillaChunkCell(1, 0, 0);
        CommitReady(model, current, 3);
        model.Clear();
        c.Eq(VanillaReadinessState.Unknown, model.State(current), "world teardown rejects old cell state");
        c.Eq(0, model.ReadyCount(section), "world teardown clears ancestor aggregates");
        c.Eq(0, model.PendingCandidates, "world teardown clears pending candidates");

        model.SetWindow(1, 0, 4, 4);
        model.EnqueueCandidate(current);
        model.TryDequeueCandidate(out _);
        model.Observe(current, rendered: true, 5, out _);
        model.Observe(current, rendered: true, 6, out VanillaReadinessPublication oldWorldPublication);
        model.Clear();
        model.SetWindow(1, 0, 4, 4);
        model.EnqueueCandidate(current);
        model.TryDequeueCandidate(out _);
        model.Observe(current, rendered: true, 7, out _);
        model.Observe(current, rendered: true, 8, out VanillaReadinessPublication newWorldPublication);
        c.False(model.ResolvePublication(oldWorldPublication, accepted: true),
            "a late publication from the old world cannot commit into a reseeded coordinate");
        c.True(model.ResolvePublication(newWorldPublication, accepted: true),
            "the current world's token still commits after rejecting the stale result");
    }

    static void SteadyStateAllocation(Check c)
    {
        var model = new VanillaRenderReadiness(4, 2);
        model.SetWindow(0, 0, 4, 4);
        DrainQueue(model);
        DrainQueue(model);
        var cell = new VanillaChunkCell(0, 0, 0);
        CommitReady(model, cell, 1);
        long section = LodWorld.SectionKey(0, 0, 0);

        // Warm JIT and dictionary lookup paths before measuring the quiet, converged path.
        _ = model.State(cell);
        _ = model.Classify(section);
        _ = model.TryDequeueCandidate(out _);
        int boundaryCursor = 0;
        int interiorCursor = 0;
        model.QueueReadyShell(0, 0, 0, 0, ref boundaryCursor, 1);
        model.TryDequeueCandidate(out VanillaChunkCell warmCell);
        model.Observe(warmCell, rendered: true, 10, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            _ = model.State(cell);
            _ = model.Classify(section);
            model.QueueReadyShell(0, 0, 0, 0, ref boundaryCursor, 1);
            model.QueueMaintenanceCells(ref interiorCursor, 1);
            model.PromoteObserved(20, 1);
            while (model.TryDequeueCandidate(out VanillaChunkCell queued))
                model.Observe(queued, rendered: true, 20, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        c.Eq(0L, allocated,
            "converged lookup, revalidation scheduling, probing, and classification allocate nothing");
    }

    /// <summary>
    /// Empties the candidate queue. Moving the window queues the columns it gained, so a
    /// test that only wants the window as setup drains that work before measuring its own.
    /// </summary>
    static void DrainQueue(VanillaRenderReadiness model)
    {
        while (model.TryDequeueCandidate(out _)) { }
        model.ResetTelemetry();
    }

    static void CommitReady(VanillaRenderReadiness model, VanillaChunkCell cell, int frame)
    {
        model.EnqueueCandidate(cell);
        model.TryDequeueCandidate(out _);
        model.Observe(cell, rendered: true, frame, out _);
        if (!model.Observe(cell, rendered: true, frame + 1, out VanillaReadinessPublication publication))
            throw new InvalidOperationException("Test setup failed to produce a readiness publication.");
        if (!model.ResolvePublication(publication, accepted: true))
            throw new InvalidOperationException("Test setup failed to commit readiness.");
    }

    /// <summary>
    /// The drawn-without-geometry diagnostic reads two internal fields on the game's own
    /// ClientChunk. Reflection fails silently by design - an unbound query measures nothing
    /// rather than guessing - so the only thing that notices a game update renaming them is
    /// a check that asks the installed assembly directly.
    ///
    /// This asserts against the real game DLL, not against a fixture, which is the point.
    /// </summary>
    static void ChunkGeometryBinding(Check c)
    {
        Type? clientChunk = Type.GetType(
            "Vintagestory.Client.NoObf.ClientChunk, VintagestoryLib", throwOnError: false);
        c.True(clientChunk != null, "the installed game still has Vintagestory.Client.NoObf.ClientChunk");
        if (clientChunk == null) return;

        foreach (string name in VanillaChunkGeometry.FieldNames)
        {
            FieldInfo? field = clientChunk.GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            c.True(field != null, $"ClientChunk still declares {name}");
            c.True(field?.FieldType == typeof(ModelDataPoolLocation[]),
                $"{name} is still a ModelDataPoolLocation array");
        }

        // The binding is what the renderer actually calls; a compiled getter that threw
        // during static construction would leave this false with everything above passing.
        c.True(VanillaChunkGeometry.Available,
            "the renderer's chunk geometry query bound against the installed game");

        // The culler's verdict is the only signal that means "drawing this now". It is
        // public API and needs no reflection, but a game update could still move it, and
        // losing it silently would put ownership back on a counter that only goes up.
        FieldInfo? cullVisible = clientChunk.GetField("CullVisible",
            BindingFlags.Instance | BindingFlags.Public);
        c.True(cullVisible != null && cullVisible.FieldType.Name == "Bools",
            "ClientChunk still exposes the culler's per-chunk visibility as a public Bools");
        FieldInfo? bufIndex = clientChunk.GetField("bufIndex",
            BindingFlags.Static | BindingFlags.Public);
        c.True(bufIndex != null && bufIndex.FieldType == typeof(int),
            "ClientChunk still exposes the public buffer index the culler writes through");

        // A chunk type the query does not understand must answer "unavailable", never a
        // confident false: the whole diagnostic depends on not inventing discrepancies.
        c.False(VanillaChunkGeometry.TryHoldsGeometry(null, out bool holds),
            "a missing chunk is unmeasurable rather than empty");
        c.False(holds, "an unmeasurable chunk never reports geometry");
    }

    /// <summary>
    /// The atlas is a wrapped ring, so a column that leaves the window hands its texels to
    /// a different column at the same address. If the mask does not forget the old one, the
    /// shader reads the departed column's ownership for the arriving one and discards cached
    /// terrain in cells nothing owns - a band of missing world at the trailing edge of a
    /// moving window, stable while standing still, invisible to every CPU-side check because
    /// the tracker is correct and only its GPU mirror is stale.
    ///
    /// That shipped from 0.3.0 to 0.3.12. `VanillaReadinessMask.ClearColumn` existed the
    /// whole time, documented as required, and nothing ever called it.
    /// </summary>
    static void MaskForgetsEvictedColumns(Check c)
    {
        const int capacity = 8;
        const int vertical = 4;
        var model = new VanillaRenderReadiness(capacity, vertical);
        var mask = new VanillaReadinessMask(capacity, vertical);
        model.ColumnEvicted = (x, z) => mask.ClearColumn(x, z);

        model.SetWindow(0, 0, 4, 4);
        var cell = new VanillaChunkCell(1, 2, 1);
        CommitReady(model, cell, frame: 10);
        mask.Set(cell, true);
        c.True(mask.IsReady(cell), "the mask holds the committed column before the window moves");

        // Slide far enough that the old column is outside the new window, and far enough
        // that a different world column wraps onto the very same ring slot.
        model.SetWindow(capacity, capacity, 4, 4);
        c.False(mask.IsReady(cell), "the mask forgot the column that left the window");

        var arriving = new VanillaChunkCell(cell.X + capacity, cell.Y, cell.Z + capacity);
        c.Eq(VanillaReadinessMask.TexelIndex(cell.X, cell.Y, cell.Z, capacity, vertical),
            VanillaReadinessMask.TexelIndex(arriving.X, arriving.Y, arriving.Z, capacity, vertical),
            "the arriving column really does reuse the departed column's texel");
        c.False(mask.IsReady(arriving),
            "the arriving column does not inherit the departed column's ownership");
        c.Eq(0L, mask.ReadyTexels, "no ownership survives the slide");
    }

    /// <summary>
    /// Air owns its cell and must never own its texel, and the two halves of that rule have
    /// to hold together. Refusing air ownership outright collapsed 0.3.3 - a column counts as
    /// owned only when every one of its chunks does and every column has sky - while letting
    /// air reach the atlas shears the top off the cached horizon, because cached terrain
    /// overshoots into air chunks that vanilla draws nothing in.
    ///
    /// The exclusion therefore lives beside the state rather than being applied by whoever
    /// happens to be writing the texel, and this asserts both halves at once: zero texel,
    /// full aggregates.
    /// </summary>
    static void MaskExcludedCellsOwnGroundWithoutATexel(Check c)
    {
        var model = new VanillaRenderReadiness(4, 2);
        model.SetWindow(0, 0, 2, 2);
        DrainQueue(model);

        long section = LodWorld.SectionKey(0, 0, 0);
        int frame = 1;
        for (int z = 0; z < 2; z++)
        for (int x = 0; x < 2; x++)
        for (int y = 0; y < 2; y++)
        {
            CommitReady(model, new VanillaChunkCell(x, y, z), frame);
            frame += 2;
        }

        var air = new VanillaChunkCell(1, 1, 1);
        c.True(model.SetMaskExcluded(air, true), "flagging a cell excluded reports the change");
        c.False(model.SetMaskExcluded(air, true), "an unchanged exclusion is not reported as a change");
        c.True(model.IsMaskExcluded(air), "the exclusion reads back from the tracker");
        c.False(model.IsMaskExcluded(new VanillaChunkCell(0, 0, 0)),
            "flagging one cell does not exclude its neighbours");

        var mask = new VanillaReadinessMask(4, 2);
        model.WriteMask(mask);
        c.False(mask.IsReady(air), "an excluded cell produces no texel from a rebuild");
        c.Eq(7L, mask.ReadyTexels, "only the excluded cell is withheld from the atlas");

        c.Eq(8, model.CommittedReadyCells, "an excluded cell still counts as committed");
        c.Eq(8, model.ReadyCount(section), "an excluded cell still counts toward its section");
        c.Eq(VanillaSectionOwnership.VanillaOnly, model.Classify(section),
            "an excluded cell does not stop its section from being wholly owned");
        model.GetColumnReadiness(new int[2], out _, out int fullColumns, out _, out _);
        c.Eq(4, fullColumns, "an excluded cell still completes its column");
        c.Eq(0, model.AuditReadyCounts(), "exclusion does not disturb the audited section counts");

        // The bit is cell state, so it goes when the cell does. A slot handed to different
        // world coordinates must not answer for them with the old column's exclusion.
        model.SetWindow(4, 4, 2, 2);
        DrainQueue(model);
        c.False(model.IsMaskExcluded(air), "a column leaving the window drops its exclusions");
        var arriving = new VanillaChunkCell(air.X + 4, air.Y, air.Z + 4);
        CommitReady(model, arriving, frame);
        c.False(model.IsMaskExcluded(arriving),
            "a column arriving in a reused slot does not inherit the departed exclusion");
        model.WriteMask(mask);
        c.True(mask.IsReady(arriving), "the arriving column reaches the atlas on its own terms");
    }

    /// <summary>
    /// Loaded, meshed, unhidden and cull-visible is still not drawn. The engine range-culls
    /// every terrain pool location per frame against the view distance, and none of the
    /// signals ownership is built on follow: the drawn counter never resets, the mesh stays
    /// in the pool, and the culler freezes its verdict outright while the camera holds still
    /// in one chunk. Ground the player travelled away from therefore stayed committed
    /// forever, and the mask discarded cached terrain there against nothing - a band at the
    /// seam, on the trailing side only, that never healed.
    ///
    /// The threshold has to clear the whole chunk rather than merely reach it, and that is
    /// the part worth pinning. The engine measures range from the mesh bounding sphere's
    /// centre, which `TesselatedChunk` builds as the geometry extents midpoint
    /// (`positionX + (xMax + xMin) / 2f`) - so it sits wherever the blocks in that chunk
    /// happen to be, anywhere across the 32-block span. Comparing the column's nearest face
    /// against the plain view distance would leave a residual stale ring up to 32*sqrt(2)
    /// blocks wide, which is a thinner copy of the same band.
    ///
    /// So the property asserted here is the no-hole direction, not the no-overlap one: for
    /// every cell whose sphere centre could be range-culled under any admissible midpoint,
    /// the clause must deny. Overlap is asserted only to be bounded, because overlap is the
    /// accepted cost.
    /// </summary>
    static void OwnershipStopsAtTheEnginesDrawRange(Check c)
    {
        // Camera at the centre of chunk 0, so a column's distance is a clean multiple of 32.
        // At a 256-block view distance the threshold is 256 - 46 = 210 blocks.
        const double cameraX = 16, cameraZ = 16;
        const float viewDistance = 256;

        c.False(VanillaRenderReadiness.BeyondVanillaDrawRange(0, 0, cameraX, cameraZ, viewDistance),
            "the camera's own column is never beyond the draw range");
        c.False(VanillaRenderReadiness.BeyondVanillaDrawRange(7, 0, cameraX, cameraZ, viewDistance),
            "the last column that cannot hide a culled sphere centre is still drawn");
        c.True(VanillaRenderReadiness.BeyondVanillaDrawRange(8, 0, cameraX, cameraZ, viewDistance),
            "a column whose geometry could sit past the engine's bound is denied");
        c.False(VanillaRenderReadiness.BeyondVanillaDrawRange(-7, 0, cameraX, cameraZ, viewDistance),
            "the denial is symmetric: the trailing side keeps the same last drawn column");
        c.True(VanillaRenderReadiness.BeyondVanillaDrawRange(-8, 0, cameraX, cameraZ, viewDistance),
            "the trailing side is denied at the same distance as the leading side");
        c.True(VanillaRenderReadiness.BeyondVanillaDrawRange(6, 6, cameraX, cameraZ, viewDistance),
            "the bound is radial, so a diagonal column is denied while its axial peer is not");
        c.False(VanillaRenderReadiness.BeyondVanillaDrawRange(6, 0, cameraX, cameraZ, viewDistance),
            "that same axial offset is inside the threshold");

        // The camera's own column must survive every view distance, including degenerate
        // ones: the mask's near-field floor is gated on the camera's cell being owned, so a
        // clamp failure here would suppress nothing at all rather than fail safe.
        foreach (float distance in new[] { 0f, 8f, 46f, 64f })
        {
            c.False(VanillaRenderReadiness.BeyondVanillaDrawRange(0, 0, cameraX, cameraZ, distance),
                $"the camera's own column survives a {distance}-block view distance");
        }

        // A window is built at the view distance plus two guard chunks, so the annulus this
        // rule releases covers the guard band - which is where the band was reported.
        VanillaReadinessWindow window = VanillaRenderReadiness.CalculateWindow(
            cameraX, cameraZ, viewDistance, mapSizeX: 32768, mapSizeZ: 32768);
        c.True(VanillaRenderReadiness.BeyondVanillaDrawRange(
                window.CenterX + window.OuterRadius, window.CenterZ, cameraX, cameraZ, viewDistance),
            "the outer edge of the tracked window is beyond what the engine draws");
        c.False(VanillaRenderReadiness.BeyondVanillaDrawRange(
                window.CenterX + window.VanillaRadius - 3, window.CenterZ, cameraX, cameraZ, viewDistance),
            "the interior of the vanilla radius is not touched by the rule");

        // The property that matters. The engine's test is the sphere centre's horizontal
        // distance squared against ViewDistanceSq = viewDistance^2 + 400
        // (FrustumCulling.UpdateViewDistance), and every terrain LOD level's bound is at
        // most that. The centre can be any point in the chunk column, so the cell is at risk
        // of being culled as soon as the column's FARTHEST point reaches that bound - and
        // every such cell has to be denied, or it is a hole.
        int denied = 0;
        int atRisk = 0;
        int holes = 0;
        int overlapTooDeep = 0;
        double worstOverlapBlocks = 0;

        foreach (float distance in new[] { 64f, 128f, 256f, 512f })
        foreach (double camera in new[] { 0d, 16d, 31.9d, 1_000_016d })
        for (int offsetZ = -24; offsetZ <= 24; offsetZ++)
        for (int offsetX = -24; offsetX <= 24; offsetX++)
        {
            int chunkX = offsetX + (int)Math.Floor(camera / 32);
            int chunkZ = offsetZ + (int)Math.Floor(camera / 32);
            bool deniedHere = VanillaRenderReadiness.BeyondVanillaDrawRange(
                chunkX, chunkZ, camera, camera, distance);
            if (deniedHere) denied++;

            // The worst admissible sphere centre: a corner of the column's horizontal span.
            double farX = Math.Max(Math.Abs(chunkX * 32.0 - camera), Math.Abs(chunkX * 32.0 + 32 - camera));
            double farZ = Math.Max(Math.Abs(chunkZ * 32.0 - camera), Math.Abs(chunkZ * 32.0 + 32 - camera));
            double engineBoundSq = distance * (double)distance + 400;
            if (farX * farX + farZ * farZ >= engineBoundSq)
            {
                atRisk++;
                if (!deniedHere) holes++;
            }

            // Overlap is allowed, but only just past the slack the sphere centre needs.
            double face = VanillaRenderReadiness.ColumnDistanceBlocksFrom(chunkX, chunkZ, camera, camera);
            if (!deniedHere) continue;
            double insideBy = distance - face;
            if (insideBy > worstOverlapBlocks) worstOverlapBlocks = insideBy;
            if (insideBy > 46.0001) overlapTooDeep++;
        }

        c.True(atRisk > 1000, "the sweep actually reaches cells the engine's range test can cull");
        c.True(denied > 1000, "the sweep actually exercises the denial");
        c.Eq(0, holes,
            "every cell whose geometry could sit beyond the engine's range bound is denied, "
            + "whatever the chunk's own flags say");
        c.Eq(0, overlapTooDeep,
            "the denial never reaches more than the in-chunk sphere-centre slack inside the view distance");
        c.True(worstOverlapBlocks > 0,
            "the accepted overlap is real: cells the engine may still draw are demoted at the seam");
    }
}
