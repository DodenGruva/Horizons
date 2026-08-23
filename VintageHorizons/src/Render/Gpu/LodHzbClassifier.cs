using OpenTK.Graphics.OpenGL4;

namespace VintageHorizons;

/// <summary>
/// One frame's shadow-mode verdict counts. Nothing acts on these; they exist so the phase
/// can be argued from measurement rather than from the source of the shader.
/// </summary>
internal readonly record struct LodHzbVerdicts(
    int Sections,
    int Occluded,
    int Visible,
    int FailedOpen,
    int Background)
{
    public double OccludedFraction => Sections > 0 ? Occluded / (double)Sections : 0.0;

    /// <summary>
    /// Share of the not-hidden verdicts that were refused only because the box overlapped
    /// background. This is the number that says whether whole sections are simply too
    /// coarse a unit to test, which would make cluster subdivision a prerequisite rather
    /// than a later optimisation.
    /// </summary>
    public double BackgroundFraction
    {
        get
        {
            int notHidden = Visible + Background;
            return notHidden > 0 ? Background / (double)notHidden : 0.0;
        }
    }

    public LodHzbVerdicts Add(in LodHzbVerdicts other) => new(
        Sections + other.Sections,
        Occluded + other.Occluded,
        Visible + other.Visible,
        FailedOpen + other.FailedOpen,
        Background + other.Background);
}

/// <summary>
/// Asks the graphics card, once per frame, which cached sections are entirely behind what
/// has already been drawn.
///
/// The CPU cannot answer this. The sections most worth skipping are the far ones, and a
/// section 20 km out is about four pixels tall - which needs the FINEST pyramid levels, and
/// a full-resolution depth level is 14 MB per frame to bring back, on a path that stalls
/// the CPU against the card. So the card decides and returns one word per section instead:
/// a few kilobytes, read a frame late, which is ample for a shadow-mode count.
///
/// The GLSL below mirrors <see cref="LodHzbProjection"/> statement for statement, and that
/// is deliberate: the C# version carries the tests, including every fail-open case Phase 4's
/// gate names. A divergence between the two is a bug in the shader, not a difference of
/// opinion, which is the same arrangement `hzbreduce` has with `LodHzbReference`.
///
/// Nothing here influences drawing. It classifies, counts, and stops.
/// </summary>
internal sealed class LodHzbClassifier : IDisposable
{
    public const uint VerdictVisible = 0u;
    public const uint VerdictOccluded = 1u;
    public const uint VerdictFailedOpen = 2u;
    public const uint VerdictBackground = 3u;

    const int LocalSize = 64;
    const int BytesPerBox = 32;     // two vec4: camera-relative min, max
    const int BytesPerResult = 4;

