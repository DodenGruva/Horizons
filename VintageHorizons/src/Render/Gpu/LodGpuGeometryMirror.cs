using System.Buffers.Binary;

namespace VintageHorizons;

/// <summary>
/// The expanded representation the established renderer already uploads: three floats of
/// position and four colour bytes per vertex, six 32-bit indices per quad. Phase 2 changes
/// where those bytes live, never what they mean, so a packed format can be measured later
/// against a submission path that is already known to be correct.
/// </summary>
internal static class LodGpuGeometryFormat
{
    public const int VertexStrideBytes = 16;
    public const int IndexStrideBytes = 4;
    public const int PositionOffset = 0;
    public const int ColorOffset = 12;

    public static long VertexBytes(int vertexCount) =>
        Math.Max(0, (long)vertexCount) * VertexStrideBytes;

    public static long IndexBytes(int indexCount) =>
        Math.Max(0, (long)indexCount) * IndexStrideBytes;

    /// <summary>
    /// Interleaves the separate position and colour arrays into arena bytes. Little-endian
    /// is written explicitly rather than reinterpreted: the encoding is then the same fact
    /// in the check tier as on the GPU, on any host this mod can run on.
    /// </summary>
    public static void EncodeVertices(
        float[] xyz, byte[] rgba, int vertexCount, Span<byte> destination)
    {
        if (vertexCount < 0) throw new ArgumentOutOfRangeException(nameof(vertexCount));
        if (xyz.Length < vertexCount * 3) throw new ArgumentException("short position array", nameof(xyz));
        if (rgba.Length < vertexCount * 4) throw new ArgumentException("short colour array", nameof(rgba));
        if (destination.Length < vertexCount * VertexStrideBytes)
            throw new ArgumentException("short destination", nameof(destination));

        for (int i = 0; i < vertexCount; i++)
        {
            Span<byte> vertex = destination.Slice(i * VertexStrideBytes, VertexStrideBytes);
            BinaryPrimitives.WriteSingleLittleEndian(vertex[PositionOffset..], xyz[i * 3]);
            BinaryPrimitives.WriteSingleLittleEndian(vertex[(PositionOffset + 4)..], xyz[i * 3 + 1]);
            BinaryPrimitives.WriteSingleLittleEndian(vertex[(PositionOffset + 8)..], xyz[i * 3 + 2]);
            vertex[ColorOffset] = rgba[i * 4];
            vertex[ColorOffset + 1] = rgba[i * 4 + 1];
            vertex[ColorOffset + 2] = rgba[i * 4 + 2];
            vertex[ColorOffset + 3] = rgba[i * 4 + 3];
        }
    }

    /// <summary>
    /// Indices are stored exactly as the mesher produced them, section-local. A regional
    /// draw rebases them with the allocation's base vertex, which keeps the stored bytes
    /// comparable to the legacy content one for one.
    /// </summary>
    public static void EncodeIndices(int[] indices, int indexCount, Span<byte> destination)
    {
        if (indexCount < 0) throw new ArgumentOutOfRangeException(nameof(indexCount));
        if (indices.Length < indexCount) throw new ArgumentException("short index array", nameof(indices));
        if (destination.Length < indexCount * IndexStrideBytes)
            throw new ArgumentException("short destination", nameof(destination));

        for (int i = 0; i < indexCount; i++)
            BinaryPrimitives.WriteInt32LittleEndian(destination[(i * IndexStrideBytes)..], indices[i]);
    }

    public static void DecodeVertex(
        ReadOnlySpan<byte> source, int index, out float x, out float y, out float z, out uint color)
    {
        ReadOnlySpan<byte> vertex = source.Slice(index * VertexStrideBytes, VertexStrideBytes);
        x = BinaryPrimitives.ReadSingleLittleEndian(vertex[PositionOffset..]);
        y = BinaryPrimitives.ReadSingleLittleEndian(vertex[(PositionOffset + 4)..]);
        z = BinaryPrimitives.ReadSingleLittleEndian(vertex[(PositionOffset + 8)..]);
        color = BinaryPrimitives.ReadUInt32LittleEndian(vertex[ColorOffset..]);
    }

