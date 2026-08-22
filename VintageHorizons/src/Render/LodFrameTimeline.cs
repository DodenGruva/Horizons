using System.Diagnostics;

namespace VintageHorizons;

/// <summary>
/// Whole-frame timing, at the resolution the reported symptom actually lives at.
///
/// The owner reports micro-hitches dozens of times a second on his frame-time graph. None
/// of the existing instruments can see them: the hitch counters trip at 25, 50 and 100 ms,
/// and a whole frame at 400 FPS is 2.5 ms, so a 400 microsecond spike - a 16% frame - is
/// counted by nothing at all. The per-phase histograms have the right resolution but only
/// ever measure our own work, so they cannot say whether a spike was ours.
///
/// This measures two things per frame and compares them:
///
/// - the interval between consecutive render frames, which is what the graph plots;
/// - the time spent inside this mod's render callback, which is our share of it.
///
/// A slow frame whose mod share is ordinary was not caused by us, and that is worth
/// establishing before another session is spent optimising our own phases. The judgement
/// is made against a moving average rather than a fixed threshold, because it has to mean
/// the same thing at 400 FPS and at 60.
/// </summary>
public sealed class LodFrameTimeline
{
    /// <summary>
    /// How far above the moving average an interval must be to count as a spike. Chosen so
    /// a frame that took half again as long as its neighbours is noticed; ordinary
    /// frame-to-frame jitter on an unsynchronised client sits well below it.
    /// </summary>
    internal const double SpikeFactor = 1.5;

    /// <summary>
    /// And how far above it in absolute terms, so that a very fast, very steady client does
    /// not report thousands of "spikes" that are tens of microseconds each.
    /// </summary>
    internal const double MinimumSpikeExcessUs = 250;

    /// <summary>
    /// Intervals longer than this are not frames, they are the game doing something else:
    /// a world load, a settings change, an alt-tab. Including one would poison the moving
    /// average for the next several hundred frames.
    /// </summary>
    internal const double ImplausibleIntervalUs = 250_000;

    /// <summary>
    /// Weight of the newest interval in the moving average. One sixty-fourth settles within
    /// a couple of hundred frames, which is well under a second at these rates, and is slow
    /// enough that a burst of hitches cannot raise the bar enough to hide itself.
    /// </summary>
    internal const double AverageWeight = 1.0 / 64;

    long previousFrameStart;
    double averageIntervalUs;
    double averageModUs;
    double pendingIntervalUs;

    public LodPhaseCost IntervalCost;
    public LodPhaseCost ModCost;

    /// <summary>
    /// How far each interval ran over the moving average, which is the shape of the
    /// reported symptom rather than its absolute size.
    ///
    /// This exists because the shared histogram's fine 25us buckets stop at one
    /// millisecond, and a frame at 400 FPS is 2.5 ms - so the interval's own percentiles
    /// are quantised to 250us and cannot resolve a 400us hitch at all. The excess over
    /// baseline is a small number by construction, so it lands in the fine buckets, and
    /// its p99 and maximum answer "how big are these hitches" directly.
    /// </summary>
    public LodPhaseCost ExcessCost;

    /// <summary>Frames whose interval stood out from the ones around it.</summary>
    public int SlowFrames { get; private set; }

    /// <summary>
    /// Slow frames where our own callback was also unusually long. The difference between
    /// this and <see cref="SlowFrames"/> is the whole point of the pair: it separates
    /// "the mod hitched" from "the frame hitched while the mod behaved".
    /// </summary>
    public int SlowFramesWithSlowMod { get; private set; }

    /// <summary>The worst interval seen, and our share of that particular frame.</summary>
    public double WorstIntervalUs { get; private set; }
    public double WorstIntervalModUs { get; private set; }

    public double AverageIntervalUs => averageIntervalUs;

    /// <summary>
    /// Opens a frame. Returns the timestamp to hand back to <see cref="End"/>; taking it
    /// here rather than reading the clock twice keeps the interval and our share measured
    /// from the same instant.
    /// </summary>
    public long Begin() => Begin(Stopwatch.GetTimestamp());

