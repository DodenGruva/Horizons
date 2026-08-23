namespace VintageHorizons.Checks;

/// <summary>
/// Section snapshot to vertex data. Two things are worth pinning here: the greedy merge,
/// which is the difference between five quads and four thousand for the same terrain, and
/// the coverage rules, which are deliberately asymmetric and were each arrived at by
/// finding the artefact the symmetric version produced.
/// </summary>
public static class MesherChecks
{
    const int Gs = LodSection.GridSize;

    public static void Run(Check c)
    {
        Empty(c);
        GreedyMerge(c);
        FaceWinding(c);
        UnevenBase(c);
        LevelScaling(c);
        AlphaBands(c);
        WaterIsASeparatePass(c);
        ThinMats(c);
        CoverageRules(c);
        Frontier(c);
        UnloadedNeighbourIsNotTheFrontier(c);
        HeightBounds(c);
        HeightSpanAlgebra(c);
        HeightDistribution(c);
        FrontierWallsSetTheFloor(c);
    }

    /// <summary>
    /// Every mesh reports the vertical extent of what it emitted, and the renderer culls
    /// with that instead of a bedrock-to-sky box. The gate is one-directional: a bound
    /// that is too LOOSE costs a little culling, and a bound that is too TIGHT deletes
    /// terrain the player can see. So the shape of this check is to build the mesh, read
    /// every vertex the mesher actually emitted, and require the reported span to contain
    /// all of them - across every face direction, both passes, and every LOD scale.
    /// </summary>
    static void HeightBounds(Check c)
    {
        // The degenerate case first: nothing emitted is not a span of zero height at y=0,
        // which would put a box on the bedrock of a section that draws nothing.
        MeshResult empty = LodMesher.BuildMesh(Fixtures.Job(new LodSection()));
        c.False(empty.Heights.Opaque.HasGeometry, "an empty section reports no opaque span");
        c.False(empty.Heights.Water.HasGeometry, "an empty section reports no water span");
        c.False(empty.Heights.HasGeometry, "an empty section has no span at all");

        // A single flat quad IS a real span, and a zero-height one. This is the case the
        // flag exists for: a plain at y=10 must bound at 10, not read as "unknown".
        var allFour = new SectionSnapshot?[4];
        LodSection plain = Solid(yTop: 10, yBottom: 0);
        for (int i = 0; i < 4; i++) allFour[i] = Fixtures.Snap(plain);
        MeshResult surface = LodMesher.BuildMesh(Fixtures.Job(plain, 0, allFour));
        c.Eq(1, Quads(surface.VertexCount), "the fully surrounded plain is a single quad");
        c.True(surface.Heights.Opaque.HasGeometry, "a single flat quad still reports a span");
        c.Eq(10f, surface.Heights.Opaque.MinY, "the flat span sits at the surface");
        c.Eq(10f, surface.Heights.Opaque.MaxY, "a flat quad's span has no height");
        c.Eq(0f, surface.Heights.Opaque.Height, "a flat quad's span measures zero blocks tall");

        // And the point of measuring the mesh rather than the section: that plain stores
        // ten blocks of ground and draws one plane. Bounding what is stored would give
        // back a box eleven times too tall for the geometry inside it.
        c.True(surface.Heights.Opaque.MinY > 0f,
            "the span describes what is drawn, not what is stored");

        // Every face direction, both passes, and coarse levels where X/Z scale but Y does
        // not. Each case is checked against its own emitted vertices rather than against a
        // number written here, so a change to the mesher cannot silently invalidate it.
        AssertBoundsContainGeometry(c, Fixtures.Job(Column(yTop: 10, yBottom: 4)),
            "an isolated run exposing all six faces");
        AssertBoundsContainGeometry(c, Fixtures.Job(Ocean()), "an ocean over a bumpy seabed");
        AssertBoundsContainGeometry(c, Fixtures.Job(Ocean(), 0, AllNeighbours(Ocean())),
            "an ocean with every neighbour present");
        AssertBoundsContainGeometry(c, Fixtures.Job(Column(LodPaletteEntry.FlagThin, yTop: 40, yBottom: 4)),
            "a mip-merged thin mat");
        AssertBoundsContainGeometry(c, Fixtures.Job(plain, 0, allFour), "a fully surrounded plain");
        for (int level = 0; level <= 3; level++)
        {
            AssertBoundsContainGeometry(c, Fixtures.Job(Ocean(), LodWorld.SectionKey(level, 0, 0)),
                $"an ocean section at L{level}");
        }

        // The coordinate limits: a run at the very bottom of the world and one at the top.
        AssertBoundsContainGeometry(c, Fixtures.Job(Column(yTop: 1, yBottom: 0)),
            "a run on the bedrock floor");
        AssertBoundsContainGeometry(c, Fixtures.Job(Column(yTop: 0x3FFF, yBottom: 0x3FFE)),
            "a run at the top of the world");

        // Y does not scale with level, so neither may the span - a scaled bound would sink
        // coarse terrain's box into the ground, further at every level out.
        MeshResult l0 = LodMesher.BuildMesh(Fixtures.Job(plain, LodWorld.SectionKey(0, 0, 0)));
        MeshResult l3 = LodMesher.BuildMesh(Fixtures.Job(plain, LodWorld.SectionKey(3, 0, 0)));
        c.Eq(l0.Heights.Opaque.MaxY, l3.Heights.Opaque.MaxY, "the span is absolute blocks at every level");
        c.Eq(l0.Heights.Opaque.MinY, l3.Heights.Opaque.MinY, "including its floor");

        // Per pass, because the two are submitted separately. The water surface sits a
        // hundred blocks above the seabed, and one box round both would be most of the
        // difference this work exists to remove.
        MeshResult sea = LodMesher.BuildMesh(Fixtures.Job(Ocean()));
        c.True(sea.Heights.Water.MinY > sea.Heights.Opaque.MinY,
            "the water span starts above the seabed's");
        c.True(sea.Heights.Water.MaxY > sea.Heights.Opaque.MaxY,
            "and reaches higher than any solid geometry in the section");
        c.True(sea.Heights.Either.Height > sea.Heights.Opaque.Height,
            "the combined span is taller than either pass alone");

        // The thin-mat lift is drawn geometry, so the span has to include it. Bounding the
        // stored run instead would leave the mat a quarter block outside its own box.
        MeshResult mat = LodMesher.BuildMesh(Fixtures.Job(Column(LodPaletteEntry.FlagThin, yTop: 10, yBottom: 4)));
        c.Eq(4.25f, mat.Heights.Water.MaxY, "the mat's span follows the quarter-block lift");
        c.False(mat.Heights.Opaque.HasGeometry, "and contributes nothing to the opaque span");
    }

