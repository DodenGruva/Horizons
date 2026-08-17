using System.Diagnostics;

namespace VintageHorizons;

/// <summary>
/// Time spent in one phase of the render frame, since the last report.
///
/// These phases cost tens of microseconds against a frame budget of ten thousand, so a
/// frame-rate measurement cannot resolve them: the benchmark's own run-to-run spread is
/// larger than the whole of any one of them. Timing them directly is the only honest way
/// to tell whether a change to one did anything.
///
/// This is the same argument the server assist already makes for its blob reads, where
/// the serve loop records what it costs the tick "so the caps can be judged against a
/// measurement instead of an estimate".
///
/// Timing is always active. Managed-allocation sampling is opt-in for stats/benchmark
/// sessions; when disabled, the hot path adds only one predictable branch to the existing
/// timestamp pair and does not call the GC allocation counter.
/// </summary>
public readonly record struct LodPhaseStart(long Timestamp, long AllocatedBytes);

public struct LodPhaseCost
{
    // Compact piecewise histogram. Fine resolution where ordinary phases live, wider
    // buckets where only hitch classification matters:
    //   0..1ms   in 25us buckets
    //   1..10ms  in 250us buckets
    //   10..100ms in 2.5ms buckets
    //   100ms+   overflow (the exact maximum is still retained)
    const int FastBuckets = 40;
    const int MidBuckets = 36;
    const int SlowBuckets = 36;
    const int BucketCount = FastBuckets + MidBuckets + SlowBuckets + 1;

    long ticks;
    long maxTicks;
    int calls;
    int[]? histogram;
    int over25Ms;
    int over50Ms;
    int over100Ms;
    long allocatedBytes;
    long maxAllocatedBytes;
    int allocationSamples;

    /// <summary>Close a measurement opened with <see cref="Start"/>.</summary>
    public void Add(LodPhaseStart start)
    {
        long elapsed = Stopwatch.GetTimestamp() - start.Timestamp;
        long allocationDelta = start.AllocatedBytes < 0
            ? -1
            : GC.GetAllocatedBytesForCurrentThread() - start.AllocatedBytes;
        AddSample(elapsed, allocationDelta);
    }

    /// <summary>
    /// Add an already measured duration. Kept internal so deterministic checks can prove
    /// percentile boundaries without sleeping; production callers normally use Add(Start()).
    /// </summary>
    internal void AddElapsedTicks(long elapsed)
    {
        AddSample(elapsed, -1);
    }

    /// <summary>Deterministic timing/allocation seam for the fast checks.</summary>
    internal void AddSample(long elapsed, long allocationDelta)
    {
        if (elapsed < 0) elapsed = 0;
        ticks += elapsed;
        if (elapsed > maxTicks) maxTicks = elapsed;
        calls++;

        double us = elapsed * 1_000_000.0 / Stopwatch.Frequency;
        (histogram ??= new int[BucketCount])[BucketForMicroseconds(us)]++;
        if (us >= 25_000) over25Ms++;
        if (us >= 50_000) over50Ms++;
        if (us >= 100_000) over100Ms++;

        if (allocationDelta >= 0)
        {
            allocatedBytes += allocationDelta;
            if (allocationDelta > maxAllocatedBytes) maxAllocatedBytes = allocationDelta;
            allocationSamples++;
        }
    }

    public static LodPhaseStart Start(bool trackAllocations = false)
    {
        long allocationStart = trackAllocations
            ? GC.GetAllocatedBytesForCurrentThread()
            : -1;
        return new LodPhaseStart(Stopwatch.GetTimestamp(), allocationStart);
    }

    public int Calls => calls;
    public double AvgUs => calls == 0 ? 0 : ticks * 1_000_000.0 / Stopwatch.Frequency / calls;
    public double MaxUs => maxTicks * 1_000_000.0 / Stopwatch.Frequency;
    public double P95Us => PercentileUs(0.95);
    public double P99Us => PercentileUs(0.99);
    public int Over25Ms => over25Ms;
    public int Over50Ms => over50Ms;
    public int Over100Ms => over100Ms;
    public long AllocatedBytes => allocatedBytes;
    public long MaxAllocatedBytes => maxAllocatedBytes;
    public int AllocationSamples => allocationSamples;
    public double AvgAllocatedBytes => allocationSamples == 0
        ? 0
        : allocatedBytes / (double)allocationSamples;

    static int BucketForMicroseconds(double us)
    {
        if (us < 1_000) return Math.Clamp((int)(us / 25), 0, FastBuckets - 1);
        if (us < 10_000) return FastBuckets + Math.Clamp((int)((us - 1_000) / 250), 0, MidBuckets - 1);
        if (us < 100_000) return FastBuckets + MidBuckets
            + Math.Clamp((int)((us - 10_000) / 2_500), 0, SlowBuckets - 1);
        return BucketCount - 1;
    }

    double PercentileUs(double fraction)
    {
        if (calls == 0 || histogram == null) return 0;
        int rank = Math.Max(1, (int)Math.Ceiling(calls * fraction));
        int seen = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            seen += histogram[i];
            if (seen < rank) continue;
            if (i < FastBuckets) return (i + 1) * 25;
            if (i < FastBuckets + MidBuckets) return 1_000 + (i - FastBuckets + 1) * 250;
            if (i < FastBuckets + MidBuckets + SlowBuckets)
            {
                return 10_000 + (i - FastBuckets - MidBuckets + 1) * 2_500;
            }
            return MaxUs;
        }
        return MaxUs;
    }

    public void Reset()
    {
        ticks = 0;
        maxTicks = 0;
        calls = 0;
        over25Ms = over50Ms = over100Ms = 0;
        allocatedBytes = maxAllocatedBytes = 0;
        allocationSamples = 0;
        if (histogram != null) Array.Clear(histogram);
    }
}
