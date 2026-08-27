namespace VintageHorizons;

/// <summary>
/// Fills cave systems that daylight never reaches, so their walls are never built.
///
/// WHY. The mesher emits a face for every run boundary nothing rests against, and a cave is
/// exactly a gap between two runs in a column. Buried caverns therefore get a floor, a
/// ceiling and walls, all of them sealed inside rock. Measured against the owner's cache,
/// that is about 30% of every vertex this mod produces - built, stored, uploaded and drawn
/// for an audience of nobody. The offline `cavefield` harness in the checks project is where
/// that figure comes from and is the place to re-measure it.
///
/// THE RULE.
///
/// Daylight, because connectivity alone is far too generous: this game's caves are long and
/// interconnected, so most of them touch the sky SOMEWHERE, and a rule that keeps anything
/// merely connected keeps nearly all of them. Skylight is a flood fill with a budget - full
/// strength falls straight down an open column, every other step costs distance - so asking
/// "is this lit" is the same walk as asking "is this connected", stopped early. It separates
/// a cave MOUTH, which daylight reaches, from the tunnel behind it, which does not. Nothing
/// needs to be stored for it: the game's own light values are not in the cache, but the rule
/// that produces them is a property of the shape, and the shape is what we have.
///
/// Underground connectivity is deliberately irrelevant. The owner wants cached terrain for
/// surface views only, so a dark tunnel is removable even when it eventually reaches another
/// entrance. Surface mouths, cliffs and overhangs are protected by the daylight reach.
///
/// WHY IT IS SAFE. Diffuse light spreads along every path, straight ones included, so
/// anything a straight line of sight could reach within the budget is lit and kept. Sight
/// travels only in straight lines; light reaches at least as far within that budget. Every
/// unknown is resolved toward keeping geometry - an absent neighbour counts as open air, an uncaptured column
/// counts as air, and the window's outer edge is treated as open sky - so the failure
/// direction is drawing something invisible, never removing something visible.
///
/// Straight surface sight survives beyond the diffuse-light budget. After the flood, a small
/// direction-independent line pass keeps dark air that shares an unobstructed lattice line with
/// exterior air above the captured terrain surface. A clear line crossing the complete local
/// window also fails open, because its real mouths may lie beyond the available snapshots. It does
/// not turn around corners and therefore does not restore the underground network rule the owner
/// rejected.
/// </summary>
public static class LodCaveCull
{
    /// <summary>
    /// How far the conservative surface envelope spreads, in world blocks. Reach 64 protects
    /// the owner's deep-overhang fixture while keeping the measured surface-only removal near
    /// the 60% target. The 3x3 window's margin is exactly 64 columns at L0; zero-budget light
    /// arriving at the centre boundary is rejected.
    /// </summary>
    public const int DefaultReach = 64;

    /// <summary>
    /// Fewest column steps of spread used at every level.
    ///
    /// Light is measured in blocks but travels between COLUMNS, and a column covers more
    /// ground at every coarser level - 64 blocks at L6. A fixed block budget therefore
    /// cannot even leave one coarse column. Coarse snapshots have already discarded the
    /// sub-column openings they cannot represent, so culling only their still-enclosed air
    /// cannot remove an opening from that mesh. We preserve a useful lateral safety envelope
    /// by scaling the working budget to about four represented columns. This is strictly more
    /// conservative than pretending the original 32 blocks still describe coarse space, and
    /// lets the same rule operate at every rendered LOD without changing or duplicating the
    /// canonical cache hierarchy.
    /// </summary>
    const int MinimumColumnReach = 4;
    // Cell values reserve 254/255 for kept/solid. The coarsest level needs 256 world
    // blocks for exactly four columns; 253 is the byte-safe conservative equivalent and
    // still reaches every immediately adjacent represented column.
    const int MaximumReach = 253;

    // Pooled per thread, for the reason the mesher pools its own buffers: this is millions
    // of cells, it runs on every mesh build, and a fresh pair per job would churn the large
    // object heap continuously during streaming.
    [ThreadStatic] static Workspace? workspace;

    sealed class Workspace
    {
        public byte[] Cells = Array.Empty<byte>();
        public bool[] Visited = Array.Empty<bool>();
        // Why this exists at all: see the telemetry block below. One byte per cell carries
        // both "the light here came from somewhere we do not actually know about" and
        // "this cell was retained by a component that had such a terminal". Two separate
        // arrays would double the per-thread window cost for no extra information.
        public byte[] Marks = Array.Empty<byte>();
        public bool[] UnknownColumn = Array.Empty<bool>();
        public int[] SurfaceTop = Array.Empty<int>();
        public ulong[] SightSolid = Array.Empty<ulong>();
        public ulong[] SightVisible = Array.Empty<ulong>();
        public ulong[] SightStateA = Array.Empty<ulong>();
        public ulong[] SightStateB = Array.Empty<ulong>();
        public ulong[] SightThrough = Array.Empty<ulong>();
        public int[] SightColumns = Array.Empty<int>();
        public List<int>[] Buckets = Array.Empty<List<int>>();
        public readonly List<int> Pocket = new();
        public readonly List<int> Frontier = new();
        public readonly List<ulong> Runs = new();

        public void Ensure(int cells, int buckets, int columns, int worldHeight,
            int horizontalSpan, bool prepareSurfaceSight)
        {
            if (Cells.Length < cells)
            {
                Cells = new byte[cells];
                Visited = new bool[cells];
                Marks = new byte[cells];
            }
            else
            {
                Array.Clear(Cells, 0, cells);
                Array.Clear(Visited, 0, cells);
                Array.Clear(Marks, 0, cells);
            }

            if (UnknownColumn.Length < columns) UnknownColumn = new bool[columns];
            else Array.Clear(UnknownColumn, 0, columns);
            if (prepareSurfaceSight)
            {
                if (SurfaceTop.Length < columns) SurfaceTop = new int[columns];
                else Array.Clear(SurfaceTop, 0, columns);
                int words = (worldHeight + 63) >> 6;
                int sightWords = columns * words;
                if (SightSolid.Length < sightWords) SightSolid = new ulong[sightWords];
                else Array.Clear(SightSolid, 0, sightWords);
                if (SightVisible.Length < sightWords) SightVisible = new ulong[sightWords];
                else Array.Clear(SightVisible, 0, sightWords);
                if (SightStateA.Length < words) SightStateA = new ulong[words];
                if (SightStateB.Length < words) SightStateB = new ulong[words];
                if (SightThrough.Length < horizontalSpan * words)
                    SightThrough = new ulong[horizontalSpan * words];
                if (SightColumns.Length < horizontalSpan) SightColumns = new int[horizontalSpan];
            }

            if (Buckets.Length < buckets + 1)
            {
                Buckets = new List<int>[buckets + 1];
                for (int i = 0; i <= buckets; i++) Buckets[i] = new List<int>();
            }
            else
            {
                for (int i = 0; i <= buckets; i++) Buckets[i].Clear();
            }
        }
    }

    readonly record struct PassageEdge(int A, int B);

