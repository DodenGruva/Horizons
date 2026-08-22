namespace VintageHorizons.Checks;

/// <summary>
/// Phase 3 groundwork: the byte layouts GLSL will read, and the command list built from the
/// CPU's already-approved candidates. Nothing here draws. The layout assertions exist
/// because a stride or offset disagreement between C# and GLSL misaddresses every section
/// at once and looks like a geometry bug rather than a packing one.
/// </summary>
public static class GpuIndirectChecks
{
    public static void Run(Check c)
    {
        RecordLayout(c);
        CommandLayout(c);
        Batching(c);
        BatchOrdering(c);
        PagePairing(c);
    }

    static void RecordLayout(Check c)
    {
        c.Eq(64, LodGpuSectionRecord.StrideBytes,
            "a section record is four std430 vec4s");
        c.Eq(0, LodGpuSectionRecord.OriginOffset, "the camera-relative origin leads the record");
        c.Eq(12, LodGpuSectionRecord.SectionSizeOffset,
            "section size completes the first vec4 rather than starting a new one");
        c.Eq(16, LodGpuSectionRecord.NoiseOriginOffset, "the stable world origin is the second vec4");
        c.Eq(24, LodGpuSectionRecord.ColumnBlocksOffset, "column blocks share the second vec4");
        c.Eq(32, LodGpuSectionRecord.MaskOriginOffset, "the mask origin is the third vec4, as integers");
        c.Eq(48, LodGpuSectionRecord.OpenEdgesOffset, "open edges are the fourth vec4");
        c.True(LodGpuSectionRecord.StrideBytes % 16 == 0,
            "the record stride is a multiple of the std430 vec4 alignment");

        var facts = new LodGpuSectionFacts(
            SectionKey: 42,
            OriginRelX: -1024.5f, OriginRelY: -70.25f, OriginRelZ: 2048.75f,
            SectionSize: 256f,
            NoiseOriginX: 512000f, NoiseOriginZ: -512000f,
            ColumnBlocks: 4f,
            MaskOriginX: 16000, MaskOriginZ: -16000,
            OpenEdges: (byte)(LodGpuSectionFacts.OpenMinusX | LodGpuSectionFacts.OpenPlusZ));

        var bytes = new byte[LodGpuSectionRecord.StrideBytes];
        LodGpuSectionRecord.Encode(facts, bytes);

        c.Eq(-1024.5f, LodGpuSectionRecord.ReadFloat(bytes, 0, LodGpuSectionRecord.OriginOffset),
            "the camera-relative origin round-trips exactly");
        c.Eq(-70.25f, LodGpuSectionRecord.ReadFloat(bytes, 0, LodGpuSectionRecord.OriginOffset + 4),
            "the camera-relative height round-trips exactly");
        c.Eq(256f, LodGpuSectionRecord.ReadFloat(bytes, 0, LodGpuSectionRecord.SectionSizeOffset),
            "section size round-trips exactly");
        c.Eq(512000f, LodGpuSectionRecord.ReadFloat(bytes, 0, LodGpuSectionRecord.NoiseOriginOffset),
            "an extreme world origin round-trips exactly");
        c.Eq(4f, LodGpuSectionRecord.ReadFloat(bytes, 0, LodGpuSectionRecord.ColumnBlocksOffset),
            "column blocks round-trip exactly");
        c.Eq(16000, LodGpuSectionRecord.ReadInt(bytes, 0, LodGpuSectionRecord.MaskOriginOffset),
            "the mask origin stays an integer, as the established shader requires");
        c.Eq(-16000, LodGpuSectionRecord.ReadInt(bytes, 0, LodGpuSectionRecord.MaskOriginOffset + 4),
            "a negative mask origin round-trips exactly");

        c.Eq(1f, LodGpuSectionRecord.ReadFloat(bytes, 0, LodGpuSectionRecord.OpenEdgesOffset),
            "an open -X side becomes 1, matching the openEdges uniform");
        c.Eq(0f, LodGpuSectionRecord.ReadFloat(bytes, 0, LodGpuSectionRecord.OpenEdgesOffset + 4),
            "a closed +X side becomes 0");
        c.Eq(0f, LodGpuSectionRecord.ReadFloat(bytes, 0, LodGpuSectionRecord.OpenEdgesOffset + 8),
            "a closed -Z side becomes 0");
        c.Eq(1f, LodGpuSectionRecord.ReadFloat(bytes, 0, LodGpuSectionRecord.OpenEdgesOffset + 12),
            "an open +Z side becomes 1, so the side order is -X, +X, -Z, +Z");

        c.Throws<ArgumentException>(
            () => LodGpuSectionRecord.Encode(facts, new byte[LodGpuSectionRecord.StrideBytes - 1]),
            "a short record destination is refused rather than partly written");
    }

