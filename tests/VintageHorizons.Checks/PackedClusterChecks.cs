namespace VintageHorizons.Checks;

/// <summary>
/// Phase 8 cluster geometry and command granularity. These checks establish that the
/// clustered stream is an exact partition of the accepted packed stream, that every box
/// encloses the geometry its command draws, and that missing cluster data falls back as
/// one whole section rather than publishing a partial horizon.
/// </summary>
public static class PackedClusterChecks
{
    public static void Run(Check c)
    {
        GridAndExactCoverage(c);
        LocalBoundsAcrossLevels(c);
        ClusterCommands(c);
        MissingClustersFailOpen(c);
    }

    static void GridAndExactCoverage(Check c)
    {
        c.Eq(4, LodPackedClusterBuilder.SubdivisionsPerAxis,
            "the measured Phase 8 grid is four cells per axis");
        c.Eq(16, LodPackedClusterBuilder.CellCount,
            "a full section has at most sixteen cluster commands");
        c.Eq(16, LodPackedClusterBuilder.ColumnsPerCell,
            "cluster cells tile all sixty-four section columns exactly");

        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(
            Fixtures.SolidSection(yTop: 40, yBottom: 3)));
        c.Eq(16, mesh.PackedOpaqueClusters.Length,
            "a full flat section populates every spatial cluster");
        c.True(mesh.ClusteredPackedOpaqueQuads.Length > mesh.PackedOpaqueQuads.Length,
            "the comparison records the real cost of splitting large greedy quads");

        int next = 0;
        foreach (LodPackedCluster cluster in mesh.PackedOpaqueClusters)
        {
            c.Eq(next, cluster.FirstQuad, $"cell {cluster.Cell} begins after the previous cell");
            next += cluster.QuadCount;
        }
        c.Eq(mesh.ClusteredPackedOpaqueQuads.Length / LodPackedQuadFormat.WordsPerQuad,
            next, "cluster ranges cover the complete stream with no gap or overlap");

