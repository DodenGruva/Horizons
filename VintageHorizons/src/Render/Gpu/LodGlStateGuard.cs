using OpenTK.Graphics.OpenGL4;

namespace VintageHorizons;

[Flags]
internal enum LodGlStateMask
{
    None = 0,
    Program = 1 << 0,
    ShaderStorageBuffer = 1 << 1,
    Framebuffers = 1 << 2,
    Texture2DUnit0 = 1 << 3,
    CopyWriteBuffer = 1 << 4,
}

internal interface ILodGlStateApi
{
    int GetProgram();
    void UseProgram(int value);
    int GetGenericShaderStorageBuffer();
    int GetIndexedShaderStorageBuffer0();
    void BindIndexedShaderStorageBuffer0(int value);
    void BindGenericShaderStorageBuffer(int value);
    int GetDrawFramebuffer();
    int GetReadFramebuffer();
    void BindDrawFramebuffer(int value);
    void BindReadFramebuffer(int value);
    int GetActiveTexture();
    void SetActiveTexture(int value);
    int GetTexture2D();
    void BindTexture2D(int value);
    int GetCopyWriteBuffer();
    void BindCopyWriteBuffer(int value);
}

internal sealed class LodOpenGlStateApi : ILodGlStateApi
{
    // GL_COPY_WRITE_BUFFER and GL_COPY_WRITE_BUFFER_BINDING share one token value, and the
    // game's OpenTK binding does not name the query form. Spelled out for the same reason
    // as the shader-storage size token in LodGpuCapabilities.
    const int CopyWriteBufferBindingToken = 0x8F37;

    public int GetProgram() => GL.GetInteger(GetPName.CurrentProgram);
    public void UseProgram(int value) => GL.UseProgram(value);
    public int GetGenericShaderStorageBuffer() =>
        GL.GetInteger(GetPName.ShaderStorageBufferBinding);
    public int GetIndexedShaderStorageBuffer0()
    {
        GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding, 0, out int value);
        return value;
    }
    public void BindIndexedShaderStorageBuffer0(int value) =>
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, value);
    public void BindGenericShaderStorageBuffer(int value) =>
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, value);
    public int GetDrawFramebuffer() => GL.GetInteger(GetPName.DrawFramebufferBinding);
    public int GetReadFramebuffer() => GL.GetInteger(GetPName.ReadFramebufferBinding);
    public void BindDrawFramebuffer(int value) =>
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, value);
    public void BindReadFramebuffer(int value) =>
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, value);
    public int GetActiveTexture() => GL.GetInteger(GetPName.ActiveTexture);
    public void SetActiveTexture(int value) => GL.ActiveTexture((TextureUnit)value);
    public int GetTexture2D() => GL.GetInteger(GetPName.TextureBinding2D);
    public void BindTexture2D(int value) => GL.BindTexture(TextureTarget.Texture2D, value);
    public int GetCopyWriteBuffer() => GL.GetInteger((GetPName)CopyWriteBufferBindingToken);
    public void BindCopyWriteBuffer(int value) =>
        GL.BindBuffer(BufferTarget.CopyWriteBuffer, value);
}

internal readonly record struct LodGlStateSnapshot(
    LodGlStateMask Mask,
    int Program,
    int GenericShaderStorageBuffer,
    int IndexedShaderStorageBuffer0,
    int DrawFramebuffer,
    int ReadFramebuffer,
    int ActiveTexture,
    int Texture2DUnit0,
    int CopyWriteBuffer = 0);

/// <summary>Exact capture, ordered restore, and verification for GPU-owned GL state.</summary>
internal static class LodGlStateGuard
{
    internal const int Texture0 = 0x84C0;

