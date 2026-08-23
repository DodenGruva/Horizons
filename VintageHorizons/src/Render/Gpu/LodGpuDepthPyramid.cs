using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

namespace VintageHorizons;

internal readonly record struct LodHzbBuildResult(
    bool Built,
    int Width,
    int Height,
    int Levels,
    string FailureReason)
{
    public static LodHzbBuildResult Failed(string reason) => new(false, 0, 0, 0, reason);
}

/// <summary>
/// A private copy of the scene's depth, reduced into a pyramid whose every texel holds the
/// FARTHEST depth in the patch it stands for.
///
/// Phase 4 of the GPU plan. Nothing here decides anything: it copies, reduces, and times
/// itself. Classification comes next, and only after the pyramid has been shown to cost
/// less than the drawing it could remove.
///
/// Three things about it are load-bearing:
///
/// - It copies rather than sampling the live depth buffer. That buffer is still attached to
///   the framebuffer being drawn into, and reading a texture attached for writing is
///   undefined - it would work on one driver and produce garbage on another.
/// - It reduces with max, never the driver's own mipmap generation, which averages. An
///   averaged pyramid reports depths nearer than the samples under it, and a test against
///   that hides terrain that is actually visible.
/// - It refuses to run under a reversed-depth convention rather than flipping the operator,
///   because a wrong guess there inverts the entire test and the failure is invisible until
///   someone notices terrain missing.
///
/// Every GL object it owns belongs to the render thread, and every piece of state it
/// touches is put back before it returns, whether or not the build succeeded.
/// </summary>
internal sealed class LodGpuDepthPyramid : IDisposable
{
    const int DepthComponent16 = 0x81A5;
    const int DepthComponent24 = 0x81A6;
    const int DepthComponent32 = 0x81A7;
    const int DepthComponent32F = 0x8CAC;

    readonly ILodGlStateApi stateApi = new LodOpenGlStateApi();
    readonly ICoreClientAPI capi;
    readonly Func<IShaderProgram?> reduceProgram;

    int texture;
    int framebuffer;
    int vertexArray;
    int width;
    int height;
    int levels;
    int internalFormat;

    public int Width => width;
    public int Height => height;
    public int Levels => levels;
    public int TextureName => texture;
    public bool Allocated => texture != 0;

    /// <summary>Builds attempted, completed, and refused since the last interval reset.</summary>
    public long BuildsAttempted { get; private set; }
    public long BuildsCompleted { get; private set; }

    /// <summary>Frames that never asked, because the window was minimised.</summary>
    public long BuildsSkipped { get; private set; }
    public long Reallocations { get; private set; }
    public string LastFailure { get; private set; } = "";

    public LodGpuDepthPyramid(ICoreClientAPI capi, Func<IShaderProgram?> reduceProgram)
    {
        this.capi = capi;
        this.reduceProgram = reduceProgram;
    }

