using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// The server side's cache, read by the client side of the same singleplayer world.
///
/// A savegame sweep can only run on the server side, because only it can ask for chunk
/// columns the player is nowhere near. But the server has no texture atlas, so what it
/// captures is geometry with every palette colour left at zero. The client has the atlas
/// and cannot reach the columns. Each half holds exactly what the other is missing.
///
/// So the swept sections travel the same road as sections fetched from a real server: they
/// arrive colourless, get recoloured from their block codes on install, and land in the
/// client's own cache. LodRemoteKeySet does not care whether a blob came off a socket or
/// off the disk beside it, which is why this is a reader and not a subsystem.
///
/// Deliberately NOT a LodStore. That class creates tables and can delete rows whose format
/// version it does not recognise, and pointing it at a file another pipeline has open for
/// writing is a good way to find out what SQLite does about two writers. This only ever
/// reads, and says so in the connection string.
/// </summary>
public sealed class LodLocalOfferSource : IDisposable
{
    readonly SqliteConnection conn;
    readonly ILogger logger;
    readonly string path;
    readonly ConcurrentQueue<long[]> keyDeltas = new();
    readonly AutoResetEvent scanSignal = new(false);
    readonly Thread scanThread;
    readonly Dictionary<long, long> blobRetryNotBefore = new();
    volatile bool running = true;

    /// <summary>
    /// A growing sibling cache changes slowly. The worker still does a safe full scan,
    /// but only at this coarse cadence and only it owns the scan connection. The owning
    /// game thread receives immutable batches containing keys it has not seen before.
    /// </summary>
    const int ScanIntervalMs = 2000;

    /// <summary>
    /// Bound the amount of quadtree registration one game tick receives when the first
    /// scan finds a large existing cache. Keys are fixed-size and cheap, but an initial
    /// cache can still contain tens of thousands of them.
    /// </summary>
    const int KeysPerDelta = 2048;

    /// <summary>A transient row miss should not become a 20 Hz SQLite poll.</summary>
    const int BlobMissRetryMs = 1000;

    LodLocalOfferSource(string path, SqliteConnection conn, ILogger logger)
    {
        this.path = path;
        this.conn = conn;
        this.logger = logger;
        scanThread = new Thread(ScanLoop)
        {
            Name = "vintagehorizons-local-offers",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        scanThread.Start();
    }

    static SqliteConnection OpenReadOnly(string path)
    {
        // Pooling off is a correctness requirement; see TryOpen and G13.
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        };
        var opened = new SqliteConnection(builder.ToString());
        opened.Open();
        return opened;
    }

    /// <summary>
    /// Opens the server-side cache beside a client cache, or returns null when there is
    /// none - which is the normal case, since only a singleplayer world that has swept has
    /// one. Never throws: this is an optional extra and must not take a join down with it.
    /// </summary>
    public static LodLocalOfferSource? TryOpen(string clientDbPath, ILogger logger)
    {
        string path = Path.ChangeExtension(clientDbPath, null) + "-server.db";
        if (!File.Exists(path)) return null;

        try
        {
            // Read-only, and shared: in singleplayer the server side of this same process
            // has the file open and is very likely still writing to it.
            //
            // Pooling off, and it is not an optimisation choice. Microsoft.Data.Sqlite
            // pools by default, and a pooled connection's Dispose parks the native handle
            // in a process-wide pool instead of closing it. This handle points at the
            // server side's cache, so it outlived leaving the world - and the next load
            // of the same world had the integrated server refused by its own cache file,
            // "it seems to be not writable", every time, on the platform whose file
            // sharing blocks a writer while any handle is open. This connection is opened
            // once per world; pooling bought nothing to begin with.
            SqliteConnection opened = OpenReadOnly(path);
            return new LodLocalOfferSource(path, opened, logger);
        }
        catch (Exception e)
        {
            logger.Warning("Could not read the server-side LOD cache at {0}: {1}", path, e.Message);
            return null;
        }
    }

