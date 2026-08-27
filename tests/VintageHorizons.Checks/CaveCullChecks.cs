namespace VintageHorizons.Checks;

/// <summary>
/// The rule that decides which cavities are never built.
///
/// These are hand-built worlds rather than cache samples, because the questions are about
/// specific shapes: a bubble with no way out, a cave open to the sky, a tunnel bored through
/// a mountain. The offline `cavefield` harness measures how much the rule is WORTH against
/// real terrain; this fixes what it must and must not do.
///
/// Every check here is one-sided in the same direction. Filling something visible is the
/// failure that matters - a player sees rock where a cave should be, from an angle nobody
/// tested - so the interesting assertions are all about geometry that must SURVIVE.
/// </summary>
public static class CaveCullChecks
{
    public static void Run(Check c)
    {
        ASealedBubbleIsFilled(c);
        ACaveOpenToTheSkyIsKept(c);
        ABentConnectedTunnelKeepsOnlyItsLitEnds(c);
        StraightSurfaceSightSurvivesAtAnyElevation(c);
        LongStraightTunnelSurvivesWhenMouthsAreOutsideTheWindow(c);
        DiagonalAndSlopedSurfaceSightSurvive(c);
        SurfaceSightDoesNotTurnAroundACorner(c);
        UndergroundNetworkConnectivityDoesNotMatter(c);
        CoarseLevelsCarryTheSameConservativeRule(c);
        AColumnWithoutRockIsNeverUsedAsCaveFill(c);
        NothingIsTouchedWithoutIt(c);
        MeshingUsesTheFilledSectionForEveryFace(c);
        CrossSectionCavesDoNotGrowSeamWalls(c);
        RetentionTelemetryNamesTheReasonEachCellSurvived(c);
        LongSightDirectionsVisitEveryCrossedVoxel(c);
    }

    const int Grid = LodSection.GridSize;
    const int Rock = 1;
    const int Surface = 120;

    /// <summary>
    /// Solid ground from bedrock to <see cref="Surface"/> across the whole section, with
    /// whatever cavities the caller carves out of it.
    /// </summary>
    static SectionSnapshot Ground(params (int X, int Z, int Bottom, int Top)[] cavities)
    {
        var runs = new List<ulong>();
        var starts = new int[Grid * Grid + 1];

        for (int cz = 0; cz < Grid; cz++)
        {
            for (int cx = 0; cx < Grid; cx++)
            {
                int col = LodSection.ColumnIndex(cx, cz);
                starts[col] = runs.Count;

                // Gaps in this column, highest first, since runs are stored top-down.
                var gaps = cavities
                    .Where(v => v.X == cx && v.Z == cz)
                    .OrderByDescending(v => v.Top)
                    .ToArray();

                int top = Surface;
                foreach ((int _, int _, int bottom, int gapTop) in gaps)
                {
                    if (gapTop < top) runs.Add(LodSection.PackRun(Rock, top, gapTop));
                    top = bottom;
                }
                if (top > 0) runs.Add(LodSection.PackRun(Rock, top, 0));
            }
        }
        starts[Grid * Grid] = runs.Count;

        return new SectionSnapshot
        {
            Runs = runs.ToArray(),
            ColumnStart = starts,
            Captured = Enumerable.Repeat(true, Grid * Grid).ToArray(),
            PaletteColors = new[] { 0, unchecked((int)0xFF808080) },
            PaletteFlags = new byte[] { 0, 0 },
            PaletteTintSlots = new byte[] { 0, 0 },
        };
    }

    static SectionSnapshot?[] SolidNeighbours() => new SectionSnapshot?[]
    {
        Ground(), Ground(), Ground(), Ground(),
        Ground(), Ground(), Ground(), Ground(),
    };

    /// <summary>True when this column has any air between bedrock and the surface.</summary>
    static bool HasCavity(SectionSnapshot section, int cx, int cz)
    {
        Span<ulong> runs = section.ColumnRuns(LodSection.ColumnIndex(cx, cz));
        for (int r = 1; r < runs.Length; r++)
        {
            if (LodSection.RunYBottom(runs[r - 1]) > LodSection.RunYTop(runs[r])) return true;
        }
        return false;
    }

