namespace VintageHorizons.Checks;

/// <summary>
/// The shapes the global span-graph prototype has to get right before its cache-wide
/// number means anything.
///
/// These are deliberately the shapes session 58 said a one-cell bridge fixture does NOT
/// cover (G109): a network larger than any local window, a branch that is itself a loop,
/// two cave layers stacked in the same columns, a mouth complicated enough to look like
/// several, and fluid - which the shipping rule cannot see at all (G110).
///
/// Every assertion is about a VERDICT, not about geometry: this prototype measures a
/// ceiling and never touches a mesh, so what has to be right is what it decides.
/// </summary>
public static class CaveSpanChecks
{
    public static void Run(Check c)
    {
        ASealedNetworkLargerThanAnyLocalWindowIsRemovable(c);
        ALoopedBranchOffAThroughTunnelIsPeeled(c);
        StackedCaveLayersAreNotOneComponent(c);
        OneRoughMouthIsOnePortal(c);
        TwoRealMouthsKeepTheRouteBetweenThem(c);
        ADeepOverhangIsProtectedByTheChosenReach(c);
        SurfaceOnlyRemovesTheDarkMiddleOfAThroughTunnel(c);
        AnEnclosedWaterChamberIsRemovable(c);
        WaterConnectedToASurfaceLakeIsKept(c);
        AnUncapturedNeighbourKeepsEverythingBesideIt(c);

        AFrontierContactLightsButDoesNotSaveWhatIsBeyondIt(c);
        ARaggedFrontierContactIsOnePseudoPortal(c);
        ARouteFromARealMouthToTheFrontierIsKept(c);
        ARouteBetweenTwoFrontierContactsIsKept(c);
        TheSettledShapesAgreeInBothFrontierModes(c);

        AStraightThroughTunnelSurvivesEveryTightening(c);
        AGentlyCurvedTunnelSurvivesEveryTightening(c);
        AWindingRouteIsPluggedInTheMiddleButLitAtBothMouths(c);
        AWideRoomKeepsItsCorridorAndShedsTheRest(c);
        ABypassLoopTheBridgeRuleCannotPeelIsShedByTheCorridor(c);
        ASymmetricTwoMouthLoopKeepsBothArcs(c);
        TighteningNeverTouchesAVerdictThatIsNotARoute(c);
    }

    const int Grid = LodSection.GridSize;
    const int Reach = 32;

    // ---------------------------------------------------------------------------------
    // Fixture world
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A rectangle of sections, solid rock from bedrock to <see cref="Surface"/>, with
    /// whatever the caller carves out of it. Coordinates are ABSOLUTE columns, so a caller
    /// can lay out a cave across section boundaries without doing the arithmetic twice.
    /// </summary>
    sealed class SpanWorld
    {
        public const int Surface = 120;
        const byte Rock = 1;
        const byte Fluid = 2;

        readonly int nx, nz;
        readonly Dictionary<(int Gx, int Gz), byte[]> carved = new();
        readonly HashSet<(int Gx, int Gz)> uncaptured = new();

        public SpanWorld(int sectionsX, int sectionsZ)
        {
            nx = sectionsX;
            nz = sectionsZ;
        }

        byte[] Materials(int gx, int gz)
        {
            if (carved.TryGetValue((gx, gz), out byte[]? had)) return had;
            var fresh = new byte[Surface];
            Array.Fill(fresh, Rock);
            carved[(gx, gz)] = fresh;
            return fresh;
        }

        public void Air(int gx, int gz, int bottom, int top)
        {
            byte[] m = Materials(gx, gz);
            for (int y = bottom; y < top && y < Surface; y++) m[y] = 0;
        }

        public void Air(int gx, int gz, int bottom) => Air(gx, gz, bottom, Surface);

        public void Water(int gx, int gz, int bottom, int top)
        {
            byte[] m = Materials(gx, gz);
            for (int y = bottom; y < top && y < Surface; y++) m[y] = Fluid;
        }

        public void Uncaptured(int gx, int gz) => uncaptured.Add((gx, gz));

        public Dictionary<(int Sx, int Sz), SectionSnapshot> Build()
        {
            var world = new Dictionary<(int Sx, int Sz), SectionSnapshot>();
            for (int sz = 0; sz < nz; sz++)
            {
                for (int sx = 0; sx < nx; sx++) world[(sx, sz)] = Section(sx, sz);
            }
            return world;
        }

