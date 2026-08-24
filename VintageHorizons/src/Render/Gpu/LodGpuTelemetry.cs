using System.Diagnostics;
using OpenTK.Graphics.OpenGL4;

namespace VintageHorizons;

internal interface ILodGpuTimerApi
{
    int CreateQuery();
    bool TimeElapsedTargetIsFree();
    void BeginTimeElapsed(int queryId);
    void EndTimeElapsed();
    bool TryGetResultNanoseconds(int queryId, out long nanoseconds);
    void DeleteQuery(int queryId);
}

internal sealed class LodOpenGlTimerApi : ILodGpuTimerApi
{
    public int CreateQuery() => GL.GenQuery();

    public bool TimeElapsedTargetIsFree()
    {
        GL.GetQuery(QueryTarget.TimeElapsed, GetQueryParam.CurrentQuery, out int current);
        return current == 0;
    }

    public void BeginTimeElapsed(int queryId) => GL.BeginQuery(QueryTarget.TimeElapsed, queryId);
    public void EndTimeElapsed() => GL.EndQuery(QueryTarget.TimeElapsed);

    public bool TryGetResultNanoseconds(int queryId, out long nanoseconds)
    {
        GL.GetQueryObject(queryId, GetQueryObjectParam.QueryResultAvailable, out int available);
        if (available == 0)
        {
            nanoseconds = 0;
            return false;
        }

        GL.GetQueryObject(queryId, GetQueryObjectParam.QueryResult, out nanoseconds);
        return true;
    }

    public void DeleteQuery(int queryId) => GL.DeleteQuery(queryId);
}

/// <summary>
/// A fixed delayed query ring. Pending slots are polled for availability and never waited
/// on; when the GPU falls more than the ring behind, timing is skipped for that pass.
/// </summary>
internal sealed class LodGpuTimerRing : IDisposable
{
    struct Slot
    {
        public int QueryId;
        public bool Pending;
        public long Epoch;
    }

    readonly ILodGpuTimerApi api;
    readonly Slot[] slots;
    int nextSlot;
    int activeSlot = -1;
    long epoch = 1;

    public int UnavailableSlots { get; private set; }
    public int TargetBusy { get; private set; }
    public int PendingCount => slots.Count(slot => slot.Pending);

    public LodGpuTimerRing(ILodGpuTimerApi api, int slotCount = 4)
    {
        if (slotCount < 2) throw new ArgumentOutOfRangeException(nameof(slotCount));
        this.api = api;
        slots = new Slot[slotCount];
    }

    public bool TryBegin()
    {
        if (activeSlot >= 0) throw new InvalidOperationException("GPU timer already active");
        if (!api.TimeElapsedTargetIsFree())
        {
            TargetBusy++;
            return false;
        }

        ref Slot slot = ref slots[nextSlot];
        if (slot.Pending)
        {
            UnavailableSlots++;
            return false;
        }

        if (slot.QueryId == 0) slot.QueryId = api.CreateQuery();
        api.BeginTimeElapsed(slot.QueryId);
        activeSlot = nextSlot;
        return true;
    }

    public void End()
    {
        if (activeSlot < 0) return;
        api.EndTimeElapsed();
        ref Slot slot = ref slots[activeSlot];
        slot.Pending = true;
        slot.Epoch = epoch;
        nextSlot = (activeSlot + 1) % slots.Length;
        activeSlot = -1;
    }

    public void Poll(ref LodPhaseCost cost)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            ref Slot slot = ref slots[i];
            if (!slot.Pending
                || !api.TryGetResultNanoseconds(slot.QueryId, out long nanoseconds)) continue;

            slot.Pending = false;
            if (slot.Epoch == epoch)
                cost.AddElapsedTicks(NanosecondsToStopwatchTicks(nanoseconds));
        }
    }

    public void ResetInterval()
    {
        epoch++;
        UnavailableSlots = 0;
        TargetBusy = 0;
    }

    internal static long NanosecondsToStopwatchTicks(long nanoseconds)
    {
        if (nanoseconds <= 0) return 0;
        double ticks = nanoseconds * (double)Stopwatch.Frequency / 1_000_000_000.0;
        return ticks >= long.MaxValue ? long.MaxValue : (long)Math.Round(ticks);
    }

    public void Dispose()
    {
        if (activeSlot >= 0)
        {
            try { api.EndTimeElapsed(); }
            catch { /* Context teardown must continue. */ }
            activeSlot = -1;
        }

        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].QueryId == 0) continue;
            try { api.DeleteQuery(slots[i].QueryId); }
            catch { /* Context teardown must continue. */ }
            slots[i].QueryId = 0;
            slots[i].Pending = false;
        }
    }
}

