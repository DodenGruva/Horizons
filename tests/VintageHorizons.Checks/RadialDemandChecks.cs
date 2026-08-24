namespace VintageHorizons.Checks;

public static class RadialDemandChecks
{
    public static void Run(Check c)
    {
        ExactRowsExcludeStructuralAncestors(c);
        ZeroResidentCacheStartsBoundedWork(c);
        EqualWaveServicesEveryDirection(c);
        RefinementAndOuterCoverageShareTheNextWave(c);
        ClosestFoundationReachesFinestStoredLevel(c);
        FoundationAndOutwardWindowsStayBoundedTogether(c);
    }

    static void ExactRowsExcludeStructuralAncestors(Check c)
    {
        var world = new LodWorld();
        long stored = LodWorld.SectionKey(0, 12, 14);
        world.InstallStoredKey(0, 12, 14, applyToParent: false);

        c.Eq(7, world.HasDataSet.Count,
            "one L0 row still synthesises its complete quadtree path");
        c.Eq(1, world.AvailableDataSet.Count,
            "the demand source set contains only the exact loadable row");
        c.True(world.AvailableDataSet.Contains(stored),
            "the exact stored row is available to the planner");
        c.False(world.AvailableDataSet.Contains(LodWorld.ParentKey(stored)),
            "a structural ancestor is not mistaken for a storage row");
    }

    static void ZeroResidentCacheStartsBoundedWork(Check c)
    {
        const double camera = 6432;
        var world = RingOfL0Rows();
        var planner = new LodRadialDemandPlanner(
            sectorCount: 8, outstandingLimit: 4, requestsPerFrame: 8);
        var pending = new HashSet<long>();

        planner.Pump(world.AvailableDataSet, world.AvailableDataRevision,
            LodWorld.DetailPolicyRevision, camera, camera, 0, 2048,
            _ => false, pending.Contains, _ => false,
            key => pending.Add(key));

        c.Eq(0, world.Sections.Count, "the persisted fixture begins with zero resident sections");
        c.Eq(0, world.RenderDirty.Count, "and no mutation accidentally bootstraps it");
        c.Eq(4, pending.Count, "the planner immediately starts a bounded admission window");
        c.Eq(4, planner.PendingCount, "the outstanding ceiling is owned explicitly");
        c.True(pending.All(world.AvailableDataSet.Contains),
            "bootstrap requests exact rows rather than synthetic ancestors");
    }

    static void EqualWaveServicesEveryDirection(Check c)
    {
        const double camera = 6432;
        var world = RingOfL0Rows();
        var planner = new LodRadialDemandPlanner(
            sectorCount: 8, outstandingLimit: 8, requestsPerFrame: 8);
        var requested = new List<long>();
        var pending = new HashSet<long>();

        planner.Pump(world.AvailableDataSet, world.AvailableDataRevision,
            LodWorld.DetailPolicyRevision, camera, camera, 0, 2048,
            _ => false, pending.Contains, _ => false,
            key => { pending.Add(key); requested.Add(key); return true; });

        int[] sectors = requested
            .Select(key => LodRadialDemandPlanner.SectorFor(key, camera, camera, 8))
            .Distinct().Order().ToArray();
        c.Eq(8, requested.Count, "one admission round gives every radial lane work");
        c.SeqEq(Enumerable.Range(0, 8).ToArray(), sectors,
            "camera direction cannot leave a rear or side lane idle");
    }

