using OpenTK.Graphics.OpenGL4;

namespace VintageHorizons;

/// <summary>
/// The dispatch half of culling, behind an interface for the same reason the draw backend is:
/// WHEN it runs relative to the upload and the draw is a correctness property, and pinning
/// that with a check requires something that can stand in for a GL program.
/// </summary>
internal interface ILodGpuCullDispatch
{
    bool Available { get; }

    bool Dispatch(
        float[] viewProjection,
        int hzbTexture,
        int screenWidth,
        int screenHeight,
        int levels,
        int count,
        float occlusionDepthBias,
        LodGpuCullBucket bucket,
        ReadOnlySpan<LodGpuCullIdentity> identities);
}

/// <summary>
/// The compute pass that turns a depth verdict into a draw that does not happen.
///
/// It owns the program and the uniforms and nothing else. The buffers it reads and writes are
/// the draw backend's own - boxes in, the live command buffer out - because the command buffer
/// has to be the very one the multi-draw sources from, and the backend is what owns it. So the
/// order is always: the backend binds and uploads, this dispatches, the backend barriers.
///
/// Failure is not an error state, in the same way nothing else in this renderer treats it as
/// one. A pass that cannot compile, or that raises a GL error, disables itself for the session
/// and every command keeps the instance count the CPU gave it - which is the unculled picture,
/// complete and correct and merely slower. There is deliberately no retry: a driver that
/// refused this program once will refuse it every frame, and asking again per frame turns one
/// failure into a stutter.
/// </summary>
internal sealed class LodGpuCullPass : ILodGpuCullDispatch, IDisposable
{
    const int LocalSize = 64;
    const int TelemetryBinding = 2;
    const int TelemetrySlotCount = 4;
    const int FramesBetweenTelemetrySamples = 30;

    readonly Action<string> warn;
    readonly LodGpuTelemetry? gpuTelemetry;
    readonly ILodGlStateApi stateApi = new LodOpenGlStateApi();
    int program;

    // Resolved once at link time rather than per dispatch. Ten glGetUniformLocation calls
    // three times a frame is not a measured cost, but the lookup is a driver-side string
    // comparison against the program's uniform table and the answer cannot change while the
    // program lives, so paying for it every frame buys nothing at all.
    int uniformViewProjection = -1;
    int uniformScreenWidth = -1;
    int uniformScreenHeight = -1;
    int uniformLevelCount = -1;
    int uniformSectionCount = -1;
    int uniformHzb = -1;
    int uniformOcclusionDepthBias = -1;
    int uniformTelemetryEnabled = -1;
    bool disabled;
    bool telemetryDisabled;
    int telemetryEpoch;
    readonly int[] telemetryBuffers = new int[TelemetrySlotCount];
    readonly IntPtr[] telemetryFences = new IntPtr[TelemetrySlotCount];
    readonly bool[] telemetryPending = new bool[TelemetrySlotCount];
    readonly LodGpuCullBucket[] telemetryBuckets = new LodGpuCullBucket[TelemetrySlotCount];
    readonly int[] telemetrySlotEpochs = new int[TelemetrySlotCount];
    readonly int[] framesSinceTelemetry =
        [int.MaxValue, int.MaxValue, int.MaxValue];
    readonly uint[] telemetryZeros = new uint[LodGpuCullTelemetryLayout.WordCount];
    readonly uint[] telemetryReadback = new uint[LodGpuCullTelemetryLayout.WordCount];
    readonly LodGpuCullStatistics[] telemetryStatistics =
        [new(), new(), new()];

    public LodGpuCullPass(Action<string> warn, LodGpuTelemetry? gpuTelemetry = null)
    {
        this.warn = warn;
        this.gpuTelemetry = gpuTelemetry;
    }

    public bool Available => program != 0 && !disabled;

    public string LastFailure { get; private set; } = "";

    /// <summary>Sections handed to the pass, and dispatches issued, since the last reset.</summary>
    public long Dispatches { get; private set; }
    public long SectionsTested { get; private set; }