    public static int DecodeIndex(ReadOnlySpan<byte> source, int index) =>
        BinaryPrimitives.ReadInt32LittleEndian(source[(index * IndexStrideBytes)..]);
}

[Flags]
internal enum LodGpuGeometryRetention
{
    None = 0,
    Expanded = 1,
    Packed = 2,
    ClusteredPacked = 4,
    All = Expanded | Packed | ClusteredPacked,
}

/// <summary>
/// Regional geometry mirror for live opaque sections. Production retains exactly the selected
/// expanded, packed, or clustered-packed representation; validation fixtures may request all
/// three. The established per-section resources remain outside this mirror as the complete
/// fallback when selected publication or drawing is unavailable.
/// </summary>
internal sealed class LodGpuGeometryMirror : IDisposable
{
    internal readonly record struct MirroredSection(
        LodRenderResourceIdentity Identity,
        long RegionId,
        long GroupId,
        LodGpuArenaRange Vertices,
        LodGpuArenaRange Indices,
        int VertexCount,
        int IndexCount,
        LodGpuArenaRange PackedQuads,
        int PackedQuadCount,
        LodGpuArenaRange ClusteredPackedQuads,
        LodPackedCluster[] PackedClusters)
    {
        /// <summary>The value a regional multi-draw would use to rebase stored indices.</summary>
        public long BaseVertex => Vertices.Offset / LodGpuGeometryFormat.VertexStrideBytes;

        public long FirstIndex => Indices.Offset / LodGpuGeometryFormat.IndexStrideBytes;

        public long FirstPackedQuad => PackedQuads.Offset / LodPackedQuadFormat.StrideBytes;

        public long FirstClusteredPackedQuad =>
            ClusteredPackedQuads.Offset / LodPackedQuadFormat.StrideBytes;
    }

    /// <summary>
    /// Sections per region edge, at each level. Eight keeps a region's geometry inside a
    /// handful of pages while staying small enough that a moving camera retires whole
    /// regions rather than scattering live spans across all of them.
    /// </summary>
    internal const int DefaultRegionShift = 3;

    readonly LodGpuArena vertices;
    readonly LodGpuArena indices;
    readonly LodGpuArena packed;
    readonly LodGpuArena clusteredPacked;
    readonly Dictionary<long, MirroredSection> sections = new();

    /// <summary>
    /// Page sets belonging to each world region, oldest first. A region needs more than one
    /// once its current set fills up, and a set is the unit one indirect batch can draw.
    /// </summary>
    readonly Dictionary<long, List<long>> regionGroups = new();
    readonly int regionShift;
    readonly bool verify;
    readonly LodGpuGeometryRetention retention;
    long nextGroupId;
    byte[] scratch = [];
    byte[] readback = [];

