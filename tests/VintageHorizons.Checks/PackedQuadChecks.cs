namespace VintageHorizons.Checks;

/// <summary>Exact packed-quad geometry, before any GPU path is allowed to draw it.</summary>
public static class PackedQuadChecks
{
    public static void Run(Check c)
    {
        LayoutAndLimits(c);
        AllFacesMatchExpandedMesh(c);
        LevelsAndCoordinateLimits(c);
        MaterialPayloadsAndDegenerates(c);
        Rejections(c);
    }

    static void LayoutAndLimits(Check c)
    {
        c.Eq(12, LodPackedQuadFormat.StrideBytes, "one exact packed quad is twelve bytes");
        c.Eq(3, LodPackedQuadFormat.WordsPerQuad, "the byte layout is three scalar words");
        c.Eq(6, LodPackedQuadFormat.VerticesPerQuad, "expanded parity covers two triangles");
        c.Eq(4, LodPackedQuadFormat.PulledVerticesPerQuad,
            "the indexed shader decodes each unique corner once");
        c.Eq(6, LodPackedQuadFormat.IndicesPerQuad,
            "the reusable topology still emits two triangles");
        c.Eq(LodSection.GridSize, LodPackedQuadFormat.MaximumGridCoordinate,
            "section endpoints include the far edge");
        c.Eq(88L, LodPackedQuadFormat.ExpandedBytes(1),
            "the comparison includes four vertices and six indices");
        c.Eq(12L, LodPackedQuadFormat.Bytes(1), "packed storage has no index payload");
        c.True(LodPackedQuadFormat.Bytes(100) * 2 < LodPackedQuadFormat.ExpandedBytes(100),
            "the selected format clears the phase's fifty-percent byte gate");
    }

    static void AllFacesMatchExpandedMesh(Check c)
    {
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(IsolatedRun(10, 4)));
        c.Eq(6, mesh.PackedOpaqueQuadCount, "an isolated solid emits all six packed faces");
        var faces = new HashSet<LodPackedFace>();
        for (int quad = 0; quad < mesh.PackedOpaqueQuadCount; quad++)
            faces.Add(LodPackedQuadFormat.FaceOf(mesh.PackedOpaqueQuads, quad));
        foreach (LodPackedFace face in Enum.GetValues<LodPackedFace>())
            c.True(faces.Contains(face), face + " winding is represented");

        AssertMatchesExpanded(c, mesh, columnBlocks: 1, "isolated L0 run");
    }

    static void LevelsAndCoordinateLimits(Check c)
    {
        foreach (int level in new[] { 0, 1, 3, LodWorld.MaxLevel })
        {
            MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(
                IsolatedRun(0x3FFF, 0x3FFE), LodWorld.SectionKey(level, 0, 0)));
            AssertMatchesExpanded(c, mesh, 1 << level, $"L{level} maximum-height run");
        }

        var words = new uint[LodPackedQuadFormat.WordsPerQuad];
        LodPackedQuadFormat.Encode(words, LodPackedFace.Top,
            0, LodSection.GridSize, 1, 1, 0, LodSection.GridSize,
            0x00332211, 63);
        LodPackedQuadFormat.DecodeVertex(words, 0, 2, 64,
            out float x, out float y, out float z, out uint color);
        c.Eq(4096f, x, "L6 reaches the exact far X edge");
        c.Eq(1f, y, "Y never scales with LOD");
        c.Eq(4096f, z, "L6 reaches the exact far Z edge");
        c.Eq(0x3F332211u, color, "the full colour/tint word survives");
    }

    static void MaterialPayloadsAndDegenerates(Check c)
    {
        foreach (byte alpha in new byte[] { 0, 63, 64, 127, 128, 191, 255 })
        {
            var words = new uint[LodPackedQuadFormat.WordsPerQuad];
            LodPackedQuadFormat.Encode(words, LodPackedFace.North,
                5, 5, 4.25f, 4.25f, 7, 7, 0x00CCBBAA, alpha);
            LodPackedQuadFormat.DecodeVertex(words, 0, 5, 8,
                out float x, out float y, out float z, out uint color);
            c.Eq(40f, x, $"degenerate X survives for alpha {alpha}");
            c.Eq(4.25f, y, $"quarter-block height survives for alpha {alpha}");
            c.Eq(56f, z, $"degenerate Z survives for alpha {alpha}");
            c.Eq(0x00CCBBAAu | (uint)alpha << 24, color,
                $"material/tint alpha {alpha} survives exactly");
        }
    }

    static void Rejections(Check c)
    {
        var words = new uint[LodPackedQuadFormat.WordsPerQuad];
        c.Throws<ArgumentOutOfRangeException>(() => LodPackedQuadFormat.Encode(
            words, LodPackedFace.Top, -1, 1, 0, 0, 0, 1, 0, 0),
            "negative grid coordinates are refused");
        c.Throws<ArgumentOutOfRangeException>(() => LodPackedQuadFormat.Encode(
            words, LodPackedFace.Top, 0, 65, 0, 0, 0, 1, 0, 0),
            "coordinates past the section edge are refused");
        c.Throws<ArgumentOutOfRangeException>(() => LodPackedQuadFormat.Encode(
            words, LodPackedFace.Top, 0, 1, 0.1f, 1, 0, 1, 0, 0),
            "non-quarter heights are refused rather than rounded");
        c.Throws<ArgumentException>(() => LodPackedQuadFormat.Encode(
            words, LodPackedFace.Top, 0, 1, 2, 1, 0, 1, 0, 0),
            "inverted height ranges are refused");
    }

    static void AssertMatchesExpanded(Check c, MeshResult mesh, int columnBlocks, string what)
    {
        c.Eq(mesh.VertexCount / 4, mesh.PackedOpaqueQuadCount,
            what + " has one record per expanded quad");
        c.Eq(mesh.PackedOpaqueQuadCount * LodPackedQuadFormat.WordsPerQuad,
            mesh.PackedOpaqueQuads.Length, what + " has no unused packed words");

        for (int quad = 0; quad < mesh.PackedOpaqueQuadCount; quad++)
        {
            for (int emitted = 0; emitted < LodPackedQuadFormat.VerticesPerQuad; emitted++)
            {
                int expandedVertex = mesh.Indices[quad * 6 + emitted];
                LodPackedQuadFormat.DecodeVertex(mesh.PackedOpaqueQuads, quad, emitted,
                    columnBlocks, out float x, out float y, out float z, out uint color);
                c.Eq(mesh.Xyz[expandedVertex * 3], x, what + " X parity");
                c.Eq(mesh.Xyz[expandedVertex * 3 + 1], y, what + " Y parity");
                c.Eq(mesh.Xyz[expandedVertex * 3 + 2], z, what + " Z parity");
                uint expandedColor = (uint)mesh.Rgba[expandedVertex * 4]
                    | (uint)mesh.Rgba[expandedVertex * 4 + 1] << 8
                    | (uint)mesh.Rgba[expandedVertex * 4 + 2] << 16
                    | (uint)mesh.Rgba[expandedVertex * 4 + 3] << 24;
                c.Eq(expandedColor, color, what + " RGBA/tint parity");
            }
        }
    }

    static LodSection IsolatedRun(int yTop, int yBottom)
    {
        var section = new LodSection();
        section.FindOrAddPaletteEntry(1, 0x00332211, 0, 63);
        section.SetColumn(LodSection.ColumnIndex(5, 7),
            new[] { LodSection.PackRun(0, yTop, yBottom) });
        return section;
    }
}
