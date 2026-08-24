using Vintagestory.API.MathTools;

namespace VintageHorizons;

/// <summary>
/// The in-memory section pyramid: all detail levels of LodSections, dirty tracking,
/// and child→parent mip propagation. All mutation happens on the main thread; the
/// worker thread only ever reads immutable snapshots (Runs/ColumnStart arrays are
/// replaced wholesale, never edited in place).
/// </summary>
public class LodWorld
{
    public const int MaxLevel = 6; // L6 sections span 4096 blocks (64-block columns at the horizon)

    /// <summary>Default distance at which each level L1-L6 begins.</summary>
    public static readonly int[] DefaultLevelThresholds = { 512, 1024, 2048, 4096, 8192, 16384 };

    /// <summary>Smallest configurable transition and drag increment in blocks.</summary>
    public const int MinDetailDistance = 256;
    public const int ThresholdStepBlocks = 64;

    /// <summary>Largest transition exposed by the shared configuration scale.</summary>
    public const int MaxLevelThreshold = 32768;

    /// <summary>Legacy/base-distance command range; L2-L6 double from this value.</summary>
    public const int MaxDetailDistance = 1024;

    static readonly double[] levelThresholds = DefaultLevelThresholds.Select(value => (double)value).ToArray();

    /// <summary>
    /// Compatibility shorthand for the first transition. Setting it rebuilds the
    /// traditional doubling sequence, which keeps .vhdetail and older configuration
    /// files meaningful while .vhconfig can edit every threshold independently.
    /// </summary>
    public static double DetailDistance
    {
        get => levelThresholds[0];
        set => SetDoublingThresholds(value);
    }

    /// <summary>
    /// Changes whenever any threshold changes. Distance-sensitive indexes use this
    /// identity rather than only L1, because moving L4 must refresh them too.
    /// </summary>
    public static int DetailPolicyRevision { get; private set; }

    public static int[] GetLevelThresholds() =>
        levelThresholds.Select(value => (int)Math.Round(value)).ToArray();

    public static double ThresholdForLevel(int level)
    {
        if (level < 1 || level > MaxLevel) throw new ArgumentOutOfRangeException(nameof(level));
        return levelThresholds[level - 1];
    }

    public static void SetLevelThresholds(IReadOnlyList<int> thresholds)
    {
        int[] normalized = NormalizeLevelThresholds(thresholds, DefaultLevelThresholds[0]);
        bool changed = false;
        for (int i = 0; i < MaxLevel; i++)
        {
            if (levelThresholds[i] == normalized[i]) continue;
            levelThresholds[i] = normalized[i];
            changed = true;
        }
        if (changed) DetailPolicyRevision++;
    }

    public static int[] NormalizeLevelThresholds(IReadOnlyList<int>? thresholds, int legacyDetailDistance)
    {
        int[] values;
        if (thresholds == null || thresholds.Count != MaxLevel)
        {
            int first = Math.Clamp(legacyDetailDistance, MinDetailDistance, MaxDetailDistance);
            values = Enumerable.Range(0, MaxLevel).Select(i => first * (1 << i)).ToArray();
        }
        else
        {
            values = thresholds.ToArray();
        }

        // Preserve room for every later marker, then walk left-to-right. This makes a
        // malformed hand-edited config deterministic without letting markers overlap.
        for (int i = 0; i < MaxLevel; i++)
        {
            int min = MinDetailDistance + i * ThresholdStepBlocks;
            if (i > 0) min = Math.Max(min, values[i - 1] + ThresholdStepBlocks);
            int max = MaxLevelThreshold - (MaxLevel - 1 - i) * ThresholdStepBlocks;
            values[i] = Math.Clamp(values[i], min, max);
        }
        return values;
    }

    static void SetDoublingThresholds(double first)
    {
        first = Math.Clamp(first, MinDetailDistance, MaxDetailDistance);
        bool changed = false;
        for (int i = 0; i < MaxLevel; i++)
        {
            double value = first * (1 << i);
            if (levelThresholds[i] == value) continue;
            levelThresholds[i] = value;
            changed = true;
        }
        if (changed) DetailPolicyRevision++;
    }

    public readonly Dictionary<long, LodSection> Sections = new();
    readonly Queue<long> residentEvictionOrder = new();
    readonly HashSet<long> residentEvictionQueued = new();

    /// <summary>Set by the coordinator when persistence is available: reload an evicted section from disk.</summary>
    public Func<long, LodSection?>? LoadFromStore;