    // Embedded rather than shipped as an asset. The engine's shader pipeline owns
    // everything under assets/.../shaders and compiles it as a draw program; a compute
    // program does not fit that shape, and the capability probe already creates one
    // through raw GL. Keeping the source here also keeps it beside the C# it mirrors.
    const string ComputeSource = @"#version 430

layout(local_size_x = 64) in;

layout(std430, binding = 0) readonly buffer Boxes { vec4 boxes[]; };
layout(std430, binding = 1) writeonly buffer Results { uint results[]; };

uniform mat4 viewProjection;
uniform ivec2 screenSize;
uniform int levelCount;
uniform int sectionCount;
uniform sampler2D hzb;

const uint VERDICT_VISIBLE = 0u;
const uint VERDICT_OCCLUDED = 1u;
const uint VERDICT_FAILED_OPEN = 2u;
// Not hidden, and specifically because the screen region this box covers includes
// background - sky, or anything else nothing was drawn over. The depth buffer holds the
// clear value there, the pyramid takes the farthest of what it covers, and nothing can be
// farther than that. So one sky pixel anywhere in the rectangle makes the box unhideable,
// however deeply buried its terrain actually is. Counted apart from an ordinary visible
// verdict because the two want completely different responses: this one says the box is
// too big, not that the terrain is in view.
const uint VERDICT_BACKGROUND = 3u;

// Matches LodHzbProjection.MinimumW. A corner at or behind the camera plane divides
// through infinity, and the rectangle that comes out is not merely inaccurate - it can be
// small and in the wrong place, which is the one failure mode that hides visible terrain.
const float MINIMUM_W = 1e-4;

void main()
{
    uint index = gl_GlobalInvocationID.x;
    if (index >= uint(sectionCount)) return;

    vec3 lo = boxes[index * 2u].xyz;
    vec3 hi = boxes[index * 2u + 1u].xyz;

    if (hi.x < lo.x || hi.y < lo.y || hi.z < lo.z)
    {
        results[index] = VERDICT_FAILED_OPEN;
        return;
    }

    float minU = 1.0 / 0.0;
    float minV = 1.0 / 0.0;
    float maxU = -1.0 / 0.0;
    float maxV = -1.0 / 0.0;
    float nearestDepth = 1.0 / 0.0;

    for (int corner = 0; corner < 8; corner++)
    {
        vec3 p = vec3(
            (corner & 1) == 0 ? lo.x : hi.x,
            (corner & 2) == 0 ? lo.y : hi.y,
            (corner & 4) == 0 ? lo.z : hi.z);

        vec4 clip = viewProjection * vec4(p, 1.0);

        if (isinf(clip.x) || isnan(clip.x) || isinf(clip.y) || isnan(clip.y)
            || isinf(clip.z) || isnan(clip.z) || isinf(clip.w) || isnan(clip.w))
        {
            results[index] = VERDICT_FAILED_OPEN;
            return;
        }
        if (clip.w <= MINIMUM_W)
        {
            results[index] = VERDICT_FAILED_OPEN;
            return;
        }

        vec3 ndc = clip.xyz / clip.w;
        float u = ndc.x * 0.5 + 0.5;
        float v = ndc.y * 0.5 + 0.5;
        float depth = ndc.z * 0.5 + 0.5;

        if (isinf(u) || isnan(u) || isinf(v) || isnan(v) || isinf(depth) || isnan(depth))
        {
            results[index] = VERDICT_FAILED_OPEN;
            return;
        }

        minU = min(minU, u);
        maxU = max(maxU, u);
        minV = min(minV, v);
        maxV = max(maxV, v);
        nearestDepth = min(nearestDepth, depth);
    }

    // Off screen is the frustum test's business, not ours.
    if (maxU < 0.0 || minU > 1.0 || maxV < 0.0 || minV > 1.0)
    {
        results[index] = VERDICT_FAILED_OPEN;
        return;
    }
    if (nearestDepth <= 0.0 || nearestDepth >= 1.0)
    {
        results[index] = VERDICT_FAILED_OPEN;
        return;
    }

    minU = clamp(minU, 0.0, 1.0);
    maxU = clamp(maxU, 0.0, 1.0);
    minV = clamp(minV, 0.0, 1.0);
    maxV = clamp(maxV, 0.0, 1.0);

    // Round the level UP. A coarser level pools more pixels into one texel, and pooling
    // with max can only push the farthest depth farther away, which makes the box harder
    // to declare hidden. Rounding down would do the opposite.
    float widthPixels = (maxU - minU) * float(screenSize.x);
    float heightPixels = (maxV - minV) * float(screenSize.y);
    float longest = max(widthPixels, heightPixels);
    int level = 0;
    if (longest > 2.0) level = int(ceil(log2(longest / 2.0)));
    level = clamp(level, 0, levelCount - 1);

    ivec2 levelSize = textureSize(hzb, level);
    if (levelSize.x <= 0 || levelSize.y <= 0)
    {
        results[index] = VERDICT_FAILED_OPEN;
        return;
    }

    // Outward on both edges: a rectangle covering a sliver of a texel must include it, or
    // the box could be called hidden on the strength of pixels it does not sit behind.
    int x0 = int(floor(minU * float(levelSize.x)));
    int y0 = int(floor(minV * float(levelSize.y)));
    int x1 = int(ceil(maxU * float(levelSize.x))) - 1;
    int y1 = int(ceil(maxV * float(levelSize.y))) - 1;

    x0 = clamp(x0, 0, levelSize.x - 1);
    y0 = clamp(y0, 0, levelSize.y - 1);
    x1 = clamp(x1, x0, levelSize.x - 1);
    y1 = clamp(y1, y0, levelSize.y - 1);

    if ((x1 - x0) > 2 || (y1 - y0) > 2)
    {
        results[index] = VERDICT_FAILED_OPEN;
        return;
    }

    float farthest = -1.0 / 0.0;
    for (int y = y0; y <= y1; y++)
    {
        for (int x = x0; x <= x1; x++)
        {
            // Not named `sample`: that is a reserved qualifier in GLSL 4.x and the
            // compiler rejects the declaration with a syntax error naming SAMPLE.
            float texel = texelFetch(hzb, ivec2(x, y), level).r;
            if (isinf(texel) || isnan(texel))
            {
                results[index] = VERDICT_FAILED_OPEN;
                return;
            }
            farthest = max(farthest, texel);
        }
    }

    // Strictly greater. Equality is the coplanar case and must draw.
    if (nearestDepth > farthest)
    {
        results[index] = VERDICT_OCCLUDED;
    }
    else if (farthest >= 1.0)
    {
        results[index] = VERDICT_BACKGROUND;
    }
    else
    {
        results[index] = VERDICT_VISIBLE;
    }
}
";