    const byte Solid = 255;
    const byte Kept = 254;      // dark, but on a retained entrance-to-entrance backbone
    const byte Dark = 0;

    // Marks, one byte per cell, kept beside the light values rather than inside them:
    // 1..253 are light levels and 254/255 are taken, so there is no spare value.
    const byte MarkUnknownLight = 1;   // this lit cell's light came from unknown territory
    const byte MarkUnknownKeep = 2;    // this dark cell was kept by a component with such a terminal
    const byte MarkSurfaceSight = 4;   // this dark cell lies on a straight line from exterior air

    // ---------------------------------------------------------------------------------
    // RETENTION TELEMETRY
    //
    // A screenshot can show a cave that survived; it cannot say which safety rule saved
    // it. The rule fails open in six distinguishable ways and they imply completely
    // different fixes - more reach, a global graph, a portal identity, a fluid pass, or
    // nothing at all - so guessing between them is how a large piece of work gets funded
    // on the wrong diagnosis.
    //
    // Counted over the CENTRE section of each classification window only. The window is a
    // 3x3 and recentres for every mesh job, so counting the margin would count the same
    // terrain nine times.
    //
    // "Faces" is the count of unit cell-faces where a counted cell meets solid (for air)
    // or meets air (for water). The mesher merges those greedily before emitting, so
    // faces x 4 is an UPPER bound on vertices, not a prediction - it is here to weight
    // cells by roughly how much geometry they are responsible for.
    // ---------------------------------------------------------------------------------

    static long sectionsAccounted;
    static long uncapturedColumns;
    static long litCells, litFaces;                 // daylight reached it within the budget
    static long sightCells, sightFaces;             // straight surface sight reached it
    static long routeCells, routeFaces;             // dark, kept as backbone between known terminals
    static long unknownCells, unknownFaces;         // dark, kept by a terminal from unknown territory
    static long noFillCells, noFillFaces;           // dark and removable, but the column has no rock
    static long waterCells, waterFaces;             // fluid: never classified at all
    static long removedCells, removedFaces;         // dark, filled - the success case

    public static void ResetRetention()
    {
        Interlocked.Exchange(ref sectionsAccounted, 0);
        Interlocked.Exchange(ref uncapturedColumns, 0);
        Interlocked.Exchange(ref litCells, 0);
        Interlocked.Exchange(ref litFaces, 0);
        Interlocked.Exchange(ref sightCells, 0);
        Interlocked.Exchange(ref sightFaces, 0);
        Interlocked.Exchange(ref routeCells, 0);
        Interlocked.Exchange(ref routeFaces, 0);
        Interlocked.Exchange(ref unknownCells, 0);
        Interlocked.Exchange(ref unknownFaces, 0);
        Interlocked.Exchange(ref noFillCells, 0);
        Interlocked.Exchange(ref noFillFaces, 0);
        Interlocked.Exchange(ref waterCells, 0);
        Interlocked.Exchange(ref waterFaces, 0);
        Interlocked.Exchange(ref removedCells, 0);
        Interlocked.Exchange(ref removedFaces, 0);
    }

    /// <summary>One reason's tally: cells, and the cell-faces they are responsible for.</summary>
    public readonly record struct Reason(long Cells, long Faces);

    /// <summary>
    /// The whole breakdown, readable by a check rather than only by a person squinting at
    /// a log line. Telemetry that nothing can assert on is telemetry that can quietly
    /// start counting zero.
    /// </summary>
    public readonly record struct Retention(
        long Sections, long UncapturedColumns,
        Reason Removed, Reason Lit, Reason Sight, Reason Route,
        Reason Unknown, Reason NoFill, Reason Water)
    {
        public long SubterraneanCells =>
            Removed.Cells + Lit.Cells + Sight.Cells + Route.Cells
            + Unknown.Cells + NoFill.Cells + Water.Cells;
    }

    public static Retention CurrentRetention() => new(
        Interlocked.Read(ref sectionsAccounted),
        Interlocked.Read(ref uncapturedColumns),
        new Reason(Interlocked.Read(ref removedCells), Interlocked.Read(ref removedFaces)),
        new Reason(Interlocked.Read(ref litCells), Interlocked.Read(ref litFaces)),
        new Reason(Interlocked.Read(ref sightCells), Interlocked.Read(ref sightFaces)),
        new Reason(Interlocked.Read(ref routeCells), Interlocked.Read(ref routeFaces)),
        new Reason(Interlocked.Read(ref unknownCells), Interlocked.Read(ref unknownFaces)),
        new Reason(Interlocked.Read(ref noFillCells), Interlocked.Read(ref noFillFaces)),
        new Reason(Interlocked.Read(ref waterCells), Interlocked.Read(ref waterFaces)));

    /// <summary>
    /// One block of lines, one reason per line, for the client log. Multi-line on purpose:
    /// the owner cannot copy game chat, so the readable form has to be the logged one.
    /// </summary>
    public static string DescribeRetention()
    {
        Retention r = CurrentRetention();
        if (r.Sections == 0) return "cull reasons: no completed sections";
        long total = r.SubterraneanCells;

        string Line(string name, Reason reason) =>
            $"\n    {name,-28} {reason.Cells,14:n0} cells {Share(reason.Cells, total),6:0.0}%"
            + $" ~{reason.Faces * 4,14:n0} vertices"
            + $"  ({reason.Cells / (double)r.Sections:0.0} cells/section)";

        return $"cull reasons over {r.Sections:n0} completed sections"
            + $" ({total:n0} subterranean cells,"
            + $" {r.UncapturedColumns:n0} uncaptured columns)"
            + Line("REMOVED dark and filled", r.Removed)
            + Line("kept lit by daylight", r.Lit)
            + Line("kept by surface sight", r.Sight)
            + Line("kept dark route (legacy)", r.Route)
            + Line("kept dark unknown (legacy)", r.Unknown)
            + Line("kept no opaque fill", r.NoFill)
            + Line("water never classified", r.Water);
    }

    static double Share(long part, long whole) => whole > 0 ? part * 100.0 / whole : 0.0;

    /// <summary>
    /// One internally consistent view of the cave-cull result for a mesh build. The
    /// rebuilt section supplies the geometry, while the borrowed classification supplies
    /// the effective filled state immediately across its four edges. Both must come from
    /// the same pass: comparing a filled column with the original unfilled neighbour is
    /// exactly how the first implementation manufactured walls through every cavern.
    /// </summary>
    internal readonly struct Prepared
    {
        readonly byte[]? cells;
        readonly int span;
        readonly int margin;
        readonly int worldHeight;

        public SectionSnapshot Self { get; }
        public bool HasBoundaryCoverage => cells != null;

        public Prepared(SectionSnapshot self)
        {
            Self = self;
            cells = null;
            span = margin = worldHeight = 0;
        }

        public Prepared(SectionSnapshot self, byte[] cells,
            int span, int margin, int worldHeight)
        {
            Self = self;
            this.cells = cells;
            this.span = span;
            this.margin = margin;
            this.worldHeight = worldHeight;
        }

        /// <summary>
        /// Whether air at a column relative to the section was classified for filling.
        /// The mesher asks only for x=-1/x=Grid or z=-1/z=Grid: the immediately adjacent
        /// neighbour columns, which lie safely inside the same 3x3 classification window.
        /// </summary>
        public bool Fills(int cx, int cz, int y)
        {
            if (cells == null || y < 0 || y >= worldHeight) return false;
            int wx = cx + margin;
            int wz = cz + margin;
            if (wx < 0 || wx >= span || wz < 0 || wz >= span) return false;
            return cells[Index(wx, wz, y, span, worldHeight)] == Dark;
        }
    }