        public SectionSnapshot Section(int sx, int sz)
        {
            var runs = new List<ulong>();
            var starts = new int[Grid * Grid + 1];
            var captured = new bool[Grid * Grid];

            for (int cz = 0; cz < Grid; cz++)
            {
                for (int cx = 0; cx < Grid; cx++)
                {
                    int col = LodSection.ColumnIndex(cx, cz);
                    starts[col] = runs.Count;
                    int gx = sx * Grid + cx, gz = sz * Grid + cz;
                    if (uncaptured.Contains((gx, gz))) continue;

                    captured[col] = true;
                    if (!carved.TryGetValue((gx, gz), out byte[]? m))
                    {
                        runs.Add(LodSection.PackRun(Rock, Surface, 0));
                        continue;
                    }

                    int y = Surface - 1;
                    while (y >= 0)
                    {
                        byte material = m[y];
                        if (material == 0) { y--; continue; }
                        int top = y + 1;
                        while (y >= 0 && m[y] == material) y--;
                        runs.Add(LodSection.PackRun(material, top, y + 1));
                    }
                }
            }
            starts[Grid * Grid] = runs.Count;

            return new SectionSnapshot
            {
                Runs = runs.ToArray(),
                ColumnStart = starts,
                Captured = captured,
                PaletteColors = new[] { 0, unchecked((int)0xFF808080), unchecked((int)0xFFC05030) },
                PaletteFlags = new byte[] { 0, 0, LodPaletteEntry.FlagWater },
                PaletteTintSlots = new byte[] { 0, 0, 0 },
            };
        }
    }

    static CaveSpanGraph Classify(SpanWorld world) =>
        CaveSpanGraph.Build(world.Build(), level: 0, reach: Reach);

    static CaveSpanGraph Classify(SpanWorld world, CaveFrontier frontier) =>
        CaveSpanGraph.Build(world.Build(), level: 0, reach: Reach, frontier);

    static CaveSpanGraph Classify(SpanWorld world, CaveRouteRule route) =>
        CaveSpanGraph.Build(world.Build(), level: 0, reach: Reach, CaveFrontier.Portal, route);

    static CaveSpanGraph Classify(SpanWorld world, int reach) =>
        CaveSpanGraph.Build(world.Build(), level: 0, reach, CaveFrontier.Portal);

    /// <summary>Every setting the whole-cache sweep visits, so a fixture can pin all of them.</summary>
    static readonly CaveRouteRule[] Sweep =
    {
        CaveRouteRule.Bridge,
        new(0, 0), new(Reach / 2, 0), new(Reach, 0), new(Reach * 2, 0),
        new(-1, 1.5), new(-1, 2), new(-1, 3),
        new(Reach, 3), new(Reach, 2),
    };

    /// <summary>
    /// A dead-end tunnel whose only way out is the edge of what has been captured. The
    /// column west of the cave head was never captured, so the head touches unknown space
    /// and nothing else does.
    /// </summary>
    static SpanWorld TunnelOntoTheFrontier()
    {
        var world = new SpanWorld(3, 3);
        for (int x = 60; x <= 150; x++) world.Air(x, 96, 40, 48);
        world.Uncaptured(59, 96);
        return world;
    }

    // ---------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// The owner's reach directive in executable form. The shelf is fifty columns deep:
    /// safely beyond the old 32-block reach but inside the chosen 64-block reach. Its air
    /// has one real opening, so the corridor rule is irrelevant; daylight reach alone must
    /// protect every column beneath the visible underside.
    /// </summary>
    static void ADeepOverhangIsProtectedByTheChosenReach(Check c)
    {
        var world = new SpanWorld(3, 3);
        const int z = 96, mouthX = 70, deepX = 120;

        world.Air(mouthX - 1, z, 40);               // exterior beside the shelf
        for (int x = mouthX; x <= deepX; x++)
        {
            world.Air(x, z, 40, 64);                // ground below, rock shelf above
        }

        CaveSpanGraph chosen = Classify(world, reach: 64);
        for (int x = mouthX; x <= deepX; x++)
        {
            c.Eq(CaveSpanVerdict.KeptLit, chosen.AirVerdictAt(x, z, 48),
                $"reach 64 keeps the visible underside {x - mouthX} blocks beneath the shelf");
        }

        CaveSpanGraph old = Classify(world, reach: 32);
        c.Eq(CaveSpanVerdict.KeptLit, old.AirVerdictAt(mouthX, z, 48),
            "the old reach still keeps the overhang mouth");
        c.Eq(CaveSpanVerdict.RemovableOverflow, old.AirVerdictAt(deepX, z, 48),
            "but the old reach cuts off the same overhang at fifty blocks of depth");
    }

    /// <summary>
    /// Product rule: underground connectivity has no value by itself. Both surface mouths
    /// and their lit surroundings stay, but the dark middle may be removed even though it
    /// connects them.
    /// </summary>
    static void SurfaceOnlyRemovesTheDarkMiddleOfAThroughTunnel(Check c)
    {
        var world = new SpanWorld(5, 3);
        const int z = 96, west = 40, east = 280;
        for (int x = west; x <= east; x++) world.Air(x, z, 40, 48);
        world.Air(west, z - 1, 40);
        world.Air(east, z - 1, 40);

        CaveSpanGraph graph = CaveSpanGraph.BuildSurfaceOnly(
            world.Build(), level: 0, reach: 64, frontier: CaveFrontier.Portal);

        c.Eq(CaveSpanVerdict.KeptLit, graph.AirVerdictAt(west + 16, z, 44),
            "surface-only keeps the lit tunnel near the west mouth");
        c.Eq(CaveSpanVerdict.KeptLit, graph.AirVerdictAt(east - 16, z, 44),
            "surface-only keeps the lit tunnel near the east mouth");
        c.Eq(CaveSpanVerdict.RemovableRouteTightened,
            graph.AirVerdictAt((west + east) / 2, z, 44),
            "surface-only removes the dark middle despite two connected entrances");
    }