    /// <summary>
    /// The span type's own rules. Two matter to the renderer: an unset span must read as
    /// unknown so a missing measurement falls back to the full-height box, and a union
    /// with an empty side must not be dragged down to zero.
    /// </summary>
    static void HeightSpanAlgebra(Check c)
    {
        c.False(default(LodHeightSpan).HasGeometry, "an unset span is unknown, not a box at y=0");
        c.False(LodHeightSpan.Empty.HasGeometry, "and so is the named empty span");
        c.Eq(0f, LodHeightSpan.Empty.Height, "an empty span measures nothing");

        LodHeightSpan low = LodHeightSpan.Of(10f, 20f);
        LodHeightSpan high = LodHeightSpan.Of(90f, 110f);
        c.Eq(10f, low.Union(high).MinY, "a union takes the lower floor");
        c.Eq(110f, low.Union(high).MaxY, "and the higher ceiling");
        c.Eq(low, low.Union(LodHeightSpan.Empty), "an empty side contributes nothing to a union");
        c.Eq(low, LodHeightSpan.Empty.Union(low), "in either order");
        c.False(LodHeightSpan.Empty.Union(LodHeightSpan.Empty).HasGeometry,
            "two empty spans stay empty");

        // Inverted input is a bug in the caller, and must fail open rather than produce a
        // box that rejects everything inside it.
        c.False(LodHeightSpan.Of(20f, 10f).HasGeometry, "an inverted span is refused");
        c.True(LodHeightSpan.Of(10f, 10f).HasGeometry, "a zero-height span is legitimate");
    }

    /// <summary>
    /// The distribution reported once per interval. It exists to answer one question
    /// before Phase 4 of the GPU plan is built: how much of the world height does an
    /// ordinary section really occupy? If the answer is "most of it", a depth pyramid
    /// cannot reject anything and the phase needs revisiting first.
    /// </summary>
    static void HeightDistribution(Check c)
    {
        var stats = new LodSectionHeightStats();
        c.Eq(0L, stats.Samples, "a fresh distribution has no samples");

        stats.Add(LodHeightSpan.Empty);
        c.Eq(0L, stats.Samples, "a section that drew nothing is not a section of height zero");

        stats.Add(LodHeightSpan.Of(60f, 62f));   // 2 blocks
        stats.Add(LodHeightSpan.Of(10f, 110f));  // 100 blocks
        c.Eq(2L, stats.Samples, "both real spans are counted");
        c.Near(51.0, stats.MeanBlocks, 0.001, "the mean is over drawn heights");
        c.Eq(100f, stats.MaxBlocks, "the tallest section is kept");
        c.True(stats.Describe(256).Contains("0-4: 50%"), "the buckets are reported as shares");
        c.False(stats.Describe(256).Contains("<"), "no bucket label opens a VTML tag in chat");

        // Floor and ceiling apart, because the height alone cannot tell a genuinely tall
        // mountain from a low surface whose bound is dragged down to bedrock, and those
        // two want opposite responses.
        c.Near(35.0, stats.MeanFloor, 0.001, "the mean floor is over the spans' own minima");
        c.Near(86.0, stats.MeanCeiling, 0.001, "and the mean ceiling over their maxima");

        stats.Reset();
        c.Eq(0L, stats.Samples, "a reset distribution starts the next interval clean");
        c.Eq(0f, stats.MaxBlocks, "including its maximum");
    }