    /// <summary>
    /// The section with unreachable cavities filled, or the same instance when there was
    /// nothing to fill. Neighbours are read for context only and are never modified.
    /// </summary>
    /// <param name="reach">Daylight reach in world blocks; zero or less disables the pass.</param>
    public static SectionSnapshot FillUnseen(
        SectionSnapshot self, SectionSnapshot?[] neighbors, int level, int reach)
        => Prepare(self, neighbors, level, reach, preserveSurfaceSight: true).Self;

    /// <summary>Measurement-only switch used by the offline cache harness for a paired baseline.</summary>
    internal static SectionSnapshot FillUnseen(
        SectionSnapshot self, SectionSnapshot?[] neighbors, int level, int reach,
        bool preserveSurfaceSight)
        => Prepare(self, neighbors, level, reach, preserveSurfaceSight).Self;

    /// <summary>
    /// Prepare the filled section and the matching edge classification for one immediate
    /// mesh build. The classification borrows the thread-local workspace and must not be
    /// retained after another cave-cull call on this thread.
    /// </summary>
    internal static Prepared Prepare(
        SectionSnapshot self, SectionSnapshot?[] neighbors, int level, int reach)
        => Prepare(self, neighbors, level, reach, preserveSurfaceSight: true);

    static Prepared Prepare(
        SectionSnapshot self, SectionSnapshot?[] neighbors, int level, int reach,
        bool preserveSurfaceSight)
    {
        if (reach <= 0) return new Prepared(self);

        int step = LodWorld.ColumnStepBlocks(level);
        int effectiveReach = Math.Min(MaximumReach,
            Math.Max(reach, MinimumColumnReach * step));

        int grid = LodSection.GridSize;

        // The whole neighbour, not just as far as the light can travel.
        //
        // The window's outer wall is treated as open sky, because what lies beyond it is
        // unknown and keeping geometry is the safe answer. That makes the wall a light
        // SOURCE, so it has to stand further off than the light can reach or it floods the
        // section it was meant to protect and rescues caves that are genuinely dark. A
        // margin equal to the reach puts it exactly in range; a margin of a full section
        // puts it out of range, and costs nothing extra because the neighbour snapshot is
        // already in hand. Measured against the owner's cache this is the difference
        // between removing 15% of the geometry and removing about 30%.
        int margin = grid;
        int span = grid + margin * 2;

        int worldHeight = HighestBlock(self, neighbors) + 2;
        if (worldHeight <= 2) return new Prepared(self);

        Workspace work = workspace ??= new Workspace();
        work.Ensure(span * span * worldHeight, effectiveReach, span * span,
            worldHeight, span, preserveSurfaceSight);

        BuildOccupancy(work, self, neighbors, span, margin, worldHeight,
            preserveSurfaceSight);
        SpreadDaylight(work, span, worldHeight, effectiveReach, step);
        if (preserveSurfaceSight) KeepSurfaceSightlines(work, span, margin, worldHeight);

        SectionSnapshot filled = Rebuild(work, self, span, margin, worldHeight);
        Account(work, self, span, margin, worldHeight);
        return new Prepared(filled, work.Cells, span, margin, worldHeight);
    }

    static int Index(int x, int z, int y, int span, int worldHeight) =>
        (z * span + x) * worldHeight + y;

    static int HighestBlock(SectionSnapshot self, SectionSnapshot?[] neighbors)
    {
        int top = HighestBlock(self);
        foreach (SectionSnapshot? nb in neighbors)
        {
            if (nb != null) top = Math.Max(top, HighestBlock(nb));
        }
        return top;
    }

    static int HighestBlock(SectionSnapshot section)
    {
        int top = 0;
        int columns = LodSection.GridSize * LodSection.GridSize;
        for (int col = 0; col < columns; col++)
        {
            if (!section.Captured[col]) continue;
            Span<ulong> runs = section.ColumnRuns(col);
            if (runs.Length > 0) top = Math.Max(top, LodSection.RunYTop(runs[0]));
        }
        return top;
    }

    /// <summary>
    /// Which snapshot owns a window column, and its index within it. Only the four edge
    /// neighbours are supplied to a mesh job, so a diagonal corner has no source and is left
    /// as air - one more place the unknown is resolved toward keeping geometry.
    /// </summary>
    static bool Locate(SectionSnapshot self, SectionSnapshot?[] neighbors,
        int wx, int wz, int margin, out SectionSnapshot section, out int column)
    {
        int grid = LodSection.GridSize;
        int lx = wx - margin;
        int lz = wz - margin;

        SectionSnapshot? found;
        if (lx >= 0 && lx < grid && lz >= 0 && lz < grid) found = self;
        else if (lz >= 0 && lz < grid) found = lx < 0 ? neighbors[0] : neighbors[1];
        else if (lx >= 0 && lx < grid) found = lz < 0 ? neighbors[2] : neighbors[3];
        else if (neighbors.Length > 7)
        {
            // Corner. Supplied for light only; nothing here answers a side-covered question.
            found = lz < 0
                ? (lx < 0 ? neighbors[4] : neighbors[5])
                : (lx < 0 ? neighbors[6] : neighbors[7]);
        }
        else found = null;                       // nothing supplies it: treated as open air

        if (found == null) { section = self; column = 0; return false; }

        column = LodSection.ColumnIndex((lx + grid) % grid, (lz + grid) % grid);
        section = found;
        return true;
    }

    static void BuildOccupancy(Workspace work, SectionSnapshot self, SectionSnapshot?[] neighbors,
        int span, int margin, int worldHeight, bool prepareSurfaceSight)
    {
        byte[] cells = work.Cells;
        bool[] unknownColumn = work.UnknownColumn;
        int[] surfaceTop = work.SurfaceTop;
        ulong[] solidBits = work.SightSolid;
        int words = (worldHeight + 63) >> 6;

        for (int wz = 0; wz < span; wz++)
        {
            for (int wx = 0; wx < span; wx++)
            {
                if (!Locate(self, neighbors, wx, wz, margin, out SectionSnapshot section, out int col))
                {
                    // Absent: all air, and honestly labelled as air we invented. Light that
                    // leaves such a column is the reason a component can look like a
                    // through-tunnel when nobody has ever seen the far side of it.
                    unknownColumn[wz * span + wx] = true;
                    continue;
                }
                if (!section.Captured[col])
                {
                    unknownColumn[wz * span + wx] = true;        // uncaptured: all air
                    continue;
                }

                Span<ulong> columnRuns = section.ColumnRuns(col);
                if (prepareSurfaceSight)
                {
                    surfaceTop[wz * span + wx] = columnRuns.Length == 0
                        ? 0 : Math.Min(worldHeight, LodSection.RunYTop(columnRuns[0]));
                }

                foreach (ulong run in columnRuns)
                {
                    int yTop = Math.Min(worldHeight, LodSection.RunYTop(run));
                    int yBottom = Math.Max(0, LodSection.RunYBottom(run));
                    for (int y = yBottom; y < yTop; y++)
                    {
                        cells[Index(wx, wz, y, span, worldHeight)] = Solid;
                        if (prepareSurfaceSight)
                            solidBits[(wz * span + wx) * words + (y >> 6)] |= 1UL << (y & 63);
                    }
                }
            }
        }
    }

