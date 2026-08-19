namespace VintageHorizons;

/// <summary>
/// Pure, renderer-owned model for the vanilla-chunk handoff. It deliberately knows
/// nothing about OpenGL or ICoreClientAPI: engine observations become publication
/// proposals, and committed CPU ownership changes only after the renderer accepts the
/// matching GPU update.
/// </summary>
internal sealed class VanillaRenderReadiness
{
    const int ChunkBlocks = 32;
    const byte QueuedFlag = 1;
    const byte PublicationPendingFlag = 2;
    const byte ObservationQueuedFlag = 4;

    readonly int capacity;
    readonly int capacityMask;
    readonly int verticalChunks;
    readonly long[] columnTags;
    readonly int[] columnReady;
    readonly byte[] states;
    readonly byte[] flags;
    readonly int[] observedFrames;
    readonly long[] pendingPublicationTokens;
    readonly VanillaChunkCell[] candidates;
    readonly long[] candidateFrames;
    readonly VanillaChunkCell[] observations;
    readonly Dictionary<long, int>[] readyCounts;

    int candidateHead;
    int candidateCount;
    int observationHead;
    int observationCount;
    int minChunkX;
    int minChunkZ;
    int width;
    int depth;
    long nextPublicationToken;

    public int PendingCandidates => candidateCount;
    public int PendingObservations => observationCount;
    public int HorizontalCapacity => capacity;
    public int VerticalChunks => verticalChunks;
    public int ActiveMinChunkX => minChunkX;
    public int ActiveMinChunkZ => minChunkZ;
    public int ActiveWidth => width;
    public int ActiveDepth => depth;
    public int ActiveCells => checked(width * depth * verticalChunks);
    public long TrackedArrayBytes =>
        columnTags.LongLength * sizeof(long)
        + states.LongLength + flags.LongLength
        + observedFrames.LongLength * sizeof(int)
        + pendingPublicationTokens.LongLength * sizeof(long)
        + candidateFrames.LongLength * sizeof(long)
        + (candidates.LongLength + observations.LongLength) * (sizeof(int) * 3L);
    public int CommittedReadyCells { get; private set; }
    public long CandidateEventsAccepted { get; private set; }
    public long CandidateEventsCoalesced { get; private set; }
    public long CandidateEventsDropped { get; private set; }
    public long ScheduledCandidatesAccepted { get; private set; }
    public long ScheduledCandidatesCoalesced { get; private set; }

