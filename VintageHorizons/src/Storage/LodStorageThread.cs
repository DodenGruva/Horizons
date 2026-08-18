using System.Collections.Concurrent;

namespace VintageHorizons;

/// <summary>A completed background read waiting for owning-thread publication.</summary>
public readonly record struct LodLoadResult(
    long Key, LodSection? Section, long EstimatedBytes, long ReadyAtMilliseconds);

/// <summary>An exact background-save acknowledgement awaiting owning-thread publication.</summary>
public readonly record struct LodSaveCompletion(
    long Key, long Revision, bool Succeeded, string? Error);

/// <summary>Which bounded producer submitted a foreign blob for structural decode.</summary>
public enum LodForeignSource
{
    ServerAssist,
    LocalOffer,
}

/// <summary>A foreign blob decoded without touching the live block registry.</summary>
public readonly record struct LodForeignDecodeResult(
    long Epoch, long Key, LodForeignSource Source, LodSection? Section,
    long EstimatedBytes, long ReadyAtMilliseconds);

readonly record struct LodForeignDecodeJob(
    long Epoch, long Key, LodForeignSource Source, byte[] Blob);

/// <summary>
/// Serializes and writes LOD sections away from the render thread.
///
/// Measured before this existed: save batches cost ~10-22ms of main-thread time on
/// average and peaked near 49ms - an entire game tick - during exploration, which is
/// exactly when the player is moving and a stall is most visible. Deflate happens
/// here, outside the store's transaction lock, so a main-thread demand load waits at
/// most for a row write.
///
/// Ordering: a single consumer writes keys in FIFO order. A newer snapshot replaces an
/// older snapshot for the same key while it is still pending; if the older one is already
/// executing, at most one newer snapshot waits behind it. Every executed revision emits
/// an owning-thread acknowledgement.
/// </summary>
public class LodStorageThread : IDisposable
{
    readonly LodStore store;
    readonly Action<LodSaveSnapshot> saveAction;
    readonly object saveGate = new();
    readonly Queue<long> saveOrder = new();
    readonly Dictionary<long, LodSaveSnapshot> pendingSaves = new();
    readonly ConcurrentQueue<LodSaveCompletion> saveCompletions = new();
    readonly AutoResetEvent signal = new(false);
    readonly Thread thread;
    volatile bool running = true;

    // Demand reloads. Requests come from the render path, which can tolerate a
    // section arriving a few frames late; the capture path still loads inline
    // because it must merge into stored data before it may create anything.
    readonly ConcurrentQueue<long> loadRequests = new();
    readonly ConcurrentQueue<LodLoadResult> loadResults = new();
    long loadResultBytes;
    Func<long, LodSection?>? loadFunc;

    // Foreign blobs already live in bounded transport/read queues. Keep separate caps
    // after responsibility transfers here so local sibling-cache work can never consume
    // the slots reserved for the network client's protocol-bounded in-flight set.
    const int MaxServerForeignOutstanding = Net.LodAssist.MaxSectionsInFlight;
    const int MaxLocalForeignOutstanding = 8;
    readonly ConcurrentQueue<LodForeignDecodeJob> foreignRequests = new();
    readonly ConcurrentQueue<LodForeignDecodeResult> foreignResults = new();
    int serverForeignOutstanding;
    int localForeignOutstanding;
    long foreignResultBytes;

    /// <summary>Set by the coordinator: performs one blocking read, called on this thread.</summary>
    public void SetLoader(Func<long, LodSection?> loader) => loadFunc = loader;

    public void RequestLoad(long key)
    {
        loadRequests.Enqueue(key);
        signal.Set();
    }

    public bool TryPeekLoadResult(out LodLoadResult result) => loadResults.TryPeek(out result);

    public bool TryTakeLoadResult(out LodLoadResult result)
    {
        if (!loadResults.TryDequeue(out result)) return false;
        Interlocked.Add(ref loadResultBytes, -result.EstimatedBytes);
        return true;
    }

    public int PendingLoadResults => loadResults.Count;
    public long PendingLoadResultBytes => Math.Max(0, Interlocked.Read(ref loadResultBytes));
    public long OldestLoadResultAgeMs => loadResults.TryPeek(out LodLoadResult oldest)
        ? Math.Max(0, Environment.TickCount64 - oldest.ReadyAtMilliseconds)
        : 0;

    public bool CanEnqueueForeign(LodForeignSource source) =>
        Volatile.Read(ref ForeignOutstandingRef(source)) < ForeignLimit(source);

