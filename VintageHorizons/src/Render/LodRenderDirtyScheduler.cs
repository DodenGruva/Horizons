namespace VintageHorizons;

/// <summary>
/// The owning-thread render-dirty set plus the additions the renderer has not indexed
/// yet. Removals need no matching queue entry: the priority scheduler validates set
/// membership when an old entry reaches its head.
/// </summary>
public sealed class LodRenderDirtySet : IEnumerable<long>
{
    readonly HashSet<long> keys = new();
    readonly Queue<long> additions = new();

    public int Count => keys.Count;
    internal long Generation { get; private set; }

    public bool Add(long key)
    {
        if (!keys.Add(key)) return false;
        additions.Enqueue(key);
        return true;
    }

    public bool Remove(long key) => keys.Remove(key);
    public bool Contains(long key) => keys.Contains(key);

    public void Clear()
    {
        keys.Clear();
        additions.Clear();
        Generation++;
    }

    internal bool TryTakeAddition(out long key) => additions.TryDequeue(out key);
    internal void DiscardAdditions() => additions.Clear();

    public HashSet<long>.Enumerator GetEnumerator() => keys.GetEnumerator();
    IEnumerator<long> IEnumerable<long>.GetEnumerator() => GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Nearest-first render-dirty index. Existing entries are re-ranked only when the
/// camera crosses a coarse cell (or detail policy/world generation changes), while
/// new dirty keys enter incrementally. Ordinary stationary frames therefore do no
/// work proportional to the complete explored/dirty history.
/// </summary>
internal sealed class LodRenderDirtyScheduler
{
    internal const int PriorityCellBlocks = 256;

    readonly PriorityQueue<long, double> queue = new();
    readonly List<long> prune = new();
    readonly List<(long Key, double Priority)> blocked = new();

    int cellX = int.MinValue;
    int cellZ = int.MinValue;
    double anchorX;
    double anchorZ;
    double detailPolicy = double.NaN;
    long dirtyGeneration = -1;

    internal int RebuildCount { get; private set; }
    internal int IndexedCount => queue.Count;

    internal void Refresh(LodRenderDirtySet dirty, double cameraX, double cameraZ,
        double currentDetailPolicy, Predicate<long> keep)
    {
        int nextCellX = CellFor(cameraX);
        int nextCellZ = CellFor(cameraZ);
        bool rebuild = nextCellX != cellX || nextCellZ != cellZ
            || currentDetailPolicy != detailPolicy || dirty.Generation != dirtyGeneration;

        if (rebuild)
        {
            cellX = nextCellX;
            cellZ = nextCellZ;
            anchorX = CellCenter(cellX);
            anchorZ = CellCenter(cellZ);
            detailPolicy = currentDetailPolicy;
            dirtyGeneration = dirty.Generation;
            Rebuild(dirty, keep);
            return;
        }

        while (dirty.TryTakeAddition(out long key))
        {
            // The key may have been removed after it entered the delta queue.
            if (!dirty.Contains(key)) continue;
            if (!keep(key))
            {
                dirty.Remove(key);
                continue;
            }
            queue.Enqueue(key, Priority(key));
        }
    }

    void Rebuild(LodRenderDirtySet dirty, Predicate<long> keep)
    {
        queue.Clear();
        blocked.Clear();
        prune.Clear();
        dirty.DiscardAdditions();

        foreach (long key in dirty)
        {
            if (keep(key)) queue.Enqueue(key, Priority(key));
            else prune.Add(key);
        }
        foreach (long key in prune) dirty.Remove(key);

        RebuildCount++;
    }

    /// <summary>
    /// Remove and return the nearest key that can start work. Busy keys retain both
    /// their dirty obligation and priority. The caller supplies a finite examination
    /// budget so stale or temporarily blocked entries can never recreate a whole-set
    /// frame scan.
    /// </summary>
    internal bool TryTake(LodRenderDirtySet dirty, Predicate<long> keep,
        Predicate<long> isBlocked, int examinationBudget, out long key)
    {
        blocked.Clear();
        int examined = 0;

        while (examined++ < examinationBudget && queue.TryDequeue(out long candidate, out double priority))
        {
            if (!dirty.Contains(candidate)) continue;
            if (!keep(candidate))
            {
                dirty.Remove(candidate);
                continue;
            }
            if (isBlocked(candidate))
            {
                blocked.Add((candidate, priority));
                continue;
            }

            dirty.Remove(candidate);
            RestoreBlocked();
            key = candidate;
            return true;
        }

        RestoreBlocked();
        key = default;
        return false;
    }

    void RestoreBlocked()
    {
        foreach ((long key, double priority) in blocked) queue.Enqueue(key, priority);
        blocked.Clear();
    }

    double Priority(long key) => LodWorld.NearestDistanceSqTo(key, anchorX, anchorZ);

    static int CellFor(double coordinate) => (int)Math.Floor(coordinate / PriorityCellBlocks);
    static double CellCenter(int cell) => (cell + 0.5) * PriorityCellBlocks;
}