    /// <summary>
    /// Compiles the program. Returns false and stays unavailable on any driver that refuses
    /// it, which costs the culling and nothing else.
    /// </summary>
    public bool TryCreate()
    {
        if (program != 0) return true;
        if (disabled) return false;

        int shader = 0;
        try
        {
            shader = GL.CreateShader(ShaderType.ComputeShader);
            GL.ShaderSource(shader, LodHzbClassifier.CullSource);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
            if (compiled == 0)
            {
                return Disable("the cull compute shader did not compile: "
                    + GL.GetShaderInfoLog(shader));
            }

            int created = GL.CreateProgram();
            GL.AttachShader(created, shader);
            GL.LinkProgram(created);
            GL.GetProgram(created, GetProgramParameterName.LinkStatus, out int linked);
            if (linked == 0)
            {
                string log = GL.GetProgramInfoLog(created);
                GL.DeleteProgram(created);
                return Disable("the cull compute program did not link: " + log);
            }

            program = created;
            uniformViewProjection = GL.GetUniformLocation(program, "viewProjection");
            uniformScreenWidth = GL.GetUniformLocation(program, "screenWidth");
            uniformScreenHeight = GL.GetUniformLocation(program, "screenHeight");
            uniformLevelCount = GL.GetUniformLocation(program, "levelCount");
            uniformSectionCount = GL.GetUniformLocation(program, "sectionCount");
            uniformHzb = GL.GetUniformLocation(program, "hzb");
            uniformOcclusionDepthBias = GL.GetUniformLocation(program, "occlusionDepthBias");
            uniformTelemetryEnabled = GL.GetUniformLocation(program, "telemetryEnabled");
            LastFailure = "";
            return true;
        }
        catch (Exception e)
        {
            return Disable("the cull compute program failed: " + e.Message);
        }
        finally
        {
            try { if (shader != 0) GL.DeleteShader(shader); }
            catch { /* the program holds its own reference once linked */ }
        }
    }