    /// <summary>
    /// Why a real section reads as most of the world tall, measured rather than assumed.
    ///
    /// The owner's 2026-08-22 log reported a mean span of 146 blocks in a 256-block world
    /// with NOT ONE section under 64 blocks. His own reading is the start of the answer:
    /// ground level sits near y=100 and the world continues down to bedrock, so a surface
    /// column is one run from 0 to about 110 - the stored data really is that tall.
    ///
    /// But the span measures what is DRAWN, and a surface is one plane. What reaches
    /// bedrock is the frontier wall: a section whose neighbour is missing walls its whole
    /// edge, and that wall is as tall as the run behind it, which is the entire column.
    /// One such edge sets the section's floor to 0 however shallow the visible ground is.
    ///
    /// The two cases below differ in nothing except whether the neighbours are present,
    /// and the span goes from the full column to a single plane. That is the mechanism,
    /// and it says the fix is not a tighter bound - the bound is honest - but that the
    /// frontier curtain and the surface want separate boxes.
    /// </summary>
    static void FrontierWallsSetTheFloor(Check c)
    {
        // One run per column from bedrock to a surface at the owner's stated ground level.
        var ground = new LodSection();
        ground.FindOrAddPaletteEntry(blockId: 1, color: 0x00405060, flags: 0);
        for (int col = 0; col < Fixtures.Total; col++)
        {
            ground.SetColumn(col, new[] { LodSection.PackRun(0, 110, 0) });
        }

        MeshResult frontier = LodMesher.BuildMesh(Fixtures.Job(ground));
        MeshResult interior = LodMesher.BuildMesh(Fixtures.Job(ground, 0, AllNeighbours(ground)));

        c.Eq(110f, interior.Heights.Opaque.MinY, "with neighbours, only the surface is drawn");
        c.Eq(110f, interior.Heights.Opaque.MaxY, "so the section's box is one plane");
        c.Eq(0f, interior.Heights.Opaque.Height, "and has no vertical extent at all");

        c.Eq(0f, frontier.Heights.Opaque.MinY, "one missing neighbour walls the edge to bedrock");
        c.Eq(110f, frontier.Heights.Opaque.MaxY, "up to the surface it is holding back");
        c.Eq(110f, frontier.Heights.Opaque.Height,
            "so an identical section reads as the whole column purely because it is at the frontier");

        // The cause is the wall and nothing else: the same terrain, meshed both ways, is
        // one quad against five, and the four extra are the curtain.
        c.Eq(1, Quads(interior.VertexCount), "the interior section is a single surface quad");
        c.Eq(5, Quads(frontier.VertexCount), "the frontier section adds four full-height walls");

        // A single open side is enough. This is what makes the measurement so lopsided at
        // join: a growing frontier means a large share of sections have at least one.
        var oneOpen = AllNeighbours(ground);
        oneOpen[0] = null;
        MeshResult single = LodMesher.BuildMesh(Fixtures.Job(ground, 0, oneOpen));
        c.Eq(0f, single.Heights.Opaque.MinY,
            "one open side out of four still drops the whole section's floor to bedrock");
    }

    /// <summary>
    /// Reads back every vertex the mesher emitted and requires the reported span to be
    /// exactly its range. Exactly, not merely containing: a loose bound is safe but silent,
    /// and a bound that drifts loose over time gives back the culling this phase exists for.
    /// </summary>
    static void AssertBoundsContainGeometry(Check c, MeshJob job, string what)
    {
        MeshResult mesh = LodMesher.BuildMesh(job);
        AssertPassBounds(c, mesh.Xyz, mesh.VertexCount, mesh.Heights.Opaque, what + ", opaque");
        AssertPassBounds(c, mesh.WaterXyz, mesh.WaterVertexCount, mesh.Heights.Water, what + ", water");
    }

    static void AssertPassBounds(Check c, float[]? xyz, int vertexCount, LodHeightSpan span, string what)
    {
        if (xyz == null || vertexCount == 0)
        {
            c.False(span.HasGeometry, what + ": a pass that emitted nothing reports no span");
            return;
        }

        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        for (int v = 0; v < vertexCount; v++)
        {
            float y = xyz[v * 3 + 1];
            if (y < min) min = y;
            if (y > max) max = y;
        }

        c.True(span.HasGeometry, what + ": a pass that emitted geometry reports a span");
        c.Eq(min, span.MinY, what + ": the span's floor is the lowest emitted vertex");
        c.Eq(max, span.MaxY, what + ": the span's ceiling is the highest emitted vertex");
    }