    public LodGpuArena VertexArena => vertices;
    public LodGpuArena IndexArena => indices;
    public LodGpuArena PackedArena => packed;
    public LodGpuArena ClusteredPackedArena => clusteredPacked;
    public bool Verifying => verify;
    public bool RetainsExpanded => retention.HasFlag(LodGpuGeometryRetention.Expanded);
    public bool RetainsPacked => retention.HasFlag(LodGpuGeometryRetention.Packed);
    public bool RetainsClusteredPacked =>
        retention.HasFlag(LodGpuGeometryRetention.ClusteredPacked);
    public LodGpuGeometryRetention Retention => retention;
    public int Count => sections.Count;
    public long MirroredSections { get; private set; }
    public long Replacements { get; private set; }
    public long SkippedSections { get; private set; }
    public long VerifiedSections { get; private set; }
    public long VerificationFailures { get; private set; }
    public long CommittedBytes => vertices.CommittedBytes + indices.CommittedBytes;
    public long LiveBytes => vertices.LiveBytes + indices.LiveBytes;
    public long PendingRetireBytes => vertices.PendingRetireBytes + indices.PendingRetireBytes;
    public long AllocationFailures => vertices.AllocationFailures + indices.AllocationFailures;
    public long PackedAllocationFailures => packed.AllocationFailures;
    public long ClusteredPackedAllocationFailures => clusteredPacked.AllocationFailures;
    public long PackedLiveBytes => packed.LiveBytes;
    public long ClusteredPackedLiveBytes => clusteredPacked.LiveBytes;
    public long PackedCommittedBytes => packed.CommittedBytes;
    public long ClusteredPackedCommittedBytes => clusteredPacked.CommittedBytes;
    public long TotalLiveBytes => LiveBytes + PackedLiveBytes + ClusteredPackedLiveBytes;
    public long TotalCommittedBytes =>
        CommittedBytes + PackedCommittedBytes + ClusteredPackedCommittedBytes;
    public long PackedMirroredSections { get; private set; }
    public long PackedSkippedSections { get; private set; }
    public long ClusteredPackedMirroredSections { get; private set; }
    public long ClusteredPackedSkippedSections { get; private set; }

