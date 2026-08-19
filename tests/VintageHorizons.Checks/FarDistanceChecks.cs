namespace VintageHorizons.Checks;

public static class FarDistanceChecks
{
    public static void Run(Check c)
    {
        CachedBounds(c);
        EffectiveDistanceAndCap(c);
        ConservativeNearHandoff(c);
        StableProjection(c);
            NearHandoffHysteresis(c);
    }

    static void CachedBounds(Check c)
    {
        var bounds = new LodMeshBounds();
        c.Eq(0.0, bounds.FarthestDistanceTo(0, 0), "empty mesh bounds require no distance");

        // L0 (2,3) occupies [128,192] x [192,256]. The farthest corner from the
        // origin is (192,256), exactly 320 blocks away.
        long first = LodWorld.SectionKey(0, 2, 3);
        bounds.Include(first);
        c.Near(320, bounds.FarthestDistanceTo(0, 0), 0.0001,
            "bounds use the farthest footprint corner rather than a section centre");

        // L1 (2,1) occupies [256,384] x [128,256], expanding only the east edge.
        long coarse = LodWorld.SectionKey(1, 2, 1);
        bounds.Include(coarse);
        c.Near(Math.Sqrt(384 * 384 + 256 * 256), bounds.FarthestDistanceTo(0, 0), 0.0001,
            "mixed LOD footprints expand cached world bounds correctly");

        var strip = new LodMeshBounds();
        long northWest = LodWorld.SectionKey(0, 0, 0);
        long northEast = LodWorld.SectionKey(0, 2, 0);
        long southWest = LodWorld.SectionKey(0, 0, 2);
        long southEast = LodWorld.SectionKey(0, 2, 2);
        long middle = LodWorld.SectionKey(0, 1, 1);
        strip.Include(northWest);
        strip.Include(northEast);
        strip.Include(southWest);
        strip.Include(southEast);
        strip.Include(middle);

        strip.Remove(middle);
        c.False(strip.Dirty, "removing an interior mesh leaves cached extremes valid");

        strip.Remove(southEast);
        c.True(strip.Dirty, "removing an extreme mesh requests one bounds rebuild");

        // The second sequence represents a water-only mesh; rebuild must include it as
        // well as opaque terrain without needing a merged allocation or HashSet.
        long waterOnly = LodWorld.SectionKey(0, 4, 1);
        strip.Rebuild(new[] { northWest }, new[] { waterOnly });
        c.False(strip.Dirty, "rebuild makes cached bounds valid again");
        c.Near(Math.Sqrt(320 * 320 + 128 * 128), strip.FarthestDistanceTo(0, 0), 0.0001,
            "rebuild includes water-only mesh footprints");

        strip.Clear();
        c.False(strip.HasBounds, "clearing a world clears its mesh bounds");
    }

    static void EffectiveDistanceAndCap(Check c)
    {
        c.Eq(2536f, LodFarDistance.Effective(0, 1000, 0),
            "vanilla view distance retains its safety margin without LOD meshes");
        c.Eq(10000f, LodFarDistance.Effective(10000, 1000, 0),
            "unlimited .vhfar behavior follows the cached terrain edge");
        c.Eq(4096f, LodFarDistance.Effective(10000, 1000, 4096),
            "an explicit .vhfar cap limits the LOD far edge");
        c.Eq(2536f, LodFarDistance.Effective(10000, 1000, 2048),
            "the .vhfar cap cannot clip the vanilla view-distance safety band");
        c.Eq(4608f, LodFarDistance.RequiredProjection(4096),
            "the camera projection retains room beyond the visible LOD edge");
    }

