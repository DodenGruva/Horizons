namespace VintageHorizons.Checks;

/// <summary>
/// Phase 2 regional arenas. Every gate the plan sets for this phase - byte-exact content,
/// allocate-new before retire-old, no stale range reuse, bounded reclamation, a real memory
/// ceiling, and convergence back to nothing - is decided here, without a GL context.
/// </summary>
public static class GpuArenaChecks
{
    public static void Run(Check c)
    {
        GeometryEncoding(c);
        ArenaAllocation(c);
        FencedRetirement(c);
        CoalescingAndConvergence(c);
        RegionAssignment(c);
        MirrorContent(c);
        PackedMirrorContent(c);
        SelectedFormatRetention(c);
        MirrorReplacement(c);
        MirrorCeiling(c);
        MirrorStress(c);
        ArenaPolicy(c);
        CeilingFollowsDrawDistance(c);
    }

    // ---- Geometry format ----

    static void GeometryEncoding(Check c)
    {
        c.Eq(16, LodGpuGeometryFormat.VertexStrideBytes,
            "the arena keeps the renderer's existing 16-byte expanded vertex");
        c.Eq(4, LodGpuGeometryFormat.IndexStrideBytes,
            "the arena keeps 32-bit indices");
        c.Eq(64L, LodGpuGeometryFormat.VertexBytes(4),
            "one greedy quad's expanded vertices occupy 64 bytes");
        c.Eq(24L, LodGpuGeometryFormat.IndexBytes(6),
            "one greedy quad's indices occupy 24 bytes");

        (float[] xyz, byte[] rgba, int[] indices) = Quad(seed: 3);
        var vertexBytes = new byte[LodGpuGeometryFormat.VertexBytes(4)];
        LodGpuGeometryFormat.EncodeVertices(xyz, rgba, 4, vertexBytes);

        bool exact = true;
        for (int i = 0; i < 4; i++)
        {
            LodGpuGeometryFormat.DecodeVertex(
                vertexBytes, i, out float x, out float y, out float z, out uint color);
            uint expected = (uint)(rgba[i * 4]
                | rgba[i * 4 + 1] << 8
                | rgba[i * 4 + 2] << 16
                | rgba[i * 4 + 3] << 24);
            exact &= x == xyz[i * 3] && y == xyz[i * 3 + 1] && z == xyz[i * 3 + 2]
                && color == expected;
        }
        c.True(exact, "interleaving preserves every position and colour byte exactly");

        var indexBytes = new byte[LodGpuGeometryFormat.IndexBytes(6)];
        LodGpuGeometryFormat.EncodeIndices(indices, 6, indexBytes);
        bool indicesExact = true;
        for (int i = 0; i < 6; i++)
            indicesExact &= LodGpuGeometryFormat.DecodeIndex(indexBytes, i) == indices[i];
        c.True(indicesExact,
            "indices are stored section-local and unchanged, ready to rebase by base vertex");

        // A mesher array is allowed to be longer than the live count; only the live part
        // may be copied, or the arena would store pooled slack as geometry.
        var oversized = new float[64];
        Array.Copy(xyz, oversized, xyz.Length);
        var partial = new byte[LodGpuGeometryFormat.VertexBytes(2)];
        c.NoThrow(() => LodGpuGeometryFormat.EncodeVertices(oversized, rgba, 2, partial),
            "an oversized source array encodes exactly its live vertex count");
        c.Throws<ArgumentException>(
            () => LodGpuGeometryFormat.EncodeVertices(xyz, rgba, 4, partial),
            "a destination too small for the live count is rejected rather than truncated");
        c.Throws<ArgumentException>(
            () => LodGpuGeometryFormat.EncodeVertices(oversized, rgba, 8, new byte[128]),
            "a colour array shorter than the live count is rejected");
    }

    // ---- Allocator ----

    static void ArenaAllocation(Check c)
    {
        var backend = new FakeArenaBackend();
        using var arena = new LodGpuArena(
            backend, LodGpuArenaKind.Vertex, new LodGpuArenaLimits(1024, 3072, 4));

        c.True(arena.TryAllocate(1, 100, out LodGpuArenaRange first),
            "a first allocation creates the group's page");
        c.Eq(112L, first.Length,
            "an allocation is rounded up to the vertex stride");
        c.Eq(0L, first.Offset, "the first allocation starts at the page origin");
        c.Eq(1024L, arena.CommittedBytes, "one page is committed");
        c.Eq(112L, arena.LiveBytes, "live bytes count the aligned allocation");

        c.True(arena.TryAllocate(1, 100, out LodGpuArenaRange second),
            "a second allocation shares the group's page");
        c.Eq(112L, second.Offset, "allocations pack from the front of the free range");
        c.Eq(1, arena.PageCount, "a page with room does not create another");
        c.True(second.Generation > first.Generation,
            "every allocation carries a strictly increasing generation");

        c.True(arena.TryAllocate(2, 100, out LodGpuArenaRange other),
            "a different group allocates its own page");
        c.Eq(2, arena.PageCount, "groups never share a page");
        c.Eq(0L, other.Offset, "the second group starts at its own page origin");

        c.False(arena.TryAllocate(3, 2048, out _),
            "an allocation larger than a page is refused, not silently split");
        c.Eq(1, arena.OversizeRejections, "an oversized request is reported as such");

        c.True(arena.TryAllocate(3, 64, out _), "the ceiling admits the last page");
        c.False(arena.TryAllocate(4, 64, out LodGpuArenaRange refused),
            "the memory ceiling refuses a page rather than growing without limit");
        c.Eq(1, arena.CeilingRejections, "a ceiling refusal is reported as such");
        c.False(refused.IsLive, "a refused allocation yields no usable span");
        c.Eq(3072L, arena.CommittedBytes, "committed bytes stop exactly at the ceiling");

        backend.RefuseNextPage = true;
        using var refusing = new LodGpuArena(
            backend, LodGpuArenaKind.Index, new LodGpuArenaLimits(1024, 4096, 4));
        c.False(refusing.TryAllocate(1, 64, out _),
            "a driver that refuses a buffer fails the allocation rather than throwing");
        c.Eq(1, refusing.BackendFailures, "a backend refusal is attributed to the backend");
        c.Eq(0L, refusing.CommittedBytes, "a refused page commits nothing");
    }