    static void CommandLayout(Check c)
    {
        c.Eq(20, LodGpuIndirectCommand.StrideBytes,
            "a draw-elements indirect command is five 32-bit words");
        c.Eq(0, LodGpuIndirectCommand.CountOffset, "index count leads the command");
        c.Eq(4, LodGpuIndirectCommand.InstanceCountOffset, "instance count follows the index count");
        c.Eq(8, LodGpuIndirectCommand.FirstIndexOffset, "first index is the third word");
        c.Eq(12, LodGpuIndirectCommand.BaseVertexOffset, "base vertex is the fourth word");
        c.Eq(16, LodGpuIndirectCommand.BaseInstanceOffset, "base instance is the fifth word");

        var bytes = new byte[LodGpuIndirectCommand.StrideBytes];
        LodGpuIndirectCommand.Encode(bytes, 384, visible: true, 1024, 4096, 7);
        c.Eq(384u, LodGpuIndirectCommand.ReadUInt(bytes, 0, LodGpuIndirectCommand.CountOffset),
            "the command draws the section's index count");
        c.Eq(1u, LodGpuIndirectCommand.ReadUInt(bytes, 0, LodGpuIndirectCommand.InstanceCountOffset),
            "a visible section draws exactly one instance");
        c.Eq(1024u, LodGpuIndirectCommand.ReadUInt(bytes, 0, LodGpuIndirectCommand.FirstIndexOffset),
            "first index is an element index, not a byte offset");
        c.Eq(4096, LodGpuIndirectCommand.ReadInt(bytes, 0, LodGpuIndirectCommand.BaseVertexOffset),
            "base vertex rebases the section-local indices");
        c.Eq(7u, LodGpuIndirectCommand.ReadUInt(bytes, 0, LodGpuIndirectCommand.BaseInstanceOffset),
            "base instance selects the section record");

        LodGpuIndirectCommand.Encode(bytes, 384, visible: false, 1024, 4096, 7);
        c.Eq(0u, LodGpuIndirectCommand.ReadUInt(bytes, 0, LodGpuIndirectCommand.InstanceCountOffset),
            "a rejected section is zeroed by instance count, not removed");
        c.Eq(384u, LodGpuIndirectCommand.ReadUInt(bytes, 0, LodGpuIndirectCommand.CountOffset),
            "a rejected section keeps its slot and its geometry range intact");

        c.Throws<ArgumentOutOfRangeException>(
            () => LodGpuIndirectCommand.Encode(bytes, 6, true, -1, 0, 0),
            "a negative first index is refused rather than wrapped into a huge offset");
        c.Throws<ArgumentOutOfRangeException>(
            () => LodGpuIndirectCommand.Encode(bytes, 6, true, 0, (long)int.MaxValue + 1, 0),
            "a base vertex past the signed range is refused");
    }