    readonly ILodGlStateApi stateApi = new LodOpenGlStateApi();
    readonly Action<string> warn;

    int program;
    int boxBuffer;
    int boxCapacity;
    // Two result buffers, used alternately. This frame dispatches into one and reads the
    // other, which the card finished with a frame ago - so the read never waits on work
    // still in flight. A shadow-mode count does not care that it is one frame old.
    readonly int[] resultBuffers = new int[2];
    // One fence per result buffer. Reading a buffer the card has not finished writing is
    // what turned a 20us phase into a 686us one: GetBufferSubData will happily stall the
    // CPU until the work lands, and "it is a frame old" is not a guarantee the driver
    // honours. The fence turns that into a question with a cheap no.
    readonly IntPtr[] resultFences = new IntPtr[2];
    int resultCapacity;
    int writeSlot;
    bool pendingRead;
    int pendingCount;

    float[] boxData = Array.Empty<float>();
    uint[] resultData = Array.Empty<uint>();

    // Last frame's distances, kept because the verdicts arrive a frame after the boxes and
    // hzbBoxes has already been refilled by then. Without this the per-distance answer
    // would silently pair each verdict with a different section's range.
    float[] pendingDistances = Array.Empty<float>();

    // Section keys for the same reason: a verdict is only checkable against the occlusion
    // queries if we still know which section it belongs to a frame later.
    long[] pendingKeys = Array.Empty<long>();

    // The measurement the owner asked for. His argument for this whole phase is that the
    // amount of hidden terrain scales with draw distance - a piece 20 km out is a few
    // pixels tall and one ridge buries hundreds of them - so the answer has to be reported
    // per distance band rather than as one average that mixes 500 blocks with 32,000.
    static readonly float[] BandCeilings = { 1000f, 2000f, 4000f, 8000f, 16000f, float.PositiveInfinity };
    static readonly string[] BandNames = { "0-1k", "1-2k", "2-4k", "4-8k", "8-16k", "16k+" };
    readonly int[] bandTested = new int[BandCeilings.Length];
    readonly int[] bandOccluded = new int[BandCeilings.Length];

    public bool Available => program != 0;
    public string LastFailure { get; private set; } = "";
    public LodHzbVerdicts Interval { get; private set; }
    public LodHzbVerdicts LastFrame { get; private set; }
    public long Dispatches { get; private set; }
    public long Reads { get; private set; }
    public long ReadsSkipped { get; private set; }

    /// <summary>
    /// Called once per section as verdicts are read, with the section key and its verdict.
    /// The renderer uses it to check each answer against the delayed occlusion queries,
    /// which observe real pixels and are therefore the only ground truth available.
    /// Assigned once and reused, so the per-frame walk allocates nothing.
    /// </summary>
    public Action<long, uint>? VerdictObserver { get; set; }