    public int EvictedSectionsTotal { get; private set; }

    /// <summary>
    /// How many times a real content change was reported, against how many section meshes
    /// that made stale. The amplification between them was inferred from a stationary run
    /// (about 145 change events against 5,057 mesh replacements) and never counted, so it
    /// could not be quoted as measured. These two count it.
    /// </summary>
    public long MarkChangedCalls { get; private set; }
    public long MarkChangedRenderDirtied { get; private set; }

    /// <summary>
    /// Neighbour rebuilds an interior change did not have to ask for. This is the saving,
    /// stated rather than estimated: a run where it stays near zero means real changes
    /// land on section edges and the conservative refresh was never the waste.
    /// </summary>
    public long MarkChangedNeighborsSkipped { get; private set; }

    /// <summary>Sections whose mesh is stale.</summary>
    public readonly LodRenderDirtySet RenderDirty = new();

    /// <summary>Sections whose DB row is stale.</summary>
    public readonly HashSet<long> SaveDirty = new();

    /// <summary>Sections whose parent still needs to absorb their content (persisted as ApplyToParent).</summary>
    public readonly HashSet<long> MipDirty = new();

    // A job keeps its child dirty until a revision-valid result commits. Parent pins
    // stop the resident section receiving that result from disappearing meanwhile.
    readonly Dictionary<long, long> mipInFlight = new();
    readonly Dictionary<long, int> mipParentPins = new();
    readonly Dictionary<long, long> contentRevisions = new();
    readonly Dictionary<long, long> persistenceRevisions = new();
    readonly Dictionary<long, long> queuedSaveRevisions = new();

    public int MipInFlightCount => mipInFlight.Count;
    public long ContentRevision(long key) => contentRevisions.GetValueOrDefault(key);
    public long PersistenceRevision(long key) => persistenceRevisions.GetValueOrDefault(key);
    public long QueuedSaveRevision(long key) => queuedSaveRevisions.GetValueOrDefault(key);

    /// <summary>Every key (all levels) that holds data or has any descendant with data. Drives quadtree descent.</summary>
    public readonly HashSet<long> HasDataSet = new();

    /// <summary>
    /// Keys that name an actual resident, local-store, or offered remote section.
    /// Structural ancestors synthesised only for quadtree descent are deliberately absent.
    /// </summary>
    public readonly HashSet<long> AvailableDataSet = new();

    /// <summary>Changes only when exact section availability changes.</summary>
    public int AvailableDataRevision { get; private set; }

    /// <summary>Top-level (MaxLevel) ancestor keys - the quadtree roots.</summary>
    public readonly HashSet<long> TopLevelKeys = new();

    // ---- Key packing: level(4) | sz(30) | sx(30). VS world coords are non-negative. ----

    public static long SectionKey(int level, int sx, int sz) =>
        ((long)level << 60) | ((long)(sz & 0x3FFFFFFF) << 30) | (uint)(sx & 0x3FFFFFFF);

    public static int KeyLevel(long key) => (int)(key >>> 60);
    public static int KeySx(long key) => (int)(key & 0x3FFFFFFF);
    public static int KeySz(long key) => (int)((key >> 30) & 0x3FFFFFFF);

    public static long ParentKey(long key) =>
        SectionKey(KeyLevel(key) + 1, KeySx(key) >> 1, KeySz(key) >> 1);

    public static long ChildKey(long key, int qx, int qz) =>
        SectionKey(KeyLevel(key) - 1, (KeySx(key) << 1) + qx, (KeySz(key) << 1) + qz);

    public static long NeighborKey(long key, int dx, int dz) =>
        SectionKey(KeyLevel(key), KeySx(key) + dx, KeySz(key) + dz);

    /// <summary>Section footprint in blocks at this key's level.</summary>
    public static int KeyFootprintBlocks(long key) => LodSection.SectionBlocks << KeyLevel(key);

    /// <summary>
    /// Distance from a point to the nearest edge of a section's footprint, squared.
    /// Nearest-edge rather than centre: an L6 section spans 4096 blocks, so centre distance
    /// would rank a section the viewer is standing inside as far away.
    /// </summary>
    public static double NearestDistanceSqTo(long key, double x, double z)
    {
        int footprint = KeyFootprintBlocks(key);
        double minX = KeySx(key) * (double)footprint;
        double minZ = KeySz(key) * (double)footprint;
        double dx = Math.Max(0, Math.Max(minX - x, x - (minX + footprint)));
        double dz = Math.Max(0, Math.Max(minZ - z, z - (minZ + footprint)));
        return dx * dx + dz * dz;
    }

