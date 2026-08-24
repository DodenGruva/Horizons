using System.Buffers.Binary;
using OpenTK.Graphics.OpenGL4;

namespace VintageHorizons;

/// <summary>
/// Packed-quad counterpart to <see cref="LodGpuOpenGlDrawBackend"/>. Section records keep
/// their existing instanced attributes; geometry is pulled from one tightly-packed SSBO
/// page with gl_VertexID. A reusable index pattern turns each record into four unique
/// corners and six triangle indices, avoiding the first experiment's six full shader
/// invocations while retaining MultiDrawElementsIndirect and the established cull layout.
/// </summary>
internal sealed class LodGpuOpenGlPackedDrawBackend : ILodGpuDrawBackend
{
    const int RecordBinding = 1;
    const int PackedQuadBinding = 0;

    readonly ILodGlStateApi stateApi;
    readonly Action<string> warn;
    int vertexArray;
    int commandBuffer;
    int recordBuffer;
    int boxBuffer;
    int sharedIndexBuffer;
    int commandCapacity;
    int recordCapacity;
    int boxCapacity;
    int sharedIndexQuads;
    LodGlStateSnapshot state;
    LodGlStateSnapshot cullState;
    bool inPass;
    bool inCull;
    bool reported;

    public LodGpuOpenGlPackedDrawBackend(Action<string> warn, ILodGlStateApi? stateApi = null)
    {
        this.warn = warn;
        this.stateApi = stateApi ?? new LodOpenGlStateApi();
    }

    public bool Create()
    {
        if (vertexArray != 0) return true;
        vertexArray = GL.GenVertexArray();
        commandBuffer = GL.GenBuffer();
        recordBuffer = GL.GenBuffer();
        boxBuffer = GL.GenBuffer();
        sharedIndexBuffer = GL.GenBuffer();
        if (vertexArray == 0 || commandBuffer == 0 || recordBuffer == 0 || boxBuffer == 0
            || sharedIndexBuffer == 0)
            return Report("the driver refused a packed vertex array or buffer name");

        LodGlStateSnapshot incoming = LodGlStateGuard.Capture(stateApi, LodGlStateMask.VertexArray);
        try
        {
            GL.BindVertexArray(vertexArray);
            BindRecordAttribute(2, LodGpuSectionRecord.OriginOffset, integer: false);
            BindRecordAttribute(3, LodGpuSectionRecord.NoiseOriginOffset, integer: false);
            BindRecordAttribute(4, LodGpuSectionRecord.MaskOriginOffset, integer: true);
            BindRecordAttribute(5, LodGpuSectionRecord.OpenEdgesOffset, integer: false);
            GL.VertexBindingDivisor(RecordBinding, 1);

            ErrorCode error = GL.GetError();
            if (error != ErrorCode.NoError)
                return Report("packed vertex array setup raised " + error);
        }
        finally { Restore(incoming, "packed vertex array setup"); }

        return true;
    }

    void BindRecordAttribute(int location, int offset, bool integer)
    {
        GL.EnableVertexAttribArray(location);
        if (integer)
            GL.VertexAttribIFormat(location, 4, VertexAttribIntegerType.Int, offset);
        else
            GL.VertexAttribFormat(location, 4, VertexAttribType.Float, false, offset);
        GL.VertexAttribBinding(location, RecordBinding);
    }

    public bool UploadFrame(ReadOnlySpan<byte> commands, ReadOnlySpan<byte> records)
    {
        if (commands.Length == 0) return false;
        LodGlStateSnapshot incoming = LodGlStateGuard.Capture(
            stateApi, LodGlStateMask.CopyWriteBuffer);
        try
        {
            if (!TryRequiredQuadCapacity(commands, out int requiredQuads)) return false;
            return EnsureSharedIndices(requiredQuads)
                && Stream(commandBuffer, commands, ref commandCapacity, "packed commands")
                && Stream(recordBuffer, records, ref recordCapacity, "packed records");
        }
        finally { Restore(incoming, "packed frame upload"); }
    }

    bool TryRequiredQuadCapacity(ReadOnlySpan<byte> commands, out int requiredQuads)
    {
        requiredQuads = 0;
        if (commands.Length % LodGpuPackedIndirectCommand.StrideBytes != 0)
            return Report("the packed command upload had a partial command slot");

        for (int offset = 0; offset < commands.Length;
             offset += LodGpuPackedIndirectCommand.StrideBytes)
        {
            uint indexCount = BinaryPrimitives.ReadUInt32LittleEndian(
                commands[(offset + LodGpuPackedIndirectCommand.CountOffset)..]);
            if (indexCount % LodPackedQuadFormat.IndicesPerQuad != 0
                || indexCount / LodPackedQuadFormat.IndicesPerQuad > int.MaxValue)
                return Report("a packed command had an invalid reusable-index count");
            requiredQuads = Math.Max(requiredQuads,
                (int)(indexCount / LodPackedQuadFormat.IndicesPerQuad));
        }
        return requiredQuads > 0;
    }

    unsafe bool EnsureSharedIndices(int requiredQuads)
    {
        if (requiredQuads <= sharedIndexQuads) return true;

        int grown = Math.Max(256, sharedIndexQuads);
        while (grown < requiredQuads)
        {
            if (grown > int.MaxValue / 2)
            {
                grown = requiredQuads;
                break;
            }
            grown *= 2;
        }

        int indexCount;
        try { indexCount = checked(grown * LodPackedQuadFormat.IndicesPerQuad); }
        catch (OverflowException) { return Report("the reusable packed index pattern was too large"); }
        var indices = new uint[indexCount];
        LodGpuPackedIndexPattern.Fill(indices, grown);

        const BufferTarget target = BufferTarget.CopyWriteBuffer;
        GL.BindBuffer(target, sharedIndexBuffer);
        fixed (uint* source = indices)
            GL.BufferData(target, indexCount * sizeof(uint), (IntPtr)source,
                BufferUsageHint.StaticDraw);
        ErrorCode error = GL.GetError();
        if (error != ErrorCode.NoError)
            return Report($"uploading the reusable packed index pattern raised {error}");
        sharedIndexQuads = grown;
        return true;
    }

