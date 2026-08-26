namespace VintageHorizons;

/// <summary>
/// The GL operations one indirect opaque pass needs, behind an interface so the order and
/// the arguments can be pinned by ordinary checks. The real implementation is
/// <see cref="LodGpuOpenGlDrawBackend"/>; nothing else in the mod issues a draw.
/// </summary>
internal interface ILodGpuDrawBackend : IDisposable
{
    /// <summary>Creates the vertex array and the two per-frame buffers.</summary>
    bool Create();

    /// <summary>Replaces this frame's command and record buffers.</summary>
    bool UploadFrame(ReadOnlySpan<byte> commands, ReadOnlySpan<byte> records);

    /// <summary>
    /// Uploads this frame's cull boxes and binds them, with the command buffer, where a
    /// compute pass can reach them. The command buffer is the SAME buffer the multi-draw
    /// will source from - that is the whole point, since zeroing a slot is how a section is
    /// dropped - so this is the one place in the mod where a buffer is written by a shader
    /// and then read by the driver as commands.
    /// </summary>
    bool BeginCull(ReadOnlySpan<byte> boxes);

    /// <summary>
    /// Ends the cull and inserts the barrier that makes those writes visible to the
    /// multi-draw. Without it the driver is free to read command data it fetched before the
    /// compute pass ran, and the culling would appear to work intermittently - which is the
    /// worst way for it to fail.
    /// </summary>
    bool EndCull();

    /// <summary>Captures the GL state the pass disturbs and binds what every batch shares.</summary>
    bool BeginDraw();

    /// <summary>Binds one page set and issues its run of commands as a single multi-draw.</summary>
    bool DrawBatch(int vertexPage, int indexPage, int firstCommand, int commandCount);

    /// <summary>Puts the captured state back. Runs even if a batch failed.</summary>
    bool EndDraw();
}

/// <summary>
/// Everything the cull step needs, gathered so the drawer can run it without knowing what a
/// depth pyramid is. A default value - no pass - means this frame draws every command the CPU
/// approved, which is the complete picture and the behaviour of every frame before Phase 5.
/// </summary>
internal readonly record struct LodGpuCullRequest(
    ILodGpuCullDispatch? Pass,
    float[]? ViewProjection,
    int HzbTexture,
    int ScreenWidth,
    int ScreenHeight,
    int Levels,
    float OcclusionDepthBias,
    LodGpuCullBucket Bucket)
{
    /// <summary>Whether this frame has everything culling needs. Anything missing draws everything.</summary>
    public bool Wanted =>
        Pass is { Available: true }
        && ViewProjection is { Length: >= 16 }
        && HzbTexture != 0
        && ScreenWidth > 0 && ScreenHeight > 0 && Levels > 0;
}

/// <summary>
/// Draws the command list the traversal already approved, one multi-draw per page set.
///
/// This is the first thing in the mod that puts a cached-terrain pixel on screen from a
/// regional arena rather than from a per-section MeshRef. It decides nothing about
/// visibility: every command it issues was chosen by the same CPU traversal, ownership
/// skip, distance cap and frustum test the established path uses, and the batches arrive
/// in the order that path submitted them.
///
/// A failure disables the drawer for the session rather than retrying per frame. The
/// established path is complete and always available, so the correct response to a driver
/// that will not multi-draw is to stop asking it.
/// </summary>
internal sealed class LodGpuIndirectDrawer : IDisposable
{
    readonly ILodGpuDrawBackend backend;
    readonly Action<string> warn;
    bool created;

    public LodGpuIndirectDrawer(ILodGpuDrawBackend backend, Action<string> warn)
    {
        this.backend = backend;
        this.warn = warn;
    }

    /// <summary>Set once a failure has been reported; never cleared within a session.</summary>
    public bool Failed { get; private set; }

    public string FailureReason { get; private set; } = "";

    public long FramesDrawn { get; private set; }
    public long BatchesDrawn { get; private set; }
    public long CommandsDrawn { get; private set; }
    public long VisibleCommandsDrawn { get; private set; }

    /// <summary>What the most recent successful frame actually issued.</summary>
    public int LastBatches { get; private set; }
    public int LastCommands { get; private set; }

    /// <summary>
    /// Whether the most recent frame's commands were culled on the card before drawing.
    /// Reported rather than assumed, because a frame that quietly skipped the cull looks
    /// exactly like one where nothing happened to be hidden.
    /// </summary>
    public bool Culled { get; private set; }

    public long CullFrames { get; private set; }

    /// <summary>
    /// Whether a pass may be handed to this drawer. Asked before the pass rather than
    /// during it: a path chosen halfway through a frame would draw some sections twice
    /// and some not at all.
    /// </summary>
    public bool Ready => !Failed;

    /// <summary>
    /// Phase 9 test hook. It takes the exact permanent-fallback route a backend refusal takes,
    /// without issuing an invalid GL operation merely to make a driver complain.
    /// </summary>
    internal void InjectFailure(string reason) => Fail(reason);

    /// <summary>
    /// Issues the built command list. Returns false when nothing was drawn, in which case
    /// the caller has already submitted, or will submit, that terrain some other way.
    /// </summary>
    public bool Draw(LodGpuIndirectBuilder builder) => Draw(builder, default);