        Dictionary<LodPackedFace, double> whole = Areas(mesh.PackedOpaqueQuads);
        Dictionary<LodPackedFace, double> split = Areas(mesh.ClusteredPackedOpaqueQuads);
        foreach (LodPackedFace face in Enum.GetValues<LodPackedFace>())
            c.Near(whole.GetValueOrDefault(face), split.GetValueOrDefault(face), 0.001,
                face + " area is unchanged by cluster subdivision");
    }

    static void LocalBoundsAcrossLevels(Check c)
    {
        foreach (int level in new[] { 0, 2, LodWorld.MaxLevel })
        {
            long key = LodWorld.SectionKey(level, 2, -3);
            MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(
                Fixtures.SolidSection(yTop: 80, yBottom: 7), key));
            int step = LodWorld.ColumnStepBlocks(level);

            foreach (LodPackedCluster cluster in mesh.PackedOpaqueClusters)
            {
                int cellX = cluster.Cell % LodPackedClusterBuilder.SubdivisionsPerAxis;
                int cellZ = cluster.Cell / LodPackedClusterBuilder.SubdivisionsPerAxis;
                float cellMinX = cellX * LodPackedClusterBuilder.ColumnsPerCell * step;
                float cellMaxX = cellMinX + LodPackedClusterBuilder.ColumnsPerCell * step;
                float cellMinZ = cellZ * LodPackedClusterBuilder.ColumnsPerCell * step;
                float cellMaxZ = cellMinZ + LodPackedClusterBuilder.ColumnsPerCell * step;
                c.True(cluster.MinX >= cellMinX && cluster.MaxX <= cellMaxX
                    && cluster.MinZ >= cellMinZ && cluster.MaxZ <= cellMaxZ,
                    $"L{level} cell {cluster.Cell} bounds stay inside their spatial cell");

                for (int q = 0; q < cluster.QuadCount; q++)
                {
                    int quad = cluster.FirstQuad + q;
                    for (int v = 0; v < LodPackedQuadFormat.VerticesPerQuad; v++)
                    {
                        LodPackedQuadFormat.DecodeVertex(
                            mesh.ClusteredPackedOpaqueQuads, quad, v, step,
                            out float x, out float y, out float z, out _);
                        c.True(x >= cluster.MinX && x <= cluster.MaxX
                            && y >= cluster.MinY && y <= cluster.MaxY
                            && z >= cluster.MinZ && z <= cluster.MaxZ,
                            $"L{level} cell {cluster.Cell} bounds enclose quad {q} vertex {v}");
                    }
                }
            }
        }
    }

    static void ClusterCommands(Check c)
    {
        var backend = new FakeBackend();
        LodGpuArenaLimits small = new(256 * 1024, 8 * 1024 * 1024, 16);
        using var mirror = new LodGpuGeometryMirror(
            backend, 8 * 1024 * 1024,
            vertexLimits: small, indexLimits: small,
            packedLimits: small, clusteredPackedLimits: small);
        long key = LodWorld.SectionKey(2, 3, -4);
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(
            Fixtures.SolidSection(yTop: 70, yBottom: 5), key));
        c.True(mirror.Mirror(Publication(key, mesh, includeClusters: true)),
            "the clustered companion publishes beside accepted geometry");
        mirror.TryGet(key, out LodGpuGeometryMirror.MirroredSection section);
        c.True(section.ClusteredPackedQuads.IsLive,
            "cluster commands never point at an uncommitted arena range");
        c.Eq(mesh.PackedOpaqueClusters.Length, section.PackedClusters.Length,
            "the mirror copies the complete immutable cluster table");

        LodGpuSectionFacts facts = Facts(key);
        var builder = new LodGpuIndirectBuilder(packed: true, clustered: true);
        builder.Begin();
        c.True(builder.Add(section, facts), "one CPU-approved section admits all its clusters");
        builder.End(mirror);

        c.True(builder.Packed && builder.Clustered,
            "the command list identifies clustered packed geometry explicitly");
        c.Eq(1, builder.AddedSections, "coverage remains section-based");
        c.Eq(section.PackedClusters.Length, builder.CommandCount,
            "one independently culled command is emitted per populated cluster");
        c.Eq(1, builder.Batches.Count,
            "clusters sharing one regional page remain one multi-draw batch");

        for (int i = 0; i < section.PackedClusters.Length; i++)
        {
            LodPackedCluster cluster = section.PackedClusters[i];
            c.Eq((uint)(cluster.QuadCount * LodPackedQuadFormat.IndicesPerQuad),
                LodGpuIndirectCommand.ReadUInt(
                    builder.Commands, i, LodGpuPackedIndirectCommand.CountOffset),
                $"cluster {i} command covers only its contiguous quad range");
            c.Eq((int)((section.FirstClusteredPackedQuad + cluster.FirstQuad)
                    * LodPackedQuadFormat.PulledVerticesPerQuad),
                LodGpuIndirectCommand.ReadInt(
                    builder.Commands, i, LodGpuPackedIndirectCommand.BaseVertexOffset),
                $"cluster {i} command starts at its own packed range");
            c.Near(facts.OriginRelX + cluster.MinX,
                LodGpuCullBox.ReadFloat(builder.Boxes, i, LodGpuCullBox.MinOffset),
                0.0001, $"cluster {i} cull box uses its local X minimum");
            c.Near(facts.OriginRelY + cluster.MinY,
                LodGpuCullBox.ReadFloat(builder.Boxes, i, LodGpuCullBox.MinOffset + 4),
                0.0001, $"cluster {i} cull box uses its geometry Y minimum");
            c.Near(facts.OriginRelZ + cluster.MaxZ,
                LodGpuCullBox.ReadFloat(builder.Boxes, i, LodGpuCullBox.MaxOffset + 8),
                0.0001, $"cluster {i} cull box uses its local Z maximum");
            c.Near(facts.SectionSize,
                LodGpuSectionRecord.ReadFloat(
                    builder.Records, i, LodGpuSectionRecord.SectionSizeOffset),
                0.0001, $"cluster {i} keeps whole-section shader addressing");
        }
    }

    static void MissingClustersFailOpen(Check c)
    {
        var backend = new FakeBackend();
        LodGpuArenaLimits small = new(256 * 1024, 8 * 1024 * 1024, 16);
        using var mirror = new LodGpuGeometryMirror(
            backend, 8 * 1024 * 1024,
            vertexLimits: small, indexLimits: small,
            packedLimits: small, clusteredPackedLimits: small);
        long key = LodWorld.SectionKey(0, 7, 9);
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(
            Fixtures.SolidSection(yTop: 20, yBottom: 2), key));
        mirror.Mirror(Publication(key, mesh, includeClusters: false));
        mirror.TryGet(key, out LodGpuGeometryMirror.MirroredSection section);

        var clusters = new LodGpuIndirectBuilder(packed: true, clustered: true);
        clusters.Begin();
        c.False(clusters.Add(section, Facts(key)),
            "missing cluster publication refuses the complete section command set");
        c.Eq(1, clusters.CandidatesDropped,
            "the refused cluster section remains visible in telemetry");
        c.Eq(0, clusters.CommandCount,
            "a failure cannot leave a partially drawn cluster set");

        var whole = new LodGpuIndirectBuilder(packed: true);
        whole.Begin();
        c.True(whole.Add(section, Facts(key)),
            "the accepted whole-section packed fallback remains ready");
    }

    static Dictionary<LodPackedFace, double> Areas(uint[] words)
    {
        var result = new Dictionary<LodPackedFace, double>();
        int quads = words.Length / LodPackedQuadFormat.WordsPerQuad;
        for (int q = 0; q < quads; q++)
        {
            double area = TriangleArea(words, q, 0, 1, 2)
                + TriangleArea(words, q, 3, 4, 5);
            LodPackedFace face = LodPackedQuadFormat.FaceOf(words, q);
            result[face] = result.GetValueOrDefault(face) + area;
        }
        return result;
    }

    static double TriangleArea(uint[] words, int quad, int a, int b, int d)
    {
        LodPackedQuadFormat.DecodeVertex(words, quad, a, 1,
            out float ax, out float ay, out float az, out _);
        LodPackedQuadFormat.DecodeVertex(words, quad, b, 1,
            out float bx, out float by, out float bz, out _);
        LodPackedQuadFormat.DecodeVertex(words, quad, d, 1,
            out float dx, out float dy, out float dz, out _);
        double ux = bx - ax, uy = by - ay, uz = bz - az;
        double vx = dx - ax, vy = dy - ay, vz = dz - az;
        double cx = uy * vz - uz * vy;
        double cy = uz * vx - ux * vz;
        double cz = ux * vy - uy * vx;
        return 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
    }

    static LodRenderPublication Publication(long key, MeshResult mesh, bool includeClusters)
    {
        return new LodRenderPublication(
            new LodRenderResourceIdentity(1, key, 1, 1, 0),
            null, null,
            mesh.VertexCount, mesh.IndexCount, 0, 0, 0,
            new LodRenderGeometry(
                mesh.Xyz, mesh.Rgba, mesh.Indices,
                mesh.PackedOpaqueQuads, mesh.PackedOpaqueQuadCount,
                includeClusters ? mesh.ClusteredPackedOpaqueQuads : null,
                includeClusters ? mesh.PackedOpaqueClusters : null));
    }

    static LodGpuSectionFacts Facts(long key)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        return new LodGpuSectionFacts(
            key,
            OriginRelX: 100f,
            OriginRelY: -64f,
            OriginRelZ: -250f,
            SectionSize: footprint,
            NoiseOriginX: 1000f,
            NoiseOriginZ: -2000f,
            ColumnBlocks: LodWorld.ColumnStepBlocks(LodWorld.KeyLevel(key)),
            MaskOriginX: 0,
            MaskOriginZ: 0,
            OpenEdges: 0,
            BoxMinY: -60f,
            BoxMaxY: 20f);
    }

    sealed class FakeBackend : ILodGpuArenaBackend
    {
        readonly Dictionary<(LodGpuArenaKind Kind, int Page), byte[]> pages = new();
        int nextPage = 1;
        long nextFence = 1;

        public int CreatePage(LodGpuArenaKind kind, long bytes)
        {
            int page = nextPage++;
            pages[(kind, page)] = new byte[bytes];
            return page;
        }

        public bool Upload(LodGpuArenaKind kind, int page, long offset, ReadOnlySpan<byte> data)
        {
            if (!pages.TryGetValue((kind, page), out byte[]? target)) return false;
            data.CopyTo(target.AsSpan((int)offset));
            return true;
        }

        public bool TryRead(LodGpuArenaKind kind, int page, long offset, Span<byte> destination)
        {
            if (!pages.TryGetValue((kind, page), out byte[]? source)) return false;
            source.AsSpan((int)offset, destination.Length).CopyTo(destination);
            return true;
        }

        public void DeletePage(LodGpuArenaKind kind, int page) => pages.Remove((kind, page));
        public long CreateFence() => nextFence++;
        public bool FenceSignaled(long fence) => true;
        public void DeleteFence(long fence) { }
    }
}