    public static int ColumnStepBlocks(int level) => LodSection.ColumnStepBlocks << level;

    public LodSection GetOrCreateSection(long key)
    {
        if (Sections.TryGetValue(key, out LodSection? section)) return section;

        // A previously-evicted section must come back from disk, not start empty -
        // capture merges and mip propagation would otherwise clobber stored data.
        if (HasDataSet.Contains(key))
        {
            section = LoadFromStore?.Invoke(key);
            if (section != null)
            {
                Sections[key] = section;
                TrackResident(key);
                SectionBecameResident?.Invoke(key);
                return section;
            }
        }

        Sections[key] = section = new LodSection();
        TrackResident(key);
        LoadFailed.Remove(key); // it has data again; a past miss must not block reloads
        RegisterAvailableData(key);
        RegisterInTree(key);
        return section;
    }

    /// <summary>Get a section from RAM or disk without creating an empty one. For mesh scheduling.</summary>
    public bool TryGetOrLoad(long key, out LodSection section)
    {
        if (Sections.TryGetValue(key, out section!)) return true;
        if (!HasDataSet.Contains(key)) return false;

        LodSection? loaded = LoadFromStore?.Invoke(key);
        if (loaded == null) return false;

        Sections[key] = section = loaded;
        TrackResident(key);
        SectionBecameResident?.Invoke(key);
        return true;
    }

    /// <summary>Ask the storage thread to reload an evicted section; null when unavailable.</summary>
    public Action<long>? RequestAsyncLoad;

    /// <summary>
    /// A section arrived in RAM carrying stored data. Neighbouring meshes may have been
    /// built while it was absent, and a mesher cannot tell "no data here" from "not
    /// loaded yet" - the renderer subscribes to repair the sides that guessed. Not the
    /// same as MarkChanged: nothing about the world changed, only what is in memory, so
    /// this must not dirty saves, mips, or the arriving section itself.
    /// </summary>
    public Action<long>? SectionBecameResident;

    /// <summary>Keys with a reload in flight, so the render path stops re-requesting them.</summary>
    public readonly HashSet<long> LoadsInFlight = new();

    /// <summary>
    /// Keys whose reload came back empty (row missing, or deleted as unreadable).
    /// Without this the demand planner would re-request them every frame forever,
    /// since the section never becomes resident.
    /// </summary>
    public readonly HashSet<long> LoadFailed = new();

    /// <summary>
    /// Non-blocking variant for the render path: returns false and starts a background
    /// reload rather than stalling the frame on a decompress. Exact dirty ownership or
    /// the radial demand planner re-requests the mesh once the section lands.
    /// </summary>
    public bool TryGetForRender(long key, out LodSection section)
    {
        if (Sections.TryGetValue(key, out section!)) return true;
        if (!HasDataSet.Contains(key) || LoadFailed.Contains(key)) return false;

        if (RequestAsyncLoad == null)
        {
            // No storage thread (no persistence this session): fall back to inline.
            return TryGetOrLoad(key, out section);
        }

        if (LoadsInFlight.Add(key)) RequestAsyncLoad(key);
        return false;
    }

    /// <summary>
    /// Install a section that finished loading in the background. A section that
    /// became resident while the read was in flight (a capture created or reloaded it
    /// inline) is strictly newer, so the arriving copy is discarded.
    /// </summary>
    public void InstallLoaded(long key, LodSection? section)
    {
        LoadsInFlight.Remove(key);
        if (section == null)
        {
            LoadFailed.Add(key);
            return;
        }
        if (Sections.ContainsKey(key)) return;

        Sections[key] = section;
        TrackResident(key);
        RegisterAvailableData(key);

        // Deliberately not marked render-dirty: reloads are requested by rendering AND
        // by mip propagation, and the radial planner re-requests a mesh if it still owns
        // demand here. Marking every arrival would mesh sections that only propagation
        // asked for.
        //
        // Neighbours are a different question, and the answer is not "all four" for the
        // same reason. Only a neighbour that actually built a mesh against our absence
        // needs one, which is a question about meshes and so belongs to the renderer.
        SectionBecameResident?.Invoke(key);
    }