    public LodHzbClassifier(Action<string> warn) => this.warn = warn;

    /// <summary>
    /// Compiles the compute program. Returns false and stays unavailable on any driver that
    /// refuses it, which costs the classification and nothing else.
    /// </summary>
    public bool TryCreate()
    {
        if (program != 0) return true;

        int shader = 0;
        try
        {
            shader = GL.CreateShader(ShaderType.ComputeShader);
            GL.ShaderSource(shader, ComputeSource);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
            if (compiled == 0)
            {
                LastFailure = "hzbcull compute shader did not compile: " + GL.GetShaderInfoLog(shader);
                return false;
            }

            int created = GL.CreateProgram();
            GL.AttachShader(created, shader);
            GL.LinkProgram(created);
            GL.GetProgram(created, GetProgramParameterName.LinkStatus, out int linked);
            if (linked == 0)
            {
                LastFailure = "hzbcull compute program did not link: " + GL.GetProgramInfoLog(created);
                GL.DeleteProgram(created);
                return false;
            }

            program = created;
            LastFailure = "";
            return true;
        }
        catch (Exception e)
        {
            LastFailure = "hzbcull compute program failed: " + e.Message;
            return false;
        }
        finally
        {
            try { if (shader != 0) GL.DeleteShader(shader); }
            catch { /* the program holds its own reference once linked */ }
        }
    }