    static bool IsAirAt(SectionSnapshot section, int cx, int cz, int y)
    {
        foreach (ulong run in section.ColumnRuns(LodSection.ColumnIndex(cx, cz)))
        {
            if (y >= LodSection.RunYBottom(run) && y < LodSection.RunYTop(run)) return false;
        }
        return true;
    }

    static void ASealedBubbleIsFilled(Check c)
    {
        // One column of air deep inside the rock, walled on all six sides by its neighbours.
        SectionSnapshot before = Ground((32, 32, 40, 48));
        c.True(HasCavity(before, 32, 32), "the fixture really does contain a cavity");

        SectionSnapshot after = LodCaveCull.FillUnseen(
            before, SolidNeighbours(), level: 0, reach: LodCaveCull.DefaultReach);

        c.False(HasCavity(after, 32, 32), "a bubble with no way out is filled");
        c.False(ReferenceEquals(before, after), "and the section really was rebuilt");
    }

    static void LongSightDirectionsVisitEveryCrossedVoxel(Check c)
    {
        (int X, int Z, int Y)[] shallow = CaveField.RayInterior(2, 1, 0);
        c.Eq(2, shallow.Length, "a 2:1 ray has two intervening voxels");
        c.Eq((1, 0, 0), shallow[0], "and cannot jump over the first voxel");
        c.Eq((1, 1, 0), shallow[1], "or the second voxel after crossing Z");

        (int X, int Z, int Y)[] diagonal = CaveField.RayInterior(1, 1, 1);
        c.Eq(0, diagonal.Length, "a one-cell diagonal has no intervening voxel");

        (int X, int Z, int Y)[] longRay = CaveField.RayInterior(3, 2, 1);
        var expected = new[] { (1, 0, 0), (1, 1, 0), (2, 1, 1), (2, 2, 1) };
        c.Eq(expected.Length, longRay.Length, "a 3:2:1 ray visits every intervening voxel");
        for (int i = 0; i < expected.Length; i++)
        {
            c.Eq(expected[i], longRay[i], $"3:2:1 ray voxel {i}");
        }

        (int X, int Z, int Y)[] reverse = CaveField.RayInterior(-3, -2, -1);
        c.Eq(expected.Length, reverse.Length, "the reverse ray visits the same number of voxels");
        for (int i = 0; i < expected.Length; i++)
        {
            c.Eq((-expected[i].Item1, -expected[i].Item2, -expected[i].Item3), reverse[i],
                $"reverse 3:2:1 ray voxel {i}");
        }
    }

    static void ACaveOpenToTheSkyIsKept(Check c)
    {
        // The shaft is in the NEXT column along, so the chamber stays a genuine gap between
        // two runs and daylight has to arrive sideways to save it. A shaft in the chamber's
        // own column would leave nothing enclosed to test.
        SectionSnapshot before = Ground(
            (20, 20, 40, 48),                    // the chamber
            (21, 20, 40, Surface));              // a shaft beside it, open to the sky

        c.True(HasCavity(before, 20, 20), "the chamber is a real cavity to begin with");

        SectionSnapshot after = LodCaveCull.FillUnseen(
            before, SolidNeighbours(), level: 0, reach: LodCaveCull.DefaultReach);

        c.True(HasCavity(after, 20, 20), "a cavity daylight reaches is kept");
    }