    /// <summary>
    /// Drop cold sections from RAM (their rows stay on disk; HasDataSet keeps the
    /// quadtree semantics intact). Cold = the walk wants this area at least two
    /// levels coarser than this section, and nothing dirty references it.
    /// </summary>
    public int LastSweepChecked { get; private set; }
    public int LastSweepPinned { get; private set; }
    public int LastSweepCold { get; private set; }

    public void EvictColdSections(double camX, double camZ, int budget)
    {
        LastSweepChecked = 0;
        LastSweepPinned = 0;
        LastSweepCold = 0;
        if (budget <= 0) return;

        // Sections normally enter through the methods above. Seed only as a defensive
        // fallback for diagnostic/tests that populate the public dictionary directly.
        if (residentEvictionOrder.Count == 0 && Sections.Count > 0)
            foreach (long key in Sections.Keys) TrackResident(key);

        int available = residentEvictionOrder.Count;
        for (int n = 0; n < budget && n < available; n++)
        {
            long key = residentEvictionOrder.Dequeue();
            LastSweepChecked++;
            if (!Sections.ContainsKey(key))
            {
                residentEvictionQueued.Remove(key);
                continue;
            }

            int level = KeyLevel(key);
            if (level >= MaxLevel)
            {
                residentEvictionOrder.Enqueue(key);
                continue;
            }
            // Unsaved or unpropagated data pins a section; a pending mesh rebuild does
            // NOT - the scheduler demand-reloads from disk when its turn comes.
            if (SaveDirty.Contains(key) || MipDirty.Contains(key) || mipParentPins.ContainsKey(key))
            {
                LastSweepPinned++;
                residentEvictionOrder.Enqueue(key);
                continue;
            }

            int footprint = KeyFootprintBlocks(key);
            double minX = KeySx(key) * (double)footprint;
            double minZ = KeySz(key) * (double)footprint;
            double dx = Math.Max(0, Math.Max(minX - camX, camX - (minX + footprint)));
            double dz = Math.Max(0, Math.Max(minZ - camZ, camZ - (minZ + footprint)));
            double distSq = dx * dx + dz * dz;

            if (WantedLevelForSq(distSq) < level + 2)
            {
                residentEvictionOrder.Enqueue(key);
                continue;
            }

            LastSweepCold++;
            Sections.Remove(key);
            residentEvictionQueued.Remove(key);
            EvictedSectionsTotal++;
        }
    }

    void TrackResident(long key)
    {
        if (!residentEvictionQueued.Add(key)) return;
        residentEvictionOrder.Enqueue(key);
    }

    public static int WantedLevelFor(double distance)
    {
        // DetailDistance names the first actual transition: L0 below it, L1 at it.
        // Each later level begins twice as far away as the previous one.
        for (int level = MaxLevel; level > 0; level--)
        {
            if (distance >= levelThresholds[level - 1]) return level;
        }
        return 0;
    }

    /// <summary>
    /// The same answer as <see cref="WantedLevelFor"/>, from the SQUARED distance.
    ///
    /// The quadtree walk asks this once per visited node, and every caller had to take a
    /// square root to ask. That is avoidable: level L (for L &gt; 0) is wanted from
    /// its configured threshold outward, so the question is a comparison against a fixed
    /// radius per level, and comparisons survive squaring.
    ///
    /// Measured at 951 resident sections, the walk cost 387us a frame and the prune pass
    /// runs the same test over the whole dirty set on top of that.
    ///
    /// The table is rebuilt when any threshold changes, which .vhconfig can do live.
    /// Callers are the render frame and the eviction sweep, both on the main thread, so
    /// no lock is needed; a worker must not call this.
    /// </summary>
    public static int WantedLevelForSq(double distanceSq)
    {
        if (wantedTableRevision != DetailPolicyRevision) RebuildWantedTable();

        for (int level = MaxLevel; level > 0; level--)
        {
            if (distanceSq >= wantedThresholdSq[level]) return level;
        }
        return 0;
    }

    static int wantedTableRevision = -1;
    static readonly double[] wantedThresholdSq = new double[MaxLevel + 1];

    static void RebuildWantedTable()
    {
        wantedThresholdSq[0] = 0;
        for (int level = 1; level <= MaxLevel; level++)
        {
            double radius = levelThresholds[level - 1];
            wantedThresholdSq[level] = radius * radius;
        }
        wantedTableRevision = DetailPolicyRevision;
    }