    /// <summary>
    /// A straight tunnel crossing four sections is globally sealed. The global graph can prove
    /// that and remove it. One local 3x3 view cannot distinguish the middle from a mountain tunnel
    /// whose real mouths lie just beyond the window, so the shipping rule deliberately fails open.
    /// </summary>
    static void ASealedNetworkLargerThanAnyLocalWindowIsRemovable(Check c)
    {
        var world = new SpanWorld(6, 3);
        const int z = 96;                                    // middle row of sections
        for (int x = Grid; x < Grid * 5; x++) world.Air(x, z, 40, 48);

        Dictionary<(int Sx, int Sz), SectionSnapshot> sections = world.Build();
        CaveSpanGraph graph = CaveSpanGraph.Build(sections, level: 0, reach: Reach);

        c.Eq(CaveSpanVerdict.RemovableSealed, graph.AirVerdictAt(Grid + 6, z, 44),
            "a network sealed everywhere is removable at its west end");
        c.Eq(CaveSpanVerdict.RemovableSealed, graph.AirVerdictAt(Grid * 5 - 6, z, 44),
            "and at its east end, four sections away");
        c.True(graph.SameComponent(Grid + 6, z, 44, Grid * 5 - 6, z, 44),
            "because the global pass sees both ends as one component");
        c.Eq(0, graph.PortalsAt(Grid * 3, z, 44),
            "and that component reaches no surface portal at all");

        // The same tunnel, put to the shipping rule one section at a time. Its uninterrupted
        // line crosses both window walls, which is the only local evidence available when real
        // surface mouths lie outside the snapshots. Visible terrain wins the ambiguity.
        var edges = new SectionSnapshot?[8];
        edges[0] = sections[(2, 1)];
        edges[1] = sections[(4, 1)];
        edges[2] = sections[(3, 0)];
        edges[3] = sections[(3, 2)];
        edges[4] = sections[(2, 0)];
        edges[5] = sections[(4, 0)];
        edges[6] = sections[(2, 2)];
        edges[7] = sections[(4, 2)];
        SectionSnapshot filled = LodCaveCull.FillUnseen(sections[(3, 1)], edges, 0, Reach);
        c.True(HasCavity(filled, Grid / 2, z - Grid),
            "while the bounded shipping rule fails open on the indistinguishable local shape");
    }

    /// <summary>
    /// G109's warning, answered on the span graph. A branch that is itself a LOOP has no
    /// bridge inside it, so a rule that only removes bridge-separated leaves at the voxel
    /// level keeps the whole thing. Contracting the loop into one block first makes it a
    /// leaf of the bridge forest, and a leaf with no portal in it is not load-bearing.
    /// </summary>
    static void ALoopedBranchOffAThroughTunnelIsPeeled(Check c)
    {
        var world = new SpanWorld(3, 3);
        const int z = 96;
        for (int x = 40; x <= 150; x++) world.Air(x, z, 40, 48);
        world.Air(40, z - 1, 40);                               // west mouth, open to the sky
        world.Air(150, z - 1, 40);                              // east mouth

        world.Air(95, z + 1, 40, 48);                           // the neck
        for (int x = 93; x <= 97; x++)                          // and a closed ring beyond it
        {
            world.Air(x, z + 2, 40, 48);
            world.Air(x, z + 6, 40, 48);
        }
        for (int zz = z + 2; zz <= z + 6; zz++)
        {
            world.Air(93, zz, 40, 48);
            world.Air(97, zz, 40, 48);
        }

        CaveSpanGraph graph = Classify(world);

        c.Eq(2, graph.PortalsAt(95, z, 44), "the through-tunnel has two distinct portals");
        c.Eq(CaveSpanVerdict.KeptRoute, graph.AirVerdictAt(95, z, 44),
            "so its dark middle is kept as the route between them");
        c.Eq(CaveSpanVerdict.RemovableBranch, graph.AirVerdictAt(95, z + 1, 44),
            "the neck onto the branch leads to no portal and goes");
        c.Eq(CaveSpanVerdict.RemovableBranch, graph.AirVerdictAt(93, z + 4, 44),
            "and so does the loop itself, which contains no bridge to separate it");
    }