/// <summary>Phase 0 capability report and optional delayed GPU-pass timers.</summary>
internal sealed class LodGpuTelemetry : IDisposable
{
    readonly Action<string> log;
    readonly Action<string> warn;
    readonly LodGpuTimerRing opaqueTimer;
    readonly LodGpuTimerRing waterTimer;
    readonly LodGpuTimerRing hzbTimer;
    readonly LodGpuTimerRing classifyTimer;
    bool probeAttempted;
    bool timingFailureReported;

    public bool TimingRequested { get; }
    public bool RuntimeValidationRequested { get; private set; }
    public bool ProbeAttempted => probeAttempted;
    public bool TimingActive { get; private set; }
    public LodGpuCapabilityFacts Capabilities { get; private set; }
    public LodGpuRuntimeValidation RuntimeValidation { get; private set; }
    public LodGpuDepthCopyValidation DepthCopyValidation { get; private set; }
    public LodGpuDepthFacts Depth { get; private set; }
    public LodGpuPathDecision Decision { get; private set; }
    public LodPhaseCost OpaqueCost;
    public LodPhaseCost WaterCost;

    /// <summary>
    /// GPU time to copy the depth buffer and reduce the whole pyramid. This is the number
    /// Phase 4's gate is written against: the pyramid has to cost less than the drawing it
    /// removes, and CPU time cannot answer that because none of the work is on the CPU.
    /// </summary>
    public LodPhaseCost HzbGpuCost;

    /// <summary>
    /// GPU time for the classification dispatch, kept apart from the pyramid build because
    /// the two scale with completely different things. The build scales with the screen; the
    /// dispatch scales with the section count AND with the square of the sampling width, and
    /// widening that from two texels to eight multiplied its texture fetches by about nine.
    /// A single combined number could absorb that entirely and report nothing.
    /// </summary>
    public LodPhaseCost ClassifyGpuCost;

    public int PendingResults =>
        opaqueTimer.PendingCount + waterTimer.PendingCount + hzbTimer.PendingCount
        + classifyTimer.PendingCount;
    public int UnavailableSlots =>
        opaqueTimer.UnavailableSlots + waterTimer.UnavailableSlots + hzbTimer.UnavailableSlots
        + classifyTimer.UnavailableSlots;
    public int TargetBusy => opaqueTimer.TargetBusy + waterTimer.TargetBusy + hzbTimer.TargetBusy
        + classifyTimer.TargetBusy;

    public LodGpuTelemetry(
        bool timingRequested,
        bool runtimeValidationRequested,
        Action<string> log,
        Action<string> warn,
        ILodGpuTimerApi? timerApi = null)
    {
        TimingRequested = timingRequested;
        RuntimeValidationRequested = runtimeValidationRequested || timingRequested;
        this.log = log;
        this.warn = warn;
        timerApi ??= new LodOpenGlTimerApi();
        opaqueTimer = new LodGpuTimerRing(timerApi);
        waterTimer = new LodGpuTimerRing(timerApi);
        hzbTimer = new LodGpuTimerRing(timerApi);
        classifyTimer = new LodGpuTimerRing(timerApi);
    }

    /// <summary>
    /// Asks for the runtime resource probes on a context that started without them. The
    /// probes themselves must run on the render thread, so this only records the request;
    /// the next frame performs it. Nothing is re-probed once validation has succeeded.
    /// </summary>
    public void RequestRuntimeValidation()
    {
        if (RuntimeValidationRequested && probeAttempted && RuntimeValidation.Succeeded) return;
        RuntimeValidationRequested = true;
        probeAttempted = false;
    }

    public void BeginFrame()
    {
        if (!probeAttempted) ProbeAndReport();
        if (!TimingActive) return;

        try
        {
            opaqueTimer.Poll(ref OpaqueCost);
            waterTimer.Poll(ref WaterCost);
            hzbTimer.Poll(ref HzbGpuCost);
            classifyTimer.Poll(ref ClassifyGpuCost);
        }
        catch (Exception e)
        {
            DisableTiming(e);
        }
    }

    public bool BeginOpaque() => Begin(opaqueTimer);
    public void EndOpaque() => End(opaqueTimer);
    public bool BeginWater() => Begin(waterTimer);
    public void EndWater() => End(waterTimer);
    public bool BeginHzb() => Begin(hzbTimer);
    public void EndHzb() => End(hzbTimer);
    public bool BeginClassify() => Begin(classifyTimer);
    public void EndClassify() => End(classifyTimer);

    public void ResetInterval()
    {
        OpaqueCost.Reset();
        WaterCost.Reset();
        HzbGpuCost.Reset();
        ClassifyGpuCost.Reset();
        opaqueTimer.ResetInterval();
        waterTimer.ResetInterval();
        hzbTimer.ResetInterval();
        classifyTimer.ResetInterval();
    }