    /// <summary>
    /// Surface-only product rule: a passage connecting two entrances has no special value.
    /// Each mouth keeps what the light envelope reaches and the dark middle is removable.
    /// </summary>
    static void ABentConnectedTunnelKeepsOnlyItsLitEnds(Check c)
    {
        var carved = new List<(int, int, int, int)>();
        for (int x = 10; x < 50; x++) carved.Add((x, 30, 40, 48));

        // A shaft beside each end, so daylight enters at the two mouths and nowhere else.
        carved.Add((10, 29, 40, Surface));
        carved.Add((49, 29, 40, Surface));

        // A short reach on purpose. The passage is 40 columns and a section is only 64, so
        // at the shipping reach daylight would meet in the middle and there would be no dark
        // stretch left to test. Eight blocks leaves one, which is the case that matters.
        const int shortReach = 8;

        SectionSnapshot before = Ground(carved.ToArray());
        SectionSnapshot after = LodCaveCull.FillUnseen(
            before, SolidNeighbours(), level: 0, reach: shortReach);

        c.True(HasCavity(after, 10, 30), "the lit mouth of a through-passage survives");
        c.False(HasCavity(after, 30, 30),
            "its dark middle is removed despite connecting two entrances");
        c.True(HasCavity(after, 49, 30), "at both ends");

        // The same shape with one mouth bricked up is a dead end, and its dark part has a
        // single way in. Without this the check above would pass on a rule that simply never
        // fills anything.
        var deadEnd = carved.Where(v => !(v.Item1 == 49 && v.Item2 == 29)).ToArray();
        SectionSnapshot blind = LodCaveCull.FillUnseen(
            Ground(deadEnd), SolidNeighbours(), level: 0, reach: shortReach);

        c.True(HasCavity(blind, 10, 30), "a dead end keeps the part daylight reaches");
        c.False(HasCavity(blind, 40, 30), "and loses the dark far end nobody can see");
    }

    /// <summary>
    /// Absolute altitude is irrelevant. A straight air segment sharing a line with exterior air
    /// remains visible from that exterior whether it happens to sit low or high in the world.
    /// </summary>
    static void StraightSurfaceSightSurvivesAtAnyElevation(Check c)
    {
        foreach (int bottom in new[] { 20, 80 })
        {
            var carved = new List<(int, int, int, int)>();
            carved.Add((10, 30, bottom, Surface));       // genuine exterior at the west mouth
            for (int x = 11; x < 50; x++) carved.Add((x, 30, bottom, bottom + 8));

            SectionSnapshot after = LodCaveCull.FillUnseen(
                Ground(carved.ToArray()), SolidNeighbours(), level: 0, reach: 8);

            c.True(HasCavity(after, 30, 30),
                $"a straight surface sightline remains open at arbitrary height {bottom}");
        }
    }

    /// <summary>
    /// A section in the middle of a long straight tunnel cannot see either real mouth inside its
    /// 3x3 classification window. The uninterrupted line crossing both opposite window edges is
    /// the only local evidence available, and must fail open. A single blocked column is the
    /// control: merely touching one window edge is not enough to preserve a hidden dead end.
    /// </summary>
    static void LongStraightTunnelSurvivesWhenMouthsAreOutsideTheWindow(Check c)
    {
        static SectionSnapshot TunnelSection(
            int blockedX = -1, int floor = 40, int raisedX = -1, int raisedFloor = 40,
            int sideDepth = 0)
        {
            var tunnel = new List<(int, int, int, int)>();
            for (int x = 0; x < Grid; x++)
            {
                if (x != blockedX)
                    tunnel.Add((x, 30, x == raisedX ? raisedFloor : floor, 48));
            }
            for (int z = 31; z <= 30 + sideDepth; z++)
                tunnel.Add((Grid / 2, z, floor, 48));
            return Ground(tunnel.ToArray());
        }

        SectionSnapshot?[] through = SolidNeighbours();
        through[0] = TunnelSection();                    // west neighbour
        through[1] = TunnelSection();                    // east neighbour
        SectionSnapshot after = LodCaveCull.FillUnseen(
            TunnelSection(), through, level: 0, reach: 8);

        c.True(HasCavity(after, 32, 30),
            "a straight tunnel crossing the whole local window survives without a nearby mouth");

        SectionSnapshot?[] blocked = SolidNeighbours();
        blocked[0] = TunnelSection(blockedX: 32);
        blocked[1] = TunnelSection();
        SectionSnapshot blockedAfter = LodCaveCull.FillUnseen(
            TunnelSection(), blocked, level: 0, reach: 8);

        c.False(HasCavity(blockedAfter, 32, 30),
            "one blocked side does not turn a hidden dead end into surface-visible geometry");

        SectionSnapshot?[] uneven = SolidNeighbours();
        uneven[0] = TunnelSection(raisedX: 32, raisedFloor: 44);
        uneven[1] = TunnelSection();
        SectionSnapshot unevenAfter = LodCaveCull.FillUnseen(
            TunnelSection(), uneven, level: 0, reach: 8);
        c.True(IsAirAt(unevenAfter, 32, 30, 40),
            "a four-block floor variation beside a proven sightline is not falsely filled");

        SectionSnapshot?[] tooDeep = SolidNeighbours();
        tooDeep[0] = TunnelSection(floor: 39, raisedX: 32, raisedFloor: 44);
        tooDeep[1] = TunnelSection(floor: 39);
        SectionSnapshot boundedAfter = LodCaveCull.FillUnseen(
            TunnelSection(floor: 39), tooDeep, level: 0, reach: 8);
        c.False(IsAirAt(boundedAfter, 32, 30, 39),
            "the clearance guard stops after four blocks instead of expanding through the cave");

        SectionSnapshot?[] shaped = SolidNeighbours();
        shaped[0] = TunnelSection();
        shaped[1] = TunnelSection();
        SectionSnapshot shapedAfter = LodCaveCull.FillUnseen(
            TunnelSection(sideDepth: 5), shaped, level: 0, reach: 8);
        c.True(IsAirAt(shapedAfter, Grid / 2, 34, 44),
            "the four-block guard preserves tunnel structure horizontally and diagonally");
        c.False(IsAirAt(shapedAfter, Grid / 2, 35, 44),
            "horizontal preservation also stops beyond the four-block envelope");
    }

