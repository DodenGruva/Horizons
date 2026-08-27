namespace VintageHorizons.Checks;

/// <summary>Why one air or water span survived, or did not, under the global rule.</summary>
public enum CaveSpanVerdict
{
    None = 0,

    /// <summary>The component touches an uncaptured column, a missing section, or the edge
    /// of the cache. Nothing about it can be proved, so all of it stays.</summary>
    KeptUnknown,

    /// <summary>Daylight from a real surface portal reaches it within the budget.</summary>
    KeptLit,

    /// <summary>Dark, but on the retained route between two DISTINCT portals.</summary>
    KeptRoute,

    /// <summary>The whole component is enclosed by known terrain and has no portal at all.</summary>
    RemovableSealed,

    /// <summary>One portal, and this span lies beyond the light it lets in.</summary>
    RemovableOverflow,

    /// <summary>Two or more portals, and this span is on a branch no route needs.</summary>
    RemovableBranch,

    /// <summary>Fluid connected to the open air, to unknown space, or to retained cavity.</summary>
    WaterKept,

    /// <summary>Fluid enclosed by known solid and by cavity that is itself removable.</summary>
    WaterRemovable,

    /// <summary>
    /// Lit, but only by light that entered through unknown space. Counted apart from
    /// <see cref="KeptLit"/> so the influence of the frontier stays visible instead of
    /// disappearing into the ordinary daylight line.
    /// </summary>
    KeptLitUnknown,

    /// <summary>
    /// Dark, on a retained route, and only retained because one of the terminals it joins
    /// is a frontier contact rather than a real mouth.
    /// </summary>
    KeptRouteUnknown,

    /// <summary>
    /// On a route by the bridge rule, but dropped by a tightened route rule: too far off
    /// the shortest way between the two mouths, or joining a pair so tortuous that no
    /// straight line through the passage could ever have shown one mouth from the other.
    /// This is the only verdict here that can remove genuinely visible geometry, which is
    /// exactly what the parameter sweep exists to expose.
    /// </summary>
    RemovableRouteTightened,
}

/// <summary>
/// How much of the passage between two mouths a route rule keeps.
///
/// The bridge rule keeps every span of the 2-edge-connected core joining any two mouths,
/// which over the owner's cache retains about a third of all subterranean geometry. The
/// visual requirement is narrower: sight travels in straight lines, so the middle of a long
/// winding passage shows nothing of either end.
/// </summary>
/// <param name="SlackBlocks">
/// CORRIDOR: keep a route span only within this much extra path length of the shortest way
/// between its two mouths, measured in the same cost as the light flood. Negative disables.
/// </param>
/// <param name="Tortuosity">
/// SIGHTLINE: a pair of mouths deserves a route at all only when the passage between them
/// is no more than this multiple of the straight line between them. Zero or less disables.
/// </param>
public readonly record struct CaveRouteRule(double SlackBlocks, double Tortuosity)
{
    public static readonly CaveRouteRule Bridge = new(-1, 0);
    public bool Corridor => SlackBlocks >= 0;
    public bool Sightline => Tortuosity > 0;
    public bool Tightens => Corridor || Sightline;

    public override string ToString() =>
        !Tightens ? "bridge (baseline)"
            : Corridor && Sightline ? $"W={SlackBlocks:0} k={Tortuosity:0.0}"
            : Corridor ? $"W={SlackBlocks:0}" : $"k={Tortuosity:0.0}";
}

/// <summary>
/// What to do where the cache runs out.
/// </summary>
public enum CaveFrontier
{
    /// <summary>
    /// The original stance: a component touching an uncaptured column, a missing section,
    /// or the bounding-box edge is undecidable in its entirety and is kept whole. Safe by
    /// construction, and measured to leave about 30% of the geometry undecided, because
    /// "unknown" is a property of a whole component and components are enormous.
    /// </summary>
    Open,

    /// <summary>
    /// The stance the shipping rule already takes, applied globally. `LodCaveCull` treats
    /// its own window wall and every uncaptured column as a LIGHT SOURCE and still fills
    /// dark air beyond that light's reach. A frontier contact therefore behaves exactly
    /// like a cave mouth: it lights what is near it and it acts as a terminal that routes
    /// must reach - but it does not save a cave five hundred blocks behind it.
    /// </summary>
    Portal,
}

/// <summary>
/// A cache-wide prototype of the classifier session 58 recommended, built to answer ONE
/// question before anybody writes the runtime version of it: how much more could a global
/// rule remove than the local 3x3 rule already does?
///
/// WHY IT IS NOT THE SHIPPING RULE. It reads the whole cache at once, off the game thread,
/// with no budget and no invalidation story. That is exactly what makes it useful here: it
/// is the CEILING the runtime design would be chasing, measured rather than argued.
///
/// THE MODEL, in the order it is built.
///
/// 1. NODES ARE SPANS, NOT COLUMNS. A node is one maximal vertical gap of air between two
///    stored runs in one column. The live rule contracts every dark height in a column into
///    a single graph node, which merges cave layers that merely share an X/Z and makes dead
///    branches look cyclic (G109). Spans keep those layers apart.
///
/// 2. EDGES JOIN SPANS IN 4-ADJACENT COLUMNS WHOSE Y RANGES OVERLAP. Nothing else. Two
///    spans stacked in the same column are separated by rock and are not neighbours.
///
/// 3. EXTERIOR IS THE AIR ABOVE EACH CAPTURED COLUMN'S TOP RUN. A span is PORTAL-ADJACENT
///    when a neighbouring column's exterior reaches into its Y range. Portal-adjacent spans
///    that touch each other are ONE portal - that is the whole point of carrying portal
///    identity rather than counting light-expiration patches, because a single rough cave
///    mouth produces several of those and falsely reads as a through-tunnel (G108).
///
/// 4. LIGHT IS A BUDGET FLOOD FROM PORTALS ONLY, carrying the identity of its portal. A hop
///    to an adjacent column costs the column's width, matching the live sideways cost, and
///    falling inside a span is free, matching the live free downward step. It does NOT
///    charge for climbing, so this over-lights compared with the live per-voxel flood: the
///    extra removal reported below is therefore a FLOOR, not an optimistic estimate.
///
/// 5. THE FRONTIER IS A CHOICE, and both answers are measurable - see
///    <see cref="CaveFrontier"/>. Under <c>Open</c> any component touching unknown space is
///    kept whole; that answered the first question, which was whether a global classifier
///    could decide anything at all, and the answer was that it leaves about 30% of the
///    geometry undecided because one uncaptured column taints an entire network. Under
///    <c>Portal</c> a frontier contact is a PSEUDO-PORTAL: it lights and it terminates,
///    which is precisely the stance `LodCaveCull` already ships. Retention caused by
///    pseudo-portals is reported on its own lines either way.
///
/// 6. WATER IS ITS OWN GRAPH. Capture stores fluid as occupied runs, so the air pass cannot
///    see it at all (G110). Water components are labelled separately and kept whenever they
///    touch open sky, unknown space, or cavity air that the air pass itself retained.
/// </summary>
public sealed class CaveSpanGraph
{
    public const int DefaultReach = 32;
    const int Grid = LodSection.GridSize;

