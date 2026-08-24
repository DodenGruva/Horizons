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
        int count);
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

    readonly Action<string> warn;
    int program;
    bool disabled;

    public LodGpuCullPass(Action<string> warn) => this.warn = warn;

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
        int count)
    {
        if (!Available) return false;
        if (hzbTexture == 0 || count <= 0) return false;
        if (viewProjection == null || viewProjection.Length < 16) return false;
        if (levels <= 0 || screenWidth <= 0 || screenHeight <= 0) return false;

        // Program and texture unit zero are the only shared state this touches; the caller
        // owns the buffer bindings and puts them back.
        LodGlStateSnapshot state = LodGlStateGuard.Capture(
            new LodOpenGlStateApi(),
            LodGlStateMask.Program | LodGlStateMask.Texture2DUnit0);

        try
        {
            GL.UseProgram(program);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, hzbTexture);

            GL.UniformMatrix4(
                GL.GetUniformLocation(program, "viewProjection"), 1, false, viewProjection);
            GL.Uniform1(GL.GetUniformLocation(program, "screenWidth"), screenWidth);
            GL.Uniform1(GL.GetUniformLocation(program, "screenHeight"), screenHeight);
            GL.Uniform1(GL.GetUniformLocation(program, "levelCount"), levels);
            GL.Uniform1(GL.GetUniformLocation(program, "sectionCount"), count);
            GL.Uniform1(GL.GetUniformLocation(program, "hzb"), 0);

            GL.DispatchCompute(GroupsFor(count), 1, 1);

            ErrorCode error = GL.GetError();
            if (error != ErrorCode.NoError) return Disable("culling raised " + error);

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
            if (!LodGlStateGuard.TryRestore(new LodOpenGlStateApi(), state, out string failure))
                Disable("the cull pass did not restore GL state exactly: " + failure);
        }
    }

    /// <summary>Workgroups needed to cover every command, rounding up.</summary>
    public static int GroupsFor(int count) =>
        count <= 0 ? 0 : (count + LocalSize - 1) / LocalSize;

    public void ResetInterval()
    {
        Dispatches = 0;
        SectionsTested = 0;
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

    void Release()
    {
        try { if (program != 0) GL.DeleteProgram(program); }
        catch { /* Context teardown must continue. */ }
        program = 0;
    }

    public void Dispose() => Release();
}