    static void DiagonalAndSlopedSurfaceSightSurvive(Check c)
    {
        var diagonal = new HashSet<(int, int, int, int)>();
        diagonal.Add((10, 10, 40, Surface));
        for (int t = 1; t < 40; t++)
        {
            // Two cells wide so consecutive diagonal slices share faces as well as a sightline.
            diagonal.Add((10 + t, 10 + t, 40, 48));
            diagonal.Add((11 + t, 10 + t, 40, 48));
        }
        SectionSnapshot diagonalAfter = LodCaveCull.FillUnseen(
            Ground(diagonal.ToArray()), SolidNeighbours(), level: 0, reach: 8);
        c.True(HasCavity(diagonalAfter, 35, 35),
            "a 45-degree surface sightline is not limited to cardinal tunnels");

        var slope = new List<(int, int, int, int)> { (10, 30, 30, Surface) };
        for (int t = 1; t < 40; t++)
        {
            int y = 30 + t;
            slope.Add((10 + t, 30, y, y + 2));
        }
        SectionSnapshot slopeAfter = LodCaveCull.FillUnseen(
            Ground(slope.ToArray()), SolidNeighbours(), level: 0, reach: 8);
        c.True(HasCavity(slopeAfter, 35, 30),
            "a sloped surface sightline remains open without an altitude threshold");
    }

    static void SurfaceSightDoesNotTurnAroundACorner(Check c)
    {
        var carved = new List<(int, int, int, int)> { (10, 30, 40, Surface) };
        for (int x = 11; x <= 30; x++) carved.Add((x, 30, 40, 48));
        for (int z = 31; z < 50; z++) carved.Add((30, z, 40, 48));

        SectionSnapshot after = LodCaveCull.FillUnseen(
            Ground(carved.ToArray()), SolidNeighbours(), level: 0, reach: 8);

        c.True(HasCavity(after, 25, 30), "the straight visible leg remains open");
        c.False(HasCavity(after, 30, 45),
            "one sight direction cannot lend visibility to another around a corner");
    }