    public bool TryEnqueueForeign(
        long epoch, long key, LodForeignSource source, byte[] blob)
    {
        if (blob.Length == 0 || !TryReserveForeign(source)) return false;
        foreignRequests.Enqueue(new LodForeignDecodeJob(epoch, key, source, blob));
        signal.Set();
        return true;
    }

    public bool TryPeekForeignResult(out LodForeignDecodeResult result) =>
        foreignResults.TryPeek(out result);

    public bool TryTakeForeignResult(out LodForeignDecodeResult result)
    {
        if (!foreignResults.TryDequeue(out result)) return false;
        Interlocked.Add(ref foreignResultBytes, -result.EstimatedBytes);
        Interlocked.Decrement(ref ForeignOutstandingRef(result.Source));
        return true;
    }

    public int PendingForeignResults => foreignResults.Count;
    public long PendingForeignResultBytes => Math.Max(0, Interlocked.Read(ref foreignResultBytes));
    public long OldestForeignResultAgeMs => foreignResults.TryPeek(out LodForeignDecodeResult oldest)
        ? Math.Max(0, Environment.TickCount64 - oldest.ReadyAtMilliseconds)
        : 0;

    ref int ForeignOutstandingRef(LodForeignSource source)
    {
        if (source == LodForeignSource.ServerAssist) return ref serverForeignOutstanding;
        return ref localForeignOutstanding;
    }

    static int ForeignLimit(LodForeignSource source) =>
        source == LodForeignSource.ServerAssist
            ? MaxServerForeignOutstanding
            : MaxLocalForeignOutstanding;

    bool TryReserveForeign(LodForeignSource source)
    {
        ref int outstanding = ref ForeignOutstandingRef(source);
        int limit = ForeignLimit(source);
        while (true)
        {
            int current = Volatile.Read(ref outstanding);
            if (current >= limit) return false;
            if (Interlocked.CompareExchange(ref outstanding, current + 1, current) == current)
                return true;
        }
    }

    public int Pending
    {
        get { lock (saveGate) return pendingSaves.Count; }
    }
    public int SaveErrors;
    public string? FirstSaveError;
    public long SectionsWritten;

    // Includes pending and executing writes. A pending same-key replacement does not
    // increase it, which is the memory bound coalescing is meant to preserve.
    long saveOutstanding;
    readonly string? interruptMipMarker =
        Environment.GetEnvironmentVariable("VINTAGEHORIZONS_INTERRUPT_MIP_MARKER");
    readonly string? interruptMipRelease =
        Environment.GetEnvironmentVariable("VINTAGEHORIZONS_INTERRUPT_MIP_RELEASE");
    int interruptMipMarked;