    public VanillaRenderReadiness(int horizontalCapacity, int verticalChunkCount)
    {
        if (horizontalCapacity <= 0 || (horizontalCapacity & (horizontalCapacity - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(horizontalCapacity),
                "Horizontal capacity must be a positive power of two.");
        if (verticalChunkCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(verticalChunkCount));

        capacity = horizontalCapacity;
        capacityMask = horizontalCapacity - 1;
        verticalChunks = verticalChunkCount;
        int columns = checked(horizontalCapacity * horizontalCapacity);
        int cells = checked(columns * verticalChunkCount);
        columnTags = new long[columns];
        Array.Fill(columnTags, -1L);
        columnReady = new int[columns];
        states = new byte[cells];
        flags = new byte[cells];
        observedFrames = new int[cells];
        pendingPublicationTokens = new long[cells];
        candidates = new VanillaChunkCell[cells];
        candidateFrames = new long[cells];
        observations = new VanillaChunkCell[cells];
        readyCounts = new Dictionary<long, int>[LodWorld.MaxLevel + 1];
        for (int level = 0; level < readyCounts.Length; level++)
            readyCounts[level] = new Dictionary<long, int>();
    }

    /// <summary>
    /// Changes the valid camera-centred window. Cells leaving it are synchronously
    /// invalidated so CPU whole-section counts cannot retain stale vanilla ownership.
    /// Cells entering it remain Unknown until events or incremental seeding enqueue them.
    /// </summary>
    public bool SetWindow(int newMinChunkX, int newMinChunkZ, int newWidth, int newDepth,
        long renderFrame = 0)
    {
        if (newMinChunkX < 0 || newMinChunkZ < 0)
            throw new ArgumentOutOfRangeException(nameof(newMinChunkX));
        if (newWidth <= 0 || newWidth > capacity || newDepth <= 0 || newDepth > capacity)
            throw new ArgumentOutOfRangeException(nameof(newWidth));

        bool changed = newMinChunkX != minChunkX || newMinChunkZ != minChunkZ
            || newWidth != width || newDepth != depth;
        if (!changed) return false;

        int oldMaxX = minChunkX + width;
        int oldMaxZ = minChunkZ + depth;
        int newMaxX = newMinChunkX + newWidth;
        int newMaxZ = newMinChunkZ + newDepth;

        for (int z = minChunkZ; z < oldMaxZ; z++)
        {
            for (int x = minChunkX; x < oldMaxX; x++)
            {
                if (x < newMinChunkX || x >= newMaxX || z < newMinChunkZ || z >= newMaxZ)
                    ClearColumn(x, z);
            }
        }

        int oldMinX = minChunkX;
        int oldMinZ = minChunkZ;
        int oldWidth = width;
        int oldDepth = depth;

        minChunkX = newMinChunkX;
        minChunkZ = newMinChunkZ;
        width = newWidth;
        depth = newDepth;

        // Columns the window just gained are the streaming frontier, and while moving they
        // are exactly the ground the player is heading into. Queue them here rather than
        // waiting for a discovery cursor to sweep the whole window and reach them: at
        // flight speed the window moves again long before a sweep gets that far, so the
        // ground ahead would be the last thing ever probed.
        for (int z = newMinChunkZ; z < newMinChunkZ + newDepth; z++)
        {
            for (int x = newMinChunkX; x < newMinChunkX + newWidth; x++)
            {
                bool wasInside = x >= oldMinX && x < oldMinX + oldWidth
                    && z >= oldMinZ && z < oldMinZ + oldDepth;
                if (wasInside) continue;
                for (int y = 0; y < verticalChunks; y++)
                    EnqueueCandidate(new VanillaChunkCell(x, y, z), renderFrame);
            }
        }

        return true;
    }

    /// <summary>Queues at most <paramref name="maxCells"/> cells for initial discovery.</summary>
    public int SeedUnknownCells(ref int cursor, int maxCells, long renderFrame = 0)
    {
        if (maxCells <= 0 || width == 0 || depth == 0) return 0;
        int total = checked(width * depth * verticalChunks);
        int accepted = 0;
        int examined = 0;
        while (cursor < total && examined++ < maxCells)
        {
            int value = cursor++;
            int y = value % verticalChunks;
            value /= verticalChunks;
            int x = minChunkX + value % width;
            int z = minChunkZ + value / width;
            if (EnqueueCandidate(new VanillaChunkCell(x, y, z), renderFrame)) accepted++;
        }
        return accepted;
    }

    /// <summary>
    /// Incrementally requeues committed cells around the expected vanilla streaming
    /// frontier. The cursor enumerates ring perimeters directly, so an interior square
    /// cannot consume the boundary-first scan budget.
    /// </summary>
    public int QueueReadyShell(int centerChunkX, int centerChunkZ, int innerRadius,
        int outerRadius, ref int cursor, int maxCells, long renderFrame = 0)
    {
        if (maxCells <= 0 || outerRadius < 0) return 0;
        innerRadius = Math.Clamp(innerRadius, 0, outerRadius);
        int columns = ShellColumnCount(innerRadius, outerRadius);
        int total = checked(columns * verticalChunks);
        if (cursor >= total) cursor = 0;

        int accepted = 0;
        int examined = 0;
        while (cursor < total && examined++ < maxCells)
        {
            int value = cursor++;
            int y = value % verticalChunks;
            int columnOrdinal = value / verticalChunks;
            ShellColumn(centerChunkX, centerChunkZ, innerRadius, outerRadius,
                columnOrdinal, out int x, out int z);
            var cell = new VanillaChunkCell(x, y, z);
            if (State(cell) == VanillaReadinessState.VanillaReady
                && EnqueueCandidate(cell, renderFrame))
                accepted++;
        }
        return accepted;
    }

    /// <summary>
    /// Queues every committed cell for revalidation. Cursor-based sweeps kept failing to
    /// reach ground the camera had moved away from, and each failure left the mask hiding
    /// cached terrain nothing else drew - a hole that outlived the movement that caused it.
    /// Re-confirming the whole committed set on a fixed interval removes the class of bug
    /// rather than the instance: ownership can then be wrong for at most that interval, no
    /// matter how the camera moved to make it wrong.
    ///
    /// The committed set is small - roughly 1,700 cells at a 256-block view distance - and
    /// a probe costs well under a microsecond, so a full pass is a fraction of one frame's
    /// probe budget and is spread across frames by that budget in any case.
    /// </summary>
    public int QueueAllReadyCells(long renderFrame = 0)
    {
        int queued = 0;
        for (int slot = 0; slot < columnTags.Length; slot++)
        {
            if (columnTags[slot] == -1L || columnReady[slot] == 0) continue;
            UnpackColumn(columnTags[slot], out int chunkX, out int chunkZ);
            if (chunkX < minChunkX || chunkX >= minChunkX + width
                || chunkZ < minChunkZ || chunkZ >= minChunkZ + depth) continue;

            int baseIndex = slot * verticalChunks;
            for (int y = 0; y < verticalChunks; y++)
            {
                if ((VanillaReadinessState)states[baseIndex + y] != VanillaReadinessState.VanillaReady)
                    continue;
                if (EnqueueCandidate(new VanillaChunkCell(chunkX, y, chunkZ), renderFrame)) queued++;
            }
        }
        return queued;
    }

    /// <summary>
    /// Low-cadence interior maintenance under an examined-cell budget. This deliberately
    /// ignores cell state. A ready-only sweep can lose ownership but never gain it: a
    /// chunk that finishes tessellating after its dirty event was probed leaves its cell
    /// Pending, and nothing revisits it. A 2026-08-18 route measured exactly that - one
    /// 15-second interval spent 432,744 probes re-confirming 1,727 already-ready cells
    /// and none on the 1,801 pending ones, so ownership could only advance when camera
    /// movement reset the discovery cursor.
    /// </summary>
    public int QueueMaintenanceCells(ref int cursor, int maxCells, long renderFrame = 0)
    {
        if (maxCells <= 0 || width == 0 || depth == 0) return 0;
        int total = checked(width * depth * verticalChunks);
        if (cursor >= total) cursor = 0;
        int accepted = 0;
        int examined = 0;
        while (cursor < total && examined++ < maxCells)
        {
            int value = cursor++;
            int y = value % verticalChunks;
            value /= verticalChunks;
            int x = minChunkX + value % width;
            int z = minChunkZ + value / width;
            if (EnqueueCandidate(new VanillaChunkCell(x, y, z), renderFrame)) accepted++;
        }
        return accepted;
    }

    /// <summary>
    /// Admits one cell for probing. <paramref name="engineEvent"/> separates candidates the
    /// client actually announced from the tracker's own scheduled sweeps, because a single
    /// combined counter is dominated by maintenance and hides whether engine events are
    /// arriving, coalescing, or being refused at all.
    /// </summary>
    public bool EnqueueCandidate(VanillaChunkCell cell, long renderFrame = 0, bool engineEvent = false)
    {
        if (!TryGetCellIndex(cell, prepareColumn: true, out int index))
        {
            if (engineEvent) CandidateEventsDropped++;
            return false;
        }

        if ((flags[index] & QueuedFlag) != 0)
        {
            if (engineEvent) CandidateEventsCoalesced++;
            else ScheduledCandidatesCoalesced++;
            return false;
        }
        if (candidateCount == candidates.Length)
        {
            if (engineEvent) CandidateEventsDropped++;
            return false;
        }

        if ((VanillaReadinessState)states[index] == VanillaReadinessState.Unknown)
            states[index] = (byte)VanillaReadinessState.Pending;
        flags[index] |= QueuedFlag;
        int queueIndex = (candidateHead + candidateCount) % candidates.Length;
        candidates[queueIndex] = cell;
        candidateFrames[queueIndex] = renderFrame;
        candidateCount++;
        if (engineEvent) CandidateEventsAccepted++;
        else ScheduledCandidatesAccepted++;
        return true;
    }

    /// <summary>
    /// Returns one still-current candidate. Stale entries left by ring movement are
    /// discarded without making the new aliased cell look queued.
    /// </summary>
    public bool TryDequeueCandidate(out VanillaChunkCell cell)
    {
        while (candidateCount > 0)
        {
            cell = candidates[candidateHead];
            candidateHead = (candidateHead + 1) % candidates.Length;
            candidateCount--;
            if (!TryGetCellIndex(cell, prepareColumn: false, out int index)) continue;
            if ((flags[index] & QueuedFlag) == 0) continue;
            flags[index] &= unchecked((byte)~QueuedFlag);
            return true;
        }
        cell = default;
        return false;
    }

    /// <summary>
    /// Moves first-true observations into the probe queue only after a later render
    /// frame. The separate fixed queue prevents an observation from immediately
    /// re-probing itself repeatedly in the frame that first saw it.
    /// </summary>
    public int PromoteObserved(long renderFrame, int maxCells)
    {
        int promoted = 0;
        int examined = 0;
        while (observationCount > 0 && examined++ < maxCells)
        {
            VanillaChunkCell cell = observations[observationHead];
            if (!TryGetCellIndex(cell, prepareColumn: false, out int index)
                || (flags[index] & ObservationQueuedFlag) == 0
                || (VanillaReadinessState)states[index] != VanillaReadinessState.ObservedRendered)
            {
                PopObservation();
                continue;
            }
            if (renderFrame <= observedFrames[index]) break;
            if (candidateCount == candidates.Length) break;

            PopObservation();
            flags[index] &= unchecked((byte)~ObservationQueuedFlag);
            if (EnqueueCandidate(cell, renderFrame)) promoted++;
        }
        return promoted;
    }

    /// <summary>
    /// Applies one public IsChunkRendered observation. A true value needs two
    /// observations separated by a render frame. A false value proposes immediate loss
    /// when the cell was committed. Neither path changes CPU aggregates yet.
    /// </summary>
    public bool Observe(VanillaChunkCell cell, bool rendered, long renderFrame,
        out VanillaReadinessPublication publication)
    {
        publication = default;
        if (!TryGetCellIndex(cell, prepareColumn: false, out int index)) return false;

        VanillaReadinessState state = (VanillaReadinessState)states[index];
        if (!rendered)
        {
            observedFrames[index] = 0;
            if (state == VanillaReadinessState.VanillaReady
                && (flags[index] & PublicationPendingFlag) == 0)
            {
                flags[index] |= PublicationPendingFlag;
                long token = NextPublicationToken();
                pendingPublicationTokens[index] = token;
                publication = new VanillaReadinessPublication(cell, Ready: false, Token: token);
                return true;
            }
            states[index] = (byte)VanillaReadinessState.Pending;
            return false;
        }

        if (state == VanillaReadinessState.VanillaReady) return false;
        if (state == VanillaReadinessState.ObservedRendered
            && renderFrame > observedFrames[index]
            && (flags[index] & PublicationPendingFlag) == 0)
        {
            flags[index] &= unchecked((byte)~ObservationQueuedFlag);
            flags[index] |= PublicationPendingFlag;
            long token = NextPublicationToken();
            pendingPublicationTokens[index] = token;
            publication = new VanillaReadinessPublication(cell, Ready: true, Token: token);
            return true;
        }

        bool firstObservation = state != VanillaReadinessState.ObservedRendered;
        states[index] = (byte)VanillaReadinessState.ObservedRendered;
        observedFrames[index] = RenderFrameStamp(renderFrame);
        if (firstObservation) QueueObservation(cell, index);
        return false;
    }

    /// <summary>
    /// Completes the atomic publication handshake. Rejection never advances CPU
    /// ownership; the caller may retry or disable the hybrid path on a failed clear.
    /// </summary>
    public bool ResolvePublication(VanillaReadinessPublication publication, bool accepted)
    {
        if (!TryGetCellIndex(publication.Cell, prepareColumn: false, out int index)) return false;
        if ((flags[index] & PublicationPendingFlag) == 0) return false;
        if (pendingPublicationTokens[index] != publication.Token) return false;
        flags[index] &= unchecked((byte)~PublicationPendingFlag);
        pendingPublicationTokens[index] = 0;

        bool wasReady = (VanillaReadinessState)states[index] == VanillaReadinessState.VanillaReady;
        if (!accepted)
        {
            if (publication.Ready && !wasReady)
            {
                states[index] = (byte)VanillaReadinessState.ObservedRendered;
                QueueObservation(publication.Cell, index);
            }
            else if (!publication.Ready && wasReady)
                states[index] = (byte)VanillaReadinessState.VanillaReady;
            return false;
        }

        if (publication.Ready == wasReady) return true;
        if (publication.Ready)
        {
            states[index] = (byte)VanillaReadinessState.VanillaReady;
            ChangeReadyCounts(publication.Cell, 1);
            columnReady[index / verticalChunks]++;
            CommittedReadyCells++;
        }
        else
        {
            states[index] = (byte)VanillaReadinessState.Pending;
            ChangeReadyCounts(publication.Cell, -1);
            columnReady[index / verticalChunks]--;
            CommittedReadyCells--;
        }
        return true;
    }

    public VanillaReadinessState State(VanillaChunkCell cell)
    {
        return TryGetCellIndex(cell, prepareColumn: false, out int index)
            ? (VanillaReadinessState)states[index]
            : VanillaReadinessState.Unknown;
    }

    public int ReadyCount(long sectionKey)
    {
        int level = LodWorld.KeyLevel(sectionKey);
        return level >= 0 && level < readyCounts.Length
            ? readyCounts[level].GetValueOrDefault(sectionKey)
            : 0;
    }

    public VanillaSectionOwnership Classify(long sectionKey)
    {
        int level = LodWorld.KeyLevel(sectionKey);
        if (level < 0 || level > LodWorld.MaxLevel) return VanillaSectionOwnership.CacheOnly;
        int count = readyCounts[level].GetValueOrDefault(sectionKey);
        if (count == 0) return VanillaSectionOwnership.CacheOnly;
        int edgeCells = 2 << level;
        int total = checked(edgeCells * edgeCells * verticalChunks);
        return count == total ? VanillaSectionOwnership.VanillaOnly : VanillaSectionOwnership.Mixed;
    }

    public void Clear()
    {
        for (int slot = 0; slot < columnTags.Length; slot++)
        {
            if (columnTags[slot] == -1L) continue;
            ClearSlot(slot);
            columnTags[slot] = -1L;
        }
        Array.Clear(columnReady);
        candidateHead = 0;
        candidateCount = 0;
        observationHead = 0;
        observationCount = 0;
        minChunkX = minChunkZ = width = depth = 0;
        CommittedReadyCells = 0;
        for (int level = 0; level < readyCounts.Length; level++) readyCounts[level].Clear();
    }

    public void ResetTelemetry()
    {
        CandidateEventsAccepted = 0;
        CandidateEventsCoalesced = 0;
        CandidateEventsDropped = 0;
        ScheduledCandidatesAccepted = 0;
        ScheduledCandidatesCoalesced = 0;
    }

    public void GetStateCounts(out int unknown, out int pending, out int observed, out int ready)
    {
        pending = observed = ready = 0;
        for (int slot = 0; slot < columnTags.Length; slot++)
        {
            if (columnTags[slot] == -1L) continue;
            int baseIndex = slot * verticalChunks;
            for (int y = 0; y < verticalChunks; y++)
            {
                switch ((VanillaReadinessState)states[baseIndex + y])
                {
                    case VanillaReadinessState.Pending: pending++; break;
                    case VanillaReadinessState.ObservedRendered: observed++; break;
                    case VanillaReadinessState.VanillaReady: ready++; break;
                }
            }
        }
        unknown = Math.Max(0, ActiveCells - pending - observed - ready);
    }

    /// <summary>
    /// Reports how committed readiness is distributed vertically. Whole-section vanilla
    /// ownership needs every vertical chunk of every covered column, so a column that
    /// never completes proves the CPU whole-mesh skip is unreachable at this granularity
    /// and that any such aggregate must come from cached geometry coverage instead.
    /// The caller owns <paramref name="readyByY"/> so periodic diagnostics allocate nothing.
    /// </summary>
    public void GetColumnReadiness(int[] readyByY, out int columnsTracked,
        out int fullyReadyColumns, out int partialColumns, out int maxReadyPerColumn)
    {
        ArgumentNullException.ThrowIfNull(readyByY);
        if (readyByY.Length < verticalChunks)
            throw new ArgumentException("Readiness histogram is shorter than the world.", nameof(readyByY));

        Array.Clear(readyByY, 0, verticalChunks);
        columnsTracked = fullyReadyColumns = partialColumns = maxReadyPerColumn = 0;
        for (int slot = 0; slot < columnTags.Length; slot++)
        {
            if (columnTags[slot] == -1L) continue;
            int baseIndex = slot * verticalChunks;
            int ready = 0;
            bool tracked = false;
            for (int y = 0; y < verticalChunks; y++)
            {
                var state = (VanillaReadinessState)states[baseIndex + y];
                if (state != VanillaReadinessState.Unknown) tracked = true;
                if (state != VanillaReadinessState.VanillaReady) continue;
                readyByY[y]++;
                ready++;
            }

            if (!tracked) continue;
            columnsTracked++;
            if (ready == verticalChunks) fullyReadyColumns++;
            else if (ready > 0) partialColumns++;
            if (ready > maxReadyPerColumn) maxReadyPerColumn = ready;
        }
    }

    /// <summary>
    /// Distance in blocks from a camera position to the nearest tracked column that is not
    /// completely vanilla-ready, and to the nearest column with no ready cell at all. A
    /// single radial handoff can only suppress cached terrain inside the first of these,
    /// so this measures whether one global radius is worth anything before any pixel
    /// depends on it. Columns outside the active window are unknown and bound the result,
    /// so an untracked frontier cannot be mistaken for owned ground.
    /// </summary>
    /// <summary>
    /// Rewrites a mask from committed state. Window movement clears departing columns
    /// inside this class, and a ring slot reused by different world coordinates would
    /// otherwise leave another place's ownership in the texture, so the mask is rebuilt
    /// wholesale whenever the window moves rather than patched from outside.
    /// </summary>
    public void WriteMask(VanillaReadinessMask mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        mask.Clear();
        if (width == 0 || depth == 0) return;

        for (int slot = 0; slot < columnTags.Length; slot++)
        {
            long tag = columnTags[slot];
            if (tag == -1L || columnReady[slot] == 0) continue;
            UnpackColumn(tag, out int chunkX, out int chunkZ);
            if (chunkX < minChunkX || chunkX >= minChunkX + width
                || chunkZ < minChunkZ || chunkZ >= minChunkZ + depth) continue;

            int baseIndex = slot * verticalChunks;
            for (int y = 0; y < verticalChunks; y++)
            {
                if ((VanillaReadinessState)states[baseIndex + y] != VanillaReadinessState.VanillaReady)
                    continue;
                mask.Set(new VanillaChunkCell(chunkX, y, chunkZ), ready: true);
            }
        }
    }

    public double NearestIncompleteColumnBlocks(double cameraX, double cameraZ,
        out double nearestUnreadyBlocks)
    {
        double nearestIncomplete = double.MaxValue;
        nearestUnreadyBlocks = double.MaxValue;
        if (width == 0 || depth == 0) return 0;

        for (int z = minChunkZ; z < minChunkZ + depth; z++)
        {
            for (int x = minChunkX; x < minChunkX + width; x++)
            {
                int slot = ColumnSlot(x, z);
                int ready = columnTags[slot] == PackColumn(x, z) ? columnReady[slot] : 0;
                if (ready == verticalChunks) continue;

                double distance = ColumnDistanceBlocks(x, z, cameraX, cameraZ);
                if (distance < nearestIncomplete) nearestIncomplete = distance;
                if (ready == 0 && distance < nearestUnreadyBlocks) nearestUnreadyBlocks = distance;
            }
        }

        // The window edge is itself an unknown boundary: nothing beyond it is owned.
        double edge = WindowEdgeDistanceBlocks(cameraX, cameraZ);
        if (edge < nearestIncomplete) nearestIncomplete = edge;
        if (edge < nearestUnreadyBlocks) nearestUnreadyBlocks = edge;
        if (nearestUnreadyBlocks == double.MaxValue) nearestUnreadyBlocks = edge;
        return nearestIncomplete == double.MaxValue ? edge : nearestIncomplete;
    }


    /// <summary>Shortest horizontal distance from a point to a chunk column's own volume.</summary>
    static double ColumnDistanceBlocks(int chunkX, int chunkZ, double x, double z)
    {
        double dx = Math.Max(0, Math.Max(chunkX * (double)ChunkBlocks - x,
            x - (chunkX * (double)ChunkBlocks + ChunkBlocks)));
        double dz = Math.Max(0, Math.Max(chunkZ * (double)ChunkBlocks - z,
            z - (chunkZ * (double)ChunkBlocks + ChunkBlocks)));
        return Math.Sqrt(dx * dx + dz * dz);
    }

    double WindowEdgeDistanceBlocks(double cameraX, double cameraZ)
    {
        double minX = minChunkX * (double)ChunkBlocks;
        double minZ = minChunkZ * (double)ChunkBlocks;
        double maxX = (minChunkX + width) * (double)ChunkBlocks;
        double maxZ = (minChunkZ + depth) * (double)ChunkBlocks;
        return Math.Max(0, Math.Min(
            Math.Min(cameraX - minX, maxX - cameraX),
            Math.Min(cameraZ - minZ, maxZ - cameraZ)));
    }

    public long OldestCandidateFrame()
    {
        long oldest = long.MaxValue;
        for (int offset = 0; offset < candidateCount; offset++)
        {
            int queueIndex = (candidateHead + offset) % candidates.Length;
            VanillaChunkCell cell = candidates[queueIndex];
            if (!TryGetCellIndex(cell, prepareColumn: false, out int index)
                || (flags[index] & QueuedFlag) == 0) continue;
            long frame = candidateFrames[queueIndex];
            if (frame > 0 && frame < oldest) oldest = frame;
        }
        return oldest == long.MaxValue ? -1 : oldest;
    }

    public long OldestObservedFrame()
    {
        long oldest = long.MaxValue;
        for (int slot = 0; slot < columnTags.Length; slot++)
        {
            if (columnTags[slot] == -1L) continue;
            int baseIndex = slot * verticalChunks;
            for (int y = 0; y < verticalChunks; y++)
            {
                int index = baseIndex + y;
                if ((VanillaReadinessState)states[index] != VanillaReadinessState.ObservedRendered) continue;
                long frame = observedFrames[index];
                if (frame > 0 && frame < oldest) oldest = frame;
            }
        }
        return oldest == long.MaxValue ? -1 : oldest;
    }

    public static int ChunkCoordinate(double worldCoordinate) =>
        checked((int)Math.Floor(worldCoordinate / ChunkBlocks));

    public static int ChunkCoordinate(int sectionChunkOrigin, float sectionLocalBlocks) =>
        checked(sectionChunkOrigin + (int)MathF.Floor(sectionLocalBlocks / ChunkBlocks));

    public static VanillaReadinessWindow CalculateWindow(double cameraX, double cameraZ,
        float approvedViewDistance, int mapSizeX, int mapSizeZ, int guardChunks = 2)
    {
        int mapChunksX = Math.Max(1, (mapSizeX + ChunkBlocks - 1) / ChunkBlocks);
        int mapChunksZ = Math.Max(1, (mapSizeZ + ChunkBlocks - 1) / ChunkBlocks);
        int centerX = Math.Clamp(ChunkCoordinate(cameraX), 0, mapChunksX - 1);
        int centerZ = Math.Clamp(ChunkCoordinate(cameraZ), 0, mapChunksZ - 1);
        int vanillaRadius = Math.Max(0, (int)Math.Ceiling(Math.Max(0f, approvedViewDistance) / ChunkBlocks));
        int outerRadius = checked(vanillaRadius + Math.Max(0, guardChunks));
        int minX = Math.Max(0, centerX - outerRadius);
        int minZ = Math.Max(0, centerZ - outerRadius);
        int maxX = Math.Min(mapChunksX - 1, centerX + outerRadius);
        int maxZ = Math.Min(mapChunksZ - 1, centerZ + outerRadius);
        int required = Math.Max(maxX - minX + 1, maxZ - minZ + 1);
        return new VanillaReadinessWindow(centerX, centerZ, minX, minZ,
            maxX - minX + 1, maxZ - minZ + 1, vanillaRadius, outerRadius,
            NextPowerOfTwo(required));
    }

    static int RenderFrameStamp(long frame) =>
        frame >= int.MaxValue ? int.MaxValue : frame <= int.MinValue ? int.MinValue : (int)frame;

    void QueueObservation(VanillaChunkCell cell, int index)
    {
        if ((flags[index] & ObservationQueuedFlag) != 0 || observationCount == observations.Length) return;
        flags[index] |= ObservationQueuedFlag;
        observations[(observationHead + observationCount) % observations.Length] = cell;
        observationCount++;
    }

    void PopObservation()
    {
        observationHead = (observationHead + 1) % observations.Length;
        observationCount--;
    }

    static int NextPowerOfTwo(int value)
    {
        int result = 1;
        while (result < value)
        {
            if (result > 1 << 29) throw new OverflowException("Readiness window is too large.");
            result <<= 1;
        }
        return result;
    }

    static int ShellColumnCount(int innerRadius, int outerRadius)
    {
        int outerWidth = checked(outerRadius * 2 + 1);
        int innerWidth = innerRadius == 0 ? 0 : innerRadius * 2 - 1;
        return checked(outerWidth * outerWidth - innerWidth * innerWidth);
    }

    static void ShellColumn(int centerX, int centerZ, int innerRadius, int outerRadius,
        int ordinal, out int x, out int z)
    {
        for (int radius = innerRadius; radius <= outerRadius; radius++)
        {
            int count = radius == 0 ? 1 : radius * 8;
            if (ordinal >= count)
            {
                ordinal -= count;
                continue;
            }
            if (radius == 0)
            {
                x = centerX;
                z = centerZ;
                return;
            }

            int top = radius * 2 + 1;
            if (ordinal < top)
            {
                x = centerX - radius + ordinal;
                z = centerZ - radius;
                return;
            }
            ordinal -= top;
            int right = radius * 2;
            if (ordinal < right)
            {
                x = centerX + radius;
                z = centerZ - radius + 1 + ordinal;
                return;
            }
            ordinal -= right;
            int bottom = radius * 2;
            if (ordinal < bottom)
            {
                x = centerX + radius - 1 - ordinal;
                z = centerZ + radius;
                return;
            }
            ordinal -= bottom;
            x = centerX - radius;
            z = centerZ + radius - 1 - ordinal;
            return;
        }
        throw new ArgumentOutOfRangeException(nameof(ordinal));
    }

    bool TryGetCellIndex(VanillaChunkCell cell, bool prepareColumn, out int index)
    {
        index = -1;
        if (cell.X < minChunkX || cell.X >= minChunkX + width
            || cell.Z < minChunkZ || cell.Z >= minChunkZ + depth
            || cell.Y < 0 || cell.Y >= verticalChunks) return false;

        int slot = ColumnSlot(cell.X, cell.Z);
        long tag = PackColumn(cell.X, cell.Z);
        if (columnTags[slot] != tag)
        {
            if (!prepareColumn) return false;
            ClearSlot(slot);
            columnTags[slot] = tag;
        }
        index = slot * verticalChunks + cell.Y;
        return true;
    }

    void ClearColumn(int chunkX, int chunkZ)
    {
        int slot = ColumnSlot(chunkX, chunkZ);
        if (columnTags[slot] != PackColumn(chunkX, chunkZ)) return;
        ClearSlot(slot);
        columnTags[slot] = -1L;
    }

    void ClearSlot(int slot)
    {
        long tag = columnTags[slot];
        int baseIndex = slot * verticalChunks;
        if (tag != -1L)
        {
            UnpackColumn(tag, out int chunkX, out int chunkZ);
            for (int y = 0; y < verticalChunks; y++)
            {
                int index = baseIndex + y;
                if ((VanillaReadinessState)states[index] == VanillaReadinessState.VanillaReady)
                {
                    ChangeReadyCounts(new VanillaChunkCell(chunkX, y, chunkZ), -1);
                    CommittedReadyCells--;
                }
            }
        }
        columnReady[slot] = 0;
        Array.Clear(states, baseIndex, verticalChunks);
        Array.Clear(flags, baseIndex, verticalChunks);
        Array.Clear(observedFrames, baseIndex, verticalChunks);
        Array.Clear(pendingPublicationTokens, baseIndex, verticalChunks);
    }

    void ChangeReadyCounts(VanillaChunkCell cell, int delta)
    {
        for (int level = 0; level <= LodWorld.MaxLevel; level++)
        {
            int edgeCells = 2 << level;
            long key = LodWorld.SectionKey(level, cell.X / edgeCells, cell.Z / edgeCells);
            Dictionary<long, int> counts = readyCounts[level];
            int next = counts.GetValueOrDefault(key) + delta;
            if (next < 0) throw new InvalidOperationException("Vanilla readiness count underflow.");
            if (next == 0) counts.Remove(key);
            else counts[key] = next;
        }
    }

    int ColumnSlot(int chunkX, int chunkZ) =>
        ((chunkZ & capacityMask) * capacity) + (chunkX & capacityMask);

    static long PackColumn(int chunkX, int chunkZ) => ((long)(uint)chunkZ << 32) | (uint)chunkX;

    static void UnpackColumn(long packed, out int chunkX, out int chunkZ)
    {
        chunkX = (int)packed;
        chunkZ = (int)(packed >> 32);
    }

    long NextPublicationToken()
    {
        nextPublicationToken++;
        if (nextPublicationToken == 0) nextPublicationToken++;
        return nextPublicationToken;
    }
}

internal enum VanillaReadinessState : byte
{
    Unknown,
    Pending,
    ObservedRendered,
    VanillaReady,
}

internal enum VanillaSectionOwnership : byte
{
    CacheOnly,
    Mixed,
    VanillaOnly,
}

internal readonly record struct VanillaChunkCell(int X, int Y, int Z);

internal readonly record struct VanillaReadinessPublication(VanillaChunkCell Cell, bool Ready, long Token);

internal readonly record struct VanillaReadinessWindow(
    int CenterX,
    int CenterZ,
    int MinX,
    int MinZ,
    int Width,
    int Depth,
    int VanillaRadius,
    int OuterRadius,
    int RequiredCapacity);