    public bool Draw(LodGpuIndirectBuilder builder, in LodGpuCullRequest cull)
    {
        // Per-call state, never the last successful call's state. The renderer can issue two
        // buckets in one frame now, and an empty/refused second bucket must not report that
        // its commands were culled merely because the first bucket was.
        Culled = false;
        LastBatches = 0;
        LastCommands = 0;
        if (Failed) return false;
        if (builder.CommandCount == 0 || builder.Batches.Count == 0) return false;

        if (!created)
        {
            if (!Try(backend.Create, "the vertex array and command buffers were refused"))
                return false;
            created = true;
        }

        if (!Try(() => backend.UploadFrame(builder.Commands, builder.Records),
                "this frame's commands could not be uploaded"))
            return false;

        // Between the upload and the draw, and nowhere else. The commands the CPU approved
        // are on the card; the cull turns some of them off in place; the barrier inside
        // EndCull makes those writes visible to the multi-draw that follows. Every failure
        // here leaves the uploaded commands exactly as they were, which draws everything.
        Culled = TryCull(builder, cull);

        if (!Try(backend.BeginDraw, "the indirect pass could not bind its state")) return false;

        int batches = 0;
        int commands = 0;
        try
        {
            foreach (LodGpuDrawBatch batch in builder.Batches)
            {
                if (!Try(() => backend.DrawBatch(
                        batch.VertexPage, batch.IndexPage, batch.FirstCommand, batch.CommandCount),
                    "a multi-draw was refused"))
                    break;
                batches++;
                commands += batch.CommandCount;
            }
        }
        finally
        {
            // Restoration runs whatever happened, and deliberately not through Try: a
            // drawer that has just failed would skip it, and the engine's own renderer
            // goes next and must find the bindings it left. A failure here is reported
            // only if nothing has been reported yet.
            try
            {
                if (!backend.EndDraw())
                    Fail("the indirect pass did not restore its state exactly");
            }
            catch (Exception e)
            {
                Fail("the indirect pass did not restore its state exactly: " + e.Message);
            }
        }

        // All or nothing. A pass that issued three batches of four has left a quarter of
        // the horizon undrawn, and the caller's only useful response is to draw the whole
        // list the established way: the same opaque geometry submitted twice writes no
        // second pixel, while a batch that never went out is a hole.
        if (batches != builder.Batches.Count) return false;

        LastBatches = batches;
        LastCommands = commands;
        FramesDrawn++;
        BatchesDrawn += batches;
        CommandsDrawn += commands;
        VisibleCommandsDrawn += builder.VisibleCommands;
        return true;
    }

    /// <summary>
    /// Runs the cull, or does not, and never throws either way.
    ///
    /// The whole method is written so that every early return leaves the command buffer
    /// holding exactly what the CPU uploaded. That is the only invariant that matters here:
    /// a cull which does not happen costs performance, and a cull which half-happens - bound
    /// but not barriered, dispatched but not finished - could let the driver read command
    /// data mid-write. So once BeginCull succeeds, EndCull runs no matter what.
    /// </summary>
    bool TryCull(LodGpuIndirectBuilder builder, in LodGpuCullRequest cull)
    {
        if (!cull.Wanted) return false;
        if (builder.Boxes.Length == 0) return false;

        // Deliberately not through Try: a cull that cannot start is not a reason to abandon
        // batching for the session. The established suppression is still in place and the
        // frame is correct without this.
        bool begun;
        try
        {
            begun = backend.BeginCull(builder.Boxes);
        }
        catch
        {
            return false;
        }
        if (!begun) return false;

        bool dispatched = false;
        try
        {
            dispatched = cull.Pass!.Dispatch(
                cull.ViewProjection!, cull.HzbTexture,
                cull.ScreenWidth, cull.ScreenHeight, cull.Levels,
                builder.CommandCount, cull.OcclusionDepthBias,
                cull.Bucket,
                builder.Identities);
        }
        catch
        {
            dispatched = false;
        }
        finally
        {
            // The barrier lives in here, so it runs even for a dispatch that failed. A
            // failed dispatch may still have written some slots before it stopped.
            try
            {
                if (!backend.EndCull())
                    Fail("the cull pass did not restore its state exactly");
            }
            catch (Exception e)
            {
                Fail("the cull pass did not restore its state exactly: " + e.Message);
            }
        }

        if (dispatched) CullFrames++;
        return dispatched;
    }

    bool Try(Func<bool> action, string what)
    {
        if (Failed) return false;
        try
        {
            if (action()) return true;
            Fail(what);
            return false;
        }
        catch (Exception e)
        {
            Fail(what + ": " + e.Message);
            return false;
        }
    }

    void Fail(string reason)
    {
        if (Failed) return;
        Failed = true;
        FailureReason = reason;
        warn("[VintageHorizons] Indirect cached-terrain drawing disabled for this session; "
            + "the established renderer takes the next frame: " + reason);
    }

    public string Describe() => Failed
        ? "failed: " + FailureReason
        : FramesDrawn == 0
            ? "ready, nothing drawn yet"
            : $"{FramesDrawn} frames, {BatchesDrawn} multi-draws, {CommandsDrawn} commands "
                + $"({VisibleCommandsDrawn} visible)";

    public void Dispose()
    {
        try { backend.Dispose(); }
        catch { /* Context teardown must continue. */ }
        created = false;
    }
}