    /// <summary>
    /// Copies the current depth attachment and reduces it through every level.
    ///
    /// Returns a failure rather than throwing for anything the driver or the frame can
    /// legitimately present - a resized window mid-frame, a multisample target, a depth
    /// format nobody has validated. A failed build leaves no pyramid, and a caller with no
    /// pyramid must draw everything, which is the only safe direction.
    /// </summary>
    public LodHzbBuildResult Build(LodGpuDepthFacts depth)
    {
        // A minimised window keeps rendering frames with a zero-area viewport. That is not
        // a failure to report or even an attempt to count - it is the game being in the
        // background - and counting it made two thirds of a real session's builds look
        // broken. Checked before anything else, including the attempt counter.
        int[] viewportProbe = new int[4];
        GL.GetInteger(GetPName.Viewport, viewportProbe);
        if (viewportProbe[2] <= 0 || viewportProbe[3] <= 0)
        {
            BuildsSkipped++;
            return LodHzbBuildResult.Failed("the window has no visible area");
        }

        BuildsAttempted++;

        if (!depth.AttachmentPresent)
            return Fail("the active depth attachment was not observed");
        if (!depth.AttachmentType.Equals("Texture", StringComparison.OrdinalIgnoreCase))
            return Fail("the active depth attachment is not texture-backed");
        if (depth.Samples != 1)
            return Fail("multisample depth needs a resolve this phase has not validated");
        if (depth.Convention != LodGpuDepthConvention.Conventional)
        {
            // Deliberately not "flip to min and carry on". The whole pyramid is an
            // inequality, and under reversed depth every one of those inequalities points
            // the wrong way - which does not look wrong, it hides visible terrain.
            return Fail($"depth convention {depth.Convention} is not the conventional "
                + "near-zero/far-one this reduction assumes");
        }

        IShaderProgram? program = reduceProgram();
        if (program == null) return Fail("the hzbreduce shader is not available");

        LodGlStateSnapshot state = default;
        bool stateCaptured = false;
        LodHzbPipelineState pipeline = default;
        bool pipelineCaptured = false;

        try
        {
            ErrorCode existing = GL.GetError();
            if (existing != ErrorCode.NoError)
                return Fail($"a pre-existing OpenGL error {existing} prevented an isolated build");

            int[] viewport = new int[4];
            GL.GetInteger(GetPName.Viewport, viewport);
            int frameWidth = viewport[2];
            int frameHeight = viewport[3];
            if (frameWidth <= 0 || frameHeight <= 0)
                return Fail("the active viewport has no area");

            state = LodGlStateGuard.Capture(
                stateApi,
                LodGlStateMask.Framebuffers | LodGlStateMask.Texture2DUnit0
                    | LodGlStateMask.Program | LodGlStateMask.VertexArray);
            stateCaptured = true;

            pipeline = LodHzbPipelineState.Capture();
            pipelineCaptured = true;

            if (!TryEnsureStorage(depth, frameWidth, frameHeight, out string storageFailure))
                return Fail(storageFailure);

            CopyLevelZero(depth, viewport);
            ReduceLevels(program);

            ErrorCode error = GL.GetError();
            if (error != ErrorCode.NoError) return Fail("pyramid build raised " + error);

            BuildsCompleted++;
            LastFailure = "";
            return new LodHzbBuildResult(true, width, height, levels, "");
        }
        catch (Exception e)
        {
            return Fail("pyramid build failed: " + e.Message);
        }
        finally
        {
            // Restored in the reverse order of the damage: pipeline switches first, then
            // the bindings the guard owns. A build that threw halfway through has left
            // both changed, so neither restore may be skipped on the failure path.
            if (pipelineCaptured)
            {
                try { pipeline.Restore(); }
                catch (Exception e) { LastFailure = "pipeline restore failed: " + e.Message; }
            }
            if (stateCaptured && !LodGlStateGuard.TryRestore(stateApi, state, out string restoreFailure))
            {
                LastFailure = "state restore failed: " + restoreFailure;
            }
        }
    }

    /// <summary>
    /// Allocates, or reallocates after a resize or a format change. The texture matches the
    /// source depth format exactly, because the copy is a framebuffer blit and a blit will
    /// not convert between depth formats.
    /// </summary>
    bool TryEnsureStorage(LodGpuDepthFacts depth, int frameWidth, int frameHeight, out string failure)
    {
        failure = "";

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, depth.AttachmentName);
        GL.GetTexLevelParameter(
            TextureTarget.Texture2D, 0, GetTextureParameter.TextureInternalFormat,
            out int sourceFormat);
        GL.GetTexLevelParameter(
            TextureTarget.Texture2D, 0, GetTextureParameter.TextureWidth, out int sourceWidth);
        GL.GetTexLevelParameter(
            TextureTarget.Texture2D, 0, GetTextureParameter.TextureHeight, out int sourceHeight);

        if (!IsSupportedDepthFormat(sourceFormat))
        {
            failure = $"depth internal format 0x{sourceFormat:X} is not validated";
            return false;
        }
        if (sourceWidth < frameWidth || sourceHeight < frameHeight)
        {
            failure = $"depth texture {sourceWidth}x{sourceHeight} does not contain the "
                + $"{frameWidth}x{frameHeight} viewport";
            return false;
        }

        if (texture != 0 && width == frameWidth && height == frameHeight
            && internalFormat == sourceFormat)
        {
            return true;
        }