    /// <summary>One span, in absolute column coordinates, with its verdict and face area.</summary>
    public readonly record struct SpanInfo(
        int Gx, int Gz, int Bottom, int Top, long Faces, CaveSpanVerdict Verdict);

    readonly int minSx, minSz, wCols, hCols;
    readonly bool[] known;
    readonly int[] columnTop;
    readonly int[] airStart, airBottom, airTop, airColumn;
    readonly int[] waterStart, waterBottom, waterTop, waterColumn;
    readonly CaveSpanVerdict[] airVerdict, waterVerdict;
    readonly int[] airComponent;
    readonly Dictionary<int, int> portalsOf = new();
    readonly Dictionary<int, int> terminalsOf = new();
    readonly HashSet<int> unknownComponents = new();
    readonly CaveFrontier frontier;

    // Route tightening. `baselineVerdict` is the bridge-rule answer, kept so a sweep can
    // apply many settings to one graph without rebuilding it; every ApplyRouteRule call
    // starts from it rather than from the last setting's output.
    CaveSpanVerdict[] baselineVerdict = Array.Empty<CaveSpanVerdict>();
    int[] labelId = Array.Empty<int>();           // nearest N terminals, by identity
    int[] labelDistance = Array.Empty<int>();     // and how far each of them is, in hops
    byte[] labelCount = Array.Empty<byte>();
    int[] minimumSlack = Array.Empty<int>();
    readonly Dictionary<long, int> pairGeodesic = new();
    readonly Dictionary<long, double> pairStraight = new();
    int routeStep = 1;
    readonly int labels;

    public CaveFrontier Frontier => frontier;
    public CaveRouteRule RouteRule { get; private set; } = CaveRouteRule.Bridge;
    public int TerminalPairs => pairGeodesic.Count;

    /// <summary>
    /// How many distinct nearby mouths each span remembers.
    ///
    /// This is the knob that decides how faithfully the corridor rule implements "within
    /// slack of SOME shortest terminal-to-terminal path". With two labels a span is judged
    /// only against its own two nearest mouths, which is STRICTER than the rule as stated -
    /// a span sitting on a longer passage between two further mouths is shed. Every extra
    /// label can only keep more, so raising it walks toward the literal rule from the
    /// aggressive side, and the sweep reports the sensitivity rather than assuming it away.
    /// </summary>
    public int TerminalLabels => labels;

    public int AirSpanCount => airBottom.Length;
    public int WaterSpanCount => waterBottom.Length;
    public int AirEdgeCount { get; private set; }
    public int ColumnCount => known.Length;

    // Why these are worth reporting: "unknown" is not a property of a cell, it is a
    // property of the whole component a cell belongs to. One uncaptured column anywhere in
    // a cave network makes the entire network undecidable, however far away the rest of it
    // is. The gap between FrontierColumns and the share of geometry kept as unknown is the
    // measure of how far that contamination travels.
    public int CapturedColumns { get; private set; }
    public int FrontierColumns { get; private set; }
    public int AirComponents { get; private set; }
    public int UnknownAirComponents => unknownComponents.Count;
    public int LargestComponentSpans { get; private set; }

    // ---------------------------------------------------------------------------------
    // Queries the fixtures ask. Coordinates are ABSOLUTE column coordinates - the same
    // sx * 64 + cx a section key implies - and a Y in blocks.
    // ---------------------------------------------------------------------------------

    /// <summary>The verdict on the air span containing this block, or None if it is not air.</summary>
    public CaveSpanVerdict AirVerdictAt(int gx, int gz, int y)
    {
        int span = FindSpan(airStart, airBottom, airTop, gx, gz, y);
        return span < 0 ? CaveSpanVerdict.None : airVerdict[span];
    }

    /// <summary>The verdict on the water span containing this block, or None if it is not water.</summary>
    public CaveSpanVerdict WaterVerdictAt(int gx, int gz, int y)
    {
        int span = FindSpan(waterStart, waterBottom, waterTop, gx, gz, y);
        return span < 0 ? CaveSpanVerdict.None : waterVerdict[span];
    }

    /// <summary>
    /// How many DISTINCT REAL portals the air component containing this block reaches, or
    /// -1 when the component was never asked the question because Open semantics had
    /// already written it off as unknown.
    /// </summary>
    public int PortalsAt(int gx, int gz, int y)
    {
        int span = FindSpan(airStart, airBottom, airTop, gx, gz, y);
        if (span < 0) return -2;
        int component = airComponent[span];
        if (frontier == CaveFrontier.Open && unknownComponents.Contains(component)) return -1;
        return portalsOf.TryGetValue(component, out int portals) ? portals : 0;
    }

    /// <summary>
    /// Distinct terminals of ANY kind - real mouths plus pseudo-portals at the frontier.
    /// Under Open semantics this is the same number as <see cref="PortalsAt"/>, because
    /// frontier contacts are not terminals there.
    /// </summary>
    public int TerminalsAt(int gx, int gz, int y)
    {
        int span = FindSpan(airStart, airBottom, airTop, gx, gz, y);
        if (span < 0) return -2;
        return terminalsOf.TryGetValue(airComponent[span], out int terminals) ? terminals : 0;
    }

    /// <summary>Whether two air blocks belong to the same global component.</summary>
    public bool SameComponent(int gx1, int gz1, int y1, int gx2, int gz2, int y2)
    {
        int a = FindSpan(airStart, airBottom, airTop, gx1, gz1, y1);
        int b = FindSpan(airStart, airBottom, airTop, gx2, gz2, y2);
        return a >= 0 && b >= 0 && airComponent[a] == airComponent[b];
    }

    int FindSpan(int[] start, int[] bottom, int[] top, int gx, int gz, int y)
    {
        int col = ColumnOf(gx, gz);
        if (col < 0) return -1;
        for (int s = start[col]; s < start[col + 1]; s++)
        {
            if (y >= bottom[s] && y < top[s]) return s;
        }
        return -1;
    }

    int ColumnOf(int gx, int gz)
    {
        int x = gx - minSx * Grid;
        int z = gz - minSz * Grid;
        if (x < 0 || x >= wCols || z < 0 || z >= hCols) return -1;
        return z * wCols + x;
    }

    public IEnumerable<SpanInfo> AirSpans()
    {
        for (int s = 0; s < airBottom.Length; s++)
        {
            int col = airColumn[s];
            yield return new SpanInfo(
                minSx * Grid + col % wCols, minSz * Grid + col / wCols,
                airBottom[s], airTop[s], AirFaces(s), airVerdict[s]);
        }
    }

