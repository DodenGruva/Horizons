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
/// THE RULE, in two parts.
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
/// Two ways in, because a budget alone would plug a tunnel bored through a mountain: longer
/// than twice the reach, its middle goes dark and gets filled, and what is lost is the
/// daylight you could see through it. A dead end touches daylight in ONE patch, the ring
/// where the light petered out; a passage that goes somewhere touches it in TWO, one per
/// mouth, however long or winding. So a pocket with more than one way in is never filled,
/// and unlike a distance threshold there is nothing in that to tune wrong.
///
/// WHY IT IS SAFE. Diffuse light spreads along every path, straight ones included, so
/// anything a straight line of sight could reach within the budget is lit and kept. Sight
/// travels only in straight lines; light reaches at least as far. Every unknown is resolved
/// toward keeping geometry - an absent neighbour counts as open air, an uncaptured column
/// counts as air, and the window's outer edge is treated as open sky - so the failure
/// direction is drawing something invisible, never removing something visible.
///
/// WHAT IT DOES NOT CLAIM. A large cavern lit through its own shaft is kept even though no
/// player can see it, because catching that needs a true line-of-sight test over every view
/// angle, and an offline attempt at one produced answers that moved with the sampling rather
/// than converging. That remains unbuilt on purpose.
/// </summary>
public static class LodCaveCull
{
    /// <summary>
    /// How far daylight spreads, in world blocks. Offline the answer barely moves between 4
    /// and 128 once the two-ways-in rule is carrying the through-passages, so this is chosen
    /// small: the working window has to be as wide as the reach, and every block of it costs
    /// memory and time on a mesh thread.
    /// </summary>
    public const int DefaultReach = 32;

    /// <summary>
    /// Fewest column steps of spread before the rule is allowed to run at all.
    ///
    /// Light is measured in blocks but travels between COLUMNS, and a column covers more
    /// ground at every coarser level - 64 blocks at L6. So the same reach that crosses 32
    /// columns at full detail crosses half of one at the coarse end, where nothing would
    /// spread sideways and everything below the surface would read as dark. Rather than
    /// scale the reach with the level and pretend that means the same thing, the rule
    /// declines to run where it cannot see far enough to be conservative.
    /// </summary>
    const int MinimumColumnReach = 4;

    // Pooled per thread, for the reason the mesher pools its own buffers: this is millions
    // of cells, it runs on every mesh build, and a fresh pair per job would churn the large
    // object heap continuously during streaming.
    [ThreadStatic] static Workspace? workspace;

    sealed class Workspace
    {
        public byte[] Cells = Array.Empty<byte>();
        public bool[] Visited = Array.Empty<bool>();
        public List<int>[] Buckets = Array.Empty<List<int>>();
        public readonly List<int> Pocket = new();
        public readonly List<int> Frontier = new();
        public readonly List<ulong> Runs = new();