    /// <summary>
    /// Two caves in the same columns at different depths, one open and one sealed. The
    /// shipping rule contracts every dark height in a column into one graph node, so the
    /// sealed layer inherits the open layer's entrance. Spans keep them apart.
    /// </summary>
    static void StackedCaveLayersAreNotOneComponent(Check c)
    {
        var world = new SpanWorld(3, 3);
        const int z = 96;
        for (int x = 40; x <= 150; x++)
        {
            world.Air(x, z, 70, 78);                            // upper layer
            world.Air(x, z, 30, 38);                            // lower layer, sealed
        }
        world.Air(40, z - 1, 70);                               // one mouth, into the UPPER layer

        CaveSpanGraph graph = Classify(world);

        c.False(graph.SameComponent(95, z, 74, 95, z, 34),
            "two cave layers sharing X and Z are not one component");
        c.Eq(1, graph.PortalsAt(95, z, 74), "the upper layer has the one real mouth");
        c.Eq(0, graph.PortalsAt(95, z, 34), "and the lower layer has none of its own");
        c.Eq(CaveSpanVerdict.RemovableSealed, graph.AirVerdictAt(95, z, 34),
            "so the sealed lower layer is removable outright");
        c.Eq(CaveSpanVerdict.RemovableOverflow, graph.AirVerdictAt(95, z, 74),
            "and the lit layer keeps only what its single portal reaches");
        c.Eq(CaveSpanVerdict.KeptLit, graph.AirVerdictAt(45, z, 74),
            "which does include the part near the mouth");
    }

    /// <summary>
    /// One mouth, three openings of different depths in a rock face. The shipping rule
    /// labels disconnected light-expiration patches, so a mouth like this can read as two
    /// or three entrances and rescue the entire cave behind it. Portal identity is a
    /// property of the OPENING, so contiguous contact is one portal however ragged.
    /// </summary>
    static void OneRoughMouthIsOnePortal(Check c)
    {
        var world = new SpanWorld(3, 3);
        const int z = 96;

        // A three-wide cave head, and a rock face broken open across all three of its
        // columns at three different heights.
        for (int zz = z - 1; zz <= z + 1; zz++)
        {
            for (int x = 60; x <= 62; x++) world.Air(x, zz, 40, 48);
        }
        world.Air(59, z - 1, 40);
        world.Air(59, z, 44);
        world.Air(59, z + 1, 40);

        for (int x = 63; x <= 150; x++) world.Air(x, z, 40, 48);   // the tunnel behind it

        CaveSpanGraph graph = Classify(world);

        c.Eq(1, graph.PortalsAt(61, z, 44),
            "a single ragged opening is one portal, not one per hole");
        c.Eq(1, graph.PortalsAt(150, z, 44),
            "including as seen from the far end of the cave behind it");
        c.Eq(CaveSpanVerdict.RemovableOverflow, graph.AirVerdictAt(150, z, 44),
            "so a dead end behind one mouth is removable beyond the light");
        c.Eq(CaveSpanVerdict.KeptLit, graph.AirVerdictAt(65, z, 44),
            "while the lit part behind that mouth stays");
    }

    /// <summary>
    /// The case the whole conservative rule exists to protect: a passage bored through a
    /// mountain, longer than twice the light, whose dark middle is the thing you can see
    /// daylight through. Two genuine portals, and the route between them must survive.
    /// </summary>
    static void TwoRealMouthsKeepTheRouteBetweenThem(Check c)
    {
        var world = new SpanWorld(3, 3);
        const int z = 96;
        for (int x = 40; x <= 150; x++) world.Air(x, z, 40, 48);
        world.Air(40, z - 1, 40);
        world.Air(150, z - 1, 40);

        CaveSpanGraph graph = Classify(world);

        c.Eq(2, graph.PortalsAt(95, z, 44), "two separated mouths are two portals");
        c.Eq(CaveSpanVerdict.KeptRoute, graph.AirVerdictAt(95, z, 44),
            "and the dark middle of a through-passage is never plugged");
        c.Eq(CaveSpanVerdict.KeptLit, graph.AirVerdictAt(45, z, 44),
            "its lit west end stays for the ordinary reason");
        c.Eq(CaveSpanVerdict.KeptLit, graph.AirVerdictAt(145, z, 44),
            "and so does its lit east end");
    }

    /// <summary>
    /// Fluid the air pass cannot see at all (G110). A flooded chamber boxed in by rock is
    /// geometry nobody will ever look at, and it is invisible to a rule that only ever
    /// replaces air.
    /// </summary>
    static void AnEnclosedWaterChamberIsRemovable(Check c)
    {
        var world = new SpanWorld(3, 3);
        for (int z = 90; z <= 93; z++)
        {
            for (int x = 90; x <= 93; x++) world.Water(x, z, 40, 48);
        }

        CaveSpanGraph graph = Classify(world);

        c.Eq(CaveSpanVerdict.WaterRemovable, graph.WaterVerdictAt(91, 91, 44),
            "a flooded chamber sealed in rock is removable");
        c.Eq(CaveSpanVerdict.None, graph.AirVerdictAt(91, 91, 44),
            "and it is not air, which is exactly why the shipping rule never sees it");
    }

    /// <summary>
    /// The other half of the fluid rule, and the reason it cannot simply delete water: a
    /// flooded tunnel that reaches a lake surface is connected to the open world, and
    /// somebody swimming down it would find rock where the water should be.
    /// </summary>
    static void WaterConnectedToASurfaceLakeIsKept(Check c)
    {
        var world = new SpanWorld(3, 3);
        const int z = 91;
        for (int x = 60; x <= 91; x++) world.Water(x, z, 40, 48);
        world.Water(91, z, 40, SpanWorld.Surface);              // a flooded shaft up to the surface

        CaveSpanGraph graph = Classify(world);

        c.Eq(CaveSpanVerdict.WaterKept, graph.WaterVerdictAt(91, z, 100),
            "the lake surface itself is exposed to the sky");
        c.Eq(CaveSpanVerdict.WaterKept, graph.WaterVerdictAt(70, z, 44),
            "and the submerged tunnel joined to it is kept with it");
    }