    /// <summary>
    /// Neither a main route nor a branch is kept merely because the dark network happens to
    /// connect two surface entrances.
    /// </summary>
    static void UndergroundNetworkConnectivityDoesNotMatter(Check c)
    {
        var carved = new List<(int, int, int, int)>();
        for (int x = 10; x < 50; x++) carved.Add((x, 30, 40, 48));
        carved.Add((10, 29, 40, Surface));
        carved.Add((49, 29, 40, Surface));
        for (int z = 31; z < 48; z++) carved.Add((30, z, 40, 48));

        SectionSnapshot after = LodCaveCull.FillUnseen(
            Ground(carved.ToArray()), SolidNeighbours(), level: 0, reach: 8);

        c.False(HasCavity(after, 30, 30),
            "the dark backbone between two tunnel mouths is removable");
        c.False(HasCavity(after, 30, 42),
            "a dark bridge-separated branch with no entrance no longer rescues the whole network");
    }

    /// <summary>
    /// Coarse snapshots have already made their own conservative occupancy decision. The
    /// cave pass now scales its light budget to four represented columns instead of giving
    /// up at L4-L6. It must remove an enclosed coarse cavity while still preserving the two
    /// represented surface opening. A dark through-route has no special protection.
    /// </summary>
    static void CoarseLevelsCarryTheSameConservativeRule(Check c)
    {
        int level = LodWorld.MaxLevel;
        SectionSnapshot sealedCave = Ground((32, 32, 40, 48));
        SectionSnapshot filled = LodCaveCull.FillUnseen(
            sealedCave, SolidNeighbours(), level, LodCaveCull.DefaultReach);
        c.False(HasCavity(filled, 32, 32),
            "an enclosed cavity is culled at the coarsest rendered level");

        SectionSnapshot open = Ground(
            (20, 20, 40, 48),
            (21, 20, 40, Surface));
        SectionSnapshot openAfter = LodCaveCull.FillUnseen(
            open, SolidNeighbours(), level, LodCaveCull.DefaultReach);
        c.True(HasCavity(openAfter, 20, 20),
            "a coarse cave with a represented sky opening remains intact");

        var tunnel = new List<(int, int, int, int)>();
        for (int x = 10; x < 50; x++) tunnel.Add((x, 30, 40, 48));
        tunnel.Add((10, 29, 40, Surface));
        tunnel.Add((49, 29, 40, Surface));

        SectionSnapshot tunnelAfter = LodCaveCull.FillUnseen(
            Ground(tunnel.ToArray()), SolidNeighbours(), level, LodCaveCull.DefaultReach);
        c.False(HasCavity(tunnelAfter, 30, 30),
            "a coarse through-tunnel removes its dark middle");
    }

    static void AColumnWithoutRockIsNeverUsedAsCaveFill(Check c)
    {
        SectionSnapshot waterOnly = Ground((32, 32, 40, 48));
        waterOnly.PaletteFlags[Rock] = LodPaletteEntry.FlagWater;

        SectionSnapshot after = LodCaveCull.FillUnseen(
            waterOnly, SolidNeighbours(), level: 0, reach: LodCaveCull.DefaultReach);

        c.True(HasCavity(after, 32, 32),
            "a dark gap is left open when its column offers only water as fill material");
    }

    static void NothingIsTouchedWithoutIt(Check c)
    {
        SectionSnapshot before = Ground((32, 32, 40, 48));

        c.True(ReferenceEquals(before, LodCaveCull.FillUnseen(
                before, SolidNeighbours(), level: 0, reach: 0)),
            "a reach of zero is the switch being off, and allocates nothing");

        SectionSnapshot open = Ground();
        c.True(ReferenceEquals(open, LodCaveCull.FillUnseen(
                open, SolidNeighbours(), level: 0, reach: LodCaveCull.DefaultReach)),
            "and a section with no cavity at all is returned unchanged");
    }

    /// <summary>
    /// The live integration, not FillUnseen in isolation. The first implementation built
    /// horizontal faces from the filled snapshot but checked vertical coverage against the
    /// original MeshJob.Self. A multi-column cavern then became a lattice of two opposing
    /// walls across every internal air-to-air boundary and produced MORE geometry.
    /// </summary>
    static void MeshingUsesTheFilledSectionForEveryFace(Check c)
    {
        var chamber = new List<(int X, int Z, int Bottom, int Top)>();
        for (int z = 24; z < 32; z++)
        {
            for (int x = 24; x < 32; x++) chamber.Add((x, z, 40, 48));
        }

        SectionSnapshot self = Ground(chamber.ToArray());
        SectionSnapshot?[] neighbours = SolidNeighbours();
        MeshResult plain = Mesh(self, neighbours, reach: 0);
        MeshResult culled = Mesh(self, neighbours, LodCaveCull.DefaultReach);

        c.True(culled.VertexCount < plain.VertexCount,
            "BuildMesh with cave culling removes a sealed multi-column chamber instead of "
            + "manufacturing walls between its filled columns");
        c.True(culled.IndexCount < plain.IndexCount,
            "the live cave-cull path reduces indices as well as vertices");
    }

