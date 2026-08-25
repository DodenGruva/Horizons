using System.Buffers.Binary;

namespace VintageHorizons;

/// <summary>
/// The per-section values the established renderer sets as uniforms before each draw. A
/// multi-draw has no per-draw uniforms, so these move into a buffer the shader indexes.
/// Every one of them is already a pure function of the section, not of the mesh.
/// </summary>
internal readonly record struct LodGpuSectionFacts(
    long SectionKey,
    float OriginRelX,
    float OriginRelY,
    float OriginRelZ,
    float SectionSize,
    float NoiseOriginX,
    float NoiseOriginZ,
    float ColumnBlocks,
    int MaskOriginX,
    int MaskOriginZ,
    byte OpenEdges,
    float BoxMinY = 0f,
    float BoxMaxY = 0f)
{
    public const byte OpenMinusX = 1 << 0;
    public const byte OpenPlusX = 1 << 1;
    public const byte OpenMinusZ = 1 << 2;
    public const byte OpenPlusZ = 1 << 3;
}

/// <summary>
/// Byte layout of one section record. Offsets are spelled out and asserted rather than
/// inherited from structure packing, because the same bytes are read by GLSL under std430
/// and a silent stride disagreement would misaddress every section at once.
/// </summary>
internal static class LodGpuSectionRecord
{
    public const int StrideBytes = 64;
    public const int OriginOffset = 0;          // vec3 camera-relative origin
    public const int SectionSizeOffset = 12;    // float, blocks along one edge
    public const int NoiseOriginOffset = 16;    // vec2 stable world x/z
    public const int ColumnBlocksOffset = 24;   // float
    public const int ReservedFloatOffset = 28;  // float, keeps the vec4 whole
    public const int MaskOriginOffset = 32;     // ivec2 whole vanilla chunks
    public const int ReservedIntOffset = 40;    // ivec2, keeps the vec4 whole
    public const int OpenEdgesOffset = 48;      // vec4 in -X, +X, -Z, +Z order

