using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;

namespace VintageHorizons.Net;

internal readonly record struct LodAssistBlobReadRequest(
    string PlayerUid, long Session, long Key, long RequestedAtMilliseconds);

internal readonly record struct LodAssistBlobReadResult(
    string PlayerUid, long Session, long Key, byte[] Blob, bool ReadFailed,
    long ElapsedTicks, long AllocatedBytes, long RequestedAtMilliseconds);

/// <summary>
/// Reads server-assist blobs on one dedicated thread and one read-only SQLite connection.
/// Requests and results are both FIFO, and the outstanding cap covers queued, executing,
/// and completed reads so large blobs cannot accumulate without bound.
/// </summary>
internal sealed class LodAssistBlobReader : IDisposable
{
    // The client protocol retains at most this many unanswered sections. Keeping the
    // server reader to the same total bounds both queued work and completed blob bytes.
    internal const int MaxOutstanding = LodAssist.MaxSectionsInFlight;

    readonly string path;
    readonly ILogger logger;
    readonly bool trackAllocations;
    readonly ConcurrentQueue<LodAssistBlobReadRequest> requests = new();
    readonly ConcurrentQueue<LodAssistBlobReadResult> results = new();
    readonly AutoResetEvent signal = new(false);
    readonly Thread thread;
    volatile bool running = true;
    int outstanding;
    int disposed;
    long resultBytes;
    long activeRequestedAtMilliseconds;

    public LodAssistBlobReader(string path, ILogger logger, bool trackAllocations)
    {
        this.path = path;
        this.logger = logger;
        this.trackAllocations = trackAllocations;
        thread = new Thread(Loop)
        {
            Name = "vintagehorizons-assist-reader",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    public bool TryEnqueue(LodAssistBlobReadRequest request)
    {
        if (!running || !TryReserve()) return false;
        requests.Enqueue(request);
        signal.Set();
        return true;
    }

    bool TryReserve()
    {
        while (true)
        {
            int current = Volatile.Read(ref outstanding);
            if (current >= MaxOutstanding) return false;
            if (Interlocked.CompareExchange(ref outstanding, current + 1, current) == current)
                return true;
        }
    }

    public bool TryPeekResult(out LodAssistBlobReadResult result) => results.TryPeek(out result);

    public bool TryTakeResult(out LodAssistBlobReadResult result)
    {
        if (!results.TryDequeue(out result)) return false;
        Interlocked.Add(ref resultBytes, -result.Blob.LongLength);
        Interlocked.Decrement(ref outstanding);
        return true;
    }

    public int Outstanding => Math.Max(0, Volatile.Read(ref outstanding));
    public int PendingRequests => requests.Count;
    public int PendingResults => results.Count;
    public long PendingResultBytes => Math.Max(0, Interlocked.Read(ref resultBytes));

    public long OldestAgeMilliseconds
    {
        get
        {
            long oldest = 0;
            if (results.TryPeek(out LodAssistBlobReadResult result))
                oldest = result.RequestedAtMilliseconds;
            if (requests.TryPeek(out LodAssistBlobReadRequest request)
                && (oldest == 0 || request.RequestedAtMilliseconds < oldest))
                oldest = request.RequestedAtMilliseconds;
            long active = Interlocked.Read(ref activeRequestedAtMilliseconds);
            if (active > 0 && (oldest == 0 || active < oldest)) oldest = active;
            return oldest == 0 ? 0 : Math.Max(0, Environment.TickCount64 - oldest);
        }
    }

    void Loop()
    {
        SqliteConnection? connection = null;
        SqliteCommand? command = null;
        try
        {
            while (running)
            {
                if (!requests.TryDequeue(out LodAssistBlobReadRequest request))
                {
                    signal.WaitOne(200);
                    continue;
                }

                Interlocked.Exchange(ref activeRequestedAtMilliseconds,
                    request.RequestedAtMilliseconds);
                ReadOne(request, ref connection, ref command);
                Interlocked.Exchange(ref activeRequestedAtMilliseconds, 0);
            }
        }
        finally
        {
            command?.Dispose();
            connection?.Dispose();
        }
    }

    void ReadOne(LodAssistBlobReadRequest request,
        ref SqliteConnection? connection, ref SqliteCommand? command)
    {
        long started = Stopwatch.GetTimestamp();
        long allocationStart = trackAllocations
            ? GC.GetAllocatedBytesForCurrentThread()
            : -1;
        byte[] blob = Array.Empty<byte>();
        bool failed = false;

        try
        {
            if (command == null)
            {
                connection = OpenReadOnly(path);
                command = connection.CreateCommand();
                command.CommandText =
                    "SELECT Data FROM Section WHERE Detail=@detail AND SX=@sx AND SZ=@sz";
                command.Parameters.Add("@detail", SqliteType.Integer);
                command.Parameters.Add("@sx", SqliteType.Integer);
                command.Parameters.Add("@sz", SqliteType.Integer);
                command.Prepare();
            }

            command.Parameters["@detail"].Value = LodWorld.KeyLevel(request.Key);
            command.Parameters["@sx"].Value = LodWorld.KeySx(request.Key);
            command.Parameters["@sz"].Value = LodWorld.KeySz(request.Key);
            blob = command.ExecuteScalar() as byte[] ?? Array.Empty<byte>();
        }
        catch (Exception e)
        {
            failed = true;
            int errors = Interlocked.Increment(ref ReadErrors);
            try
            {
                FirstReadError ??= e.ToString();
                if (errors == 1)
                    logger.Warning("Could not read a server-assist LOD section: {0}", e.Message);
            }
            catch
            {
                // Diagnostics must never kill the reader or strand a client request.
            }

            command?.Dispose();
            connection?.Dispose();
            command = null;
            connection = null;
        }

        long elapsed = Stopwatch.GetTimestamp() - started;
        long allocated = allocationStart < 0
            ? -1
            : GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        Interlocked.Add(ref resultBytes, blob.LongLength);
        results.Enqueue(new LodAssistBlobReadResult(
            request.PlayerUid, request.Session, request.Key, blob, failed,
            elapsed, allocated, request.RequestedAtMilliseconds));
    }

    static SqliteConnection OpenReadOnly(string path)
    {
        var options = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            // Disposal must close the native handle; a pooled reader can retain the
            // server cache file across world teardown on Windows.
            Pooling = false,
        };
        var connection = new SqliteConnection(options.ToString());
        connection.Open();
        return connection;
    }

    public int ReadErrors;
    public string? FirstReadError;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        running = false;
        signal.Set();
        thread.Join(15000);
        signal.Dispose();
    }
}