    static void RefinementAndOuterCoverageShareTheNextWave(Check c)
    {
        const double camera = 7680;
        long nearCoarse = LodWorld.SectionKey(6, 1, 1);
        long nearRefined = LodWorld.SectionKey(5, 3, 3);
        long outerCoarse = LodWorld.SectionKey(6, 2, 1);

        c.Eq(0, LodRadialDemandPlanner.WaveFor(nearCoarse, camera, camera),
            "nearby L6 coverage is the first wave");
        c.Eq(1, LodRadialDemandPlanner.WaveFor(nearRefined, camera, camera),
            "one nearby refinement is the next wave");
        c.Eq(1, LodRadialDemandPlanner.WaveFor(outerCoarse, camera, camera),
            "the next radial band's coarse coverage shares that wave");

        var available = new[] { outerCoarse, nearRefined, nearCoarse };
        var ready = new HashSet<long>();
        var pending = new HashSet<long>();
        var planner = new LodRadialDemandPlanner(
            sectorCount: 8, outstandingLimit: 8, requestsPerFrame: 8);

        planner.Pump(available, 1, LodWorld.DetailPolicyRevision,
            camera, camera, 0, 32768, ready.Contains, pending.Contains, _ => false,
            key => pending.Add(key));
        c.SeqEq(new[] { nearCoarse }, pending.ToArray(),
            "a later wave does not overtake unresolved nearby coarse coverage");

        pending.Clear();
        ready.Add(nearCoarse);
        planner.Pump(available, 1, LodWorld.DetailPolicyRevision,
            camera, camera, 0, 32768, ready.Contains, pending.Contains, _ => false,
            key => pending.Add(key));
        c.True(pending.SetEquals(new[] { nearRefined, outerCoarse }),
            "near sharpening and farther coarse arrival pipeline together");
    }

    static void ClosestFoundationReachesFinestStoredLevel(Check c)
    {
        const double camera = 6432;
        long finest = LodWorld.SectionKey(0, 100, 100);
        var chain = new List<long>();
        for (long key = finest; ; key = LodWorld.ParentKey(key))
        {
            chain.Add(key);
            if (LodWorld.KeyLevel(key) == LodWorld.MaxLevel) break;
        }

        var ready = new HashSet<long>();
        var pending = new HashSet<long>();
        var requested = new List<long>();
        var planner = new LodRadialDemandPlanner(
            sectorCount: 8, outstandingLimit: 16, requestsPerFrame: 4);

        for (int wave = 0; wave <= LodWorld.MaxLevel; wave++)
        {
            planner.Pump(chain, 1, LodWorld.DetailPolicyRevision,
                camera, camera, 0, 512, ready.Contains, pending.Contains, _ => false,
                key => { pending.Add(key); requested.Add(key); return true; });
            foreach (long key in pending) ready.Add(key);
            pending.Clear();
        }

        c.True(requested.Contains(finest),
            "the under-player foundation continues through the exact stored L0 row");
        c.Eq(LodWorld.MaxLevel + 1, ready.Count,
            "every required gate from coarse coverage to finest terrain becomes ready");
        c.Eq(finest, requested[^1],
            "the foundation finishes at fine terrain rather than stopping at a suppressed coarse mesh");
    }

    static void FoundationAndOutwardWindowsStayBoundedTogether(Check c)
    {
        c.Eq(LodRadialDemandPlanner.DefaultOutstandingLimit,
            LodTerrainRenderer.FoundationDemandOutstanding
                + LodTerrainRenderer.OutwardDemandOutstanding,
            "the inner and outward planners share the established 32-row admission ceiling");
        c.Eq(LodRadialDemandPlanner.DefaultRequestsPerFrame,
            LodTerrainRenderer.FoundationDemandRequestsPerFrame
                + LodTerrainRenderer.OutwardDemandRequestsPerFrame,
            "the two waves share rather than double the per-frame request allowance");
        c.True(LodTerrainRenderer.FoundationDemandOutstanding
                > LodTerrainRenderer.OutwardDemandOutstanding,
            "the closest L0 foundation owns the dominant admission share");
        c.True(LodTerrainRenderer.FoundationDemandRequestsPerFrame
                > LodTerrainRenderer.OutwardDemandRequestsPerFrame,
            "the closest L0 foundation owns the dominant per-frame request share");
        c.True(LodTerrainRenderer.OutwardDemandOutstanding
                >= LodRadialDemandPlanner.DefaultSectorCount,
            "outward progress retains at least one outstanding slot per radial lane");
        c.True(LodTerrainRenderer.OutwardDemandRequestsPerFrame > 0,
            "near-first acceleration never stops the outward wave");
    }

    static LodWorld RingOfL0Rows()
    {
        var world = new LodWorld();
        foreach ((int sx, int sz) in new[]
        {
            (102, 100), (102, 102), (100, 102), (98, 102),
            (98, 100), (98, 98), (100, 98), (102, 98),
        })
        {
            world.InstallStoredKey(0, sx, sz, applyToParent: false);
        }
        return world;
    }
}