        ReleaseGpuObjects();

        width = frameWidth;
        height = frameHeight;
        internalFormat = sourceFormat;
        levels = LodHzbReference.LevelCount(width, height);

        texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexStorage2D(
            TextureTarget2d.Texture2D, levels, (SizedInternalFormat)internalFormat, width, height);

        // Nearest and clamped: every read is a texelFetch at an explicit level, so
        // filtering would only ever blend two texels that mean different things. The
        // compare mode is off because this is sampled as a value, never as a shadow test.
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureCompareMode, (int)TextureCompareMode.None);

        framebuffer = GL.GenFramebuffer();

        // Core profile refuses a draw with no vertex array bound, and the reduction has no
        // attributes at all - the full-screen triangle comes out of gl_VertexID. An empty
        // array object is exactly what that needs.
        vertexArray = GL.GenVertexArray();

        Reallocations++;
        return true;
    }

    /// <summary>Blits the live depth into level zero, the one level not produced by reduction.</summary>
    void CopyLevelZero(LodGpuDepthFacts depth, int[] viewport)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, texture, 0);
        // A fresh framebuffer object targets colour attachment zero, which a depth-only
        // attachment can never satisfy. Both have to be turned off before completeness
        // means anything.
        GL.DrawBuffer(DrawBufferMode.None);
        GL.ReadBuffer(ReadBufferMode.None);

        FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != FramebufferErrorCode.FramebufferComplete)
            throw new InvalidOperationException("private depth framebuffer is incomplete: " + status);

        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, depth.DrawFramebuffer);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, framebuffer);
        GL.BlitFramebuffer(
            viewport[0], viewport[1], viewport[0] + width, viewport[1] + height,
            0, 0, width, height,
            ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
    }

    /// <summary>
    /// Builds every level above zero, each from the one below it.
    ///
    /// Reading and writing the same texture object in one draw is only defined because the
    /// levels are disjoint and the base/max clamp makes that explicit to the driver: the
    /// sampler can see exactly one level, and it is never the level attached for writing.
    /// </summary>
    void ReduceLevels(IShaderProgram program)
    {
        // Use() and Stop() are a pair, and the finally below is not optional. The engine
        // keeps its OWN record of which shader program is in use, in static state that
        // LodGlStateGuard knows nothing about - the guard restores the raw GL binding and
        // leaves that record pointing at us. The next prog.Use() then throws "Already a
        // different shader (hzbreduce) in use!" and takes the client down with it, which
        // is exactly what happened on the first benchmark run of this code.
        program.Use();
        try
        {
        GL.BindVertexArray(vertexArray);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        program.Uniform("sourceDepth", 0);

        // Depth writes are the output of this pass, so the test has to be on and has to
        // accept every fragment. Colour is off, blending is off, and nothing is culled or
        // scissored: a scissor left over from the engine would silently leave part of a
        // level holding whatever the last frame put there.
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Always);
        GL.DepthMask(true);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.ScissorTest);
        GL.ColorMask(false, false, false, false);

        for (int level = 1; level < levels; level++)
        {
            (int sourceWidth, int sourceHeight) = LodHzbReference.LevelSize(width, height, level - 1);
            (int targetWidth, int targetHeight) = LodHzbReference.LevelSize(width, height, level);

            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, level - 1);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, level - 1);

            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                TextureTarget.Texture2D, texture, level);

            GL.Viewport(0, 0, targetWidth, targetHeight);

            // Four scalars, never two Vec2i: the engine's vector overload reaches
            // glUniform2f and an integer uniform rejects it outright, leaving zero. G42.
            program.Uniform("sourceWidth", sourceWidth);
            program.Uniform("sourceHeight", sourceHeight);
            program.Uniform("targetWidth", targetWidth);
            program.Uniform("targetHeight", targetHeight);

            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }

        }
        finally
        {
            // Put the clamp back so the texture is sampleable across its whole chain by
            // whatever reads it next. Left at the last level, every later fetch would
            // silently read a 1x1 image. In the finally because a throw halfway through
            // the loop leaves it clamped just as surely as a clean exit does.
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, levels - 1);

            // Releases the engine's record of the shader in use. See the note at Use().
            program.Stop();
        }
    }

    public string Describe()
    {
        if (!Allocated) return "no pyramid allocated";
        string failure = LastFailure.Length > 0 ? $" | last failure: {LastFailure}" : "";
        string minimised = BuildsSkipped > 0
            ? $", {BuildsSkipped} frames skipped with the window minimised" : "";
        return $"{width}x{height}, {levels} levels, format 0x{internalFormat:X} | "
            + $"{BuildsCompleted}/{BuildsAttempted} builds completed, "
            + $"{Reallocations} (re)allocations{minimised}{failure}";
    }

    public void ResetInterval()
    {
        BuildsAttempted = 0;
        BuildsCompleted = 0;
        BuildsSkipped = 0;
        Reallocations = 0;
    }

    LodHzbBuildResult Fail(string reason)
    {
        LastFailure = reason;
        return LodHzbBuildResult.Failed(reason);
    }

    static bool IsSupportedDepthFormat(int format) =>
        format is DepthComponent16 or DepthComponent24 or DepthComponent32 or DepthComponent32F;

    void ReleaseGpuObjects()
    {
        try { if (framebuffer != 0) GL.DeleteFramebuffer(framebuffer); }
        catch { /* teardown must continue over an object the driver already released */ }
        try { if (vertexArray != 0) GL.DeleteVertexArray(vertexArray); }
        catch { /* as above */ }
        try { if (texture != 0) GL.DeleteTexture(texture); }
        catch { /* as above */ }
        framebuffer = 0;
        vertexArray = 0;
        texture = 0;
        width = 0;
        height = 0;
        levels = 0;
        internalFormat = 0;
    }

    public void Dispose() => ReleaseGpuObjects();
}

