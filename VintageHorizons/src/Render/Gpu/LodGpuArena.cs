namespace VintageHorizons;

internal enum LodGpuArenaKind
{
    Vertex,
    Index,
}

/// <summary>
/// One allocated span inside a regional page. <see cref="Generation"/> is globally unique
/// within the arena, so a delayed publication that still holds an old range can be
/// rejected even when the same bytes have since been handed to another section.
/// </summary>
internal readonly record struct LodGpuArenaRange(
    int Page,
    long Offset,
    long Length,
    long Generation)
{
    public static readonly LodGpuArenaRange None = new(-1, 0, 0, 0);

    public bool IsLive => Page >= 0 && Length > 0;
}

/// <summary>
/// Every GL operation the arena performs. Keeping it behind an interface is what lets
/// allocation, replacement, fenced retirement and byte-exact content be proven in the
/// fast check tier, where there is no OpenGL context at all.
/// </summary>
internal interface ILodGpuArenaBackend
{
    /// <summary>Returns the page handle, or 0 when the driver refused the allocation.</summary>
    int CreatePage(LodGpuArenaKind kind, long bytes);

    bool Upload(LodGpuArenaKind kind, int page, long offset, ReadOnlySpan<byte> data);

    bool TryRead(LodGpuArenaKind kind, int page, long offset, Span<byte> destination);

    void DeletePage(LodGpuArenaKind kind, int page);

    /// <summary>Returns the fence handle, or 0 when this context cannot fence.</summary>
    long CreateFence();

    bool FenceSignaled(long fence);

    void DeleteFence(long fence);
}

internal readonly record struct LodGpuArenaLimits(
    long PageBytes,
    long CeilingBytes,
    int ReclaimPerFrame);

/// <summary>
/// Fixed-size pages with a coalescing free list inside each, one page per group. A group is
/// the unit a regional multi-draw can address: one indirect batch binds exactly one vertex
/// buffer and one index buffer, so a section's geometry has to sit inside a single page on
/// each side or it could not be drawn with its neighbours. The caller owns how groups map
/// onto the world.
///
/// Retirement is deferred behind a GPU fence and reclaimed in a bounded number of entries
/// per frame: a frame never waits, and a retired span is not reusable until the GPU has
/// passed it.
/// </summary>
internal sealed class LodGpuArena : IDisposable
{
    /// <summary>
    /// Frames a retired span is held when the context cannot create a fence. Deep enough
    /// for an ordinary driver pipeline, and only ever reached on a context that should not
    /// have selected this path in the first place.
    /// </summary>
    internal const int FenceUnavailablePasses = 3;

    sealed class Page
    {
        public required int Slot;
        public required int Handle;
        public required long GroupId;
        public required long Bytes;
        public long Used;
        public readonly List<(long Offset, long Length)> Free = new();
    }

    readonly record struct Pending(LodGpuArenaRange Range, long Fence, int PassesRemaining);

    readonly ILodGpuArenaBackend backend;
    readonly LodGpuArenaLimits limits;
    readonly int alignment;
    readonly Dictionary<int, Page> pages = new();
    readonly Dictionary<long, int> groups = new();
    readonly Queue<Pending> pending = new();
    int nextPageSlot = 1;
    long nextGeneration;
    bool disposed;

    public LodGpuArenaKind Kind { get; }
    public long CommittedBytes { get; private set; }
    public long PendingRetireBytes { get; private set; }

    /// <summary>Allocated and not yet handed back, including spans awaiting their fence.</summary>
    public long UsedBytes { get; private set; }

    public long LiveBytes => UsedBytes - PendingRetireBytes;
    public long FreeBytes => CommittedBytes - UsedBytes;
    public int PageCount => pages.Count;
    public int PendingRetireCount => pending.Count;
    public int AllocationFailures { get; private set; }
    public int CeilingRejections { get; private set; }
    public int OversizeRejections { get; private set; }
    public int BackendFailures { get; private set; }
    public int GroupFull { get; private set; }
    public int FenceUnavailable { get; private set; }
    public int PagesCreated { get; private set; }
    public int PagesDeleted { get; private set; }