    static void Batching(Check c)
    {
        var backend = new FakeIndirectBackend();
        using var mirror = new LodGpuGeometryMirror(backend, 64L * 1024 * 1024);
        var builder = new LodGpuIndirectBuilder();

        // Two sections in one region, one far away: two page sets, three commands.
        long near = LodWorld.SectionKey(0, 0, 0);
        long beside = LodWorld.SectionKey(0, 1, 0);
        long far = LodWorld.SectionKey(0, 64, 64);
        foreach (long key in new[] { near, beside, far })
            mirror.Mirror(Publication(key));

        builder.Begin();
        foreach (long key in new[] { near, beside, far })
        {
            mirror.TryGet(key, out LodGpuGeometryMirror.MirroredSection section);
            builder.Add(section, Facts(key));
        }
        builder.End(mirror.VertexArena, mirror.IndexArena);

        c.Eq(3, builder.CommandCount, "every candidate becomes one command");
        c.Eq(3, builder.VisibleCommands, "CPU-approved candidates are all visible");
        c.Eq(0, builder.ZeroedCommands, "nothing is rejected without a depth classifier");
        c.Eq(2, builder.Batches.Count,
            "sections in one region share a batch; a distant region needs its own");
        c.Eq(2, builder.Batches[0].CommandCount, "the shared region contributes two commands");
        c.Eq(1, builder.Batches[1].CommandCount, "the distant region contributes one");
        c.Eq(0, builder.Batches[0].FirstCommand, "the first batch starts at the first command");
        c.Eq(2, builder.Batches[1].FirstCommand,
            "each batch reads a contiguous run, as a multi-draw requires");

        bool pagesResolved = true;
        foreach (LodGpuDrawBatch batch in builder.Batches)
            pagesResolved &= batch.VertexPage != 0 && batch.IndexPage != 0;
        c.True(pagesResolved, "every batch names the pair of buffers it draws from");

        // Base instance must address this command's own record slot.
        bool slotsMatch = true;
        for (int i = 0; i < builder.CommandCount; i++)
        {
            slotsMatch &= LodGpuIndirectCommand.ReadUInt(
                builder.Commands, i, LodGpuIndirectCommand.BaseInstanceOffset) == (uint)i;
        }
        c.True(slotsMatch, "each command's base instance is its own record slot");
        c.Eq(builder.CommandCount * LodGpuSectionRecord.StrideBytes, builder.Records.Length,
            "the record buffer stays exactly parallel to the command buffer");

        // The record a command points at must describe that command's section.
        mirror.TryGet(near, out LodGpuGeometryMirror.MirroredSection nearSection);
        c.Eq((uint)nearSection.IndexCount,
            LodGpuIndirectCommand.ReadUInt(builder.Commands, 0, LodGpuIndirectCommand.CountOffset),
            "the first command draws the first candidate's geometry");
        c.Eq(Facts(near).NoiseOriginX,
            LodGpuSectionRecord.ReadFloat(builder.Records, 0, LodGpuSectionRecord.NoiseOriginOffset),
            "the first record carries the first candidate's stable world origin");

        // A rejected candidate keeps its slot so the order never shifts.
        builder.Begin();
        mirror.TryGet(near, out LodGpuGeometryMirror.MirroredSection first);
        mirror.TryGet(beside, out LodGpuGeometryMirror.MirroredSection secondSection);
        builder.Add(first, Facts(near), visible: false);
        builder.Add(secondSection, Facts(beside));
        builder.End(mirror.VertexArena, mirror.IndexArena);
        c.Eq(2, builder.CommandCount, "a rejected candidate still occupies its command slot");
        c.Eq(1, builder.ZeroedCommands, "the rejection is counted");
        c.Eq(0u, LodGpuIndirectCommand.ReadUInt(
                builder.Commands, 0, LodGpuIndirectCommand.InstanceCountOffset),
            "the rejected command draws no instance");
        c.Eq(1u, LodGpuIndirectCommand.ReadUInt(
                builder.Commands, 1, LodGpuIndirectCommand.InstanceCountOffset),
            "the surviving command is unaffected by its neighbour's rejection");

        // A section the mirror never stored cannot become a command.
        builder.Begin();
        builder.Add(default, Facts(near));
        builder.End(mirror.VertexArena, mirror.IndexArena);
        c.Eq(0, builder.CommandCount, "a section with no arena span produces no command");
        c.Eq(1, builder.CandidatesDropped, "the dropped candidate is counted");
        c.Eq(0, builder.Batches.Count, "no batch is emitted for nothing");

        // Coverage: a batch count over half the drawn terrain is not a draw-call reduction,
        // and the builder has to be able to say so.
        builder.Begin();
        mirror.TryGet(near, out LodGpuGeometryMirror.MirroredSection held);
        builder.Add(held, Facts(near));
        builder.AddMissing();
        builder.AddMissing();
        builder.End(mirror.VertexArena, mirror.IndexArena);
        c.Eq(1, builder.CommandCount, "only the mirrored section becomes a command");
        c.Eq(2, builder.MissingSections, "drawn sections the arenas do not hold are counted");
        c.Eq(3, builder.DrawnSections, "drawn sections are commands plus what was missed");
        c.Near(1.0 / 3.0, builder.Coverage, 0.001,
            "coverage reports the fraction of drawn terrain the batches actually represent");

        builder.Begin();
        builder.End(mirror.VertexArena, mirror.IndexArena);
        c.Eq(1.0, builder.Coverage, "an empty frame reports full coverage rather than dividing by zero");
    }

