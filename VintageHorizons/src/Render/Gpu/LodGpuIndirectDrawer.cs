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

    /// <summary>Captures the GL state the pass disturbs and binds what every batch shares.</summary>
    bool BeginDraw();

    /// <summary>Binds one page set and issues its run of commands as a single multi-draw.</summary>
    bool DrawBatch(int vertexPage, int indexPage, int firstCommand, int commandCount);

    /// <summary>Puts the captured state back. Runs even if a batch failed.</summary>
    bool EndDraw();
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
    /// Whether a pass may be handed to this drawer. Asked before the pass rather than
    /// during it: a path chosen halfway through a frame would draw some sections twice
    /// and some not at all.
    /// </summary>
    public bool Ready => !Failed;

    /// <summary>
    /// Issues the built command list. Returns false when nothing was drawn, in which case
    /// the caller has already submitted, or will submit, that terrain some other way.
    /// </summary>
    public bool Draw(LodGpuIndirectBuilder builder)
    {
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