    public LodGpuGeometryMirror(
        ILodGpuArenaBackend backend,
        long ceilingBytes,
        bool verify = false,
        int regionShift = DefaultRegionShift,
        LodGpuArenaLimits? vertexLimits = null,
        LodGpuArenaLimits? indexLimits = null,
        LodGpuArenaLimits? packedLimits = null,
        LodGpuArenaLimits? clusteredPackedLimits = null,
        LodGpuGeometryRetention retention = LodGpuGeometryRetention.All)
    {
        if (retention == LodGpuGeometryRetention.None
            || (retention & ~LodGpuGeometryRetention.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(retention));

        vertices = new LodGpuArena(
            backend,
            LodGpuArenaKind.Vertex,
            vertexLimits ?? LodGpuArenaPolicy.VertexLimits(ceilingBytes));
        indices = new LodGpuArena(
            backend,
            LodGpuArenaKind.Index,
            indexLimits ?? LodGpuArenaPolicy.IndexLimits(ceilingBytes));
        packed = new LodGpuArena(
            backend,
            LodGpuArenaKind.PackedQuad,
            packedLimits ?? LodGpuArenaPolicy.PackedLimits(ceilingBytes));
        clusteredPacked = new LodGpuArena(
            backend,
            LodGpuArenaKind.PackedCluster,
            clusteredPackedLimits ?? LodGpuArenaPolicy.PackedClusterLimits(ceilingBytes));
        this.verify = verify;
        this.regionShift = regionShift;
        this.retention = retention;
    }

    public static LodGpuGeometryRetention SelectRetention(bool packed, bool clustered) =>
        !packed ? LodGpuGeometryRetention.Expanded
        : clustered ? LodGpuGeometryRetention.ClusteredPacked
        : LodGpuGeometryRetention.Packed;

    /// <summary>
    /// Groups sections by level and by a coarse tile of section indices, so one region's
    /// pages hold terrain that is drawn together. Levels never share a region: their
    /// footprints differ by powers of two, and a later indirect draw batches per level.
    /// </summary>
    public static long RegionId(long sectionKey, int regionShift) => LodWorld.SectionKey(
        LodWorld.KeyLevel(sectionKey),
        LodWorld.KeySx(sectionKey) >> regionShift,
        LodWorld.KeySz(sectionKey) >> regionShift);

    public bool TryGet(long sectionKey, out MirroredSection section) =>
        sections.TryGetValue(sectionKey, out section);

    public IReadOnlyDictionary<long, MirroredSection> Sections => sections;

    /// <summary>
    /// Mirrors one publication's opaque geometry. New spans are allocated and filled before
    /// the record is replaced, and only then is the previous pair retired, so the mirror
    /// never holds a section whose bytes are half old and half new.
    /// </summary>
    public bool Mirror(in LodRenderPublication publication)
    {
        long key = publication.Identity.SectionKey;
        LodRenderGeometry geometry = publication.OpaqueGeometry;
        int vertexCount = publication.OpaqueVertices;
        int indexCount = publication.OpaqueIndices;

        if (geometry.Xyz == null || geometry.Rgba == null || geometry.Indices == null
            || vertexCount <= 0 || indexCount <= 0)
        {
            // A section that publishes no opaque geometry is not a mirror failure. It also
            // must not keep the geometry it had before the replacement.
            Remove(key);
            return false;
        }

        bool hadPrevious = sections.TryGetValue(key, out MirroredSection previous);
        long region = RegionId(key, regionShift);
        long vertexBytes = LodGpuGeometryFormat.VertexBytes(vertexCount);
        long indexBytes = LodGpuGeometryFormat.IndexBytes(indexCount);
        LodGpuArenaRange vertexRange = LodGpuArenaRange.None;
        LodGpuArenaRange indexRange = LodGpuArenaRange.None;
        LodGpuArenaRange packedRange = LodGpuArenaRange.None;
        int packedCount = 0;
        uint[]? packedWords = geometry.PackedQuads;
        bool packedAvailable = packedWords != null && geometry.PackedQuadCount > 0;
        long packedBytes = packedAvailable
            ? LodPackedQuadFormat.Bytes(geometry.PackedQuadCount)
            : 0;

        LodGpuArenaRange clusteredPackedRange = LodGpuArenaRange.None;
        LodPackedCluster[] packedClusters = Array.Empty<LodPackedCluster>();
        uint[]? clusteredWords = geometry.ClusteredPackedQuads;
        LodPackedCluster[]? publishedClusters = geometry.PackedClusters;
        int clusteredQuadCount = clusteredWords?.Length / LodPackedQuadFormat.WordsPerQuad ?? 0;
        bool clustersAvailable = clusteredWords != null
            && publishedClusters is { Length: > 0 }
            && clusteredWords.Length % LodPackedQuadFormat.WordsPerQuad == 0
            && ClustersDescribe(publishedClusters, clusteredQuadCount);
        long clusteredBytes = clustersAvailable
            ? LodPackedQuadFormat.Bytes(clusteredQuadCount)
            : 0;

        long group;
        if (RetainsExpanded)
        {
            if (!TryAllocatePair(region, vertexBytes, indexBytes,
                    out group, out vertexRange, out indexRange))
                return Skip(key, hadPrevious, previous);

            // Each half is compared while its own encoding is still in scratch, which is
            // the only point where the GPU bytes and the legacy upload input coexist.
            EnsureScratch(Math.Max(vertexBytes, indexBytes));
            LodGpuGeometryFormat.EncodeVertices(
                geometry.Xyz, geometry.Rgba, vertexCount, scratch);
            bool expandedStored = vertices.Upload(
                    vertexRange, scratch.AsSpan(0, (int)vertexBytes))
                && Matches(vertices, vertexRange, vertexBytes);
            if (expandedStored)
            {
                LodGpuGeometryFormat.EncodeIndices(geometry.Indices, indexCount, scratch);
                expandedStored = indices.Upload(
                        indexRange, scratch.AsSpan(0, (int)indexBytes))
                    && Matches(indices, indexRange, indexBytes);
            }

            if (!expandedStored)
            {
                vertices.Retire(vertexRange);
                indices.Retire(indexRange);
                return Skip(key, hadPrevious, previous);
            }
        }
        else if (RetainsClusteredPacked)
        {
            if (!clustersAvailable
                || !TryAllocateSingle(region, clusteredPacked, clusteredBytes,
                    out group, out clusteredPackedRange))
            {
                ClusteredPackedSkippedSections++;
                return Skip(key, hadPrevious, previous);
            }

            EnsureScratch(clusteredBytes);
            LodPackedQuadFormat.EncodeBytes(clusteredWords!, clusteredQuadCount, scratch);
            bool clusteredStored = clusteredPacked.Upload(
                    clusteredPackedRange, scratch.AsSpan(0, (int)clusteredBytes))
                && Matches(clusteredPacked, clusteredPackedRange, clusteredBytes);
            if (!clusteredStored)
            {
                clusteredPacked.Retire(clusteredPackedRange);
                ClusteredPackedSkippedSections++;
                return Skip(key, hadPrevious, previous);
            }

            packedClusters = (LodPackedCluster[])publishedClusters!.Clone();
            ClusteredPackedMirroredSections++;
        }
        else
        {
            if (!packedAvailable
                || !TryAllocateSingle(region, packed, packedBytes,
                    out group, out packedRange))
            {
                PackedSkippedSections++;
                return Skip(key, hadPrevious, previous);
            }

            EnsureScratch(packedBytes);
            LodPackedQuadFormat.EncodeBytes(
                packedWords!, geometry.PackedQuadCount, scratch);
            bool packedStored = packed.Upload(
                    packedRange, scratch.AsSpan(0, (int)packedBytes))
                && Matches(packed, packedRange, packedBytes);
            if (!packedStored)
            {
                packed.Retire(packedRange);
                PackedSkippedSections++;
                return Skip(key, hadPrevious, previous);
            }

            packedCount = geometry.PackedQuadCount;
            PackedMirroredSections++;
        }

        // The all-representations mode remains available to pure validation fixtures. In
        // production exactly one format is retained, so these optional companions allocate
        // only when an expanded validation mirror selected the page group above.
        if (RetainsPacked && !packedRange.IsLive && packedAvailable)
        {
            if (packed.TryAllocate(group, packedBytes, out packedRange))
            {
                EnsureScratch(packedBytes);
                LodPackedQuadFormat.EncodeBytes(
                    packedWords!, geometry.PackedQuadCount, scratch);
                bool packedStored = packed.Upload(
                        packedRange, scratch.AsSpan(0, (int)packedBytes))
                    && Matches(packed, packedRange, packedBytes);
                if (packedStored)
                {
                    packedCount = geometry.PackedQuadCount;
                    PackedMirroredSections++;
                }
                else
                {
                    packed.Retire(packedRange);
                    packedRange = LodGpuArenaRange.None;
                    PackedSkippedSections++;
                }
            }
            else
            {
                PackedSkippedSections++;
            }
        }

        if (RetainsClusteredPacked && !clusteredPackedRange.IsLive && clustersAvailable)
        {
            if (clusteredPacked.TryAllocate(group, clusteredBytes, out clusteredPackedRange))
            {
                EnsureScratch(clusteredBytes);
                LodPackedQuadFormat.EncodeBytes(clusteredWords!, clusteredQuadCount, scratch);
                bool clusteredStored = clusteredPacked.Upload(
                        clusteredPackedRange, scratch.AsSpan(0, (int)clusteredBytes))
                    && Matches(clusteredPacked, clusteredPackedRange, clusteredBytes);
                if (clusteredStored)
                {
                    packedClusters = (LodPackedCluster[])publishedClusters!.Clone();
                    ClusteredPackedMirroredSections++;
                }
                else
                {
                    clusteredPacked.Retire(clusteredPackedRange);
                    clusteredPackedRange = LodGpuArenaRange.None;
                    ClusteredPackedSkippedSections++;
                }
            }
            else
            {
                ClusteredPackedSkippedSections++;
            }
        }
        else if (RetainsClusteredPacked && !clusteredPackedRange.IsLive
            && (clusteredWords != null || publishedClusters != null))
        {
            ClusteredPackedSkippedSections++;
        }

        sections[key] = new MirroredSection(
            publication.Identity, region, group, vertexRange, indexRange, vertexCount, indexCount,
            packedRange, packedCount, clusteredPackedRange, packedClusters);
        MirroredSections++;
        if (verify) VerifiedSections++;

        if (hadPrevious)
        {
            if (previous.Vertices.IsLive) vertices.Retire(previous.Vertices);
            if (previous.Indices.IsLive) indices.Retire(previous.Indices);
            if (previous.PackedQuads.IsLive) packed.Retire(previous.PackedQuads);
            if (previous.ClusteredPackedQuads.IsLive)
                clusteredPacked.Retire(previous.ClusteredPackedQuads);
            Replacements++;
        }

        return true;
    }

    public void Remove(long sectionKey)
    {
        if (!sections.Remove(sectionKey, out MirroredSection section)) return;
        if (section.Vertices.IsLive) vertices.Retire(section.Vertices);
        if (section.Indices.IsLive) indices.Retire(section.Indices);
        if (section.PackedQuads.IsLive) packed.Retire(section.PackedQuads);
        if (section.ClusteredPackedQuads.IsLive)
            clusteredPacked.Retire(section.ClusteredPackedQuads);
    }

    /// <summary>Bounded reclamation for one frame. Never waits on a fence.</summary>
    public int Reclaim() => vertices.Reclaim() + indices.Reclaim() + packed.Reclaim()
        + clusteredPacked.Reclaim();

    /// <summary>
    /// The model, the GPU buffers and the records that make them meaningful are one unit:
    /// a world change drops all three together rather than leaving pages committed against
    /// terrain that no longer exists.
    /// </summary>
    public void Clear()
    {
        sections.Clear();
        regionGroups.Clear();
        vertices.Clear();
        indices.Clear();
        packed.Clear();
        clusteredPacked.Clear();
    }

    public void Dispose()
    {
        sections.Clear();
        regionGroups.Clear();
        vertices.Dispose();
        indices.Dispose();
        packed.Dispose();
        clusteredPacked.Dispose();
        scratch = [];
        readback = [];
    }

    /// <summary>
    /// Finds a page set in this region where both halves fit. Vertices and indices must
    /// land in the same set: an indirect batch binds one buffer of each, so a section split
    /// across two sets could never be drawn with the rest of its region. A half that is
    /// allocated while the other fails is abandoned outright rather than fenced, because
    /// nothing ever saw it.
    /// </summary>
    bool TryAllocatePair(
        long region,
        long vertexBytes,
        long indexBytes,
        out long group,
        out LodGpuArenaRange vertexRange,
        out LodGpuArenaRange indexRange)
    {
        if (!regionGroups.TryGetValue(region, out List<long>? candidates))
        {
            candidates = new List<long>();
            regionGroups[region] = candidates;
        }

        foreach (long candidate in candidates)
        {
            if (TryAllocateIn(candidate, vertexBytes, indexBytes, out vertexRange, out indexRange))
            {
                group = candidate;
                return true;
            }
        }

        long fresh = ++nextGroupId;
        if (TryAllocateIn(fresh, vertexBytes, indexBytes, out vertexRange, out indexRange))
        {
            candidates.Add(fresh);
            group = fresh;
            return true;
        }

        group = 0;
        return false;
    }

    bool TryAllocateSingle(
        long region,
        LodGpuArena arena,
        long bytes,
        out long group,
        out LodGpuArenaRange range)
    {
        if (!regionGroups.TryGetValue(region, out List<long>? candidates))
        {
            candidates = new List<long>();
            regionGroups[region] = candidates;
        }

        foreach (long candidate in candidates)
        {
            if (!arena.TryAllocate(candidate, bytes, out range)) continue;
            group = candidate;
            return true;
        }

        long fresh = ++nextGroupId;
        if (arena.TryAllocate(fresh, bytes, out range))
        {
            candidates.Add(fresh);
            group = fresh;
            return true;
        }

        group = 0;
        return false;
    }

    bool TryAllocateIn(
        long group,
        long vertexBytes,
        long indexBytes,
        out LodGpuArenaRange vertexRange,
        out LodGpuArenaRange indexRange)
    {
        indexRange = LodGpuArenaRange.None;
        if (!vertices.TryAllocate(group, vertexBytes, out vertexRange)) return false;
        if (indices.TryAllocate(group, indexBytes, out indexRange)) return true;

        vertices.Abandon(vertexRange);
        vertexRange = LodGpuArenaRange.None;
        return false;
    }

    bool Skip(long key, bool hadPrevious, in MirroredSection previous)
    {
        SkippedSections++;
        if (!hadPrevious) return false;

        // The previous spans describe geometry this section no longer has. Retiring them
        // keeps the mirror a subset of the truth instead of a stale copy of it.
        sections.Remove(key);
        if (previous.Vertices.IsLive) vertices.Retire(previous.Vertices);
        if (previous.Indices.IsLive) indices.Retire(previous.Indices);
        if (previous.PackedQuads.IsLive) packed.Retire(previous.PackedQuads);
        if (previous.ClusteredPackedQuads.IsLive)
            clusteredPacked.Retire(previous.ClusteredPackedQuads);
        return false;
    }

    static bool ClustersDescribe(ReadOnlySpan<LodPackedCluster> clusters, int quadCount)
    {
        int next = 0;
        int previousCell = -1;
        foreach (LodPackedCluster cluster in clusters)
        {
            if (!cluster.HasGeometry
                || cluster.Cell <= previousCell
                || cluster.Cell >= LodPackedClusterBuilder.CellCount
                || cluster.FirstQuad != next
                || cluster.QuadCount > quadCount - next)
                return false;
            next += cluster.QuadCount;
            previousCell = cluster.Cell;
        }
        return next == quadCount;
    }

    /// <summary>
    /// Reads one stored span back and compares it with the encoding still held in scratch.
    /// Opt-in: this is the Phase 2 content gate, and a readback is far too expensive to run
    /// as ordinary mirroring work.
    /// </summary>
    bool Matches(LodGpuArena arena, in LodGpuArenaRange range, long bytes)
    {
        if (!verify) return true;
        if (readback.Length < bytes) readback = new byte[bytes];

        bool matched = arena.TryRead(range, readback.AsSpan(0, (int)bytes))
            && readback.AsSpan(0, (int)bytes).SequenceEqual(scratch.AsSpan(0, (int)bytes));
        if (!matched) VerificationFailures++;
        return matched;
    }

    void EnsureScratch(long bytes)
    {
        if (bytes > int.MaxValue)
            throw new InvalidOperationException("a single section exceeded the arena scratch limit");
        if (scratch.Length >= bytes) return;
        scratch = new byte[Math.Max(bytes, Math.Min(scratch.Length * 2L, int.MaxValue))];
    }

    public string Describe() =>
        $"retaining {retention}; sections {sections.Count} live, {MirroredSections} mirrored, "
        + $"{Replacements} replaced, "
        + $"{SkippedSections} skipped | vertices {Describe(vertices)} | indices {Describe(indices)}"
        + $" | packed {Describe(packed)}, {PackedMirroredSections} mirrored, "
        + $"{PackedSkippedSections} skipped"
        + $" | clusters {Describe(clusteredPacked)}, {ClusteredPackedMirroredSections} mirrored, "
        + $"{ClusteredPackedSkippedSections} skipped"
        + (verify ? $" | verified {VerifiedSections}, {VerificationFailures} mismatched" : "");

    static string Describe(LodGpuArena arena) =>
        $"{arena.LiveBytes / (1024.0 * 1024.0):0.00}/{arena.CommittedBytes / (1024.0 * 1024.0):0.00} MiB "
        + $"in {arena.PageCount} pages, {arena.PendingRetireBytes / (1024.0 * 1024.0):0.00} MiB "
        + $"retiring in {arena.PendingRetireCount}, frag {arena.Fragmentation:0.00}, "
        + $"{arena.AllocationFailures} failures ({arena.CeilingRejections} ceiling)";
}