    /// <summary>
    /// Batching cannot preserve strict global front-to-back, but it must preserve the two
    /// things that matter: order inside a batch, and batches ordered by their nearest
    /// member. Overdraw is charged to whatever draws second.
    /// </summary>
    static void BatchOrdering(Check c)
    {
        var backend = new FakeIndirectBackend();
        using var mirror = new LodGpuGeometryMirror(backend, 64L * 1024 * 1024);
        var builder = new LodGpuIndirectBuilder();

        // Interleaved arrival: region A, region B, region A, region B.
        long[] order =
        [
            LodWorld.SectionKey(0, 0, 0),
            LodWorld.SectionKey(0, 64, 0),
            LodWorld.SectionKey(0, 1, 0),
            LodWorld.SectionKey(0, 65, 0),
        ];
        foreach (long key in order) mirror.Mirror(Publication(key));

        builder.Begin();
        foreach (long key in order)
        {
            mirror.TryGet(key, out LodGpuGeometryMirror.MirroredSection section);
            builder.Add(section, Facts(key));
        }
        builder.End(mirror.VertexArena, mirror.IndexArena);

        c.Eq(2, builder.Batches.Count, "interleaved arrivals collapse into two batches");
        c.Eq(4, builder.CommandCount, "no candidate is lost to regrouping");

        mirror.TryGet(order[0], out LodGpuGeometryMirror.MirroredSection nearest);
        c.Eq(nearest.GroupId, builder.Batches[0].GroupId,
            "the batch holding the nearest candidate is submitted first");

        // Within the first batch, the two region-A sections keep their arrival order.
        mirror.TryGet(order[2], out LodGpuGeometryMirror.MirroredSection secondOfA);
        c.Eq((uint)(secondOfA.FirstIndex),
            LodGpuIndirectCommand.ReadUInt(builder.Commands, 1, LodGpuIndirectCommand.FirstIndexOffset),
            "the second candidate of a batch keeps its front-to-back position");
        c.Eq(0, builder.Batches[0].FirstCommand, "the nearest batch owns the first command run");
        c.Eq(2, builder.Batches[1].FirstCommand, "the farther batch follows it contiguously");
    }

