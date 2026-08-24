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

    public void End() => Finish(keep: true);

    /// <summary>
    /// Ends the query but throws its result away.
    ///
    /// For a pass that was timed and then did not happen. The GL query has already started and
    /// must be closed, but recording it would add a near-zero sample to an average of real
    /// work. On 2026-08-24 the depth pyramid reported 4.8us over 256,368 timed builds when only
    /// 50,733 builds had actually occurred - the other 205,635 were frames with the window
    /// minimised, and they dragged a genuine 24us figure down by a factor of five. The gate for
    /// this whole phase is that number.
    ///
    /// Discarding reuses the ring's existing stale-epoch path: the slot is still polled and
    /// freed, its result simply never reaches the total.
    /// </summary>
    public void Discard() => Finish(keep: false);

    void Finish(bool keep)
    {
        if (activeSlot < 0) return;
        api.EndTimeElapsed();
        ref Slot slot = ref slots[activeSlot];
        slot.Pending = true;
        slot.Epoch = keep ? epoch : long.MinValue;
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
    readonly LodGpuTimerRing splitNearTimer;
    readonly LodGpuTimerRing splitFarTimer;
    readonly LodGpuTimerRing waterTimer;
    readonly LodGpuTimerRing hzbTimer;
    readonly LodGpuTimerRing splitHzbTimer;
    readonly LodGpuTimerRing classifyTimer;
    bool probeAttempted;
    bool timingFailureReported;

    public bool TimingRequested { get; private set; }
    public bool RuntimeValidationRequested { get; private set; }
    public bool ProbeAttempted => probeAttempted;
    public bool TimingActive { get; private set; }
    public LodGpuCapabilityFacts Capabilities { get; private set; }
    public LodGpuRuntimeValidation RuntimeValidation { get; private set; }
    public LodGpuDepthCopyValidation DepthCopyValidation { get; private set; }
    public LodGpuDepthFacts Depth { get; private set; }
    public LodGpuPathDecision Decision { get; private set; }
    public LodPhaseCost OpaqueCost;
    public LodPhaseCost SplitNearCost;
    public LodPhaseCost SplitFarCost;
    public LodPhaseCost WaterCost;

    /// <summary>
    /// GPU time to copy the depth buffer and reduce the whole pyramid. This is the number
    /// Phase 4's gate is written against: the pyramid has to cost less than the drawing it
    /// removes, and CPU time cannot answer that because none of the work is on the CPU.
    /// </summary>
    public LodPhaseCost HzbGpuCost;
    public LodPhaseCost SplitHzbGpuCost;

    /// <summary>
    /// GPU time for the classification dispatch, kept apart from the pyramid build because
    /// the two scale with completely different things. The build scales with the screen; the
    /// dispatch scales with the section count AND with the square of the sampling width, and
    /// widening that from two texels to eight multiplied its texture fetches by about nine.
    /// A single combined number could absorb that entirely and report nothing.
    /// </summary>
    public LodPhaseCost ClassifyGpuCost;

    public int PendingResults =>
        opaqueTimer.PendingCount + splitNearTimer.PendingCount + splitFarTimer.PendingCount
        + waterTimer.PendingCount + hzbTimer.PendingCount + splitHzbTimer.PendingCount
        + classifyTimer.PendingCount;
    public int UnavailableSlots =>
        opaqueTimer.UnavailableSlots + splitNearTimer.UnavailableSlots
        + splitFarTimer.UnavailableSlots + waterTimer.UnavailableSlots + hzbTimer.UnavailableSlots
        + splitHzbTimer.UnavailableSlots
        + classifyTimer.UnavailableSlots;
    public int TargetBusy => opaqueTimer.TargetBusy + splitNearTimer.TargetBusy
        + splitFarTimer.TargetBusy + waterTimer.TargetBusy + hzbTimer.TargetBusy
        + splitHzbTimer.TargetBusy
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
        splitNearTimer = new LodGpuTimerRing(timerApi);
        splitFarTimer = new LodGpuTimerRing(timerApi);
        waterTimer = new LodGpuTimerRing(timerApi);
        hzbTimer = new LodGpuTimerRing(timerApi);
        splitHzbTimer = new LodGpuTimerRing(timerApi);
        classifyTimer = new LodGpuTimerRing(timerApi);
    }

    /// <summary>
    /// Arms the delayed GPU timers on a session that started without
    /// VINTAGEHORIZONS_GPU_STATS.
    ///
    /// This exists because the only number that can answer a depth-phase gate is GPU time,
    /// and until now the only way to obtain it was to restart the game under an environment
    /// variable the benchmark harness sets. An ordinary session therefore reported 0.0us for
    /// the pyramid, the classify and the cull, which reads exactly like "it costs nothing"
    /// rather than "nobody measured". Two playtests have already been spent on instruments
    /// that could not see what they claimed to (G72).
    ///
    /// Whether the timers actually arm is the card's decision, not this one: a driver that
    /// does not advertise timer queries stays unmeasured and everything else is unchanged.
    /// Returns whether timing is on NOW; false with <see cref="TimingRequested"/> set means
    /// the capability probe has not run yet and the next frame will decide.
    /// </summary>
    public bool RequestTiming()
    {
        TimingRequested = true;
        if (TimingActive) return true;

        // Not probed yet: BeginFrame probes on the render thread, where GL calls are legal,
        // and applies the request there. Doing it here would issue GL from the chat thread.
        if (!probeAttempted) return false;

        // Timing that already failed once is not retried. A ring that threw will throw
        // again, and re-arming it per request turns one failure into a repeated one.
        if (timingFailureReported) return false;

        TimingActive = Decision.TimerQueriesAvailable;
        return TimingActive;
    }

    /// <summary>
    /// One line saying whether the GPU-time figures in a report mean anything. Written for
    /// the person reading the report rather than for a log parser, because a zero with no
    /// explanation beside it is the failure this whole path exists to prevent.
    /// </summary>
    public string DescribeTiming() =>
        TimingActive ? "on"
        : !TimingRequested ? "off - nothing has asked for it"
        : timingFailureReported ? "off - the timers failed and are not retried this session"
        : !probeAttempted ? "asked for; it arms on the next frame"
        : "unavailable - this driver does not advertise timer queries";

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
            splitNearTimer.Poll(ref SplitNearCost);
            splitFarTimer.Poll(ref SplitFarCost);
            waterTimer.Poll(ref WaterCost);
            hzbTimer.Poll(ref HzbGpuCost);
            splitHzbTimer.Poll(ref SplitHzbGpuCost);
            classifyTimer.Poll(ref ClassifyGpuCost);
        }
        catch (Exception e)
        {
            DisableTiming(e);
        }
    }

    public bool BeginOpaque() => Begin(opaqueTimer);
    public void EndOpaque() => End(opaqueTimer);
    public bool BeginSplitNear() => Begin(splitNearTimer);
    public void EndSplitNear() => End(splitNearTimer);
    public bool BeginSplitFar() => Begin(splitFarTimer);
    public void EndSplitFar() => End(splitFarTimer);
    public bool BeginWater() => Begin(waterTimer);
    public void EndWater() => End(waterTimer);
    public bool BeginHzb() => Begin(hzbTimer);
    public void EndHzb() => End(hzbTimer);
    public bool BeginSplitHzb() => Begin(splitHzbTimer);
    public void EndSplitHzb() => End(splitHzbTimer);

    /// <summary>Closes the pyramid's timer without recording it, for a build that did not run.</summary>
    public void DiscardHzb()
    {
        if (!TimingActive) return;
        try { hzbTimer.Discard(); }
        catch (Exception e) { DisableTiming(e); }
    }
    public void DiscardSplitHzb()
    {
        if (!TimingActive) return;
        try { splitHzbTimer.Discard(); }
        catch (Exception e) { DisableTiming(e); }
    }
    public bool BeginClassify() => Begin(classifyTimer);
    public void EndClassify() => End(classifyTimer);

    public void ResetInterval()
    {
        OpaqueCost.Reset();
        SplitNearCost.Reset();
        SplitFarCost.Reset();
        WaterCost.Reset();
        HzbGpuCost.Reset();
        SplitHzbGpuCost.Reset();
        ClassifyGpuCost.Reset();
        opaqueTimer.ResetInterval();
        splitNearTimer.ResetInterval();
        splitFarTimer.ResetInterval();
        waterTimer.ResetInterval();
        hzbTimer.ResetInterval();
        splitHzbTimer.ResetInterval();
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
        splitNearTimer.Dispose();
        splitFarTimer.Dispose();
        waterTimer.Dispose();
        hzbTimer.Dispose();
        splitHzbTimer.Dispose();
        classifyTimer.Dispose();
        TimingActive = false;
    }
}
