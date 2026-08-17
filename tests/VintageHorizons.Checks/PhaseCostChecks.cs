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
    }

    static long TicksForUs(double microseconds) =>
        Math.Max(1, (long)Math.Round(microseconds * Stopwatch.Frequency / 1_000_000.0));
}