    internal long Begin(long timestamp)
    {
        long previous = previousFrameStart;
        previousFrameStart = timestamp;
        if (previous == 0) return timestamp;

        long elapsed = timestamp - previous;
        if (elapsed <= 0) return timestamp;

        double us = elapsed * 1_000_000.0 / Stopwatch.Frequency;
        if (us > ImplausibleIntervalUs) return timestamp;

        IntervalCost.AddElapsedTicks(elapsed);
        pendingIntervalUs = us;
        return timestamp;
    }

    /// <summary>Closes the frame opened with <see cref="Begin"/>.</summary>
    public void End(long frameStart) => End(frameStart, Stopwatch.GetTimestamp());

    internal void End(long frameStart, long timestamp)
    {
        long elapsed = timestamp - frameStart;
        if (elapsed < 0) elapsed = 0;
        ModCost.AddElapsedTicks(elapsed);

        double intervalUs = pendingIntervalUs;
        pendingIntervalUs = 0;
        if (intervalUs <= 0) return;

        double modUs = elapsed * 1_000_000.0 / Stopwatch.Frequency;
        double modAverage = averageModUs;
        averageModUs = modAverage == 0
            ? modUs
            : modAverage + (modUs - modAverage) * AverageWeight;
        if (intervalUs > WorstIntervalUs)
        {
            WorstIntervalUs = intervalUs;
            WorstIntervalModUs = modUs;
        }

        // Compared against the average as it stood BEFORE this frame, so a spike is judged
        // against its neighbours rather than partly against itself.
        double average = averageIntervalUs;
        averageIntervalUs = average == 0
            ? intervalUs
            : average + (intervalUs - average) * AverageWeight;
        if (average == 0) return;

        double excessUs = intervalUs - average;
        ExcessCost.AddElapsedTicks(
            excessUs <= 0 ? 0 : (long)(excessUs * Stopwatch.Frequency / 1_000_000.0));

        if (intervalUs < average * SpikeFactor || excessUs < MinimumSpikeExcessUs) return;

        SlowFrames++;

        // Our share is judged on exactly the same terms - same moving average, same
        // factor - so the two answers cannot disagree merely because they used different
        // rules. The mod average is maintained here rather than read from ModCost, which
        // is reset at every report and would make the first frames after one look elevated.
        if (modAverage > 0 && modUs >= modAverage * SpikeFactor) SlowFramesWithSlowMod++;
    }

    /// <summary>
    /// One line for the periodic report. Deliberately states the median beside the tail:
    /// a p99 means nothing without knowing what an ordinary frame costs.
    /// </summary>
    public string Describe()
    {
        if (IntervalCost.Calls == 0) return "no frames measured";

        double interval = IntervalCost.P50Us;
        string share = SlowFrames == 0
            ? "no frames stood out from their neighbours"
            : $"{SlowFrames} frames stood out ({SlowFrames * 1000.0 / Math.Max(1, IntervalCost.Calls * interval / 1000.0):0.0}/s), "
                + $"{SlowFramesWithSlowMod} of them with our own work also elevated";

        return $"{IntervalCost.Calls} frames, interval p50 {interval:0}us "
            + $"(~{(interval > 0 ? 1_000_000.0 / interval : 0):0} fps), p95 {IntervalCost.P95Us:0}us, "
            + $"p99 {IntervalCost.P99Us:0}us, max {IntervalCost.MaxUs:0}us | "
            + $"over baseline p95 {ExcessCost.P95Us:0}us, p99 {ExcessCost.P99Us:0}us, "
            + $"max {ExcessCost.MaxUs:0}us | "
            + $"our callback p50 {ModCost.P50Us:0}us, p99 {ModCost.P99Us:0}us, max {ModCost.MaxUs:0}us | "
            + $"worst frame {WorstIntervalUs:0}us, of which ours {WorstIntervalModUs:0}us | {share}";
    }

    public void Reset()
    {
        IntervalCost.Reset();
        ModCost.Reset();
        ExcessCost.Reset();
        SlowFrames = 0;
        SlowFramesWithSlowMod = 0;
        WorstIntervalUs = 0;
        WorstIntervalModUs = 0;
        // The moving average and the previous timestamp deliberately survive a report
        // boundary: they describe the client, not the reporting interval, and restarting
        // them would make the first frames of every interval look like spikes.
    }
}