    /// <summary>
    /// One immutable batch of newly discovered keys, or false when the worker has
    /// published nothing since the last game tick. The SQL enumeration never crosses
    /// onto the caller's thread.
    /// </summary>
    public bool TryTakeDiscoveredKeys(out long[] keys) => keyDeltas.TryDequeue(out keys!);

    /// <summary>Wake the reader early. Used by the isolated fixture; production polls.</summary>
    internal void RequestDiscovery() => scanSignal.Set();

    void ScanLoop()
    {
        var known = new HashSet<long>();
        try
        {
            // This connection is created, used and disposed on this thread. The separate
            // connection above remains game-thread-owned for small, visibility-driven
            // blob reads; no command or connection state crosses thread ownership.
            using SqliteConnection scanConn = OpenReadOnly(path);
            using SqliteCommand cmd = scanConn.CreateCommand();
            cmd.CommandText = "SELECT Detail, SX, SZ FROM Section";

            while (running)
            {
                try
                {
                    var scanned = new List<long>();
                    using SqliteDataReader reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        scanned.Add(LodWorld.SectionKey(
                            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)));
                    }

                    // Commit the scan to the known set only after the reader reached EOF.
                    // If SQLite throws halfway through, marking that partial prefix known
                    // before publishing it would suppress those keys forever on retry.
                    List<long>? discovered = null;
                    foreach (long key in scanned)
                    {
                        if (known.Add(key)) (discovered ??= new List<long>()).Add(key);
                    }

                    if (discovered != null)
                    {
                        for (int start = 0; start < discovered.Count; start += KeysPerDelta)
                        {
                            int count = Math.Min(KeysPerDelta, discovered.Count - start);
                            keyDeltas.Enqueue(discovered.GetRange(start, count).ToArray());
                        }
                    }
                }
                catch (Exception e)
                {
                    // A live writer can make a read fail transiently. Keep the known set
                    // and try again; re-publishing old keys would only add owning-thread
                    // work and AddRemoteKeys already has its own deduplication.
                    try { logger.Warning("Could not list server-side LOD sections: {0}", e.Message); }
                    catch { /* diagnostics must not kill the reader */ }
                }

                scanSignal.WaitOne(ScanIntervalMs);
            }
        }
        catch (Exception e)
        {
            try { logger.Warning("Could not start server-side LOD discovery: {0}", e.Message); }
            catch { /* diagnostics must not kill shutdown */ }
        }
    }

    /// <summary>
    /// One section's stored blob, or null when it is not there. A miss is ordinary rather
    /// than exceptional: the sweep is very likely still running and writing more.
    /// </summary>
    public byte[]? Blob(long key)
    {
        long now = Environment.TickCount64;
        if (blobRetryNotBefore.TryGetValue(key, out long notBefore) && now < notBefore)
        {
            return null;
        }

        try
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Data FROM Section WHERE Detail=@d AND SX=@x AND SZ=@z";
            cmd.Parameters.AddWithValue("@d", LodWorld.KeyLevel(key));
            cmd.Parameters.AddWithValue("@x", LodWorld.KeySx(key));
            cmd.Parameters.AddWithValue("@z", LodWorld.KeySz(key));
            byte[]? blob = cmd.ExecuteScalar() as byte[];
            if (blob == null || blob.Length == 0)
            {
                blobRetryNotBefore[key] = now + BlobMissRetryMs;
            }
            else
            {
                blobRetryNotBefore.Remove(key);
            }
            return blob;
        }
        catch (Exception e)
        {
            blobRetryNotBefore[key] = now + BlobMissRetryMs;
            logger.Warning("Could not read a server-side LOD section: {0}", e.Message);
            return null;
        }
    }

    public void Dispose()
    {
        running = false;
        scanSignal.Set();
        scanThread.Join(15000);
        scanSignal.Dispose();

        try
        {
            conn.Close();
            conn.Dispose();
        }
        catch (Exception)
        {
            // Closing a read-only connection is not worth reporting a failure over.
        }
    }
}
