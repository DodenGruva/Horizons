using OpenTK.Graphics.OpenGL4;

namespace VintageHorizons;

/// <summary>
/// The real indirect draw backend: one vertex array, one command buffer, one record
/// buffer, and one multi-draw per page set.
///
/// Render thread only, while the cached-terrain stage owns the context. Everything this
/// touches is either private to the mod's own vertex array or captured and restored
/// through the shared state guard, because the engine's renderer runs immediately after
/// and must find its bindings where it left them. Data transfer goes through
/// GL_COPY_WRITE_BUFFER for the same reason the arena backend uses it: an ordinary upload
/// must not disturb the array or element bindings anything else depends on.
/// </summary>
internal sealed class LodGpuOpenGlDrawBackend : ILodGpuDrawBackend
{
    /// <summary>Geometry comes from a page set; the binding is re-pointed per batch.</summary>
    const int GeometryBinding = 0;

    /// <summary>
    /// The section records, bound once for the whole pass. Divisor one plus one instance
    /// per command means each draw reads exactly element `baseInstance` of this buffer,
    /// which is how a multi-draw replaces what used to be a per-section uniform upload.
    /// </summary>
    const int RecordBinding = 1;

    /// <summary>
    /// Shader-storage bindings the cull pass reads and writes. Boxes in, commands out,
    /// mirroring the classifier's own 0-in/1-out arrangement so the two compute passes do
    /// not each invent a convention.
    /// </summary>
    public const int CullBoxBinding = 0;
    public const int CullCommandBinding = 1;

    readonly ILodGlStateApi stateApi;
    readonly Action<string> warn;
    int vertexArray;
    int commandBuffer;
    int recordBuffer;
    int boxBuffer;
    int commandCapacity;
    int recordCapacity;
    int boxCapacity;
    LodGlStateSnapshot state;
    LodGlStateSnapshot cullState;
    bool inPass;
    bool inCull;
    bool reported;

    public LodGpuOpenGlDrawBackend(Action<string> warn, ILodGlStateApi? stateApi = null)
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
        if (vertexArray == 0 || commandBuffer == 0 || recordBuffer == 0 || boxBuffer == 0)
            return Report("the driver refused a vertex array or buffer name");

        LodGlStateSnapshot incoming = LodGlStateGuard.Capture(stateApi, LodGlStateMask.VertexArray);
        try
        {
            GL.BindVertexArray(vertexArray);

            // Geometry, exactly as the arenas store it and exactly as the established
            // per-section MeshRef presents it: three floats of position then four
            // normalised colour bytes, sixteen bytes to a vertex.
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribFormat(0, 3, VertexAttribType.Float, false,
                LodGpuGeometryFormat.PositionOffset);
            GL.VertexAttribBinding(0, GeometryBinding);

            GL.EnableVertexAttribArray(1);
            GL.VertexAttribFormat(1, 4, VertexAttribType.UnsignedByte, true,
                LodGpuGeometryFormat.ColorOffset);
            GL.VertexAttribBinding(1, GeometryBinding);

            // The four record vectors. Offsets come from the record layout rather than
            // from constants repeated here, so a change to the layout cannot leave the
            // shader reading one thing and this reading another.
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribFormat(2, 4, VertexAttribType.Float, false,
                LodGpuSectionRecord.OriginOffset);
            GL.VertexAttribBinding(2, RecordBinding);

            GL.EnableVertexAttribArray(3);
            GL.VertexAttribFormat(3, 4, VertexAttribType.Float, false,
                LodGpuSectionRecord.NoiseOriginOffset);
            GL.VertexAttribBinding(3, RecordBinding);

            // Integer, not float. The mask origin addresses whole vanilla chunks, and a
            // float would round it onto the neighbouring chunk at large world
            // coordinates, which is the same class of bug as G42.
            GL.EnableVertexAttribArray(4);
            GL.VertexAttribIFormat(4, 4, VertexAttribIntegerType.Int,
                LodGpuSectionRecord.MaskOriginOffset);
            GL.VertexAttribBinding(4, RecordBinding);

            GL.EnableVertexAttribArray(5);
            GL.VertexAttribFormat(5, 4, VertexAttribType.Float, false,
                LodGpuSectionRecord.OpenEdgesOffset);
            GL.VertexAttribBinding(5, RecordBinding);

            GL.VertexBindingDivisor(RecordBinding, 1);

            ErrorCode error = GL.GetError();
            if (error != ErrorCode.NoError)
                return Report("vertex array setup raised " + error);
        }
        finally
        {
            Restore(incoming, "vertex array setup");
        }