    /// <summary>
    /// A cave spanning two sections must be classified once across their shared edge. The
    /// current section is rebuilt, but its neighbour remains an immutable original snapshot;
    /// the shared cave classification supplies the neighbour's effective filled coverage.
    /// </summary>
    static void CrossSectionCavesDoNotGrowSeamWalls(Check c)
    {
        var westHalf = new List<(int X, int Z, int Bottom, int Top)>();
        var eastHalf = new List<(int X, int Z, int Bottom, int Top)>();
        for (int z = 24; z < 32; z++)
        {
            for (int x = Grid - 4; x < Grid; x++) westHalf.Add((x, z, 40, 48));
            for (int x = 0; x < 4; x++) eastHalf.Add((x, z, 40, 48));
        }

        SectionSnapshot self = Ground(westHalf.ToArray());
        SectionSnapshot?[] neighbours = SolidNeighbours();
        neighbours[1] = Ground(eastHalf.ToArray());

        MeshResult plain = Mesh(self, neighbours, reach: 0);
        MeshResult culled = Mesh(self, neighbours, LodCaveCull.DefaultReach);

        c.Eq(0, CaveWallsOnX(plain, Grid, 40, 48),
            "the uncullled cross-section fixture has no wall where cave air meets cave air");
        c.Eq(0, CaveWallsOnX(culled, Grid, 40, 48),
            "culling both sides through shared boundary coverage does not grow a section seam wall");
        c.True(culled.VertexCount < plain.VertexCount,
            "a sealed cave crossing a section boundary still reduces the section mesh");
    }

