using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace VintageHorizons;

/// <summary>
/// Colour and tint slot for a captured block. The only part of capture that is not
/// side-agnostic: block colour comes from <c>capi.BlockTextureAtlas</c>, which a
/// dedicated server does not have. See DESIGN.md §10.4.
/// </summary>
/// <param name="blockId">Live block id from the capture.</param>
/// <param name="cx">Chunk column X, for sampling position.</param>
/// <param name="cz">Chunk column Z, for sampling position.</param>
/// <param name="sampleY">Y of the run's top, for sampling position.</param>
public delegate (int Color, byte TintSlot) LodPaletteDescriber(int blockId, int blockX, int blockY, int blockZ);

/// <summary>Which live tint applies to a block. The server has none and answers 0.</summary>
public delegate byte LodTintSlotResolver(Block block);

public enum LodForeignQueueOutcome
{
    Queued,
    Retryable,
    Unavailable,
}

public readonly record struct LodForeignInstallCompletion(
    long Key, LodForeignSource Source, bool Installed);

/// <summary>
/// Everything between "a chunk column arrived" and "a section is on disk": capture
/// scheduling, palette registration, mip propagation and persistence. Owns all mutation
/// of the <see cref="LodWorld"/>; the worker thread only reads immutable snapshots.
///
/// Side-agnostic on purpose. The client drives it from `ChunkDirty` and also renders from
/// the same LodWorld; a server drives it from `ChunkColumnLoaded` and never renders. What
/// differs between them is which chunks arrive and what a palette entry's colour is, so
/// those are the two things injected rather than branched on - a copy of this per side
/// would drift, and the mip and persistence rules are exactly what must not.
///
/// Every method here must be called from the thread that owns the world (the game tick).
/// </summary>
public class LodPipeline
{
    const int CaptureSchedulesPerTick = 8;
    const int CaptureAppliesPerTick = 8;
    const int PropagationsPerTick = 3;
    const int MaxMipBacklog = 12;
    const int SectionSavesPerTick = 6;
    const int MaxWorkerCaptureBacklog = 24;
    const int ChunkSize = GlobalConstants.ChunkSize;

    /// <summary>
    /// Queued snapshots hold copies of their section's run data, so an unbounded queue
    /// is an unbounded memory leak if the disk can't keep up. Past this depth the
    /// sections simply stay dirty (and therefore RAM-resident) and retry later.
    /// </summary>
    const int MaxStorageBacklog = 256;

    readonly ICoreAPI api;
    readonly ILogger logger;
    readonly LodPaletteDescriber describePalette;

    /// <summary>Tint slot for a block; the server has no tints and leaves it 0.</summary>
    readonly LodTintSlotResolver tintSlotFor;

    public LodWorld World { get; }
    public LodWorker Worker { get; }

    LodStore? store;
    LodStorageThread? storageThread;

    /// <summary>Block codes the registry never answered for; empty is the norm.</summary>
    public string[] UnresolvedBlockCodes() => store?.UnresolvedCodes() ?? Array.Empty<string>();

    /// <summary>
    /// Colours palette entries that were saved without one. Set by the client, which has
    /// the texture atlas; a server leaves it null, because storing 0 is what a server is
    /// supposed to do. See LodPaletteRepair for why an existing cache needs this.
    /// </summary>
    public System.Func<LodSection, int>? RepairUncoloredPalette;

    /// <summary>
    /// Gives server-authored palette entries client atlas colours after their structural
    /// decode and live block resolution. Client-only; a dedicated server leaves it null.
    /// </summary>
    public Action<LodSection>? RecolorForeignSection;

    /// <summary>Palette entries given a colour on load because the cache had none.</summary>
    public int PaletteEntriesRepaired { get; private set; }

    readonly ConcurrentDictionary<long, byte> queuedColumns = new();
    readonly ConcurrentQueue<long> pendingColumns = new();
    readonly BlockPos paletteSamplePos = new(0, 0, 0);