    // ---- Fenced retirement ----

    static void FencedRetirement(Check c)
    {
        var backend = new FakeArenaBackend();
        using var arena = new LodGpuArena(
            backend, LodGpuArenaKind.Vertex, new LodGpuArenaLimits(1024, 8192, 2));

        arena.TryAllocate(1, 256, out LodGpuArenaRange first);
        arena.Retire(first);
        c.Eq(256L, arena.PendingRetireBytes, "a retired span is pending, not free");
        c.Eq(0L, arena.LiveBytes, "a retired span stops counting as live immediately");
        c.Eq(256L, arena.UsedBytes, "a retired span still occupies its page");

        arena.TryAllocate(1, 256, out LodGpuArenaRange afterRetire);
        c.True(afterRetire.Offset != first.Offset,
            "a span awaiting its fence cannot be handed to another section");

        c.Eq(0, arena.Reclaim(), "an unsignaled fence reclaims nothing and does not wait");
        c.Eq(1, arena.PendingRetireCount, "the unsignaled span stays queued in order");

        backend.SignalAll();
        c.Eq(1, arena.Reclaim(), "a signaled fence returns its span");
        c.Eq(0L, arena.PendingRetireBytes, "reclaimed bytes leave the pending total");
        c.Eq(1, backend.DeletedFences, "a reclaimed span deletes its fence object");

        arena.TryAllocate(1, 256, out LodGpuArenaRange reused);
        c.Eq(first.Offset, reused.Offset,
            "a reclaimed span becomes allocatable again once the GPU has passed it");
        c.True(reused.Generation > afterRetire.Generation,
            "reused bytes carry a new generation, so an old handle cannot match them");

        // Bounded reclamation: four retirements against a budget of two.
        arena.TryAllocate(1, 64, out LodGpuArenaRange a);
        arena.TryAllocate(1, 64, out LodGpuArenaRange b);
        arena.TryAllocate(1, 64, out LodGpuArenaRange d);
        arena.TryAllocate(1, 64, out LodGpuArenaRange e);
        arena.Retire(a);
        arena.Retire(b);
        arena.Retire(d);
        arena.Retire(e);
        backend.SignalAll();
        c.Eq(2, arena.Reclaim(), "reclamation stops at its per-frame budget");
        c.Eq(2, arena.PendingRetireCount, "the remainder waits for the next frame");
        c.Eq(2, arena.Reclaim(), "the next frame reclaims the rest");

        // A context that cannot fence must still converge, and must say so.
        var unfenced = new FakeArenaBackend { FencesUnavailable = true };
        using var fallback = new LodGpuArena(
            unfenced, LodGpuArenaKind.Vertex, new LodGpuArenaLimits(1024, 4096, 8));
        fallback.TryAllocate(1, 128, out LodGpuArenaRange unfencedRange);
        fallback.Retire(unfencedRange);
        c.Eq(1, fallback.FenceUnavailable, "a context that cannot fence is reported");
        for (int i = 0; i < LodGpuArena.FenceUnavailablePasses; i++)
            c.Eq(0, fallback.Reclaim(), "an unfenceable span is held for a fixed delay");
        c.Eq(1, fallback.Reclaim(), "the held span is released after that delay");
    }

    static void CoalescingAndConvergence(Check c)
    {
        var backend = new FakeArenaBackend();
        var arena = new LodGpuArena(
            backend, LodGpuArenaKind.Vertex, new LodGpuArenaLimits(1024, 4096, 16));

        arena.TryAllocate(1, 256, out LodGpuArenaRange a);
        arena.TryAllocate(1, 256, out LodGpuArenaRange b);
        arena.TryAllocate(1, 256, out LodGpuArenaRange d);
        arena.Retire(a);
        arena.Retire(d);
        backend.SignalAll();
        arena.Reclaim();
        c.Eq(768L, arena.FreeBytes, "both reclaimed spans return to the page");
        c.Eq(512L, arena.LargestFreeRange,
            "the reclaimed span against the page tail merges with it; the isolated hole does not");
        c.True(arena.Fragmentation > 0,
            "free bytes split across separate holes report fragmentation");

        arena.Retire(b);
        backend.SignalAll();
        arena.Reclaim();
        c.Eq(0, arena.PageCount, "a page whose last span is reclaimed is deleted");
        c.Eq(0L, arena.CommittedBytes, "committed bytes converge back to nothing");
        c.Eq(0.0, arena.Fragmentation, "an empty arena reports no fragmentation");
        c.Eq(0, backend.Pages.Count, "the backend holds no buffer after convergence");

        // Freeing the same span twice is a stale-reuse bug, not a tolerable no-op. The
        // second allocation keeps the page alive so the duplicate is actually examined.
        arena.TryAllocate(1, 128, out _);
        arena.TryAllocate(1, 128, out LodGpuArenaRange live);
        arena.Retire(live);
        arena.Retire(live);
        backend.SignalAll();
        c.Throws<InvalidOperationException>(() => arena.Reclaim(),
            "releasing a span that is already free is refused rather than double-issued");

        arena.Dispose();
        c.Eq(0, backend.Pages.Count, "disposal leaves no page behind");
    }