    // Horizontal projections for the 26-neighbour direction set. Each is combined with vertical
    // deltas -1, 0 and +1 below. The remaining pure-vertical pair is already covered exactly by
    // the daylight flood's free downward propagation.
    static readonly (int X, int Z)[] SurfaceSightDirections =
    {
        (1, 0), (0, 1), (1, 1), (1, -1),
    };
    const int SurfaceSightClearance = 4;

    /// <summary>
    /// Keep dark cells that lie on an unobstructed straight lattice line containing real exterior
    /// air. Exterior is derived independently for every captured column from its highest stored
    /// run; it has no sea-level assumption. Unknown columns have top zero and therefore fail open.
    /// A second, deliberately conservative case keeps a line that crosses the complete local
    /// window without obstruction. The middle section of a long mountain tunnel cannot see either
    /// real mouth in its 3x3 input, so requiring an in-window exterior seed would plug it.
    ///
    /// Vertical occupancy is packed into 64-bit words. One operation advances 64 independent rays
    /// to the next horizontal column, shifting the word for a rising or falling sightline. Only
    /// lines intersecting the centre section or its one-cell mesh boundary are scanned. Direction
    /// state is cleared for every line, and accumulated results are never used as seeds, so two
    /// directions cannot combine into an unbounded turn around a corner. A four-block, 26-way air
    /// halo around a proven ray preserves the nearby floor, ceiling, sidewalls and diagonal tunnel
    /// contour, but is stopped by solid terrain and by the fixed distance rather than following the
    /// cave network.
    /// </summary>
    static void KeepSurfaceSightlines(Workspace work, int span, int margin, int worldHeight)
    {
        byte[] cells = work.Cells;
        byte[] marks = work.Marks;
        int[] surfaceTop = work.SurfaceTop;
        ulong[] solid = work.SightSolid;
        ulong[] visible = work.SightVisible;
        ulong[] stateA = work.SightStateA;
        ulong[] stateB = work.SightStateB;
        ulong[] through = work.SightThrough;
        int[] columns = work.SightColumns;
        int grid = LodSection.GridSize;
        int targetMin = margin - 1;
        int targetMax = margin + grid;
        int words = (worldHeight + 63) >> 6;

        foreach ((int dx, int dz) in SurfaceSightDirections)
        {
            int xEntry = dx > 0 ? 0 : span - 1;
            int zEntry = dz > 0 ? 0 : span - 1;

            if (dx != 0)
            {
                for (int z = 0; z < span; z++)
                    ScanLine(xEntry, z, dx, dz);
            }
            if (dz != 0)
            {
                for (int x = 0; x < span; x++)
                {
                    if (dx != 0 && x == xEntry) continue;
                    ScanLine(x, zEntry, dx, dz);
                }
            }
        }

        List<int> halo = work.Pocket;
        halo.Clear();
        for (int z = targetMin; z <= targetMax; z++)
        {
            for (int x = targetMin; x <= targetMax; x++)
            {
                int column = z * span + x;
                int bitBase = column * words;
                for (int word = 0; word < words; word++)
                {
                    ulong bits = visible[bitBase + word];
                    while (bits != 0)
                    {
                        int bit = System.Numerics.BitOperations.TrailingZeroCount(bits);
                        int y = (word << 6) + bit;
                        int i = Index(x, z, y, span, worldHeight);
                        if (cells[i] == Dark)
                        {
                            cells[i] = Kept;
                            marks[i] |= MarkSurfaceSight;
                            halo.Add(i);
                        }
                        bits &= bits - 1;
                    }
                }
            }
        }

        int layerStart = 0;
        int layerEnd = halo.Count;
        for (int distance = 1; distance <= SurfaceSightClearance; distance++)
        {
            for (int at = layerStart; at < layerEnd; at++)
            {
                int i = halo[at];
                int y = i % worldHeight;
                int column = i / worldHeight;
                int x = column % span;
                int z = column / span;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0 && dz == 0) continue;
                            KeepNearby(x + dx, z + dz, y + dy);
                        }
                    }
                }
            }
            layerStart = layerEnd;
            layerEnd = halo.Count;
            if (layerStart == layerEnd) break;
        }

        void KeepNearby(int x, int z, int y)
        {
            if (x < targetMin || x > targetMax || z < targetMin || z > targetMax
                || y < 0 || y >= worldHeight) return;
            int nearby = Index(x, z, y, span, worldHeight);
            if (cells[nearby] == Solid || (marks[nearby] & MarkSurfaceSight) != 0) return;
            marks[nearby] |= MarkSurfaceSight;
            if (cells[nearby] == Dark) cells[nearby] = Kept;
            halo.Add(nearby);
        }

        void ScanLine(int sx, int sz, int dx, int dz)
        {
            int count = 0;
            bool touchesTarget = false;
            for (int x = sx, z = sz;
                 x >= 0 && x < span && z >= 0 && z < span;
                 x += dx, z += dz)
            {
                columns[count++] = z * span + x;
                touchesTarget |= x >= targetMin && x <= targetMax
                    && z >= targetMin && z <= targetMax;
            }
            if (!touchesTarget) return;

            for (int dy = -1; dy <= 1; dy++)
            {
                Sweep(count, dy, reverse: false);
                Sweep(count, -dy, reverse: true);
                SweepThroughWindow(count, dy);
            }
        }

        void Sweep(int count, int dy, bool reverse)
        {
            Array.Clear(stateA, 0, words);
            ulong[] prior = stateA;
            ulong[] next = stateB;

            for (int at = 0; at < count; at++)
            {
                int column = columns[reverse ? count - 1 - at : at];
                int bitBase = column * words;
                int top = surfaceTop[column];

                for (int word = 0; word < words; word++)
                {
                    int bottom = word << 6;
                    int validBits = Math.Min(64, worldHeight - bottom);
                    ulong valid = validBits == 64
                        ? ulong.MaxValue : (1UL << validBits) - 1;
                    int localTop = top - bottom;
                    ulong exterior = localTop <= 0
                        ? valid
                        : localTop >= validBits ? 0 : (ulong.MaxValue << localTop) & valid;

                    ulong shifted = dy switch
                    {
                        > 0 => (prior[word] << 1)
                            | (word > 0 ? prior[word - 1] >> 63 : 0),
                        < 0 => (prior[word] >> 1)
                            | (word + 1 < words ? prior[word + 1] << 63 : 0),
                        _ => prior[word],
                    };
                    next[word] = (shifted | exterior) & ~solid[bitBase + word] & valid;
                }

                int x = column % span;
                int z = column / span;
                if (x >= targetMin && x <= targetMax && z >= targetMin && z <= targetMax)
                {
                    for (int word = 0; word < words; word++)
                        visible[bitBase + word] |= next[word];
                }

                (prior, next) = (next, prior);
            }
        }

        void SweepThroughWindow(int count, int dy)
        {
            // Carry every air cell on the line's entry edge forward, remembering which heights
            // survive at every column. No interior column may start a ray in this sweep.
            Array.Clear(stateA, 0, words);
            ulong[] prior = stateA;
            ulong[] next = stateB;
            for (int at = 0; at < count; at++)
            {
                int bitBase = columns[at] * words;
                int savedBase = at * words;
                for (int word = 0; word < words; word++)
                {
                    int validBits = Math.Min(64, worldHeight - (word << 6));
                    ulong valid = validBits == 64
                        ? ulong.MaxValue : (1UL << validBits) - 1;
                    ulong shifted = dy switch
                    {
                        > 0 => (prior[word] << 1)
                            | (word > 0 ? prior[word - 1] >> 63 : 0),
                        < 0 => (prior[word] >> 1)
                            | (word + 1 < words ? prior[word + 1] << 63 : 0),
                        _ => prior[word],
                    };
                    ulong air = ~solid[bitBase + word] & valid;
                    next[word] = at == 0 ? air : shifted & air;
                    through[savedBase + word] = next[word];
                }
                (prior, next) = (next, prior);
            }

            // Carry the opposite edge backward. The two results must meet at the same height,
            // proving one straight line crosses the entire available window rather than merely
            // touching one unknown boundary.
            Array.Clear(stateA, 0, words);
            prior = stateA;
            next = stateB;
            for (int reverseAt = 0; reverseAt < count; reverseAt++)
            {
                int at = count - 1 - reverseAt;
                int column = columns[at];
                int bitBase = column * words;
                int savedBase = at * words;
                for (int word = 0; word < words; word++)
                {
                    int validBits = Math.Min(64, worldHeight - (word << 6));
                    ulong valid = validBits == 64
                        ? ulong.MaxValue : (1UL << validBits) - 1;
                    ulong shifted = dy switch
                    {
                        > 0 => (prior[word] >> 1)
                            | (word + 1 < words ? prior[word + 1] << 63 : 0),
                        < 0 => (prior[word] << 1)
                            | (word > 0 ? prior[word - 1] >> 63 : 0),
                        _ => prior[word],
                    };
                    ulong air = ~solid[bitBase + word] & valid;
                    next[word] = reverseAt == 0 ? air : shifted & air;
                }

                int x = column % span;
                int z = column / span;
                if (x >= targetMin && x <= targetMax && z >= targetMin && z <= targetMax)
                {
                    for (int word = 0; word < words; word++)
                        visible[bitBase + word] |= through[savedBase + word] & next[word];
                }

                (prior, next) = (next, prior);
            }
        }
    }

    /// <summary>
    /// Daylight from the sky and from the window's outer wall. Full strength falls straight
    /// down an open column for free, the way sunlight does; a sideways step costs the width
    /// of a column and any other step costs one block.
    ///
    /// Buckets are drained brightest first, so a cell settles at its true value the first
    /// time it is written and nothing is ever revisited: the free downward step lands back
    /// in the bucket being drained, and every dimmer step lands in a later one.
    /// </summary>
    static void SpreadDaylight(Workspace work, int span, int worldHeight, int reach, int step)
    {
        byte[] cells = work.Cells;
        byte[] marks = work.Marks;
        bool[] unknownColumn = work.UnknownColumn;
        List<int>[] buckets = work.Buckets;

        void Seed(int x, int z, int y, bool unknown)
        {
            int i = Index(x, z, y, span, worldHeight);
            if (cells[i] != Dark) return;
            cells[i] = (byte)reach;
            if (unknown || unknownColumn[z * span + x]) marks[i] = MarkUnknownLight;
            buckets[reach].Add(i);
        }

        // The four walls first, then the sky. Both seed the same cells to the same value,
        // so the classification is unchanged; the order decides only which provenance a
        // shared corner cell records, and "unknown" is the one worth knowing about.
        for (int y = 0; y < worldHeight; y++)
        {
            for (int t = 0; t < span; t++)
            {
                Seed(0, t, y, true);
                Seed(span - 1, t, y, true);
                Seed(t, 0, y, true);
                Seed(t, span - 1, y, true);
            }
        }
        for (int z = 0; z < span; z++)
        {
            for (int x = 0; x < span; x++) Seed(x, z, worldHeight - 1, false);
        }

        void Spread(int x, int z, int y, int value, bool unknown)
        {
            if (value <= 0) return;
            if (x < 0 || x >= span || z < 0 || z >= span || y < 0 || y >= worldHeight) return;
            int i = Index(x, z, y, span, worldHeight);
            if (cells[i] == Solid || cells[i] >= value) return;
            cells[i] = (byte)value;
            marks[i] = unknown || unknownColumn[z * span + x] ? MarkUnknownLight : (byte)0;
            buckets[value].Add(i);
        }

        for (int level = reach; level >= 1; level--)
        {
            List<int> bucket = buckets[level];

            // Indexed rather than foreach: a free downward step appends to this same bucket
            // while it is being drained, and those cells have to be drained too.
            for (int at = 0; at < bucket.Count; at++)
            {
                int i = bucket[at];
                if (cells[i] != level) continue;              // a brighter path got here first

                int y = i % worldHeight;
                int column = i / worldHeight;
                int x = column % span;
                int z = column / span;
                // The brightest path to a cell settles it, and its provenance travels on
                // with it. A dimmer genuine path arriving later does not relabel it, so a
                // cell reachable both ways stays labelled unknown - the conservative
                // direction for a measurement whose whole purpose is to size the unknown.
                bool unknown = (marks[i] & MarkUnknownLight) != 0;

                Spread(x, z, y - 1, level == reach ? reach : level - 1, unknown);
                Spread(x, z, y + 1, level - 1, unknown);
                Spread(x - 1, z, y, level - step, unknown);
                Spread(x + 1, z, y, level - step, unknown);
                Spread(x, z - 1, y, level - step, unknown);
                Spread(x, z + 1, y, level - step, unknown);
            }
        }
    }

    /// <summary>
    /// Promote every dark pocket with two or more separate ways in back to kept. Patches are
    /// grouped among the frontier cells themselves rather than through open air, which is the
    /// whole point: both mouths of a through-tunnel reach the same sky, so grouping through
    /// the sky would call them one way in and plug the tunnel anyway.
    /// </summary>
    static void KeepPassageBackbones(Workspace work, int span, int worldHeight)
    {
        byte[] cells = work.Cells;
        bool[] visited = work.Visited;
        List<int> pocket = work.Pocket;
        List<int> frontier = work.Frontier;

        for (int start = 0; start < cells.Length; start++)
        {
            if (cells[start] != Dark || visited[start]) continue;

            pocket.Clear();
            frontier.Clear();
            visited[start] = true;
            pocket.Add(start);

            for (int at = 0; at < pocket.Count; at++)
            {
                int i = pocket[at];
                int y = i % worldHeight;
                int column = i / worldHeight;
                int x = column % span;
                int z = column / span;

                Visit(x, z, y - 1);
                Visit(x, z, y + 1);
                Visit(x - 1, z, y);
                Visit(x + 1, z, y);
                Visit(x, z - 1, y);
                Visit(x, z + 1, y);
            }

            Dictionary<int, int> patchByCell = LabelPatches(
                frontier, span, worldHeight, out int patchCount);
            if (patchCount < 2) continue;

            // Telemetry only, and it changes no decision: whether any of the terminals
            // that saved this component is a patch of light that entered from the window
            // wall or an uncaptured column, rather than from terrain we have actually
            // seen. That is the difference between "a tunnel goes through here" and "we
            // have no idea what is on the other side", and the two want different fixes.
            bool unknownTerminal = false;
            foreach (int cell in patchByCell.Keys)
            {
                if ((work.Marks[cell] & MarkUnknownLight) == 0) continue;
                unknownTerminal = true;
                break;
            }

            KeepTerminalBackbone(cells, visited, pocket, patchByCell,
                patchCount, span, worldHeight);

            if (unknownTerminal)
            {
                foreach (int i in pocket)
                {
                    if (cells[i] == Kept) work.Marks[i] = MarkUnknownKeep;
                }
            }

            void Visit(int x, int z, int y)
            {
                if (x < 0 || x >= span || z < 0 || z >= span || y < 0 || y >= worldHeight) return;
                int n = Index(x, z, y, span, worldHeight);
                byte value = cells[n];
                if (value == Solid) return;
                if (value != Dark && value != Kept) { frontier.Add(n); return; }
                if (visited[n]) return;
                visited[n] = true;
                pocket.Add(n);
            }
        }
    }

    /// <summary>
    /// Label connected groups among the frontier cells, touching on any of the 26 directions
    /// so that a merely ragged opening is not mistaken for two of them. The labels become
    /// distinct terminals in the passage graph below.
    /// </summary>
    static Dictionary<int, int> LabelPatches(List<int> frontier,
        int span, int worldHeight, out int patchCount)
    {
        var members = new HashSet<int>(frontier);
        var labels = new Dictionary<int, int>(members.Count);
        var stack = new Stack<int>();
        patchCount = 0;

        foreach (int start in frontier)
        {
            if (labels.ContainsKey(start)) continue;
            int patch = patchCount++;
            labels[start] = patch;

            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int y = i % worldHeight;
                int column = i / worldHeight;
                int x = column % span;
                int z = column / span;

                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0 && dz == 0) continue;
                            int nx = x + dx, nz = z + dz, ny = y + dy;
                            if (nx < 0 || nx >= span || nz < 0 || nz >= span
                                || ny < 0 || ny >= worldHeight) continue;
                            int n = Index(nx, nz, ny, span, worldHeight);
                            if (!members.Contains(n) || labels.ContainsKey(n)) continue;
                            labels[n] = patch;
                            stack.Push(n);
                        }
                    }
                }
            }
        }
        return labels;
    }

    /// <summary>
    /// Keep only the conservative topological backbone joining distinct light-frontier
    /// patches. Dark cells are contracted by horizontal column before analysis. That
    /// contraction can invent connections and therefore keep too much, but it cannot erase
    /// a real entrance-to-entrance route. A bridge whose far subtree has no entrance is the
    /// only thing removed; rooms, loops, wide ambiguous junctions and every through-route
    /// remain fail-open.
    /// </summary>
    static void KeepTerminalBackbone(byte[] cells, bool[] visited, List<int> pocket,
        Dictionary<int, int> patchByCell, int patchCount, int span, int worldHeight)
    {
        var nodeByColumn = new Dictionary<int, int>();
        foreach (int i in pocket)
        {
            int column = i / worldHeight;
            if (!nodeByColumn.ContainsKey(column)) nodeByColumn[column] = nodeByColumn.Count;
        }

        int darkNodes = nodeByColumn.Count;
        int nodeCount = darkNodes + patchCount;
        var edgeKeys = new HashSet<ulong>();

        void AddEdge(int a, int b)
        {
            if (a == b) return;
            uint lo = (uint)Math.Min(a, b);
            uint hi = (uint)Math.Max(a, b);
            edgeKeys.Add(((ulong)hi << 32) | lo);
        }

        foreach (int i in pocket)
        {
            int y = i % worldHeight;
            int column = i / worldHeight;
            int x = column % span;
            int z = column / span;
            int node = nodeByColumn[column];

            // One edge per neighbouring column. Contracting the vertical cross-section is
            // deliberate: a thick corridor remains one topological route rather than a
            // bundle of parallel voxel edges that would make every dead end look cyclic.
            ConnectDark(x + 1, z, y);
            ConnectDark(x, z + 1, y);

            ConnectFrontier(x, z, y - 1);
            ConnectFrontier(x, z, y + 1);
            ConnectFrontier(x - 1, z, y);
            ConnectFrontier(x + 1, z, y);
            ConnectFrontier(x, z - 1, y);
            ConnectFrontier(x, z + 1, y);

            void ConnectDark(int nx, int nz, int ny)
            {
                if (nx < 0 || nx >= span || nz < 0 || nz >= span
                    || ny < 0 || ny >= worldHeight) return;
                int n = Index(nx, nz, ny, span, worldHeight);
                if (cells[n] != Dark || !visited[n]) return;
                int neighborColumn = n / worldHeight;
                if (nodeByColumn.TryGetValue(neighborColumn, out int other)) AddEdge(node, other);
            }

            void ConnectFrontier(int nx, int nz, int ny)
            {
                if (nx < 0 || nx >= span || nz < 0 || nz >= span
                    || ny < 0 || ny >= worldHeight) return;
                int n = Index(nx, nz, ny, span, worldHeight);
                if (patchByCell.TryGetValue(n, out int patch)) AddEdge(node, darkNodes + patch);
            }
        }

        var edges = new PassageEdge[edgeKeys.Count];
        var adjacency = new List<int>[nodeCount];
        for (int i = 0; i < nodeCount; i++) adjacency[i] = new List<int>();

        int edgeAt = 0;
        foreach (ulong key in edgeKeys)
        {
            int a = (int)(key & uint.MaxValue);
            int b = (int)(key >> 32);
            edges[edgeAt] = new PassageEdge(a, b);
            adjacency[a].Add(edgeAt);
            adjacency[b].Add(edgeAt);
            edgeAt++;
        }

        bool[] bridges = FindBridges(edges, adjacency);
        var parent = new int[nodeCount];
        for (int i = 0; i < nodeCount; i++) parent[i] = i;

        int Root(int n)
        {
            while (parent[n] != n)
            {
                parent[n] = parent[parent[n]];
                n = parent[n];
            }
            return n;
        }

        void Union(int a, int b)
        {
            a = Root(a);
            b = Root(b);
            if (a != b) parent[b] = a;
        }

        for (int e = 0; e < edges.Length; e++)
        {
            if (!bridges[e]) Union(edges[e].A, edges[e].B);
        }

        var componentByRoot = new Dictionary<int, int>();
        var componentOf = new int[nodeCount];
        for (int n = 0; n < nodeCount; n++)
        {
            int root = Root(n);
            if (!componentByRoot.TryGetValue(root, out int component))
            {
                component = componentByRoot.Count;
                componentByRoot[root] = component;
            }
            componentOf[n] = component;
        }

        int componentCount = componentByRoot.Count;
        var tree = new List<int>[componentCount];
        var terminals = new int[componentCount];
        for (int i = 0; i < componentCount; i++) tree[i] = new List<int>();
        for (int patch = 0; patch < patchCount; patch++)
            terminals[componentOf[darkNodes + patch]]++;

        for (int e = 0; e < edges.Length; e++)
        {
            if (!bridges[e]) continue;
            int a = componentOf[edges[e].A];
            int b = componentOf[edges[e].B];
            if (a == b) continue;
            tree[a].Add(b);
            tree[b].Add(a);
        }

        // Repeatedly peel terminal-free leaves. What remains is exactly the bridge-tree
        // subtree needed to connect all distinct entrance patches.
        var degree = tree.Select(v => v.Count).ToArray();
        var removed = new bool[componentCount];
        var queue = new Queue<int>();
        for (int i = 0; i < componentCount; i++)
        {
            if (degree[i] <= 1 && terminals[i] == 0) queue.Enqueue(i);
        }
        while (queue.Count > 0)
        {
            int leaf = queue.Dequeue();
            if (removed[leaf] || terminals[leaf] != 0 || degree[leaf] > 1) continue;
            removed[leaf] = true;
            foreach (int other in tree[leaf])
            {
                if (removed[other]) continue;
                degree[other]--;
                if (degree[other] <= 1 && terminals[other] == 0) queue.Enqueue(other);
            }
        }

        foreach (int i in pocket)
        {
            int node = nodeByColumn[i / worldHeight];
            if (!removed[componentOf[node]]) cells[i] = Kept;
        }
    }

    static bool[] FindBridges(PassageEdge[] edges, List<int>[] adjacency)
    {
        int nodes = adjacency.Length;
        var discovered = new int[nodes];
        var low = new int[nodes];
        var parentEdge = new int[nodes];
        var nextEdge = new int[nodes];
        Array.Fill(parentEdge, -1);
        var bridges = new bool[edges.Length];
        var stack = new Stack<int>();
        int time = 0;

        for (int root = 0; root < nodes; root++)
        {
            if (discovered[root] != 0) continue;
            discovered[root] = low[root] = ++time;
            stack.Push(root);

            while (stack.Count > 0)
            {
                int node = stack.Peek();
                if (nextEdge[node] < adjacency[node].Count)
                {
                    int edgeId = adjacency[node][nextEdge[node]++];
                    if (edgeId == parentEdge[node]) continue;
                    PassageEdge edge = edges[edgeId];
                    int other = edge.A == node ? edge.B : edge.A;
                    if (discovered[other] == 0)
                    {
                        parentEdge[other] = edgeId;
                        discovered[other] = low[other] = ++time;
                        stack.Push(other);
                    }
                    else
                    {
                        low[node] = Math.Min(low[node], discovered[other]);
                    }
                    continue;
                }

                stack.Pop();
                int through = parentEdge[node];
                if (through < 0) continue;
                PassageEdge parent = edges[through];
                int above = parent.A == node ? parent.B : parent.A;
                if (low[node] > discovered[above]) bridges[through] = true;
                low[above] = Math.Min(low[above], low[node]);
            }
        }

        return bridges;
    }

    /// <summary>
    /// The section's runs with unreachable cavities turned to rock. A column with nothing to
    /// fill keeps its stored runs unchanged, so an untouched column cannot drift through a
    /// rebuild; a section with nothing to fill anywhere returns the original snapshot and
    /// allocates nothing at all.
    /// </summary>
    static SectionSnapshot Rebuild(Workspace work, SectionSnapshot self,
        int span, int margin, int worldHeight)
    {
        byte[] cells = work.Cells;
        int grid = LodSection.GridSize;
        List<ulong> runs = work.Runs;
        runs.Clear();

        var starts = new int[grid * grid + 1];
        bool changed = false;

        for (int cz = 0; cz < grid; cz++)
        {
            for (int cx = 0; cx < grid; cx++)
            {
                int col = LodSection.ColumnIndex(cx, cz);
                starts[col] = runs.Count;
                if (!self.Captured[col]) continue;

                Span<ulong> source = self.ColumnRuns(col);
                if (source.Length == 0) continue;

                int wx = cx + margin;
                int wz = cz + margin;
                int columnTop = Math.Min(worldHeight, LodSection.RunYTop(source[0]));
                int columnBottom = Math.Max(0, LodSection.RunYBottom(source[^1]));
                if (columnTop <= columnBottom)
                {
                    foreach (ulong run in source) runs.Add(run);
                    continue;
                }

                bool anyFilled = false;
                for (int y = columnBottom; y < columnTop && !anyFilled; y++)
                {
                    if (cells[Index(wx, wz, y, span, worldHeight)] == Dark) anyFilled = true;
                }
                if (!anyFilled)
                {
                    foreach (ulong run in source) runs.Add(run);
                    continue;
                }

                // A coarse column can contain water or thin cover beside a cavity without
                // containing any opaque terrain at all. Such a column has no honest rock
                // material to extend into the gap. Leaving it unchanged is the fail-open
                // answer; borrowing water creates translucent cave plugs, while borrowing
                // another column changes terrain identity.
                bool hasOpaqueFill = false;
                foreach (ulong run in source)
                {
                    int pid = LodSection.RunPaletteId(run);
                    byte flags = self.PaletteFlags[pid];
                    if ((flags & (LodPaletteEntry.FlagWater | LodPaletteEntry.FlagThin
                        | LodPaletteEntry.FlagSkip)) == 0)
                    {
                        hasOpaqueFill = true;
                        break;
                    }
                }
                if (!hasOpaqueFill)
                {
                    foreach (ulong run in source) runs.Add(run);
                    continue;
                }

                changed = true;
                RebuildColumn(cells, source, runs, wx, wz,
                    columnBottom, columnTop, span, worldHeight, self.PaletteFlags);
            }
        }
        starts[grid * grid] = runs.Count;

        if (!changed) return self;

        return new SectionSnapshot
        {
            Runs = runs.ToArray(),
            ColumnStart = starts,
            Captured = self.Captured,
            PaletteColors = self.PaletteColors,
            PaletteFlags = self.PaletteFlags,
            PaletteTintSlots = self.PaletteTintSlots,
        };
    }

    static void RebuildColumn(byte[] cells, Span<ulong> source, List<ulong> runs,
        int wx, int wz, int columnBottom, int columnTop, int span, int worldHeight,
        byte[] paletteFlags)
    {
        int height = columnTop - columnBottom;
        Span<bool> occupied = height <= 512 ? stackalloc bool[height] : new bool[height];
        Span<int> palette = height <= 512 ? stackalloc int[height] : new int[height];
        Span<bool> known = height <= 512 ? stackalloc bool[height] : new bool[height];

        for (int y = columnBottom; y < columnTop; y++)
        {
            int at = y - columnBottom;
            byte value = cells[Index(wx, wz, y, span, worldHeight)];
            occupied[at] = value == Solid || value == Dark;
            palette[at] = 0;
            known[at] = false;
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

        // A filled cell takes the material below it, then the one above for a pocket that
        // bottoms out the column with nothing beneath it to inherit from.
        int carried = -1;
        for (int at = 0; at < height; at++)
        {
            if (!occupied[at]) continue;
            if (known[at])
            {
                byte flags = paletteFlags[palette[at]];
                if ((flags & (LodPaletteEntry.FlagWater | LodPaletteEntry.FlagThin
                    | LodPaletteEntry.FlagSkip)) == 0) carried = palette[at];
            }
            else if (carried >= 0) palette[at] = carried;
        }
        carried = -1;
        for (int at = height - 1; at >= 0; at--)
        {
            if (!occupied[at]) continue;
            if (known[at])
            {
                byte flags = paletteFlags[palette[at]];
                if ((flags & (LodPaletteEntry.FlagWater | LodPaletteEntry.FlagThin
                    | LodPaletteEntry.FlagSkip)) == 0) carried = palette[at];
            }
            else if (carried >= 0) palette[at] = carried;
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

    /// <summary>
    /// Tally why every subterranean cell of the CENTRE section survived or did not.
    ///
    /// The classification has already happened; this reads it and adds nothing to the
    /// decision. It walks only the centre section because the 3x3 window recentres for
    /// every mesh job, so a margin column belongs to some other section's tally.
    ///
    /// "Subterranean" is the same range the rebuild can touch: from the bottom of a
    /// column's lowest stored run to the top of its highest. Above that is sky and below
    /// it is nothing at all, and neither is a cave the rule could ever have removed.
    /// </summary>
    static void Account(Workspace work, SectionSnapshot self,
        int span, int margin, int worldHeight)
    {
        byte[] cells = work.Cells;
        byte[] marks = work.Marks;
        int grid = LodSection.GridSize;
        const byte NotFill = LodPaletteEntry.FlagWater
            | LodPaletteEntry.FlagThin | LodPaletteEntry.FlagSkip;

        long lit = 0, litF = 0, sight = 0, sightF = 0;
        long route = 0, routeF = 0, unknown = 0, unknownF = 0;
        long noFill = 0, noFillF = 0, water = 0, waterF = 0, removed = 0, removedF = 0;
        long uncaptured = 0;

        for (int cz = 0; cz < grid; cz++)
        {
            for (int cx = 0; cx < grid; cx++)
            {
                int col = LodSection.ColumnIndex(cx, cz);
                if (!self.Captured[col]) { uncaptured++; continue; }

                Span<ulong> runs = self.ColumnRuns(col);
                if (runs.Length == 0) continue;

                int wx = cx + margin;
                int wz = cz + margin;

                bool hasOpaqueFill = false;
                foreach (ulong run in runs)
                {
                    if ((self.PaletteFlags[LodSection.RunPaletteId(run)] & NotFill) != 0) continue;
                    hasOpaqueFill = true;
                    break;
                }

                foreach (ulong run in runs)
                {
                    byte flags = self.PaletteFlags[LodSection.RunPaletteId(run)];
                    if ((flags & LodPaletteEntry.FlagWater) == 0) continue;
                    int top = Math.Min(worldHeight, LodSection.RunYTop(run));
                    int bottom = Math.Max(0, LodSection.RunYBottom(run));
                    for (int y = bottom; y < top; y++)
                    {
                        water++;
                        waterF += Faces(cells, wx, wz, y, span, worldHeight, wantSolid: false);
                    }
                }

                int columnTop = Math.Min(worldHeight, LodSection.RunYTop(runs[0]));
                int columnBottom = Math.Max(0, LodSection.RunYBottom(runs[^1]));

                for (int y = columnBottom; y < columnTop; y++)
                {
                    int i = Index(wx, wz, y, span, worldHeight);
                    byte value = cells[i];
                    if (value == Solid) continue;

                    long faces = Faces(cells, wx, wz, y, span, worldHeight, wantSolid: true);
                    if (value == Dark)
                    {
                        if (hasOpaqueFill) { removed++; removedF += faces; }
                        else { noFill++; noFillF += faces; }
                    }
                    else if (value == Kept)
                    {
                        if ((marks[i] & MarkSurfaceSight) != 0) { sight++; sightF += faces; }
                        else if ((marks[i] & MarkUnknownKeep) != 0) { unknown++; unknownF += faces; }
                        else { route++; routeF += faces; }
                    }
                    else { lit++; litF += faces; }
                }
            }
        }

        Interlocked.Increment(ref sectionsAccounted);
        Interlocked.Add(ref uncapturedColumns, uncaptured);
        Interlocked.Add(ref litCells, lit);
        Interlocked.Add(ref litFaces, litF);
        Interlocked.Add(ref sightCells, sight);
        Interlocked.Add(ref sightFaces, sightF);
        Interlocked.Add(ref routeCells, route);
        Interlocked.Add(ref routeFaces, routeF);
        Interlocked.Add(ref unknownCells, unknown);
        Interlocked.Add(ref unknownFaces, unknownF);
        Interlocked.Add(ref noFillCells, noFill);
        Interlocked.Add(ref noFillFaces, noFillF);
        Interlocked.Add(ref waterCells, water);
        Interlocked.Add(ref waterFaces, waterF);
        Interlocked.Add(ref removedCells, removed);
        Interlocked.Add(ref removedFaces, removedF);
    }

    /// <summary>
    /// How many of a cell's six faces meet solid (for air) or meet air (for water).
    /// Outside the window counts as air, matching every other unknown in this pass.
    /// </summary>
    static int Faces(byte[] cells, int x, int z, int y,
        int span, int worldHeight, bool wantSolid)
    {
        int count = 0;
        Look(x, z, y - 1);
        Look(x, z, y + 1);
        Look(x - 1, z, y);
        Look(x + 1, z, y);
        Look(x, z - 1, y);
        Look(x, z + 1, y);
        return count;

        void Look(int nx, int nz, int ny)
        {
            bool solid = nx >= 0 && nx < span && nz >= 0 && nz < span
                && ny >= 0 && ny < worldHeight
                && cells[Index(nx, nz, ny, span, worldHeight)] == Solid;
            if (solid == wantSolid) count++;
        }
    }
}