    /// <summary>
    /// False until the store exists. Nothing may touch sections before then: applying a
    /// capture to a freshly-created empty section would shadow (and later overwrite) the
    /// stored row. The column queue holds work until it flips.
    /// </summary>
    public bool Active { get; private set; }

    public bool Persisting => store != null;
    public int CachedSectionsLoaded { get; private set; }
    public int ColumnsCaptured { get; private set; }
    public int PendingColumns => pendingColumns.Count;
    public string? DbPath { get; private set; }

    // Main-thread storage cost, measured to decide whether moving SQLite work to a
    // background thread is worth its complexity.
    readonly System.Diagnostics.Stopwatch storageClock = new();
    public double SaveMsMax { get; private set; }
    public double SaveMsTotal { get; private set; }
    public int SaveCalls { get; private set; }
    public double LoadMsMax { get; private set; }
    public double LoadMsTotal { get; private set; }
    public int LoadCalls { get; private set; }
    public LodStorageThread? StorageThread => storageThread;

    /// <summary>Owning-thread pipeline phases since the last telemetry report.</summary>
    public LodPhaseCost LoadInstallCost, ForeignInstallCost, CaptureScheduleCost, CaptureApplyCost,
        MipApplyCost, MipScheduleCost, SaveSnapshotCost;
    public bool TrackPhaseAllocations { get; set; }

    /// <summary>Completed background-load publications since the last telemetry reset.</summary>
    public int LoadInstallItems { get; private set; }
    public long LoadInstallBytes { get; private set; }
    public int PendingLoadResults => storageThread?.PendingLoadResults ?? 0;
    public long PendingLoadResultBytes => storageThread?.PendingLoadResultBytes ?? 0;
    public long OldestLoadResultAgeMs => storageThread?.OldestLoadResultAgeMs ?? 0;
    public int ForeignInstallItems { get; private set; }
    public long ForeignInstallBytes { get; private set; }
    public int PendingForeignResults => storageThread?.PendingForeignResults ?? 0;
    public long PendingForeignResultBytes => storageThread?.PendingForeignResultBytes ?? 0;
    public long OldestForeignResultAgeMs => storageThread?.OldestForeignResultAgeMs ?? 0;
    public int CaptureApplyItems { get; private set; }
    public long CaptureApplyBytes { get; private set; }
    public int PendingCaptureResults => Worker.PendingCaptureResults + deferredCaptures.Count;
    public long PendingCaptureResultBytes => Worker.PendingCaptureResultBytes
        + deferredCaptures.Sum(result => result.EstimatedBytes);
    public long OldestCaptureResultAgeMs => Math.Max(Worker.OldestCaptureResultAgeMs,
        deferredCaptures.Count > 0
            ? Math.Max(0, Environment.TickCount64 - deferredCaptures[0].ReadyAtMilliseconds)
            : 0);

    readonly Queue<LodForeignInstallCompletion> foreignInstallCompletions = new();

    int tickCounter;
    long worldEpoch;

    public LodPipeline(ICoreAPI api, ILogger logger, LodPaletteDescriber describePalette,
        LodTintSlotResolver? tintSlotFor = null)
    {
        this.api = api;
        this.logger = logger;
        this.describePalette = describePalette;
        this.tintSlotFor = tintSlotFor ?? (_ => 0);
        World = new LodWorld();
        Worker = new LodWorker();
        Remote = new LodRemoteKeySet(World);
    }

    public void ResetStorageStats()
    {
        SaveMsMax = SaveMsTotal = LoadMsMax = LoadMsTotal = 0;
        SaveCalls = LoadCalls = 0;
    }

    public void ResetPhaseCosts()
    {
        LoadInstallCost.Reset();
        ForeignInstallCost.Reset();
        CaptureScheduleCost.Reset();
        CaptureApplyCost.Reset();
        MipApplyCost.Reset();
        MipScheduleCost.Reset();
        SaveSnapshotCost.Reset();
        LoadInstallItems = 0;
        LoadInstallBytes = 0;
        ForeignInstallItems = 0;
        ForeignInstallBytes = 0;
        CaptureApplyItems = 0;
        CaptureApplyBytes = 0;
    }