        public void Ensure(int cells, int buckets)
        {
            if (Cells.Length < cells)
            {
                Cells = new byte[cells];
                Visited = new bool[cells];
            }
            else
            {
                Array.Clear(Cells, 0, cells);
                Array.Clear(Visited, 0, cells);
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

    const byte Solid = 255;
    const byte Kept = 254;      // dark, but with more than one way in
    const byte Dark = 0;

    /// <summary>
    /// The section with unreachable cavities filled, or the same instance when there was
    /// nothing to fill. Neighbours are read for context only and are never modified.
    /// </summary>
    /// <param name="reach">Daylight reach in world blocks; zero or less disables the pass.</param>
    public static SectionSnapshot FillUnseen(
        SectionSnapshot self, SectionSnapshot?[] neighbors, int level, int reach)
    {
        if (reach <= 0) return self;

        int step = LodWorld.ColumnStepBlocks(level);
        int reachColumns = reach / step;
        if (reachColumns < MinimumColumnReach) return self;

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
        if (worldHeight <= 2) return self;

        Workspace work = workspace ??= new Workspace();
        work.Ensure(span * span * worldHeight, reach);

        BuildOccupancy(work.Cells, self, neighbors, span, margin, worldHeight);
        SpreadDaylight(work, span, worldHeight, reach, step);
        KeepPassages(work, span, worldHeight);

        return Rebuild(work, self, span, margin, worldHeight);
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

    static void BuildOccupancy(byte[] cells, SectionSnapshot self, SectionSnapshot?[] neighbors,
        int span, int margin, int worldHeight)
    {
        for (int wz = 0; wz < span; wz++)
        {
            for (int wx = 0; wx < span; wx++)
            {
                if (!Locate(self, neighbors, wx, wz, margin, out SectionSnapshot section, out int col))
                {
                    continue;                                   // absent: all air
                }
                if (!section.Captured[col]) continue;            // uncaptured: all air

                foreach (ulong run in section.ColumnRuns(col))
                {
                    int yTop = Math.Min(worldHeight, LodSection.RunYTop(run));
                    int yBottom = Math.Max(0, LodSection.RunYBottom(run));
                    for (int y = yBottom; y < yTop; y++)
                    {
                        cells[Index(wx, wz, y, span, worldHeight)] = Solid;
                    }
                }
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
        List<int>[] buckets = work.Buckets;

        void Seed(int x, int z, int y)
        {
            int i = Index(x, z, y, span, worldHeight);
            if (cells[i] != Dark) return;
            cells[i] = (byte)reach;
            buckets[reach].Add(i);
        }

        for (int z = 0; z < span; z++)
        {
            for (int x = 0; x < span; x++) Seed(x, z, worldHeight - 1);
        }
        for (int y = 0; y < worldHeight; y++)
        {
            for (int t = 0; t < span; t++)
            {
                Seed(0, t, y);
                Seed(span - 1, t, y);
                Seed(t, 0, y);
                Seed(t, span - 1, y);
            }
        }

        void Spread(int x, int z, int y, int value)
        {
            if (value <= 0) return;
            if (x < 0 || x >= span || z < 0 || z >= span || y < 0 || y >= worldHeight) return;
            int i = Index(x, z, y, span, worldHeight);
            if (cells[i] == Solid || cells[i] >= value) return;
            cells[i] = (byte)value;
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

                Spread(x, z, y - 1, level == reach ? reach : level - 1);
                Spread(x, z, y + 1, level - 1);
                Spread(x - 1, z, y, level - step);
                Spread(x + 1, z, y, level - step);
                Spread(x, z - 1, y, level - step);
                Spread(x, z + 1, y, level - step);
            }
        }
    }

    /// <summary>
    /// Promote every dark pocket with two or more separate ways in back to kept. Patches are
    /// grouped among the frontier cells themselves rather than through open air, which is the
    /// whole point: both mouths of a through-tunnel reach the same sky, so grouping through
    /// the sky would call them one way in and plug the tunnel anyway.
    /// </summary>
    static void KeepPassages(Workspace work, int span, int worldHeight)
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

            if (CountPatches(frontier, span, worldHeight) < 2) continue;
            foreach (int i in pocket) cells[i] = Kept;

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
    /// Connected groups among the frontier cells, touching on any of the 26 directions so
    /// that a merely ragged opening is not mistaken for two of them. Stops at two, because
    /// two is all the caller needs to know.
    /// </summary>
    static int CountPatches(List<int> frontier, int span, int worldHeight)
    {
        if (frontier.Count == 0) return 0;

        var members = new HashSet<int>(frontier);
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        int patches = 0;

        foreach (int start in frontier)
        {
            if (!seen.Add(start)) continue;
            if (++patches >= 2) return patches;

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
                            if (!members.Contains(n) || !seen.Add(n)) continue;
                            stack.Push(n);
                        }
                    }
                }
            }
        }
        return patches;
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

                changed = true;
                RebuildColumn(cells, source, runs, wx, wz,
                    columnBottom, columnTop, span, worldHeight);
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
        int wx, int wz, int columnBottom, int columnTop, int span, int worldHeight)
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
            if (known[at]) carried = palette[at];
            else if (carried >= 0) palette[at] = carried;
        }
        carried = -1;
        for (int at = height - 1; at >= 0; at--)
        {
            if (!occupied[at]) continue;
            if (known[at]) carried = palette[at];
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
}