    /// <summary>
    /// The air spans of one column. The local-rule comparison walks section by section, so
    /// it needs to ask about a column rather than stream the whole cache in span order.
    /// </summary>
    public IEnumerable<SpanInfo> AirSpansInColumn(int gx, int gz)
    {
        int col = ColumnOf(gx, gz);
        if (col < 0) yield break;
        for (int s = airStart[col]; s < airStart[col + 1]; s++)
        {
            yield return new SpanInfo(gx, gz, airBottom[s], airTop[s], AirFaces(s), airVerdict[s]);
        }
    }

    public IEnumerable<SpanInfo> WaterSpansInColumn(int gx, int gz)
    {
        int col = ColumnOf(gx, gz);
        if (col < 0) yield break;
        for (int s = waterStart[col]; s < waterStart[col + 1]; s++)
        {
            yield return new SpanInfo(gx, gz, waterBottom[s], waterTop[s],
                WaterFaces(s), waterVerdict[s]);
        }
    }

    // ---------------------------------------------------------------------------------
    // Span-indexed access. A parameter sweep re-tallies the same spans a dozen times, so
    // it walks them by index once rather than re-deriving them from sections each pass.
    // ---------------------------------------------------------------------------------

    public (int Start, int End) AirSpanRange(int gx, int gz)
    {
        int col = ColumnOf(gx, gz);
        return col < 0 ? (0, 0) : (airStart[col], airStart[col + 1]);
    }

    public int AirSpanBottom(int s) => airBottom[s];
    public int AirSpanTop(int s) => airTop[s];
    public int AirSpanCells(int s) => airTop[s] - airBottom[s];
    public long AirSpanFaces(int s) => AirFaces(s);
    public CaveSpanVerdict AirSpanVerdict(int s) => airVerdict[s];

    public int WaterSpanCells(int s) => waterTop[s] - waterBottom[s];
    public long WaterSpanFaces(int s) => WaterFaces(s);
    public CaveSpanVerdict WaterSpanVerdict(int s) => waterVerdict[s];

    public IEnumerable<SpanInfo> WaterSpans()
    {
        for (int s = 0; s < waterBottom.Length; s++)
        {
            int col = waterColumn[s];
            yield return new SpanInfo(
                minSx * Grid + col % wCols, minSz * Grid + col / wCols,
                waterBottom[s], waterTop[s], WaterFaces(s), waterVerdict[s]);
        }
    }

    // ---------------------------------------------------------------------------------
    // Face area, the same estimator the live telemetry uses
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Unit cell-faces where this air span meets solid. The mesher greedily merges those
    /// before it emits anything, so faces x 4 is an upper bound on vertices rather than a
    /// prediction - it exists to weight a cell by how much geometry it is responsible for.
    /// </summary>
    long AirFaces(int s)
    {
        int col = airColumn[s];
        int lo = airBottom[s], hi = airTop[s];
        long faces = 1;                                  // a run always caps a span above
        if (lo > 0) faces++;                             // and below, unless it is world bottom
        int x = col % wCols, z = col / wCols;
        if (x > 0) faces += (hi - lo) - AirOverlap(col - 1, lo, hi);
        if (x < wCols - 1) faces += (hi - lo) - AirOverlap(col + 1, lo, hi);
        if (z > 0) faces += (hi - lo) - AirOverlap(col - wCols, lo, hi);
        if (z < hCols - 1) faces += (hi - lo) - AirOverlap(col + wCols, lo, hi);
        return faces;
    }

    /// <summary>Unit cell-faces where this water span meets air, which is what a viewer sees.</summary>
    long WaterFaces(int s)
    {
        int col = waterColumn[s];
        int lo = waterBottom[s], hi = waterTop[s];
        long faces = 0;
        if (IsAir(col, hi)) faces++;
        if (lo > 0 && IsAir(col, lo - 1)) faces++;
        int x = col % wCols, z = col / wCols;
        if (x > 0) faces += AirOverlap(col - 1, lo, hi);
        if (x < wCols - 1) faces += AirOverlap(col + 1, lo, hi);
        if (z > 0) faces += AirOverlap(col - wCols, lo, hi);
        if (z < hCols - 1) faces += AirOverlap(col + wCols, lo, hi);
        return faces;
    }

    /// <summary>How many blocks of [lo,hi) are air in this column, exterior sky included.</summary>
    int AirOverlap(int col, int lo, int hi)
    {
        if (!known[col]) return hi - lo;                  // uncaptured: all air, deliberately
        int overlap = Math.Max(0, hi - Math.Max(lo, columnTop[col]));
        for (int s = airStart[col]; s < airStart[col + 1]; s++)
        {
            overlap += Math.Max(0, Math.Min(hi, airTop[s]) - Math.Max(lo, airBottom[s]));
        }
        return overlap;
    }

