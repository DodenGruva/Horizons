namespace VintageHorizons;

/// <summary>
/// Admits persisted mesh demand as an orientation-independent outward refinement wave.
/// Each exact data key belongs to one radial lane. Nearby coarse coverage begins first;
/// nearby refinement and the next band's coarse coverage then advance together.
/// </summary>
internal sealed class LodRadialDemandPlanner
{
    internal const int DefaultSectorCount = 8;
    internal const int DefaultOutstandingLimit = 32;
    internal const int DefaultRequestsPerFrame = 8;
    internal const int AnchorBlocks = LodRenderDirtyScheduler.PriorityCellBlocks;
    internal const int UnlimitedPlanningRadius = LodWorld.MaxLevelThreshold;

    readonly int sectorCount;
    readonly int outstandingLimit;
    readonly int requestsPerFrame;
    readonly List<Candidate>[] lanes;
    readonly HashSet<long> candidateKeys = new();
    readonly HashSet<long> admitted = new();
    readonly List<long> completedAdmissions = new();

    int sourceRevision = int.MinValue;
    int detailRevision = int.MinValue;
    int anchorX = int.MinValue;
    int anchorZ = int.MinValue;
    int innerRadius = int.MinValue;
    int outerRadius = int.MinValue;
    int nextSector;

    readonly record struct Candidate(long Key, int Wave, double DistanceSq);

    internal LodRadialDemandPlanner(
        int sectorCount = DefaultSectorCount,
        int outstandingLimit = DefaultOutstandingLimit,
        int requestsPerFrame = DefaultRequestsPerFrame)
    {
        this.sectorCount = Math.Max(1, sectorCount);
        this.outstandingLimit = Math.Max(1, outstandingLimit);
        this.requestsPerFrame = Math.Max(1, requestsPerFrame);
        lanes = Enumerable.Range(0, this.sectorCount).Select(_ => new List<Candidate>()).ToArray();
    }

    internal int RequiredCount { get; private set; }
    internal int ReadyCount { get; private set; }
    internal int PendingCount => admitted.Count;
    internal int RequestsStarted { get; private set; }
    internal int RebuildCount { get; private set; }
    internal int CurrentWave { get; private set; } = -1;

    internal void Pump(
        IEnumerable<long> availableData,
        int currentSourceRevision,
        int currentDetailRevision,
        double cameraX,
        double cameraZ,
        int requestedInnerRadius,
        int requestedOuterRadius,
        Predicate<long> ready,
        Predicate<long> pending,
        Predicate<long> terminal,
        Func<long, bool> request)
    {
        int nextAnchorX = CellFor(cameraX);
        int nextAnchorZ = CellFor(cameraZ);
        int boundedInner = Math.Max(0, requestedInnerRadius);
        int boundedOuter = Math.Max(boundedInner + LodSection.SectionBlocks, requestedOuterRadius);
        if (currentSourceRevision != sourceRevision
            || currentDetailRevision != detailRevision
            || nextAnchorX != anchorX
            || nextAnchorZ != anchorZ
            || boundedInner != innerRadius
            || boundedOuter != outerRadius)
        {
            Rebuild(availableData, currentSourceRevision, currentDetailRevision,
                cameraX, cameraZ, nextAnchorX, nextAnchorZ, boundedInner, boundedOuter);
        }

        RefreshAdmissions(ready, pending, terminal);

        int capacity = outstandingLimit - admitted.Count;
        int requestBudget = Math.Min(requestsPerFrame, capacity);
        while (requestBudget > 0)
        {
            int minimumWave = MinimumUnresolvedWave(ready, terminal);
            CurrentWave = minimumWave == int.MaxValue ? -1 : minimumWave;
            if (minimumWave == int.MaxValue) break;

            bool startedAny = false;
            for (int offset = 0; offset < sectorCount && requestBudget > 0; offset++)
            {
                int sector = (nextSector + offset) % sectorCount;
                if (!TryFindRequestable(sector, minimumWave,
                    ready, pending, terminal, out long key)) continue;
                if (!request(key)) continue;

                admitted.Add(key);
                RequestsStarted++;
                requestBudget--;
                startedAny = true;
            }

            nextSector = (nextSector + 1) % sectorCount;
            if (!startedAny) break;
        }

        CountReady(ready, terminal);
    }