    public LodGpuArena(ILodGpuArenaBackend backend, LodGpuArenaKind kind, LodGpuArenaLimits limits)
    {
        if (limits.PageBytes <= 0) throw new ArgumentOutOfRangeException(nameof(limits));
        if (limits.ReclaimPerFrame <= 0) throw new ArgumentOutOfRangeException(nameof(limits));
        this.backend = backend;
        this.limits = limits;
        Kind = kind;
        alignment = kind == LodGpuArenaKind.Vertex
            ? LodGpuGeometryFormat.VertexStrideBytes
            : LodGpuGeometryFormat.IndexStrideBytes;
    }

    /// <summary>
    /// Largest single span still available. Reported beside <see cref="FreeBytes"/> because
    /// the difference between them is the whole of what fragmentation costs.
    /// </summary>
    public long LargestFreeRange
    {
        get
        {
            long largest = 0;
            foreach (Page page in pages.Values)
            {
                foreach ((long _, long length) in page.Free)
                    if (length > largest) largest = length;
            }
            return largest;
        }
    }

    public double Fragmentation
    {
        get
        {
            long free = FreeBytes;
            return free <= 0 ? 0 : 1.0 - LargestFreeRange / (double)free;
        }
    }

    /// <summary>
    /// Allocates inside this group's single page, creating it on first use. A group whose
    /// page is full fails rather than spilling into a second page: the caller is expected
    /// to move to another group, because a span that lands elsewhere could not join the
    /// same indirect batch.
    /// </summary>
    public bool TryAllocate(long groupId, long bytes, out LodGpuArenaRange range)
    {
        range = LodGpuArenaRange.None;
        if (disposed || bytes <= 0) return false;

        long needed = Align(bytes);
        if (needed > limits.PageBytes)
        {
            OversizeRejections++;
            AllocationFailures++;
            return false;
        }

        if (groups.TryGetValue(groupId, out int existing))
        {
            if (TryCarve(pages[existing], needed, out range)) return true;

            // Not an allocation failure: the caller is expected to try another group, and
            // counting these as failures buried the real refusals under thousands of them.
            GroupFull++;
            return false;
        }

        if (CommittedBytes + limits.PageBytes > limits.CeilingBytes)
        {
            CeilingRejections++;
            AllocationFailures++;
            return false;
        }

        int handle = backend.CreatePage(Kind, limits.PageBytes);
        if (handle == 0)
        {
            BackendFailures++;
            AllocationFailures++;
            return false;
        }

        var created = new Page
        {
            Slot = nextPageSlot++,
            Handle = handle,
            GroupId = groupId,
            Bytes = limits.PageBytes,
        };
        created.Free.Add((0, limits.PageBytes));
        pages[created.Slot] = created;
        groups[groupId] = created.Slot;
        CommittedBytes += limits.PageBytes;
        PagesCreated++;
        return TryCarve(created, needed, out range);
    }

    /// <summary>
    /// Returns a span that was allocated and then never published. No frame can hold a
    /// reference to bytes that never left this method's caller, so there is nothing for a
    /// fence to wait for.
    /// </summary>
    public void Abandon(in LodGpuArenaRange range)
    {
        if (range.IsLive) Release(range);
    }

    /// <summary>
    /// The backend object holding this span, or 0 when the span is not current. A regional
    /// draw needs the buffer name, not just the offset.
    /// </summary>
    public int PageHandle(in LodGpuArenaRange range) =>
        IsCurrent(range) ? pages[range.Page].Handle : 0;

    public bool Upload(in LodGpuArenaRange range, ReadOnlySpan<byte> data)
    {
        if (!IsCurrent(range) || data.Length > range.Length) return false;
        return backend.Upload(Kind, pages[range.Page].Handle, range.Offset, data);
    }

    public bool TryRead(in LodGpuArenaRange range, Span<byte> destination)
    {
        if (!IsCurrent(range) || destination.Length > range.Length) return false;
        return backend.TryRead(Kind, pages[range.Page].Handle, range.Offset, destination);
    }

    /// <summary>
    /// Hands a span back to the GPU's timeline rather than to the free list. Until the
    /// fence signals, those bytes cannot be allocated again, so an in-flight frame that
    /// still references them cannot observe another section's geometry.
    /// </summary>
    public void Retire(in LodGpuArenaRange range)
    {
        if (!range.IsLive || !pages.ContainsKey(range.Page)) return;
        long fence = backend.CreateFence();
        if (fence == 0) FenceUnavailable++;
        pending.Enqueue(new Pending(range, fence, fence == 0 ? FenceUnavailablePasses : 0));
        PendingRetireBytes += range.Length;
    }