    /// <summary>
    /// The frontier rule. A global classifier still has a boundary - the edge of what has
    /// ever been captured - and the answer there must be the same fail-open answer the
    /// local rule gives, or the mod starts removing terrain it has simply not loaded yet.
    /// </summary>
    static void AnUncapturedNeighbourKeepsEverythingBesideIt(Check c)
    {
        var sealedWorld = new SpanWorld(3, 3);
        sealedWorld.Air(95, 96, 40, 48);
        c.Eq(CaveSpanVerdict.RemovableSealed, Classify(sealedWorld).AirVerdictAt(95, 96, 44),
            "a chamber with every neighbour captured is removable");

        var frontier = new SpanWorld(3, 3);
        frontier.Air(95, 96, 40, 48);
        frontier.Uncaptured(96, 96);
        CaveSpanGraph graph = Classify(frontier);
        c.Eq(CaveSpanVerdict.KeptUnknown, graph.AirVerdictAt(95, 96, 44),
            "the same chamber beside one uncaptured column is kept, not guessed at");
        c.Eq(-1, graph.PortalsAt(95, 96, 44),
            "and its component is never asked how many portals it has");
    }

    // ---------------------------------------------------------------------------------
    // Pseudo-portal frontier semantics
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// The whole point of the second frontier mode, and it is the stance the shipping rule
    /// already takes: unknown space is a light source, not a blanket amnesty. What is near
    /// it might be seen from it and stays; what is ninety blocks behind it cannot be, and
    /// keeping that was costing about thirty per cent of the geometry.
    /// </summary>
    static void AFrontierContactLightsButDoesNotSaveWhatIsBeyondIt(Check c)
    {
        CaveSpanGraph open = Classify(TunnelOntoTheFrontier(), CaveFrontier.Open);
        c.Eq(CaveSpanVerdict.KeptUnknown, open.AirVerdictAt(65, 96, 44),
            "under Open semantics the near end is kept because the component is unknown");
        c.Eq(CaveSpanVerdict.KeptUnknown, open.AirVerdictAt(150, 96, 44),
            "and so is the far end, ninety blocks away from anything unknown");

        CaveSpanGraph portal = Classify(TunnelOntoTheFrontier(), CaveFrontier.Portal);
        c.Eq(CaveSpanVerdict.KeptLitUnknown, portal.AirVerdictAt(65, 96, 44),
            "as a pseudo-portal it lights what is beside it, and that is kept");
        c.Eq(CaveSpanVerdict.RemovableOverflow, portal.AirVerdictAt(150, 96, 44),
            "but the dark far end beyond its reach becomes removable");
        c.Eq(0, portal.PortalsAt(150, 96, 44), "with no real mouth anywhere in the component");
        c.Eq(1, portal.TerminalsAt(150, 96, 44), "and exactly one terminal, the frontier itself");
    }

    /// <summary>
    /// The same trap portal identity exists to avoid, on the other kind of terminal. A
    /// ragged edge of the cache touching three columns of one cave head must be ONE way in,
    /// or the component gets two terminals for free and a route is kept between two parts
    /// of the same hole.
    /// </summary>
    static void ARaggedFrontierContactIsOnePseudoPortal(Check c)
    {
        var world = new SpanWorld(3, 3);
        for (int z = 95; z <= 97; z++)
        {
            for (int x = 60; x <= 62; x++) world.Air(x, z, 40, 48);
        }
        for (int x = 63; x <= 150; x++) world.Air(x, 96, 40, 48);
        for (int z = 95; z <= 97; z++) world.Uncaptured(59, z);

        CaveSpanGraph graph = Classify(world, CaveFrontier.Portal);

        c.Eq(1, graph.TerminalsAt(61, 96, 44),
            "three columns of one ragged frontier contact are one pseudo-portal");
        c.Eq(1, graph.TerminalsAt(150, 96, 44),
            "as seen from the far end of the cave behind it");
        c.Eq(CaveSpanVerdict.RemovableOverflow, graph.AirVerdictAt(150, 96, 44),
            "so the dead end behind it is removable rather than routed to a second terminal");
    }

    /// <summary>
    /// A passage you could genuinely see daylight along, if the far half of it were ever
    /// captured. One end is a real mouth, the other runs off the edge of what is known, and
    /// the dark middle joins them - so it is kept, and counted as kept BY the frontier.
    /// </summary>
    static void ARouteFromARealMouthToTheFrontierIsKept(Check c)
    {
        SpanWorld world = TunnelOntoTheFrontier();
        world.Air(150, 95, 40);                              // a real mouth at the east end

        CaveSpanGraph graph = Classify(world, CaveFrontier.Portal);

        c.Eq(1, graph.PortalsAt(105, 96, 44), "one real mouth");
        c.Eq(2, graph.TerminalsAt(105, 96, 44), "and two terminals once the frontier counts");
        c.Eq(CaveSpanVerdict.KeptRouteUnknown, graph.AirVerdictAt(105, 96, 44),
            "the dark middle is kept, and charged to the frontier rather than to daylight");
        c.Eq(CaveSpanVerdict.KeptLit, graph.AirVerdictAt(145, 96, 44),
            "while the part lit by the real mouth is charged to daylight");
    }