    public static void Encode(in LodGpuSectionFacts facts, Span<byte> destination)
    {
        if (destination.Length < StrideBytes)
            throw new ArgumentException("short section record", nameof(destination));

        Write(destination, OriginOffset, facts.OriginRelX);
        Write(destination, OriginOffset + 4, facts.OriginRelY);
        Write(destination, OriginOffset + 8, facts.OriginRelZ);
        Write(destination, SectionSizeOffset, facts.SectionSize);
        Write(destination, NoiseOriginOffset, facts.NoiseOriginX);
        Write(destination, NoiseOriginOffset + 4, facts.NoiseOriginZ);
        Write(destination, ColumnBlocksOffset, facts.ColumnBlocks);
        Write(destination, ReservedFloatOffset, 0f);
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[MaskOriginOffset..], facts.MaskOriginX);
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[(MaskOriginOffset + 4)..], facts.MaskOriginZ);
        BinaryPrimitives.WriteInt32LittleEndian(destination[ReservedIntOffset..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(destination[(ReservedIntOffset + 4)..], 0);

        // Expanded to one float per side so the shader can multiply without branching,
        // matching the openEdges uniform the established shader already consumes.
        Write(destination, OpenEdgesOffset, Edge(facts.OpenEdges, LodGpuSectionFacts.OpenMinusX));
        Write(destination, OpenEdgesOffset + 4, Edge(facts.OpenEdges, LodGpuSectionFacts.OpenPlusX));
        Write(destination, OpenEdgesOffset + 8, Edge(facts.OpenEdges, LodGpuSectionFacts.OpenMinusZ));
        Write(destination, OpenEdgesOffset + 12, Edge(facts.OpenEdges, LodGpuSectionFacts.OpenPlusZ));
    }

    public static float ReadFloat(ReadOnlySpan<byte> source, int slot, int offset) =>
        BinaryPrimitives.ReadSingleLittleEndian(source[(slot * StrideBytes + offset)..]);

    public static int ReadInt(ReadOnlySpan<byte> source, int slot, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(source[(slot * StrideBytes + offset)..]);

    static float Edge(byte openEdges, byte bit) => (openEdges & bit) != 0 ? 1f : 0f;

    static void Write(Span<byte> destination, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(destination[offset..], value);
}

/// <summary>
/// One `DrawElementsIndirectCommand`, exactly as the driver reads it. Zeroing
/// <c>instanceCount</c> is how a rejected section is dropped without disturbing any other
/// command's slot, which is what keeps the front-to-back order stable frame to frame.
/// </summary>
internal static class LodGpuIndirectCommand
{
    public const int StrideBytes = 20;
    public const int CountOffset = 0;
    public const int InstanceCountOffset = 4;
    public const int FirstIndexOffset = 8;
    public const int BaseVertexOffset = 12;
    public const int BaseInstanceOffset = 16;

    public static void Encode(
        Span<byte> destination,
        int indexCount,
        bool visible,
        long firstIndex,
        long baseVertex,
        int recordSlot)
    {
        if (destination.Length < StrideBytes)
            throw new ArgumentException("short indirect command", nameof(destination));
        if (firstIndex is < 0 or > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(firstIndex));
        if (baseVertex is < 0 or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(baseVertex));

        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[CountOffset..], (uint)Math.Max(0, indexCount));
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[InstanceCountOffset..], visible ? 1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[FirstIndexOffset..], (uint)firstIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[BaseVertexOffset..], (int)baseVertex);

        // The instanced record index is selected by base instance: with a divisor of one
        // and a single instance, the vertex shader reads element `recordSlot` of the record
        // attribute. Shader draw parameters would do the same job, but they are an
        // extension outside the supported tier and must not become a hidden dependency.
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[BaseInstanceOffset..], (uint)Math.Max(0, recordSlot));
    }

    public static uint ReadUInt(ReadOnlySpan<byte> source, int command, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(source[(command * StrideBytes + offset)..]);

    public static int ReadInt(ReadOnlySpan<byte> source, int command, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(source[(command * StrideBytes + offset)..]);
}

/// <summary>
/// One section's camera-relative cull box, in the layout the classifier's box buffer uses:
/// two vec4, min then max, with the fourth component unused padding.
///
/// The box is built from the same numbers the CPU frustum test uses - origin, footprint, and
/// the mesh's own vertical span - so a section culled on the GPU is culled against exactly
/// the volume the established renderer would have judged. Building it from anything else
/// would let the two paths disagree about where a section is, which is the one disagreement
/// that shows up as terrain appearing and disappearing between them.
/// </summary>
internal static class LodGpuCullBox
{
    public const int StrideBytes = 32;      // two vec4
    public const int MinOffset = 0;
    public const int MaxOffset = 16;

    public static void Encode(in LodGpuSectionFacts facts, Span<byte> destination)
    {
        Encode(LodGpuCullBounds.FromSection(facts), destination);
    }

    public static void Encode(in LodGpuCullBounds bounds, Span<byte> destination)
    {
        if (destination.Length < StrideBytes)
            throw new ArgumentException("short cull box", nameof(destination));

        Write(destination, MinOffset, bounds.MinX);
        Write(destination, MinOffset + 4, bounds.MinY);
        Write(destination, MinOffset + 8, bounds.MinZ);
        Write(destination, MinOffset + 12, 0f);

        Write(destination, MaxOffset, bounds.MaxX);
        Write(destination, MaxOffset + 4, bounds.MaxY);
        Write(destination, MaxOffset + 8, bounds.MaxZ);
        Write(destination, MaxOffset + 12, 0f);
    }

    public static float ReadFloat(ReadOnlySpan<byte> source, int slot, int offset) =>
        BinaryPrimitives.ReadSingleLittleEndian(source[(slot * StrideBytes + offset)..]);

    static void Write(Span<byte> destination, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(destination[offset..], value);
}

/// <summary>
/// One multi-draw: the page pair to bind and the run of consecutive commands that read
/// from it.
/// </summary>
internal readonly record struct LodGpuDrawBatch(
    long GroupId,
    int VertexPage,
    int IndexPage,
    int FirstCommand,
    int CommandCount);

/// <summary>
/// The measured Phase 6 boundary. Offline runs found no meaningful difference between
/// 384 and 768 blocks, so the midpoint is kept explicit and testable instead of being
/// buried in the renderer's draw loop.
/// </summary>
internal static class LodGpuDepthSplitPolicy
{
    public const float NearRadiusBlocks = 512f;

    public static bool IsNear(float horizontalDistanceBlocks) =>
        float.IsFinite(horizontalDistanceBlocks)
        && horizontalDistanceBlocks <= NearRadiusBlocks;
}

internal readonly record struct LodGpuCullBounds(
    float MinX,
    float MinY,
    float MinZ,
    float MaxX,
    float MaxY,
    float MaxZ)
{
    public static LodGpuCullBounds FromSection(in LodGpuSectionFacts facts) => new(
        facts.OriginRelX,
        facts.BoxMinY,
        facts.OriginRelZ,
        facts.OriginRelX + facts.SectionSize,
        facts.BoxMaxY,
        facts.OriginRelZ + facts.SectionSize);

    public static LodGpuCullBounds FromCluster(
        in LodGpuSectionFacts facts, in LodPackedCluster cluster) => new(
        facts.OriginRelX + cluster.MinX,
        facts.OriginRelY + cluster.MinY,
        facts.OriginRelZ + cluster.MinZ,
        facts.OriginRelX + cluster.MaxX,
        facts.OriginRelY + cluster.MaxY,
        facts.OriginRelZ + cluster.MaxZ);
}

/// <summary>
/// One indexed indirect command over a reusable 0,1,2,0,2,3 quad pattern. `baseVertex`
/// selects four virtual corners per packed record, so the post-transform cache shades four
/// vertices rather than the six invocations the first draw-arrays experiment required.
/// The ordinary five-word layout also lets the existing cull shader keep zeroing word one.
/// </summary>
internal static class LodGpuPackedIndirectCommand
{
    public const int StrideBytes = LodGpuIndirectCommand.StrideBytes;
    public const int CountOffset = 0;
    public const int InstanceCountOffset = 4;
    public const int FirstIndexOffset = 8;
    public const int BaseVertexOffset = 12;
    public const int BaseInstanceOffset = 16;

    public static void Encode(
        Span<byte> destination,
        int quadCount,
        bool visible,
        long firstQuad,
        int recordSlot)
    {
        if (destination.Length < StrideBytes)
            throw new ArgumentException("short packed indirect command", nameof(destination));
        if (quadCount < 0 || quadCount > uint.MaxValue / LodPackedQuadFormat.IndicesPerQuad)
            throw new ArgumentOutOfRangeException(nameof(quadCount));
        if (firstQuad < 0
            || firstQuad > int.MaxValue / LodPackedQuadFormat.PulledVerticesPerQuad)
            throw new ArgumentOutOfRangeException(nameof(firstQuad));

        LodGpuIndirectCommand.Encode(
            destination,
            quadCount * LodPackedQuadFormat.IndicesPerQuad,
            visible,
            firstIndex: 0,
            baseVertex: firstQuad * LodPackedQuadFormat.PulledVerticesPerQuad,
            recordSlot);
    }
}

/// <summary>Reusable topology shared by every packed section draw.</summary>
internal static class LodGpuPackedIndexPattern
{
    public static void Fill(Span<uint> destination, int quadCount)
    {
        if (quadCount < 0 || destination.Length < quadCount * LodPackedQuadFormat.IndicesPerQuad)
            throw new ArgumentOutOfRangeException(nameof(quadCount));

        for (int quad = 0; quad < quadCount; quad++)
        {
            uint corner = (uint)(quad * LodPackedQuadFormat.PulledVerticesPerQuad);
            int index = quad * LodPackedQuadFormat.IndicesPerQuad;
            destination[index] = corner;
            destination[index + 1] = corner + 1;
            destination[index + 2] = corner + 2;
            destination[index + 3] = corner;
            destination[index + 4] = corner + 2;
            destination[index + 5] = corner + 3;
        }
    }
}

/// <summary>
/// Turns the CPU's already-approved, already-ordered candidate list into indirect commands.
/// Commands for one page set are consecutive, because a multi-draw reads a contiguous run;
/// the sets themselves are emitted in the order their nearest section arrived, so the
/// front-to-back submission the current renderer relies on survives batching as closely as
/// batching allows. Nothing here decides visibility - that stays with CPU traversal until a
/// later phase measures a depth classifier.
/// </summary>
internal sealed class LodGpuIndirectBuilder
{
    readonly record struct Entry(
        LodGpuGeometryMirror.MirroredSection Section,
        LodGpuSectionFacts Facts,
        LodGpuCullBounds Bounds,
        LodGpuArenaRange PackedRange,
        long FirstPackedQuad,
        int PackedQuadCount,
        int ClusterCell,
        bool Visible);

    readonly List<List<Entry>> buckets = new();
    readonly List<LodGpuDrawBatch> batches = new();
    readonly Dictionary<long, int> bucketOfGroup = new();
    byte[] commands = [];
    byte[] records = [];
    byte[] boxes = [];
    LodGpuCullIdentity[] identities = [];
    int bucketCount;

    public bool Packed { get; }
    public bool Clustered { get; }

    public LodGpuIndirectBuilder(bool packed = false, bool clustered = false)
    {
        if (clustered && !packed)
            throw new ArgumentException("cluster commands require packed geometry", nameof(clustered));
        Packed = packed;
        Clustered = clustered;
    }

    public IReadOnlyList<LodGpuDrawBatch> Batches => batches;
    public int CommandCount { get; private set; }
    public int VisibleCommands { get; private set; }
    public int ZeroedCommands { get; private set; }
    public int CandidatesDropped { get; private set; }
    public int AddedSections { get; private set; }

    /// <summary>
    /// Sections the visible path drew that the arenas do not hold. Counted explicitly
    /// because a batch count means nothing without it: a mirror covering half the drawn
    /// terrain would otherwise report a flatteringly small number of batches and no hint
    /// that half the work was missing.
    /// </summary>
    public int MissingSections { get; private set; }

    public int DrawnSections => AddedSections + MissingSections;

    public double Coverage => DrawnSections == 0 ? 1 : CommandCount / (double)DrawnSections;

    public ReadOnlySpan<byte> Commands =>
        commands.AsSpan(0, CommandCount * LodGpuIndirectCommand.StrideBytes);

    public ReadOnlySpan<byte> Records =>
        records.AsSpan(0, CommandCount * LodGpuSectionRecord.StrideBytes);

    /// <summary>
    /// One cull box per command, in COMMAND order, laid out as the classifier's box buffer
    /// expects: two vec4 per section, camera-relative min then max.
    ///
    /// Command order is the whole point. The walk visits sections front to back, but this
    /// builder regroups them into page buckets, so the order boxes were collected in during
    /// the walk is not the order their commands end up in. Anything that culls by writing
    /// into a command slot has to index boxes the same way the driver indexes commands, and
    /// the only way to guarantee that is to emit them from the same loop.
    /// </summary>
    public ReadOnlySpan<byte> Boxes =>
        boxes.AsSpan(0, CommandCount * LodGpuCullBox.StrideBytes);

    /// <summary>
    /// Stable CPU identities in command order. The optional flicker capture copies these
    /// beside an asynchronous GPU verdict sample, so a command slot can be followed even
    /// when page regrouping gives it a different index on the following frame.
    /// </summary>
    public ReadOnlySpan<LodGpuCullIdentity> Identities =>
        identities.AsSpan(0, CommandCount);

    public void Begin()
    {
        for (int i = 0; i < bucketCount; i++) buckets[i].Clear();
        bucketCount = 0;
        bucketOfGroup.Clear();
        batches.Clear();
        CommandCount = 0;
        VisibleCommands = 0;
        ZeroedCommands = 0;
        CandidatesDropped = 0;
        MissingSections = 0;
        AddedSections = 0;
    }

    /// <summary>Records a drawn section the mirror does not hold at all.</summary>
    public void AddMissing() => MissingSections++;

    /// <summary>
    /// Adds one CPU-approved candidate, in the traversal's own front-to-back order.
    /// A section with no geometry is dropped rather than turned into an empty command.
    /// </summary>
    public bool Add(
        in LodGpuGeometryMirror.MirroredSection section,
        in LodGpuSectionFacts facts,
        bool visible = true)
    {
        bool geometryReady = Clustered
            ? section.ClusteredPackedQuads.IsLive && section.PackedClusters.Length > 0
            : Packed
            ? section.PackedQuadCount > 0 && section.PackedQuads.IsLive
            : section.IndexCount > 0 && section.Indices.IsLive && section.Vertices.IsLive;
        if (!geometryReady)
        {
            CandidatesDropped++;
            return false;
        }

        int bucket = BucketFor(section.GroupId);
        if (Clustered)
        {
            foreach (LodPackedCluster cluster in section.PackedClusters)
            {
                if (!cluster.HasGeometry)
                {
                    CandidatesDropped++;
                    return false;
                }
            }

            foreach (LodPackedCluster cluster in section.PackedClusters)
            {
                buckets[bucket].Add(new Entry(
                    section,
                    facts,
                    LodGpuCullBounds.FromCluster(facts, cluster),
                    section.ClusteredPackedQuads,
                    section.FirstClusteredPackedQuad + cluster.FirstQuad,
                    cluster.QuadCount,
                    cluster.Cell,
                    visible));
            }
        }
        else
        {
            buckets[bucket].Add(new Entry(
                section,
                facts,
                LodGpuCullBounds.FromSection(facts),
                section.PackedQuads,
                section.FirstPackedQuad,
                section.PackedQuadCount,
                -1,
                visible));
        }

        AddedSections++;
        return true;
    }

    int BucketFor(long groupId)
    {
        if (bucketOfGroup.TryGetValue(groupId, out int bucket)) return bucket;
        bucket = bucketCount++;
        if (buckets.Count < bucketCount) buckets.Add(new List<Entry>());
        bucketOfGroup[groupId] = bucket;
        return bucket;
    }

    /// <summary>
    /// Emits the command and record buffers. Both are written in one pass and stay
    /// parallel: a command's base instance is its own slot, so there is no second mapping
    /// that could drift out of step with the commands.
    /// </summary>
    public void End(LodGpuArena vertexArena, LodGpuArena indexArena)
    {
        if (Packed) throw new InvalidOperationException("packed commands need a packed arena");
        EndCore(vertexArena, indexArena, null);
    }

    public void End(LodGpuGeometryMirror mirror) => EndCore(
        mirror.VertexArena,
        mirror.IndexArena,
        Clustered ? mirror.ClusteredPackedArena : mirror.PackedArena);

    void EndCore(LodGpuArena vertexArena, LodGpuArena indexArena, LodGpuArena? packedArena)
    {
        int total = 0;
        for (int i = 0; i < bucketCount; i++) total += buckets[i].Count;
        EnsureCapacity(total);

        for (int i = 0; i < bucketCount; i++)
        {
            List<Entry> bucket = buckets[i];
            if (bucket.Count == 0) continue;

            int first = CommandCount;
            int vertexPage = Packed
                ? packedArena?.PageHandle(bucket[0].PackedRange) ?? 0
                : vertexArena.PageHandle(bucket[0].Section.Vertices);
            int indexPage = Packed ? 0 : indexArena.PageHandle(bucket[0].Section.Indices);
            if (vertexPage == 0 || (!Packed && indexPage == 0))
            {
                // This can only happen if publication changed during command construction.
                // Refusing the complete build lets the renderer repair every recorded key
                // through legacy drawing; silently dropping this bucket would make holes.
                throw new InvalidOperationException("an indirect arena page vanished during command construction");
            }

            foreach (Entry entry in bucket)
            {
                Span<byte> command = commands.AsSpan(
                    CommandCount * LodGpuIndirectCommand.StrideBytes);
                if (Packed)
                {
                    LodGpuPackedIndirectCommand.Encode(
                        command,
                        entry.PackedQuadCount,
                        entry.Visible,
                        entry.FirstPackedQuad,
                        CommandCount);
                }
                else
                {
                    LodGpuIndirectCommand.Encode(
                        command,
                        entry.Section.IndexCount,
                        entry.Visible,
                        entry.Section.FirstIndex,
                        entry.Section.BaseVertex,
                        CommandCount);
                }
                LodGpuSectionRecord.Encode(
                    entry.Facts,
                    records.AsSpan(CommandCount * LodGpuSectionRecord.StrideBytes));
                LodGpuCullBox.Encode(
                    entry.Bounds,
                    boxes.AsSpan(CommandCount * LodGpuCullBox.StrideBytes));
                identities[CommandCount] = new LodGpuCullIdentity(
                    entry.Facts.SectionKey, entry.ClusterCell);
                CommandCount++;
                if (entry.Visible) VisibleCommands++;
                else ZeroedCommands++;
            }

            batches.Add(new LodGpuDrawBatch(
                bucket[0].Section.GroupId, vertexPage, indexPage, first, CommandCount - first));
        }
    }

    void EnsureCapacity(int commandSlots)
    {
        int commandBytes = commandSlots * LodGpuIndirectCommand.StrideBytes;
        int recordBytes = commandSlots * LodGpuSectionRecord.StrideBytes;
        int boxBytes = commandSlots * LodGpuCullBox.StrideBytes;
        if (commands.Length < commandBytes) commands = new byte[Math.Max(commandBytes, 4096)];
        if (records.Length < recordBytes) records = new byte[Math.Max(recordBytes, 16384)];
        if (boxes.Length < boxBytes) boxes = new byte[Math.Max(boxBytes, 8192)];
        if (identities.Length < commandSlots)
            identities = new LodGpuCullIdentity[Math.Max(commandSlots, 256)];
    }
}