/// <summary>
/// The fixed-function switches the reduction pass has to change, captured exactly and put
/// back exactly. The shared <see cref="LodGlStateGuard"/> covers bindings; this covers the
/// pipeline, which nothing else in the mod has needed to disturb before.
/// </summary>
internal readonly record struct LodHzbPipelineState(
    bool DepthTest,
    int DepthFunc,
    bool DepthMask,
    bool CullFace,
    bool Blend,
    bool ScissorTest,
    int ViewportX,
    int ViewportY,
    int ViewportWidth,
    int ViewportHeight,
    bool ColorMaskR,
    bool ColorMaskG,
    bool ColorMaskB,
    bool ColorMaskA)
{
    public static LodHzbPipelineState Capture()
    {
        int[] viewport = new int[4];
        GL.GetInteger(GetPName.Viewport, viewport);
        bool[] colorMask = new bool[4];
        GL.GetBoolean(GetPName.ColorWritemask, colorMask);

        return new LodHzbPipelineState(
            GL.IsEnabled(EnableCap.DepthTest),
            GL.GetInteger(GetPName.DepthFunc),
            GL.GetBoolean(GetPName.DepthWritemask),
            GL.IsEnabled(EnableCap.CullFace),
            GL.IsEnabled(EnableCap.Blend),
            GL.IsEnabled(EnableCap.ScissorTest),
            viewport[0], viewport[1], viewport[2], viewport[3],
            colorMask[0], colorMask[1], colorMask[2], colorMask[3]);
    }

    public void Restore()
    {
        if (DepthTest) GL.Enable(EnableCap.DepthTest); else GL.Disable(EnableCap.DepthTest);
        GL.DepthFunc((DepthFunction)DepthFunc);
        GL.DepthMask(DepthMask);
        if (CullFace) GL.Enable(EnableCap.CullFace); else GL.Disable(EnableCap.CullFace);
        if (Blend) GL.Enable(EnableCap.Blend); else GL.Disable(EnableCap.Blend);
        if (ScissorTest) GL.Enable(EnableCap.ScissorTest); else GL.Disable(EnableCap.ScissorTest);
        GL.Viewport(ViewportX, ViewportY, ViewportWidth, ViewportHeight);
        GL.ColorMask(ColorMaskR, ColorMaskG, ColorMaskB, ColorMaskA);
    }
}