        return true;
    }

    public bool UploadFrame(ReadOnlySpan<byte> commands, ReadOnlySpan<byte> records)
    {
        if (commands.Length == 0) return false;

        LodGlStateSnapshot incoming = LodGlStateGuard.Capture(
            stateApi, LodGlStateMask.CopyWriteBuffer);
        try
        {
            return Stream(commandBuffer, commands, ref commandCapacity, "commands")
                && Stream(recordBuffer, records, ref recordCapacity, "records");
        }
        finally
        {
            Restore(incoming, "frame upload");
        }
    }

    /// <summary>
    /// Replaces a buffer's contents for this frame. The buffer is reallocated at its
    /// current size first, which tells the driver the old contents are dead and lets it
    /// hand back fresh storage instead of waiting for the previous frame's draw to finish
    /// reading them.
    /// </summary>
    unsafe bool Stream(int buffer, ReadOnlySpan<byte> data, ref int capacity, string what)
    {
        const BufferTarget target = BufferTarget.CopyWriteBuffer;
        GL.BindBuffer(target, buffer);
        if (data.Length > capacity) capacity = Math.Max(data.Length, capacity * 2);
        GL.BufferData(target, capacity, IntPtr.Zero, BufferUsageHint.StreamDraw);
        fixed (byte* source = data)
        {
            GL.BufferSubData(target, IntPtr.Zero, data.Length, (IntPtr)source);
        }

        ErrorCode error = GL.GetError();
        return error == ErrorCode.NoError
            || Report($"uploading {data.Length} bytes of {what} raised {error}");
    }

    public bool BeginCull(ReadOnlySpan<byte> boxes)
    {
        if (commandBuffer == 0 || boxBuffer == 0) return false;
        if (boxes.Length == 0) return false;
        if (inCull) return Report("a cull pass was begun while one was already open");

        // Uploaded through COPY_WRITE like everything else here, so the box upload cannot
        // disturb a binding the engine's renderer expects to still be its own.
        LodGlStateSnapshot incoming = LodGlStateGuard.Capture(
            stateApi, LodGlStateMask.CopyWriteBuffer);
        bool uploaded;
        try
        {
            uploaded = Stream(boxBuffer, boxes, ref boxCapacity, "cull boxes");
        }
        finally
        {
            Restore(incoming, "cull box upload");
        }
        if (!uploaded) return false;

        cullState = LodGlStateGuard.Capture(stateApi, LodGlStateMask.ShaderStorageBuffer);
        inCull = true;

        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, CullBoxBinding, boxBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, CullCommandBinding, commandBuffer);

        ErrorCode error = GL.GetError();
        return error == ErrorCode.NoError || Report("binding the cull pass raised " + error);
    }

    public bool EndCull()
    {
        if (!inCull) return true;
        inCull = false;

        // COMMAND_BARRIER_BIT and nothing else. This is the exact hazard: a shader wrote
        // the buffer, and the next reader is the driver fetching indirect draw parameters
        // out of it. Adding the other bits would hide a mistake about which hazard this is,
        // and cost synchronisation nobody asked for.
        GL.MemoryBarrier(MemoryBarrierFlags.CommandBarrierBit);

        ErrorCode error = GL.GetError();
        if (error != ErrorCode.NoError) return Report("the cull barrier raised " + error);

        return Restore(cullState, "cull pass");
    }

    public bool BeginDraw()
    {
        if (vertexArray == 0) return false;

        state = LodGlStateGuard.Capture(
            stateApi, LodGlStateMask.VertexArray | LodGlStateMask.DrawIndirectBuffer);
        inPass = true;

        GL.BindVertexArray(vertexArray);
        GL.BindVertexBuffer(RecordBinding, recordBuffer, IntPtr.Zero,
            LodGpuSectionRecord.StrideBytes);
        GL.BindBuffer(BufferTarget.DrawIndirectBuffer, commandBuffer);

        ErrorCode error = GL.GetError();
        return error == ErrorCode.NoError || Report("binding the indirect pass raised " + error);
    }

    public bool DrawBatch(int vertexPage, int indexPage, int firstCommand, int commandCount)
    {
        if (!inPass || vertexPage == 0 || indexPage == 0 || commandCount <= 0) return false;

        GL.BindVertexBuffer(GeometryBinding, vertexPage, IntPtr.Zero,
            LodGpuGeometryFormat.VertexStrideBytes);

        // The element binding is vertex-array state, so this writes into our own array
        // and not into whichever one the engine has been using.
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexPage);

        GL.MultiDrawElementsIndirect(
            PrimitiveType.Triangles,
            DrawElementsType.UnsignedInt,
            (IntPtr)((long)firstCommand * LodGpuIndirectCommand.StrideBytes),
            commandCount,
            LodGpuIndirectCommand.StrideBytes);

        ErrorCode error = GL.GetError();
        return error == ErrorCode.NoError
            || Report($"a multi-draw of {commandCount} commands raised {error}");
    }

    public bool EndDraw()
    {
        if (!inPass) return true;
        inPass = false;
        return Restore(state, "indirect pass");
    }

    bool Restore(in LodGlStateSnapshot snapshot, string what)
    {
        if (LodGlStateGuard.TryRestore(stateApi, snapshot, out string failure)) return true;
        return Report(what + " did not restore GL state exactly: " + failure);
    }

    /// <summary>
    /// One warning per session, and always false, so a caller can return it directly. The
    /// drawer turns the first false into a permanent fallback to the established path.
    /// </summary>
    bool Report(string message)
    {
        if (!reported)
        {
            reported = true;
            warn("[VintageHorizons] Indirect draw backend: " + message + ".");
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
            if (vertexArray != 0) GL.DeleteVertexArray(vertexArray);
        }
        catch { /* Context teardown must continue. */ }
        commandBuffer = 0;
        recordBuffer = 0;
        boxBuffer = 0;
        vertexArray = 0;
        commandCapacity = 0;
        recordCapacity = 0;
        boxCapacity = 0;
    }
}
