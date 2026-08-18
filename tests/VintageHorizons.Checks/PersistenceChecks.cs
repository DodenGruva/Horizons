using System.Collections.Concurrent;

namespace VintageHorizons.Checks;

/// <summary>Durable-save identity, acknowledgement, coalescing, and retry invariants.</summary>
public static class PersistenceChecks
{
    public static void Run(Check c)
    {
        ExactAcknowledgements(c);
        PendingSnapshotsCoalesce(c);
        FailedWritesAcknowledgeAndRecover(c);
        FullWorkerBacklogDrains(c);
        NewestRevisionSurvivesRestart(c);
        RetryDelayIsBounded(c);
    }

    static void ExactAcknowledgements(Check c)
    {
        var world = new LodWorld();
        long key = LodWorld.SectionKey(0, 4, 5);
        world.Sections[key] = Fixtures.SolidSection();

        world.MarkChanged(key);
        c.Eq(1L, world.PersistenceRevision(key), "a content change starts persistence revision one");
        c.True(world.TryQueueSave(key, out long first), "the dirty revision can be reserved");

        world.MarkChanged(key);
        c.Eq(2L, world.PersistenceRevision(key), "a repeated mutation advances the row revision");
        c.False(world.CompleteSave(key, first, succeeded: true),
            "a stale success cannot acknowledge newer content");
        c.True(world.SaveDirty.Contains(key), "newer content stays dirty after the stale success");

        c.True(world.TryQueueSave(key, out long newest), "the newer revision can be queued");
        c.Eq(2L, newest, "the replacement snapshot names the newest revision");
        c.False(world.CompleteSave(key, newest, succeeded: false),
            "a failed write never clears dirty state");
        c.True(world.SaveDirty.Contains(key), "a failed revision remains a durable obligation");
        c.Eq(0L, world.QueuedSaveRevision(key), "failure releases the revision for retry");

        c.True(world.TryQueueSave(key, out long retry), "the same revision can be retried");
        c.True(world.CompleteSave(key, retry, succeeded: true),
            "an exact successful acknowledgement clears the obligation");
        c.False(world.SaveDirty.Contains(key), "only the exact newest success clears dirty state");

        long contentRevision = world.ContentRevision(key);
        world.MarkSaveDirty(key);
        c.Eq(contentRevision, world.ContentRevision(key),
            "a stored flag change does not pretend terrain content changed");
        c.Eq(3L, world.PersistenceRevision(key),
            "a stored flag change still advances the persistence revision");
    }

    static void PendingSnapshotsCoalesce(Check c)
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var written = new ConcurrentQueue<(long Key, long Revision)>();
        using var store = new LodStore(null!);
        using var worker = new LodStorageThread(store, snapshot =>
        {
            written.Enqueue((snapshot.Key, snapshot.Revision));
            if (snapshot.SX == 1)
            {
                entered.Set();
                release.Wait(5000);
            }
        });

        worker.Enqueue(Fixtures.Snapshot(Fixtures.SolidSection(), sx: 1, revision: 1));
        c.True(entered.Wait(5000), "the fixture holds one save in execution");
        worker.Enqueue(Fixtures.Snapshot(Fixtures.SolidSection(), sx: 2, revision: 1));
        worker.Enqueue(Fixtures.Snapshot(Fixtures.SolidSection(), sx: 2, revision: 2));
        c.Eq(2L, worker.Backlog,
            "one executing key plus one coalesced pending key consume two backlog slots");

        release.Set();
        c.True(worker.Drain(5000), "the coalesced queue drains");
        var rows = written.ToArray();
        c.Eq(2, rows.Length, "the superseded pending snapshot is never written");
        c.Eq(2L, rows.Single(row => LodWorld.KeySx(row.Key) == 2).Revision,
            "the pending key writes only its newest revision");