    /// <summary>
    /// Bounded per-frame reclamation. Each call examines at most its budget of entries and
    /// never blocks: an unsignaled fence goes back on the queue untouched.
    /// </summary>
    public int Reclaim()
    {
        int scan = Math.Min(limits.ReclaimPerFrame, pending.Count);
        int reclaimed = 0;
        for (int i = 0; i < scan; i++)
        {
            Pending entry = pending.Dequeue();
            bool ready = entry.Fence != 0
                ? backend.FenceSignaled(entry.Fence)
                : entry.PassesRemaining <= 0;
            if (!ready)
            {
                pending.Enqueue(entry with { PassesRemaining = entry.PassesRemaining - 1 });
                continue;
            }

            if (entry.Fence != 0) backend.DeleteFence(entry.Fence);
            PendingRetireBytes -= entry.Range.Length;
            Release(entry.Range);
            reclaimed++;
        }
        return reclaimed;
    }

    /// <summary>
    /// Drops every page and every pending fence. A world change must leave no committed
    /// bytes behind, so this is deliberately not a reclaim with a larger budget.
    /// </summary>
    public void Clear()
    {
        while (pending.Count > 0)
        {
            Pending entry = pending.Dequeue();
            if (entry.Fence != 0)
            {
                try { backend.DeleteFence(entry.Fence); }
                catch { /* Context teardown must continue. */ }
            }
        }

        foreach (Page page in pages.Values)
        {
            try { backend.DeletePage(Kind, page.Handle); }
            catch { /* Context teardown must continue. */ }
            PagesDeleted++;
        }

        pages.Clear();
        groups.Clear();
        CommittedBytes = 0;
        UsedBytes = 0;
        PendingRetireBytes = 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Clear();
    }

    bool IsCurrent(in LodGpuArenaRange range) =>
        range.IsLive && pages.ContainsKey(range.Page);

    long Align(long bytes) => (bytes + alignment - 1) / alignment * alignment;

    bool TryCarve(Page page, long needed, out LodGpuArenaRange range)
    {
        for (int i = 0; i < page.Free.Count; i++)
        {
            (long offset, long length) = page.Free[i];
            if (length < needed) continue;

            if (length == needed) page.Free.RemoveAt(i);
            else page.Free[i] = (offset + needed, length - needed);

            page.Used += needed;
            UsedBytes += needed;
            range = new LodGpuArenaRange(page.Slot, offset, needed, ++nextGeneration);
            return true;
        }

        range = LodGpuArenaRange.None;
        return false;
    }

    /// <summary>
    /// Returns reclaimed bytes to the page's free list, merging with either neighbour. A
    /// span that is already free is a stale-reuse bug rather than a tolerable no-op, so it
    /// throws and the mirror's fault isolation disables mirroring instead of quietly
    /// handing the same bytes to two sections.
    /// </summary>
    void Release(in LodGpuArenaRange range)
    {
        if (!pages.TryGetValue(range.Page, out Page? page)) return;

        int index = 0;
        while (index < page.Free.Count && page.Free[index].Offset < range.Offset) index++;

        if (index < page.Free.Count && page.Free[index].Offset < range.Offset + range.Length)
            throw new InvalidOperationException("arena span was released while already free");
        if (index > 0)
        {
            (long previousOffset, long previousLength) = page.Free[index - 1];
            if (previousOffset + previousLength > range.Offset)
                throw new InvalidOperationException("arena span was released while already free");
        }

        page.Free.Insert(index, (range.Offset, range.Length));
        page.Used -= range.Length;
        UsedBytes -= range.Length;
        Coalesce(page, index);

        if (page.Used == 0) DeletePage(range.Page, page);
    }

    static void Coalesce(Page page, int index)
    {
        if (index + 1 < page.Free.Count)
        {
            (long offset, long length) = page.Free[index];
            (long nextOffset, long nextLength) = page.Free[index + 1];
            if (offset + length == nextOffset)
            {
                page.Free[index] = (offset, length + nextLength);
                page.Free.RemoveAt(index + 1);
            }
        }

        if (index > 0)
        {
            (long previousOffset, long previousLength) = page.Free[index - 1];
            (long offset, long length) = page.Free[index];
            if (previousOffset + previousLength == offset)
            {
                page.Free[index - 1] = (previousOffset, previousLength + length);
                page.Free.RemoveAt(index);
            }
        }
    }