    unsafe bool Stream(int buffer, ReadOnlySpan<byte> data, ref int capacity, string what)
    {
        const BufferTarget target = BufferTarget.CopyWriteBuffer;
        GL.BindBuffer(target, buffer);
        if (data.Length > capacity) capacity = Math.Max(data.Length, capacity * 2);
        GL.BufferData(target, capacity, IntPtr.Zero, BufferUsageHint.StreamDraw);
        fixed (byte* source = data)
            GL.BufferSubData(target, IntPtr.Zero, data.Length, (IntPtr)source);
        ErrorCode error = GL.GetError();
        return error == ErrorCode.NoError
            || Report($"uploading {data.Length} bytes of {what} raised {error}");
    }

    public bool BeginCull(ReadOnlySpan<byte> boxes)
    {
        if (commandBuffer == 0 || boxBuffer == 0 || boxes.Length == 0) return false;
        if (inCull) return Report("a packed cull pass was begun while one was already open");

        LodGlStateSnapshot incoming = LodGlStateGuard.Capture(
            stateApi, LodGlStateMask.CopyWriteBuffer);
        bool uploaded;
        try { uploaded = Stream(boxBuffer, boxes, ref boxCapacity, "packed cull boxes"); }
        finally { Restore(incoming, "packed cull box upload"); }
        if (!uploaded) return false;

        cullState = LodGlStateGuard.Capture(stateApi, LodGlStateMask.ShaderStorageBuffer);
        inCull = true;
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
            LodGpuOpenGlDrawBackend.CullBoxBinding, boxBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
            LodGpuOpenGlDrawBackend.CullCommandBinding, commandBuffer);
        ErrorCode error = GL.GetError();
        return error == ErrorCode.NoError || Report("binding the packed cull pass raised " + error);
    }

    public bool EndCull()
    {
        if (!inCull) return true;
        inCull = false;
        GL.MemoryBarrier(MemoryBarrierFlags.CommandBarrierBit);
        ErrorCode error = GL.GetError();
        if (error != ErrorCode.NoError) return Report("the packed cull barrier raised " + error);
        return Restore(cullState, "packed cull pass");
    }

    public bool BeginDraw()
    {
        if (vertexArray == 0) return false;
        state = LodGlStateGuard.Capture(stateApi,
            LodGlStateMask.VertexArray | LodGlStateMask.DrawIndirectBuffer
            | LodGlStateMask.ShaderStorageBuffer);
        inPass = true;
        GL.BindVertexArray(vertexArray);
        // Element-array binding is state of this private VAO. Restoring the caller's VAO
        // restores its own element buffer without a separate global binding mutation.
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, sharedIndexBuffer);
        GL.BindVertexBuffer(RecordBinding, recordBuffer, IntPtr.Zero,
            LodGpuSectionRecord.StrideBytes);
        GL.BindBuffer(BufferTarget.DrawIndirectBuffer, commandBuffer);
        ErrorCode error = GL.GetError();
        return error == ErrorCode.NoError || Report("binding the packed indirect pass raised " + error);
    }

    public bool DrawBatch(int packedPage, int unusedIndexPage, int firstCommand, int commandCount)
    {
        if (!inPass || packedPage == 0 || commandCount <= 0) return false;
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, PackedQuadBinding, packedPage);
        GL.MultiDrawElementsIndirect(
            PrimitiveType.Triangles,
            DrawElementsType.UnsignedInt,
            (IntPtr)((long)firstCommand * LodGpuPackedIndirectCommand.StrideBytes),
            commandCount,
            LodGpuPackedIndirectCommand.StrideBytes);
        ErrorCode error = GL.GetError();
        return error == ErrorCode.NoError
            || Report($"a packed multi-draw of {commandCount} commands raised {error}");
    }

    public bool EndDraw()
    {
        if (!inPass) return true;
        inPass = false;
        return Restore(state, "packed indirect pass");
    }

    bool Restore(in LodGlStateSnapshot snapshot, string what)
    {
        if (LodGlStateGuard.TryRestore(stateApi, snapshot, out string failure)) return true;
        return Report(what + " did not restore GL state exactly: " + failure);
    }

    bool Report(string message)
    {
        if (!reported)
        {
            reported = true;
            warn("[VintageHorizons] Packed indirect draw backend: " + message + ".");
        }
        return false;
    }

    public void Dispose()
    {
        inPass = false;
        inCull = false;
        try
        {
            if (commandBuffer != 0) GL.DeleteBuffer(commandBuffer);
            if (recordBuffer != 0) GL.DeleteBuffer(recordBuffer);
            if (boxBuffer != 0) GL.DeleteBuffer(boxBuffer);
            if (sharedIndexBuffer != 0) GL.DeleteBuffer(sharedIndexBuffer);
            if (vertexArray != 0) GL.DeleteVertexArray(vertexArray);
        }
        catch { }
        commandBuffer = recordBuffer = boxBuffer = sharedIndexBuffer = vertexArray = 0;
        commandCapacity = recordCapacity = boxCapacity = 0;
        sharedIndexQuads = 0;
    }
}
