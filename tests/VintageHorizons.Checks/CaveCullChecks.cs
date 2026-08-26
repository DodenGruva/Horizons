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
        ATunnelThroughAMountainIsKept(c);
        TheRuleDeclinesWhereItCannotSeeFarEnough(c);
        NothingIsTouchedWithoutIt(c);
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
    /// The case a light budget alone cannot handle. The passage is 40 columns long and
    /// daylight reaches 32 blocks, so its middle is dark - but it has a mouth at each end,
    /// which is two ways in, and a passage that goes somewhere is never plugged.
    /// </summary>
    static void ATunnelThroughAMountainIsKept(Check c)
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
        c.True(HasCavity(after, 30, 30),
            "and so does its dark middle, because a passage with two ways in is never plugged");
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
    /// Light is measured in blocks but travels between columns, and a column covers 64
    /// blocks at the coarsest level - so the same reach that crosses 32 columns at full
    /// detail crosses half of one out there, where nothing would spread sideways and every
    /// cavity would read as unreachable. The rule must refuse to run rather than run blind.
    /// </summary>
    static void TheRuleDeclinesWhereItCannotSeeFarEnough(Check c)
    {
        SectionSnapshot before = Ground((32, 32, 40, 48));

        SectionSnapshot coarse = LodCaveCull.FillUnseen(
            before, SolidNeighbours(), level: LodWorld.MaxLevel, reach: LodCaveCull.DefaultReach);
        c.True(ReferenceEquals(before, coarse),
            "a level whose columns are wider than the light can spread is left alone");

        SectionSnapshot fine = LodCaveCull.FillUnseen(
            before, SolidNeighbours(), level: 0, reach: LodCaveCull.DefaultReach);
        c.False(ReferenceEquals(before, fine), "while the finest level is culled normally");
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
}