    void DeletePage(int slot, Page page)
    {
        backend.DeletePage(Kind, page.Handle);
        pages.Remove(slot);
        groups.Remove(page.GroupId);
        CommittedBytes -= page.Bytes;
        PagesDeleted++;
    }
}

internal enum LodGpuArenaMode
{
    Off,
    On,
    Verify,
    Invalid,
}

/// <summary>
/// Pure Phase 2 development controls. The arena mirror is opt-in beneath the renderer
/// shadow and carries its own memory ceiling, because dual residency would otherwise let
/// shadow validation double an unbounded geometry cache.
/// </summary>
internal static class LodGpuArenaPolicy
{
    internal const long DefaultCeilingBytes = 256L * 1024 * 1024;
    internal const long MinimumCeilingBytes = 16L * 1024 * 1024;
    internal const long MaximumCeilingBytes = 4096L * 1024 * 1024;
    internal const long DefaultVertexPageBytes = 8L * 1024 * 1024;
    internal const long MinimumVertexPageBytes = 1L * 1024 * 1024;
    internal const long MaximumVertexPageBytes = 128L * 1024 * 1024;
    internal const int ReclaimPerFrame = 8;

    /// <summary>
    /// Page size is the lever on batch count: a batch is one page set, so the number of
    /// drawn sections that fit in a page is the number that can be drawn together.
    /// Configurable rather than compiled in, because finding the right value is a
    /// measurement and should not cost a build each time.
    /// </summary>
    public static long VertexPageBytes { get; private set; } = DefaultVertexPageBytes;

    /// <summary>
    /// Half the vertex page. Expanded geometry is four 16-byte vertices and six 32-bit
    /// indices per quad, so indices need roughly half the room; pairing then keeps both
    /// arenas at the same page count.
    /// </summary>
    public static long IndexPageBytes => VertexPageBytes / 2;

    public static void ConfigurePageBytes(string? vertexPageMegabytes)
    {
        VertexPageBytes = long.TryParse(vertexPageMegabytes, out long value)
            ? Math.Clamp(value * 1024 * 1024, MinimumVertexPageBytes, MaximumVertexPageBytes)
            : DefaultVertexPageBytes;
    }

    public static LodGpuArenaMode Parse(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Equals("on", StringComparison.OrdinalIgnoreCase)
            ? LodGpuArenaMode.On
            : value.Equals("off", StringComparison.OrdinalIgnoreCase)
                ? LodGpuArenaMode.Off
                : value.Equals("verify", StringComparison.OrdinalIgnoreCase)
                    ? LodGpuArenaMode.Verify
                    : LodGpuArenaMode.Invalid;

    /// <summary>
    /// The ceiling covers vertices and indices together. An unreadable or out-of-range
    /// value falls back to the default rather than to no limit at all.
    /// </summary>
    public static long CeilingBytes(string? megabytes)
    {
        if (!long.TryParse(megabytes, out long value)) return DefaultCeilingBytes;
        long bytes = value <= long.MaxValue / (1024 * 1024) ? value * 1024 * 1024 : MaximumCeilingBytes;
        return Math.Clamp(bytes, MinimumCeilingBytes, MaximumCeilingBytes);
    }

    public static LodGpuArenaLimits VertexLimits(long ceilingBytes) =>
        new(VertexPageBytes, PageSets(ceilingBytes) * VertexPageBytes, ReclaimPerFrame);

    public static LodGpuArenaLimits IndexLimits(long ceilingBytes) =>
        new(IndexPageBytes, PageSets(ceilingBytes) * IndexPageBytes, ReclaimPerFrame);

    /// <summary>
    /// Page sets the ceiling affords. The split between the two arenas has to follow the
    /// page sizes, not the byte ratio of the geometry: pages are allocated in pairs, so the
    /// arena that runs out of pages first caps both, however much room its bytes still had.
    /// Splitting 70/30 by bytes did exactly that - the index arena refused new sets at 60%
    /// full while the vertex arena still had three pages of headroom.
    /// </summary>
    internal static long PageSets(long ceilingBytes) =>
        Math.Max(1, ceilingBytes / (VertexPageBytes + IndexPageBytes));
}
