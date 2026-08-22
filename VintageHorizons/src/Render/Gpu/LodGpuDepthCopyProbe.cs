using OpenTK.Graphics.OpenGL4;

namespace VintageHorizons;

internal readonly record struct LodGpuDepthCopyValidation(
    bool CopyValidated,
    bool MipAllocationValidated,
    bool StateRestored,
    int Width,
    int Height,
    int MipLevels,
    int InternalFormat,
    string FailureReason)
{
    public bool Succeeded => CopyValidated && MipAllocationValidated && StateRestored;
}

/// <summary>
/// One-time disposable proof that the active single-sample depth texture can be blitted
/// into a private, same-format texture with a complete mip allocation. It does not retain
/// the texture, build a conservative HZB, or influence drawing.
/// </summary>
internal static class LodGpuDepthCopyProbe
{
    // Sized depth formats accepted by TexStorage2D. Depth/stencil sources are deliberately
    // deferred until their attachment and copy semantics receive a dedicated probe.
    const int DepthComponent16 = 0x81A5;
    const int DepthComponent24 = 0x81A6;
    const int DepthComponent32 = 0x81A7;
    const int DepthComponent32F = 0x8CAC;

    public static LodGpuDepthCopyValidation Probe(LodGpuDepthFacts depth)
    {
        if (!depth.AttachmentPresent)
            return Failure("the active depth attachment was not observed");
        if (!depth.AttachmentType.Equals("Texture", StringComparison.OrdinalIgnoreCase))
            return Failure("the active depth attachment is not texture-backed");
        if (depth.Samples != 1)
            return Failure("multisample depth resolve is not validated by this probe");

        var stateApi = new LodOpenGlStateApi();
        LodGlStateSnapshot state = default;
        bool stateCaptured = false;
        int privateTexture = 0;
        int privateFramebuffer = 0;
        int width = 0;
        int height = 0;
        int levels = 0;
        int internalFormat = 0;
        var result = Failure("private depth-copy probe did not run");

        try
        {
            ErrorCode existingError = GL.GetError();
            if (existingError != ErrorCode.NoError)
            {
                throw new InvalidOperationException(
                    $"pre-existing OpenGL error {existingError} prevented an isolated depth probe");
            }

            state = LodGlStateGuard.Capture(
                stateApi,
                LodGlStateMask.Framebuffers | LodGlStateMask.Texture2DUnit0);
            stateCaptured = true;
            GL.ActiveTexture(TextureUnit.Texture0);

            int[] viewport = new int[4];
            GL.GetInteger(GetPName.Viewport, viewport);
            width = viewport[2];
            height = viewport[3];
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("the active viewport has no area");

            GL.BindTexture(TextureTarget.Texture2D, depth.AttachmentName);
            GL.GetTexLevelParameter(
                TextureTarget.Texture2D, 0, GetTextureParameter.TextureWidth, out int sourceWidth);
            GL.GetTexLevelParameter(
                TextureTarget.Texture2D, 0, GetTextureParameter.TextureHeight, out int sourceHeight);
            GL.GetTexLevelParameter(
                TextureTarget.Texture2D, 0, GetTextureParameter.TextureInternalFormat,
                out internalFormat);
            if (sourceWidth < viewport[0] + width || sourceHeight < viewport[1] + height)
            {
                throw new InvalidOperationException(
                    $"depth texture {sourceWidth}x{sourceHeight} does not contain viewport "
                    + $"{viewport[0]},{viewport[1]} {width}x{height}");
            }
            if (!IsSupportedDepthFormat(internalFormat))
            {
                throw new InvalidOperationException(
                    $"depth internal format 0x{internalFormat:X} is not yet validated");
            }

            levels = CalculateMipLevels(width, height);
            privateTexture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, privateTexture);
            GL.TexStorage2D(
                TextureTarget2d.Texture2D,
                levels,
                (SizedInternalFormat)internalFormat,
                width,
                height);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureBaseLevel,
                0);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMaxLevel,
                levels - 1);

            GL.GetTexLevelParameter(
                TextureTarget.Texture2D,
                levels - 1,
                GetTextureParameter.TextureWidth,
                out int finalWidth);
            GL.GetTexLevelParameter(
                TextureTarget.Texture2D,
                levels - 1,
                GetTextureParameter.TextureHeight,
                out int finalHeight);
            if (finalWidth != 1 || finalHeight != 1)
            {
                throw new InvalidOperationException(
                    $"private mip chain ended at {finalWidth}x{finalHeight}, expected 1x1");
            }

            privateFramebuffer = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, privateFramebuffer);
            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.DepthAttachment,
                TextureTarget.Texture2D,
                privateTexture,
                0);
            // A new FBO initially targets colour attachment zero. A depth-only FBO must
            // explicitly disable those FBO-local read/draw colour buffers before its
            // completeness status is meaningful.
            GL.DrawBuffer(DrawBufferMode.None);
            GL.ReadBuffer(ReadBufferMode.None);
            FramebufferErrorCode framebufferStatus =
                GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (framebufferStatus != FramebufferErrorCode.FramebufferComplete)
            {
                throw new InvalidOperationException(
                    "private depth framebuffer is incomplete: " + framebufferStatus);
            }

            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, depth.DrawFramebuffer);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, privateFramebuffer);
            GL.BlitFramebuffer(
                viewport[0],
                viewport[1],
                viewport[0] + width,
                viewport[1] + height,
                0,
                0,
                width,
                height,
                ClearBufferMask.DepthBufferBit,
                BlitFramebufferFilter.Nearest);

            ErrorCode error = GL.GetError();
            if (error != ErrorCode.NoError)
                throw new InvalidOperationException("private depth copy raised " + error);

            result = new LodGpuDepthCopyValidation(
                CopyValidated: true,
                MipAllocationValidated: true,
                StateRestored: false,
                width,
                height,
                levels,
                internalFormat,
                FailureReason: "");
        }
        catch (Exception e)
        {
            result = Failure("private depth-copy probe failed: " + e.Message,
                width, height, levels, internalFormat);
        }
        finally
        {
            string restoreFailure = "";
            bool restored = !stateCaptured || LodGlStateGuard.TryRestore(
                stateApi, state, out restoreFailure);
            if (!restored)
            {
                result = Failure("private depth-copy state restore failed: " + restoreFailure,
                    width, height, levels, internalFormat);
            }
            else if (result.CopyValidated && result.MipAllocationValidated)
            {
                result = result with { StateRestored = true };
            }

            try { if (privateFramebuffer != 0) GL.DeleteFramebuffer(privateFramebuffer); }
            catch { /* Context teardown must continue. */ }
            try { if (privateTexture != 0) GL.DeleteTexture(privateTexture); }
            catch { /* Context teardown must continue. */ }
        }

        return result;
    }

    internal static int CalculateMipLevels(int width, int height)
    {
        if (width <= 0 || height <= 0) return 0;
        int largest = Math.Max(width, height);
        int levels = 1;
        while (largest > 1)
        {
            largest >>= 1;
            levels++;
        }
        return levels;
    }

    internal static bool IsSupportedDepthFormat(int internalFormat) =>
        internalFormat is DepthComponent16
            or DepthComponent24
            or DepthComponent32
            or DepthComponent32F;

    static LodGpuDepthCopyValidation Failure(
        string reason,
        int width = 0,
        int height = 0,
        int levels = 0,
        int internalFormat = 0) =>
        new(false, false, false, width, height, levels, internalFormat, reason);
}
