using System.Diagnostics;

namespace VintageHorizons.Checks;

public static class PhaseCostChecks
{
    public static void Run(Check c)
    {
        var cost = new LodPhaseCost();
        for (int i = 0; i < 95; i++) cost.AddElapsedTicks(TicksForUs(100));
        for (int i = 0; i < 4; i++) cost.AddElapsedTicks(TicksForUs(2_000));
        cost.AddElapsedTicks(TicksForUs(30_000));

        c.Eq(100, cost.Calls, "every phase sample is counted");
        c.True(cost.P95Us >= 100 && cost.P95Us <= 125,
            "p95 resolves inside the fine 25us bucket");
        c.True(cost.P99Us >= 2_000 && cost.P99Us <= 2_250,
            "p99 resolves inside the middle 250us bucket");
        c.True(cost.MaxUs >= 29_999, "the exact maximum survives histogram bucketing");
        c.Eq(1, cost.Over25Ms, "25ms hitches are counted");
        c.Eq(0, cost.Over50Ms, "a 30ms phase is not a 50ms hitch");
        c.Eq(0, cost.Over100Ms, "a 30ms phase is not a 100ms hitch");

        cost.AddElapsedTicks(TicksForUs(55_000));
        cost.AddElapsedTicks(TicksForUs(120_000));
        c.Eq(3, cost.Over25Ms, "every duration over 25ms is counted");
        c.Eq(2, cost.Over50Ms, "50ms hitch count uses its own threshold");
        c.Eq(1, cost.Over100Ms, "100ms hitch count uses its own threshold");

        cost.AddSample(TicksForUs(200), 64);
        cost.AddSample(TicksForUs(300), 128);
        c.Eq(2, cost.AllocationSamples, "allocation-enabled samples are counted separately");
        c.Eq(192L, cost.AllocatedBytes, "per-phase allocation deltas accumulate");
        c.Eq(128L, cost.MaxAllocatedBytes, "the worst single-call allocation is retained");
        c.Eq(96.0, cost.AvgAllocatedBytes, "average allocation uses measured samples only");

        c.Eq(-1L, LodPhaseCost.Start(trackAllocations: false).AllocatedBytes,
            "disabled allocation telemetry does not call the allocation counter");
        c.True(LodPhaseCost.Start(trackAllocations: true).AllocatedBytes >= 0,
            "enabled allocation telemetry captures the current-thread counter");

        var live = new LodPhaseCost();
        LodPhaseStart liveStart = LodPhaseCost.Start(trackAllocations: true);
        byte[] knownAllocation = new byte[4096];
        live.Add(liveStart);
        GC.KeepAlive(knownAllocation);
        c.Eq(1, live.AllocationSamples, "a live phase records one allocation sample");
        c.True(live.AllocatedBytes >= knownAllocation.Length,
            "a live phase observes managed bytes allocated between start and add");

        cost.Reset();
        c.Eq(0, cost.Calls, "reset clears sample count");
        c.Eq(0, cost.Over25Ms, "reset clears hitch counts");
        c.Eq(0.0, cost.P95Us, "an empty interval reports zero percentile");
        c.Eq(0.0, cost.MaxUs, "an empty interval reports zero maximum");
        c.Eq(0, cost.AllocationSamples, "reset clears allocation sample count");
        c.Eq(0L, cost.AllocatedBytes, "reset clears allocated bytes");
        c.Eq(0L, cost.MaxAllocatedBytes, "reset clears maximum allocation");

        FrameTimeline(c);
    }

