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
    byte OpenEdges)
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
        bool Visible);

    readonly List<List<Entry>> buckets = new();
    readonly List<LodGpuDrawBatch> batches = new();
    readonly Dictionary<long, int> bucketOfGroup = new();
    byte[] commands = [];
    byte[] records = [];
    int bucketCount;

    public IReadOnlyList<LodGpuDrawBatch> Batches => batches;
    public int CommandCount { get; private set; }
    public int VisibleCommands { get; private set; }
    public int ZeroedCommands { get; private set; }
    public int CandidatesDropped { get; private set; }

    /// <summary>
    /// Sections the visible path drew that the arenas do not hold. Counted explicitly
    /// because a batch count means nothing without it: a mirror covering half the drawn
    /// terrain would otherwise report a flatteringly small number of batches and no hint
    /// that half the work was missing.
    /// </summary>
    public int MissingSections { get; private set; }

    public int DrawnSections => CommandCount + MissingSections;

    public double Coverage => DrawnSections == 0 ? 1 : CommandCount / (double)DrawnSections;

    public ReadOnlySpan<byte> Commands =>
        commands.AsSpan(0, CommandCount * LodGpuIndirectCommand.StrideBytes);

    public ReadOnlySpan<byte> Records =>
        records.AsSpan(0, CommandCount * LodGpuSectionRecord.StrideBytes);

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
    }

    /// <summary>Records a drawn section the mirror does not hold at all.</summary>
    public void AddMissing() => MissingSections++;

    /// <summary>
    /// Adds one CPU-approved candidate, in the traversal's own front-to-back order.
    /// A section with no geometry is dropped rather than turned into an empty command.
    /// </summary>
    public void Add(
        in LodGpuGeometryMirror.MirroredSection section,
        in LodGpuSectionFacts facts,
        bool visible = true)
    {
        if (section.IndexCount <= 0 || !section.Indices.IsLive || !section.Vertices.IsLive)
        {
            CandidatesDropped++;
            return;
        }

        if (!bucketOfGroup.TryGetValue(section.GroupId, out int bucket))
        {
            bucket = bucketCount++;
            if (buckets.Count < bucketCount) buckets.Add(new List<Entry>());
            bucketOfGroup[section.GroupId] = bucket;
        }

        buckets[bucket].Add(new Entry(section, facts, visible));
    }

    /// <summary>
    /// Emits the command and record buffers. Both are written in one pass and stay
    /// parallel: a command's base instance is its own slot, so there is no second mapping
    /// that could drift out of step with the commands.
    /// </summary>
    public void End(LodGpuArena vertexArena, LodGpuArena indexArena)
    {
        int total = 0;
        for (int i = 0; i < bucketCount; i++) total += buckets[i].Count;
        EnsureCapacity(total);

        for (int i = 0; i < bucketCount; i++)
        {
            List<Entry> bucket = buckets[i];
            if (bucket.Count == 0) continue;

            int first = CommandCount;
            int vertexPage = vertexArena.PageHandle(bucket[0].Section.Vertices);
            int indexPage = indexArena.PageHandle(bucket[0].Section.Indices);
            if (vertexPage == 0 || indexPage == 0)
            {
                // The page went away between publication and this frame. Dropping the whole
                // set is correct: every command in it would address a deleted buffer.
                CandidatesDropped += bucket.Count;
                continue;
            }

            foreach (Entry entry in bucket)
            {
                LodGpuIndirectCommand.Encode(
                    commands.AsSpan(CommandCount * LodGpuIndirectCommand.StrideBytes),
                    entry.Section.IndexCount,
                    entry.Visible,
                    entry.Section.FirstIndex,
                    entry.Section.BaseVertex,
                    CommandCount);
                LodGpuSectionRecord.Encode(
                    entry.Facts,
                    records.AsSpan(CommandCount * LodGpuSectionRecord.StrideBytes));
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
        if (commands.Length < commandBytes) commands = new byte[Math.Max(commandBytes, 4096)];
        if (records.Length < recordBytes) records = new byte[Math.Max(recordBytes, 16384)];
    }
}
