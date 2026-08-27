using Microsoft.Data.Sqlite;

namespace VintageHorizons.Checks;

/// <summary>
/// How much of the cached terrain we build is sealed inside rock, measured against a real
/// cache with no game process.
///
/// WHY THIS EXISTS. The mesher emits a face for every run boundary that nothing is resting
/// against, and a cave is exactly a gap between two runs in one column. So a buried cavern
/// gets a floor, a ceiling and walls, all of them enclosed in solid rock. That is
/// source-traced, and the owner confirmed it by flying through terrain with no-clip - but
/// "it happens" and "it costs enough to be worth fixing" are different claims, and only the
/// second one justifies changing the mesher. This measures the second one.
///
/// HOW IT DECIDES WHAT IS SEALED. Air is flooded from the sky and from the outer edge of a
/// 3x3 neighbourhood of sections; anything the flood does not reach is sealed. The 3x3 is
/// what makes the answer honest: a cave that leaves through the side of one section is
/// reachable, and flooding a single section alone would call it sealed and overstate the
/// saving. Two deliberate leaks push the answer the same way - the outer boundary of the
/// 3x3 counts as open, and an uncaptured column counts as air rather than rock. Both let
/// the flood escape where the truth is unknown, so what this reports is a LOWER bound.
///
/// HOW IT MEASURES THE SAVING. Not by counting faces itself - that would measure a
/// reimplementation of the mesher. Sealed air is filled with rock and the section is meshed
/// AGAIN by the renderer's own mesher, with its neighbours filled too so that no false wall
/// appears along a section edge. The difference between the two meshes is the answer, and
/// both sides of it come from the shipping code.
///
/// WHAT IT DOES NOT MODEL. Whether filling sealed pockets is a safe thing to ship. A cave
/// mouth, an arch and an overhang are all reachable from outside and the flood keeps them;
/// what it cannot say is whether the capture pipeline would agree at boundaries it has never
/// seen. This sizes the prize, it does not design the fix.
/// </summary>
public static class CaveField
{
    const int Grid = LodSection.GridSize;       // columns per section edge

    /// <summary>
    /// How far the flood may travel before it gives up and calls a pocket sealed, in
    /// sections either side of the one being measured.
    ///
    /// This is the fidelity knob, not a tuning detail. A cave that opens sideways is kept
    /// because the flood reaches it through its mouth - but only if the mouth is inside the
    /// neighbourhood. Widen it and fewer real caves are mistaken for sealed ones; widen it
    /// and the cost of deciding grows with the cube. Sweeping it is how you find out whether
    /// a shippable radius is safe: if the sealed share barely moves between radii, the
    /// pockets really are small and local, and a cheap implementation cannot swallow a cave
    /// system by accident. If it falls as the radius grows, the opposite - much of what a
    /// narrow flood calls sealed is a real cave that opens somewhere further away.
    ///
    /// Static and set once from the options, because the tool is one-shot and single
    /// threaded and threading it through every helper would obscure the measurement.
    /// </summary>
    static int Radius = 1;

    /// <summary>
    /// What counts as removable.
    ///
    /// <c>sealed</c> removes only air the flood proved enclosed - the safe rule, and the one
    /// that keeps every cave you could see into. <c>subsurface</c> removes everything below
    /// the top of each column, caves that open to the sky included, which is far too
    /// aggressive to ship: it flattens overhangs and arches and seals every cave mouth in
    /// the world. It is here as a CEILING. The gap between the two numbers is the geometry
    /// that is buried but connected, and therefore the size of the prize that a real
    /// visibility test - rather than a connectivity test - would be competing for.
    /// </summary>
    static bool SubsurfaceMode;

    /// <summary>
    /// Measure the complete shipping integration: an original snapshot is handed to
    /// <see cref="LodMesher.BuildMesh"/> with cave culling enabled on the job. Pre-filling
    /// the snapshot here would test the culler and mesher separately and can conceal a
    /// disagreement between the post-cull geometry and the coverage view used for its sides.
    /// </summary>
    static bool Shipping;
    static bool ShippingSurfaceSight = true;

    /// <summary>Detail level to sample. Coarse levels mesh differently and must be checked too.</summary>
    static int SampleLevel;

    /// <summary>
    /// How far daylight reaches, in blocks of spreading, or 0 for the binary flood.
    ///
    /// Skylight IS a flood fill with a budget: it falls straight down an open column
    /// undimmed and loses a level for every step it takes sideways or upward into a cavity.
    /// So lighting a cave and flooding it are the same walk with a different stopping rule,
    /// and running it with a budget separates the two things plain connectivity cannot: a
    /// cave MOUTH, which daylight reaches, from the tunnel five hundred blocks behind it,
    /// which is connected and pitch black.
    ///
    /// Nothing needs to be stored for this. The game's own light values are not in the
    /// cache - capture reads block ids alone - but they do not have to be, because the rule
    /// that produces them is a property of the shape, and the shape is what we have.
    ///
    /// It also disposes of the neighbourhood-size problem. A binary flood has to be given a
    /// window big enough that its leaky edges do not reach the middle; a budgeted one only
    /// needs a margin as wide as the budget, because light from a false edge cannot travel
    /// further than that anyway.
    /// </summary>
    static int LightBudget;

    /// <summary>
    /// Keep any dark pocket that has more than one way in.
    ///
    /// The failure a budget alone cannot fix: a tunnel bored clean through a mountain,
    /// longer than twice the light's reach, goes dark in the middle and gets plugged - and
    /// what you lose is the daylight you could see through it, which is exactly the sort of
    /// thing somebody flies out to look at.
    ///
    /// A dead end touches daylight in ONE patch, the ring where the light petered out. A
    /// passage that goes somewhere touches it in TWO, one from each mouth, however long or
    /// winding it is. Even the worst case - a tunnel just over twice the budget, whose dark
    /// part is a single block of plug - has lit air on two opposite faces that do not touch
    /// each other. So the count of patches is the discriminator, and unlike a distance
    /// threshold there is nothing in it to tune wrong.
    ///
    /// Patches are grouped among the frontier cells themselves rather than through open air,
    /// which is the whole point: both mouths of a through-tunnel reach the same sky, so
    /// grouping through the sky would call them one patch and plug the tunnel anyway.
    /// </summary>
    static bool TwoWaysIn;

    /// <summary>
    /// Carry sight in straight lines instead of spreading it like light.
    ///
    /// Diffuse light turns corners; eyes do not. A winding tunnel is lit some way in by a
    /// real skylight model and is still unseeable, because seeing into it would need the
    /// view to bend. So propagating along fixed lattice directions - a cell inherits from
    /// the one cell behind it along each ray, and from nowhere else - models what can
    /// actually be LOOKED at rather than what would be illuminated.
    ///
    /// Conservative by direction count rather than by distance: a cell survives if ANY of
    /// the 26 directions reaches it, because the viewpoint is not known when a mesh is
    /// built and anything visible from anywhere has to be kept. More directions keep more.
    /// Twenty-six is the full lattice neighbourhood; a genuinely arbitrary view angle falls
    /// between them, which is the approximation to be aware of.
    /// </summary>
    static bool Straight;

    /// <summary>
    /// Surface-only visibility measurement: retain the conservative daylight flood and
    /// additionally retain unlimited straight sight from air above the top solid in each
    /// column. Unlike the historical <see cref="Straight"/> experiment, directions never
    /// share propagation state and every multi-cell step checks its intervening voxels.
    /// </summary>
    static bool SurfaceSight;

    /// <summary>
    /// Largest component of a sampled sight direction. 1 gives the 26 lattice neighbours;
    /// 2 adds the shallower angles between them, 98 in all.
    ///
    /// This is the approximation to keep honest. A real viewer can sit at ANY angle, and a
    /// cell is only invisible if EVERY direction is blocked - so sampling too few directions
    /// misses sight lines, calls visible geometry hidden, and removes something a player
    /// could see. Sampling more can only keep more. If the answer stops moving as the count
    /// rises, the coarse set was good enough; if it keeps falling, it was not, and the
    /// figure from the coarse one was never real.
    /// </summary>
    static int RayDetail = 1;

    static int Sections => Radius * 2 + 1;
    static int Span => Grid * Sections;

    sealed class Options
    {
        public string? Cache;
        public int Samples = 40;
        public int Seed = 20260825;
        public int Radius = 1;
        public bool Subsurface;
        public int Light;
        public bool TwoWaysIn;
        public bool Straight;
        public bool SurfaceSight;
        public int Rays = 1;
        public bool Verify;
        public bool Shipping;
        public bool NoShippingSurfaceSight;
        public bool Global;
        public int Tiles;
        public CaveFrontier Frontier = CaveFrontier.Open;
        public bool Sweep;
        public int Labels = 4;
        public bool SurfaceOnly;
        public int Level;
        public bool Help;
    }