    /// <summary>
    /// Both ends run off the edge of the cache in different places. Neither is a mouth we
    /// have seen, but the passage between them could be a through-route in either, so the
    /// route survives - which is exactly the conservatism the shipping rule already applies
    /// to its own window wall.
    /// </summary>
    static void ARouteBetweenTwoFrontierContactsIsKept(Check c)
    {
        var world = new SpanWorld(3, 3);
        for (int x = 60; x <= 150; x++) world.Air(x, 96, 40, 48);
        world.Uncaptured(59, 96);
        world.Uncaptured(151, 96);

        CaveSpanGraph graph = Classify(world, CaveFrontier.Portal);

        c.Eq(0, graph.PortalsAt(105, 96, 44), "no real mouth at either end");
        c.Eq(2, graph.TerminalsAt(105, 96, 44), "but two separate frontier contacts");
        c.Eq(CaveSpanVerdict.KeptRouteUnknown, graph.AirVerdictAt(105, 96, 44),
            "so the dark middle between them is kept");

        // And a branch off that route still goes: the route rule keeps a passage, not a
        // licence for everything attached to one.
        var branched = new SpanWorld(3, 3);
        for (int x = 60; x <= 150; x++) branched.Air(x, 96, 40, 48);
        branched.Uncaptured(59, 96);
        branched.Uncaptured(151, 96);
        for (int z = 97; z <= 110; z++) branched.Air(105, z, 40, 48);

        c.Eq(CaveSpanVerdict.RemovableBranch,
            Classify(branched, CaveFrontier.Portal).AirVerdictAt(105, 108, 44),
            "a dead branch off a frontier-to-frontier route is still peeled");
    }

    /// <summary>
    /// The shapes that have nothing to do with the frontier must not have moved. A sealed
    /// network and a ragged real mouth are both far from anything uncaptured, so both modes
    /// have to agree about them exactly.
    /// </summary>
    static void TheSettledShapesAgreeInBothFrontierModes(Check c)
    {
        var network = new SpanWorld(6, 3);
        for (int x = Grid; x < Grid * 5; x++) network.Air(x, 96, 40, 48);

        foreach (CaveFrontier mode in new[] { CaveFrontier.Open, CaveFrontier.Portal })
        {
            CaveSpanGraph graph = Classify(network, mode);
            c.Eq(CaveSpanVerdict.RemovableSealed, graph.AirVerdictAt(Grid * 3, 96, 44),
                $"a sealed four-section network is removable under {mode} semantics");
        }

        var mouth = new SpanWorld(3, 3);
        for (int z = 95; z <= 97; z++)
        {
            for (int x = 60; x <= 62; x++) mouth.Air(x, z, 40, 48);
        }
        mouth.Air(59, 95, 40);
        mouth.Air(59, 96, 44);
        mouth.Air(59, 97, 40);
        for (int x = 63; x <= 150; x++) mouth.Air(x, 96, 40, 48);

        foreach (CaveFrontier mode in new[] { CaveFrontier.Open, CaveFrontier.Portal })
        {
            CaveSpanGraph graph = Classify(mouth, mode);
            c.Eq(1, graph.PortalsAt(61, 96, 44),
                $"one ragged real mouth is still one portal under {mode} semantics");
            c.Eq(CaveSpanVerdict.RemovableOverflow, graph.AirVerdictAt(150, 96, 44),
                $"and its dead end is still removable beyond the light under {mode}");
            c.Eq(CaveSpanVerdict.KeptLit, graph.AirVerdictAt(65, 96, 44),
                $"with the lit part still charged to daylight under {mode}");
        }
    }

    // ---------------------------------------------------------------------------------
    // Route tightening
    //
    // The bridge rule keeps the whole 2-edge-connected core between any two mouths, which
    // over the real cache is about a third of everything underground. Sight is a straight
    // line, so the middle of a long winding passage shows nothing of either end - these pin
    // where a tightened rule may and may not cut.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// The shape the whole conservative rule exists to protect. A passage bored dead
    /// straight through a mountain IS the shortest way between its two mouths and its
    /// tortuosity is 1, so no setting of either rule may touch it - including the most
    /// aggressive one in the sweep.
    /// </summary>
    static void AStraightThroughTunnelSurvivesEveryTightening(Check c)
    {
        var world = new SpanWorld(3, 3);
        for (int x = 40; x <= 150; x++) world.Air(x, 96, 40, 48);
        world.Air(40, 95, 40);
        world.Air(150, 95, 40);

        foreach (CaveRouteRule rule in Sweep)
        {
            c.Eq(CaveSpanVerdict.KeptRoute, Classify(world, rule).AirVerdictAt(95, 96, 44),
                $"a straight through-tunnel keeps its dark middle at {rule}");
        }
    }