    /// <summary>
    /// A section's vertices and indices must live in the same page set. If they could
    /// split, a batch that binds one vertex buffer and one index buffer would draw one
    /// section's positions with another's indices.
    /// </summary>
    static void PagePairing(Check c)
    {
        var backend = new FakeIndirectBackend();
        using var mirror = new LodGpuGeometryMirror(
            backend,
            ceilingBytes: 0,
            // Vertices fill their page after two sections; indices would still have room,
            // so an unpaired allocator would put the third section's halves in different
            // sets.
            vertexLimits: new LodGpuArenaLimits(4096, 1 << 20, 8),
            indexLimits: new LodGpuArenaLimits(65536, 1 << 20, 8));

        var groups = new HashSet<long>();
        var pairs = new HashSet<(int Vertex, int Index)>();
        for (int i = 0; i < 24; i++)
        {
            long key = LodWorld.SectionKey(0, i, 0);
            mirror.Mirror(Publication(key, quads: 16));
            if (!mirror.TryGet(key, out LodGpuGeometryMirror.MirroredSection section)) continue;
            groups.Add(section.GroupId);
            pairs.Add((
                mirror.VertexArena.PageHandle(section.Vertices),
                mirror.IndexArena.PageHandle(section.Indices)));
        }

        c.True(groups.Count > 1, "the tight vertex page forces more than one page set");
        c.Eq(groups.Count, pairs.Count,
            "each page set is exactly one vertex buffer and one index buffer");

        // Every section in a set must agree on both buffers.
        var byGroup = new Dictionary<long, (int Vertex, int Index)>();
        bool consistent = true;
        foreach (LodGpuGeometryMirror.MirroredSection section in mirror.Sections.Values)
        {
            (int Vertex, int Index) pair = (
                mirror.VertexArena.PageHandle(section.Vertices),
                mirror.IndexArena.PageHandle(section.Indices));
            if (byGroup.TryGetValue(section.GroupId, out (int Vertex, int Index) seen))
                consistent &= seen == pair;
            else byGroup[section.GroupId] = pair;
        }
        c.True(consistent, "every section in a page set draws from the same buffer pair");
    }

    // ---- Fixtures ----

    static LodRenderPublication Publication(long key, int quads = 1)
    {
        var xyz = new float[quads * 4 * 3];
        var rgba = new byte[quads * 4 * 4];
        var indices = new int[quads * 6];
        for (int i = 0; i < indices.Length; i++) indices[i] = i % (quads * 4);
        return new LodRenderPublication(
            new LodRenderResourceIdentity(1, key, key, key * 2, 0),
            null, null,
            quads * 4, quads * 6, 0, 0, 0,
            new LodRenderGeometry(xyz, rgba, indices));
    }

    static LodGpuSectionFacts Facts(long key)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        float originX = LodWorld.KeySx(key) * (float)footprint;
        float originZ = LodWorld.KeySz(key) * (float)footprint;
        return new LodGpuSectionFacts(
            key, originX, -70f, originZ, footprint,
            originX, originZ,
            LodWorld.ColumnStepBlocks(LodWorld.KeyLevel(key)),
            (int)(originX / VanillaReadinessMask.ChunkBlocks),
            (int)(originZ / VanillaReadinessMask.ChunkBlocks),
            0);
    }

    /// <summary>Pages are byte arrays and fences signal on request; see GpuArenaChecks.</summary>
    sealed class FakeIndirectBackend : ILodGpuArenaBackend
    {
        readonly Dictionary<(LodGpuArenaKind, int), byte[]> pages = new();
        int nextHandle = 1;
        long nextFence = 1;

        public int CreatePage(LodGpuArenaKind kind, long bytes)
        {
            int handle = nextHandle++;
            pages[(kind, handle)] = new byte[bytes];
            return handle;
        }

        public bool Upload(LodGpuArenaKind kind, int page, long offset, ReadOnlySpan<byte> data)
        {
            if (!pages.TryGetValue((kind, page), out byte[]? bytes)) return false;
            data.CopyTo(bytes.AsSpan((int)offset));
            return true;
        }

        public bool TryRead(LodGpuArenaKind kind, int page, long offset, Span<byte> destination)
        {
            if (!pages.TryGetValue((kind, page), out byte[]? bytes)) return false;
            bytes.AsSpan((int)offset, destination.Length).CopyTo(destination);
            return true;
        }

        public void DeletePage(LodGpuArenaKind kind, int page) => pages.Remove((kind, page));
        public long CreateFence() => nextFence++;
        public bool FenceSignaled(long fence) => true;
        public void DeleteFence(long fence) { }
    }
}
