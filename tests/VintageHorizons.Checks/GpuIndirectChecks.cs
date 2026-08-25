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
        PackedCommandLayout(c);
        PackedBatching(c);
        Batching(c);
        BatchOrdering(c);
        PagePairing(c);
        DrawerIssuesEveryBatch(c);
        DrawerStopsAfterAFailure(c);
        CullBoxesFollowCommandOrder(c);
        CullRunsBetweenUploadAndDraw(c);
        ADeclinedCullStillDrawsEverything(c);
        LiveCullTelemetry(c);
        DepthSplitBoundary(c);
        TwoSameFrameBucketsStayIndependent(c);
    }

    static void DepthSplitBoundary(Check c)
    {
        c.Eq(512f, LodGpuDepthSplitPolicy.NearRadiusBlocks,
            "the measured Phase 6 split stays at 512 blocks");
        c.True(LodGpuDepthSplitPolicy.IsNear(0), "the camera origin is in the near bucket");
        c.True(LodGpuDepthSplitPolicy.IsNear(511.999f),
            "a section just inside the boundary is near");
        c.True(LodGpuDepthSplitPolicy.IsNear(512f),
            "the boundary itself draws before the second picture");
        c.False(LodGpuDepthSplitPolicy.IsNear(512.001f),
            "a section beyond the boundary is culled against the second picture");
        c.False(LodGpuDepthSplitPolicy.IsNear(float.NaN),
            "an invalid distance cannot become a near occluder");
        c.False(LodGpuDepthSplitPolicy.IsNear(float.PositiveInfinity),
            "an unbounded distance cannot become a near occluder");
        c.Eq(4f / 16777215f, LodHzbProjection.OcclusionDepthBias,
            "the coplanar fail-open band is four exact 24-bit depth steps");
    }

    static void LiveCullTelemetry(Check c)
    {
        c.Eq(47, LodGpuCullTelemetryLayout.WordCount,
            "the live counter buffer has the pinned shader word count");
        c.Eq(0, LodGpuCullTelemetryLayout.BandFor(999f),
            "live command distance telemetry starts in the near kilometre");
        c.Eq(1, LodGpuCullTelemetryLayout.BandFor(1000f),
            "and uses the same inclusive kilometre boundary as its shader");
        c.Eq(5, LodGpuCullTelemetryLayout.BandFor(float.PositiveInfinity),
            "an unbounded command lands in the final diagnostic band");

        var words = new uint[LodGpuCullTelemetryLayout.WordCount];
        words[LodGpuCullTelemetryLayout.TestedCommands] = 10;
        words[LodGpuCullTelemetryLayout.CulledCommands] = 6;
        words[LodGpuCullTelemetryLayout.TestedIndices] = 1000;
        words[LodGpuCullTelemetryLayout.CulledIndices] = 750;
        words[LodGpuCullTelemetryLayout.BackgroundVerdicts] = 3;
        words[LodGpuCullTelemetryLayout.OccludedVerdicts] = 6;
        words[LodGpuCullTelemetryLayout.VisibleVerdicts] = 1;
        int far = LodGpuCullTelemetryLayout.BandBase
            + 2 * LodGpuCullTelemetryLayout.BandStride;
        words[far] = 10;
        words[far + 1] = 6;
        words[far + 2] = 1000;
        words[far + 3] = 750;
        words[far + 4] = 3;

        var statistics = new LodGpuCullStatistics();
        statistics.Add(words);
        c.Eq(1L, statistics.Samples,
            "one asynchronous read becomes one live-command sample");
        c.Eq(10UL, statistics.TestedCommands,
            "the live total counts commands that were actually enabled before culling");
        c.Eq(6UL, statistics.CulledCommands,
            "the live total counts the exact commands zeroed by compute");
        c.Eq(750UL, statistics.CulledIndices,
            "geometry weighting follows the index count in those exact commands");
        string report = statistics.Describe("split far live cull");
        c.True(report.Contains("60.0%", StringComparison.Ordinal),
            "the report exposes actual command suppression as a percentage");
        c.True(report.Contains("75.0%", StringComparison.Ordinal),
            "and separately exposes geometry-weighted suppression");
        c.True(report.Contains("2-4k", StringComparison.Ordinal),
            "the ridge result remains attributable by distance");
        c.True(report.Contains("30.0% background", StringComparison.Ordinal),
            "each distance band exposes how often sky overlap defeated rejection");
        statistics.Reset();
        c.Eq(0L, statistics.Samples,
            "a preset change starts a clean live-command interval");
    }
    static void TwoSameFrameBucketsStayIndependent(Check c)
    {
        var backend = new FakeDrawBackend();
        using var drawer = new LodGpuIndirectDrawer(backend, _ => { });
        LodGpuIndirectBuilder builder = BuiltList(out LodGpuGeometryMirror mirror);

        c.True(drawer.Draw(builder, Cull()), "the near bucket draws against the first picture");
        backend.Calls.Add("mid-depth-picture");
        c.True(drawer.Draw(builder, Cull()), "the far bucket draws against the fresh picture");

        int firstUpload = backend.Calls.IndexOf("upload");
        int firstDraw = backend.Calls.IndexOf("draw");
        int picture = backend.Calls.IndexOf("mid-depth-picture");
        int secondUpload = backend.Calls.IndexOf("upload", firstUpload + 1);
        int secondCull = backend.Calls.IndexOf("cull-begin", picture + 1);
        int secondDraw = backend.Calls.IndexOf("draw", picture + 1);
        c.True(firstUpload >= 0 && firstDraw > firstUpload,
            "the near commands upload and draw before the second picture");
        c.True(picture > firstDraw, "the second picture follows the complete near draw");
        c.True(secondUpload > picture && secondCull > secondUpload && secondDraw > secondCull,
            "the far commands upload, cull, and draw only after the second picture");

        backend.Calls.Clear();
        builder.Begin();
        builder.End(mirror.VertexArena, mirror.IndexArena);
        c.False(drawer.Draw(builder, Cull()), "an empty far bucket draws nothing");
        c.False(drawer.Culled, "and cannot inherit the near bucket's cull report");
        c.Eq(0, drawer.LastBatches, "and cannot inherit its batch count");
        c.Eq(0, drawer.LastCommands, "or its command count");
        mirror.Dispose();
    }

    /// <summary>
    /// Phase 5 rests entirely on this: the cull boxes must be indexed the same way the driver
    /// indexes commands.
    ///
    /// The walk hands sections over front to back, but the builder regroups them into page
    /// buckets, so slot N of the command list is usually NOT the Nth section added. Anything
    /// that culls by zeroing a command slot reads box N to decide command N - so if the two
    /// orders ever come apart, the renderer hides terrain based on a completely different
    /// section's position. That failure draws the world correctly most of the time and
    /// deletes scattered terrain the rest of it, which is close to undiagnosable from a
    /// screenshot. It is cheap to pin here and expensive to find anywhere else.
    /// </summary>
    static void CullBoxesFollowCommandOrder(Check c)
    {
        var arenaBackend = new FakeIndirectBackend();
        using var mirror = new LodGpuGeometryMirror(arenaBackend, 64L * 1024 * 1024);
        var builder = new LodGpuIndirectBuilder();

        // Deliberately interleaved so the bucketing cannot coincide with insertion order:
        // two page groups, alternating, which is exactly the case that reorders.
        long[] keys =
        {
            LodWorld.SectionKey(0, 0, 0),
            LodWorld.SectionKey(0, 64, 64),
            LodWorld.SectionKey(0, 1, 0),
            LodWorld.SectionKey(0, 65, 64),
            LodWorld.SectionKey(0, 2, 0),
        };
        foreach (long key in keys) mirror.Mirror(Publication(key));

        builder.Begin();
        foreach (long key in keys)
        {
            mirror.TryGet(key, out LodGpuGeometryMirror.MirroredSection section);
            builder.Add(section, Facts(key));
        }
        builder.End(mirror.VertexArena, mirror.IndexArena);

        c.Eq(keys.Length, builder.CommandCount, "every section became a command");
        c.Eq(builder.CommandCount * LodGpuCullBox.StrideBytes, builder.Boxes.Length,
            "there is exactly one cull box per command");

        // The record buffer is already known to be in command order - the base instance of
        // command N is N - so it is the reference the boxes are checked against rather than
        // the insertion order, which is the thing under suspicion.
        ReadOnlySpan<byte> boxes = builder.Boxes;
        ReadOnlySpan<byte> records = builder.Records;

        for (int slot = 0; slot < builder.CommandCount; slot++)
        {
            float recordOriginX = LodGpuSectionRecord.ReadFloat(
                records, slot, LodGpuSectionRecord.OriginOffset);
            float recordOriginZ = LodGpuSectionRecord.ReadFloat(
                records, slot, LodGpuSectionRecord.OriginOffset + 8);
            float recordSize = LodGpuSectionRecord.ReadFloat(
                records, slot, LodGpuSectionRecord.SectionSizeOffset);

            float boxMinX = LodGpuCullBox.ReadFloat(boxes, slot, LodGpuCullBox.MinOffset);
            float boxMinZ = LodGpuCullBox.ReadFloat(boxes, slot, LodGpuCullBox.MinOffset + 8);
            float boxMaxX = LodGpuCullBox.ReadFloat(boxes, slot, LodGpuCullBox.MaxOffset);
            float boxMaxZ = LodGpuCullBox.ReadFloat(boxes, slot, LodGpuCullBox.MaxOffset + 8);

            c.Eq(recordOriginX, boxMinX, $"slot {slot}: the box starts where its record does in x");
            c.Eq(recordOriginZ, boxMinZ, $"slot {slot}: the box starts where its record does in z");
            c.Eq(recordOriginX + recordSize, boxMaxX, $"slot {slot}: the box spans the footprint in x");
            c.Eq(recordOriginZ + recordSize, boxMaxZ, $"slot {slot}: the box spans the footprint in z");
            LodGpuCullIdentity identity = builder.Identities[slot];
            c.Eq(-1, identity.ClusterCell,
                $"slot {slot}: a whole-section command has the whole-section identity");
            c.Eq(recordOriginX, Facts(identity.SectionKey).OriginRelX,
                $"slot {slot}: its stable identity follows the regrouped command order");

            // And the vertical extent is the mesh's own, not the bedrock-to-sky fallback the
            // record's origin carries. Phase 3b exists precisely so this is the tighter one.
            float boxMinY = LodGpuCullBox.ReadFloat(boxes, slot, LodGpuCullBox.MinOffset + 4);
            float boxMaxY = LodGpuCullBox.ReadFloat(boxes, slot, LodGpuCullBox.MaxOffset + 4);
            c.True(boxMaxY > boxMinY, $"slot {slot}: the box has a real vertical extent");
            c.Eq(30f, boxMaxY - boxMinY, $"slot {slot}: it is the span the section reported");
        }

        // The bucketing really did reorder, or this check proved nothing at all.
        float firstSlotX = LodGpuCullBox.ReadFloat(boxes, 1, LodGpuCullBox.MinOffset);
        c.True(builder.Batches.Count > 1, "the fixture produced more than one page group");
        c.True(Math.Abs(firstSlotX - Facts(keys[1]).OriginRelX) > 0.5f,
            "and slot 1 is not the second section added, so command order really did differ from insertion order");
    }

    /// <summary>
    /// The drawer turns the built command list into GL calls. What matters is that every
    /// batch is issued exactly once, against the page pair it named, at the byte offset of
    /// its own first command - an offset in commands rather than bytes would silently draw
    /// the wrong geometry - and that the state the pass disturbs is put back afterwards.
    /// </summary>
    static void DrawerIssuesEveryBatch(Check c)
    {
        var arenaBackend = new FakeIndirectBackend();
        using var mirror = new LodGpuGeometryMirror(arenaBackend, 64L * 1024 * 1024);
        var builder = new LodGpuIndirectBuilder();

        long near = LodWorld.SectionKey(0, 0, 0);
        long beside = LodWorld.SectionKey(0, 1, 0);
        long far = LodWorld.SectionKey(0, 64, 64);
        foreach (long key in new[] { near, beside, far }) mirror.Mirror(Publication(key));

        builder.Begin();
        foreach (long key in new[] { near, beside, far })
        {
            mirror.TryGet(key, out LodGpuGeometryMirror.MirroredSection section);
            builder.Add(section, Facts(key));
        }
        builder.End(mirror.VertexArena, mirror.IndexArena);

        var drawBackend = new FakeDrawBackend();
        var warnings = new List<string>();
        using var drawer = new LodGpuIndirectDrawer(drawBackend, warnings.Add);

        c.True(drawer.Draw(builder), "a complete command list draws");
        c.SeqEq(new[] { "create", "upload", "begin", "draw", "draw", "end" }, drawBackend.Calls,
            "the pass creates once, uploads this frame, then draws inside one begin/end");
        c.Eq(2, drawBackend.Batches.Count, "both batches are issued");
        c.Eq(builder.Batches[0].VertexPage, drawBackend.Batches[0].VertexPage,
            "the first batch binds the page set it named");
        c.Eq(builder.Batches[1].FirstCommand, drawBackend.Batches[1].FirstCommand,
            "the second batch starts at its own first command, not at the buffer start");
        c.Eq(builder.CommandCount * LodGpuIndirectCommand.StrideBytes,
            drawBackend.UploadedCommandBytes,
            "every command reaches the GPU, and no more than that");
        c.Eq(builder.CommandCount * LodGpuSectionRecord.StrideBytes,
            drawBackend.UploadedRecordBytes,
            "the record buffer is uploaded in step with the commands");
        c.Eq(2, drawer.LastBatches, "the frame reports what it issued");
        c.Eq(3, drawer.LastCommands, "and how many sections that covered");
        c.SeqEq(Array.Empty<string>(), warnings, "a clean pass says nothing");

        // A second frame reuses the objects rather than recreating them.
        drawBackend.Calls.Clear();
        c.True(drawer.Draw(builder), "the next frame draws too");
        c.False(drawBackend.Calls.Contains("create"),
            "the vertex array and buffers are created once, not per frame");

        // An empty list is not a failure: there is simply nothing to draw.
        builder.Begin();
        builder.End(mirror.VertexArena, mirror.IndexArena);
        drawBackend.Calls.Clear();
        c.False(drawer.Draw(builder), "an empty command list draws nothing");
        c.SeqEq(Array.Empty<string>(), drawBackend.Calls, "and issues no GL calls at all");
        c.False(drawer.Failed, "which is not a failure");
    }

    /// <summary>
    /// A driver that refuses a multi-draw must cost the fast path and nothing else. The
    /// drawer gives up for the session rather than retrying every frame, says so once, and
    /// still restores the state it captured - the engine's own renderer runs next.
    /// </summary>
    static void DrawerStopsAfterAFailure(Check c)
    {
        var arenaBackend = new FakeIndirectBackend();
        using var mirror = new LodGpuGeometryMirror(arenaBackend, 64L * 1024 * 1024);
        var builder = new LodGpuIndirectBuilder();
        builder.Begin();
        foreach (long key in new[] { LodWorld.SectionKey(0, 0, 0), LodWorld.SectionKey(0, 64, 64) })
        {
            mirror.Mirror(Publication(key));
            mirror.TryGet(key, out LodGpuGeometryMirror.MirroredSection section);
            builder.Add(section, Facts(key));
        }
        builder.End(mirror.VertexArena, mirror.IndexArena);

        var drawBackend = new FakeDrawBackend { FailOnBatch = 1 };
        var warnings = new List<string>();
        using var drawer = new LodGpuIndirectDrawer(drawBackend, warnings.Add);

        c.False(drawer.Draw(builder), "a pass that could not issue every batch is not a draw");
        c.True(drawer.Failed, "the drawer disables itself rather than retrying every frame");
        c.False(drawer.Ready, "so the renderer stops choosing it");
        c.True(drawBackend.Calls.Contains("end"),
            "the captured state is restored even though a batch failed");
        c.Eq(1, warnings.Count, "the failure is reported once");

        drawBackend.Calls.Clear();
        c.False(drawer.Draw(builder), "a failed drawer does not try again");
        c.SeqEq(Array.Empty<string>(), drawBackend.Calls, "and issues nothing further");
        c.Eq(1, warnings.Count, "and does not repeat itself every frame");
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
        c.False(builder.Add(default, Facts(near)),
            "a section with no arena geometry reports that it needs fallback");
        builder.End(mirror.VertexArena, mirror.IndexArena);
        c.Eq(0, builder.CommandCount, "a section with no arena span produces no command");
        c.Eq(1, builder.CandidatesDropped, "the dropped candidate is counted");
        c.Eq(0, builder.Batches.Count, "no batch is emitted for nothing");

        // Coverage: a batch count over half the drawn terrain is not a draw-call reduction,
        // and the builder has to be able to say so.
        builder.Begin();
        mirror.TryGet(near, out LodGpuGeometryMirror.MirroredSection held);
        c.True(builder.Add(held, Facts(near)),
            "a live arena section reports that it was recorded");
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

    static void PackedCommandLayout(Check c)
    {
        c.Eq(LodGpuIndirectCommand.StrideBytes, LodGpuPackedIndirectCommand.StrideBytes,
            "packed and indexed commands share the cull shader's five-word slot");
        var bytes = new byte[LodGpuPackedIndirectCommand.StrideBytes];
        LodGpuPackedIndirectCommand.Encode(bytes, 9, true, 17, 4);
        c.Eq(54u, LodGpuIndirectCommand.ReadUInt(bytes, 0,
                LodGpuPackedIndirectCommand.CountOffset),
            "one pulled quad expands to six array vertices");
        c.Eq(1u, LodGpuIndirectCommand.ReadUInt(bytes, 0,
                LodGpuPackedIndirectCommand.InstanceCountOffset),
            "a visible packed section draws one instance");
        c.Eq(0u, LodGpuIndirectCommand.ReadUInt(bytes, 0,
                LodGpuPackedIndirectCommand.FirstIndexOffset),
            "every packed command reuses the index pattern from its beginning");
        c.Eq(68, LodGpuIndirectCommand.ReadInt(bytes, 0,
                LodGpuPackedIndirectCommand.BaseVertexOffset),
            "base vertex selects four unique pulled corners per arena quad");
        c.Eq(4u, LodGpuIndirectCommand.ReadUInt(bytes, 0,
                LodGpuPackedIndirectCommand.BaseInstanceOffset),
            "base instance still selects the section record");

        LodGpuPackedIndirectCommand.Encode(bytes, 9, false, 17, 4);
        c.Eq(0u, LodGpuIndirectCommand.ReadUInt(bytes, 0,
                LodGpuPackedIndirectCommand.InstanceCountOffset),
            "the same cull write suppresses packed and expanded commands");

        var pattern = new uint[2 * LodPackedQuadFormat.IndicesPerQuad];
        LodGpuPackedIndexPattern.Fill(pattern, 2);
        c.SeqEq(new uint[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 }, pattern,
            "one reusable pattern preserves both triangles and advances four corners per quad");
    }

    static void PackedBatching(Check c)
    {
        var backend = new FakeIndirectBackend();
        using var mirror = new LodGpuGeometryMirror(backend, 64L * 1024 * 1024);
        var builder = new LodGpuIndirectBuilder(packed: true);
        long a = LodWorld.SectionKey(0, 0, 0);
        long b = LodWorld.SectionKey(0, 1, 0);
        mirror.Mirror(Publication(a, quads: 3));
        mirror.Mirror(Publication(b, quads: 5));

        builder.Begin();
        mirror.TryGet(a, out LodGpuGeometryMirror.MirroredSection first);
        mirror.TryGet(b, out LodGpuGeometryMirror.MirroredSection second);
        builder.Add(first, Facts(a));
        builder.Add(second, Facts(b));
        builder.End(mirror);

        c.True(builder.Packed, "the builder names the geometry representation it encoded");
        c.Eq(2, builder.CommandCount, "packed geometry retains one command per section");
        c.Eq(1, builder.Batches.Count, "packed sections in one page set still batch together");
        c.True(builder.Batches[0].VertexPage != 0, "the batch names its packed page");
        c.Eq(0, builder.Batches[0].IndexPage, "packed drawing binds no index page");
        c.Eq(18u, LodGpuIndirectCommand.ReadUInt(builder.Commands, 0,
                LodGpuPackedIndirectCommand.CountOffset),
            "the first command expands all three quads");
        c.Eq(30u, LodGpuIndirectCommand.ReadUInt(builder.Commands, 1,
                LodGpuPackedIndirectCommand.CountOffset),
            "the second command expands all five quads");
        c.Eq(0u, LodGpuIndirectCommand.ReadUInt(builder.Commands, 0,
                LodGpuPackedIndirectCommand.FirstIndexOffset),
            "the command starts at the reusable index pattern");
        c.Eq((int)(first.FirstPackedQuad * LodPackedQuadFormat.PulledVerticesPerQuad),
            LodGpuIndirectCommand.ReadInt(builder.Commands, 0,
                LodGpuPackedIndirectCommand.BaseVertexOffset),
            "the command rebases virtual corners onto its packed arena range");
        c.Eq(1u, LodGpuIndirectCommand.ReadUInt(builder.Commands, 1,
                LodGpuPackedIndirectCommand.BaseInstanceOffset),
            "the second packed command selects the second section record");
        c.Eq(builder.CommandCount * LodGpuCullBox.StrideBytes, builder.Boxes.Length,
            "packed commands preserve the parallel cull-box layout");

        var clustered = new LodGpuIndirectBuilder(packed: true, clustered: true);
        clustered.Begin();
        clustered.Add(first, Facts(a));
        clustered.Add(second, Facts(b));
        clustered.End(mirror);
        c.Eq(clustered.CommandCount, clustered.Identities.Length,
            "cluster commands retain one stable identity per regrouped command slot");
        c.Eq(a, clustered.Identities[0].SectionKey,
            "the first cluster identity names its source section");
        c.Eq(0, clustered.Identities[0].ClusterCell,
            "and carries the packed cluster's stable 4x4 cell number");

        long expandedOnly = LodWorld.SectionKey(0, 2, 0);
        mirror.Mirror(Publication(expandedOnly, quads: 2, includePacked: false));
        mirror.TryGet(expandedOnly, out LodGpuGeometryMirror.MirroredSection expandedOnlySection);
        builder.Begin();
        c.False(builder.Add(expandedOnlySection, Facts(expandedOnly)),
            "a section whose optional packed publication was refused requests fallback");
        c.Eq(1, builder.CandidatesDropped,
            "the missing packed representation remains visible in telemetry");
        var expandedBuilder = new LodGpuIndirectBuilder();
        expandedBuilder.Begin();
        c.True(expandedBuilder.Add(expandedOnlySection, Facts(expandedOnly)),
            "the same section remains available to expanded batching");
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

    static LodRenderPublication Publication(long key, int quads = 1, bool includePacked = true)
    {
        var xyz = new float[quads * 4 * 3];
        var rgba = new byte[quads * 4 * 4];
        var indices = new int[quads * 6];
        for (int i = 0; i < indices.Length; i++) indices[i] = i % (quads * 4);
        var packedWords = new uint[quads * LodPackedQuadFormat.WordsPerQuad];
        for (int q = 0; q < quads; q++)
        {
            LodPackedQuadFormat.Encode(
                packedWords.AsSpan(q * LodPackedQuadFormat.WordsPerQuad),
                LodPackedFace.Top, 0, 1, q, q, 0, 1, q, (byte)(q & 63));
        }
        return new LodRenderPublication(
            new LodRenderResourceIdentity(1, key, key, key * 2, 0),
            null, null,
            quads * 4, quads * 6, 0, 0, 0,
            new LodRenderGeometry(
                xyz, rgba, indices,
                includePacked ? packedWords : null,
                includePacked ? quads : 0,
                includePacked ? packedWords : null,
                includePacked
                    ? [new LodPackedCluster(
                        Cell: 0, FirstQuad: 0, QuadCount: quads,
                        MinX: 0, MinY: 0, MinZ: 0,
                        MaxX: quads, MaxY: 1, MaxZ: quads)]
                    : null));
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
            0,
            // A span distinct per key, so a box read from the wrong slot cannot coincide
            // with the right answer.
            BoxMinY: -70f + LodWorld.KeySx(key),
            BoxMaxY: -70f + LodWorld.KeySx(key) + 30f);
    }

    /// <summary>
    /// Records the calls a pass makes instead of issuing them. Everything the real backend
    /// does is a GL call with no return value, so the only way to hold its behaviour is to
    /// write down what it was asked to do and in what order.
    /// </summary>
    sealed class FakeDrawBackend : ILodGpuDrawBackend
    {
        public readonly List<string> Calls = new();
        public readonly List<LodGpuDrawBatch> Batches = new();
        public int UploadedCommandBytes;
        public int UploadedRecordBytes;

        /// <summary>Zero-based index of a batch to refuse, or -1 for none.</summary>
        public int FailOnBatch { get; init; } = -1;

        public bool Create()
        {
            Calls.Add("create");
            return true;
        }

        public bool UploadFrame(ReadOnlySpan<byte> commands, ReadOnlySpan<byte> records)
        {
            Calls.Add("upload");
            UploadedCommandBytes = commands.Length;
            UploadedRecordBytes = records.Length;
            return true;
        }

        public int UploadedBoxBytes;

        /// <summary>Refuses to bind the cull, as a driver that cannot would.</summary>
        public bool RefuseCull { get; init; }

        public bool BeginCull(ReadOnlySpan<byte> boxes)
        {
            Calls.Add("cull-begin");
            UploadedBoxBytes = boxes.Length;
            return !RefuseCull;
        }

        public bool EndCull()
        {
            Calls.Add("cull-end");
            return true;
        }

        public bool BeginDraw()
        {
            Calls.Add("begin");
            return true;
        }

        public bool DrawBatch(int vertexPage, int indexPage, int firstCommand, int commandCount)
        {
            Calls.Add("draw");
            if (Batches.Count == FailOnBatch) return false;
            Batches.Add(new LodGpuDrawBatch(0, vertexPage, indexPage, firstCommand, commandCount));
            return true;
        }

        public bool EndDraw()
        {
            Calls.Add("end");
            return true;
        }

        public void Dispose() => Calls.Add("dispose");
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
    /// <summary>
    /// The cull has exactly one correct place in the frame: after the commands are on the
    /// card and before anything draws from them.
    ///
    /// Earlier, and it would cull commands that are about to be overwritten by the upload.
    /// Later, and the driver may already have fetched draw parameters that the cull then
    /// changes underneath it. Neither shows up as a crash - both show up as culling that
    /// works on some frames and not others, which is the hardest kind of fault to attribute.
    /// </summary>
    static void CullRunsBetweenUploadAndDraw(Check c)
    {
        var backend = new FakeDrawBackend();
        var warnings = new List<string>();
        var pass = new FakeCullDispatch();
        using var drawer = new LodGpuIndirectDrawer(backend, warnings.Add);

        drawer.Draw(BuiltList(out LodGpuGeometryMirror mirror), Cull(pass));
        mirror.Dispose();

        int upload = backend.Calls.IndexOf("upload");
        int cullBegin = backend.Calls.IndexOf("cull-begin");
        int cullEnd = backend.Calls.IndexOf("cull-end");
        int drawBegin = backend.Calls.IndexOf("begin");

        c.True(upload >= 0, "the frame's commands were uploaded");
        c.True(cullBegin > upload, "the cull binds only after the commands are on the card");
        c.True(cullEnd > cullBegin, "the cull is closed, which is where its barrier lives");
        c.True(drawBegin > cullEnd, "and nothing is drawn until after that barrier");
        c.Eq(LodHzbProjection.OcclusionDepthBias,
            pass.LastDepthBias,
            "both buckets receive the one established safety margin");
        c.Eq(LodGpuCullBucket.SplitFar, pass.LastBucket,
            "the real dispatch attributes its counters to the far split bucket");
        c.Eq(pass.LastCount, pass.LastIdentityCount,
            "the optional capture receives one stable identity per command slot");
        c.Eq(0, warnings.Count, "a clean cull warns about nothing");
    }

    /// <summary>
    /// Every way the cull can decline has to leave a complete picture, because the commands on
    /// the card are the ones the CPU approved and drawing all of them is the pre-Phase-5
    /// behaviour. A cull that fails must cost performance and nothing else.
    /// </summary>
    static void ADeclinedCullStillDrawsEverything(Check c)
    {
        // No request at all: the switch is off, or there is no pyramid yet.
        var backend = new FakeDrawBackend();
        using (var drawer = new LodGpuIndirectDrawer(backend, _ => { }))
        {
            c.True(drawer.Draw(BuiltList(out LodGpuGeometryMirror mirror), default),
                "a frame with no cull request still draws");
            mirror.Dispose();
            c.False(backend.Calls.Contains("cull-begin"), "and never begins a cull");
            c.False(drawer.Culled, "and reports that nothing was culled");
            c.True(backend.Batches.Count > 0, "every batch still went out");
        }

        // A backend that refuses to bind the cull. The draw must proceed regardless.
        var refusing = new FakeDrawBackend { RefuseCull = true };
        using (var drawer = new LodGpuIndirectDrawer(refusing, _ => { }))
        {
            c.True(drawer.Draw(BuiltList(out LodGpuGeometryMirror mirror), Cull()),
                "a refused cull does not stop the frame drawing");
            mirror.Dispose();
            c.False(drawer.Culled, "the frame reports itself unculled");
            c.True(refusing.Batches.Count > 0, "and every batch still went out");
        }

        // A cull request with nothing usable in it is declined without touching the backend.
        c.False(new LodGpuCullRequest(null, new float[16], 1, 100, 100, 4,
                LodHzbProjection.OcclusionDepthBias, LodGpuCullBucket.General).Wanted,
            "a request with no cull pass is not wanted");
        c.False(new LodGpuCullRequest(null, null, 1, 100, 100, 4,
                LodHzbProjection.OcclusionDepthBias, LodGpuCullBucket.General).Wanted,
            "nor one with no matrix");
        c.False(new LodGpuCullRequest(null, new float[16], 0, 100, 100, 4,
                LodHzbProjection.OcclusionDepthBias, LodGpuCullBucket.General).Wanted,
            "nor one with no pyramid texture");
        c.False(new LodGpuCullRequest(null, new float[16], 1, 100, 100, 0,
                LodHzbProjection.OcclusionDepthBias, LodGpuCullBucket.General).Wanted,
            "nor one with no pyramid levels");
    }

    /// <summary>A small built command list, and the mirror that must outlive it.</summary>
    static LodGpuIndirectBuilder BuiltList(out LodGpuGeometryMirror mirror)
    {
        var arenaBackend = new FakeIndirectBackend();
        mirror = new LodGpuGeometryMirror(arenaBackend, 64L * 1024 * 1024);
        var builder = new LodGpuIndirectBuilder();

        long[] keys = { LodWorld.SectionKey(0, 0, 0), LodWorld.SectionKey(0, 1, 0) };
        foreach (long key in keys) mirror.Mirror(Publication(key));

        builder.Begin();
        foreach (long key in keys)
        {
            mirror.TryGet(key, out LodGpuGeometryMirror.MirroredSection section);
            builder.Add(section, Facts(key));
        }
        builder.End(mirror.VertexArena, mirror.IndexArena);
        return builder;
    }

    /// <summary>
    /// A cull request backed by a fake dispatch, so the drawer takes the real culling path
    /// without any GL. The ordering is then observable through the backend's call log.
    /// </summary>
    static LodGpuCullRequest Cull(FakeCullDispatch? pass = null) =>
        new(pass ?? new FakeCullDispatch(), new float[16], HzbTexture: 1, ScreenW, ScreenH, Levels: 4,
            LodHzbProjection.OcclusionDepthBias,
            LodGpuCullBucket.SplitFar);

    const int ScreenW = 1920;
    const int ScreenH = 1080;

    /// <summary>Stands in for the compute program: records what it was asked to cull.</summary>
    sealed class FakeCullDispatch : ILodGpuCullDispatch
    {
        public bool Available { get; init; } = true;
        public bool Refuse { get; init; }
        public int Dispatches;
        public int LastCount;
        public int LastIdentityCount;
        public float LastDepthBias;
        public LodGpuCullBucket LastBucket;

        public bool Dispatch(float[] viewProjection, int hzbTexture,
            int screenWidth, int screenHeight, int levels, int count,
            float occlusionDepthBias,
            LodGpuCullBucket bucket,
            ReadOnlySpan<LodGpuCullIdentity> identities)
        {
            Dispatches++;
            LastCount = count;
            LastIdentityCount = identities.Length;
            LastDepthBias = occlusionDepthBias;
            LastBucket = bucket;
            return !Refuse;
        }
    }
}
