using VintageHorizons.Net;

namespace VintageHorizons.Checks;

/// <summary>
/// The on-disk blob format, round-tripped with no database.
///
/// LodStore extends the game's SQLite base class, but Serialize is static and the base
/// constructor only stores its logger - so the format can be exercised without opening a
/// file, and DeserializeForeign explicitly accepts a null world to defer block-id lookup
/// to the main thread. That same door is what the network path uses for foreign sections.
/// </summary>
public static class StoreChecks
{
    public static void Run(Check c)
    {
        RoundTrip(c);
        DeferredPalette(c);
        AFailedLookupIsNotRemembered(c);
        AColourlessCacheIsRepaired(c);
        DisposingTheOfferReaderReleasesItsFileHandle(c);
        AssistBlobReaderIsBoundedOrderedAndReadOnly(c);
        BackgroundForeignDecodeIsBoundedAndIsolated(c);
        Rejection(c);
        PurgeKeepsMatchingData(c);
    }

    /// <summary>
    /// Reopening a cache at the same format version must keep every row.
    ///
    /// This is the one place the mod deletes data a player accumulated over weeks, so it
    /// gets a check that opens a real file rather than reasoning about the SQL. The
    /// nearby bug was the reverse of this: a brand-new cache announced that it was
    /// discarding data it never had, because a missing FormatVersion row compares
    /// unequal to the current version.
    /// </summary>
    static void PurgeKeepsMatchingData(Check c)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vh-purge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cache.db");
        try
        {
            var logger = new CaptureLogger();
            var store = new LodStore(logger);
            c.True(store.Open(path), "a new cache file opens");

            // A first-ever cache holds nothing, so it must not claim to discard anything.
            c.False(logger.Contains("discarding"),
                "a brand new cache does not announce that it is discarding data");

            store.SaveBlob(0, 1, 1, new byte[] { 1, 2, 3, 4 }, applyToParent: false);
            store.SaveBlob(0, 2, 2, new byte[] { 5, 6, 7, 8 }, applyToParent: true);
            store.Dispose();

            // Reopen at the SAME version. Every row survives.
            var reopenLogger = new CaptureLogger();
            var reopened = new LodStore(reopenLogger);
            c.True(reopened.Open(path), "an existing cache reopens");
            int kept = reopened.LoadAllKeys((_, _, _, _) => { });
            c.Eq(2, kept, "reopening at the same format version keeps every section");
            c.False(reopenLogger.Contains("discarding"),
                "reopening at the same version does not discard anything");
            reopened.Dispose();

            // Now make the stored version disagree. The purge must fire, and say how
            // much it took - silent destruction of a player's cache is the thing to
            // avoid, not the destruction itself.
            // Pooling is deliberately off. Dispose on a pooled connection only returns
            // its native handle to the process-wide pool; on Windows that keeps this
            // temporary file open and makes LodStore's writable reopen fail. The fixture
            // is testing schema invalidation, not connection-pool lifetime.
            var editOptions = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
            };
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(editOptions.ToString()))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE Meta SET Value='1' WHERE Key='FormatVersion'";
                cmd.ExecuteNonQuery();
            }

            var staleLogger = new CaptureLogger();
            var stale = new LodStore(staleLogger);
            c.True(stale.Open(path), "a cache in an older format still opens");
            c.Eq(0, stale.LoadAllKeys((_, _, _, _) => { }),
                "an older format is discarded, not read");
            c.True(staleLogger.Contains("discarding"), "the purge says that it discarded data");
            c.True(staleLogger.Contains("2"), "the purge reports how many sections it took");
            stale.Dispose();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
        }
    }

    static void RoundTrip(Check c)
    {
        var section = new LodSection();
        int stone = section.FindOrAddPaletteEntry(blockId: 11, color: 0x00445566, flags: 0);
        int water = section.FindOrAddPaletteEntry(blockId: 22, color: 0x00112233,
            flags: LodPaletteEntry.FlagWater, tintSlot: 9);

        section.SetColumn(0, new[] { LodSection.PackRun(stone, 30, 0) });
        section.SetColumn(1, new[] { LodSection.PackRun(water, 40, 30), LodSection.PackRun(stone, 30, 0) });
        section.SetColumn(Fixtures.Total - 1, new[] { LodSection.PackRun(stone, 12, 0) });

        LodSection back = Restore(c, section);
        if (back == null!) return;

        c.SeqEq(section.Runs, back.Runs, "runs survive the round trip");
        c.SeqEq(section.ColumnStart, back.ColumnStart, "column offsets survive the round trip");
        c.SeqEq(section.Captured, back.Captured, "the captured bitmask survives the round trip");
        c.Eq(section.CapturedColumns, back.CapturedColumns, "the captured count is rebuilt");
        c.Eq(section.Palette.Count, back.Palette.Count, "the palette keeps its length");

        for (int i = 0; i < section.Palette.Count; i++)
        {
            c.Eq(section.Palette[i].Color, back.Palette[i].Color, $"palette[{i}] colour survives");
            c.Eq(section.Palette[i].Flags, back.Palette[i].Flags, $"palette[{i}] flags survive");
        }

        // The last column is the one an off-by-one in the 4096 run counts or the 512-byte
        // captured bitmask would lose, and losing it is invisible until terrain has a seam.
        c.True(back.Captured[Fixtures.Total - 1], "the final column survives the bitmask");
        c.SeqEq(section.ColumnRuns(Fixtures.Total - 1).ToArray(),
            back.ColumnRuns(Fixtures.Total - 1).ToArray(), "the final column's runs survive");
    }

    /// <summary>
    /// Two fields deliberately do NOT round-trip, and asserting full equality would lock in
    /// the wrong thing:
    ///   - TintSlot is never written, so an existing cache picks up corrected per-species
    ///     tints without re-capturing, and stays right when a game update remaps them.
    ///   - BlockId cannot be resolved off the main thread, so a null world defers the codes
    ///     into PendingPaletteCodes for the main thread to resolve on install.
    /// </summary>
    static void DeferredPalette(Check c)
    {
        var section = new LodSection();
        section.FindOrAddPaletteEntry(blockId: 11, color: 0x00445566, flags: 0, tintSlot: 5);
        section.SetColumn(0, new[] { LodSection.PackRun(0, 10, 0) });

        string[] codes = { "game:rock-granite" };
        LodSection back = Restore(c, section, codes);
        if (back == null!) return;

        c.True(back.PendingPaletteCodes != null, "a null world defers palette codes for the main thread");
        c.SeqEq(codes, back.PendingPaletteCodes!, "the deferred codes are the ones written");
        c.Eq(0, back.Palette[0].BlockId, "block ids stay unresolved until the main thread runs");
        c.Eq((byte)0, back.Palette[0].TintSlot, "tint slots are re-derived, never persisted");
        c.Eq(0x00445566, back.Palette[0].Color, "colour is persisted and comes back");
    }

    /// <summary>
    /// A block code that does not resolve must not become a permanent answer.
    ///
    /// The lookup runs on the storage thread against whatever the registry holds at that
    /// moment, and sections start loading from cache before a world has finished coming
    /// up. Caching the failure alongside the successes meant a code that lost that race
    /// answered 0 for the rest of the session, and an entry with no block id keeps the
    /// flags the capturing side worked out - so it was still drawn, as terrain, with the
    /// colour a server leaves at zero. Black ground, correctly shaped, and nothing logged.
    /// </summary>
    static void AFailedLookupIsNotRemembered(Check c)
    {
        var section = new LodSection();
        section.FindOrAddPaletteEntry(blockId: 0, color: 0, flags: 0, tintSlot: 0);
        section.SetColumn(0, new[] { LodSection.PackRun(0, 10, 0) });
        section.PendingPaletteCodes = new[] { "game:rock-granite" };

        var store = new LodStore(null!);

        // The registry is not ready yet, which is the race.
        int calls = 0;
        store.ResolvePendingPalette(section, _ => { calls++; return 0; });
        c.Eq(1, calls, "the first resolve asks the registry");
        c.Eq(0, section.Palette[0].BlockId, "and gets nothing, because nothing is registered yet");
        c.SeqEq(new[] { "game:rock-granite" }, store.UnresolvedCodes(),
            "the code that did not resolve is recorded, so it can be named rather than guessed at");

        // Same code, same store, registry now up. Asking again is the whole point.
        section.PendingPaletteCodes = new[] { "game:rock-granite" };
        store.ResolvePendingPalette(section, _ => { calls++; return 42; });
        c.Eq(2, calls, "a code that failed is asked again rather than answered from cache");
        c.Eq(42, section.Palette[0].BlockId, "and resolves once the registry has it");
        c.Eq(0, store.UnresolvedCodes().Length, "and stops being reported as unresolved");

        // A code that DID resolve is cached, because that is the hot path.
        section.PendingPaletteCodes = new[] { "game:rock-granite" };
        store.ResolvePendingPalette(section, _ => { calls++; return 99; });
        c.Eq(2, calls, "a code that resolved is answered from cache and not looked up again");
        c.Eq(42, section.Palette[0].BlockId, "keeping the id it first resolved to");
    }

    /// <summary>
    /// A cache holding sections with no palette colour must repair itself as it loads.
    ///
    /// A capturing server stores 0 for every colour, because it has no texture atlas, and
    /// the receiving client fills them in. A client also PERSISTS what it received, so
    /// anything that stopped the fill-in was written to disk and stayed there. Measured on
    /// a real world afterwards: 7 sections entirely uncoloured and 59 partly, on ground as
    /// ordinary as soil-low-normal and rock-slate. Colour 0 draws as pure black, and no
    /// amount of fixing the cause reaches a cache that already has it.
    /// </summary>
    static void AColourlessCacheIsRepaired(Check c)
    {
        c.True(LodPaletteRepair.NeedsColor(0), "colour 0 is what a writer leaves when it cannot answer");
        c.False(LodPaletteRepair.NeedsColor(unchecked((int)0xFF000000)),
            "opaque black is a real colour and is left alone");
        c.False(LodPaletteRepair.NeedsColor(1), "so is anything else");

        var section = new LodSection();
        section.FindOrAddPaletteEntry(blockId: 11, color: 0, flags: 0, tintSlot: 0);
        section.FindOrAddPaletteEntry(blockId: 12, color: unchecked((int)0xFF336699), flags: 0, tintSlot: 0);
        section.FindOrAddPaletteEntry(blockId: 0, color: 0, flags: 0, tintSlot: 0);

        int asked = 0;
        int repaired = LodPaletteRepair.Fill(section, id =>
        {
            asked++;
            return id == 11 ? unchecked((int)0xFF112233) : 0;
        });

        c.Eq(2, repaired, "both uncoloured entries are repaired");
        c.Eq(2, asked, "and the entry that already had a colour is not asked about");
        c.Eq(unchecked((int)0xFF112233), section.Palette[0].Color, "a known block takes its real colour");
        c.Eq(unchecked((int)0xFF336699), section.Palette[1].Color, "an already-coloured entry is untouched");

        // The provider answered 0 for the unknown block. Storing that would leave the
        // entry needing repair for ever, and repairing marks the section dirty, so the
        // cache would be rewritten on every single load.
        c.Eq(LodPaletteRepair.UnknownBlockColor, section.Palette[2].Color,
            "a block nothing can colour becomes grey rather than staying black");
        c.False(LodPaletteRepair.NeedsColor(section.Palette[2].Color),
            "so the repair finishes instead of running again every load");

        c.Eq(0, LodPaletteRepair.Fill(section, _ => 0), "a repaired section needs no second pass");
    }

    /// <summary>
    /// Disposing the local offer reader must release its file handle, not park it.
    ///
    /// Microsoft.Data.Sqlite pools connections by default: Dispose returns the native
    /// handle to a process-wide pool keyed by connection string, and the file stays open
    /// until the process exits. In singleplayer that handle points at the server side's
    /// cache, and it survived leaving the world - so the next load of the same world had
    /// the integrated server's LodStore.Open refused with "it seems to be not writable",
    /// every time, on the platform whose file sharing blocks it. Reported from the field
    /// against 0.2.0, the release that introduced this reader; 0.1.0 had nothing to leak.
    ///
    /// Proven through /proc/self/fd, which is why the assertion is Linux-only: the leak is
    /// a handle held by this process, and that is where a process's handles are listed.
    /// </summary>
    static void DisposingTheOfferReaderReleasesItsFileHandle(Check c)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vh-offer-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string clientDb = Path.Combine(dir, "world.db");
            string serverDb = Path.Combine(dir, "world-server.db");

            // A throwaway unpooled writer builds the fixture file, so the only connection
            // that can linger afterwards is the one under test.
            var writer = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = serverDb,
                Pooling = false,
            };
            using (var w = new Microsoft.Data.Sqlite.SqliteConnection(writer.ToString()))
            {
                w.Open();
                using var cmd = w.CreateCommand();
                cmd.CommandText =
                    "CREATE TABLE Section (Detail INTEGER, SX INTEGER, SZ INTEGER, "
                    + "Data BLOB, ApplyToParent INTEGER, ModifiedMs INTEGER);"
                    + "INSERT INTO Section VALUES (0, 1, 2, x'00', 0, 0);";
                cmd.ExecuteNonQuery();
            }

            var logger = new CaptureLogger();
            LodLocalOfferSource? offers = LodLocalOfferSource.TryOpen(clientDb, logger);
            c.True(offers != null, "the server-side cache beside a client path opens");
            if (offers == null) return;

            long first = LodWorld.SectionKey(0, 1, 2);
            long[] initial = WaitForOfferDelta(offers);
            c.SeqEq(new[] { first }, initial,
                "the reader publishes its initial key scan from the worker");

            // A later scan publishes only the row that appeared since the first one,
            // never the complete retained table again.
            using (var w = new Microsoft.Data.Sqlite.SqliteConnection(writer.ToString()))
            {
                w.Open();
                using var cmd = w.CreateCommand();
                cmd.CommandText = "INSERT INTO Section VALUES (1, 3, 4, x'00', 0, 1);";
                cmd.ExecuteNonQuery();
            }

            long second = LodWorld.SectionKey(1, 3, 4);
            offers.RequestDiscovery();
            c.SeqEq(new[] { second }, WaitForOfferDelta(offers),
                "a later scan publishes only newly discovered keys");

            offers.RequestDiscovery();
            Thread.Sleep(100);
            c.False(offers.TryTakeDiscoveredKeys(out _),
                "an unchanged scan publishes no owning-thread work");
            offers.Dispose();

            if (OperatingSystem.IsLinux())
            {
                c.False(ProcessHoldsHandleTo(serverDb),
                    "Dispose releases the file handle instead of pooling it for the rest of the process");
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp dir; best effort */ }
        }
    }

    static long[] WaitForOfferDelta(LodLocalOfferSource offers)
    {
        offers.RequestDiscovery();
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (timeout.ElapsedMilliseconds < 5000)
        {
            if (offers.TryTakeDiscoveredKeys(out long[] keys)) return keys;
            Thread.Sleep(10);
        }
        return Array.Empty<long>();
    }

    static bool ProcessHoldsHandleTo(string path)
    {
        foreach (string fd in Directory.EnumerateFiles("/proc/self/fd"))
        {
            try
            {
                if (new FileInfo(fd).LinkTarget == path) return true;
            }
            catch
            {
                // A descriptor can close between listing and reading; not this check's business.
            }
        }
        return false;
    }

    /// <summary>
    /// Server assist must never borrow the writable LodStore connection on the owning
    /// thread. Its dedicated reader owns one unpooled read-only connection, preserves
    /// FIFO result order, and counts completed blobs against the same bounded allowance
    /// as queued and executing reads.
    /// </summary>
    static void AssistBlobReaderIsBoundedOrderedAndReadOnly(Check c)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vh-assist-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "server.db");
        try
        {
            var writerOptions = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
            };
            using (var writer = new Microsoft.Data.Sqlite.SqliteConnection(writerOptions.ToString()))
            {
                writer.Open();
                using var create = writer.CreateCommand();
                create.CommandText =
                    "CREATE TABLE Section (Detail INTEGER NOT NULL, SX INTEGER NOT NULL, "
                    + "SZ INTEGER NOT NULL, Data BLOB NOT NULL, PRIMARY KEY (Detail, SX, SZ));";
                create.ExecuteNonQuery();

                for (int i = 0; i < LodAssistBlobReader.MaxOutstanding; i++)
                {
                    using var insert = writer.CreateCommand();
                    insert.CommandText =
                        "INSERT INTO Section (Detail, SX, SZ, Data) VALUES (0, @x, 7, @data)";
                    insert.Parameters.AddWithValue("@x", i + 1);
                    insert.Parameters.AddWithValue("@data", new byte[] { (byte)(i + 1), 0x5a });
                    insert.ExecuteNonQuery();
                }
            }

            var logger = new CaptureLogger();
            using (var reader = new LodAssistBlobReader(path, logger, trackAllocations: true))
            {
                var expected = new List<long>();
                for (int i = 0; i < LodAssistBlobReader.MaxOutstanding; i++)
                {
                    long key = LodWorld.SectionKey(0, i + 1, 7);
                    expected.Add(key);
                    c.True(reader.TryEnqueue(new LodAssistBlobReadRequest(
                        i % 2 == 0 ? "alice" : "bob", 4, key, Environment.TickCount64)),
                        "a read within the total outstanding cap is accepted");
                }

                c.False(reader.TryEnqueue(new LodAssistBlobReadRequest(
                    "carol", 9, LodWorld.SectionKey(0, 99, 7), Environment.TickCount64)),
                    "queued and completed reads together enforce the outstanding cap");

                LodAssistBlobReadResult[] results = WaitForAssistReadResults(
                    reader, LodAssistBlobReader.MaxOutstanding);
                c.Eq(LodAssistBlobReader.MaxOutstanding, results.Length,
                    "every accepted read publishes one result");
                c.SeqEq(expected, results.Select(result => result.Key).ToArray(),
                    "the single reader publishes results in request order");
                c.True(results.All(result => !result.ReadFailed),
                    "ordinary reads do not report failures");
                c.True(results.Select((result, i) =>
                        result.Blob.SequenceEqual(new byte[] { (byte)(i + 1), 0x5a })).All(ok => ok),
                    "each result carries the exact stored blob");
                c.Eq(0, reader.Outstanding,
                    "taking every result releases the complete outstanding allowance");

                long missing = LodWorld.SectionKey(0, 999, 7);
                c.True(reader.TryEnqueue(new LodAssistBlobReadRequest(
                    "alice", 4, missing, Environment.TickCount64)),
                    "a later read is accepted after results release the cap");
                LodAssistBlobReadResult[] miss = WaitForAssistReadResults(reader, 1);
                c.Eq(1, miss.Length, "a missing row still publishes one terminal result");
                c.Eq(0, miss[0].Blob.Length, "a missing row is represented by an empty blob");
                c.False(miss[0].ReadFailed, "an ordinary row miss is not a database failure");
            }

            using (var failedReader = new LodAssistBlobReader(
                Path.Combine(dir, "absent.db"), logger, trackAllocations: false))
            {
                long key = LodWorld.SectionKey(0, 1, 7);
                c.True(failedReader.TryEnqueue(new LodAssistBlobReadRequest(
                    "alice", 4, key, Environment.TickCount64)),
                    "a read is accepted before a lazy connection failure is known");
                LodAssistBlobReadResult[] failed = WaitForAssistReadResults(failedReader, 1);
                c.Eq(1, failed.Length, "a database failure still publishes one result");
                c.True(failed[0].ReadFailed, "a database failure is explicit and retryable upstream");
                c.Eq(1, failedReader.ReadErrors, "the reader counts the failed query");
                c.True(failedReader.FirstReadError != null,
                    "the reader retains the first failure for diagnostics");
            }

            if (OperatingSystem.IsLinux())
            {
                c.False(ProcessHoldsHandleTo(path),
                    "disposing the assist reader closes rather than pools its file handle");
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
        }
    }

    static LodAssistBlobReadResult[] WaitForAssistReadResults(
        LodAssistBlobReader reader, int count)
    {
        var results = new List<LodAssistBlobReadResult>(count);
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (results.Count < count && timeout.ElapsedMilliseconds < 5000)
        {
            while (reader.TryTakeResult(out LodAssistBlobReadResult result))
                results.Add(result);
            if (results.Count < count) Thread.Sleep(10);
        }
        return results.ToArray();
    }

    /// <summary>
    /// Bad input must come back null, never throw. The storage thread deserializes rows off
    /// the main thread and the network path deserializes whatever a server sent; an
    /// exception on either takes down more than the one bad section.
    /// </summary>
    static void Rejection(Check c)
    {
        var store = new LodStore(null!);
        byte[] good = LodStore.Serialize(Fixtures.Snapshot(Fixtures.SolidSection()));

        c.NoThrow(() => store.DeserializeForeign(Array.Empty<byte>(), null), "an empty blob does not throw");
        c.Eq(null, store.DeserializeForeign(Array.Empty<byte>(), null), "an empty blob returns null");

        c.Eq(null, store.DeserializeForeign(new byte[] { 4 }, null), "a one-byte blob returns null");

        byte[] wrongVersion = (byte[])good.Clone();
        wrongVersion[0] = 99;
        c.Eq(null, store.DeserializeForeign(wrongVersion, null), "a blob from a future format returns null");

        // Truncation is the realistic corruption: a partial write, or a section cut short
        // in transit.
        byte[] truncated = good[..(good.Length / 2)];
        c.NoThrow(() => store.DeserializeForeign(truncated, null), "a truncated blob does not throw");
        c.Eq(null, store.DeserializeForeign(truncated, null), "a truncated blob returns null");

        byte[] garbage = (byte[])good.Clone();
        for (int i = 1; i < garbage.Length; i += 3) garbage[i] ^= 0xA5;
        c.NoThrow(() => store.DeserializeForeign(garbage, null), "a corrupted blob does not throw");

        c.True(store.DeserializeForeign(good, null) != null, "a good blob still deserializes");
    }

    static void BackgroundForeignDecodeIsBoundedAndIsolated(Check c)
    {
        using var store = new LodStore(null!);
        using var worker = new LodStorageThread(store);
        byte[] good = LodStore.Serialize(Fixtures.Snapshot(Fixtures.SolidSection()));

        // Local sibling-cache work has its own cap. Completed results still count until
        // the owning thread takes them, so a fast decoder cannot make this race flaky.
        for (int i = 0; i < 8; i++)
        {
            c.True(worker.TryEnqueueForeign(
                7, i, LodForeignSource.LocalOffer, good),
                $"local foreign decode {i} enters the bounded queue");
        }
        c.False(worker.TryEnqueueForeign(
            7, 99, LodForeignSource.LocalOffer, good),
            "a ninth local foreign decode is rejected before retaining its blob");

        for (int i = 0; i < 8; i++)
        {
            LodForeignDecodeResult result = WaitForForeign(worker, c);
            c.Eq(7L, result.Epoch, "the decoded result retains its world epoch");
            c.Eq((long)i, result.Key, "the foreign decoder preserves FIFO identity");
            c.True(result.Section != null, "a valid foreign blob decodes on the worker");
            c.True(result.Section?.PendingPaletteCodes != null,
                "worker decode retains codes for owning-thread registry resolution");
            c.True(result.Section == null || result.Section.Palette.All(e => e.BlockId == 0),
                "worker decode never resolves live block ids");
        }
        c.True(worker.CanEnqueueForeign(LodForeignSource.LocalOffer),
            "taking results releases local decoder capacity");

        byte[] future = (byte[])good.Clone();
        future[0] = 99;
        c.True(worker.TryEnqueueForeign(
            8, 100, LodForeignSource.ServerAssist, future),
            "a future blob is isolated to one server decode job");
        c.True(worker.TryEnqueueForeign(
            8, 101, LodForeignSource.ServerAssist, good),
            "valid work can queue behind a rejected blob");
        c.Eq(null, WaitForForeign(worker, c).Section,
            "a future blob returns one failed result rather than throwing");
        c.True(WaitForForeign(worker, c).Section != null,
            "the decoder remains alive after rejecting a future blob");
    }

    static LodForeignDecodeResult WaitForForeign(LodStorageThread worker, Check c)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 5000)
        {
            if (worker.TryTakeForeignResult(out LodForeignDecodeResult result)) return result;
            Thread.Sleep(1);
        }

        c.True(false, "the foreign decoder publishes a result within five seconds");
        return default;
    }

    static LodSection Restore(Check c, LodSection section, string[]? codes = null)
    {
        byte[] blob = LodStore.Serialize(Fixtures.Snapshot(section, codes: codes));
        LodSection? back = new LodStore(null!).DeserializeForeign(blob, null);
        c.True(back != null, "the blob deserializes");
        return back!;
    }
}