    static void RegionAssignment(Check c)
    {
        const int shift = LodGpuGeometryMirror.DefaultRegionShift;
        long a = LodWorld.SectionKey(0, 0, 0);
        long b = LodWorld.SectionKey(0, 7, 7);
        long outside = LodWorld.SectionKey(0, 8, 7);
        long higher = LodWorld.SectionKey(1, 0, 0);

        c.Eq(LodGpuGeometryMirror.RegionId(a, shift), LodGpuGeometryMirror.RegionId(b, shift),
            "sections inside one region tile share a region");
        c.True(LodGpuGeometryMirror.RegionId(a, shift)
            != LodGpuGeometryMirror.RegionId(outside, shift),
            "the section beyond the tile edge belongs to the next region");
        c.True(LodGpuGeometryMirror.RegionId(a, shift)
            != LodGpuGeometryMirror.RegionId(higher, shift),
            "levels never share a region, because their footprints differ");

        // The far corner of the largest world this key packing can express.
        long extreme = LodWorld.SectionKey(0, 0x3FFFFFFF, 0x3FFFFFFF);
        long extremeRegion = LodGpuGeometryMirror.RegionId(extreme, shift);
        c.Eq(0, LodWorld.KeyLevel(extremeRegion),
            "an extreme coordinate keeps its level through region assignment");
        c.Eq(0x3FFFFFFF >> shift, LodWorld.KeySx(extremeRegion),
            "an extreme x coordinate maps to its own region without wrapping");
        c.Eq(0x3FFFFFFF >> shift, LodWorld.KeySz(extremeRegion),
            "an extreme z coordinate maps to its own region without wrapping");
        c.True(LodGpuGeometryMirror.RegionId(extreme, shift)
            != LodGpuGeometryMirror.RegionId(a, shift),
            "the far corner of the world does not collide with its origin");
    }

    // ---- Mirror ----

    static void MirrorContent(Check c)
    {
        var backend = new FakeArenaBackend();
        using var mirror = new LodGpuGeometryMirror(backend, 64L * 1024 * 1024, verify: true);

        long key = LodWorld.SectionKey(0, 5, 9);
        (float[] xyz, byte[] rgba, int[] indices) = Quad(seed: 11);
        c.True(mirror.Mirror(Publication(key, xyz, rgba, indices)),
            "a published opaque section is mirrored into the regional arenas");
        c.Eq(1, mirror.Count, "the mirror holds one section");
        c.Eq(0L, mirror.VerificationFailures,
            "byte-exact readback verification finds no mismatch");

        c.True(mirror.TryGet(key, out LodGpuGeometryMirror.MirroredSection section),
            "the mirrored section is retrievable by key");
        c.Eq(4, section.VertexCount, "the mirror records the live vertex count");
        c.Eq(6, section.IndexCount, "the mirror records the live index count");
        c.Eq(section.Vertices.Offset / 16, section.BaseVertex,
            "base vertex follows from the allocation offset");
        c.Eq(section.Indices.Offset / 4, section.FirstIndex,
            "first index follows from the allocation offset");

        c.True(backend.MatchesVertices(mirror.VertexArena, section, xyz, rgba),
            "arena vertex bytes match the geometry the legacy upload received");
        c.True(backend.MatchesIndices(mirror.IndexArena, section, indices),
            "arena index bytes match the geometry the legacy upload received");

        // A section with water but no opaque geometry is simply not mirrored.
        long empty = LodWorld.SectionKey(0, 6, 9);
        c.False(mirror.Mirror(new LodRenderPublication(
                Identity(empty), null, null, 0, 0, 8, 12, 0, default)),
            "a section with no opaque geometry is not an arena failure");
        c.Eq(1, mirror.Count, "a section with no opaque geometry occupies no arena span");

        mirror.Remove(key);
        c.Eq(0, mirror.Count, "removal drops the section from the mirror");
        c.True(mirror.PendingRetireBytes > 0, "removal retires its spans behind a fence");
        backend.SignalAll();
        mirror.Reclaim();
        c.Eq(0L, mirror.PendingRetireBytes, "the removed spans are reclaimed");
        c.Eq(0L, mirror.LiveBytes, "no live bytes remain");
        c.Eq(0L, mirror.CommittedBytes, "the emptied pages are released");
    }