    void RegisterInTree(long key)
    {
        while (true)
        {
            HasDataSet.Add(key);
            if (KeyLevel(key) == MaxLevel)
            {
                TopLevelKeys.Add(key);
                return;
            }
            key = ParentKey(key);
        }
    }

    void RegisterAvailableData(long key)
    {
        if (AvailableDataSet.Add(key)) AvailableDataRevision++;
    }

    /// <summary>
    /// Record that a section's content changed.
    ///
    /// A neighbour's mesh hides its faces against our edge columns, so a change on a
    /// shared edge really does make the neighbour's mesh wrong. A change in the interior
    /// does not, and every caller used to claim all four anyway - which is where a
    /// stationary world turned 64 changed sections into 5,057 mesh rebuilds, because the
    /// same five-way fan-out repeats at every level of the mip pyramid.
    ///
    /// <paramref name="touchedEdges"/> defaults to every edge, so a caller that genuinely
    /// does not know - a whole-section install, a palette repair - keeps the old
    /// conservative behaviour by saying nothing. Only callers holding a real answer
    /// narrow it.
    /// </summary>
    public void MarkChanged(long key, int touchedEdges = LodSection.EdgeAll)
    {
        MarkChangedCalls++;
        contentRevisions[key] = ContentRevision(key) + 1;
        RenderDirty.Add(key);
        MarkChangedRenderDirtied++;
        MarkSaveDirty(key);
        if (KeyLevel(key) < MaxLevel) MipDirty.Add(key);

        for (int d = 0; d < 4; d++)
        {
            long nk = NeighborKey(key, d == 0 ? -1 : d == 1 ? 1 : 0, d == 2 ? -1 : d == 3 ? 1 : 0);
            if (!Sections.ContainsKey(nk)) continue;

            // Bit order matches the neighbour order above: -X, +X, -Z, +Z.
            if ((touchedEdges & (1 << d)) == 0)
            {
                MarkChangedNeighborsSkipped++;
                continue;
            }

            RenderDirty.Add(nk);
            MarkChangedRenderDirtied++;
        }
    }

    /// <summary>
    /// Record a change to the row that must become durable. Kept separate from content
    /// revisions because clearing ApplyToParent changes the stored row without changing
    /// terrain content.
    /// </summary>
    public void MarkSaveDirty(long key)
    {
        persistenceRevisions[key] = PersistenceRevision(key) + 1;
        SaveDirty.Add(key);
    }

    /// <summary>
    /// Reserve the current row revision for a background snapshot. Dirty state remains
    /// set until the storage thread acknowledges this exact revision.
    /// </summary>
    public bool TryQueueSave(long key, out long revision)
    {
        revision = PersistenceRevision(key);
        if (!SaveDirty.Contains(key) || revision == 0) return false;
        if (queuedSaveRevisions.GetValueOrDefault(key) >= revision) return false;
        queuedSaveRevisions[key] = revision;
        return true;
    }

    /// <summary>Undo a reservation that the storage queue could not accept.</summary>
    public void CancelQueuedSave(long key, long revision)
    {
        if (queuedSaveRevisions.GetValueOrDefault(key) == revision)
            queuedSaveRevisions.Remove(key);
    }

    /// <summary>
    /// Apply one storage acknowledgement. Success clears dirty state only when it names
    /// the newest row revision; a failure or stale success leaves the obligation intact.
    /// </summary>
    /// <returns>True only when the newest revision became durable.</returns>
    public bool CompleteSave(long key, long revision, bool succeeded)
    {
        if (queuedSaveRevisions.GetValueOrDefault(key) == revision)
            queuedSaveRevisions.Remove(key);

        if (!succeeded || PersistenceRevision(key) != revision) return false;
        SaveDirty.Remove(key);
        return true;
    }

    /// <summary>
    /// Registers a stored section KEY from the persistent cache - no data attached.
    /// The quadtree skeleton (HasDataSet/TopLevelKeys) and pending-mip flags come
    /// from keys alone; section data demand-loads when first needed, so join time
    /// and RAM stay independent of how much was ever explored.
    /// </summary>
    public void InstallStoredKey(int level, int sx, int sz, bool applyToParent)
    {
        long key = SectionKey(level, sx, sz);
        RegisterAvailableData(key);
        RegisterInTree(key);
        if (applyToParent && level < MaxLevel) MipDirty.Add(key);
    }

    // ---- Mip propagation (child → parent), worker-built and revision-validated ----