    void Rebuild(
        IEnumerable<long> availableData,
        int currentSourceRevision,
        int currentDetailRevision,
        double cameraX,
        double cameraZ,
        int nextAnchorX,
        int nextAnchorZ,
        int boundedInner,
        int boundedOuter)
    {
        foreach (List<Candidate> lane in lanes) lane.Clear();
        candidateKeys.Clear();

        double innerSq = boundedInner * (double)boundedInner;
        double outerSq = boundedOuter * (double)boundedOuter;
        foreach (long key in availableData)
        {
            int level = LodWorld.KeyLevel(key);
            double distanceSq = LodWorld.NearestDistanceSqTo(key, cameraX, cameraZ);
            if (distanceSq > outerSq || FarthestDistanceSqTo(key, cameraX, cameraZ) < innerSq) continue;

            int wanted = LodWorld.WantedLevelForSq(distanceSq);
            if (level < wanted) continue;

            int wave = wanted + (LodWorld.MaxLevel - level);
            int sector = SectorFor(key, cameraX, cameraZ, sectorCount);
            lanes[sector].Add(new Candidate(key, wave, distanceSq));
            candidateKeys.Add(key);
        }

        foreach (List<Candidate> lane in lanes)
        {
            lane.Sort(static (left, right) =>
            {
                int wave = left.Wave.CompareTo(right.Wave);
                if (wave != 0) return wave;
                int distance = left.DistanceSq.CompareTo(right.DistanceSq);
                return distance != 0 ? distance : left.Key.CompareTo(right.Key);
            });
        }

        completedAdmissions.Clear();
        foreach (long key in admitted)
        {
            if (!candidateKeys.Contains(key)) completedAdmissions.Add(key);
        }
        foreach (long key in completedAdmissions) admitted.Remove(key);

        sourceRevision = currentSourceRevision;
        detailRevision = currentDetailRevision;
        anchorX = nextAnchorX;
        anchorZ = nextAnchorZ;
        innerRadius = boundedInner;
        outerRadius = boundedOuter;
        RequiredCount = candidateKeys.Count;
        RebuildCount++;
    }

    void RefreshAdmissions(Predicate<long> ready, Predicate<long> pending, Predicate<long> terminal)
    {
        completedAdmissions.Clear();
        foreach (long key in admitted)
        {
            if (ready(key) || terminal(key) || !candidateKeys.Contains(key) || !pending(key))
                completedAdmissions.Add(key);
        }
        foreach (long key in completedAdmissions) admitted.Remove(key);
    }

    int MinimumUnresolvedWave(Predicate<long> ready, Predicate<long> terminal)
    {
        int minimum = int.MaxValue;
        foreach (List<Candidate> lane in lanes)
        {
            foreach (Candidate candidate in lane)
            {
                if (ready(candidate.Key) || terminal(candidate.Key)) continue;
                if (candidate.Wave < minimum) minimum = candidate.Wave;
                break;
            }
        }
        return minimum;
    }

    bool TryFindRequestable(int sector, int maximumWave,
        Predicate<long> ready, Predicate<long> pending, Predicate<long> terminal, out long key)
    {
        key = 0;
        List<Candidate> lane = lanes[sector];
        int laneWave = int.MaxValue;
        foreach (Candidate candidate in lane)
        {
            if (ready(candidate.Key) || terminal(candidate.Key)) continue;
            if (laneWave == int.MaxValue) laneWave = candidate.Wave;
            if (candidate.Wave != laneWave || laneWave > maximumWave) return false;
            if (pending(candidate.Key)) continue;
            key = candidate.Key;
            return true;
        }
        return false;
    }

    void CountReady(Predicate<long> ready, Predicate<long> terminal)
    {
        int count = 0;
        foreach (long key in candidateKeys)
        {
            if (ready(key) || terminal(key)) count++;
        }
        ReadyCount = count;
    }

    internal string Describe() => RequiredCount == 0
        ? "wave has no exact cached rows in range"
        : $"wave {CurrentWave}, {ReadyCount}/{RequiredCount} ready, "
            + $"{PendingCount} admitted, {RequestsStarted} requests";

    internal static int WaveFor(long key, double cameraX, double cameraZ)
    {
        int wanted = LodWorld.WantedLevelForSq(
            LodWorld.NearestDistanceSqTo(key, cameraX, cameraZ));
        return wanted + (LodWorld.MaxLevel - LodWorld.KeyLevel(key));
    }

    internal static int SectorFor(long key, double cameraX, double cameraZ, int sectors)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        double centerX = LodWorld.KeySx(key) * (double)footprint + footprint * 0.5;
        double centerZ = LodWorld.KeySz(key) * (double)footprint + footprint * 0.5;
        double angle = Math.Atan2(centerZ - cameraZ, centerX - cameraX);
        // Rotate half a lane so the cardinal/diagonal axes sit at lane centres rather
        // than boundaries. Wrap explicitly because Atan2 may return positive PI.
        double normalized = (angle + Math.PI + Math.PI / sectors) / (Math.PI * 2.0);
        int sector = (int)Math.Floor(normalized * sectors) % sectors;
        return sector < 0 ? sector + sectors : sector;
    }

    static double FarthestDistanceSqTo(long key, double cameraX, double cameraZ)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        double minX = LodWorld.KeySx(key) * (double)footprint;
        double minZ = LodWorld.KeySz(key) * (double)footprint;
        double dx = Math.Max(Math.Abs(cameraX - minX), Math.Abs(cameraX - (minX + footprint)));
        double dz = Math.Max(Math.Abs(cameraZ - minZ), Math.Abs(cameraZ - (minZ + footprint)));
        return dx * dx + dz * dz;
    }

    static int CellFor(double coordinate) => (int)Math.Floor(coordinate / AnchorBlocks);
}