    /// <summary>
    /// The same, bent. A passage that wanders a little is still something you can see
    /// daylight along, and a rule that cannot tell a bend from a maze is no use.
    /// </summary>
    static void AGentlyCurvedTunnelSurvivesEveryTightening(Check c)
    {
        var world = new SpanWorld(3, 3);
        for (int x = 40; x <= 95; x++) world.Air(x, 96, 40, 48);
        for (int z = 96; z <= 106; z++) world.Air(95, z, 40, 48);
        for (int x = 95; x <= 150; x++) world.Air(x, 106, 40, 48);
        world.Air(40, 95, 40);
        world.Air(150, 107, 40);

        foreach (CaveRouteRule rule in Sweep)
        {
            c.Eq(CaveSpanVerdict.KeptRoute, Classify(world, rule).AirVerdictAt(95, 101, 44),
                $"a gently curved through-tunnel keeps its dark corner at {rule}");
        }
    }

    /// <summary>
    /// The case the sightline rule is FOR. Two mouths four blocks apart, joined by a
    /// hundred and thirty blocks of serpentine: you can stand at either one and see nothing
    /// of the other, because no straight line goes down that passage. Both mouth vicinities
    /// stay lit; only the far middle goes.
    ///
    /// Note which rule does it. The corridor rule cannot: the serpentine IS the shortest
    /// path between those mouths, so every span of it is at slack zero. Only tortuosity
    /// separates a passage from a detour.
    /// </summary>
    static void AWindingRouteIsPluggedInTheMiddleButLitAtBothMouths(Check c)
    {
        var world = new SpanWorld(3, 3);
        for (int z = 96; z <= 160; z++) world.Air(60, z, 40, 48);
        for (int x = 60; x <= 64; x++) world.Air(x, 160, 40, 48);
        for (int z = 96; z <= 160; z++) world.Air(64, z, 40, 48);
        world.Air(59, 96, 40);
        world.Air(65, 96, 40);

        CaveSpanGraph bridge = Classify(world, CaveRouteRule.Bridge);
        c.Eq(2, bridge.TerminalsAt(60, 130, 44), "the serpentine really does join two mouths");
        c.Eq(CaveSpanVerdict.KeptRoute, bridge.AirVerdictAt(60, 160, 44),
            "and the bridge rule keeps every block of it");

        CaveSpanGraph corridor = Classify(world, new CaveRouteRule(Reach, 0));
        c.Eq(CaveSpanVerdict.KeptRoute, corridor.AirVerdictAt(60, 160, 44),
            "the corridor rule keeps it too, because the serpentine is the shortest way round");

        CaveSpanGraph sight = Classify(world, new CaveRouteRule(-1, 3));
        c.Eq(CaveSpanVerdict.RemovableRouteTightened, sight.AirVerdictAt(60, 160, 44),
            "the sightline rule plugs the far middle: no straight line runs down it");
        c.Eq(CaveSpanVerdict.KeptLit, sight.AirVerdictAt(60, 110, 44),
            "while the west mouth still lights its own vicinity");
        c.Eq(CaveSpanVerdict.KeptLit, sight.AirVerdictAt(64, 110, 44),
            "and so does the east mouth");
    }

    /// <summary>
    /// A room the passage runs through. The part of it on the way between the mouths is
    /// route; the corners are a detour of fourteen blocks, so a tight corridor sheds them
    /// and a slack one does not. This is the knob the sweep is measuring.
    /// </summary>
    static void AWideRoomKeepsItsCorridorAndShedsTheRest(Check c)
    {
        var world = new SpanWorld(3, 3);
        for (int x = 40; x <= 150; x++) world.Air(x, 96, 40, 48);
        for (int z = 89; z <= 103; z++)
        {
            for (int x = 90; x <= 104; x++) world.Air(x, z, 40, 48);
        }
        world.Air(40, 95, 40);
        world.Air(150, 95, 40);

        CaveSpanGraph tight = Classify(world, new CaveRouteRule(0, 0));
        c.Eq(CaveSpanVerdict.KeptRoute, tight.AirVerdictAt(97, 96, 44),
            "the line straight through the room is the route and stays at W=0");
        c.Eq(CaveSpanVerdict.RemovableRouteTightened, tight.AirVerdictAt(97, 89, 44),
            "the far edge of the room is a seven-block detour and goes at W=0");

        CaveSpanGraph slack = Classify(world, new CaveRouteRule(Reach, 0));
        c.Eq(CaveSpanVerdict.KeptRoute, slack.AirVerdictAt(97, 89, 44),
            "and comes back at W=32, because fourteen blocks of detour is inside the slack");

        c.Eq(CaveSpanVerdict.KeptRoute,
            Classify(world, CaveRouteRule.Bridge).AirVerdictAt(97, 89, 44),
            "the bridge rule keeps the whole room, which is what the sweep is measuring against");
    }