    public static int Run(string[] args)
    {
        Options o;
        try { o = Parse(args); }
        catch (Exception e) { Console.Error.WriteLine("  " + e.Message); return 2; }
        if (o.Help) { PrintHelp(); return 0; }

        string? cache = o.Cache ?? NewestClientCache();
        if (cache == null || !File.Exists(cache))
        {
            Console.Error.WriteLine("  no cache database found - pass one with --cache <path>");
            return 2;
        }

        string copy = CopyForReading(cache);
        try
        {
            using var conn = new SqliteConnection($"Data Source={copy};Mode=ReadOnly");
            conn.Open();
            return Measure(conn, cache, o);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(copy)!, true); } catch { }
        }
    }

    static int Measure(SqliteConnection conn, string cacheName, Options o)
    {
        if (o.Global) return MeasureGlobal(conn, cacheName, o);

        Radius = Math.Max(1, o.Radius);
        SubsurfaceMode = o.Subsurface;
        Shipping = o.Shipping;
        ShippingSurfaceSight = !o.NoShippingSurfaceSight;
        SampleLevel = Math.Clamp(o.Level, 0, LodWorld.MaxLevel);
        LightBudget = Math.Max(0, o.Light);
        TwoWaysIn = o.TwoWaysIn;
        Straight = o.Straight;
        SurfaceSight = o.SurfaceSight;
        RayDetail = Math.Clamp(o.Rays, 1, 4);
        var sections = new CacheSections(conn);
        long[] candidates = KeysAtLevel(conn, SampleLevel);

        Console.WriteLine();
        Console.WriteLine("  VintageHorizons sealed-geometry field measurement");
        Console.WriteLine("  cache: " + cacheName);
        Console.WriteLine($"  {candidates.Length} level-{SampleLevel} sections in cache, sampling {o.Samples}");
        Console.WriteLine(SubsurfaceMode
            ? "  CEILING MODE: removing everything below each column top, cave mouths included"
            : LightBudget > 0
                ? $"  daylight reach {LightBudget} blocks, neighbourhood {Sections}x{Sections} "
                    + $"sections ({Span} blocks across)"
                : $"  flood neighbourhood {Sections}x{Sections} sections ({Span} blocks across)");
        if (SurfaceSight)
        {
            Console.WriteLine($"  surface flood plus exact-voxel straight sight, "
                + $"{DirectionCount()} directions");
        }
        if (Shipping && !ShippingSurfaceSight)
            Console.WriteLine("  SHIPPING BASELINE: surface-sight preservation disabled");
        else if (Straight)
        {
            Console.WriteLine($"  sight travels in straight lines only, {DirectionCount()} directions");
        }
        if (TwoWaysIn) Console.WriteLine("  keeping every dark pocket with more than one way in");
        if (LightBudget > 0 && LightBudget >= Grid * Radius)
        {
            Console.WriteLine($"  WARNING: a reach of {LightBudget} can cross the {Grid * Radius}-block "
                + "margin, so the window edge can light the measured section. Widen --radius.");
        }
        Console.WriteLine();

        if (candidates.Length == 0)
        {
            Console.WriteLine("  no level-0 sections to measure");
            return 0;
        }

        var rng = new Random(o.Seed);
        long[] chosen = candidates.OrderBy(_ => rng.Next()).Take(o.Samples).ToArray();
        int rebuildChecked = 0, rebuildDrifted = 0;

        long vertsBefore = 0, vertsAfter = 0, indicesBefore = 0, indicesAfter = 0;
        long sealedCells = 0, airCells = 0;
        double floorBefore = 0, floorAfter = 0, heightBefore = 0, heightAfter = 0;
        int measured = 0, floorRose = 0;
        var floorGains = new List<double>();

        foreach (long key in chosen)
        {
            Sample? sample = MeasureOne(key, sections, o.Verify, ref rebuildChecked, ref rebuildDrifted);
            if (sample == null) continue;

            measured++;
            vertsBefore += sample.VerticesBefore;
            vertsAfter += sample.VerticesAfter;
            indicesBefore += sample.IndicesBefore;
            indicesAfter += sample.IndicesAfter;
            sealedCells += sample.SealedCells;
            airCells += sample.AirCells;
            floorBefore += sample.FloorBefore;
            floorAfter += sample.FloorAfter;
            heightBefore += sample.HeightBefore;
            heightAfter += sample.HeightAfter;
            if (sample.FloorAfter > sample.FloorBefore + 0.5)
            {
                floorRose++;
                floorGains.Add(sample.FloorAfter - sample.FloorBefore);
            }
        }

        if (measured == 0)
        {
            Console.WriteLine("  no sampled section could be meshed");
            return 0;
        }

        Console.WriteLine($"  measured {measured} sections with their neighbours loaded");
        if (o.Verify)
        {
            Console.WriteLine();
            Console.WriteLine($"  REBUILD CHECK: {rebuildChecked - rebuildDrifted} of {rebuildChecked} "
                + "sections mesh identically when every column is rebuilt with nothing filled"
                + (rebuildDrifted == 0
                    ? " - the difference below is the fill and nothing else"
                    : " - THE REBUILD IS NOT NEUTRAL AND THE FIGURES BELOW ARE NOT TRUSTWORTHY"));
        }
        Console.WriteLine();
        Console.WriteLine("  GEOMETRY");
        Console.WriteLine($"    vertices   {vertsBefore,12:n0} -> {vertsAfter,12:n0}   "
            + $"{Share(vertsBefore - vertsAfter, vertsBefore):0.0}% is sealed inside rock");
        Console.WriteLine($"    indices    {indicesBefore,12:n0} -> {indicesAfter,12:n0}   "
            + $"{Share(indicesBefore - indicesAfter, indicesBefore):0.0}%");
        Console.WriteLine();
        Console.WriteLine("  BOX HEIGHTS");
        Console.WriteLine($"    mean floor    y={floorBefore / measured:0.0} -> y={floorAfter / measured:0.0}");
        Console.WriteLine($"    mean height   {heightBefore / measured:0.0} -> {heightAfter / measured:0.0} blocks");
        Console.WriteLine($"    {floorRose} of {measured} sections have a floor that rises"
            + (floorGains.Count > 0
                ? $", by {floorGains.Average():0} blocks on average, up to {floorGains.Max():0}"
                : ""));
        Console.WriteLine();
        Console.WriteLine("  AIR");
        Console.WriteLine($"    {Share(sealedCells, airCells):0.0}% of the air in these sections is sealed "
            + $"({sealedCells:n0} of {airCells:n0} cells)");
        Console.WriteLine();
        if (Shipping)
        {
            Console.WriteLine("  COLOUR AUDIT");
            Console.WriteLine($"    {survivingVertices:n0} vertices exist in both meshes, "
                + $"{recolouredVertices:n0} changed colour "
                + $"({Share(recolouredVertices, survivingVertices):0.00}%)");
            foreach (string line in recolourExamples) Console.WriteLine(line);
            Console.WriteLine();
            Console.WriteLine("  SEAM COST");
            Console.WriteLine($"    live integrated path:    {seamBefore,12:n0} vertices");
            Console.WriteLine($"    prefilled reference:     {seamAfter,12:n0} vertices");
            Console.WriteLine($"    live/reference delta:    "
                + $"{Share(seamBefore - seamAfter, seamBefore):0.0}%");
            Console.WriteLine();
            Console.WriteLine("  FILL AUDIT");
            Console.WriteLine($"    {fillSpans:n0} filled spans, {fillBlocks:n0} blocks, largest {biggestFill}");
            Console.WriteLine($"    {thinFills:n0} took a THIN material (drawn as a flat mat, no sides)");
            Console.WriteLine($"    {translucentFills:n0} took a WATER material (drawn in the water pass)");
            Console.WriteLine($"    {foreignFills:n0} took a material that column never contained");
            Console.WriteLine();
        }
        Console.WriteLine($"  Both figures are lower bounds: the flood escapes through the edge of the");
        Console.WriteLine($"  {Sections}x{Sections} neighbourhood and through uncaptured columns, so anything it could");
        Console.WriteLine($"  not prove enclosed was counted as visible. A wider --radius leaks less and");
        Console.WriteLine($"  therefore reports MORE sealed geometry, not less.");
        Console.WriteLine();
        return 0;
    }

    // =================================================================================
    // The ceiling measurement: what a GLOBAL classifier would remove, over the whole
    // cache, compared with what the shipping local rule removes on the same terrain.
    //
    // This exists to decide whether the runtime classifier is worth building at all. It
    // is deliberately not shippable - it reads every cached section at once and has no
    // budget, no invalidation and no idea what a mesh thread is.
    // =================================================================================

    sealed class GlobalTally
    {
        public readonly long[] Cells = new long[16];
        public readonly long[] Faces = new long[16];
        public readonly long[] BeyondLocalCells = new long[16];
        public readonly long[] BeyondLocalFaces = new long[16];
        // Cells the LOCAL rule filled, keyed by what the global prototype said about the
        // span they sit in. This is what turns the sanity line from a number into an
        // explanation: it names which global verdict is doing the disagreeing.
        public readonly long[] LocalByVerdict = new long[16];
        public long AirCells, AirFaces, WaterCells, WaterFaces;
        public long LocalCells, LocalFaces, LocalOnKeptCells;
        public int Sections;

        public void Absorb(GlobalTally other)
        {
            for (int i = 0; i < Cells.Length; i++)
            {
                Cells[i] += other.Cells[i];
                Faces[i] += other.Faces[i];
                BeyondLocalCells[i] += other.BeyondLocalCells[i];
                BeyondLocalFaces[i] += other.BeyondLocalFaces[i];
                LocalByVerdict[i] += other.LocalByVerdict[i];
            }
            AirCells += other.AirCells;
            AirFaces += other.AirFaces;
            WaterCells += other.WaterCells;
            WaterFaces += other.WaterFaces;
            LocalCells += other.LocalCells;
            LocalFaces += other.LocalFaces;
            LocalOnKeptCells += other.LocalOnKeptCells;
            Sections += other.Sections;
        }
    }

    static bool Removable(CaveSpanVerdict v) =>
        v is CaveSpanVerdict.RemovableSealed or CaveSpanVerdict.RemovableOverflow
            or CaveSpanVerdict.RemovableBranch or CaveSpanVerdict.WaterRemovable
            or CaveSpanVerdict.RemovableRouteTightened;

    /// <summary>
    /// How much of each air span the shipping local rule actually filled, indexed by span.
    ///
    /// This is the expensive half of the comparison - it runs `LodCaveCull` over every
    /// cached section - and it does not depend on the global rule's settings at all. A
    /// parameter sweep therefore pays for it once and re-tallies against it.
    /// </summary>
    static int[] MeasureLocalFill(
        Dictionary<(int Sx, int Sz), SectionSnapshot> sections,
        CaveSpanGraph graph, int level, int reach, out int sectionsDone)
    {
        var occupied = new int[graph.AirSpanCount];
        int done = 0;

        Parallel.ForEach(sections.Keys, at =>
        {
            SectionSnapshot self = sections[at];

            SectionSnapshot? Neighbour(int dx, int dz) =>
                sections.TryGetValue((at.Sx + dx, at.Sz + dz), out SectionSnapshot? s) ? s : null;

            var edges = new SectionSnapshot?[8];
            edges[0] = Neighbour(-1, 0);
            edges[1] = Neighbour(1, 0);
            edges[2] = Neighbour(0, -1);
            edges[3] = Neighbour(0, 1);
            edges[4] = Neighbour(-1, -1);
            edges[5] = Neighbour(1, -1);
            edges[6] = Neighbour(-1, 1);
            edges[7] = Neighbour(1, 1);

            SectionSnapshot filled = LodCaveCull.FillUnseen(self, edges, level, reach);
            Interlocked.Increment(ref done);

            for (int cz = 0; cz < Grid; cz++)
            {
                for (int cx = 0; cx < Grid; cx++)
                {
                    int col = LodSection.ColumnIndex(cx, cz);
                    if (!self.Captured[col]) continue;
                    Span<ulong> after = filled.ColumnRuns(col);

                    (int start, int end) = graph.AirSpanRange(at.Sx * Grid + cx, at.Sz * Grid + cz);
                    for (int s = start; s < end; s++)
                    {
                        int bottom = graph.AirSpanBottom(s);
                        int top = graph.AirSpanTop(s);
                        int filledCells = 0;
                        foreach (ulong run in after)
                        {
                            filledCells += Math.Max(0,
                                Math.Min(top, LodSection.RunYTop(run))
                                - Math.Max(bottom, LodSection.RunYBottom(run)));
                        }
                        // Each span belongs to exactly one section, so no two workers ever
                        // write the same entry.
                        occupied[s] = filledCells;
                    }
                }
            }
        });

        sectionsDone = done;
        return occupied;
    }

    /// <summary>
    /// One setting's answer, from span data alone. Cheap enough to run a dozen times.
    /// </summary>
    static GlobalTally TallySpans(CaveSpanGraph graph, int[] localOccupied, int sections)
    {
        var t = new GlobalTally { Sections = sections };

        for (int s = 0; s < graph.AirSpanCount; s++)
        {
            long cells = graph.AirSpanCells(s);
            if (cells <= 0) continue;
            long faces = graph.AirSpanFaces(s);
            long occupied = localOccupied[s];
            int v = (int)graph.AirSpanVerdict(s);

            t.AirCells += cells;
            t.AirFaces += faces;
            t.Cells[v] += cells;
            t.Faces[v] += faces;
            t.LocalCells += occupied;
            t.LocalFaces += faces * occupied / cells;
            t.LocalByVerdict[v] += occupied;

            if (Removable(graph.AirSpanVerdict(s)))
            {
                long beyond = cells - occupied;
                t.BeyondLocalCells[v] += beyond;
                t.BeyondLocalFaces[v] += faces * beyond / cells;
            }
            else t.LocalOnKeptCells += occupied;
        }

        for (int s = 0; s < graph.WaterSpanCount; s++)
        {
            long cells = graph.WaterSpanCells(s);
            if (cells <= 0) continue;
            long faces = graph.WaterSpanFaces(s);
            int v = (int)graph.WaterSpanVerdict(s);

            t.WaterCells += cells;
            t.WaterFaces += faces;
            t.Cells[v] += cells;
            t.Faces[v] += faces;
            // The local rule cannot touch fluid at all: capture stores it as an occupied
            // run, so the air flood never sees it as a cavity (G110).
            if (graph.WaterSpanVerdict(s) == CaveSpanVerdict.WaterRemovable)
            {
                t.BeyondLocalCells[v] += cells;
                t.BeyondLocalFaces[v] += faces;
            }
        }

        return t;
    }

    static int MeasureGlobal(SqliteConnection conn, string cacheName, Options o)
    {
        int level = Math.Clamp(o.Level, 0, LodWorld.MaxLevel);
        int reach = o.Light > 0 ? o.Light : LodCaveCull.DefaultReach;
        int localReach = LodCaveCull.DefaultReach;
        long[] keys = KeysAtLevel(conn, level);

        Console.WriteLine();
        Console.WriteLine("  VintageHorizons GLOBAL cave-classification ceiling");
        Console.WriteLine("  cache: " + cacheName);
        Console.WriteLine($"  {keys.Length:n0} level-{level} sections in cache");
        if (keys.Length == 0) { Console.WriteLine("  nothing to classify"); return 0; }

        var chosen = keys.ToList();
        if (o.Tiles > 0)
        {
            int minSx = keys.Min(LodWorld.KeySx), maxSx = keys.Max(LodWorld.KeySx);
            int minSz = keys.Min(LodWorld.KeySz), maxSz = keys.Max(LodWorld.KeySz);
            int midX = (minSx + maxSx) / 2, midZ = (minSz + maxSz) / 2;
            int half = o.Tiles / 2;
            chosen = keys.Where(k =>
                Math.Abs(LodWorld.KeySx(k) - midX) <= half
                && Math.Abs(LodWorld.KeySz(k) - midZ) <= half).ToList();
            Console.WriteLine($"  restricted to the {o.Tiles}x{o.Tiles} sections around "
                + $"{midX},{midZ}: {chosen.Count:n0} present");
        }
        Console.WriteLine($"  daylight reach {reach} blocks, column step "
            + $"{LodWorld.ColumnStepBlocks(level)} blocks");
        Console.WriteLine(o.Frontier == CaveFrontier.Open
            ? "  frontier OPEN: a component touching unknown space is kept whole"
            : "  frontier PORTAL: unknown contact is a pseudo-portal - it lights and it "
                + "terminates routes");
        Console.WriteLine();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var sections = new Dictionary<(int Sx, int Sz), SectionSnapshot>(chosen.Count);
        var loader = new CacheSections(conn);
        foreach (long key in chosen)
        {
            LodSection? s = loader.Get(key);
            if (s == null) continue;
            sections[(LodWorld.KeySx(key), LodWorld.KeySz(key))] = SectionSnapshot.Of(s);
            loader.Forget(key);
        }
        Console.WriteLine($"  decoded {sections.Count:n0} sections in {watch.Elapsed.TotalSeconds:0.0}s");

        watch.Restart();
        CaveSpanGraph graph = o.SurfaceOnly
            ? CaveSpanGraph.BuildSurfaceOnly(sections, level, reach, o.Frontier)
            : CaveSpanGraph.Build(sections, level, reach, o.Frontier, null, o.Labels);
        Console.WriteLine($"  span graph: {graph.AirSpanCount:n0} air spans, "
            + $"{graph.WaterSpanCount:n0} water spans, {graph.AirEdgeCount:n0} edges, "
            + $"over {graph.ColumnCount:n0} column slots, in {watch.Elapsed.TotalSeconds:0.0}s");
        Console.WriteLine($"  {graph.CapturedColumns:n0} captured columns, of which "
            + $"{graph.FrontierColumns:n0} ({Share(graph.FrontierColumns, graph.CapturedColumns):0.0}%) "
            + "sit beside something uncaptured");
        Console.WriteLine($"  {graph.AirComponents:n0} air components, "
            + $"{graph.UnknownAirComponents:n0} of them undecidable "
            + $"({Share(graph.UnknownAirComponents, graph.AirComponents):0.0}%); "
            + $"largest holds {graph.LargestComponentSpans:n0} spans");

        watch.Restart();
        int[] localOccupied = MeasureLocalFill(sections, graph, level, localReach, out int done);
        Console.WriteLine($"  local shipping rule (fixed reach {localReach}) run over "
            + $"{done:n0} sections in {watch.Elapsed.TotalSeconds:0.0}s");

        if (o.SurfaceOnly || !o.Sweep)
        {
            if (o.SurfaceOnly)
            {
                Console.WriteLine("  SURFACE ONLY: every span beyond the surface-light flood is "
                    + "removable; entrance-to-entrance routes carry no protection");
            }
            ReportGlobal(TallySpans(graph, localOccupied, done), level, o.Frontier);
            return 0;
        }

        Console.WriteLine($"  {graph.TerminalPairs:n0} mouth pairs carry a passage; "
            + $"each span is judged against its {graph.TerminalLabels} nearest mouths");
        Sweep(graph, localOccupied, done, reach);
        return 0;
    }

    /// <summary>
    /// The saving-versus-safety curve for route tightening, as one table.
    ///
    /// Every row is the same cache and the same local-rule comparison; only the route rule
    /// moves. That is the point: the owner is choosing a parameter, and a parameter is only
    /// legible next to its neighbours.
    /// </summary>
    static void Sweep(CaveSpanGraph graph, int[] localOccupied, int sections, int reach)
    {
        var settings = new List<CaveRouteRule> { CaveRouteRule.Bridge };
        foreach (int slack in new[] { 0, reach / 2, reach, reach * 2, reach * 4 })
        {
            settings.Add(new CaveRouteRule(slack, 0));
        }

        Console.WriteLine();
        Console.WriteLine("  ROUTE TIGHTENING SWEEP  (all figures are geometry, the face estimate)");
        Console.WriteLine();
        Console.WriteLine("    route rule          removable   route kept   additional   both     disagree");
        Console.WriteLine("                          total      remaining   over local   together  cells");
        Console.WriteLine("    ------------------  ---------   ----------   ----------   -------- --------");

        foreach (CaveRouteRule rule in settings)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            graph.ApplyRouteRule(rule);
            GlobalTally t = TallySpans(graph, localOccupied, sections);
            watch.Stop();

            long totalFaces = t.AirFaces + t.WaterFaces;
            long removable = 0, beyond = 0, routeKept = 0;
            for (int v = 0; v < t.Faces.Length; v++)
            {
                if (Removable((CaveSpanVerdict)v)) { removable += t.Faces[v]; beyond += t.BeyondLocalFaces[v]; }
            }
            routeKept = t.Faces[(int)CaveSpanVerdict.KeptRoute]
                + t.Faces[(int)CaveSpanVerdict.KeptRouteUnknown];

            Console.WriteLine($"    {rule,-18}  {Share(removable, totalFaces),8:0.0}%   "
                + $"{Share(routeKept, totalFaces),9:0.0}%   "
                + $"{Share(beyond, totalFaces),9:0.0}%   "
                + $"{Share(t.LocalFaces + beyond, totalFaces),7:0.0}%  "
                + $"{t.LocalOnKeptCells,8:n0}   ({watch.Elapsed.TotalSeconds:0.0}s)");
        }

        Console.WriteLine();
        Console.WriteLine("    'route kept remaining' is what the bridge rule still holds after the");
        Console.WriteLine("    tightening; 'disagree' is cells the local rule fills that this setting");
        Console.WriteLine("    keeps. Read the removable column as a curve and stop where it flattens.");
        Console.WriteLine();
        Console.WriteLine("    Route tightening is the ONE rule here that can remove geometry a player");
        Console.WriteLine("    could actually see: it decides that a passage is too indirect to look");
        Console.WriteLine("    through. Small W is aggressive. The sweep is the evidence,");
        Console.WriteLine("    not an endorsement of any row.");
        Console.WriteLine();
    }

    static void ReportGlobal(GlobalTally t, int level, CaveFrontier frontier)
    {
        long subterranean = t.AirCells + t.WaterCells;
        long subterraneanFaces = t.AirFaces + t.WaterFaces;

        long Cells(CaveSpanVerdict v) => t.Cells[(int)v];
        long Faces(CaveSpanVerdict v) => t.Faces[(int)v];
        long Beyond(CaveSpanVerdict v) => t.BeyondLocalCells[(int)v];
        long BeyondFaces(CaveSpanVerdict v) => t.BeyondLocalFaces[(int)v];

        // TWO shares, because they answer different questions and disagree wildly. Cells
        // say how much space this is; faces say how much GEOMETRY it costs, and geometry
        // is what the mod builds, uploads and draws. Bulk water is the extreme case: it
        // is most of the cells underground and almost none of the vertices, because a
        // flooded volume has a surface and no interior.
        void Row(string name, long cells, long faces) =>
            Console.WriteLine($"    {name,-34} {cells,15:n0} {Share(cells, subterranean),7:0.0}%"
                + $"   ~{faces * 4,15:n0} vtx {Share(faces, subterraneanFaces),7:0.0}%");

        long removableCells = 0, removableFaces = 0, beyondCells = 0, beyondFaces = 0;
        for (int v = 0; v < t.Faces.Length; v++)
        {
            if (!Removable((CaveSpanVerdict)v)) continue;
            removableCells += t.Cells[v];
            removableFaces += t.Faces[v];
            beyondCells += t.BeyondLocalCells[v];
            beyondFaces += t.BeyondLocalFaces[v];
        }

        Console.WriteLine();
        Console.WriteLine($"  EVERYTHING THE RULE COULD EVER TOUCH at L{level} ({t.Sections:n0} sections)");
        Row("cavity air below the surface", t.AirCells, t.AirFaces);
        Row("stored fluid, ocean included", t.WaterCells, t.WaterFaces);
        Row("TOTAL", subterranean, subterraneanFaces);
        Console.WriteLine();
        Console.WriteLine("  REMOVED BY THE SHIPPING LOCAL RULE (3x3 window, mesh time)");
        Row("filled", t.LocalCells, t.LocalFaces);
        Console.WriteLine();
        Console.WriteLine("  REMOVABLE BY THE GLOBAL SPAN-GRAPH PROTOTYPE");
        Row("sealed network, no portal", Cells(CaveSpanVerdict.RemovableSealed),
            Faces(CaveSpanVerdict.RemovableSealed));
        Row("single-portal overflow", Cells(CaveSpanVerdict.RemovableOverflow),
            Faces(CaveSpanVerdict.RemovableOverflow));
        Row("multi-portal branch peel", Cells(CaveSpanVerdict.RemovableBranch),
            Faces(CaveSpanVerdict.RemovableBranch));
        if (Cells(CaveSpanVerdict.RemovableRouteTightened) > 0)
        {
            Row("route tightened away", Cells(CaveSpanVerdict.RemovableRouteTightened),
                Faces(CaveSpanVerdict.RemovableRouteTightened));
        }
        Row("enclosed water", Cells(CaveSpanVerdict.WaterRemovable),
            Faces(CaveSpanVerdict.WaterRemovable));
        Row("TOTAL removable", removableCells, removableFaces);
        Console.WriteLine();
        Console.WriteLine("  KEPT BY THE GLOBAL PROTOTYPE");
        Row("lit by a real portal", Cells(CaveSpanVerdict.KeptLit), Faces(CaveSpanVerdict.KeptLit));
        Row("route between real portals", Cells(CaveSpanVerdict.KeptRoute), Faces(CaveSpanVerdict.KeptRoute));
        if (frontier == CaveFrontier.Open)
        {
            Row("unknown frontier, whole component",
                Cells(CaveSpanVerdict.KeptUnknown), Faces(CaveSpanVerdict.KeptUnknown));
        }
        else
        {
            // Kept only because of the frontier. Split out so the influence of unknown
            // space stays visible instead of being absorbed into the daylight line.
            Row("lit via a pseudo-portal", Cells(CaveSpanVerdict.KeptLitUnknown),
                Faces(CaveSpanVerdict.KeptLitUnknown));
            Row("route to a pseudo-portal", Cells(CaveSpanVerdict.KeptRouteUnknown),
                Faces(CaveSpanVerdict.KeptRouteUnknown));
        }
        Row("water kept", Cells(CaveSpanVerdict.WaterKept), Faces(CaveSpanVerdict.WaterKept));
        Console.WriteLine();
        Console.WriteLine("  THE HEADLINE: what the global rule would add");
        Row("sealed network", Beyond(CaveSpanVerdict.RemovableSealed),
            BeyondFaces(CaveSpanVerdict.RemovableSealed));
        Row("single-portal overflow", Beyond(CaveSpanVerdict.RemovableOverflow),
            BeyondFaces(CaveSpanVerdict.RemovableOverflow));
        Row("multi-portal branch peel", Beyond(CaveSpanVerdict.RemovableBranch),
            BeyondFaces(CaveSpanVerdict.RemovableBranch));
        Row("enclosed water", Beyond(CaveSpanVerdict.WaterRemovable),
            BeyondFaces(CaveSpanVerdict.WaterRemovable));
        Row("ADDITIONAL over the local rule", beyondCells, beyondFaces);
        Console.WriteLine();
        Console.WriteLine("                        cells                geometry (face estimate)");
        Console.WriteLine($"    local rule alone    {Share(t.LocalCells, subterranean),5:0.0}%"
            + $"                {Share(t.LocalFaces, subterraneanFaces),5:0.0}%");
        Console.WriteLine($"    global rule alone   {Share(removableCells, subterranean),5:0.0}%"
            + $"                {Share(removableFaces, subterraneanFaces),5:0.0}%");
        Console.WriteLine($"    both together       {Share(t.LocalCells + beyondCells, subterranean),5:0.0}%"
            + $"                {Share(t.LocalFaces + beyondFaces, subterraneanFaces),5:0.0}%");
        Console.WriteLine();
        Console.WriteLine("    The geometry column is the one that matters: it is what gets built,");
        Console.WriteLine("    uploaded and drawn. The cell column is distorted by bulk fluid, which is");
        Console.WriteLine("    most of the volume underground and almost none of the vertices.");
        Console.WriteLine();
        Console.WriteLine($"    SANITY: {t.LocalOnKeptCells:n0} cells the local rule filled sit in spans");
        Console.WriteLine("    the global rule keeps. The two rules are not nested, and this says which");
        Console.WriteLine("    global verdict is doing the disagreeing:");
        foreach (CaveSpanVerdict v in new[]
                 {
                     CaveSpanVerdict.KeptLit, CaveSpanVerdict.KeptRoute,
                     CaveSpanVerdict.KeptLitUnknown, CaveSpanVerdict.KeptRouteUnknown,
                     CaveSpanVerdict.KeptUnknown, CaveSpanVerdict.WaterKept,
                 })
        {
            long cells = t.LocalByVerdict[(int)v];
            if (cells == 0) continue;
            Console.WriteLine($"      {v,-20} {cells,14:n0} cells "
                + $"{Share(cells, t.LocalOnKeptCells),6:0.0}% of the disagreement");
        }
        Console.WriteLine();
        Console.WriteLine("  These are CELL counts and a face-area estimate, not measured vertices. The");
        Console.WriteLine("  mesher merges faces greedily, so vertices x 4 is an upper bound. The light");
        Console.WriteLine("  model does not charge for climbing, so it lights more than the live flood and");
        Console.WriteLine("  the additional removal above is a floor rather than an optimistic estimate.");
        Console.WriteLine();
    }

    sealed record Sample(
        long VerticesBefore, long VerticesAfter, long IndicesBefore, long IndicesAfter,
        double FloorBefore, double FloorAfter, double HeightBefore, double HeightAfter,
        long SealedCells, long AirCells);

    static Sample? MeasureOne(long key, CacheSections sections,
        bool verify, ref int rebuildChecked, ref int rebuildDrifted)
    {
        LodSection? self = sections.Get(key);
        if (self == null) return null;

        // The 3x3 the flood runs over, indexed [dz + 1, dx + 1].
        var around = new LodSection?[Sections, Sections];
        for (int dz = -Radius; dz <= Radius; dz++)
        {
            for (int dx = -Radius; dx <= Radius; dx++)
            {
                around[dz + Radius, dx + Radius] = sections.Get(LodWorld.NeighborKey(key, dx, dz));
            }
        }

        int worldHeight = WorldHeightOf(around);
        if (worldHeight <= 2) return null;

        bool[] solid = BuildSolid(around, worldHeight);
        bool[] outside = FloodOutsideAir(solid, worldHeight);

        long sealedCells = 0, airCells = 0;
        for (int i = 0; i < solid.Length; i++)
        {
            if (solid[i]) continue;
            airCells++;
            if (!outside[i]) sealedCells++;
        }

        MeshResult before = Mesh(key, around, null);

        if (Shipping)
        {
            var direct = new Dictionary<(int dx, int dz), SectionSnapshot>();
            LodSection? centre = around[Radius, Radius];
            if (centre == null) return null;

            // Eight, matching what a real mesh job now carries: four edges for side-face
            // culling and four corners so light cannot pour in through an assumed-open one.
            var edges = new SectionSnapshot?[8];
            edges[0] = Snap(around, -1, 0);
            edges[1] = Snap(around, 1, 0);
            edges[2] = Snap(around, 0, -1);
            edges[3] = Snap(around, 0, 1);
            edges[4] = Snap(around, -1, -1);
            edges[5] = Snap(around, 1, -1);
            edges[6] = Snap(around, -1, 1);
            edges[7] = Snap(around, 1, 1);

            SectionSnapshot filledSelf = LodCaveCull.FillUnseen(
                SectionSnapshot.Of(centre), edges, LodWorld.KeyLevel(key),
                LightBudget > 0 ? LightBudget : LodCaveCull.DefaultReach,
                ShippingSurfaceSight);

            direct[(0, 0)] = filledSelf;
            int shippingReach = LightBudget > 0 ? LightBudget : LodCaveCull.DefaultReach;
            MeshResult? shipped = ShippingSurfaceSight
                ? Mesh(key, around, null, shippingReach)
                : null;

            // Independent reference: pre-fill the same section and its edge neighbours,
            // then mesh with cave culling disabled. The live path should closely match this
            // without paying four additional flood passes per section, because it obtains
            // neighbour-edge coverage from the centre pass's shared 3x3 classification.
            var alsoNeighbours = new Dictionary<(int dx, int dz), SectionSnapshot>(direct);
            foreach ((int dx, int dz) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
            {
                LodSection? nb = around[dz + Radius, dx + Radius];
                if (nb == null) continue;
                var nbEdges = new SectionSnapshot?[8];
                nbEdges[0] = Snap(around, dx - 1, dz);
                nbEdges[1] = Snap(around, dx + 1, dz);
                nbEdges[2] = Snap(around, dx, dz - 1);
                nbEdges[3] = Snap(around, dx, dz + 1);
                nbEdges[4] = Snap(around, dx - 1, dz - 1);
                nbEdges[5] = Snap(around, dx + 1, dz - 1);
                nbEdges[6] = Snap(around, dx - 1, dz + 1);
                nbEdges[7] = Snap(around, dx + 1, dz + 1);
                alsoNeighbours[(dx, dz)] = LodCaveCull.FillUnseen(
                    SectionSnapshot.Of(nb), nbEdges, LodWorld.KeyLevel(key),
                    LightBudget > 0 ? LightBudget : LodCaveCull.DefaultReach,
                    ShippingSurfaceSight);
            }
            MeshResult bothSides = Mesh(key, around, alsoNeighbours);
            shipped ??= bothSides;
            seamBefore += shipped.VertexCount;
            seamAfter += bothSides.VertexCount;

            ReportPalettes(key, centre, filledSelf);
            AuditFill(centre, filledSelf);
            AuditColours(before, shipped);

            LodHeightSpan shippedSpan = shipped.Heights.Either;
            LodHeightSpan baseSpan = before.Heights.Either;
            if (!baseSpan.HasGeometry) return null;
            return new Sample(
                before.VertexCount, shipped.VertexCount,
                before.IndexCount, shipped.IndexCount,
                baseSpan.MinY, shippedSpan.HasGeometry ? shippedSpan.MinY : baseSpan.MinY,
                baseSpan.Height, shippedSpan.HasGeometry ? shippedSpan.Height : baseSpan.Height,
                0, 0);
        }

        // Filled snapshots for the centre AND its neighbours: a centre whose caves are
        // filled against neighbours whose caves are not would grow a wall along every shared
        // edge, and that wall is an artefact of measuring rather than of the change.
        var filled = new Dictionary<(int dx, int dz), SectionSnapshot>();
        for (int dz = -Radius; dz <= Radius; dz++)
        {
            for (int dx = -Radius; dx <= Radius; dx++)
            {
                LodSection? s = around[dz + Radius, dx + Radius];
                if (s == null) continue;
                filled[(dx, dz)] = FillSealed(s, solid, outside, worldHeight, dx, dz);
            }
        }

        // The fill and the rebuild are two different changes sharing one code path, and only
        // one of them is meant to remove geometry. Rebuilding every column while sealing
        // NOTHING isolates the other: if that mesh differs from the original, the run
        // rewriting is not neutral and any saving below is partly an artefact of it.
        if (verify)
        {
            var neutral = new Dictionary<(int dx, int dz), SectionSnapshot>();
            var allOpen = new bool[outside.Length];
            for (int i = 0; i < allOpen.Length; i++) allOpen[i] = true;
            for (int dz = -Radius; dz <= Radius; dz++)
            {
                for (int dx = -Radius; dx <= Radius; dx++)
                {
                    LodSection? s = around[dz + Radius, dx + Radius];
                    if (s == null) continue;
                    neutral[(dx, dz)] = FillSealed(s, solid, allOpen, worldHeight, dx, dz, forceRebuild: true);
                }
            }
            MeshResult rebuilt = Mesh(key, around, neutral);
            rebuildChecked++;
            if (rebuilt.VertexCount != before.VertexCount || rebuilt.IndexCount != before.IndexCount)
            {
                rebuildDrifted++;
            }
        }

        MeshResult after = Mesh(key, around, filled);

        LodHeightSpan spanBefore = before.Heights.Either;
        LodHeightSpan spanAfter = after.Heights.Either;
        if (!spanBefore.HasGeometry) return null;

        return new Sample(
            before.VertexCount, after.VertexCount,
            before.IndexCount, after.IndexCount,
            spanBefore.MinY, spanAfter.HasGeometry ? spanAfter.MinY : spanBefore.MinY,
            spanBefore.Height, spanAfter.HasGeometry ? spanAfter.Height : spanBefore.Height,
            sealedCells, airCells);
    }

    static SectionSnapshot? Snap(LodSection?[,] around, int dx, int dz)
    {
        // A neighbour's own neighbour can fall outside the loaded window; absent is the
        // honest answer there and the rule treats it as open, which keeps geometry.
        if (dx < -Radius || dx > Radius || dz < -Radius || dz > Radius) return null;
        LodSection? s = around[dz + Radius, dx + Radius];
        return s == null ? null : SectionSnapshot.Of(s);
    }

    static int palettesReported;
    static long seamBefore, seamAfter;
    static long survivingVertices, recolouredVertices;
    static readonly List<string> recolourExamples = new();

    /// <summary>
    /// Whether a surface that SURVIVES the cull is drawn in the same colour it was before.
    ///
    /// The fill audit only proves the rule handed sensible materials to rock it invented. It
    /// says nothing about the geometry that stays, and "the caves turned white" is a
    /// complaint about exactly that: a face still there, in the wrong colour. Comparing the
    /// two meshes vertex by vertex is the only thing that can tell those apart, because a
    /// palette id can be preserved while the face that reads it is not the face it was.
    /// </summary>
    static void AuditColours(MeshResult before, MeshResult after)
    {
        var was = new Dictionary<(float, float, float), uint>();
        for (int v = 0; v < before.VertexCount; v++)
        {
            var at = (before.Xyz[v * 3], before.Xyz[v * 3 + 1], before.Xyz[v * 3 + 2]);
            was[at] = Colour(before.Rgba, v);
        }

        for (int v = 0; v < after.VertexCount; v++)
        {
            var at = (after.Xyz[v * 3], after.Xyz[v * 3 + 1], after.Xyz[v * 3 + 2]);
            if (!was.TryGetValue(at, out uint had)) continue;

            survivingVertices++;
            uint now = Colour(after.Rgba, v);
            if (now == had) continue;

            recolouredVertices++;
            if (recolourExamples.Count < 5)
            {
                recolourExamples.Add($"      ({at.Item1:0.#},{at.Item2:0.#},{at.Item3:0.#}) "
                    + $"{had:X8} -> {now:X8}");
            }
        }
    }

    static uint Colour(byte[] rgba, int vertex) =>
        (uint)rgba[vertex * 4] | ((uint)rgba[vertex * 4 + 1] << 8)
        | ((uint)rgba[vertex * 4 + 2] << 16) | ((uint)rgba[vertex * 4 + 3] << 24);
    static long fillSpans, fillBlocks, thinFills, translucentFills, foreignFills, biggestFill;

    /// <summary>
    /// What material the rule handed to the rock it invented.
    ///
    /// A filled cell inherits from its neighbour above or below, and the mesher branches on
    /// palette FLAGS rather than on colour: a thin id is drawn as a flat mat with no sides at
    /// all, and a translucent one is emitted into the water pass. Inheriting either does not
    /// merely miscolour a wall - it changes which geometry gets built and where it is drawn,
    /// which is what somebody would report as terrain looking wrong rather than terrain
    /// disappearing.
    /// </summary>
    static void AuditFill(LodSection before, SectionSnapshot after)
    {
        int columns = LodSection.GridSize * LodSection.GridSize;
        for (int col = 0; col < columns; col++)
        {
            if (!before.Captured[col]) continue;
            Span<ulong> was = before.ColumnRuns(col);
            Span<ulong> now = after.ColumnRuns(col);
            if (was.Length == 0 || now.Length == 0) continue;

            var wasSolid = new HashSet<int>();
            var originalPalettes = new HashSet<int>();
            foreach (ulong r in was)
            {
                originalPalettes.Add(LodSection.RunPaletteId(r));
                for (int y = LodSection.RunYBottom(r); y < LodSection.RunYTop(r); y++) wasSolid.Add(y);
            }

            foreach (ulong r in now)
            {
                int pid = LodSection.RunPaletteId(r);
                int span = 0;
                for (int y = LodSection.RunYBottom(r); y < LodSection.RunYTop(r); y++)
                {
                    if (!wasSolid.Contains(y)) span++;
                }
                if (span == 0) continue;

                fillSpans++;
                fillBlocks += span;
                if (span > biggestFill) biggestFill = span;
                if (!originalPalettes.Contains(pid)) foreignFills++;
                if (pid >= 0 && pid < after.PaletteFlags.Length)
                {
                    byte flags = after.PaletteFlags[pid];
                    if ((flags & LodPaletteEntry.FlagThin) != 0) thinFills++;
                    if ((flags & LodPaletteEntry.FlagWater) != 0) translucentFills++;
                }
            }
        }
    }

    /// <summary>
    /// The first few columns the rule rewrote, before and after, because "it turned white"
    /// is a palette symptom and a run listing is the only thing that can tell a wrong
    /// material apart from a run that failed to merge.
    /// </summary>
    static void ReportPalettes(long key, LodSection before, SectionSnapshot after)
    {
        if (palettesReported >= 3) return;

        int columns = LodSection.GridSize * LodSection.GridSize;
        for (int col = 0; col < columns && palettesReported < 3; col++)
        {
            if (!before.Captured[col]) continue;
            Span<ulong> was = before.ColumnRuns(col);
            Span<ulong> now = after.ColumnRuns(col);
            if (was.Length == now.Length) continue;

            palettesReported++;
            Console.WriteLine($"    column {col} of L{LodWorld.KeyLevel(key)} "
                + $"{LodWorld.KeySx(key)},{LodWorld.KeySz(key)}: "
                + $"{was.Length} runs -> {now.Length}");
            Console.Write("      was:");
            foreach (ulong r in was)
            {
                Console.Write($" [p{LodSection.RunPaletteId(r)} {LodSection.RunYBottom(r)}-{LodSection.RunYTop(r)}]");
            }
            Console.WriteLine();
            Console.Write("      now:");
            foreach (ulong r in now)
            {
                Console.Write($" [p{LodSection.RunPaletteId(r)} {LodSection.RunYBottom(r)}-{LodSection.RunYTop(r)}]");
            }
            Console.WriteLine();
        }
    }

    static MeshResult Mesh(long key, LodSection?[,] around,
        Dictionary<(int dx, int dz), SectionSnapshot>? filled, int caveCullReach = 0)
    {
        SectionSnapshot? Snapshot(int dx, int dz)
        {
            if (filled != null && filled.TryGetValue((dx, dz), out SectionSnapshot? swap)) return swap;
            LodSection? s = around[dz + Radius, dx + Radius];
            return s == null ? null : SectionSnapshot.Of(s);
        }

        var neighbors = new SectionSnapshot?[8];
        neighbors[0] = Snapshot(-1, 0);
        neighbors[1] = Snapshot(1, 0);
        neighbors[2] = Snapshot(0, -1);
        neighbors[3] = Snapshot(0, 1);
        neighbors[4] = Snapshot(-1, -1);
        neighbors[5] = Snapshot(1, -1);
        neighbors[6] = Snapshot(-1, 1);
        neighbors[7] = Snapshot(1, 1);

        return LodMesher.BuildMesh(new MeshJob
        {
            Key = key,
            Self = Snapshot(0, 0)!,
            Neighbors = neighbors,
            // Every neighbour present in the cache was supplied, so an absent side really is
            // the frontier and nothing is assumed covered.
            AssumedCoveredSides = 0,
            CaveCullReach = caveCullReach,
        });
    }

    // ---- occupancy and flood ----

    static int Index(int x, int z, int y, int worldHeight) => (z * Span + x) * worldHeight + y;

    static int WorldHeightOf(LodSection?[,] around)
    {
        int top = 0;
        foreach (LodSection? s in around)
        {
            if (s == null) continue;
            for (int col = 0; col < Grid * Grid; col++)
            {
                if (!s.Captured[col]) continue;
                Span<ulong> runs = s.ColumnRuns(col);
                if (runs.Length > 0) top = Math.Max(top, LodSection.RunYTop(runs[0]));
            }
        }
        // One clear layer of sky above the highest block, which is where the flood starts.
        return top + 2;
    }

    static bool[] BuildSolid(LodSection?[,] around, int worldHeight)
    {
        var solid = new bool[Span * Span * worldHeight];
        for (int dz = -Radius; dz <= Radius; dz++)
        {
            for (int dx = -Radius; dx <= Radius; dx++)
            {
                LodSection? s = around[dz + Radius, dx + Radius];
                if (s == null) continue;              // absent: all air, so the flood leaks in

                int baseX = (dx + Radius) * Grid;
                int baseZ = (dz + Radius) * Grid;
                for (int cz = 0; cz < Grid; cz++)
                {
                    for (int cx = 0; cx < Grid; cx++)
                    {
                        int col = LodSection.ColumnIndex(cx, cz);
                        if (!s.Captured[col]) continue;   // unknown: left as air, deliberately
                        foreach (ulong run in s.ColumnRuns(col))
                        {
                            int yTop = LodSection.RunYTop(run);
                            int yBottom = Math.Max(0, LodSection.RunYBottom(run));
                            for (int y = yBottom; y < yTop && y < worldHeight; y++)
                            {
                                solid[Index(baseX + cx, baseZ + cz, y, worldHeight)] = true;
                            }
                        }
                    }
                }
            }
        }
        return solid;
    }

    /// <summary>
    /// Air reachable from the sky or from outside the neighbourhood. Six-way, with an
    /// explicit stack: the reach of a cave system is not something to hand to the call stack.
    /// </summary>
    static bool[] FloodOutsideAir(bool[] solid, int worldHeight)
    {
        bool[] open = LightBudget <= 0
            ? ReachableAir(solid, worldHeight)
            : SurfaceSight ? SurfaceVisibleAir(solid, worldHeight)
            : Straight ? SightlineAir(solid, worldHeight) : LitAir(solid, worldHeight);
        return TwoWaysIn ? KeepPassages(solid, open, worldHeight) : open;
    }

    /// <summary>
    /// Promote every dark pocket with two or more separate ways in back to "keep". See
    /// <see cref="TwoWaysIn"/> for why the count of ways in is the right question.
    /// </summary>
    static bool[] KeepPassages(bool[] solid, bool[] open, int worldHeight)
    {
        var result = (bool[])open.Clone();
        var visited = new bool[solid.Length];
        var pocket = new List<int>();
        var frontier = new List<int>();
        var stack = new Stack<int>();

        for (int start = 0; start < solid.Length; start++)
        {
            if (solid[start] || open[start] || visited[start]) continue;

            pocket.Clear();
            frontier.Clear();
            visited[start] = true;
            stack.Push(start);

            while (stack.Count > 0)
            {
                int i = stack.Pop();
                pocket.Add(i);
                (int x, int z, int y) = Unpack(i, worldHeight);

                foreach ((int nx, int nz, int ny) in Sides(x, z, y))
                {
                    if (nx < 0 || nx >= Span || nz < 0 || nz >= Span
                        || ny < 0 || ny >= worldHeight) continue;
                    int n = Index(nx, nz, ny, worldHeight);
                    if (solid[n]) continue;
                    if (open[n]) { frontier.Add(n); continue; }
                    if (visited[n]) continue;
                    visited[n] = true;
                    stack.Push(n);
                }
            }

            if (CountPatches(frontier, worldHeight) < 2) continue;
            foreach (int i in pocket) result[i] = true;
        }

        return result;
    }

    /// <summary>
    /// Connected groups among the frontier cells, touching on any of the 26 directions so a
    /// merely ragged opening is not mistaken for two of them.
    /// </summary>
    static int CountPatches(List<int> frontier, int worldHeight)
    {
        if (frontier.Count == 0) return 0;

        var members = new HashSet<int>(frontier);
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        int patches = 0;

        foreach (int start in frontier)
        {
            if (!seen.Add(start)) continue;
            patches++;
            if (patches >= 2) return patches;

            stack.Push(start);
            while (stack.Count > 0)
            {
                (int x, int z, int y) = Unpack(stack.Pop(), worldHeight);
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0 && dz == 0) continue;
                            int nx = x + dx, nz = z + dz, ny = y + dy;
                            if (nx < 0 || nx >= Span || nz < 0 || nz >= Span
                                || ny < 0 || ny >= worldHeight) continue;
                            int n = Index(nx, nz, ny, worldHeight);
                            if (!members.Contains(n) || !seen.Add(n)) continue;
                            stack.Push(n);
                        }
                    }
                }
            }
        }
        return patches;
    }

    static IEnumerable<(int X, int Z, int Y)> Sides(int x, int z, int y)
    {
        yield return (x, z, y - 1);
        yield return (x, z, y + 1);
        yield return (x - 1, z, y);
        yield return (x + 1, z, y);
        yield return (x, z - 1, y);
        yield return (x, z + 1, y);
    }

    /// <summary>
    /// Air reachable from the sky or from outside the neighbourhood, at any distance. Six-way,
    /// with an explicit stack: the reach of a cave system is not something to hand to the call
    /// stack.
    /// </summary>
    static bool[] ReachableAir(bool[] solid, int worldHeight)
    {
        var outside = new bool[solid.Length];
        var stack = new Stack<int>();

        void Push(int x, int z, int y)
        {
            if (x < 0 || x >= Span || z < 0 || z >= Span || y < 0 || y >= worldHeight) return;
            int i = Index(x, z, y, worldHeight);
            if (solid[i] || outside[i]) return;
            outside[i] = true;
            stack.Push(i);
        }

        SeedBoundary(Push, worldHeight);

        while (stack.Count > 0)
        {
            int i = stack.Pop();
            (int x, int z, int y) = Unpack(i, worldHeight);
            Push(x, z, y - 1);
            Push(x, z, y + 1);
            Push(x - 1, z, y);
            Push(x + 1, z, y);
            Push(x, z - 1, y);
            Push(x, z + 1, y);
        }

        return outside;
    }

    /// <summary>
    /// Air that daylight actually reaches, which is the same walk under a budget.
    ///
    /// Full strength falls straight down an open column for nothing, the way sunlight does;
    /// every other step costs one. Processing brightest-first makes each cell settle at its
    /// true value on first visit, so the walk never revisits: the free downward step keeps a
    /// cell in the bucket being drained, and every dimmer step lands in the next one.
    /// </summary>
    static bool[] LitAir(bool[] solid, int worldHeight)
    {
        var light = new byte[solid.Length];
        var buckets = new List<int>[LightBudget + 1];
        for (int v = 0; v <= LightBudget; v++) buckets[v] = new List<int>();

        void Seed(int x, int z, int y)
        {
            if (x < 0 || x >= Span || z < 0 || z >= Span || y < 0 || y >= worldHeight) return;
            int i = Index(x, z, y, worldHeight);
            if (solid[i] || light[i] >= LightBudget) return;
            light[i] = (byte)LightBudget;
            buckets[LightBudget].Add(i);
        }

        SeedBoundary(Seed, worldHeight);

        for (int level = LightBudget; level >= 1; level--)
        {
            List<int> bucket = buckets[level];

            // Indexed rather than foreach: the free downward step appends to this same
            // bucket while it is being drained, and those cells must be drained too.
            for (int at = 0; at < bucket.Count; at++)
            {
                int i = bucket[at];
                if (light[i] != level) continue;      // already superseded by a brighter path
                (int x, int z, int y) = Unpack(i, worldHeight);

                if (level == LightBudget) Spread(x, z, y - 1, LightBudget);
                else Spread(x, z, y - 1, level - 1);

                Spread(x, z, y + 1, level - 1);
                Spread(x - 1, z, y, level - 1);
                Spread(x + 1, z, y, level - 1);
                Spread(x, z - 1, y, level - 1);
                Spread(x, z + 1, y, level - 1);
            }
        }

        void Spread(int x, int z, int y, int value)
        {
            if (value <= 0) return;
            if (x < 0 || x >= Span || z < 0 || z >= Span || y < 0 || y >= worldHeight) return;
            int i = Index(x, z, y, worldHeight);
            if (solid[i] || light[i] >= value) return;
            light[i] = (byte)value;
            buckets[value].Add(i);
        }

        var lit = new bool[solid.Length];
        for (int i = 0; i < lit.Length; i++) lit[i] = light[i] > 0;
        return lit;
    }

    /// <summary>
    /// Air a straight ray can reach from outside, along any of the 26 lattice directions.
    ///
    /// One sweep per direction, each in an order that visits a cell after the cell behind it
    /// along that ray, so a single pass carries every ray in that direction to its full
    /// length. No queue and no revisiting: sight either continues straight or stops.
    /// </summary>
    static bool[] SightlineAir(bool[] solid, int worldHeight)
    {
        var reach = new byte[solid.Length];

        void Seed(int x, int z, int y)
        {
            if (x < 0 || x >= Span || z < 0 || z >= Span || y < 0 || y >= worldHeight) return;
            int i = Index(x, z, y, worldHeight);
            if (!solid[i]) reach[i] = (byte)LightBudget;
        }
        SeedBoundary(Seed, worldHeight);

        foreach ((int dx, int dz, int dy) in Directions()) Sweep(solid, reach, worldHeight, dx, dz, dy);

        var seen = new bool[solid.Length];
        for (int i = 0; i < seen.Length; i++) seen[i] = reach[i] > 0;
        return seen;
    }

    /// <summary>
    /// Conservative surface visibility: everything reached by the diffuse light budget,
    /// plus unlimited straight rays beginning in air above each column's highest solid.
    ///
    /// Each direction owns an independent visibility array. Sharing one array lets a ray
    /// propagated by one direction turn when the next sweep consumes it, which is not a
    /// straight line. Multi-column directions use <see cref="RayInterior"/> so they cannot
    /// jump a thin wall. The direction sets are nested, therefore increasing --rays can
    /// only keep geometry; a reported curve must flatten before it is trusted.
    /// </summary>
    static bool[] SurfaceVisibleAir(bool[] solid, int worldHeight)
    {
        bool[] seen = LitAir(solid, worldHeight);
        var surfaceSeed = new bool[solid.Length];

        for (int z = 0; z < Span; z++)
        {
            for (int x = 0; x < Span; x++)
            {
                int top = 0;
                for (int y = worldHeight - 1; y >= 0; y--)
                {
                    if (!solid[Index(x, z, y, worldHeight)]) continue;
                    top = y + 1;
                    break;
                }
                for (int y = top; y < worldHeight; y++)
                {
                    int i = Index(x, z, y, worldHeight);
                    surfaceSeed[i] = true;
                    seen[i] = true;
                }
            }
        }

        var visible = new bool[solid.Length];
        foreach ((int dx, int dz, int dy) in Directions())
        {
            Array.Copy(surfaceSeed, visible, surfaceSeed.Length);
            (int X, int Z, int Y)[] interior = RayInterior(dx, dz, dy);
            SweepSurfaceRay(solid, visible, seen, worldHeight, dx, dz, dy, interior);
        }
        return seen;
    }

    /// <summary>
    /// Cells crossed between the centres of (0,0,0) and one lattice-direction step,
    /// excluding both endpoints. This is a 3D DDA: longer primitive vectors visit every
    /// intervening voxel instead of teleporting sight across it.
    /// </summary>
    internal static (int X, int Z, int Y)[] RayInterior(int dx, int dz, int dy)
    {
        var cells = new List<(int X, int Z, int Y)>();
        int x = 0, z = 0, y = 0;
        int sx = Math.Sign(dx), sz = Math.Sign(dz), sy = Math.Sign(dy);
        double tx = dx == 0 ? double.PositiveInfinity : 0.5 / Math.Abs(dx);
        double tz = dz == 0 ? double.PositiveInfinity : 0.5 / Math.Abs(dz);
        double ty = dy == 0 ? double.PositiveInfinity : 0.5 / Math.Abs(dy);
        double dtx = dx == 0 ? double.PositiveInfinity : 1.0 / Math.Abs(dx);
        double dtz = dz == 0 ? double.PositiveInfinity : 1.0 / Math.Abs(dz);
        double dty = dy == 0 ? double.PositiveInfinity : 1.0 / Math.Abs(dy);

        while (x != dx || z != dz || y != dy)
        {
            double next = Math.Min(tx, Math.Min(tz, ty));
            const double epsilon = 1e-12;
            if (tx <= next + epsilon) { x += sx; tx += dtx; }
            if (tz <= next + epsilon) { z += sz; tz += dtz; }
            if (ty <= next + epsilon) { y += sy; ty += dty; }
            if (x != dx || z != dz || y != dy) cells.Add((x, z, y));
        }
        return cells.ToArray();
    }

    static void SweepSurfaceRay(bool[] solid, bool[] visible, bool[] seen, int worldHeight,
        int dx, int dz, int dy, (int X, int Z, int Y)[] interior)
    {
        int xFrom = dx > 0 ? 0 : Span - 1, xTo = dx > 0 ? Span : -1, xStep = dx > 0 ? 1 : -1;
        int zFrom = dz > 0 ? 0 : Span - 1, zTo = dz > 0 ? Span : -1, zStep = dz > 0 ? 1 : -1;
        int yFrom = dy > 0 ? 0 : worldHeight - 1, yTo = dy > 0 ? worldHeight : -1, yStep = dy > 0 ? 1 : -1;

        for (int z = zFrom; z != zTo; z += zStep)
        {
            for (int x = xFrom; x != xTo; x += xStep)
            {
                for (int y = yFrom; y != yTo; y += yStep)
                {
                    int i = Index(x, z, y, worldHeight);
                    if (solid[i] || visible[i]) continue;

                    int px = x - dx, pz = z - dz, py = y - dy;
                    if (px < 0 || px >= Span || pz < 0 || pz >= Span
                        || py < 0 || py >= worldHeight
                        || !visible[Index(px, pz, py, worldHeight)]) continue;

                    bool clear = true;
                    foreach ((int ox, int oz, int oy) in interior)
                    {
                        int ix = px + ox, iz = pz + oz, iy = py + oy;
                        if (solid[Index(ix, iz, iy, worldHeight)]) { clear = false; break; }
                    }
                    if (!clear) continue;
                    visible[i] = true;
                    seen[i] = true;
                }
            }
        }
    }

    /// <summary>
    /// Primitive lattice directions up to <see cref="RayDetail"/>. Primitive because a
    /// doubled vector traces the same line through coarser steps, which would let sight
    /// jump a one-block wall rather than be stopped by it.
    /// </summary>
    static IEnumerable<(int X, int Z, int Y)> Directions()
    {
        int n = RayDetail;
        for (int dy = -n; dy <= n; dy++)
        {
            for (int dz = -n; dz <= n; dz++)
            {
                for (int dx = -n; dx <= n; dx++)
                {
                    if (dx == 0 && dy == 0 && dz == 0) continue;
                    if (Gcd(Gcd(Math.Abs(dx), Math.Abs(dz)), Math.Abs(dy)) != 1) continue;
                    yield return (dx, dz, dy);
                }
            }
        }
    }

    static int DirectionCount() => Directions().Count();

    static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    static void Sweep(bool[] solid, byte[] reach, int worldHeight, int dx, int dz, int dy)
    {
        // Visit against the ray direction so the predecessor is always already final.
        int xFrom = dx > 0 ? 0 : Span - 1, xTo = dx > 0 ? Span : -1, xStep = dx > 0 ? 1 : -1;
        int zFrom = dz > 0 ? 0 : Span - 1, zTo = dz > 0 ? Span : -1, zStep = dz > 0 ? 1 : -1;
        int yFrom = dy > 0 ? 0 : worldHeight - 1, yTo = dy > 0 ? worldHeight : -1, yStep = dy > 0 ? 1 : -1;

        for (int z = zFrom; z != zTo; z += zStep)
        {
            for (int x = xFrom; x != xTo; x += xStep)
            {
                for (int y = yFrom; y != yTo; y += yStep)
                {
                    int i = Index(x, z, y, worldHeight);
                    if (solid[i]) continue;

                    int px = x - dx, pz = z - dz, py = y - dy;
                    if (px < 0 || px >= Span || pz < 0 || pz >= Span
                        || py < 0 || py >= worldHeight) continue;

                    int prior = reach[Index(px, pz, py, worldHeight)];
                    if (prior <= 1) continue;
                    if (prior - 1 > reach[i]) reach[i] = (byte)(prior - 1);
                }
            }
        }
    }

    /// <summary>
    /// The whole sky plane and every vertical face of the neighbourhood. Unknown territory
    /// outside the window is treated as open, which is the direction that keeps geometry
    /// rather than removing it.
    /// </summary>
    static void SeedBoundary(Action<int, int, int> seed, int worldHeight)
    {
        for (int z = 0; z < Span; z++)
        {
            for (int x = 0; x < Span; x++) seed(x, z, worldHeight - 1);
        }
        for (int y = 0; y < worldHeight; y++)
        {
            for (int t = 0; t < Span; t++)
            {
                seed(0, t, y);
                seed(Span - 1, t, y);
                seed(t, 0, y);
                seed(t, Span - 1, y);
            }
        }
    }

    static (int X, int Z, int Y) Unpack(int i, int worldHeight)
    {
        int y = i % worldHeight;
        int column = i / worldHeight;
        return (column % Span, column / Span, y);
    }

    /// <summary>
    /// The same section with every sealed air cell turned to rock, as a snapshot the mesher
    /// accepts. Filled cells inherit the material of the run below them - the cave floor's
    /// own - which keeps the colour plausible. Geometry does not depend on that choice: the
    /// mesher covers a face by contact and translucency, not by material.
    /// </summary>
    static SectionSnapshot FillSealed(LodSection s, bool[] solid, bool[] outside,
        int worldHeight, int dx, int dz, bool forceRebuild = false)
    {
        SectionSnapshot original = SectionSnapshot.Of(s);
        int baseX = (dx + Radius) * Grid;
        int baseZ = (dz + Radius) * Grid;

        var runs = new List<ulong>();
        var starts = new int[Grid * Grid + 1];

        for (int cz = 0; cz < Grid; cz++)
        {
            for (int cx = 0; cx < Grid; cx++)
            {
                int col = LodSection.ColumnIndex(cx, cz);
                starts[col] = runs.Count;
                if (!s.Captured[col]) continue;

                Span<ulong> source = s.ColumnRuns(col);
                if (source.Length == 0) continue;

                int columnTop = LodSection.RunYTop(source[0]);
                int columnBottom = Math.Max(0, LodSection.RunYBottom(source[^1]));
                if (columnTop <= columnBottom)
                {
                    foreach (ulong run in source) runs.Add(run);
                    continue;
                }

                // An untouched column keeps its stored runs byte for byte, so a rebuild
                // cannot introduce a difference of its own where there was nothing to fill.
                bool anySealed = false;
                for (int y = columnBottom; y < columnTop && !anySealed; y++)
                {
                    int i = Index(baseX + cx, baseZ + cz, y, worldHeight);
                    if (SubsurfaceMode ? !solid[i] : (!solid[i] && !outside[i])) anySealed = true;
                }
                if (!anySealed && !forceRebuild)
                {
                    foreach (ulong run in source) runs.Add(run);
                    continue;
                }

                int height = columnTop - columnBottom;
                var occupied = new bool[height];
                var palette = new int[height];
                var known = new bool[height];

                for (int y = columnBottom; y < columnTop; y++)
                {
                    int i = Index(baseX + cx, baseZ + cz, y, worldHeight);
                    occupied[y - columnBottom] = SubsurfaceMode || solid[i] || !outside[i];
                }
                foreach (ulong run in source)
                {
                    int top = Math.Min(columnTop, LodSection.RunYTop(run));
                    int bottom = Math.Max(columnBottom, LodSection.RunYBottom(run));
                    int pid = LodSection.RunPaletteId(run);
                    for (int y = bottom; y < top; y++)
                    {
                        palette[y - columnBottom] = pid;
                        known[y - columnBottom] = true;
                    }
                }

                // Carry a material upward into the filled cells, then downward for a pocket
                // that bottoms out the column and has nothing beneath it to inherit from.
                int carried = -1;
                for (int at = 0; at < height; at++)
                {
                    if (!occupied[at]) continue;
                    if (known[at]) carried = palette[at];
                    else if (carried >= 0) palette[at] = carried;
                }
                carried = -1;
                for (int at = height - 1; at >= 0; at--)
                {
                    if (!occupied[at]) continue;
                    if (known[at]) carried = palette[at];
                    else if (carried >= 0 && !known[at]) palette[at] = carried;
                }

                int runTop = -1;
                int runPid = -1;
                for (int y = columnTop - 1; y >= columnBottom; y--)
                {
                    int at = y - columnBottom;
                    if (occupied[at])
                    {
                        if (runTop < 0) { runTop = y + 1; runPid = palette[at]; }
                        else if (palette[at] != runPid)
                        {
                            runs.Add(LodSection.PackRun(runPid, runTop, y + 1));
                            runTop = y + 1;
                            runPid = palette[at];
                        }
                    }
                    else if (runTop >= 0)
                    {
                        runs.Add(LodSection.PackRun(runPid, runTop, y + 1));
                        runTop = -1;
                    }
                }
                if (runTop >= 0) runs.Add(LodSection.PackRun(runPid, runTop, columnBottom));
            }
        }
        starts[Grid * Grid] = runs.Count;

        return new SectionSnapshot
        {
            Runs = runs.ToArray(),
            ColumnStart = starts,
            Captured = original.Captured,
            PaletteColors = original.PaletteColors,
            PaletteFlags = original.PaletteFlags,
            PaletteTintSlots = original.PaletteTintSlots,
        };
    }

    // ---- cache access, the same approach HzbField uses ----

    sealed class CacheSections
    {
        readonly SqliteCommand cmd;
        readonly LodStore decoder = new(new CaptureLogger());
        readonly Dictionary<long, LodSection?> loaded = new();

        public CacheSections(SqliteConnection conn)
        {
            cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Data FROM Section WHERE Detail=@d AND SX=@x AND SZ=@z";
            cmd.Parameters.Add("@d", SqliteType.Integer);
            cmd.Parameters.Add("@x", SqliteType.Integer);
            cmd.Parameters.Add("@z", SqliteType.Integer);
            cmd.Prepare();
        }

        public LodSection? Get(long key)
        {
            if (loaded.TryGetValue(key, out LodSection? cached)) return cached;
            cmd.Parameters["@d"].Value = LodWorld.KeyLevel(key);
            cmd.Parameters["@x"].Value = LodWorld.KeySx(key);
            cmd.Parameters["@z"].Value = LodWorld.KeySz(key);
            LodSection? section = cmd.ExecuteScalar() is byte[] blob
                ? decoder.DeserializeForeign(blob, null)
                : null;
            loaded[key] = section;
            return section;
        }

        /// <summary>
        /// Drop a decoded section once the caller has taken what it needs. The whole-cache
        /// pass converts every section to a snapshot as it goes; keeping the LodSection
        /// beside it would hold thousands of palettes alive for nothing.
        /// </summary>
        public void Forget(long key) => loaded.Remove(key);
    }

    static long[] KeysAtLevel(SqliteConnection conn, int level)
    {
        var keys = new List<long>();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT SX,SZ FROM Section WHERE Detail={level}";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            keys.Add(LodWorld.SectionKey(level, reader.GetInt32(0), reader.GetInt32(1)));
        }
        return keys.ToArray();
    }

    static string? NewestClientCache()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VintagestoryData", "ModData", "vintagehorizons");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateFiles(root, "*.db")
            .Where(p => !p.EndsWith("-server.db", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// The owner's cache is not a test fixture. A snapshot beside it costs a file copy and
    /// removes any possibility of this tool writing to the only copy.
    /// </summary>
    static string CopyForReading(string cache)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vh-cavefield-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string copy = Path.Combine(dir, Path.GetFileName(cache));
        File.Copy(cache, copy);
        foreach (string side in new[] { "-wal", "-shm" })
        {
            if (File.Exists(cache + side)) File.Copy(cache + side, copy + side);
        }
        return copy;
    }

    static double Share(long part, long whole) => whole > 0 ? part * 100.0 / whole : 0.0;

    static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--cache": o.Cache = Next(args, ref i); break;
                case "--samples": o.Samples = int.Parse(Next(args, ref i)); break;
                case "--seed": o.Seed = int.Parse(Next(args, ref i)); break;
                case "--radius": o.Radius = int.Parse(Next(args, ref i)); break;
                case "--subsurface": o.Subsurface = true; break;
                case "--light": o.Light = int.Parse(Next(args, ref i)); break;
                case "--twowaysin": o.TwoWaysIn = true; break;
                case "--straight": o.Straight = true; break;
                case "--surface-sight": o.SurfaceSight = true; break;
                case "--rays": o.Rays = int.Parse(Next(args, ref i)); break;
                case "--verify": o.Verify = true; break;
                case "--shipping": o.Shipping = true; break;
                case "--shipping-no-sight":
                    o.Shipping = true;
                    o.NoShippingSurfaceSight = true;
                    break;
                case "--global": o.Global = true; break;
                case "--sweep": o.Global = true; o.Sweep = true; break;
                case "--surface": o.Global = true; o.SurfaceOnly = true; break;
                case "--labels": o.Labels = int.Parse(Next(args, ref i)); break;
                case "--tiles": o.Tiles = int.Parse(Next(args, ref i)); break;
                case "--frontier":
                    o.Frontier = Next(args, ref i).ToLowerInvariant() switch
                    {
                        "open" => CaveFrontier.Open,
                        "portal" => CaveFrontier.Portal,
                        var other => throw new ArgumentException(
                            $"--frontier takes open or portal, not '{other}'"),
                    };
                    break;
                case "--level": o.Level = int.Parse(Next(args, ref i)); break;
                case "--help" or "-h": o.Help = true; break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
        }
        return o;
    }

    static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} needs a value");
        return args[++i];
    }

    static void PrintHelp()
    {
        Console.WriteLine();
        Console.WriteLine("  cavefield - how much cached geometry is sealed inside rock");
        Console.WriteLine();
        Console.WriteLine("    --cache <path>    cache database (default: newest client cache)");
        Console.WriteLine("    --samples <n>     level-0 sections to measure (default 40)");
        Console.WriteLine("    --seed <n>        sampling seed (default 20260825)");
        Console.WriteLine("    --radius <n>      sections the flood may cross before calling a pocket");
        Console.WriteLine("                      sealed (default 1, a 3x3 neighbourhood)");
        Console.WriteLine("    --subsurface      ceiling instead: remove ALL geometry below each column's");
        Console.WriteLine("                      top, cave mouths included. Not shippable; bounds the prize.");
        Console.WriteLine("    --light <n>       keep only what daylight reaches within n blocks of");
        Console.WriteLine("                      spreading; 0 (default) keeps anything merely connected");
        Console.WriteLine("    --straight        carry sight in straight lines rather than spreading it,");
        Console.WriteLine("                      so a winding tunnel goes dark the moment it bends");
        Console.WriteLine("    --rays <n>        sight direction detail: 1 = 26 directions, 2 = 98");
        Console.WriteLine("    --surface-sight   union the daylight flood with independent straight rays");
        Console.WriteLine("                      from above-ground air; every ray checks crossed voxels");
        Console.WriteLine("    --twowaysin       never fill a dark pocket with more than one way in, so a");
        Console.WriteLine("                      passage through a mountain cannot be plugged");
        Console.WriteLine("    --shipping        run the mod's own LodCaveCull instead of this tool's model,");
        Console.WriteLine("                      with the four neighbours a real mesh job actually gets");
        Console.WriteLine("    --shipping-no-sight paired shipping baseline without the surface-sight pass");
        Console.WriteLine("    --verify          prove the column rebuild is neutral before measuring");
        Console.WriteLine("    --global          the CEILING measurement: classify the WHOLE cache at once");
        Console.WriteLine("                      with the global span-graph prototype and report what it");
        Console.WriteLine("                      would remove beyond the shipping local rule. Ignores");
        Console.WriteLine("                      --samples; use --tiles to work on a smaller square.");
        Console.WriteLine("    --tiles <n>       with --global, use only the n x n sections nearest the");
        Console.WriteLine("                      middle of the cached region (0 = all of it)");
        Console.WriteLine("    --frontier <how>  what the edge of the cache means. 'open' (default) keeps");
        Console.WriteLine("                      any component touching unknown space, whole. 'portal'");
        Console.WriteLine("                      treats a frontier contact as a pseudo-portal: it lights");
        Console.WriteLine("                      and it terminates routes, exactly as the shipping rule");
        Console.WriteLine("                      already treats its own window wall, but it does not save");
        Console.WriteLine("                      a cave beyond the reach of that light.");
        Console.WriteLine("    --labels <n>      how many nearby mouths each span is judged against when a");
        Console.WriteLine("                      route rule asks whether it is on a passage (default 4).");
        Console.WriteLine("                      More can only KEEP more; 2 is the aggressive extreme.");
        Console.WriteLine("    --sweep           implies --global: instead of one answer, sweep the route-");
        Console.WriteLine("                      tightening parameters and print the saving-versus-safety");
        Console.WriteLine("                      curve. Use with --frontier portal.");
        Console.WriteLine("    --surface         implies --global: keep only the conservative surface-light");
        Console.WriteLine("                      flood; dark entrance-to-entrance routes are removable.");
        Console.WriteLine();
    }
}