    /// <summary>
    /// Hardware back-face rejection is safe only when every opaque face is wound from
    /// outside the represented run. OpenGL treats counter-clockwise triangles as front
    /// faces by default. The original two-sided mesh opposed top and bottom, but emitted
    /// west/east with the same winding and north/south with the same winding; enabling
    /// culling would therefore remove two visible wall directions.
    /// </summary>
    static void FaceWinding(Check c)
    {
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(Column(yTop: 10, yBottom: 4)));
        c.Eq(6, Quads(mesh.VertexCount), "one isolated solid run exposes all six faces");

        AssertNormalOnPlane(c, mesh, axis: 0, value: 5f, -1, 0, 0, "west face winds outward");
        AssertNormalOnPlane(c, mesh, axis: 0, value: 6f, 1, 0, 0, "east face winds outward");
        AssertNormalOnPlane(c, mesh, axis: 1, value: 4f, 0, -1, 0, "bottom face winds outward");
        AssertNormalOnPlane(c, mesh, axis: 1, value: 10f, 0, 1, 0, "top face winds outward");
        AssertNormalOnPlane(c, mesh, axis: 2, value: 5f, 0, 0, -1, "north face winds outward");
        AssertNormalOnPlane(c, mesh, axis: 2, value: 6f, 0, 0, 1, "south face winds outward");
    }

    static void Empty(Check c)
    {
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(new LodSection()));
        c.Eq(0, mesh.VertexCount, "an empty section produces no vertices");
        c.Eq(0, mesh.IndexCount, "an empty section produces no indices");
        c.Eq(null, mesh.WaterXyz, "an empty section produces no water pass");
    }

    /// <summary>
    /// The reason the mesher exists in this shape. A flat plain is 4096 columns with
    /// identical tops; naively that is 4096 quads for the surface plus a wall per column
    /// edge. Merged it is one rectangle and four frontier ribbons.
    /// </summary>
    static void GreedyMerge(Check c)
    {
        LodSection flat = Solid(yTop: 10, yBottom: 0);
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(flat));

        // 1 top rectangle + 4 frontier walls. No bottom faces: yBottom is 0, and the mesher
        // skips floors at or below y=1 because nothing can ever see under the world.
        c.Eq(5, Quads(mesh.VertexCount), "a flat 64x64 plain collapses to five quads");
        c.Eq(20, mesh.VertexCount, "five quads is twenty vertices");
        c.Eq(30, mesh.IndexCount, "five quads is thirty indices (two triangles each)");

        // The merged top must actually span the section, not just claim to.
        float[] xs = Every3rd(mesh.Xyz, 0);
        float[] zs = Every3rd(mesh.Xyz, 2);
        c.Eq(0f, xs.Min(), "the merged surface starts at the section's near edge");
        c.Eq((float)Gs, xs.Max(), "the merged surface reaches the section's far edge");
        c.Eq(0f, zs.Min(), "the merged surface starts at the near z edge");
        c.Eq((float)Gs, zs.Max(), "the merged surface reaches the far z edge");

        // A hole must break the merge, or the rectangle would pave over missing terrain.
        LodSection holed = Solid(yTop: 10, yBottom: 0);
        holed.SetColumn(LodSection.ColumnIndex(32, 32), Array.Empty<ulong>());
        MeshResult holedMesh = LodMesher.BuildMesh(Fixtures.Job(holed));
        c.True(Quads(holedMesh.VertexCount) > 5, "a hole in the plain prevents a single-rectangle merge");

        // Differing heights cannot merge into one plane either.
        LodSection stepped = Solid(yTop: 10, yBottom: 0);
        stepped.SetColumn(LodSection.ColumnIndex(32, 32), new[] { LodSection.PackRun(0, 11, 0) });
        MeshResult steppedMesh = LodMesher.BuildMesh(Fixtures.Job(stepped));
        c.True(Quads(steppedMesh.VertexCount) > 5, "a column at a different height breaks the plane");
    }

    /// <summary>
    /// A surface merges by its own plane, not by whatever sits underneath it. Every top
    /// face here shares a y and a palette entry, and only the depth of the run beneath
    /// them differs, stepping once halfway across the section. The surface is one
    /// rectangle, because a surface quad is drawn at its own y and never reads the depth
    /// below it.
    ///
    /// This is the ocean case, and it is why GreedyMerge above cannot see it: that plain
    /// gives every column the same yBottom. A water run reaches the seabed, so its bottom
    /// tracks the floor contour and changes every few columns while the surface stays
    /// flat. A merge that keys on the run's bottom therefore falls apart on exactly the
    /// terrain the mesher exists to collapse.
    /// </summary>
    static void UnevenBase(Check c)
    {
        var s = new LodSection();
        s.FindOrAddPaletteEntry(blockId: 1, color: 0x00607080, flags: 0);
        for (int cz = 0; cz < Gs; cz++)
        {
            for (int cx = 0; cx < Gs; cx++)
            {
                s.SetColumn(LodSection.ColumnIndex(cx, cz),
                    new[] { LodSection.PackRun(0, 10, cx < Gs / 2 ? 0 : 5) });
            }
        }

        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(s));

        // Counted on the surface plane alone: the walls and the floor under the deep half
        // are real geometry that has nothing to do with the claim being made here.
        c.Eq(1, QuadsOnPlane(mesh.Xyz, axis: 1, value: 10f),
            "one flat surface over a stepped base merges into a single rectangle");

        // The general form, over bases with no pattern to them at all. One step could be
        // merged by a rule that happens to sort the two depths into two contiguous
        // groups; nothing merges 40 scattered depths into one rectangle except not
        // keying the surface on depth in the first place.
        foreach (int seed in new[] { 1, 2, 3 })
        {
            var rnd = new Random(seed);
            var noisy = new LodSection();
            noisy.FindOrAddPaletteEntry(blockId: 1, color: 0x00607080, flags: 0);
            for (int col = 0; col < Fixtures.Total; col++)
            {
                noisy.SetColumn(col, new[] { LodSection.PackRun(0, 10, rnd.Next(1, 9)) });
            }

            c.Eq(1, QuadsOnPlane(LodMesher.BuildMesh(Fixtures.Job(noisy)).Xyz, axis: 1, value: 10f),
                $"a flat surface merges whatever the depths beneath it do (seed {seed})");
        }
    }

    /// <summary>
    /// Horizontal extent scales with the level's block step, but Y does not: y values are
    /// absolute world blocks at every level. Scaling Y too would sink coarse terrain into
    /// the ground, progressively further at each level out.
    /// </summary>
    static void LevelScaling(Check c)
    {
        LodSection flat = Solid(yTop: 10, yBottom: 0);

        MeshResult l0 = LodMesher.BuildMesh(Fixtures.Job(flat, LodWorld.SectionKey(0, 0, 0)));
        MeshResult l2 = LodMesher.BuildMesh(Fixtures.Job(flat, LodWorld.SectionKey(2, 0, 0)));

        c.Eq(Quads(l0.VertexCount), Quads(l2.VertexCount), "level does not change the quad count");
        c.Eq((float)Gs, Every3rd(l0.Xyz, 0).Max(), "L0 spans one block per column");
        c.Eq((float)(Gs * 4), Every3rd(l2.Xyz, 0).Max(), "L2 spans four blocks per column");
        c.Eq(10f, Every3rd(l2.Xyz, 1).Max(), "L2 keeps absolute block heights");
    }

    /// <summary>
    /// The tint slot rides in the vertex alpha byte in three bands, because the vertex
    /// format is position plus colour and there is nowhere else to put it. The shader
    /// divides by TINT_SLOTS to recover which band it is - so the band boundaries here and
    /// the constant in the GLSL are the same number seen from two sides.
    /// </summary>
    static void AlphaBands(Check c)
    {
        c.Eq((byte)5, AlphaOf(Column(flags: 0, tintSlot: 5)), "opaque encodes the slot directly");
        c.Eq((byte)(LodTintRegistry.MaxSlots + 5),
            AlphaOf(Column(LodPaletteEntry.FlagWater, tintSlot: 5)), "water sits in the second band");
        c.Eq((byte)(LodTintRegistry.MaxSlots * 2 + 5),
            AlphaOf(Column(LodPaletteEntry.FlagThin, tintSlot: 5)), "thin cover sits in the third band");

        // An out-of-range slot must fall back to the identity tint rather than wrap into
        // the next band and repaint the block as water.
        c.Eq((byte)LodTintRegistry.SlotNone,
            AlphaOf(Column(flags: 0, tintSlot: (byte)LodTintRegistry.MaxSlots)),
            "a slot at the limit falls back to no tint");
        c.Eq((byte)LodTintRegistry.SlotNone,
            AlphaOf(Column(flags: 0, tintSlot: 255)), "a wildly out-of-range slot falls back to no tint");
    }

    static void WaterIsASeparatePass(Check c)
    {
        LodSection sea = Solid(yTop: 10, yBottom: 0, flags: LodPaletteEntry.FlagWater);
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(sea));

        c.Eq(0, mesh.VertexCount, "an all-water section contributes nothing to the opaque pass");
        c.True(mesh.WaterVertexCount > 0, "water geometry lands in the blended pass");
        c.True(mesh.WaterXyz != null && mesh.WaterIndices != null, "the water pass carries its own buffers");

        // Water has no floor quads: they would z-fight with the seabed below.
        LodSection land = Solid(yTop: 10, yBottom: 0);
        MeshResult landMesh = LodMesher.BuildMesh(Fixtures.Job(land));
        c.Eq(0, landMesh.WaterVertexCount, "an all-solid section contributes nothing to the water pass");
    }

    /// <summary>
    /// Ground cover is a few centimetres of plant in a one-block cell. Drawn as a cube it
    /// turned meadows into fields of solid colour, so it is drawn as a mat instead: top face
    /// only, no walls, lifted a quarter block off the soil.
    ///
    /// The offset is measured UP from the run's bottom, never down from its top. Mip merging
    /// fuses adjacent thin runs, so at coarse levels one run can span several blocks, and a
    /// fixed drop from the top left the mat floating in mid-air.
    /// </summary>
    static void ThinMats(Check c)
    {
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(Column(LodPaletteEntry.FlagThin, yTop: 10, yBottom: 4)));

        c.Eq(0, mesh.VertexCount, "thin cover draws nothing in the opaque pass");
        c.Eq(1, Quads(mesh.WaterVertexCount), "thin cover is a single quad: a top face and no walls");
        c.Eq(4.25f, Every3rd(mesh.WaterXyz!, 1).Max(), "the mat sits a quarter block above its own base");

        // A tall run left by mip merging must still sit on the ground, not at its top.
        MeshResult tall = LodMesher.BuildMesh(Fixtures.Job(Column(LodPaletteEntry.FlagThin, yTop: 40, yBottom: 4)));
        c.Eq(4.25f, Every3rd(tall.WaterXyz!, 1).Max(), "a mip-merged tall thin run still sits on its base");

        // Clamped so the mat can never rise above the run it stands for.
        MeshResult flat = LodMesher.BuildMesh(Fixtures.Job(Column(LodPaletteEntry.FlagThin, yTop: 5, yBottom: 5)));
        c.Eq(5f, Every3rd(flat.WaterXyz!, 1).Max(), "a zero-height thin run is clamped to its own top");
    }

    /// <summary>
    /// Three deliberately asymmetric rules, each one the fix for a specific artefact:
    ///   - solid is culled only by solid, so a seabed stays visible through the water;
    ///   - water is culled by anything, so a submerged cliff does not double up;
    ///   - thin cover never culls anything, because a fern on a shoreline was deleting the
    ///     wall of the pond beside it and letting you see through the water's edge.
    /// </summary>
    static void CoverageRules(Check c)
    {
        // Solid beside water: the solid wall survives.
        c.True(WallsBetween(c, LodPaletteEntry.FlagWater, 0) > 0,
            "a solid wall is not culled by water beside it");

        // Water beside solid: the water wall is culled.
        c.Eq(0, WallsBetween(c, 0, LodPaletteEntry.FlagWater),
            "a water wall is culled by solid beside it");

        // Solid beside solid: culled, the ordinary case.
        c.Eq(0, WallsBetween(c, 0, 0), "a solid wall is culled by solid beside it");

        // Solid beside thin: the wall survives, because a mat covers nothing.
        c.True(WallsBetween(c, LodPaletteEntry.FlagThin, 0) > 0,
            "a solid wall is not culled by thin cover beside it");
    }

    /// <summary>
    /// A missing neighbour section is the edge of explored space, and renders as a wall.
    /// Treating it as covered would open the world at every frontier; treating a present
    /// but empty neighbour as a wall would build one down the middle of every plain.
    /// </summary>
    static void Frontier(Check c)
    {
        LodSection flat = Solid(yTop: 10, yBottom: 0);

        MeshResult alone = LodMesher.BuildMesh(Fixtures.Job(flat));
        c.Eq(5, Quads(alone.VertexCount), "with no neighbours, all four frontier walls are drawn");

        // West neighbour present and matching: that wall goes away.
        var withWest = new SectionSnapshot?[4];
        withWest[0] = Fixtures.Snap(flat);
        MeshResult joined = LodMesher.BuildMesh(Fixtures.Job(flat, 0, withWest));
        c.Eq(4, Quads(joined.VertexCount), "a matching west neighbour removes the west wall");

        // All four present: only the surface remains.
        var allFour = new SectionSnapshot?[4];
        for (int i = 0; i < 4; i++) allFour[i] = Fixtures.Snap(flat);
        MeshResult surrounded = LodMesher.BuildMesh(Fixtures.Job(flat, 0, allFour));
        c.Eq(1, Quads(surrounded.VertexCount), "fully surrounded terrain is just its surface");

        // A neighbour that is present but shorter leaves the exposed part of the wall.
        LodSection shorter = Solid(yTop: 4, yBottom: 0);
        var withShort = new SectionSnapshot?[4];
        for (int i = 0; i < 4; i++) withShort[i] = Fixtures.Snap(shorter);
        MeshResult stepped = LodMesher.BuildMesh(Fixtures.Job(flat, 0, withShort));
        c.Eq(5, Quads(stepped.VertexCount), "a shorter neighbour leaves the exposed wall above it");
    }

    /// <summary>
    /// The ocean seam. A section whose neighbour is merely not in RAM is not at the edge
    /// of explored space, and walling that edge off puts a seabed-deep sheet of 66%-opaque
    /// water down the boundary - which shows straight through the flat surface as a dark
    /// line, because water does not hide what is behind it the way ground does.
    ///
    /// Measured before the fix on the section built below: 1 water quad with the
    /// neighbour present, 65 with it absent, 64 of them a 3,200 block^2 wall on the shared
    /// plane. Sections mesh nearest-first, so the outward side of nearly every one of them
    /// was built that way and nothing ever re-meshed it.
    ///
    /// Solid ground keeps the wall either way. It is hidden by the neighbouring terrain,
    /// and dropping it would open a see-through gap at a cliff until the repair lands.
    /// </summary>
    static void UnloadedNeighbourIsNotTheFrontier(Check c)
    {
        LodSection sea = Ocean();
        const int West = 0;

        MeshResult joined = LodMesher.BuildMesh(Fixtures.Job(sea, 0, AllNeighbours(sea)));
        c.Eq(0, QuadsOnPlane(joined.WaterXyz, axis: 0, value: 0f),
            "water between two loaded sections has no wall at all");

        var missingWest = AllNeighbours(sea);
        missingWest[West] = null;

        MeshResult guessed = LodMesher.BuildMesh(Fixtures.Job(sea, 0, missingWest));
        c.True(QuadsOnPlane(guessed.WaterXyz, axis: 0, value: 0f) > 0,
            "an unloaded neighbour still walls the edge when the job does not say otherwise");

        MeshResult repaired = LodMesher.BuildMesh(
            Fixtures.Job(sea, 0, missingWest, assumedCoveredSides: 1 << West));
        c.Eq(0, QuadsOnPlane(repaired.WaterXyz, axis: 0, value: 0f),
            "a side the job marks as merely unloaded grows no water wall");
        c.Eq(Quads(joined.WaterVertexCount), Quads(repaired.WaterVertexCount),
            "and the result matches the mesh the loaded neighbour would have produced");

        // The flag is per side: the other three edges are still the frontier.
        c.True(QuadsOnPlane(repaired.WaterXyz, axis: 0, value: (float)Gs) == 0,
            "the east edge had its neighbour, so it has no wall either");

        LodSection land = Solid(yTop: 10, yBottom: 0);
        var landMissingWest = new SectionSnapshot?[4];
        for (int i = 1; i < 4; i++) landMissingWest[i] = Fixtures.Snap(land);
        MeshResult solid = LodMesher.BuildMesh(
            Fixtures.Job(land, 0, landMissingWest, assumedCoveredSides: 1 << West));
        c.True(QuadsOnPlane(solid.Xyz, axis: 0, value: 0f) > 0,
            "solid ground keeps its wall on an assumed-covered side");
    }

    /// <summary>A full section of water over a bumpy seabed - the shape that produced the seam.</summary>
    static LodSection Ocean()
    {
        var s = new LodSection();
        int water = s.FindOrAddPaletteEntry(blockId: 1, color: 0x00806040, flags: LodPaletteEntry.FlagWater);
        int rock = s.FindOrAddPaletteEntry(blockId: 2, color: 0x00707070, flags: 0);
        for (int cz = 0; cz < Gs; cz++)
        {
            for (int cx = 0; cx < Gs; cx++)
            {
                // An uneven floor on purpose: it is what stops the wall merging into one
                // ribbon, so the seam is 64 quads per edge rather than one.
                int floor = 58 + ((cx * 7 + cz * 3) % 5);
                s.SetColumn(LodSection.ColumnIndex(cx, cz), new[]
                {
                    LodSection.PackRun(water, 110, floor),
                    LodSection.PackRun(rock, floor, 0),
                });
            }
        }
        return s;
    }

    static SectionSnapshot?[] AllNeighbours(LodSection s)
    {
        var n = new SectionSnapshot?[4];
        for (int i = 0; i < 4; i++) n[i] = Fixtures.Snap(s);
        return n;
    }

    // ---- helpers ----

    /// <summary>
    /// Walls the subject column emits on the edge it shares with its neighbour.
    ///
    /// Both columns' walls land on the same plane - the subject's east face and the
    /// neighbour's west face are both at x = 11 - so the plane alone cannot tell them
    /// apart. The pass does: a translucent column writes to the water buffer and an opaque
    /// one to the opaque buffer, and the two columns here always differ in exactly that.
    /// </summary>
    static int WallsBetween(Check c, byte neighborFlags, byte subjectFlags)
    {
        var s = new LodSection();
        int subject = s.FindOrAddPaletteEntry(blockId: 1, color: 0x00808080, flags: subjectFlags);
        int neighbor = s.FindOrAddPaletteEntry(blockId: 2, color: 0x00304050, flags: neighborFlags);

        s.SetColumn(LodSection.ColumnIndex(10, 10), new[] { LodSection.PackRun(subject, 10, 0) });
        s.SetColumn(LodSection.ColumnIndex(11, 10), new[] { LodSection.PackRun(neighbor, 10, 0) });

        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(s));

        bool subjectIsTranslucent =
            (subjectFlags & (LodPaletteEntry.FlagWater | LodPaletteEntry.FlagThin)) != 0;

        return QuadsOnPlane(subjectIsTranslucent ? mesh.WaterXyz : mesh.Xyz, axis: 0, value: 11f);
    }

    /// <summary>
    /// Quads whose four vertices all share one coordinate: axis 0 for an east/west wall,
    /// axis 1 for a horizontal surface. Counting on a plane keeps a claim about one face
    /// from being answered by the quad count of the whole section.
    /// </summary>
    static int QuadsOnPlane(float[]? xyz, int axis, float value)
    {
        if (xyz == null) return 0;
        int count = 0;
        for (int v = 0; v + 12 <= xyz.Length; v += 12)
        {
            bool onPlane = true;
            for (int k = 0; k < 4; k++)
            {
                if (Math.Abs(xyz[v + k * 3 + axis] - value) > 0.0001f) { onPlane = false; break; }
            }
            if (onPlane) count++;
        }
        return count;
    }

    static void AssertNormalOnPlane(Check c, MeshResult mesh, int axis, float value,
        double expectedX, double expectedY, double expectedZ, string what)
    {
        for (int quad = 0; quad < mesh.VertexCount / 4; quad++)
        {
            int vertexBase = quad * 12;
            bool onPlane = true;
            for (int k = 0; k < 4; k++)
            {
                if (Math.Abs(mesh.Xyz[vertexBase + k * 3 + axis] - value) > 0.0001f)
                {
                    onPlane = false;
                    break;
                }
            }
            if (!onPlane) continue;

            int indexBase = quad * 6;
            int i0 = mesh.Indices[indexBase] * 3;
            int i1 = mesh.Indices[indexBase + 1] * 3;
            int i2 = mesh.Indices[indexBase + 2] * 3;

            double ax = mesh.Xyz[i1] - mesh.Xyz[i0];
            double ay = mesh.Xyz[i1 + 1] - mesh.Xyz[i0 + 1];
            double az = mesh.Xyz[i1 + 2] - mesh.Xyz[i0 + 2];
            double bx = mesh.Xyz[i2] - mesh.Xyz[i0];
            double by = mesh.Xyz[i2 + 1] - mesh.Xyz[i0 + 1];
            double bz = mesh.Xyz[i2 + 2] - mesh.Xyz[i0 + 2];

            double nx = ay * bz - az * by;
            double ny = az * bx - ax * bz;
            double nz = ax * by - ay * bx;
            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);

            c.Near(expectedX, nx / length, 0.0001, what + " X");
            c.Near(expectedY, ny / length, 0.0001, what + " Y");
            c.Near(expectedZ, nz / length, 0.0001, what + " Z");
            return;
        }

        c.True(false, what + " has a quad on the expected plane");
    }

    static byte AlphaOf(LodSection section)
    {
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(section));
        byte[]? rgba = mesh.VertexCount > 0 ? mesh.Rgba : mesh.WaterRgba;
        return rgba is { Length: >= 4 } ? rgba[3] : (byte)255;
    }

    /// <summary>A section with exactly one captured column.</summary>
    static LodSection Column(byte flags = 0, byte tintSlot = 0, int yTop = 10, int yBottom = 0)
    {
        var s = new LodSection();
        s.FindOrAddPaletteEntry(blockId: 1, color: 0x00A0B0C0, flags: flags, tintSlot: tintSlot);
        s.SetColumn(LodSection.ColumnIndex(5, 5), new[] { LodSection.PackRun(0, yTop, yBottom) });
        return s;
    }

    static LodSection Solid(int yTop, int yBottom, byte flags = 0)
    {
        var s = new LodSection();
        s.FindOrAddPaletteEntry(blockId: 1, color: 0x00607080, flags: flags);
        ulong[] run = { LodSection.PackRun(0, yTop, yBottom) };
        for (int col = 0; col < Fixtures.Total; col++) s.SetColumn(col, run);
        return s;
    }

    static int Quads(int vertexCount) => vertexCount / 4;

    static float[] Every3rd(float[] xyz, int offset)
    {
        var result = new float[xyz.Length / 3];
        for (int i = 0; i < result.Length; i++) result[i] = xyz[i * 3 + offset];
        return result;
    }
}