    static void MirrorReplacement(Check c)
    {
        var backend = new FakeArenaBackend();
        using var mirror = new LodGpuGeometryMirror(backend, 64L * 1024 * 1024);

        long key = LodWorld.SectionKey(0, 1, 1);
        (float[] xyz, byte[] rgba, int[] indices) = Quad(seed: 2);
        mirror.Mirror(Publication(key, xyz, rgba, indices));
        mirror.TryGet(key, out LodGpuGeometryMirror.MirroredSection before);

        (float[] newXyz, byte[] newRgba, int[] newIndices) = Quad(seed: 40);
        c.True(mirror.Mirror(Publication(key, newXyz, newRgba, newIndices)),
            "a remesh replaces the mirrored geometry");
        mirror.TryGet(key, out LodGpuGeometryMirror.MirroredSection after);

        c.Eq(1, mirror.Count, "a replacement does not duplicate the section");
        c.Eq(1L, mirror.Replacements, "the replacement is counted");
        c.True(after.Vertices.Offset != before.Vertices.Offset,
            "the replacement is allocated before the old span is retired");
        c.True(after.Vertices.Generation > before.Vertices.Generation,
            "the replacement carries a newer allocation generation");
        c.True(backend.MatchesVertices(mirror.VertexArena, after, newXyz, newRgba),
            "the mirrored bytes are the new geometry, not the old");
        c.True(mirror.PendingRetireBytes > 0,
            "the superseded span is retired behind a fence rather than overwritten");

        backend.SignalAll();
        mirror.Reclaim();

        // A world change is one unit: records, pages and fences all go together.
        mirror.Mirror(Publication(LodWorld.SectionKey(0, 2, 2), xyz, rgba, indices));
        c.True(mirror.CommittedBytes > 0, "the mirror holds committed pages before a clear");
        mirror.Clear();
        c.Eq(0, mirror.Count, "a world change drops every mirrored record");
        c.Eq(0L, mirror.CommittedBytes, "a world change releases every committed page");
        c.Eq(0L, mirror.PendingRetireBytes, "a world change leaves nothing pending");
        c.Eq(0, backend.Pages.Count, "a world change leaves no buffer alive in the driver");
    }

    static void PackedMirrorContent(Check c)
    {
        var backend = new FakeArenaBackend();
        using var mirror = new LodGpuGeometryMirror(backend, 64L * 1024 * 1024, verify: true);
        long key = LodWorld.SectionKey(3, 4, -2);
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(
            Fixtures.SolidSection(yTop: 40, yBottom: 3), key));
        var publication = new LodRenderPublication(
            Identity(key), null, null,
            mesh.VertexCount, mesh.IndexCount, 0, 0, 0,
            new LodRenderGeometry(mesh.Xyz, mesh.Rgba, mesh.Indices,
                mesh.PackedOpaqueQuads, mesh.PackedOpaqueQuadCount));

        c.True(mirror.Mirror(publication), "an exact packed payload accompanies the expanded mirror");
        c.True(mirror.TryGet(key, out LodGpuGeometryMirror.MirroredSection section),
            "the packed section is published");
        c.True(section.PackedQuads.IsLive, "the packed range is live only after upload succeeds");
        c.Eq(mesh.PackedOpaqueQuadCount, section.PackedQuadCount,
            "the mirror retains the exact packed quad count");
        c.Eq(section.PackedQuads.Offset / LodPackedQuadFormat.StrideBytes,
            section.FirstPackedQuad, "the first pulled quad follows from its byte range");
        c.True(backend.MatchesPacked(mirror.PackedArena, section,
                mesh.PackedOpaqueQuads, mesh.PackedOpaqueQuadCount),
            "packed page bytes match the worker-produced records");
        c.True(mirror.PackedLiveBytes * 2 < mirror.LiveBytes,
            "the packed companion is materially smaller than expanded vertices and indices");