    /// <summary>
    /// Freeze a bounded set of pending children for the mip worker. Dirty state is not
    /// cleared here: it is the durable rerun obligation until a matching result commits.
    /// </summary>
    public List<MipJob> CreatePropagationJobs(long epoch, int maxSections)
    {
        var jobs = new List<MipJob>(maxSections);
        if (MipDirty.Count == 0 || maxSections <= 0) return jobs;

        var batch = new List<long>(maxSections);
        foreach (long key in MipDirty)
        {
            if (mipInFlight.ContainsKey(key)) continue;
            batch.Add(key);
            if (batch.Count >= maxSections) break;
        }

        foreach (long childKey in batch)
        {
            // Both sides must be in RAM before the flag may be cleared. Clearing it
            // while a section is still on disk would drop the propagation on the
            // floor, so a section awaiting a reload simply stays pending and is
            // retried on a later tick.
            long parentKey = ParentKey(childKey);
            if (!EnsureResident(childKey)) continue;
            if (!EnsureResident(parentKey)) continue;

            if (!Sections.TryGetValue(childKey, out LodSection? child) || child.CapturedColumns == 0)
            {
                MipDirty.Remove(childKey);
                MarkSaveDirty(childKey); // persist the cleared ApplyToParent flag
                continue;
            }

            long revision = ContentRevision(childKey);
            mipInFlight[childKey] = revision;
            mipParentPins[parentKey] = mipParentPins.GetValueOrDefault(parentKey) + 1;
            jobs.Add(LodMip.CreateJob(epoch, childKey, revision, child,
                KeySx(childKey) & 1, KeySz(childKey) & 1));
        }

        return jobs;
    }

    /// <summary>
    /// Publish one worker result if the child still has exactly the revision that was
    /// snapshotted. A stale or failed result releases its slot but leaves MipDirty set,
    /// so the newest content is scheduled again rather than overwritten.
    /// </summary>
    public bool CompletePropagation(MipResult result)
    {
        if (!mipInFlight.TryGetValue(result.ChildKey, out long expectedRevision)
            || expectedRevision != result.ChildRevision) return false;

        mipInFlight.Remove(result.ChildKey);
        long parentKey = ParentKey(result.ChildKey);
        if (mipParentPins.TryGetValue(parentKey, out int pins))
        {
            if (pins <= 1) mipParentPins.Remove(parentKey);
            else mipParentPins[parentKey] = pins - 1;
        }

        if (result.Failed || ContentRevision(result.ChildKey) != result.ChildRevision)
        {
            return false;
        }

        MipDirty.Remove(result.ChildKey);
        MarkSaveDirty(result.ChildKey); // persist the cleared ApplyToParent flag

        LodSection parent = GetOrCreateSection(parentKey);
        int parentEdges = LodMip.ApplyToParent(result, parent);
        if (parentEdges != LodSection.EdgeNone) MarkChanged(parentKey, parentEdges);
        return true;
    }

    /// <summary>
    /// True when the section is in RAM, or when there is nothing to load for it so the
    /// caller may proceed. False means a background reload was started and the caller
    /// must leave its pending work alone and retry later.
    ///
    /// This is how mip propagation avoids blocking the frame on a decompress without
    /// ever creating an empty section that would shadow -- and then overwrite -- a
    /// stored row. It is TryGetForRender's policy with one difference: a key with
    /// nothing to load is "proceed" here, rather than "no mesh".
    /// </summary>
    public bool EnsureResident(long key) =>
        TryGetForRender(key, out _) || !LoadsInFlight.Contains(key);

    public string DescribeLevels()
    {
        var counts = new int[MaxLevel + 1];
        foreach (long key in Sections.Keys) counts[KeyLevel(key)]++;
        return string.Join(" ", counts.Select((c, i) => $"L{i}:{c}"));
    }

    public void Clear()
    {
        Sections.Clear();
        residentEvictionOrder.Clear();
        residentEvictionQueued.Clear();
        RenderDirty.Clear();
        SaveDirty.Clear();
        MipDirty.Clear();
        mipInFlight.Clear();
        mipParentPins.Clear();
        contentRevisions.Clear();
        persistenceRevisions.Clear();
        queuedSaveRevisions.Clear();
        HasDataSet.Clear();
        AvailableDataSet.Clear();
        AvailableDataRevision++;
        TopLevelKeys.Clear();
        LoadsInFlight.Clear();
        LoadFailed.Clear();
    }
}