    /// <summary>
    /// The rule fails open in six distinguishable ways, and a screenshot of a surviving
    /// cave cannot say which one saved it. These pin that the counters name the right
    /// reason, because telemetry nothing asserts on can quietly start counting zero.
    /// </summary>
    static void RetentionTelemetryNamesTheReasonEachCellSurvived(Check c)
    {
        // A sealed bubble: eight cells, dark, filled, and nothing else in the section.
        LodCaveCull.ResetRetention();
        LodCaveCull.FillUnseen(Ground((32, 32, 40, 48)), SolidNeighbours(),
            level: 0, reach: LodCaveCull.DefaultReach);
        LodCaveCull.Retention bubble = LodCaveCull.CurrentRetention();
        c.Eq(1L, bubble.Sections, "one completed section is accounted once");
        c.Eq(8L, bubble.Removed.Cells, "and its eight removed cells are counted exactly");
        c.Eq(8L, bubble.SubterraneanCells, "with nothing else underground in the section");
        c.True(bubble.Removed.Faces > 0, "removed cells carry a face-area estimate");

        // A bent through-passage: only its lit ends survive; connectivity cannot turn sight.
        var tunnel = new List<(int, int, int, int)>();
        for (int x = 10; x < 50; x++) tunnel.Add((x, 30, 40, 48));
        tunnel.Add((10, 29, 40, Surface));
        tunnel.Add((49, 29, 40, Surface));

        LodCaveCull.ResetRetention();
        LodCaveCull.FillUnseen(Ground(tunnel.ToArray()), SolidNeighbours(), level: 0, reach: 8);
        LodCaveCull.Retention through = LodCaveCull.CurrentRetention();
        c.Eq(0L, through.Route.Cells, "surface-only retains no dark route between real mouths");
        c.Eq(0L, through.Unknown.Cells,
            "and retains no dark unknown route either");
        c.True(through.Removed.Cells > 0, "the dark middle is counted as removed");
        c.True(through.Lit.Cells > 0, "the lit mouths are counted separately from the dark route");

        var visible = new List<(int, int, int, int)> { (10, 30, 40, Surface) };
        for (int x = 11; x < 50; x++) visible.Add((x, 30, 40, 48));
        LodCaveCull.ResetRetention();
        LodCaveCull.FillUnseen(Ground(visible.ToArray()), SolidNeighbours(), level: 0, reach: 8);
        LodCaveCull.Retention sight = LodCaveCull.CurrentRetention();
        c.True(sight.Sight.Cells > 0,
            "straight dark air preserved from the surface is attributed to sight, not a route");
        c.Eq(0L, sight.Route.Cells, "surface sight does not revive legacy route retention");

        // The same straight passage with its east end running into a neighbour nobody captured.
        // It is not retained as a network route, but its unobstructed line from real exterior air
        // remains visible and therefore fails open through the unknown edge.
        var toNowhere = new List<(int, int, int, int)>();
        for (int x = 20; x < Grid; x++) toNowhere.Add((x, 30, 40, 48));
        toNowhere.Add((20, 29, 40, Surface));

        SectionSnapshot?[] open = SolidNeighbours();
        open[1] = null;                                   // east: absent, therefore unknown

        // A short reach on purpose, for the same reason the through-passage above uses
        // one: at the shipping reach the shaft's light and the invented light pouring out
        // of the absent neighbour meet in the middle and there is no dark stretch left to
        // attribute to anything.
        LodCaveCull.ResetRetention();
        LodCaveCull.FillUnseen(Ground(toNowhere.ToArray()), open, level: 0, reach: 8);
        LodCaveCull.Retention frontier = LodCaveCull.CurrentRetention();
        c.Eq(0L, frontier.Unknown.Cells,
            "an uncaptured neighbour does not revive legacy unknown-route retention");
        c.Eq(0L, frontier.Route.Cells,
            "and no network route is retained");
        c.True(frontier.Sight.Cells > 0,
            "but a straight surface sightline remains open through the unknown edge");

        // A column whose only material is water has no honest rock to fill a gap with.
        SectionSnapshot waterOnly = Ground((32, 32, 40, 48));
        waterOnly.PaletteFlags[Rock] = LodPaletteEntry.FlagWater;

        LodCaveCull.ResetRetention();
        LodCaveCull.FillUnseen(waterOnly, SolidNeighbours(), level: 0, reach: LodCaveCull.DefaultReach);
        LodCaveCull.Retention noRock = LodCaveCull.CurrentRetention();
        c.Eq(8L, noRock.NoFill.Cells, "a refused fill is counted as refused, not as removed");
        c.Eq(0L, noRock.Removed.Cells, "and nothing is claimed removed in that section");
        c.True(noRock.Water.Cells > 0, "fluid runs are counted as never classified at all");

        LodCaveCull.ResetRetention();
        c.Eq(0L, LodCaveCull.CurrentRetention().Sections, "toggling resets the whole tally");
    }

    static MeshResult Mesh(SectionSnapshot self, SectionSnapshot?[] neighbours, int reach) =>
        LodMesher.BuildMesh(new MeshJob
        {
            Key = LodWorld.SectionKey(0, 0, 0),
            Self = self,
            Neighbors = neighbours,
            CaveCullReach = reach,
        });

    static int CaveWallsOnX(MeshResult mesh, float x, float bottom, float top)
    {
        int walls = 0;
        for (int vertex = 0; vertex + 3 < mesh.VertexCount; vertex += 4)
        {
            bool onPlane = true;
            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;
            for (int corner = 0; corner < 4; corner++)
            {
                int at = (vertex + corner) * 3;
                onPlane &= Math.Abs(mesh.Xyz[at] - x) < 0.001f;
                minY = Math.Min(minY, mesh.Xyz[at + 1]);
                maxY = Math.Max(maxY, mesh.Xyz[at + 1]);
            }
            if (onPlane && minY < top && maxY > bottom) walls++;
        }
        return walls;
    }
}