    /// <summary>
    /// Dispatches over <paramref name="count"/> commands. The caller must already have bound
    /// the box and command buffers; this binds only the program and the pyramid.
    ///
    /// Returns false when nothing was dispatched, which always means every command keeps the
    /// instance count the CPU gave it.
    /// </summary>
    public bool Dispatch(
        float[] viewProjection,
        int hzbTexture,
        int screenWidth,
        int screenHeight,
        int levels,
        int count,
        float occlusionDepthBias,
        LodGpuCullBucket bucket,
        ReadOnlySpan<LodGpuCullIdentity> identities)
    {
        if (!Available) return false;
        if (hzbTexture == 0 || count <= 0) return false;
        if (viewProjection == null || viewProjection.Length < 16) return false;
        if (levels <= 0 || screenWidth <= 0 || screenHeight <= 0) return false;
        if (!float.IsFinite(occlusionDepthBias) || occlusionDepthBias < 0f) return false;

        // Program and texture unit zero are the only shared state this touches; the caller
        // owns the buffer bindings and puts them back.
        LodGlStateSnapshot state = LodGlStateGuard.Capture(
            stateApi,
            LodGlStateMask.Program | LodGlStateMask.ShaderStorageBuffer
                | LodGlStateMask.Texture2DUnit0);

        try
        {
            ReadReadyTelemetry();
            int telemetrySlot = BeginTelemetrySample(bucket);

            GL.UseProgram(program);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, hzbTexture);

            GL.UniformMatrix4(uniformViewProjection, 1, false, viewProjection);
            GL.Uniform1(uniformScreenWidth, screenWidth);
            GL.Uniform1(uniformScreenHeight, screenHeight);
            GL.Uniform1(uniformLevelCount, levels);
            GL.Uniform1(uniformSectionCount, count);
            GL.Uniform1(uniformHzb, 0);
            GL.Uniform1(uniformOcclusionDepthBias, occlusionDepthBias);
            GL.Uniform1(uniformTelemetryEnabled, telemetrySlot >= 0 ? 1 : 0);

            bool timing = gpuTelemetry?.BeginCull(bucket) ?? false;
            bool dispatched = false;
            try
            {
                GL.DispatchCompute(GroupsFor(count), 1, 1);
                dispatched = true;
            }
            finally
            {
                if (timing)
                {
                    if (dispatched) gpuTelemetry!.EndCull(bucket);
                    else gpuTelemetry!.DiscardCull(bucket);
                }
            }

            ErrorCode error = GL.GetError();
            if (error != ErrorCode.NoError) return Disable("culling raised " + error);

            if (telemetrySlot >= 0) FinishTelemetrySample(telemetrySlot, bucket);

            Dispatches++;
            SectionsTested += count;
            return true;
        }
        catch (Exception e)
        {
            return Disable("culling failed: " + e.Message);
        }
        finally
        {
            if (!LodGlStateGuard.TryRestore(stateApi, state, out string failure))
                Disable("the cull pass did not restore GL state exactly: " + failure);
        }
    }

    int BeginTelemetrySample(LodGpuCullBucket bucket)
    {
        int bucketIndex = (int)bucket;
        if ((uint)bucketIndex >= (uint)framesSinceTelemetry.Length) return -1;
        if (framesSinceTelemetry[bucketIndex] < int.MaxValue)
            framesSinceTelemetry[bucketIndex]++;
        if (telemetryDisabled
            || framesSinceTelemetry[bucketIndex] < FramesBetweenTelemetrySamples)
            return -1;

        int slot = Array.FindIndex(telemetryPending, pending => !pending);
        if (slot < 0) return -1;

        try
        {
            if (telemetryBuffers[slot] == 0)
            {
                telemetryBuffers[slot] = GL.GenBuffer();
                if (telemetryBuffers[slot] == 0)
                    return DisableTelemetry("the driver refused a counter buffer");
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, telemetryBuffers[slot]);
                GL.BufferData(BufferTarget.ShaderStorageBuffer,
                    LodGpuCullTelemetryLayout.WordCount * sizeof(uint), IntPtr.Zero,
                    BufferUsageHint.StreamRead);
            }

            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, telemetryBuffers[slot]);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero,
                LodGpuCullTelemetryLayout.WordCount * sizeof(uint), telemetryZeros);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
                TelemetryBinding, telemetryBuffers[slot]);

            ErrorCode error = GL.GetError();
            return error == ErrorCode.NoError
                ? slot
                : DisableTelemetry("preparing counters raised " + error);
        }
        catch (Exception e)
        {
            return DisableTelemetry("preparing counters failed: " + e.Message);
        }
    }

    void FinishTelemetrySample(int slot, LodGpuCullBucket bucket)
    {
        try
        {
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit
                | MemoryBarrierFlags.BufferUpdateBarrierBit);
            IntPtr fence = GL.FenceSync(
                SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
            if (fence == IntPtr.Zero)
            {
                DisableTelemetry("the driver refused a sample fence");
                return;
            }

            telemetryFences[slot] = fence;
            telemetryBuckets[slot] = bucket;
            telemetrySlotEpochs[slot] = telemetryEpoch;
            telemetryPending[slot] = true;
            framesSinceTelemetry[(int)bucket] = 0;
            ErrorCode error = GL.GetError();
            if (error != ErrorCode.NoError)
                DisableTelemetry("finishing a sample raised " + error);
        }
        catch (Exception e)
        {
            DisableTelemetry("fencing a sample failed: " + e.Message);
        }
    }

    void ReadReadyTelemetry()
    {
        if (telemetryDisabled) return;
        try
        {
            for (int slot = 0; slot < TelemetrySlotCount; slot++)
            {
                if (!telemetryPending[slot]) continue;
                WaitSyncStatus status = GL.ClientWaitSync(
                    telemetryFences[slot], ClientWaitSyncFlags.None, 0);
                if (status != WaitSyncStatus.AlreadySignaled
                    && status != WaitSyncStatus.ConditionSatisfied)
                {
                    if (status == WaitSyncStatus.WaitFailed)
                        DisableTelemetry("waiting for a sample fence failed");
                    continue;
                }

                GL.DeleteSync(telemetryFences[slot]);
                telemetryFences[slot] = IntPtr.Zero;
                telemetryPending[slot] = false;
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, telemetryBuffers[slot]);
                GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero,
                    LodGpuCullTelemetryLayout.WordCount * sizeof(uint), telemetryReadback);
                if (telemetrySlotEpochs[slot] == telemetryEpoch)
                    telemetryStatistics[(int)telemetryBuckets[slot]].Add(telemetryReadback);
            }
        }
        catch (Exception e)
        {
            DisableTelemetry("reading counters failed: " + e.Message);
        }
    }

    /// <summary>Workgroups needed to cover every command, rounding up.</summary>
    public static int GroupsFor(int count) =>
        count <= 0 ? 0 : (count + LocalSize - 1) / LocalSize;

    public void ResetInterval()
    {
        Dispatches = 0;
        SectionsTested = 0;
        telemetryEpoch++;
        Array.Fill(framesSinceTelemetry, int.MaxValue);
        foreach (LodGpuCullStatistics statistics in telemetryStatistics) statistics.Reset();
    }

    public string DescribeLiveTelemetry()
    {
        if (telemetryDisabled) return "live command counters unavailable";
        var reports = new List<string>();
        if (telemetryStatistics[(int)LodGpuCullBucket.General].Samples > 0)
            reports.Add(telemetryStatistics[(int)LodGpuCullBucket.General]
                .Describe("ordinary live cull"));
        if (telemetryStatistics[(int)LodGpuCullBucket.SplitNear].Samples > 0)
            reports.Add(telemetryStatistics[(int)LodGpuCullBucket.SplitNear]
                .Describe("split near live cull"));
        if (telemetryStatistics[(int)LodGpuCullBucket.SplitFar].Samples > 0)
            reports.Add(telemetryStatistics[(int)LodGpuCullBucket.SplitFar]
                .Describe("split far live cull"));
        return reports.Count > 0
            ? string.Join(Environment.NewLine, reports)
            : "live command counters awaiting their first asynchronous sample";
    }

    public string Describe() =>
        disabled ? "failed: " + LastFailure
        : program == 0 ? "not created"
        : Dispatches == 0 ? "ready, nothing culled yet"
        : $"{Dispatches} dispatches over {SectionsTested} commands";

    bool Disable(string reason)
    {
        if (disabled) return false;
        disabled = true;
        LastFailure = reason;
        warn("[VintageHorizons] GPU cull disabled for this session; cached terrain is drawn "
            + "without depth suppression, which is complete and merely slower: " + reason);
        Release();
        return false;
    }

    int DisableTelemetry(string reason)
    {
        if (!telemetryDisabled)
        {
            telemetryDisabled = true;
            warn("[VintageHorizons] Live GPU-cull counters disabled; culling and drawing "
                + "continue unchanged: " + reason);
            ReleaseTelemetry();
        }
        return -1;
    }

    void ReleaseTelemetry()
    {
        for (int i = 0; i < TelemetrySlotCount; i++)
        {
            try
            {
                if (telemetryFences[i] != IntPtr.Zero) GL.DeleteSync(telemetryFences[i]);
                if (telemetryBuffers[i] != 0) GL.DeleteBuffer(telemetryBuffers[i]);
            }
            catch { /* context teardown must continue */ }
            telemetryFences[i] = IntPtr.Zero;
            telemetryBuffers[i] = 0;
            telemetryPending[i] = false;
        }
    }

    void Release()
    {
        try { if (program != 0) GL.DeleteProgram(program); }
        catch { /* Context teardown must continue. */ }
        program = 0;
    }

    public void Dispose()
    {
        ReleaseTelemetry();
        Release();
    }
}