    public LodStorageThread(LodStore store, Action<LodSaveSnapshot>? saveAction = null)
    {
        this.store = store;
        this.saveAction = saveAction ?? (snapshot => store.SaveBlob(
            snapshot.Level, snapshot.SX, snapshot.SZ, LodStore.Serialize(snapshot),
            snapshot.ApplyToParent));
        thread = new Thread(Loop)
        {
            Name = "vintagehorizons-storage",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    /// <summary>
    /// Queue a frozen row. Newer pending revisions for the same key supersede their
    /// snapshot in place, avoiding unbounded copies while preserving the latest state.
    /// </summary>
    public bool Enqueue(LodSaveSnapshot snapshot)
    {
        lock (saveGate)
        {
            if (!running) return false;
            long key = snapshot.Key;
            if (pendingSaves.TryGetValue(key, out LodSaveSnapshot? pending))
            {
                if (snapshot.Revision > pending.Revision) pendingSaves[key] = snapshot;
            }
            else
            {
                pendingSaves[key] = snapshot;
                saveOrder.Enqueue(key);
                Interlocked.Increment(ref saveOutstanding);
            }
        }
        signal.Set();
        return true;
    }

    public bool TryTakeSaveCompletion(out LodSaveCompletion completion) =>
        saveCompletions.TryDequeue(out completion);

    void Loop()
    {
        while (running)
        {
            bool didWork = false;

            // One foreign decode per pass keeps it responsive without letting a burst of
            // compressed network data indefinitely delay ordinary demand loads or saves.
            if (foreignRequests.TryDequeue(out LodForeignDecodeJob foreign))
            {
                didWork = true;
                DecodeForeign(foreign);
            }

            // Loads first: a pending read is blocking terrain from appearing, a
            // pending write is not blocking anything.
            while (loadRequests.TryDequeue(out long key))
            {
                didWork = true;
                ReadOne(key);
            }

            if (TryTakePendingSave(out LodSaveSnapshot snap))
            {
                didWork = true;
                WriteOne(snap);
            }

            if (!didWork) signal.WaitOne(200);
        }

        // Shutting down: never drop queued work, the rows are the player's cache.
        while (TryTakePendingSave(out LodSaveSnapshot snap)) WriteOne(snap);
    }

    bool TryTakePendingSave(out LodSaveSnapshot snapshot)
    {
        lock (saveGate)
        {
            while (saveOrder.Count > 0)
            {
                long key = saveOrder.Dequeue();
                if (!pendingSaves.Remove(key, out LodSaveSnapshot? pending) || pending == null) continue;
                snapshot = pending;
                return true;
            }
        }
        snapshot = null!;
        return false;
    }

    void ReadOne(long key)
    {
        LodSection? section = null;
        try
        {
            section = loadFunc?.Invoke(key);
        }
        catch (Exception e)
        {
            Interlocked.Increment(ref LoadErrors);
            try { FirstLoadError ??= e.ToString(); } catch { /* diagnostics must not kill the thread */ }
        }

        if (section != null) SectionsRead++;

        // Always answer, even on failure/miss: the requester clears its in-flight
        // marker from this queue and would otherwise never retry the key.
        long estimatedBytes = section?.EstimatedContentBytes ?? 0;
        Interlocked.Add(ref loadResultBytes, estimatedBytes);
        loadResults.Enqueue(new LodLoadResult(
            key, section, estimatedBytes, Environment.TickCount64));
    }

    void DecodeForeign(LodForeignDecodeJob job)
    {
        LodSection? section = null;
        try
        {
            // Null deliberately defers every registry lookup to the owning thread.
            section = store.DeserializeForeign(job.Blob, null);
        }
        catch (Exception e)
        {
            Interlocked.Increment(ref ForeignDecodeErrors);
            try { FirstForeignDecodeError ??= e.ToString(); } catch { /* diagnostics must not kill the thread */ }
        }

        long estimatedBytes = section?.EstimatedContentBytes ?? 0;
        Interlocked.Add(ref foreignResultBytes, estimatedBytes);
        foreignResults.Enqueue(new LodForeignDecodeResult(
            job.Epoch, job.Key, job.Source, section, estimatedBytes, Environment.TickCount64));
    }

    public int LoadErrors;
    public string? FirstLoadError;
    public long SectionsRead;
    public int ForeignDecodeErrors;
    public string? FirstForeignDecodeError;

    void WriteOne(LodSaveSnapshot snap)
    {
        bool succeeded = false;
        string? error = null;
        try
        {
            saveAction(snap);
            SectionsWritten++;
            succeeded = true;

            // Sandbox-only crash-recovery hook. The marker is published only after a row
            // carrying the durable propagation flag has been written. Pausing this one
            // writer prevents a later clearing snapshot from overtaking the runner before
            // it interrupts the exact pidfile-verified client. Ordinary processes have no
            // marker path and never enter this branch.
            if (snap.ApplyToParent && !string.IsNullOrEmpty(interruptMipMarker)
                && Interlocked.CompareExchange(ref interruptMipMarked, 1, 0) == 0)
            {
                File.WriteAllText(interruptMipMarker,
                    $"{snap.Level},{snap.SX},{snap.SZ},{DateTime.UtcNow:o}\n");
                while (running && !string.IsNullOrEmpty(interruptMipRelease)
                    && !File.Exists(interruptMipRelease))
                {
                    Thread.Sleep(25);
                }
            }
        }
        catch (Exception e)
        {
            error = e.ToString();
            Interlocked.Increment(ref SaveErrors);
            try { FirstSaveError ??= error; } catch { /* never let diagnostics kill the thread */ }
        }
        finally
        {
            saveCompletions.Enqueue(new LodSaveCompletion(
                snap.Key, snap.Revision, succeeded, error));
            Interlocked.Decrement(ref saveOutstanding);
        }
    }

    /// <summary>
    /// Block until every queued section has been written. Called before the store is
    /// closed on leave-world; without it a crash-free exit could still lose sections.
    /// </summary>
    public bool Drain(int timeoutMs = 15000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        signal.Set();
        while (Backlog > 0 && clock.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(10);
        }
        return Backlog == 0;
    }

    /// <summary>Sections enqueued but not yet written - surfaced in the stats line.</summary>
    public long Backlog => Math.Max(0, Interlocked.Read(ref saveOutstanding));

    public void Dispose()
    {
        running = false;
        signal.Set();
        thread.Join(15000);
        signal.Dispose();
    }
}
