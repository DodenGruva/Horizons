using OpenTK.Graphics.OpenGL4;

namespace VintageHorizons;

/// <summary>
/// The real arena backend. Every buffer operation goes through GL_COPY_WRITE_BUFFER, which
/// exists precisely so that data transfer cannot disturb the array or element bindings the
/// engine's own renderer depends on - and the element binding in particular is vertex-array
/// state, so touching it would reach into whichever VAO happened to be bound.
///
/// Render thread only, while the cached-terrain stage owns the context. Each call captures
/// and restores the one binding it uses through the shared state guard, so nothing here can
/// leave state behind for the engine to find.
/// </summary>
internal sealed class LodGpuOpenGlArenaBackend : ILodGpuArenaBackend
{
    const BufferTarget Target = BufferTarget.CopyWriteBuffer;

    readonly ILodGlStateApi stateApi;
    readonly Action<string> warn;
    bool reported;

    public int PageAllocationFailures { get; private set; }
    public int TransferFailures { get; private set; }
    public int StateRestoreFailures { get; private set; }

    public LodGpuOpenGlArenaBackend(Action<string> warn, ILodGlStateApi? stateApi = null)
    {
        this.warn = warn;
        this.stateApi = stateApi ?? new LodOpenGlStateApi();
    }

    public int CreatePage(LodGpuArenaKind kind, long bytes)
    {
        if (bytes <= 0 || bytes > int.MaxValue) return 0;

        int handle = 0;
        bool created = WithCopyWriteBinding(() =>
        {
            handle = GL.GenBuffer();
            GL.BindBuffer(Target, handle);
            GL.BufferData(Target, (int)bytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            ErrorCode error = GL.GetError();
            if (error == ErrorCode.NoError) return true;

            Report($"{kind} arena page of {bytes} bytes was refused with {error}");
            GL.DeleteBuffer(handle);
            handle = 0;
            return false;
        });

        if (created && handle != 0) return handle;
        PageAllocationFailures++;
        return 0;
    }

    public unsafe bool Upload(LodGpuArenaKind kind, int page, long offset, ReadOnlySpan<byte> data)
    {
        if (page == 0 || data.Length == 0 || offset < 0) return false;

        bool uploaded;
        fixed (byte* source = data)
        {
            IntPtr pointer = (IntPtr)source;
            int length = data.Length;
            uploaded = WithCopyWriteBinding(() =>
            {
                GL.BindBuffer(Target, page);
                GL.BufferSubData(Target, (IntPtr)offset, length, pointer);
                ErrorCode error = GL.GetError();
                if (error == ErrorCode.NoError) return true;
                Report($"{kind} arena upload of {length} bytes raised {error}");
                return false;
            });
        }

        if (!uploaded) TransferFailures++;
        return uploaded;
    }

    public unsafe bool TryRead(LodGpuArenaKind kind, int page, long offset, Span<byte> destination)
    {
        if (page == 0 || destination.Length == 0 || offset < 0) return false;

        bool read;
        fixed (byte* target = destination)
        {
            IntPtr pointer = (IntPtr)target;
            int length = destination.Length;
            read = WithCopyWriteBinding(() =>
            {
                GL.BindBuffer(Target, page);
                GL.GetBufferSubData(Target, (IntPtr)offset, length, pointer);
                ErrorCode error = GL.GetError();
                if (error == ErrorCode.NoError) return true;
                Report($"{kind} arena readback of {length} bytes raised {error}");
                return false;
            });
        }

        if (!read) TransferFailures++;
        return read;
    }

    public void DeletePage(LodGpuArenaKind kind, int page)
    {
        if (page == 0) return;
        try { GL.DeleteBuffer(page); }
        catch { /* Context teardown must continue. */ }
    }

    public long CreateFence()
    {
        try
        {
            IntPtr sync = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
            return sync.ToInt64();
        }
        catch (Exception e)
        {
            Report("arena fence creation failed: " + e.Message);
            return 0;
        }
    }

    /// <summary>
    /// A zero timeout asks the driver for the current state and returns immediately. A
    /// frame must never block on retirement, so an unfinished fence simply stays pending.
    /// </summary>
    public bool FenceSignaled(long fence)
    {
        if (fence == 0) return false;
        try
        {
            WaitSyncStatus status = GL.ClientWaitSync(
                (IntPtr)fence, ClientWaitSyncFlags.None, 0);
            return status is WaitSyncStatus.AlreadySignaled or WaitSyncStatus.ConditionSatisfied;
        }
        catch (Exception e)
        {
            // A fence that cannot be queried must not strand its bytes forever. Treating it
            // as signaled is safe here only because Phase 2 never draws from these spans.
            Report("arena fence query failed; the span is reclaimed anyway: " + e.Message);
            return true;
        }
    }

    public void DeleteFence(long fence)
    {
        if (fence == 0) return;
        try { GL.DeleteSync((IntPtr)fence); }
        catch { /* Context teardown must continue. */ }
    }

    bool WithCopyWriteBinding(Func<bool> action)
    {
        LodGlStateSnapshot state = LodGlStateGuard.Capture(
            stateApi, LodGlStateMask.CopyWriteBuffer);
        bool result;
        try
        {
            result = action();
        }
        finally
        {
            if (!LodGlStateGuard.TryRestore(stateApi, state, out string failure))
            {
                StateRestoreFailures++;
                Report("arena copy-write binding was not restored exactly: " + failure);
            }
        }
        return result;
    }

    /// <summary>
    /// One warning per session. A driver that refuses one page usually refuses many, and
    /// the shadow mirror has no visible authority to lose either way.
    /// </summary>
    void Report(string message)
    {
        if (reported) return;
        reported = true;
        warn("[VintageHorizons] GPU arena shadow: " + message
            + ". Visible legacy rendering is unchanged.");
    }
}