    static void ConservativeNearHandoff(Check c)
    {
        c.Eq(0f, LodNearHandoff.InnerDiscardRadius(0),
            "no vanilla radius keeps all available cached fallback");
        c.Eq(0f, LodNearHandoff.InnerDiscardRadius(128),
            "a tiny view retains the minimum fallback overlap");
        c.Eq(64f, LodNearHandoff.InnerDiscardRadius(256),
            "a 256-block view hands off only in its close 64-block core");
        c.Eq(256f, LodNearHandoff.InnerDiscardRadius(512),
            "a normal view keeps cached fallback through its inner half");
        c.Eq(512f, LodNearHandoff.InnerDiscardRadius(1024),
            "a large view still hands off no farther out than half radius");

        foreach (float view in new[] { 192f, 256f, 512f, 1024f, 4096f })
        {
            float inner = LodNearHandoff.InnerDiscardRadius(view);
            c.True(view - inner >= LodNearHandoff.MinimumFallbackOverlap,
                $"view distance {view} retains at least the minimum fallback overlap");
            c.True(inner <= view * LodNearHandoff.MaximumInnerFraction,
                $"view distance {view} never hands off outside half radius");
        }
    }

    static void StableProjection(Check c)
    {
        var state = new LodFarPlaneState();

        LodFarPlaneUpdate update = state.Update(3512, 0);
        c.True(update.Changed, "the first projection distance is applied");
        c.Eq(3584f, update.Distance, "projection distance rounds up to a safe 512-block step");

        update = state.Update(3585, 100);
        c.True(update.Changed, "crossing a distance step grows the projection immediately");
        c.Eq(4096f, update.Distance, "growth selects the next complete distance step");

        update = state.Update(3600, 200);
        c.False(update.Changed, "movement within one distance band does not reset projection");
        c.Eq(4096f, update.Distance, "the applied projection remains stable within a band");

        update = state.Update(3500, 1000);
        c.False(update.Changed, "a lower band starts a shrink cooldown instead of resetting immediately");
        update = state.Update(3500, 5999);
        c.False(update.Changed, "projection does not shrink before the cooldown expires");
        update = state.Update(3500, 6000);
        c.True(update.Changed, "projection shrinks after a stable full cooldown");
        c.Eq(3584f, update.Distance, "shrink lands on the safe upper edge of the lower band");

        update = state.Update(3000, 7000);
        c.False(update.Changed, "another lower band starts a new cooldown");
        update = state.Update(3580, 7100);
        c.False(update.Changed, "returning to the applied band cancels pending shrink");
        update = state.Update(3000, 12000);
        c.False(update.Changed, "a cancelled shrink must earn a fresh cooldown");

        update = state.Update(5000, 12001);
        c.True(update.Changed, "required growth interrupts a shrink and applies immediately");
        c.Eq(5120f, update.Distance, "immediate growth still rounds upward safely");

        state.Reset();
        update = state.Update(3000, 20000);
        c.True(update.Changed, "a new world applies a fresh projection after state reset");
        c.Eq(3072f, update.Distance, "reset does not retain the previous world's far plane");
    }
    /// <summary>
    /// The readiness-derived handoff must restore cached coverage the instant measured
    /// ownership shrinks, and must make a player wait before it hides cached terrain that
    /// vanilla may not have drawn yet. A hole is worse than an overlap in every ordering
    /// the plan states, so the asymmetry is the whole point of this state.
    /// </summary>
    static void NearHandoffHysteresis(Check c)
    {
        var state = new LodNearHandoffState();
        c.Eq(0f, state.Update(200, 0), "the first grow waits rather than suppressing cache immediately");
        c.Eq(0f, state.Update(200, 499), "growth is withheld for the whole hold period");
        c.Eq(192f, state.Update(200, 500),
            "growth applies after the hold, quantized down to a whole vanilla chunk");

        c.Eq(64f, state.Update(64, 501), "a measured shrink restores cached coverage in the same frame");
        c.Eq(0f, state.Update(0, 502), "losing all measured ownership hands everything back to the cache");

        // The smallest radius seen during a hold is what applies, so continuous movement
        // neither stalls growth forever nor lets a transient peak through.
        c.Eq(0f, state.Update(300, 600), "a new grow candidate starts its own hold");
        c.Eq(0f, state.Update(128, 700), "a lower reading during the hold does not apply early");
        c.Eq(128f, state.Update(300, 1100),
            "the hold applies the smallest radius it saw rather than the largest");

        var settled = new LodNearHandoffState();
        settled.Update(96, 0);
        c.Eq(96f, settled.Update(96, 1000), "a stable measurement settles at its quantized value");
        c.Eq(96f, settled.Update(127, 3000), "a sub-chunk increase does not move the applied radius");
    }
}