    /// <summary>Note a chunk column as needing (re)capture. Safe from any thread.</summary>
    public void QueueColumn(int cx, int cz)
    {
        long key = ((long)cz << 32) | (uint)cx;
        if (queuedColumns.TryAdd(key, 0)) pendingColumns.Enqueue(key);
    }

    /// <summary>
    /// Capture a chunk column the caller already holds, rather than one the world can
    /// be asked for. <see cref="QueueColumn"/> cannot serve a peeked column: a peek
    /// puts nothing in the loaded chunk list, so the BlockAccessor lookup in
    /// ScheduleCaptures finds nothing - or, for a coordinate that also exists on disk,
    /// finds the savegame's version instead.
    ///
    /// Safe from any thread. The chunk array is copied and the rain map is cloned, so
    /// the caller can drop its references as soon as this returns. Deliberately not
    /// routed through the queued-column dedup dictionaries: that keeps this lock-free,
    /// and a duplicate capture of identical data is idempotent at apply time.
    /// </summary>
    /// <returns>False when the pipeline is closed or the inputs cannot describe a column.</returns>
    public bool CaptureColumn(int cx, int cz, IWorldChunk?[] chunks, ushort[] rainMap)
    {
        if (!Active) return false;
        if (chunks.Length == 0 || rainMap.Length < ChunkSize * ChunkSize) return false;

        var refs = new IWorldChunk?[chunks.Length];
        Array.Copy(chunks, refs, chunks.Length);

        Worker.EnqueueCapture(new CaptureJob
        {
            Epoch = worldEpoch,
            Cx = cx,
            Cz = cz,
            Chunks = refs,
            RainMap = (ushort[])rainMap.Clone(),
        });
        return true;
    }

    /// <summary>
    /// Combined capture jobs and completed/deferred results retained by the pipeline.
    /// A producer that can throttle itself (chunk generation) must stop at the source:
    /// jobs hold unpacked chunk columns, while results retain their converted run arrays.
    /// </summary>
    public int CaptureBacklog => Worker.PendingCaptures + PendingCaptureResults;
    public bool CaptureBacklogFull => CaptureBacklog >= MaxWorkerCaptureBacklog;