    public static LodGlStateSnapshot Capture(ILodGlStateApi api, LodGlStateMask mask)
    {
        int program = 0;
        int generalSsbo = 0;
        int indexedSsbo = 0;
        int drawFramebuffer = 0;
        int readFramebuffer = 0;
        int activeTexture = 0;
        int texture2DUnit0 = 0;
        int copyWriteBuffer = 0;

        if ((mask & LodGlStateMask.Program) != 0) program = api.GetProgram();
        if ((mask & LodGlStateMask.ShaderStorageBuffer) != 0)
        {
            generalSsbo = api.GetGenericShaderStorageBuffer();
            indexedSsbo = api.GetIndexedShaderStorageBuffer0();
        }
        if ((mask & LodGlStateMask.Framebuffers) != 0)
        {
            drawFramebuffer = api.GetDrawFramebuffer();
            readFramebuffer = api.GetReadFramebuffer();
        }
        if ((mask & LodGlStateMask.Texture2DUnit0) != 0)
        {
            activeTexture = api.GetActiveTexture();
            try
            {
                api.SetActiveTexture(Texture0);
                texture2DUnit0 = api.GetTexture2D();
            }
            finally { api.SetActiveTexture(activeTexture); }
        }
        if ((mask & LodGlStateMask.CopyWriteBuffer) != 0)
            copyWriteBuffer = api.GetCopyWriteBuffer();

        return new(mask, program, generalSsbo, indexedSsbo, drawFramebuffer,
            readFramebuffer, activeTexture, texture2DUnit0, copyWriteBuffer);
    }

    public static bool TryRestore(
        ILodGlStateApi api, in LodGlStateSnapshot state, out string failure)
    {
        try
        {
            if ((state.Mask & LodGlStateMask.Program) != 0)
                api.UseProgram(state.Program);
            if ((state.Mask & LodGlStateMask.ShaderStorageBuffer) != 0)
            {
                // BindBufferBase also changes the generic binding. Indexed must be first.
                api.BindIndexedShaderStorageBuffer0(state.IndexedShaderStorageBuffer0);
                api.BindGenericShaderStorageBuffer(state.GenericShaderStorageBuffer);
            }
            if ((state.Mask & LodGlStateMask.Framebuffers) != 0)
            {
                api.BindDrawFramebuffer(state.DrawFramebuffer);
                api.BindReadFramebuffer(state.ReadFramebuffer);
            }
            if ((state.Mask & LodGlStateMask.Texture2DUnit0) != 0)
            {
                api.SetActiveTexture(Texture0);
                api.BindTexture2D(state.Texture2DUnit0);
                api.SetActiveTexture(state.ActiveTexture);
            }
            if ((state.Mask & LodGlStateMask.CopyWriteBuffer) != 0)
                api.BindCopyWriteBuffer(state.CopyWriteBuffer);

            if ((state.Mask & LodGlStateMask.Program) != 0
                && api.GetProgram() != state.Program)
                return Fail("program binding did not match its incoming value", out failure);
            if ((state.Mask & LodGlStateMask.ShaderStorageBuffer) != 0
                && (api.GetGenericShaderStorageBuffer() != state.GenericShaderStorageBuffer
                    || api.GetIndexedShaderStorageBuffer0() != state.IndexedShaderStorageBuffer0))
                return Fail("generic or indexed SSBO binding did not match its incoming value",
                    out failure);
            if ((state.Mask & LodGlStateMask.Framebuffers) != 0
                && (api.GetDrawFramebuffer() != state.DrawFramebuffer
                    || api.GetReadFramebuffer() != state.ReadFramebuffer))
                return Fail("framebuffer bindings did not match their incoming values", out failure);
            if ((state.Mask & LodGlStateMask.Texture2DUnit0) != 0)
            {
                int active = api.GetActiveTexture();
                int texture;
                try
                {
                    api.SetActiveTexture(Texture0);
                    texture = api.GetTexture2D();
                }
                finally { api.SetActiveTexture(active); }
                if (texture != state.Texture2DUnit0)
                    return Fail("texture-unit-zero binding did not match its incoming value",
                        out failure);
                if (api.GetActiveTexture() != state.ActiveTexture)
                    return Fail("active texture unit did not match its incoming value", out failure);
            }
            if ((state.Mask & LodGlStateMask.CopyWriteBuffer) != 0
                && api.GetCopyWriteBuffer() != state.CopyWriteBuffer)
                return Fail("copy-write buffer binding did not match its incoming value",
                    out failure);

            failure = "";
            return true;
        }
        catch (Exception e)
        {
            failure = e.Message;
            return false;
        }
    }

    static bool Fail(string reason, out string failure)
    {
        failure = reason;
        return false;
    }
}