    void ProbeAndReport()
    {
        probeAttempted = true;
        try
        {
            Capabilities = LodGpuCapabilities.Probe();
            RuntimeValidation = RuntimeValidationRequested
                ? LodGpuRuntimeProbe.Probe(Capabilities)
                : new LodGpuRuntimeValidation(
                    false, false, LodGpuRuntimeProbe.ProbeValue, 0,
                    "runtime resource validation was not requested");
            Capabilities = Capabilities with
            {
                RequiredEntryPointsValidated =
                    RuntimeValidation.RequiredEntryPointsValidated,
                MinimalComputeValidated = RuntimeValidation.MinimalComputeValidated,
            };
            Depth = LodGpuCapabilities.ProbeActiveDepth();
            DepthCopyValidation = RuntimeValidationRequested && RuntimeValidation.Succeeded
                ? LodGpuDepthCopyProbe.Probe(Depth)
                : new LodGpuDepthCopyValidation(
                    false, false, false, 0, 0, 0, 0,
                    RuntimeValidationRequested
                        ? "compute/SSBO validation did not pass"
                        : "runtime resource validation was not requested");
            Depth = Depth with { CopyValidated = DepthCopyValidation.Succeeded };
            Decision = LodGpuCapabilityPolicy.Evaluate(Capabilities, Depth);
            TimingActive = TimingRequested && Decision.TimerQueriesAvailable;

            string depth = string.IsNullOrEmpty(Depth.FailureReason)
                ? $"FBO draw/read {Depth.DrawFramebuffer}/{Depth.ReadFramebuffer}, "
                  + $"{Depth.AttachmentType} {Depth.AttachmentName}, {Depth.DepthBits}-bit, "
                  + $"{Depth.Samples}x, {Depth.DepthFunction} clear {Depth.ClearDepth:0.###}, {Depth.Convention}"
                : "probe failed: " + Depth.FailureReason;
            log($"[VintageHorizons] GPU probe: {Capabilities.Vendor} {Capabilities.Renderer}; "
                + $"GL {Capabilities.Version}, GLSL {Capabilities.ShadingLanguageVersion}; "
                + $"timer {(Decision.TimerQueriesAvailable ? "yes" : "no")}, "
                + $"regional MDI advertised {(Decision.RegionalIndirectAdvertised ? "yes" : "no")}, "
                + $"HZB advertised {(Decision.HierarchicalDepthAdvertised ? "yes" : "no")}; "
                + $"advanced entry points {(Capabilities.RequiredEntryPointsValidated ? "validated" : "unavailable")}, "
                + $"compute/SSBO {(Capabilities.MinimalComputeValidated ? "validated" : "failed")}; "
                + $"private depth copy/mips {(DepthCopyValidation.Succeeded
                    ? $"validated {DepthCopyValidation.Width}x{DepthCopyValidation.Height}/"
                        + $"{DepthCopyValidation.MipLevels} levels/0x{DepthCopyValidation.InternalFormat:X}"
                    : "not validated")}; "
                + $"depth {depth}; GPU timing {(TimingActive ? "on" : "off")}. "
                + $"Fast path remains legacy: {Decision.Reason}."
                + (string.IsNullOrEmpty(RuntimeValidation.FailureReason)
                    ? ""
                    : " Runtime probe: " + RuntimeValidation.FailureReason + ".")
                + (string.IsNullOrEmpty(DepthCopyValidation.FailureReason)
                    ? ""
                    : " Depth probe: " + DepthCopyValidation.FailureReason + "."));
        }
        catch (Exception e)
        {
            TimingActive = false;
            warn("[VintageHorizons] GPU capability probe failed; legacy rendering remains active: "
                + e.Message);
        }
    }

    bool Begin(LodGpuTimerRing timer)
    {
        if (!TimingActive) return false;
        try { return timer.TryBegin(); }
        catch (Exception e)
        {
            DisableTiming(e);
            return false;
        }
    }

    void End(LodGpuTimerRing timer)
    {
        if (!TimingActive) return;
        try { timer.End(); }
        catch (Exception e) { DisableTiming(e); }
    }

    void DisableTiming(Exception e)
    {
        TimingActive = false;
        if (timingFailureReported) return;
        timingFailureReported = true;
        warn("[VintageHorizons] Delayed GPU timing disabled; rendering is unchanged: " + e.Message);
    }

    public void Dispose()
    {
        opaqueTimer.Dispose();
        waterTimer.Dispose();
        hzbTimer.Dispose();
        classifyTimer.Dispose();
        TimingActive = false;
    }
}