    /// <summary>
    /// G109 answered directly. A bypass loop around the main passage is 2-edge-connected
    /// with it, so no bridge separates it and the peel is powerless - this is exactly why
    /// route retention is a third of the cache. Distance from the shortest path is not
    /// fooled by a cycle.
    /// </summary>
    static void ABypassLoopTheBridgeRuleCannotPeelIsShedByTheCorridor(Check c)
    {
        var world = new SpanWorld(3, 3);
        for (int x = 40; x <= 150; x++) world.Air(x, 96, 40, 48);
        for (int z = 96; z <= 130; z++) world.Air(90, z, 40, 48);
        for (int x = 90; x <= 110; x++) world.Air(x, 130, 40, 48);
        for (int z = 96; z <= 130; z++) world.Air(110, z, 40, 48);
        world.Air(40, 95, 40);
        world.Air(150, 95, 40);

        c.Eq(CaveSpanVerdict.KeptRoute,
            Classify(world, CaveRouteRule.Bridge).AirVerdictAt(100, 130, 44),
            "no bridge separates a bypass loop, so the peel keeps all of it");

        CaveSpanGraph corridor = Classify(world, new CaveRouteRule(Reach, 0));
        c.Eq(CaveSpanVerdict.RemovableRouteTightened, corridor.AirVerdictAt(100, 130, 44),
            "the corridor rule sheds it: it is a sixty-eight block detour round a straight line");
        c.Eq(CaveSpanVerdict.KeptRoute, corridor.AirVerdictAt(100, 96, 44),
            "while the direct segment it bypasses stays");
    }

    /// <summary>
    /// Sanity where the two arcs are equally good. A ring with a mouth at each of two
    /// opposite corners has no single shortest way round - both halves are, exactly - and a
    /// rule that arbitrarily plugged one of them would be removing a passage a player can
    /// see straight down.
    /// </summary>
    static void ASymmetricTwoMouthLoopKeepsBothArcs(Check c)
    {
        var world = new SpanWorld(3, 3);
        for (int x = 60; x <= 100; x++)
        {
            world.Air(x, 60, 40, 48);
            world.Air(x, 100, 40, 48);
        }
        for (int z = 60; z <= 100; z++)
        {
            world.Air(60, z, 40, 48);
            world.Air(100, z, 40, 48);
        }
        world.Air(59, 60, 40);
        world.Air(101, 100, 40);

        foreach (CaveRouteRule rule in new[]
                 { CaveRouteRule.Bridge, new CaveRouteRule(0, 0), new CaveRouteRule(-1, 1.5) })
        {
            CaveSpanGraph graph = Classify(world, rule);
            c.Eq(CaveSpanVerdict.KeptRoute, graph.AirVerdictAt(100, 60, 44),
                $"the north-east arc of a symmetric two-mouth loop stays at {rule}");
            c.Eq(CaveSpanVerdict.KeptRoute, graph.AirVerdictAt(60, 100, 44),
                $"and so does the south-west arc at {rule}");
        }
    }

    /// <summary>
    /// Tightening is a filter on ROUTES and nothing else. A sealed network has no route to
    /// tighten and a ragged mouth's verdicts are decided by light, so the most aggressive
    /// setting in the sweep must leave both exactly where the bridge rule left them.
    /// </summary>
    static void TighteningNeverTouchesAVerdictThatIsNotARoute(Check c)
    {
        var network = new SpanWorld(6, 3);
        for (int x = Grid; x < Grid * 5; x++) network.Air(x, 96, 40, 48);

        var mouth = new SpanWorld(3, 3);
        for (int z = 95; z <= 97; z++)
        {
            for (int x = 60; x <= 62; x++) mouth.Air(x, z, 40, 48);
        }
        mouth.Air(59, 95, 40);
        mouth.Air(59, 96, 44);
        mouth.Air(59, 97, 40);
        for (int x = 63; x <= 150; x++) mouth.Air(x, 96, 40, 48);

        var hardest = new CaveRouteRule(0, 1.5);

        c.Eq(CaveSpanVerdict.RemovableSealed,
            Classify(network, hardest).AirVerdictAt(Grid * 3, 96, 44),
            "a sealed network is still removable under the hardest sweep setting");
        c.Eq(1, Classify(mouth, hardest).PortalsAt(61, 96, 44),
            "a ragged mouth is still one portal under it");
        c.Eq(CaveSpanVerdict.RemovableOverflow,
            Classify(mouth, hardest).AirVerdictAt(150, 96, 44),
            "and its dead end is still removable for the ordinary reason, not the new one");
        c.Eq(CaveSpanVerdict.KeptLit, Classify(mouth, hardest).AirVerdictAt(65, 96, 44),
            "with its lit part untouched");
    }

    static bool HasCavity(SectionSnapshot section, int cx, int cz)
    {
        Span<ulong> runs = section.ColumnRuns(LodSection.ColumnIndex(cx, cz));
        for (int r = 1; r < runs.Length; r++)
        {
            if (LodSection.RunYBottom(runs[r - 1]) > LodSection.RunYTop(runs[r])) return true;
        }
        return false;
    }
}