        mirror.Remove(key);
        c.True(mirror.PackedArena.PendingRetireBytes > 0,
            "packed replacement bytes obey the same fenced lifetime");
        backend.SignalAll();
        mirror.Reclaim();
        c.Eq(0L, mirror.PackedLiveBytes, "packed bytes converge after reclamation");
    }

    static void SelectedFormatRetention(Check c)
    {
        c.Eq(LodGpuGeometryRetention.Expanded,
            LodGpuGeometryMirror.SelectRetention(packed: false, clustered: true),
            "packing off selects only expanded regional geometry");
        c.Eq(LodGpuGeometryRetention.Packed,
            LodGpuGeometryMirror.SelectRetention(packed: true, clustered: false),
            "packing without clusters selects only whole-section packed geometry");
        c.Eq(LodGpuGeometryRetention.ClusteredPacked,
            LodGpuGeometryMirror.SelectRetention(packed: true, clustered: true),
            "the default packed cluster path selects only clustered geometry");

        var backend = new FakeArenaBackend();
        using var mirror = new LodGpuGeometryMirror(
            backend, 64L * 1024 * 1024,
            retention: LodGpuGeometryRetention.Packed);
        long key = LodWorld.SectionKey(1, -3, 8);
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(
            Fixtures.SolidSection(yTop: 48, yBottom: 4), key));
        var publication = new LodRenderPublication(
            Identity(key), null, null,
            mesh.VertexCount, mesh.IndexCount, 0, 0, 0,
            new LodRenderGeometry(mesh.Xyz, mesh.Rgba, mesh.Indices,
                mesh.PackedOpaqueQuads, mesh.PackedOpaqueQuadCount));

        c.True(mirror.Mirror(publication), "the selected packed-only representation publishes");
        mirror.TryGet(key, out LodGpuGeometryMirror.MirroredSection section);
        c.False(section.Vertices.IsLive || section.Indices.IsLive,
            "packed selection allocates no expanded regional spans");
        c.True(section.PackedQuads.IsLive,
            "packed selection retains its exact selected span");
        c.False(section.ClusteredPackedQuads.IsLive,
            "packed selection allocates no unused cluster span");
        c.Eq(0L, mirror.LiveBytes, "expanded regional live bytes remain zero");
        c.True(mirror.PackedLiveBytes > 0, "the selected packed bytes are reported");
        c.Eq(0L, mirror.ClusteredPackedLiveBytes,
            "the unselected cluster arena commits nothing");
        c.True(backend.Pages.Keys.All(page => page.Kind == LodGpuArenaKind.PackedQuad),
            "the backend created pages only for the selected representation");
    }

    static void MirrorCeiling(Check c)
    {
        const long ceiling = 16L * 1024 * 1024;
        var backend = new FakeArenaBackend();
        using var mirror = new LodGpuGeometryMirror(backend, ceiling);
        (float[] xyz, byte[] rgba, int[] indices) = Quads(64, seed: 7);

        for (int i = 0; i < 4096; i++)
            mirror.Mirror(Publication(LodWorld.SectionKey(0, i % 64, i / 64), xyz, rgba, indices));

        c.True(mirror.CommittedBytes <= ceiling,
            "dual-path shadow memory stays inside its configured ceiling");
        c.True(mirror.AllocationFailures > 0,
            "sections beyond the ceiling are refused rather than admitted");
        c.True(mirror.SkippedSections > 0,
            "a refused section is counted as skipped, not silently mirrored");
        c.Eq(mirror.Count, mirror.Sections.Count,
            "only the sections that were actually stored remain in the mirror");
        c.Eq(0L, mirror.VerificationFailures,
            "refusing an allocation is not a content failure");

        // A replacement that cannot be allocated must not leave the previous geometry
        // mirrored. The mirror may be a subset of the truth; it may never be a stale copy
        // of it. Deliberately tight pages so the refusal is exact rather than incidental.
        var tight = new FakeArenaBackend();
        using var full = new LodGpuGeometryMirror(
            tight,
            ceilingBytes: 0,
            vertexLimits: new LodGpuArenaLimits(2048, 4096, 8),
            indexLimits: new LodGpuArenaLimits(2048, 4096, 8));
        (float[] bigXyz, byte[] bigRgba, int[] bigIndices) = Quads(24, seed: 8);
        long first = LodWorld.SectionKey(0, 0, 0);
        long second = LodWorld.SectionKey(0, 1, 0);

        c.True(full.Mirror(Publication(first, bigXyz, bigRgba, bigIndices)),
            "the first section fills most of its page");
        c.True(full.Mirror(Publication(second, bigXyz, bigRgba, bigIndices)),
            "the second section takes the last page the ceiling allows");
        c.False(full.Mirror(Publication(first, bigXyz, bigRgba, bigIndices)),
            "a replacement with nowhere to go is refused");
        c.False(full.TryGet(first, out _),
            "the refused section is dropped rather than left holding superseded geometry");
        c.Eq(1, full.Count, "the sections that did fit are untouched by another's refusal");
    }

    /// <summary>
    /// Repeated publication, replacement, removal and world clear against a bounded
    /// ceiling. The invariant under test is the one that matters for a later indirect
    /// draw: no two live sections may ever describe the same bytes.
    /// </summary>
    static void MirrorStress(Check c)
    {
        var backend = new FakeArenaBackend();
        using var mirror = new LodGpuGeometryMirror(backend, 32L * 1024 * 1024);
        var random = new Random(20260821);
        int overlaps = 0;
        int worldClears = 0;

        for (int step = 0; step < 3000; step++)
        {
            long key = LodWorld.SectionKey(
                random.Next(0, 3), random.Next(0, 40), random.Next(0, 40));

            int action = random.Next(0, 10);
            if (action < 7)
            {
                (float[] xyz, byte[] rgba, int[] indices) =
                    Quads(random.Next(1, 6), random.Next());
                mirror.Mirror(Publication(key, xyz, rgba, indices));
            }
            else if (action < 9)
            {
                mirror.Remove(key);
            }
            else
            {
                mirror.Clear();
                worldClears++;
            }

            if (random.Next(0, 3) == 0) backend.SignalAll();
            mirror.Reclaim();
            if (Overlapping(mirror)) overlaps++;
        }

        c.Eq(0, overlaps, "no two live sections ever share arena bytes under streaming churn");
        c.True(worldClears > 0, "the stress run exercised world clears");
        c.True(mirror.CommittedBytes <= 32L * 1024 * 1024,
            "the ceiling holds across replacement, eviction and world-clear churn");

        // Convergence: once activity stops and the GPU catches up, nothing is left.
        mirror.Clear();
        backend.SignalAll();
        for (int i = 0; i < 64; i++) mirror.Reclaim();
        c.Eq(0L, mirror.CommittedBytes, "the arena converges to nothing after activity stops");
        c.Eq(0L, mirror.PendingRetireBytes, "no retirement is stranded after convergence");
        c.Eq(0, backend.Pages.Count, "no driver buffer survives convergence");
    }

    /// <summary>
    /// Vertex and index page numbering are separate: a vertex span and an index span may
    /// carry the same page slot without describing the same bytes, so each arena is
    /// examined on its own.
    /// </summary>
    static bool Overlapping(LodGpuGeometryMirror mirror) =>
        Overlapping(mirror, vertices: true) || Overlapping(mirror, vertices: false);

    static bool Overlapping(LodGpuGeometryMirror mirror, bool vertices)
    {
        var seen = new List<(int Page, long Start, long End)>();
        foreach (LodGpuGeometryMirror.MirroredSection section in mirror.Sections.Values)
        {
            LodGpuArenaRange range = vertices ? section.Vertices : section.Indices;
            foreach ((int page, long start, long end) in seen)
            {
                if (page != range.Page) continue;
                if (range.Offset < end && start < range.Offset + range.Length) return true;
            }
            seen.Add((range.Page, range.Offset, range.Offset + range.Length));
        }
        return false;
    }

    static void ArenaPolicy(Check c)
    {
        c.Eq(LodGpuArenaMode.On, LodGpuArenaPolicy.Parse(null),
            "an unset arena switch keeps the arena on beneath an enabled shadow");
        c.Eq(LodGpuArenaMode.Off, LodGpuArenaPolicy.Parse("off"),
            "the arena can be switched off independently of the renderer shadow");
        c.Eq(LodGpuArenaMode.Verify, LodGpuArenaPolicy.Parse("VERIFY"),
            "content verification is requested case-insensitively");
        c.Eq(LodGpuArenaMode.Invalid, LodGpuArenaPolicy.Parse("yes"),
            "an unknown arena value is invalid and must fail closed");

        c.Eq(LodGpuArenaPolicy.DefaultCeilingBytes, LodGpuArenaPolicy.CeilingBytes(null),
            "an unset ceiling uses the default rather than no limit");
        c.Eq(LodGpuArenaPolicy.DefaultCeilingBytes, LodGpuArenaPolicy.CeilingBytes("not a number"),
            "an unreadable ceiling uses the default rather than no limit");
        c.Eq(LodGpuArenaPolicy.MinimumCeilingBytes, LodGpuArenaPolicy.CeilingBytes("1"),
            "a tiny ceiling is clamped to something an arena can work in");
        c.Eq(LodGpuArenaPolicy.MaximumCeilingBytes, LodGpuArenaPolicy.CeilingBytes("999999"),
            "an absurd ceiling is clamped rather than trusted");
        c.Eq(512L * 1024 * 1024, LodGpuArenaPolicy.CeilingBytes("512"),
            "an ordinary ceiling is taken in mebibytes");

        // Pages are allocated in pairs, so the two ceilings must afford the SAME number of
        // pages. Splitting by the byte ratio of the geometry instead let the index arena
        // refuse new page sets while still 40% empty, which capped the whole mirror.
        long ceiling = LodGpuArenaPolicy.CeilingBytes("256");
        LodGpuArenaLimits vertex = LodGpuArenaPolicy.VertexLimits(ceiling);
        LodGpuArenaLimits index = LodGpuArenaPolicy.IndexLimits(ceiling);
        c.Eq(vertex.CeilingBytes / vertex.PageBytes, index.CeilingBytes / index.PageBytes,
            "both arenas afford exactly the same number of pages");
        c.Eq(LodGpuArenaPolicy.PageSets(ceiling), vertex.CeilingBytes / vertex.PageBytes,
            "the page count is the number of page sets the ceiling affords");
        c.True(vertex.CeilingBytes + index.CeilingBytes <= ceiling,
            "the two shares never exceed the configured ceiling");
        c.True(vertex.CeilingBytes > index.CeilingBytes,
            "vertices receive the larger share, because their pages are larger");
        c.True(LodGpuArenaPolicy.PageSets(LodGpuArenaPolicy.MinimumCeilingBytes) >= 1,
            "even the smallest allowed ceiling affords a page set");

        // Page size is the measured lever on batch count, so it is configurable - and
        // therefore has to be clamped and to restore its default on unreadable input.
        try
        {
            LodGpuArenaPolicy.ConfigurePageBytes("32");
            c.Eq(32L * 1024 * 1024, LodGpuArenaPolicy.VertexPageBytes,
                "a page size is taken in mebibytes");
            c.Eq(16L * 1024 * 1024, LodGpuArenaPolicy.IndexPageBytes,
                "index pages stay half the vertex page, matching expanded geometry");
            c.Eq(LodGpuArenaPolicy.PageSets(768L * 1024 * 1024) * 32L * 1024 * 1024,
                LodGpuArenaPolicy.VertexLimits(768L * 1024 * 1024).CeilingBytes,
                "the page count follows the configured page size");

            LodGpuArenaPolicy.ConfigurePageBytes("99999");
            c.Eq(LodGpuArenaPolicy.MaximumVertexPageBytes, LodGpuArenaPolicy.VertexPageBytes,
                "an absurd page size is clamped rather than trusted");
            LodGpuArenaPolicy.ConfigurePageBytes("not a number");
            c.Eq(LodGpuArenaPolicy.DefaultVertexPageBytes, LodGpuArenaPolicy.VertexPageBytes,
                "an unreadable page size falls back to the default");
        }
        finally
        {
            LodGpuArenaPolicy.ConfigurePageBytes(null);
        }
    }

    // ---- Fixtures ----

    static LodRenderResourceIdentity Identity(long key) => new(1, key, key, key * 2, 0);

    static LodRenderPublication Publication(
        long key, float[] xyz, byte[] rgba, int[] indices) => new(
        Identity(key), null, null,
        xyz.Length / 3, indices.Length, 0, 0, 0,
        new LodRenderGeometry(xyz, rgba, indices));

    static (float[] Xyz, byte[] Rgba, int[] Indices) Quad(int seed) => Quads(1, seed);

    static (float[] Xyz, byte[] Rgba, int[] Indices) Quads(int quads, int seed)
    {
        var random = new Random(seed);
        var xyz = new float[quads * 4 * 3];
        var rgba = new byte[quads * 4 * 4];
        var indices = new int[quads * 6];
        for (int i = 0; i < xyz.Length; i++) xyz[i] = (float)(random.NextDouble() * 512 - 256);
        random.NextBytes(rgba);
        for (int q = 0; q < quads; q++)
        {
            int v = q * 4;
            indices[q * 6] = v;
            indices[q * 6 + 1] = v + 1;
            indices[q * 6 + 2] = v + 2;
            indices[q * 6 + 3] = v;
            indices[q * 6 + 4] = v + 2;
            indices[q * 6 + 5] = v + 3;
        }
        return (xyz, rgba, indices);
    }

    /// <summary>
    /// Stands in for the driver: pages are ordinary byte arrays, so what the arena believes
    /// it stored can be compared with what the mesher produced, and fences signal only when
    /// the test says so.
    /// </summary>
    sealed class FakeArenaBackend : ILodGpuArenaBackend
    {
        public readonly Dictionary<(LodGpuArenaKind Kind, int Handle), byte[]> Pages = new();
        public readonly HashSet<long> Signaled = new();
        public bool FencesUnavailable;
        public bool RefuseNextPage;
        public int DeletedPages;
        public int DeletedFences;
        int nextHandle = 1;
        long nextFence = 1;

        public int CreatePage(LodGpuArenaKind kind, long bytes)
        {
            if (RefuseNextPage)
            {
                RefuseNextPage = false;
                return 0;
            }
            int handle = nextHandle++;
            Pages[(kind, handle)] = new byte[bytes];
            return handle;
        }

        public bool Upload(LodGpuArenaKind kind, int page, long offset, ReadOnlySpan<byte> data)
        {
            if (!Pages.TryGetValue((kind, page), out byte[]? bytes)) return false;
            data.CopyTo(bytes.AsSpan((int)offset));
            return true;
        }

        public bool TryRead(LodGpuArenaKind kind, int page, long offset, Span<byte> destination)
        {
            if (!Pages.TryGetValue((kind, page), out byte[]? bytes)) return false;
            bytes.AsSpan((int)offset, destination.Length).CopyTo(destination);
            return true;
        }

        public void DeletePage(LodGpuArenaKind kind, int page)
        {
            if (Pages.Remove((kind, page))) DeletedPages++;
        }

        public long CreateFence() => FencesUnavailable ? 0 : nextFence++;
        public bool FenceSignaled(long fence) => Signaled.Contains(fence);
        public void DeleteFence(long fence)
        {
            Signaled.Remove(fence);
            DeletedFences++;
        }

        public void SignalAll()
        {
            for (long fence = 1; fence < nextFence; fence++) Signaled.Add(fence);
        }

        public bool MatchesVertices(
            LodGpuArena arena,
            in LodGpuGeometryMirror.MirroredSection section,
            float[] xyz,
            byte[] rgba)
        {
            byte[] page = Pages[(LodGpuArenaKind.Vertex, arena.PageHandle(section.Vertices))];
            ReadOnlySpan<byte> stored = page.AsSpan(
                (int)section.Vertices.Offset,
                (int)LodGpuGeometryFormat.VertexBytes(section.VertexCount));
            for (int i = 0; i < section.VertexCount; i++)
            {
                LodGpuGeometryFormat.DecodeVertex(
                    stored, i, out float x, out float y, out float z, out uint color);
                uint expected = (uint)(rgba[i * 4]
                    | rgba[i * 4 + 1] << 8
                    | rgba[i * 4 + 2] << 16
                    | rgba[i * 4 + 3] << 24);
                if (x != xyz[i * 3] || y != xyz[i * 3 + 1] || z != xyz[i * 3 + 2]
                    || color != expected) return false;
            }
            return true;
        }

        public bool MatchesIndices(
            LodGpuArena arena, in LodGpuGeometryMirror.MirroredSection section, int[] indices)
        {
            byte[] page = Pages[(LodGpuArenaKind.Index, arena.PageHandle(section.Indices))];
            ReadOnlySpan<byte> stored = page.AsSpan(
                (int)section.Indices.Offset,
                (int)LodGpuGeometryFormat.IndexBytes(section.IndexCount));
            for (int i = 0; i < section.IndexCount; i++)
                if (LodGpuGeometryFormat.DecodeIndex(stored, i) != indices[i]) return false;
            return true;
        }

        public bool MatchesPacked(
            LodGpuArena arena,
            in LodGpuGeometryMirror.MirroredSection section,
            uint[] words,
            int quadCount)
        {
            byte[] page = Pages[(LodGpuArenaKind.PackedQuad,
                arena.PageHandle(section.PackedQuads))];
            int bytes = (int)LodPackedQuadFormat.Bytes(quadCount);
            var expected = new byte[bytes];
            LodPackedQuadFormat.EncodeBytes(words, quadCount, expected);
            return page.AsSpan((int)section.PackedQuads.Offset, bytes).SequenceEqual(expected);
        }
    }

    /// <summary>
    /// The pool has to be able to hold whatever the player's own distance setting asks the mod
    /// to draw.
    ///
    /// A fixed 256 MiB was refusing 322 sections on 2026-08-24 and left 35% of drawn terrain
    /// outside the batched path, which meant batching and culling could not touch two thirds of
    /// the screen and no measurement taken through it meant much. The failure was silent in the
    /// only way that matters: everything still drew, correctly, by the slower route.
    /// </summary>
    static void CeilingFollowsDrawDistance(Check c)
    {
        // What this model is, stated precisely, because the first version of this check got it
        // wrong: it counts the sections a FULL CIRCLE of terrain would draw at one level each.
        // The 2026-08-24 session reported 580 resident against 560 modelled, and that agreement
        // was a coincidence rather than a validation - that world was a partly-explored corridor,
        // so a populated one at the same distance would hold considerably more. The model is
        // therefore a floor, and ResidencyHeadroom is what turns it into a ceiling worth having.
        double atThreeThousand = LodGpuArenaPolicy.SectionsWithin(3000);
        c.True(atThreeThousand > 450 && atThreeThousand < 700,
            "the full-circle model is the right order of magnitude at 3,000 blocks, got "
            + atThreeThousand.ToString("0"));
        c.True(LodGpuArenaPolicy.ResidencyHeadroom >= 2.0,
            "the derived ceiling carries at least the margin the bare model lacks");

        c.Eq(0.0, LodGpuArenaPolicy.SectionsWithin(0), "no distance holds no sections");
        c.Eq(0.0, LodGpuArenaPolicy.SectionsWithin(-1), "a negative distance holds no sections");
        c.Eq(0.0, LodGpuArenaPolicy.SectionsWithin(double.NaN), "nonsense holds no sections");

        // Monotonic, or a player who increases their distance could be given a smaller pool.
        double previous = 0;
        for (double d = 512; d <= 65536; d *= 1.5)
        {
            double sections = LodGpuArenaPolicy.SectionsWithin(d);
            c.True(sections >= previous,
                "asking for more distance never models fewer sections at " + d.ToString("0"));
            previous = sections;
        }

        // Doubling the distance must not double the pool while the level scheme still has a
        // coarser level to reach for - that is the whole reason this can be afforded at all.
        double at4k = LodGpuArenaPolicy.SectionsWithin(4096);
        double at8k = LodGpuArenaPolicy.SectionsWithin(8192);
        c.True(at8k < at4k * 1.6,
            "doubling from 4k to 8k adds a ring rather than multiplying, got "
            + at4k.ToString("0") + " then " + at8k.ToString("0"));

        // Past the last transition every section is the coarsest size there is, so the count
        // does start growing with area again. Worth pinning, because that is the regime where
        // a player's setting can genuinely cost them memory.
        double at16k = LodGpuArenaPolicy.SectionsWithin(16384);
        double at64k = LodGpuArenaPolicy.SectionsWithin(65536);
        c.True(at64k > at16k * 1.5,
            "past the last level the count grows with area, got "
            + at16k.ToString("0") + " then " + at64k.ToString("0"));

        // Never smaller than it has always been, whatever the setting.
        foreach (double d in new[] { 1024.0, 2048, 4096, 16384, 262144 })
        {
            c.True(LodGpuArenaPolicy.CeilingBytesFor(d, null)
                    >= LodGpuArenaPolicy.DefaultCeilingBytes,
                "a " + d.ToString("0") + "-block distance never shrinks the pool below the "
                + "old fixed default");
        }

        c.True(LodGpuArenaPolicy.CeilingBytesFor(262144, null)
                <= LodGpuArenaPolicy.MaximumCeilingBytes,
            "an extreme distance is still clamped");

        // The 2026-08-24 configuration - unlimited - must now afford more than it refused at.
        long unlimited = LodGpuArenaPolicy.CeilingBytesFor(
            LodGpuArenaPolicy.UnlimitedDrawDistanceBlocks, null);
        c.True(unlimited > LodGpuArenaPolicy.DefaultCeilingBytes,
            "an unlimited distance affords more than the fixed 256 MiB that refused 322 sections");

        // An explicit value still wins outright, or the benchmark harness loses the ability to
        // pin a pool size the way it pins every other path.
        c.Eq(64L * 1024 * 1024, LodGpuArenaPolicy.CeilingBytesFor(65536, "64"),
            "an explicit override beats the derived size, even downwards");
        c.Eq(LodGpuArenaPolicy.CeilingBytesFor(4096, null),
            LodGpuArenaPolicy.CeilingBytesFor(4096, "not a number"),
            "an unreadable override falls back to the derived size, not to nothing");
    }
}