    /// <summary>
    /// The frame timeline answers one question the per-phase costs cannot: was a slow frame
    /// ours? Every rule it uses is relative to the frames around it, so the same code has to
    /// behave at 400 FPS and at 60, and a world load has to not register as a hitch.
    ///
    /// Timestamps are supplied rather than read from the clock, so these are exact rather
    /// than approximately true on a fast machine.
    /// </summary>
    static void FrameTimeline(Check c)
    {
        var timeline = new LodFrameTimeline();
        long now = 1_000_000;

        // One frame at `intervalUs`, of which `modUs` is spent inside the mod.
        void Frame(double intervalUs, double modUs)
        {
            now += TicksForUs(intervalUs);
            long start = timeline.Begin(now);
            timeline.End(start, start + TicksForUs(modUs));
        }

        long first = timeline.Begin(now);
        timeline.End(first, first + TicksForUs(200));
        c.Eq(0, timeline.IntervalCost.Calls,
            "the first frame has nothing to measure an interval against");
        c.Eq(1, timeline.ModCost.Calls, "but our own share of it is still measured");

        // A steady 400 FPS client: 2.5ms frames, 200us of it ours.
        for (int i = 0; i < 400; i++) Frame(2_500, 200);
        c.Eq(400, timeline.IntervalCost.Calls, "every interval is measured");
        c.Eq(0, timeline.SlowFrames, "a steady client reports no spikes at all");
        // 2.5ms lands in the shared histogram's 250us band, which is exactly why the
        // excess-over-baseline histogram exists: a steady client's excess is zero, so a
        // hitch of a few hundred microseconds is visible there and invisible here.
        c.True(timeline.IntervalCost.P50Us > 2_500 && timeline.IntervalCost.P50Us <= 2_750,
            "the median interval is quantised to the 250us band that holds it");
        c.True(timeline.ExcessCost.P99Us <= 25,
            "a steady client runs at its own baseline, so its excess histogram is empty");

        // One frame takes four times as long, and our own work was ordinary during it.
        Frame(10_000, 200);
        c.Eq(1, timeline.SlowFrames, "a frame four times its neighbours is a spike");
        c.Eq(0, timeline.SlowFramesWithSlowMod,
            "and is not blamed on us when our own share of it was ordinary");
        c.True(timeline.WorstIntervalUs >= 10_000, "the worst frame is retained exactly");
        c.True(timeline.WorstIntervalModUs <= 250,
            "together with how much of that particular frame was ours");
        c.True(timeline.ExcessCost.MaxUs >= 7_000,
            "and the excess histogram carries how far over baseline it ran");

        // One where our own callback is what took the time.
        Frame(10_000, 4_000);
        c.Eq(2, timeline.SlowFrames, "the second spike is counted");
        c.Eq(1, timeline.SlowFramesWithSlowMod, "and this one is ours");

        // A world load is not a hitch. It must neither be counted nor drag the average up,
        // which would hide real spikes for the next several hundred frames.
        double averageBefore = timeline.AverageIntervalUs;
        Frame(2_000_000, 500);
        c.Eq(2, timeline.SlowFrames, "a two-second gap is not a frame spike");
        c.Eq(averageBefore, timeline.AverageIntervalUs,
            "and does not move the moving average at all");

        // A slower client: the same absolute jitter must not register once frames are 16ms.
        var slow = new LodFrameTimeline();
        long slowNow = 5_000_000;
        void SlowFrame(double intervalUs)
        {
            slowNow += TicksForUs(intervalUs);
            long start = slow.Begin(slowNow);
            slow.End(start, start + TicksForUs(1_000));
        }

        for (int i = 0; i < 200; i++) SlowFrame(16_600);
        for (int i = 0; i < 20; i++) SlowFrame(17_000);
        c.Eq(0, slow.SlowFrames,
            "400us of jitter is a spike at 400 FPS and noise at 60, and the rule knows it");

        int beforeReset = timeline.IntervalCost.Calls;
        c.True(beforeReset > 0, "there is something to reset");
        timeline.Reset();
        c.Eq(0, timeline.IntervalCost.Calls, "a report boundary clears the counts");
        c.Eq(0, timeline.SlowFrames, "and the spike tally");
        c.True(timeline.AverageIntervalUs > 0,
            "but keeps the moving average, or every interval would open with false spikes");

        Frame(2_500, 200);
        c.Eq(0, timeline.SlowFrames, "and the first frame after a report is not a spike");
    }

    static long TicksForUs(double microseconds) =>
        Math.Max(1, (long)Math.Round(microseconds * Stopwatch.Frequency / 1_000_000.0));
}