    /// <summary>
    /// Open (or create) the LOD cache for the current world and adopt its key set.
    /// Failing to open is not fatal: capture and rendering work without persistence.
    /// </summary>
    /// <param name="subdir">ModData-relative directory for the cache file.</param>
    /// <param name="suffix">
    /// Appended to the world key. Belt and braces after a real bug: client and server
    /// resolve the same ModData path from the same savegame identifier, so in one process
    /// they opened one file through two connections. Naming them apart means that class of
    /// mistake cannot silently corrupt a cache even if the two ever coexist again.
    /// </param>
    public void Open(string subdir, string suffix = "")
    {
        worldEpoch++;
        string worldKey = api.World.SavegameIdentifier;
        if (string.IsNullOrEmpty(worldKey)) worldKey = "seed-" + api.World.Seed;
        worldKey = Regex.Replace(worldKey, "[^A-Za-z0-9_-]", "_");

        string dir = api.GetOrCreateDataPath(subdir);
        string dbPath = Path.Combine(dir, worldKey + suffix + ".db");

        var newStore = new LodStore(logger);
        if (!newStore.Open(dbPath))
        {
            newStore.Dispose();
            Active = true; // no persistence this session; everything else still works
            return;
        }

        store = newStore;
        DbPath = dbPath;
        newStore.ClassifyBlock = blockId =>
        {
            Block? block = blockId > 0 ? api.World.GetBlock(blockId) : null;
            return block == null ? ((byte)0, (byte)0) : (LodBlockPolicy.FlagsFor(block), tintSlotFor(block));
        };
        storageThread = new LodStorageThread(newStore);

        // Background reloads for the render path. The loader runs on the storage
        // thread; results are installed on the world thread in Tick.
        storageThread.SetLoader(key => newStore.LoadSection(
            LodWorld.KeyLevel(key), LodWorld.KeySx(key), LodWorld.KeySz(key), api.World, resolveBlockIds: false));
        // Routing, not just loading: a key the server offered and local disk has never
        // held would come back empty from the store and land in LoadFailed, which is
        // permanent. Those go to the network instead, and the quadtree's own
        // LoadsInFlight bookkeeping covers both paths unchanged.
        World.RequestAsyncLoad = key =>
        {
            if (!Remote.WantFromRemote(key)) storageThread?.RequestLoad(key);
        };

        World.LoadFromStore = key =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            LodSection? loaded = store?.LoadSection(
                LodWorld.KeyLevel(key), LodWorld.KeySx(key), LodWorld.KeySz(key), api.World);
            double ms = clock.Elapsed.TotalMilliseconds;
            LoadCalls++;
            LoadMsTotal += ms;
            if (ms > LoadMsMax) LoadMsMax = ms;
            return loaded;
        };
        CachedSectionsLoaded = store.LoadAllKeys((level, sx, sz, applyToParent) =>
        {
            Remote.AddLocalKey(LodWorld.SectionKey(level, sx, sz));
            World.InstallStoredKey(level, sx, sz, applyToParent);
        });
        Active = true;
        logger.Notification("LOD cache: {0}", dbPath);
    }

    /// <summary>
    /// The stored blob for a key, unparsed, for serving over the network. Null when the
    /// key is not on disk - including when it is resident in RAM but not yet flushed,
    /// which is why the caller treats a miss as "ask again later" rather than "gone".
    /// </summary>
    public byte[]? LoadBlob(long key) => store?.LoadBlob(
        LodWorld.KeyLevel(key), LodWorld.KeySx(key), LodWorld.KeySz(key));

    /// <summary>
    /// Transfer one foreign compressed blob to the storage-owned decoder. The worker
    /// retains block codes and stored flags only; registry resolution and publication
    /// happen later on this pipeline's owning thread.
    /// </summary>
    public LodForeignQueueOutcome QueueForeignBlob(
        long key, byte[] blob, LodForeignSource source)
    {
        if (store == null || storageThread == null || blob.Length == 0)
            return LodForeignQueueOutcome.Unavailable;
        if (World.Sections.ContainsKey(key)) return LodForeignQueueOutcome.Unavailable;
        return storageThread.TryEnqueueForeign(worldEpoch, key, source, blob)
            ? LodForeignQueueOutcome.Queued
            : LodForeignQueueOutcome.Retryable;
    }

    public bool CanQueueForeignBlob(LodForeignSource source) =>
        storageThread?.CanEnqueueForeign(source) == true;

    public bool TryTakeForeignInstallCompletion(out LodForeignInstallCompletion completion) =>
        foreignInstallCompletions.TryDequeue(out completion);

    public int ForeignSectionsInstalled { get; private set; }

    /// <summary>
    /// Which keys a remote source can supply and which the view is waiting on. Its own
    /// class so the set logic can be tested without a game API - see LodRemoteKeySet.
    /// Private, and reached only through the delegating members below: the pipeline is
    /// the facade the mod system talks to, and two doors to the same state is how they
    /// drift apart.
    /// </summary>
    readonly LodRemoteKeySet Remote;

    /// <inheritdoc cref="LodRemoteKeySet.RemoteOnly"/>
    public HashSet<long> RemoteOnly => Remote.RemoteOnly;

    /// <inheritdoc cref="LodRemoteKeySet.MarkUnavailable"/>
    public void MarkRemoteUnavailable(long key) => Remote.MarkUnavailable(key);

    /// <inheritdoc cref="LodRemoteKeySet.AddRemoteKeys"/>
    public int AddRemoteKeys(IEnumerable<long> keys) => Remote.AddRemoteKeys(keys);

    /// <inheritdoc cref="LodRemoteKeySet.Wanted"/>
    public long[] RemoteWanted() => Remote.Wanted();

    /// <inheritdoc cref="LodRemoteKeySet.MarkRequested"/>
    public void MarkRemoteRequested(IEnumerable<long> sent) => Remote.MarkRequested(sent);

    /// <inheritdoc cref="LodRemoteKeySet.CompleteLocalOffer"/>
    public void CompleteLocalOffer(long key, LodLocalOfferOutcome outcome) =>
        Remote.CompleteLocalOffer(key, outcome);

    /// <inheritdoc cref="LodRemoteKeySet.MarkLocalOfferAccepted"/>
    public void MarkLocalOfferAccepted(long key) => Remote.MarkLocalOfferAccepted(key);

    /// <inheritdoc cref="LodRemoteKeySet.MarkRetryable"/>
    public void MarkRemoteRetryable(long key) => Remote.MarkRetryable(key);

    /// <summary>One step of the whole pipeline. Call once per game tick.</summary>
    public void Tick()
    {
        if (!Active) return;

        LodPhaseStart phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        InstallForeignSections();
        ForeignInstallCost.Add(phaseStart);

        phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        InstallLoadedSections();
        LoadInstallCost.Add(phaseStart);

        phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        ScheduleCaptures();
        CaptureScheduleCost.Add(phaseStart);

        phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        ApplyCaptureResults();
        CaptureApplyCost.Add(phaseStart);

        phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        ApplyMipResults();
        MipApplyCost.Add(phaseStart);

        phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        ScheduleMipJobs();
        MipScheduleCost.Add(phaseStart);

        phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        SaveSomeDirtySections(SectionSavesPerTick);
        SaveSnapshotCost.Add(phaseStart);
        tickCounter++;
    }

    /// <summary>
    /// Finish decoded foreign sections on the owning thread. Palette lookup, live policy
    /// classification, atlas recolouring, skip filtering, and publication all remain
    /// here and share the same elapsed-time/byte policy as other section installs.
    /// </summary>
    void InstallForeignSections()
    {
        if (storageThread == null || store == null) return;

        var budget = new LodDrainBudget();
        while (storageThread.TryPeekForeignResult(out LodForeignDecodeResult waiting)
            && budget.TryStart(waiting.EstimatedBytes))
        {
            if (!storageThread.TryTakeForeignResult(out LodForeignDecodeResult result)) break;
            if (result.Epoch != worldEpoch) continue;

            LodSection? section = result.Section;
            bool installed = section != null && !World.Sections.ContainsKey(result.Key);
            if (installed)
            {
                store.ResolvePendingPalette(section!, api.World);
                RecolorForeignSection?.Invoke(section!);
                section!.RemoveRunsWithFlag(LodPaletteEntry.FlagSkip);

                // No worker result may overwrite data that became authoritative after
                // submission. This second check documents the commit rule beside the
                // actual mutation, even though no callback above is expected to capture.
                installed = !World.Sections.ContainsKey(result.Key);
            }

            if (installed)
            {
                World.InstallLoaded(result.Key, section);
                // Persist it: re-fetching a mean 45.9 KB a section every session is not an
                // option, so a foreign section becomes local cache data after adoption.
                World.MarkChanged(result.Key);
                Remote.MarkInstalled(result.Key);
                ForeignSectionsInstalled++;
            }
            else
            {
                // Corruption is terminal for this offered copy. If capture won the race,
                // MarkUnavailable sees the resident section and clears only remote state,
                // without poisoning its future local reload.
                Remote.MarkUnavailable(result.Key);
            }

            foreignInstallCompletions.Enqueue(new LodForeignInstallCompletion(
                result.Key, result.Source, installed));
        }

        ForeignInstallItems += budget.Items;
        ForeignInstallBytes += budget.Bytes;
    }

    void ApplyMipResults()
    {
        for (int n = 0; n < PropagationsPerTick && Worker.MipResults.TryDequeue(out MipResult? result); n++)
        {
            if (result.Epoch == worldEpoch) World.CompletePropagation(result);
        }
    }

    void ScheduleMipJobs()
    {
        int capacity = MaxMipBacklog - World.MipInFlightCount;
        if (capacity <= 0) return;

        foreach (MipJob job in World.CreatePropagationJobs(
            worldEpoch, Math.Min(PropagationsPerTick, capacity)))
        {
            Worker.EnqueueMip(job);
        }
    }

    /// <summary>
    /// Drop cold sections from RAM around an anchor. Only meaningful once reload-from-disk
    /// exists, and only every ~5s: the sweep walks every resident section.
    /// </summary>
    public bool MaybeEvictAround(double x, double z)
    {
        if (tickCounter % 100 != 0 || World.LoadFromStore == null) return false;
        World.EvictColdSections(x, z, 50);
        return tickCounter % 1200 == 0;
    }

    /// <summary>
    /// Adopt sections the storage thread finished reading. Decompression already happened
    /// off-thread, but palette resolution and policy repair still touch the live registry
    /// here, so publication is bounded by both elapsed time and estimated section bytes.
    /// </summary>
    void InstallLoadedSections()
    {
        if (storageThread == null) return;

        var budget = new LodDrainBudget();
        while (storageThread.TryPeekLoadResult(out LodLoadResult waiting)
            && budget.TryStart(waiting.EstimatedBytes))
        {
            // Single owning-thread consumer: after the successful peek this can fail only
            // during teardown, in which case no live world remains to publish into.
            if (!storageThread.TryTakeLoadResult(out LodLoadResult result)) break;

            int repaired = 0;
            // Palette ids are resolved here, on the world thread, before anything can
            // read them: the storage thread must not touch the block registry.
            if (result.Section != null && store != null)
            {
                store.ResolvePendingPalette(result.Section, api.World);
                // Reclassify has just refreshed flags from the live blocks, so this drops
                // runs for anything that is no longer terrain (fire, meta) from sections
                // captured under an older policy, without needing a re-explore.
                result.Section.RemoveRunsWithFlag(LodPaletteEntry.FlagSkip);
                repaired = RepairUncoloredPalette?.Invoke(result.Section) ?? 0;
            }
            World.InstallLoaded(result.Key, result.Section);

            // Written back, so the repair is done once rather than on every load for the
            // rest of the world's life. This is the only reason a read marks a section
            // dirty, and it stops as soon as the cache is clean.
            if (repaired > 0)
            {
                PaletteEntriesRepaired += repaired;
                World.MarkChanged(result.Key);
            }
        }

        LoadInstallItems += budget.Items;
        LoadInstallBytes += budget.Bytes;
    }

    // ---- Capture scheduling (world thread gathers refs, worker reads blocks) ----

    void ScheduleCaptures()
    {
        int capacity = MaxWorkerCaptureBacklog - CaptureBacklog;
        if (capacity <= 0) return;

        int chunkYCount = api.World.BlockAccessor.MapSizeY / ChunkSize;

        int scheduleBudget = Math.Min(CaptureSchedulesPerTick, capacity);
        for (int n = 0; n < scheduleBudget && pendingColumns.TryDequeue(out long key); n++)
        {
            queuedColumns.TryRemove(key, out _);
            int cx = (int)(key & 0xFFFFFFFF);
            int cz = (int)(key >> 32);

            IMapChunk? mapChunk = api.World.BlockAccessor.GetMapChunk(cx, cz);
            ushort[]? rainMap = mapChunk?.RainHeightMap;
            if (rainMap == null) continue;

            var chunks = new IWorldChunk?[chunkYCount];
            for (int cy = 0; cy < chunkYCount; cy++)
            {
                chunks[cy] = api.World.BlockAccessor.GetChunk(cx, cy, cz);
            }

            Worker.EnqueueCapture(new CaptureJob
            {
                Epoch = worldEpoch,
                Cx = cx,
                Cz = cz,
                Chunks = chunks,
                RainMap = (ushort[])rainMap.Clone(),
            });
        }
    }

    // ---- Applying capture results: block ids → section palette ids ----

    /// <summary>
    /// Capture results whose section was evicted and is being reloaded. Bounded, and the
    /// bound is not decoration: past it the tick takes the blocking load rather than let
    /// this grow without limit, because throwing a result away loses captured terrain.
    /// </summary>
    readonly List<CaptureResult> deferredCaptures = new();

    const int MaxDeferredCaptures = 64;

    void ApplyCaptureResults()
    {
        int remaining = CaptureAppliesPerTick;
        var drain = new LodDrainBudget();

        // Results waiting on a reload get first refusal, so a section that has come back
        // is merged before anything newer touches it.
        for (int i = 0; i < deferredCaptures.Count && remaining > 0;)
        {
            if (!World.EnsureResident(deferredCaptures[i].SectionKey)) { i++; continue; }
            if (!drain.TryStart(deferredCaptures[i].EstimatedBytes)) break;
            ApplyOneCaptureResult(deferredCaptures[i]);
            deferredCaptures.RemoveAt(i);
            remaining--;
        }

        while (remaining > 0 && Worker.TryPeekCaptureResult(out CaptureResult result))
        {
            if (result.Epoch != worldEpoch)
            {
                if (!Worker.TryTakeCaptureResult(out _)) break;
                remaining--;
                continue;
            }

            // An evicted section has to come back from disk before capture may merge into
            // it, or the merge writes into an empty section that then overwrites the
            // stored row. That was solved for mip propagation and not here, so this path
            // still paid a synchronous SQLite read and a Deflate on the game tick:
            // measured at 10.60ms average and 112.98ms worst, in a 50ms tick.
            //
            // EnsureResident starts the background reload and says "not yet". The result
            // waits a tick or two, which is invisible, instead of the whole world waiting
            // on a decompress.
            if (!World.EnsureResident(result.SectionKey))
            {
                // Room is made by forcing the OLDEST result through, blocking load and
                // all, never by letting this one past. A newer result that overtook an
                // older one for the same section would leave the older one landing last,
                // writing columns that have already been superseded. The list is a queue
                // for that reason, and the bound is enforced from its head.
                if (deferredCaptures.Count >= MaxDeferredCaptures)
                {
                    if (!drain.TryStart(deferredCaptures[0].EstimatedBytes)) break;
                    if (!Worker.TryTakeCaptureResult(out result)) break;
                    ApplyOneCaptureResult(deferredCaptures[0]);
                    deferredCaptures.RemoveAt(0);
                }
                else if (!Worker.TryTakeCaptureResult(out result)) break;
                deferredCaptures.Add(result);
                remaining--;
                continue;
            }

            if (!drain.TryStart(result.EstimatedBytes)) break;
            if (!Worker.TryTakeCaptureResult(out result)) break;
            ApplyOneCaptureResult(result);
            remaining--;
        }

        CaptureApplyItems += drain.Items;
        CaptureApplyBytes += drain.Bytes;
    }

    void ApplyOneCaptureResult(CaptureResult result)
    {
        LodSection section = World.GetOrCreateSection(result.SectionKey);

        var pidByBlockId = new Dictionary<int, int>();
        ulong[]?[] batch = result.RunsByColumn;

        for (int col = 0; col < batch.Length; col++)
        {
            ulong[]? runs = batch[col];
            if (runs == null) continue;

            int kept = 0;
            for (int i = 0; i < runs.Length; i++)
            {
                int blockId = LodSection.RunPaletteId(runs[i]); // raw block id from capture
                if (!pidByBlockId.TryGetValue(blockId, out int pid))
                {
                    // One palette entry per block id per section, coloured from the first
                    // run seen. For chiselled blocks that means one chisel's material mix
                    // stands in for the whole section - coarse, but theirs, where the
                    // centre probe answered with the placeholder texture for all of them.
                    pid = RegisterPaletteEntry(section, result.SectionKey, blockId, col, runs[i]);
                    pidByBlockId[blockId] = pid;
                }

                // Decorative ground cover never becomes terrain: a flower would
                // otherwise be a solid, pale-grey 1-block cube.
                if ((section.Palette[pid].Flags & LodPaletteEntry.FlagSkip) != 0) continue;

                runs[kept++] = LodSection.PackRun(pid, LodSection.RunYTop(runs[i]), LodSection.RunYBottom(runs[i]));
            }

            if (kept != runs.Length) batch[col] = runs[..kept];
        }

        ColumnsCaptured++;
        if (section.ReplaceColumns(batch))
        {
            World.MarkChanged(result.SectionKey);
        }
    }

    /// <summary>
    /// World position of the top block of a run. The describer must get the block's own
    /// position, not a stand-in: chiselled blocks answer GetColorWithoutTint from the
    /// block entity at that exact position, and a probe at the chunk-column centre made
    /// every chisel average unknown.png instead - a real cache held that near-white
    /// (0x00FCFCFC) 8319 times. Capture results are always level 0, where a column is
    /// one block wide, and yTop is exclusive (a run spans [yBottom, yTop)).
    /// </summary>
    public static (int X, int Y, int Z) CaptureBlockPos(long sectionKey, int col, ulong run)
    {
        int localX = col % LodSection.GridSize;
        int localZ = col / LodSection.GridSize;
        return (LodWorld.KeySx(sectionKey) * LodSection.SectionBlocks + localX,
                LodSection.RunYTop(run) - 1,
                LodWorld.KeySz(sectionKey) * LodSection.SectionBlocks + localZ);
    }

    int RegisterPaletteEntry(LodSection section, long sectionKey, int blockId, int col, ulong run)
    {
        Block block = api.World.Blocks[blockId];
        (int x, int y, int z) = CaptureBlockPos(sectionKey, col, run);
        (int color, byte tintSlot) = describePalette(blockId, x, y, z);
        return section.FindOrAddPaletteEntry(blockId, color, LodBlockPolicy.FlagsFor(block), tintSlot);
    }

    // ---- Persistence ----

    void SaveSomeDirtySections(int budget)
    {
        if (store == null || World.SaveDirty.Count == 0) return;
        if (storageThread != null && storageThread.Backlog >= MaxStorageBacklog) return;

        storageClock.Restart();
        List<long>? saved = null;
        foreach (long key in World.SaveDirty)
        {
            if (World.Sections.TryGetValue(key, out LodSection? section))
            {
                // Freeze on this thread (the section keeps mutating), compress and
                // write on the storage thread.
                var snap = LodSaveSnapshot.Of(LodWorld.KeyLevel(key), LodWorld.KeySx(key), LodWorld.KeySz(key),
                    section, api.World, World.MipDirty.Contains(key));
                storageThread?.Enqueue(snap);
            }
            (saved ??= new List<long>()).Add(key);
            if (--budget <= 0) break;
        }
        if (saved != null) foreach (long key in saved) World.SaveDirty.Remove(key);

        double ms = storageClock.Elapsed.TotalMilliseconds;
        SaveCalls++;
        SaveMsTotal += ms;
        if (ms > SaveMsMax) SaveMsMax = ms;
    }

    /// <summary>
    /// Flush and shut the cache down. Order matters: queue everything, let the writer
    /// finish, stop the thread, and only then close the connection it was writing through.
    /// </summary>
    public void Close()
    {
        Active = false;
        worldEpoch++;
        World.LoadFromStore = null;
        World.RequestAsyncLoad = null;

        if (store != null)
        {
            SaveSomeDirtySections(int.MaxValue);
            if (storageThread != null)
            {
                storageThread.Drain();
                if (storageThread.Backlog > 0)
                {
                    logger.Warning("Storage drain timed out with {0} sections unwritten", storageThread.Backlog);
                }
                storageThread.Dispose();
                storageThread = null;
            }
            store.Close();
            store.Dispose();
            store = null;
        }

        queuedColumns.Clear();
        pendingColumns.Clear();
        Remote.Clear();
        foreignInstallCompletions.Clear();
        // Results for the world we are leaving must not be applied to the next one.
        // Both queues, or a result held back for a reload would cross worlds.
        Worker.ClearCaptureResults();
        Worker.ClearMipWork();
        deferredCaptures.Clear();
        World.Clear();
        CachedSectionsLoaded = 0;
        ColumnsCaptured = 0;
        DbPath = null;
    }

    public void Dispose()
    {
        storageThread?.Drain();
        storageThread?.Dispose();
        storageThread = null;
        store?.Dispose();
        store = null;
        Worker.Dispose();
    }
}