    /// <summary>
    /// Reads last frame's verdicts, then dispatches this frame's. In that order: the read
    /// targets the buffer this call is about to stop using, and doing it first is what
    /// keeps the card a whole frame ahead of the CPU.
    /// </summary>
    public void Classify(
        IReadOnlyList<LodHzbSectionBox> boxes,
        float[] viewProjection,
        int hzbTexture,
        int screenWidth,
        int screenHeight,
        int levels)
    {
        LastFrame = default;
        if (program == 0 || hzbTexture == 0) return;
        if (viewProjection == null || viewProjection.Length < 16) return;
        if (levels <= 0 || screenWidth <= 0 || screenHeight <= 0) return;

        LodGlStateSnapshot state = default;
        bool stateCaptured = false;

        try
        {
            state = LodGlStateGuard.Capture(
                stateApi,
                LodGlStateMask.Program | LodGlStateMask.ShaderStorageBuffer
                    | LodGlStateMask.Texture2DUnit0);
            stateCaptured = true;

            ReadPending();

            int count = boxes.Count;
            if (count == 0) return;

            EnsureCapacity(count);
            UploadBoxes(boxes, count);

            GL.UseProgram(program);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, boxBuffer);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, resultBuffers[writeSlot]);

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, hzbTexture);
            SetUniforms(viewProjection, screenWidth, screenHeight, levels, count);

            GL.DispatchCompute((count + LocalSize - 1) / LocalSize, 1, 1);
            GL.MemoryBarrier(
                MemoryBarrierFlags.ShaderStorageBarrierBit
                | MemoryBarrierFlags.BufferUpdateBarrierBit);

            if (pendingDistances.Length < count) pendingDistances = new float[Math.Max(count, 256)];
            if (pendingKeys.Length < count) pendingKeys = new long[Math.Max(count, 256)];
            for (int i = 0; i < count; i++)
            {
                pendingDistances[i] = boxes[i].DistanceBlocks;
                pendingKeys[i] = boxes[i].SectionKey;
            }

            // Fenced after the dispatch, so the next frame can tell whether the results
            // are actually there without asking the card to catch up.
            if (resultFences[writeSlot] != IntPtr.Zero)
            {
                GL.DeleteSync(resultFences[writeSlot]);
                resultFences[writeSlot] = IntPtr.Zero;
            }
            resultFences[writeSlot] = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);

            Dispatches++;
            pendingRead = true;
            pendingCount = count;
            writeSlot ^= 1;

            ErrorCode error = GL.GetError();
            if (error != ErrorCode.NoError) Disable("classification raised " + error);
        }
        catch (Exception e)
        {
            Disable("classification failed: " + e.Message);
        }
        finally
        {
            if (stateCaptured
                && !LodGlStateGuard.TryRestore(stateApi, state, out string restoreFailure))
            {
                Disable("classification state restore failed: " + restoreFailure);
            }
        }
    }

    void ReadPending()
    {
        if (!pendingRead || pendingCount <= 0) return;
        pendingRead = false;

        int slot = writeSlot ^ 1;
        if (resultBuffers[slot] == 0) return;

        // Ask, never wait. A zero timeout means the answer is whatever is already true, and
        // an unfinished dispatch simply costs this frame's sample - which a running count
        // over thousands of frames does not miss. Waiting instead would reintroduce exactly
        // the stall this exists to avoid, and it would do it on the render thread.
        if (resultFences[slot] != IntPtr.Zero)
        {
            WaitSyncStatus status = GL.ClientWaitSync(resultFences[slot], ClientWaitSyncFlags.None, 0);
            GL.DeleteSync(resultFences[slot]);
            resultFences[slot] = IntPtr.Zero;

            if (status != WaitSyncStatus.AlreadySignaled && status != WaitSyncStatus.ConditionSatisfied)
            {
                ReadsSkipped++;
                return;
            }
        }

        Reads++;
        if (resultData.Length < pendingCount) resultData = new uint[Math.Max(pendingCount, 256)];

        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, resultBuffers[slot]);
        GL.GetBufferSubData(
            BufferTarget.ShaderStorageBuffer,
            IntPtr.Zero,
            pendingCount * BytesPerResult,
            resultData);

        int occluded = 0, visible = 0, failedOpen = 0, background = 0;
        for (int i = 0; i < pendingCount; i++)
        {
            bool wasOccluded = false;
            switch (resultData[i])
            {
                case VerdictOccluded: occluded++; wasOccluded = true; break;
                case VerdictFailedOpen: failedOpen++; break;
                case VerdictBackground: background++; break;
                default: visible++; break;
            }

            // Undecided sections are excluded from the bands entirely rather than counted
            // as visible: they say nothing about how much depth rejection is available at
            // that range, and folding them in would drag every band's fraction down.
            if (VerdictObserver != null && i < pendingKeys.Length)
                VerdictObserver(pendingKeys[i], resultData[i]);

            if (resultData[i] == VerdictFailedOpen) continue;
            if (i >= pendingDistances.Length) continue;
            int band = BandFor(pendingDistances[i]);
            bandTested[band]++;
            if (wasOccluded) bandOccluded[band]++;
        }

        LastFrame = new LodHzbVerdicts(pendingCount, occluded, visible, failedOpen, background);
        Interval = Interval.Add(LastFrame);
    }

    void EnsureCapacity(int count)
    {
        if (boxBuffer == 0)
        {
            boxBuffer = GL.GenBuffer();
            resultBuffers[0] = GL.GenBuffer();
            resultBuffers[1] = GL.GenBuffer();
        }

        if (count > boxCapacity)
        {
            // Grown with headroom so an ordinary frame-to-frame wobble in the draw list
            // does not reallocate two buffers every frame.
            boxCapacity = Math.Max(count + count / 2, 256);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, boxBuffer);
            GL.BufferData(
                BufferTarget.ShaderStorageBuffer,
                boxCapacity * BytesPerBox,
                IntPtr.Zero,
                BufferUsageHint.StreamDraw);
            boxData = new float[boxCapacity * 8];
        }

        if (count > resultCapacity)
        {
            resultCapacity = Math.Max(count + count / 2, 256);
            for (int i = 0; i < 2; i++)
            {
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, resultBuffers[i]);
                GL.BufferData(
                    BufferTarget.ShaderStorageBuffer,
                    resultCapacity * BytesPerResult,
                    IntPtr.Zero,
                    BufferUsageHint.StreamRead);
            }
            // A grown result buffer holds nothing the pending read could want.
            pendingRead = false;
        }
    }

    void UploadBoxes(IReadOnlyList<LodHzbSectionBox> boxes, int count)
    {
        for (int i = 0; i < count; i++)
        {
            LodHzbSectionBox box = boxes[i];
            int o = i * 8;
            boxData[o] = box.MinX;
            boxData[o + 1] = box.MinY;
            boxData[o + 2] = box.MinZ;
            boxData[o + 3] = 0f;
            boxData[o + 4] = box.MaxX;
            boxData[o + 5] = box.MaxY;
            boxData[o + 6] = box.MaxZ;
            boxData[o + 7] = 0f;
        }

        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, boxBuffer);
        GL.BufferSubData(
            BufferTarget.ShaderStorageBuffer, IntPtr.Zero, count * BytesPerBox, boxData);
    }

    void SetUniforms(float[] viewProjection, int width, int height, int levels, int count)
    {
        GL.UniformMatrix4(GL.GetUniformLocation(program, "viewProjection"), 1, false, viewProjection);
        GL.Uniform2(GL.GetUniformLocation(program, "screenSize"), width, height);
        GL.Uniform1(GL.GetUniformLocation(program, "levelCount"), levels);
        GL.Uniform1(GL.GetUniformLocation(program, "sectionCount"), count);
        GL.Uniform1(GL.GetUniformLocation(program, "hzb"), 0);
    }

    void Disable(string reason)
    {
        LastFailure = reason;
        warn("[VintageHorizons] depth-pyramid classification disabled; nothing that draws "
            + "depends on it: " + reason);
        ReleaseGpuObjects();
    }

    static int BandFor(float distance)
    {
        for (int i = 0; i < BandCeilings.Length; i++)
        {
            if (distance <= BandCeilings[i]) return i;
        }
        return BandCeilings.Length - 1;
    }

    public void ResetInterval()
    {
        Interval = default;
        Dispatches = 0;
        Reads = 0;
        ReadsSkipped = 0;
        Array.Clear(bandTested);
        Array.Clear(bandOccluded);
    }

    /// <summary>Hidden share per distance band, or an empty string when nothing was tested.</summary>
    public string DescribeByDistance()
    {
        var parts = new List<string>();
        for (int i = 0; i < bandTested.Length; i++)
        {
            if (bandTested[i] == 0) continue;
            parts.Add($"{BandNames[i]} {bandOccluded[i] * 100.0 / bandTested[i]:0}% of {bandTested[i]}");
        }
        return parts.Count == 0 ? "" : string.Join(", ", parts);
    }

    public string Describe()
    {
        if (program == 0)
            return LastFailure.Length > 0 ? "unavailable: " + LastFailure : "not created";
        if (Interval.Sections == 0) return "created, no sections classified yet";

        return $"{Interval.Sections} section-tests over {Dispatches} dispatches, "
            + $"{Reads} read / {ReadsSkipped} skipped | "
            + $"{Interval.Occluded} hidden ({Interval.OccludedFraction:P1}), "
            + $"{Interval.Visible} visible, {Interval.Background} refused on background "
            + $"({Interval.BackgroundFraction:P0} of not-hidden), "
            + $"{Interval.FailedOpen} undecided";
    }

    void ReleaseGpuObjects()
    {
        try { if (program != 0) GL.DeleteProgram(program); } catch { /* teardown continues */ }
        try { if (boxBuffer != 0) GL.DeleteBuffer(boxBuffer); } catch { /* as above */ }
        for (int i = 0; i < 2; i++)
        {
            try { if (resultBuffers[i] != 0) GL.DeleteBuffer(resultBuffers[i]); }
            catch { /* as above */ }
            try { if (resultFences[i] != IntPtr.Zero) GL.DeleteSync(resultFences[i]); }
            catch { /* as above */ }
            resultBuffers[i] = 0;
            resultFences[i] = IntPtr.Zero;
        }
        program = 0;
        boxBuffer = 0;
        boxCapacity = 0;
        resultCapacity = 0;
        pendingRead = false;
    }

    public void Dispose() => ReleaseGpuObjects();
}

/// <summary>One section's camera-relative box, as handed to the card.</summary>
internal readonly record struct LodHzbSectionBox(
    long SectionKey,
    float MinX, float MinY, float MinZ,
    float MaxX, float MaxY, float MaxZ,
    float DistanceBlocks);
