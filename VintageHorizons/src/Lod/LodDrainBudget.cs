using System.Diagnostics;

namespace VintageHorizons;

/// <summary>
/// Bounds an owning-thread FIFO drain by elapsed time and estimated content bytes.
/// The first item is always admitted: one section can exceed either limit by itself,
/// and refusing that oldest item forever would prevent every item behind it progressing.
/// </summary>
// A value type because render-frame callers create two of these every frame. Turning the
// budget itself into heap traffic would make the latency guard add steady GC pressure.
internal struct LodDrainBudget
{
    public const long DefaultMaxBytes = 512 * 1024;
    public const double DefaultMaxMilliseconds = 2.0;

    readonly long maxBytes;
    readonly long deadline;
    readonly Func<long> timestamp;

    public int Items { get; private set; }
    public long Bytes { get; private set; }

    // Struct construction with `new LodDrainBudget()` otherwise zero-initializes every
    // field instead of selecting an overload whose arguments happen to be optional.
    // Keep the production shorthand explicit so its clock delegate is always present.
    public LodDrainBudget()
        : this(DefaultMaxBytes, DefaultMaxMilliseconds)
    {
    }

    public LodDrainBudget(long maxBytes, double maxMilliseconds)
        : this(maxBytes, Stopwatch.GetTimestamp(),
            Math.Max(1, (long)Math.Ceiling(maxMilliseconds * Stopwatch.Frequency / 1000.0)),
            Stopwatch.GetTimestamp)
    {
    }

    /// <summary>Deterministic clock seam for the fast tier.</summary>
    internal LodDrainBudget(long maxBytes, long started, long durationTicks, Func<long> timestamp)
    {
        this.maxBytes = Math.Max(1, maxBytes);
        this.timestamp = timestamp;
        deadline = started + Math.Max(1, durationTicks);
    }

    /// <summary>
    /// Reserve one item whose size is known before processing. A zero estimate is valid
    /// for work such as a database read whose blob size is learned only afterward.
    /// </summary>
    public bool TryStart(long estimatedBytes)
    {
        estimatedBytes = Math.Max(0, estimatedBytes);
        if (Items > 0
            && (timestamp() >= deadline || WouldExceedByteLimit(estimatedBytes))) return false;

        Items++;
        Bytes = SaturatingAdd(Bytes, estimatedBytes);
        return true;
    }

    /// <summary>Add size learned during an already-admitted item.</summary>
    public void AddBytes(long bytes)
    {
        if (bytes > 0) Bytes = SaturatingAdd(Bytes, bytes);
    }

    bool WouldExceedByteLimit(long nextBytes) =>
        Bytes >= maxBytes || nextBytes > maxBytes - Bytes;

    static long SaturatingAdd(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;
}