    bool IsAir(int col, int y)
    {
        if (!known[col] || y >= columnTop[col]) return true;
        for (int s = airStart[col]; s < airStart[col + 1]; s++)
        {
            if (y >= airBottom[s] && y < airTop[s]) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------------------------
    // Construction
    // ---------------------------------------------------------------------------------

    public static CaveSpanGraph Build(
        IReadOnlyDictionary<(int Sx, int Sz), SectionSnapshot> sections, int level, int reach,
        CaveFrontier frontier = CaveFrontier.Open, CaveRouteRule? route = null,
        int terminalLabels = 4)
        => new CaveSpanGraph(sections, level, reach, frontier,
            route ?? CaveRouteRule.Bridge, terminalLabels, prepareRoutes: true);

    /// <summary>
    /// The owner's surface-only rule: retain air reached by the conservative surface flood
    /// and remove every dark span, regardless of whether it connects two cave entrances.
    /// Route labels are deliberately never built.
    /// </summary>
    public static CaveSpanGraph BuildSurfaceOnly(
        IReadOnlyDictionary<(int Sx, int Sz), SectionSnapshot> sections, int level, int reach,
        CaveFrontier frontier = CaveFrontier.Portal)
        => new CaveSpanGraph(sections, level, reach, frontier,
            CaveRouteRule.Bridge, terminalLabels: 2, prepareRoutes: false);

    CaveSpanGraph(IReadOnlyDictionary<(int Sx, int Sz), SectionSnapshot> sections,
        int level, int reach, CaveFrontier frontier, CaveRouteRule route, int terminalLabels,
        bool prepareRoutes)
    {
        this.frontier = frontier;
        labels = Math.Clamp(terminalLabels, 2, 16);
        if (sections.Count == 0) throw new ArgumentException("no sections to classify");

        int maxSx = int.MinValue, maxSz = int.MinValue;
        minSx = minSz = int.MaxValue;
        foreach ((int sx, int sz) in sections.Keys)
        {
            minSx = Math.Min(minSx, sx);
            maxSx = Math.Max(maxSx, sx);
            minSz = Math.Min(minSz, sz);
            maxSz = Math.Max(maxSz, sz);
        }

        wCols = (maxSx - minSx + 1) * Grid;
        hCols = (maxSz - minSz + 1) * Grid;
        long columns = (long)wCols * hCols;
        if (columns > int.MaxValue / 2)
        {
            throw new InvalidOperationException(
                $"{wCols}x{hCols} columns is too wide a bounding box to classify at once");
        }

        known = new bool[columns];
        columnTop = new int[columns];
        airStart = new int[columns + 1];
        waterStart = new int[columns + 1];

        // Pass one: how many spans each column holds, so the arrays can be exact.
        foreach (((int sx, int sz), SectionSnapshot section) in sections)
        {
            for (int cz = 0; cz < Grid; cz++)
            {
                for (int cx = 0; cx < Grid; cx++)
                {
                    int index = LodSection.ColumnIndex(cx, cz);
                    if (!section.Captured[index]) continue;
                    Span<ulong> runs = section.ColumnRuns(index);
                    if (runs.Length == 0) continue;

                    int col = ((sz - minSz) * Grid + cz) * wCols + (sx - minSx) * Grid + cx;
                    known[col] = true;
                    columnTop[col] = LodSection.RunYTop(runs[0]);

                    int air = 0;
                    for (int r = 1; r < runs.Length; r++)
                    {
                        if (LodSection.RunYBottom(runs[r - 1]) > LodSection.RunYTop(runs[r])) air++;
                    }
                    if (LodSection.RunYBottom(runs[^1]) > 0) air++;
                    airStart[col + 1] = air;
                    waterStart[col + 1] = CountWaterSpans(section, runs);
                }
            }
        }

        for (int i = 0; i < columns; i++)
        {
            airStart[i + 1] += airStart[i];
            waterStart[i + 1] += waterStart[i];
        }

        airBottom = new int[airStart[columns]];
        airTop = new int[airStart[columns]];
        airColumn = new int[airStart[columns]];
        waterBottom = new int[waterStart[columns]];
        waterTop = new int[waterStart[columns]];
        waterColumn = new int[waterStart[columns]];

        // Pass two: fill them, in exactly the order pass one counted.
        foreach (((int sx, int sz), SectionSnapshot section) in sections)
        {
            for (int cz = 0; cz < Grid; cz++)
            {
                for (int cx = 0; cx < Grid; cx++)
                {
                    int index = LodSection.ColumnIndex(cx, cz);
                    if (!section.Captured[index]) continue;
                    Span<ulong> runs = section.ColumnRuns(index);
                    if (runs.Length == 0) continue;

                    int col = ((sz - minSz) * Grid + cz) * wCols + (sx - minSx) * Grid + cx;

                    int at = airStart[col];
                    for (int r = 1; r < runs.Length; r++)
                    {
                        int lo = LodSection.RunYTop(runs[r]);
                        int hi = LodSection.RunYBottom(runs[r - 1]);
                        if (hi <= lo) continue;
                        airBottom[at] = lo;
                        airTop[at] = hi;
                        airColumn[at] = col;
                        at++;
                    }
                    int bottom = LodSection.RunYBottom(runs[^1]);
                    if (bottom > 0)
                    {
                        airBottom[at] = 0;
                        airTop[at] = bottom;
                        airColumn[at] = col;
                    }

                    at = waterStart[col];
                    int waterTopY = -1, waterBottomY = -1;
                    for (int r = 0; r < runs.Length; r++)
                    {
                        if (IsWater(section, runs[r]))
                        {
                            if (waterTopY >= 0 && LodSection.RunYTop(runs[r]) == waterBottomY)
                            {
                                waterBottomY = LodSection.RunYBottom(runs[r]);
                                continue;
                            }
                            if (waterTopY >= 0) Emit();
                            waterTopY = LodSection.RunYTop(runs[r]);
                            waterBottomY = LodSection.RunYBottom(runs[r]);
                        }
                        else if (waterTopY >= 0)
                        {
                            Emit();
                            waterTopY = -1;
                        }
                    }
                    if (waterTopY >= 0) Emit();

                    void Emit()
                    {
                        waterBottom[at] = waterBottomY;
                        waterTop[at] = waterTopY;
                        waterColumn[at] = col;
                        at++;
                    }
                }
            }
        }

        airVerdict = new CaveSpanVerdict[airBottom.Length];
        waterVerdict = new CaveSpanVerdict[waterBottom.Length];
        airComponent = new int[airBottom.Length];

        ClassifyAir(level, reach, prepareRoutes);
        if (prepareRoutes) ApplyRouteRule(route);
        else ApplySurfaceOnly();
    }

    static bool IsWater(SectionSnapshot section, ulong run) =>
        (section.PaletteFlags[LodSection.RunPaletteId(run)] & LodPaletteEntry.FlagWater) != 0;

    static int CountWaterSpans(SectionSnapshot section, Span<ulong> runs)
    {
        int count = 0;
        int pendingBottom = -1;
        for (int r = 0; r < runs.Length; r++)
        {
            if (!IsWater(section, runs[r])) { pendingBottom = -1; continue; }
            if (pendingBottom >= 0 && LodSection.RunYTop(runs[r]) == pendingBottom)
            {
                pendingBottom = LodSection.RunYBottom(runs[r]);
                continue;
            }
            count++;
            pendingBottom = LodSection.RunYBottom(runs[r]);
        }
        return count;
    }

    // ---------------------------------------------------------------------------------
    // Classification
    // ---------------------------------------------------------------------------------

    void ClassifyAir(int level, int reach, bool prepareRoutes)
    {
        int step = LodWorld.ColumnStepBlocks(level);
        int spans = airBottom.Length;
        if (spans == 0) return;

        var portalAdjacent = new bool[spans];
        var unknownAdjacent = new bool[spans];

        // Flags need all four neighbours; edges need only two, because an edge found from
        // the east is the same edge found from the west.
        var edgeA = new List<int>();
        var edgeB = new List<int>();

        for (int col = 0; col < known.Length; col++)
        {
            if (airStart[col] == airStart[col + 1]) continue;
            int x = col % wCols, z = col / wCols;

            Side(x > 0 ? col - 1 : -1, false);
            Side(x < wCols - 1 ? col + 1 : -1, true);
            Side(z > 0 ? col - wCols : -1, false);
            Side(z < hCols - 1 ? col + wCols : -1, true);

            void Side(int n, bool link)
            {
                if (n < 0 || !known[n])
                {
                    // Outside the loaded box, or a column capture never reached. Either way
                    // it is air we invented, and everything beside it is unprovable.
                    for (int s = airStart[col]; s < airStart[col + 1]; s++) unknownAdjacent[s] = true;
                    return;
                }

                int nTop = columnTop[n];
                for (int s = airStart[col]; s < airStart[col + 1]; s++)
                {
                    if (airTop[s] > nTop) portalAdjacent[s] = true;
                    if (!link) continue;
                    for (int t = airStart[n]; t < airStart[n + 1]; t++)
                    {
                        if (airTop[t] <= airBottom[s] || airTop[s] <= airBottom[t]) continue;
                        edgeA.Add(s);
                        edgeB.Add(t);
                    }
                }
            }
        }

        int[] ea = edgeA.ToArray();
        int[] eb = edgeB.ToArray();
        AirEdgeCount = ea.Length;

        // Components, and terminal identity. A portal is a connected group of
        // portal-adjacent spans: one rough mouth is one portal however many spans it
        // grazes, and two mouths at opposite ends of a tunnel stay two because dark spans
        // lie between them. A pseudo-portal is grouped the same way over frontier contacts,
        // for the same reason - one ragged edge of the cache is one terminal, not thirty.
        //
        // The two identity spaces are kept SEPARATE. A span that is both a real mouth and a
        // frontier contact contributes two distinct terminals, which is correct: the route
        // between them is a route you could see daylight along.
        var portalGroup = new int[spans];
        var frontierGroup = new int[spans];
        for (int i = 0; i < spans; i++) airComponent[i] = portalGroup[i] = frontierGroup[i] = i;
        for (int e = 0; e < ea.Length; e++)
        {
            Union(airComponent, ea[e], eb[e]);
            if (portalAdjacent[ea[e]] && portalAdjacent[eb[e]]) Union(portalGroup, ea[e], eb[e]);
            if (unknownAdjacent[ea[e]] && unknownAdjacent[eb[e]]) Union(frontierGroup, ea[e], eb[e]);
        }
        for (int i = 0; i < spans; i++)
        {
            airComponent[i] = Find(airComponent, i);
            portalGroup[i] = Find(portalGroup, i);
            frontierGroup[i] = Find(frontierGroup, i);
        }

        bool pseudo = frontier == CaveFrontier.Portal;
        var terminalAdjacent = new bool[spans];

        var pairs = new List<long>();          // real portals only
        var allPairs = new List<long>();       // real portals plus pseudo-portals
        var spansPerComponent = new int[spans];
        for (int s = 0; s < spans; s++)
        {
            if (unknownAdjacent[s]) unknownComponents.Add(airComponent[s]);
            if (portalAdjacent[s])
            {
                pairs.Add(((long)airComponent[s] << 32) | (uint)portalGroup[s]);
                allPairs.Add(((long)airComponent[s] << 32) | (uint)portalGroup[s]);
                terminalAdjacent[s] = true;
            }
            if (pseudo && unknownAdjacent[s])
            {
                // Offset by `spans` so a pseudo-portal can never collide with a real
                // portal id: both are span indices, and a span can hold one of each.
                allPairs.Add(((long)airComponent[s] << 32) | (uint)(spans + frontierGroup[s]));
                terminalAdjacent[s] = true;
            }
            if (spansPerComponent[airComponent[s]]++ == 0) AirComponents++;
        }
        for (int i = 0; i < spans; i++)
        {
            LargestComponentSpans = Math.Max(LargestComponentSpans, spansPerComponent[i]);
        }

        for (int col = 0; col < known.Length; col++)
        {
            if (!known[col]) continue;
            CapturedColumns++;
            int x = col % wCols, z = col / wCols;
            bool frontier = x == 0 || x == wCols - 1 || z == 0 || z == hCols - 1
                || !known[col - 1] || !known[col + 1]
                || !known[col - wCols] || !known[col + wCols];
            if (frontier) FrontierColumns++;
        }
        Count(pairs, portalsOf);
        Count(allPairs, terminalsOf);

        Adjacency adjacency = BuildAdjacency(spans, ea, eb);

        // Run the flood and the peel TWICE when pseudo-portals are in play: once from real
        // mouths alone, once from every terminal. A span kept by the second and not by the
        // first is kept BECAUSE OF the frontier, and that is an exact attribution rather
        // than a guess from what its component happens to contain.
        bool[] litReal = SpreadFromPortals(portalAdjacent, ea, eb, adjacency, reach, step);
        bool[] peeledReal = PeelBranches(portalAdjacent, ea, eb, adjacency);
        bool[] litAll = pseudo
            ? SpreadFromPortals(terminalAdjacent, ea, eb, adjacency, reach, step)
            : litReal;
        bool[] peeledAll = pseudo
            ? PeelBranches(terminalAdjacent, ea, eb, adjacency)
            : peeledReal;

        for (int s = 0; s < spans; s++)
        {
            int component = airComponent[s];
            if (!pseudo && unknownComponents.Contains(component))
            {
                airVerdict[s] = CaveSpanVerdict.KeptUnknown;
                continue;
            }
            terminalsOf.TryGetValue(component, out int terminals);
            if (terminals == 0) { airVerdict[s] = CaveSpanVerdict.RemovableSealed; continue; }
            if (litAll[s])
            {
                airVerdict[s] = litReal[s] ? CaveSpanVerdict.KeptLit : CaveSpanVerdict.KeptLitUnknown;
                continue;
            }
            if (terminals == 1) { airVerdict[s] = CaveSpanVerdict.RemovableOverflow; continue; }
            if (peeledAll[s]) { airVerdict[s] = CaveSpanVerdict.RemovableBranch; continue; }
            airVerdict[s] = peeledReal[s]
                ? CaveSpanVerdict.KeptRouteUnknown
                : CaveSpanVerdict.KeptRoute;
        }

        baselineVerdict = (CaveSpanVerdict[])airVerdict.Clone();
        routeStep = step;
        if (prepareRoutes)
        {
            MeasureRoutes(terminalAdjacent, portalAdjacent, unknownAdjacent,
                portalGroup, frontierGroup, pseudo, ea, eb, adjacency, step);
        }
    }

    /// <summary>
    /// Remove dark underground geometry even when it belongs to a route between entrances.
    /// Lit spans and component-conservative unknown spans keep their existing verdicts.
    /// </summary>
    public void ApplySurfaceOnly()
    {
        Array.Copy(baselineVerdict, airVerdict, airVerdict.Length);
        for (int s = 0; s < airVerdict.Length; s++)
        {
            if (airVerdict[s] is CaveSpanVerdict.KeptRoute
                or CaveSpanVerdict.KeptRouteUnknown)
            {
                airVerdict[s] = CaveSpanVerdict.RemovableRouteTightened;
            }
        }

        Array.Clear(waterVerdict, 0, waterVerdict.Length);
        ClassifyWater();
    }

    /// <summary>
    /// Everything a tightened route rule needs, computed once for every setting a sweep
    /// will try: for each span, how long the shortest terminal-to-terminal path THROUGH it
    /// is and which two mouths it joins; and for each pair of mouths, the length of the
    /// passage between them and the straight line between them.
    ///
    /// COST MODEL. Every graph edge costs exactly one column width - the same sideways cost
    /// the light flood uses - and moving inside a span is free, the same free fall. Uniform
    /// weights mean this is breadth-first search, not Dijkstra: O(V+E) with two labels per
    /// span, which is what makes a whole-cache sweep affordable.
    ///
    /// CONSERVATISM. Both quantities are computed so that any error KEEPS geometry:
    ///
    /// - `throughBlocks` is the length of a real path, exact by construction.
    /// - the pair distance is the minimum of two upper bounds - the best `through` seen for
    ///   that pair, and the classic Voronoi boundary-edge value, which is EXACT for the
    ///   adjacent terminal pairs that actually define passages. Taking the smaller keeps it
    ///   as tight as this machinery allows. The corridor test compares `through` against
    ///   pair distance + slack, so an over-stated pair distance widens the corridor and
    ///   keeps MORE. The sightline test divides pair distance by the straight line, so an
    ///   over-stated pair distance would disqualify more; using the minimum of the two
    ///   bounds is what holds that error down, and the residual is stated in the report.
    /// </summary>
    void MeasureRoutes(bool[] terminalAdjacent, bool[] portalAdjacent, bool[] unknownAdjacent,
        int[] portalGroup, int[] frontierGroup, bool pseudo,
        int[] ea, int[] eb, Adjacency adjacency, int step)
    {
        int spans = airBottom.Length;
        int n = labels;
        labelId = new int[(long)spans * n <= int.MaxValue ? spans * n : 0];
        labelDistance = new int[labelId.Length];
        labelCount = new byte[spans];
        if (labelId.Length == 0) return;

        // FIFO of (span, slot). Uniform edge weights make plain BFS order correct, and each
        // span is admitted at most `labels` times, so the queue can be a flat array.
        var fifo = new int[labelId.Length];
        int head = 0, tail = 0;
        int slotBits = n <= 2 ? 1 : n <= 4 ? 2 : n <= 8 ? 3 : 4;

        void Admit(int s, int id, int distance)
        {
            int have = labelCount[s];
            if (have >= n) return;
            int at = s * n;
            for (int i = 0; i < have; i++)
            {
                if (labelId[at + i] == id) return;      // this mouth already reached it sooner
            }
            labelId[at + have] = id;
            labelDistance[at + have] = distance;
            labelCount[s] = (byte)(have + 1);
            fifo[tail++] = (s << slotBits) | have;
        }

        for (int s = 0; s < spans; s++)
        {
            if (!terminalAdjacent[s]) continue;
            if (portalAdjacent[s]) Admit(s, portalGroup[s], 0);
            if (pseudo && unknownAdjacent[s]) Admit(s, spans + frontierGroup[s], 0);
        }

        int slotMask = (1 << slotBits) - 1;
        while (head < tail)
        {
            int packed = fifo[head++];
            int node = packed >> slotBits;
            int slot = packed & slotMask;
            int id = labelId[node * n + slot];
            int distance = labelDistance[node * n + slot] + 1;

            for (int i = adjacency.Start[node]; i < adjacency.Start[node + 1]; i++)
            {
                int e = adjacency.Edge[i];
                Admit(ea[e] == node ? eb[e] : ea[e], id, distance);
            }
        }

        // Every pair of mouths that meets at some span: the shortest passage between them
        // is at most the shortest path through the span where they meet.
        for (int s = 0; s < spans; s++)
        {
            int have = labelCount[s];
            int at = s * n;
            for (int i = 0; i < have; i++)
            {
                for (int j = i + 1; j < have; j++)
                {
                    int through = (labelDistance[at + i] + labelDistance[at + j]) * step;
                    long key = PairKey(labelId[at + i], labelId[at + j]);
                    if (!pairGeodesic.TryGetValue(key, out int had) || through < had)
                    {
                        pairGeodesic[key] = through;
                    }
                }
            }
        }

        // Voronoi boundary edges. For two mouths whose nearest-cells touch, this is the
        // EXACT shortest passage between them, and it is usually shorter than any `through`.
        for (int e = 0; e < ea.Length; e++)
        {
            int u = ea[e], v = eb[e];
            if (labelCount[u] == 0 || labelCount[v] == 0) continue;
            int a = labelId[u * n], b = labelId[v * n];
            if (a == b) continue;
            int candidate = (labelDistance[u * n] + 1 + labelDistance[v * n]) * step;
            long key = PairKey(a, b);
            if (!pairGeodesic.TryGetValue(key, out int had) || candidate < had)
            {
                pairGeodesic[key] = candidate;
            }
        }

        MeasureMinimumSlack(spans, n, step);
        MeasureTerminalPositions(terminalAdjacent, portalAdjacent, unknownAdjacent,
            portalGroup, frontierGroup, pseudo, spans, step);
    }

    /// <summary>
    /// The corridor sweep changes only W. The expensive mouth-pair work does not, so pay
    /// for it once and reduce every corridor-only setting to one array lookup per span.
    ///
    /// A missing pair stays at MaxValue and therefore keeps no route by itself. Spans with
    /// fewer than two labels still fail open in <see cref="ApplyRouteRule"/>, exactly as
    /// before. This is the same nearest-label approximation as the original loop; it only
    /// changes when the arithmetic is performed.
    /// </summary>
    void MeasureMinimumSlack(int spans, int n, int step)
    {
        minimumSlack = new int[spans];
        Array.Fill(minimumSlack, int.MaxValue);

        for (int s = 0; s < spans; s++)
        {
            int have = labelCount[s];
            if (have < 2) continue;

            int at = s * n;
            int best = int.MaxValue;
            for (int i = 0; i < have; i++)
            {
                for (int j = i + 1; j < have; j++)
                {
                    long key = PairKey(labelId[at + i], labelId[at + j]);
                    if (!pairGeodesic.TryGetValue(key, out int geodesic)) continue;
                    int through = (labelDistance[at + i] + labelDistance[at + j]) * step;
                    best = Math.Min(best, through - geodesic);
                }
            }
            minimumSlack[s] = best;
        }
    }

    static long PairKey(int a, int b) =>
        ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);

    /// <summary>
    /// Where each mouth is, so the sightline rule has a straight line to compare against.
    /// A mouth is a group of spans; its position is their centroid in blocks.
    /// </summary>
    void MeasureTerminalPositions(bool[] terminalAdjacent, bool[] portalAdjacent,
        bool[] unknownAdjacent, int[] portalGroup, int[] frontierGroup, bool pseudo,
        int spans, int step)
    {
        var sum = new Dictionary<int, (double X, double Y, double Z, long N)>();

        void Add(int id, int s)
        {
            int col = airColumn[s];
            double x = (double)(col % wCols) * step;
            double z = (double)(col / wCols) * step;
            double y = (airBottom[s] + airTop[s]) * 0.5;
            (double X, double Y, double Z, long N) had =
                sum.TryGetValue(id, out var was) ? was : default;
            sum[id] = (had.X + x, had.Y + y, had.Z + z, had.N + 1);
        }

        for (int s = 0; s < spans; s++)
        {
            if (!terminalAdjacent[s]) continue;
            if (portalAdjacent[s]) Add(portalGroup[s], s);
            if (pseudo && unknownAdjacent[s]) Add(spans + frontierGroup[s], s);
        }

        var centre = new Dictionary<int, (double X, double Y, double Z)>(sum.Count);
        foreach ((int id, (double x, double y, double z, long n)) in sum)
        {
            centre[id] = (x / n, y / n, z / n);
        }

        foreach (long key in pairGeodesic.Keys)
        {
            int a = (int)(key >> 32);
            int b = (int)(key & uint.MaxValue);
            if (!centre.TryGetValue(a, out var pa) || !centre.TryGetValue(b, out var pb))
            {
                pairStraight[key] = 0;                 // unknown position: fail open below
                continue;
            }
            double dx = pa.X - pb.X, dy = pa.Y - pb.Y, dz = pa.Z - pb.Z;
            pairStraight[key] = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }

    /// <summary>
    /// Re-decide every span the bridge rule kept as a route, under a tightened rule, and
    /// re-run the fluid pass because what water can be seen through has changed with it.
    /// Always starts from the baseline, so a sweep can call this repeatedly on one graph.
    /// </summary>
    public void ApplyRouteRule(CaveRouteRule rule)
    {
        RouteRule = rule;
        Array.Copy(baselineVerdict, airVerdict, airVerdict.Length);

        if (rule.Tightens)
        {
            int n = labels;
            for (int s = 0; s < airVerdict.Length; s++)
            {
                if (airVerdict[s] is not (CaveSpanVerdict.KeptRoute
                    or CaveSpanVerdict.KeptRouteUnknown)) continue;

                int have = labelCount[s];
                // Fewer than two mouths ever reached it: there is nothing to judge it
                // against, so it stays.
                if (have < 2) continue;

                // The funded rule is corridor-only. Its answer is the minimum over all
                // nearby mouth pairs, precomputed once when the graph was built; changing
                // W now costs one comparison instead of O(labels^2) hash lookups per row.
                if (rule.Corridor && !rule.Sightline)
                {
                    if (minimumSlack[s] > rule.SlackBlocks)
                    {
                        airVerdict[s] = CaveSpanVerdict.RemovableRouteTightened;
                    }
                    continue;
                }

                // ANY pair of nearby mouths that still deserves a passage through this span
                // keeps it. Checking every pair rather than only the nearest two is what
                // makes this an honest reading of "near a shortest path between SOME pair".
                bool keep = false;
                int at = s * n;
                for (int i = 0; i < have && !keep; i++)
                {
                    for (int j = i + 1; j < have && !keep; j++)
                    {
                        long key = PairKey(labelId[at + i], labelId[at + j]);
                        if (!pairGeodesic.TryGetValue(key, out int geodesic)) continue;
                        int through = (labelDistance[at + i] + labelDistance[at + j]) * routeStep;

                        if (rule.Corridor && through > geodesic + rule.SlackBlocks) continue;
                        if (rule.Sightline)
                        {
                            double straight = pairStraight.TryGetValue(key, out double had) ? had : 0;
                            // Two mouths on top of each other give no straight line to
                            // reason about, so the sightline rule declines to judge them.
                            if (straight > routeStep && geodesic > rule.Tortuosity * straight) continue;
                        }
                        keep = true;
                    }
                }
                if (!keep) airVerdict[s] = CaveSpanVerdict.RemovableRouteTightened;
            }
        }

        Array.Clear(waterVerdict, 0, waterVerdict.Length);
        ClassifyWater();
    }

    static void Count(List<long> pairs, Dictionary<int, int> into)
    {
        pairs.Sort();
        for (int i = 0; i < pairs.Count; i++)
        {
            if (i > 0 && pairs[i] == pairs[i - 1]) continue;
            int component = (int)(pairs[i] >> 32);
            into[component] = into.TryGetValue(component, out int had) ? had + 1 : 1;
        }
    }

    /// <summary>
    /// Daylight, from portals only.
    ///
    /// A hop into an adjacent column costs that column's width, exactly as the live
    /// sideways step does; falling within a span is free, exactly as the live downward step
    /// is. Climbing is NOT charged, so this lights more than the live per-voxel flood would
    /// and the removal it reports is a floor rather than an optimistic estimate.
    /// </summary>
    static bool[] SpreadFromPortals(bool[] portalAdjacent, int[] ea, int[] eb,
        Adjacency adjacency, int reach, int step)
    {
        int spans = portalAdjacent.Length;
        var light = new int[spans];
        var buckets = new List<int>[reach + 1];
        for (int v = 0; v <= reach; v++) buckets[v] = new List<int>();

        for (int s = 0; s < spans; s++)
        {
            if (!portalAdjacent[s]) continue;
            light[s] = reach;
            buckets[reach].Add(s);
        }

        for (int level = reach; level >= 1; level--)
        {
            List<int> bucket = buckets[level];
            int next = level - step;
            if (next <= 0) continue;
            for (int at = 0; at < bucket.Count; at++)
            {
                int s = bucket[at];
                if (light[s] != level) continue;
                for (int i = adjacency.Start[s]; i < adjacency.Start[s + 1]; i++)
                {
                    int e = adjacency.Edge[i];
                    int other = ea[e] == s ? eb[e] : ea[e];
                    if (light[other] >= next) continue;
                    light[other] = next;
                    buckets[next].Add(other);
                }
            }
        }

        var lit = new bool[spans];
        for (int s = 0; s < spans; s++) lit[s] = light[s] > 0;
        return lit;
    }

    /// <summary>
    /// Which spans lie on a branch no portal-to-portal route needs.
    ///
    /// The same shape of argument the live rule makes, on the span graph instead of the
    /// column graph: contract everything a bridge does not separate, then repeatedly peel
    /// leaves of the resulting forest that contain no portal. Whatever survives is the
    /// subtree joining the distinct portals, and only that is load-bearing. Dropping the
    /// live rule's whole-column contraction is the point - that contraction is what makes
    /// vertically stacked lobes of one system look like one cyclic blob (G109).
    /// </summary>
    static bool[] PeelBranches(bool[] terminal, int[] ea, int[] eb, Adjacency adjacency)
    {
        int spans = terminal.Length;
        bool[] bridge = FindBridges(spans, ea, eb, adjacency);

        var block = new int[spans];
        for (int i = 0; i < spans; i++) block[i] = i;
        for (int e = 0; e < ea.Length; e++)
        {
            if (!bridge[e]) Union(block, ea[e], eb[e]);
        }

        // Dense ids, because the forest below wants arrays and the roots are sparse.
        var dense = new Dictionary<int, int>();
        var blockId = new int[spans];
        for (int s = 0; s < spans; s++)
        {
            int root = Find(block, s);
            if (!dense.TryGetValue(root, out int id))
            {
                id = dense.Count;
                dense[root] = id;
            }
            blockId[s] = id;
        }

        // Only whether a block holds ANY terminal matters here: the peel removes a leaf
        // exactly when it holds none, and never compares two counts. Whether a component
        // has enough DISTINCT terminals to deserve a route at all is decided before the
        // peel is consulted.
        int blocks = dense.Count;
        var terminals = new bool[blocks];
        for (int s = 0; s < spans; s++)
        {
            if (terminal[s]) terminals[blockId[s]] = true;
        }

        var tree = new List<int>[blocks];
        for (int i = 0; i < blocks; i++) tree[i] = new List<int>();
        for (int e = 0; e < ea.Length; e++)
        {
            if (!bridge[e]) continue;
            int a = blockId[ea[e]], b = blockId[eb[e]];
            if (a == b) continue;
            tree[a].Add(b);
            tree[b].Add(a);
        }

        var degree = new int[blocks];
        for (int i = 0; i < blocks; i++) degree[i] = tree[i].Count;
        var removed = new bool[blocks];
        var queue = new Queue<int>();
        for (int i = 0; i < blocks; i++)
        {
            if (degree[i] <= 1 && !terminals[i]) queue.Enqueue(i);
        }
        while (queue.Count > 0)
        {
            int leaf = queue.Dequeue();
            if (removed[leaf] || terminals[leaf] || degree[leaf] > 1) continue;
            removed[leaf] = true;
            foreach (int other in tree[leaf])
            {
                if (removed[other]) continue;
                degree[other]--;
                if (degree[other] <= 1 && !terminals[other]) queue.Enqueue(other);
            }
        }

        var peeled = new bool[spans];
        for (int s = 0; s < spans; s++) peeled[s] = removed[blockId[s]];
        return peeled;
    }

    readonly record struct Adjacency(int[] Start, int[] Edge);

    static Adjacency BuildAdjacency(int nodes, int[] ea, int[] eb)
    {
        var start = new int[nodes + 1];
        foreach (int a in ea) start[a + 1]++;
        foreach (int b in eb) start[b + 1]++;
        for (int i = 0; i < nodes; i++) start[i + 1] += start[i];

        var cursor = (int[])start.Clone();
        var edge = new int[ea.Length * 2];
        for (int e = 0; e < ea.Length; e++)
        {
            edge[cursor[ea[e]]++] = e;
            edge[cursor[eb[e]]++] = e;
        }
        return new Adjacency(start, edge);
    }

    /// <summary>Iterative Tarjan. Cave systems are far too big for the call stack.</summary>
    static bool[] FindBridges(int nodes, int[] ea, int[] eb, Adjacency adjacency)
    {
        var discovered = new int[nodes];
        var low = new int[nodes];
        var parentEdge = new int[nodes];
        var nextEdge = new int[nodes];
        Array.Fill(parentEdge, -1);
        var bridges = new bool[ea.Length];
        var stack = new Stack<int>();
        int time = 0;

        for (int root = 0; root < nodes; root++)
        {
            if (discovered[root] != 0) continue;
            discovered[root] = low[root] = ++time;
            nextEdge[root] = adjacency.Start[root];
            stack.Push(root);

            while (stack.Count > 0)
            {
                int node = stack.Peek();
                if (nextEdge[node] < adjacency.Start[node + 1])
                {
                    int e = adjacency.Edge[nextEdge[node]++];
                    if (e == parentEdge[node]) continue;
                    int other = ea[e] == node ? eb[e] : ea[e];
                    if (discovered[other] == 0)
                    {
                        parentEdge[other] = e;
                        discovered[other] = low[other] = ++time;
                        nextEdge[other] = adjacency.Start[other];
                        stack.Push(other);
                    }
                    else low[node] = Math.Min(low[node], discovered[other]);
                    continue;
                }

                stack.Pop();
                int through = parentEdge[node];
                if (through < 0) continue;
                int above = ea[through] == node ? eb[through] : ea[through];
                if (low[node] > discovered[above]) bridges[through] = true;
                low[above] = Math.Min(low[above], low[node]);
            }
        }
        return bridges;
    }

    /// <summary>
    /// Fluid, on its own graph and by its own rule.
    ///
    /// Kept when it touches the open sky, when it touches anything unknown, and when it
    /// touches cavity air the air pass itself decided to keep - in that last case somebody
    /// can see through the air into the water. Only fluid boxed in by known solid and by
    /// cavity that is itself going away can be removed.
    /// </summary>
    void ClassifyWater()
    {
        int spans = waterBottom.Length;
        if (spans == 0) return;

        var parent = new int[spans];
        for (int i = 0; i < spans; i++) parent[i] = i;
        var exposed = new bool[spans];

        for (int col = 0; col < known.Length; col++)
        {
            for (int s = waterStart[col]; s < waterStart[col + 1]; s++)
            {
                int lo = waterBottom[s], hi = waterTop[s];

                // An ocean or lake surface IS the top of its column, so it faces the sky.
                if (hi >= columnTop[col]) exposed[s] = true;
                if (KeptAirBeside(col, hi)) exposed[s] = true;
                if (lo > 0 && KeptAirBeside(col, lo - 1)) exposed[s] = true;

                int x = col % wCols, z = col / wCols;
                Side(x > 0 ? col - 1 : -1, s, lo, hi);
                Side(x < wCols - 1 ? col + 1 : -1, s, lo, hi);
                Side(z > 0 ? col - wCols : -1, s, lo, hi);
                Side(z < hCols - 1 ? col + wCols : -1, s, lo, hi);
            }
        }

        void Side(int n, int s, int lo, int hi)
        {
            if (n < 0 || !known[n]) { exposed[s] = true; return; }
            if (hi > columnTop[n]) exposed[s] = true;              // open air beside it
            for (int t = waterStart[n]; t < waterStart[n + 1]; t++)
            {
                if (waterTop[t] <= lo || hi <= waterBottom[t]) continue;
                Union(parent, s, t);
            }
            for (int t = airStart[n]; t < airStart[n + 1]; t++)
            {
                if (airTop[t] <= lo || hi <= airBottom[t]) continue;
                if (IsKept(airVerdict[t])) exposed[s] = true;
            }
        }

        var componentExposed = new HashSet<int>();
        for (int s = 0; s < spans; s++)
        {
            parent[s] = Find(parent, s);
            if (exposed[s]) componentExposed.Add(parent[s]);
        }
        for (int s = 0; s < spans; s++)
        {
            waterVerdict[s] = componentExposed.Contains(parent[s])
                ? CaveSpanVerdict.WaterKept
                : CaveSpanVerdict.WaterRemovable;
        }
    }

    bool KeptAirBeside(int col, int y)
    {
        if (y >= columnTop[col]) return true;                 // straight up into the sky
        for (int s = airStart[col]; s < airStart[col + 1]; s++)
        {
            if (y >= airBottom[s] && y < airTop[s]) return IsKept(airVerdict[s]);
        }
        return false;
    }

    public static bool IsKept(CaveSpanVerdict v) =>
        v is CaveSpanVerdict.KeptUnknown or CaveSpanVerdict.KeptLit
            or CaveSpanVerdict.KeptRoute or CaveSpanVerdict.WaterKept
            or CaveSpanVerdict.KeptLitUnknown or CaveSpanVerdict.KeptRouteUnknown;

    static int Find(int[] parent, int n)
    {
        while (parent[n] != n)
        {
            parent[n] = parent[parent[n]];
            n = parent[n];
        }
        return n;
    }

    static void Union(int[] parent, int a, int b)
    {
        a = Find(parent, a);
        b = Find(parent, b);
        if (a != b) parent[b] = a;
    }
}