        var completions = new List<LodSaveCompletion>();
        while (worker.TryTakeSaveCompletion(out LodSaveCompletion completion)) completions.Add(completion);
        c.Eq(2, completions.Count, "every executed write emits one acknowledgement");
        c.False(completions.Any(completion => completion.Key == LodWorld.SectionKey(0, 2, 0)
            && completion.Revision == 1), "a coalesced revision emits no false acknowledgement");
    }

    static void FailedWritesAcknowledgeAndRecover(Check c)
    {
        int attempts = 0;
        using var store = new LodStore(null!);
        using var worker = new LodStorageThread(store, _ =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("injected save failure");
        });
        LodSaveSnapshot snapshot = Fixtures.Snapshot(Fixtures.SolidSection(), sx: 7, revision: 3);

        c.True(worker.Enqueue(snapshot), "the injected-failure snapshot is accepted");
        c.True(worker.Drain(5000), "a failed write still completes its worker responsibility");
        c.True(worker.TryTakeSaveCompletion(out LodSaveCompletion failed),
            "a failed write emits an acknowledgement");
        c.False(failed.Succeeded, "the acknowledgement identifies the injected failure");
        c.Eq(3L, failed.Revision, "the failure names the exact revision");

        c.True(worker.Enqueue(snapshot), "the same revision can be submitted again");
        c.True(worker.Drain(5000), "the storage worker survives the exception");
        c.True(worker.TryTakeSaveCompletion(out LodSaveCompletion recovered),
            "the retry emits its own acknowledgement");
        c.True(recovered.Succeeded, "the retry succeeds after the injected failure");
        c.Eq(1L, worker.SectionsWritten, "only the durable retry counts as written");
    }

    static void FullWorkerBacklogDrains(Check c)
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var store = new LodStore(null!);
        using var worker = new LodStorageThread(store, snapshot =>
        {
            if (snapshot.SX != 0) return;
            entered.Set();
            release.Wait(5000);
        });
        const int count = 300;
        worker.Enqueue(Fixtures.Snapshot(Fixtures.SolidSection(), sx: 0, revision: 1));
        if (!entered.Wait(5000)) throw new TimeoutException("storage backlog fixture did not start");
        for (int i = 1; i < count; i++)
            worker.Enqueue(Fixtures.Snapshot(Fixtures.SolidSection(), sx: i, revision: 1));

        c.Eq((long)count, worker.Backlog,
            "the fixture really exceeds the pipeline admission cap before shutdown draining");
        release.Set();
        c.True(worker.Drain(5000), "a backlog larger than the pipeline admission cap drains fully");
        int acknowledged = 0;
        while (worker.TryTakeSaveCompletion(out _)) acknowledged++;
        c.Eq(count, acknowledged, "shutdown-style draining acknowledges every distinct queued key");
    }

    static void NewestRevisionSurvivesRestart(Check c)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vh-save-revision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cache.db");
        try
        {
            using (var store = new LodStore(new CaptureLogger()))
            {
                c.True(store.Open(path), "the revision restart fixture opens");
                using var worker = new LodStorageThread(store);
                worker.Enqueue(Fixtures.Snapshot(Fixtures.SolidSection(yTop: 8),
                    sx: 11, sz: 12, revision: 1));
                worker.Enqueue(Fixtures.Snapshot(Fixtures.SolidSection(yTop: 20),
                    sx: 11, sz: 12, revision: 2));
                c.True(worker.Drain(5000), "both repeated mutations become durable before close");
            }

            using var reopened = new LodStore(new CaptureLogger());
            c.True(reopened.Open(path), "the cache reopens after the repeated mutation");
            LodSection? restored = reopened.LoadSection(0, 11, 12, null!, resolveBlockIds: false);
            c.True(restored != null, "the newest row remains readable after restart");
            if (restored != null)
                c.Eq(20, LodSection.RunYTop(restored.ColumnRuns(0)[0]),
                    "restart reads the newest repeated mutation, never its predecessor");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    static void RetryDelayIsBounded(Check c)
    {
        c.Eq(250, LodSaveRetry.DelayMilliseconds(1), "the first retry waits briefly");
        c.Eq(500, LodSaveRetry.DelayMilliseconds(2), "retry delay grows exponentially");
        c.Eq(30000, LodSaveRetry.DelayMilliseconds(30), "retry delay has a finite ceiling");
        c.Eq(30000, LodSaveRetry.DelayMilliseconds(int.MaxValue),
            "extreme failure counts cannot overflow the retry delay");
    }
}
