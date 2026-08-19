using System.Diagnostics;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VintageHorizons;

/// <summary>
/// Renders the LodWorld section pyramid beyond the vanilla view distance. Meshes are
/// built off-thread by the LodWorker from section snapshots; this class schedules
/// mesh jobs (nearest-first), uploads finished vertex data on the render thread, and
/// walks the quadtree each frame picking detail by distance - a parent renders until
/// all four child slots are covered, so level swaps never open holes (DH's rule).
///
/// Rendering techniques (render order/stage, ZFar extension, camera-relative model
/// matrices, fog + transition handling in the shaders) adapted from Farseer
/// (https://github.com/ViciousBadger/VSMod-Farseer, MIT, (c) Badgerson).
/// </summary>
public class LodTerrainRenderer : IRenderer
{
    public double RenderOrder => 0.36; // just before opaque terrain → occluded by real chunks
    public int RenderRange => 9999;

    const int MeshSchedulesPerFrame = 4;
    const int MeshUploadsPerFrame = 4;
    // Initial frame-local ceilings. Item caps remain as a final safety rail, while live
    // telemetry records enough time/byte/backlog evidence to tune these without guessing.
    internal const long MeshSnapshotMaxBytesPerFrame = 2 * 1024 * 1024;
    internal const double MeshSnapshotMaxMillisecondsPerFrame = 1.0;
    internal const long MeshUploadMaxBytesPerFrame = 4 * 1024 * 1024;
    internal const double MeshUploadMaxMillisecondsPerFrame = 2.0;
    internal const int ReadinessProbeMaxItemsPerFrame = 256;
    internal const double ReadinessProbeMaxMillisecondsPerFrame = 0.25;

    /// <summary>
    /// Backlog ceilings. A settled view empties its queue every frame and never reaches
    /// the ordinary limits, so those limits only ever bind while the window is moving -
    /// which is precisely when unowned ground shows cached terrain over real terrain. A
    /// probe costs well under a microsecond, so the item cap, not the clock, was ending
    /// ordinary frames early. When the queue is genuinely deep the budget opens up and the
    /// elapsed-time ceiling becomes the real guard.
    /// </summary>
    internal const int ReadinessProbeCatchUpItemsPerFrame = 1024;
    internal const double ReadinessProbeCatchUpMillisecondsPerFrame = 1.0;
    internal const int ReadinessProbeCatchUpQueueDepth = 512;
    internal const int ReadinessSeedCellsPerFrame = 256;
    internal const int ReadinessBoundaryCellsPerFrame = 128;

    /// <summary>
    /// Cells examined in the loss-detection shell during the frame the camera crosses a
    /// chunk boundary, which is the moment vanilla's own unload radius has just swept past
    /// ground we may still be marking as vanilla-owned.
    ///
    /// The ordinary per-frame scan walks a cursor and takes tens of frames to circle the
    /// camera. Flying backwards outruns it: chunks unload in front of the eyes while their
    /// cells still read owned, so the mask keeps suppressing cached terrain that nothing
    /// else is drawing and a band of world goes missing. A crossing happens once every 32
    /// blocks of travel, so completing the sweep then is affordable, and it costs a queue
    /// of probes rather than the probes themselves.
    /// </summary>
    internal const int ReadinessBoundarySweepOnCrossing = 4096;

    /// <summary>
    /// How often every committed cell is re-confirmed, in milliseconds. This is the upper
    /// bound on how long the mask can hide cached terrain that vanilla has stopped drawing,
    /// and it holds however the camera moved. Cursor sweeps could not provide that bound:
    /// each one restarted or fell behind under movement, and holes outlived the movement.
    /// </summary>
    internal const long ReadinessFullRevalidateMilliseconds = 1000;
    internal const int ReadinessInteriorCellsPerFrame = 32;
    internal const int ReadinessGuardChunks = 2;

    /// <summary>
    /// Pulled back from the nearest column that is not wholly owned. A cell becomes ready
    /// when two render-frame-separated probes agree, which is a tessellation signal rather
    /// than proof that vanilla's replacement mesh is on screen, so the handoff stops one
    /// vanilla chunk short of the boundary it measured.
    /// </summary>
    internal const float ReadinessHandoffMarginBlocks = 32;

    /// <summary>
    /// Camera movement that re-measures owned ownership. Well under one vanilla chunk, so
    /// the radius cannot lag the camera by a whole ownership cell.
    /// </summary>
    internal const float ReadinessHandoffRecheckBlocks = 4;

    /// <summary>
    /// Cached terrain stays suppressed this close to the camera even under the per-cell
    /// mask, but only while the camera's own cell is committed vanilla ready.
    ///
    /// The mask decides ownership per cell, so a cell vanilla has not proven - usually an
    /// underground one whose chunk never reports rendered - keeps drawing cached terrain.
    /// That is correct at distance and wrong in the player's face: coarse cached geometry
    /// at arm's length reads as a wall through the world. The old radius hid this by
    /// suppressing everything near the camera unconditionally, which is also what made it
    /// open holes. Requiring the camera's own cell to be owned keeps the near-field clean
    /// without reintroducing that: if vanilla is not drawing where the player stands,
    /// nothing is suppressed at all.
    /// </summary>
    internal const float MaskNearFloorBlocks = 48;
    /// <summary>
    /// Queue depth allowed at the mesh workers. Per thread, not absolute: a fixed 12 was
    /// sized for one builder and would leave a four-thread pool idling three quarters of
    /// the time. Deep enough that a thread finishing a job always has another waiting,
    /// shallow enough that the queue does not outlive the view that asked for it.
    /// </summary>
    const int MeshBacklogPerThread = 4;
    int maxWorkerMeshBacklog;

    /// <summary>Reload requests per frame; only enqueues a key, so it can far exceed the mesh budget.</summary>
    const int MeshLoadRequestsPerFrame = 32;

    readonly ICoreClientAPI capi;
    readonly LodWorld world;
    readonly LodWorker worker;
    /// <summary>
    /// What the renderer's own frame costs, by phase, since the last stats report. Each
    /// of these is tens of microseconds against a ten-millisecond frame, which is well
    /// under what any frame-rate comparison can resolve, so they are timed rather than
    /// inferred. Reported by .vhinfo and by the periodic stats line.
    /// </summary>
    public LodPhaseCost PruneCost, ScheduleCost, UploadCost, GlUploadCost, MeshDisposeCost,
        EvictCost, SeasonalCost, FarDistanceCost, ReadinessCost, WalkCost, DrawCost;
    public bool TrackPhaseAllocations { get; set; }

    public int ProjectionResetCount { get; private set; }
    public long MeshUploadBytes { get; private set; }
    public long MeshSnapshotBytes { get; private set; }
    public int MeshSnapshotItems { get; private set; }
    public int MeshUploadItems { get; private set; }
    public long ReadinessProbes { get; private set; }
    public long ReadinessTrueResults { get; private set; }
    public long ReadinessFalseResults { get; private set; }
    public long ReadinessReadyTransitions { get; private set; }
    public long ReadinessLostTransitions { get; private set; }
    public long ReadinessProbeErrors { get; private set; }
    public long ReadinessWindowChanges { get; private set; }
    public long ReadinessResizes { get; private set; }
    public long ReadinessFullRevalidations { get; private set; }
    public long VanillaOwnedDrawsSkipped { get; private set; }
    public long CoarseWaitingLoad { get; private set; }
    public long CoarseWaitingMesh { get; private set; }
    public long CoarseWaitingSchedule { get; private set; }
    public long CoarseWaitingOther { get; private set; }

    public void ResetPhaseCosts()
    {
        PruneCost.Reset();
        ScheduleCost.Reset();
        UploadCost.Reset();
        GlUploadCost.Reset();
        MeshDisposeCost.Reset();
        EvictCost.Reset();
        SeasonalCost.Reset();
        FarDistanceCost.Reset();
        ReadinessCost.Reset();
        WalkCost.Reset();
        DrawCost.Reset();
        ProjectionResetCount = 0;
        MeshUploadBytes = 0;
        MeshSnapshotBytes = 0;
        MeshSnapshotItems = 0;
        MeshUploadItems = 0;
        ReadinessProbes = 0;
        ReadinessTrueResults = 0;
        ReadinessFalseResults = 0;
        ReadinessReadyTransitions = 0;
        ReadinessLostTransitions = 0;
        ReadinessProbeErrors = 0;
        ReadinessWindowChanges = 0;
        ReadinessResizes = 0;
        ReadinessFullRevalidations = 0;
        VanillaOwnedDrawsSkipped = 0;
        CoarseWaitingLoad = 0;
        CoarseWaitingMesh = 0;
        CoarseWaitingSchedule = 0;
        CoarseWaitingOther = 0;
        readiness?.ResetTelemetry();
        readinessMask?.ResetTelemetry();
    }

    readonly Dictionary<long, MeshRef> sectionMeshes = new();
    readonly Dictionary<long, MeshRef> waterMeshes = new();
    readonly LodMeshBounds meshBounds = new();
    readonly LodFarPlaneState farPlaneState = new();
    readonly HashSet<long> meshJobInFlight = new();
    readonly LodRenderDirtyScheduler dirtyScheduler = new();
    readonly Predicate<long> keepRenderDirty;
    readonly Predicate<long> renderDirtyBlocked;
    // Visibility is intentionally absent from this state. The camera may stop walking
    // an off-screen subtree, but distance-based residency still keeps appropriate meshes
    // warm for a turn-around (G8).
    readonly Dictionary<long, long> lastResidencyFrame = new();
    readonly List<long> evictBatch = new();
    long frameCounter;

    // Phase 1 shadow tracker. It follows vanilla readiness and maintains aggregate
    // classifications, but no draw path reads those classifications yet. Pixel ownership
    // remains on the existing radial fallback until the GPU mask is implemented.
    VanillaRenderReadiness? readiness;
    readonly EntityPos readinessProbePos = new();
    int readinessSeedCursor;
    int readinessBoundaryCursor;
    int readinessInteriorCursor;
    bool readinessDisabled;
    bool readinessFailureReported;
    int[] readinessColumnReady = Array.Empty<int>();
    readonly StringBuilder readinessColumnText = new();
    readonly LodNearHandoffState nearHandoff = new();
    // Phase 2 per-cell ownership. Off unless VINTAGEHORIZONS_CHUNK_MASK=1, so the measured
    // radius stays the only pixel owner until the mask has its own runtime evidence.
    bool chunkMaskRequested =
        Environment.GetEnvironmentVariable("VINTAGEHORIZONS_CHUNK_MASK") == "1";
    VanillaReadinessMask? readinessMask;
    LoadedTexture? readinessMaskTexture;
    bool readinessMaskFailed;
    bool readinessMaskActive;
    long readinessMaskUploadUs;
    float readinessHandoffDistance;
    bool readinessOwnsHandoff;
    float readinessOwnedRadius;
    double readinessHandoffCameraX;
    double readinessHandoffCameraZ;
    bool readinessHandoffStale = true;
    bool readinessSweepShellNow;
    long readinessLastFullRevalidateMs;

    /// <summary>Meshes unselected for this many frames (~1 min) get evicted; the quadtree re-requests on demand.</summary>
    const int EvictAfterFrames = 3600;
    const int EvictSweepInterval = 300;

    public int EvictedTotal { get; private set; }
    readonly Matrixf modelMat = new();
    readonly List<long> drawList = new();
    IShaderProgram? prog;
    bool shaderOk;
    float appliedZFar;
    Vec3d camPos = new();

    /// <summary>Dev/testing: keep the game unpaused even without window focus.</summary>
    public bool AutoUnpause;

    // Live seasonal state, refreshed periodically and fed to the shader as uniforms.
    float snowLineY = 99999;
    long lastSeasonRefreshFrame = -99999;
    readonly BlockPos climatePos = new(0, 0, 0);

    /// <summary>Optional hard cap in blocks; 0 = unlimited (render every cached section).</summary>
    public int FarViewDistanceCap = 0;

    /// <summary>Current far edge in blocks: the farthest loaded LOD data, independent of the vanilla view distance.</summary>
    public float EffectiveFarDistance { get; private set; } = 3000;

    public int MeshCount => sectionMeshes.Count;
    public int LastDrawCount { get; private set; }

    /// <summary>Sections selected by the walk but skipped this frame as off-screen.</summary>
    public int LastCulledCount { get; private set; }

    /// <summary>Whole quadtree subtrees rejected before descent this frame.</summary>
    public int LastTraversalCulledCount { get; private set; }

    readonly LodFrustum frustum = new();
    int worldHeight = 1024;


    /// <summary>
    /// Why each coarse node in the current draw list is not descending. Written for one
    /// specific failure: a node drawn far below its wanted level, with the pipeline idle, so
    /// no amount of waiting changes it. Reports each child's actual state rather than an
    /// inference - which is what three wrong diagnoses in a row cost.
    /// </summary>
    public string ExplainCoarseDraws(double px, double pz, int maxNodes = 6)
    {
        var sb = new System.Text.StringBuilder();
        int shown = 0;

        foreach (long key in drawList)
        {
            int level = LodWorld.KeyLevel(key);
            int wanted = WantedLevel(NearestDistanceTo(key));
            if (level <= wanted || shown >= maxNodes) continue;

            shown++;
            double dist = Math.Sqrt(LodWorld.NearestDistanceSqTo(key, px, pz));
            sb.Append($"\n  L{level} at {LodWorld.KeySx(key) * LodWorld.KeyFootprintBlocks(key)},")
              .Append($"{LodWorld.KeySz(key) * LodWorld.KeyFootprintBlocks(key)} dist {(int)dist} wants L{wanted}:");

            for (int qz = 0; qz < 2; qz++)
            {
                for (int qx = 0; qx < 2; qx++)
                {
                    long ck = LodWorld.ChildKey(key, qx, qz);
                    string state;
                    if (!world.HasDataSet.Contains(ck)) state = "no-data";
                    else if (!world.Sections.TryGetValue(ck, out LodSection? cs))
                    {
                        state = world.LoadsInFlight.Contains(ck) ? "loading"
                            : world.LoadFailed.Contains(ck) ? "load-failed"
                            : "not-resident";
                    }
                    else if (cs.CapturedColumns == 0) state = "empty";
                    else if (!HasAnyMesh(ck)) state = world.RenderDirty.Contains(ck) ? "meshing" : "no-mesh!";
                    else state = "ok";
                    sb.Append(' ').Append(state);
                }
            }
        }

        return shown == 0 ? "no coarse draws: every drawn node is at or below its wanted level" : sb.ToString();
    }

    public string DescribeDrawnLevels()
    {
        var counts = new int[LodWorld.MaxLevel + 1];
        foreach (long key in drawList) counts[LodWorld.KeyLevel(key)]++;
        return string.Join(" ", counts.Select((c, i) => $"L{i}:{c}"));
    }

    readonly LodTintRegistry tints;
    int uploadedTintVersion = -1;

    public LodTerrainRenderer(ICoreClientAPI capi, LodWorld world, LodWorker worker, LodTintRegistry tints)
    {
        this.capi = capi;
        this.world = world;
        this.worker = worker;
        this.tints = tints;
        keepRenderDirty = KeepRenderDirty;
        renderDirtyBlocked = RenderDirtyBlocked;
        maxWorkerMeshBacklog = worker.MeshThreads * MeshBacklogPerThread;

        capi.Event.ReloadShader += LoadShader;
        capi.Event.ChunkDirty += OnReadinessChunkDirty;
        LoadShader();

        capi.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "vintagehorizons-lod");
    }

    void OnReadinessChunkDirty(Vec3i chunkCoord, IWorldChunk chunk, EnumChunkDirtyReason reason)
    {
        // ClientWorldMap raises this from the main thread after installing the chunk but
        // before tessellation. Store only value coordinates; never retain the engine chunk.
        readiness?.EnqueueCandidate(
            new VanillaChunkCell(chunkCoord.X, chunkCoord.Y, chunkCoord.Z), frameCounter,
            engineEvent: true);
    }

    public bool LoadShader()
    {
        prog = capi.Shader.NewShaderProgram();
        prog.AssetDomain = "vintagehorizons";

        prog.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
        prog.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);

        capi.Shader.RegisterFileShaderProgram("lodterrain", prog);

        // The shaders carry their own `const int TINT_SLOTS`, because this game version
        // exposes no way to inject a #define, and a mismatch decodes water as opaque and
        // thin plants as water with no compile error. That used to be guarded here by
        // comparing MaxSlots against a hand-maintained C# mirror of the shader's value -
        // which is two constants in one file, and so could never notice a shader being
        // edited. The compiler agreed: the branch raised CS0162, unreachable code.
        // The check that works reads the shader files, in the fast tier of check.sh.

        uploadedTintVersion = -1; // fresh program object: uniform state is gone
        shaderOk = prog.Compile();
        if (!shaderOk) capi.Logger.Error("[VintageHorizons] lodterrain shader failed to compile; LOD rendering disabled");
        return shaderOk;
    }

    public void ApplyZFar()
    {
        float needed = LodFarDistance.RequiredProjection(EffectiveFarDistance);
        LodFarPlaneUpdate update = farPlaneState.Update(needed, Environment.TickCount64);
        var clientMain = (ClientMain)capi.World;

        if (!update.Changed && clientMain.MainCamera.ZFar >= update.Distance
            && appliedZFar == update.Distance) return;

        clientMain.MainCamera.ZFar = update.Distance;
        capi.Render.Reset3DProjection();
        appliedZFar = update.Distance;
        ProjectionResetCount++;
    }

    void UpdateEffectiveFarDistance(float vanillaViewDistance)
    {
        if (meshBounds.Dirty) meshBounds.Rebuild(sectionMeshes.Keys, waterMeshes.Keys);
        double farthest = meshBounds.FarthestDistanceTo(camPos.X, camPos.Z);
        EffectiveFarDistance = LodFarDistance.Effective(farthest, vanillaViewDistance, FarViewDistanceCap);
    }

    // ---- Detail selection (quadtree walk) ----

    double NearestDistanceTo(long key)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        double minX = LodWorld.KeySx(key) * (double)footprint;
        double minZ = LodWorld.KeySz(key) * (double)footprint;
        double dx = Math.Max(0, Math.Max(minX - camPos.X, camPos.X - (minX + footprint)));
        double dz = Math.Max(0, Math.Max(minZ - camPos.Z, camPos.Z - (minZ + footprint)));
        return Math.Sqrt(dx * dx + dz * dz);
    }

    static int WantedLevel(double distance) => LodWorld.WantedLevelFor(distance);

    /// <summary>
    /// Squared distance to the section's nearest edge. The hot paths compare and pick a
    /// level, and both work on the square, so neither needs the root.
    /// </summary>
    double NearestDistanceSqTo(long key) => LodWorld.NearestDistanceSqTo(key, camPos.X, camPos.Z);

    bool HasAnyMesh(long key) => sectionMeshes.ContainsKey(key) || waterMeshes.ContainsKey(key);

    bool AllVisibleChildrenCovered(long key)
    {
        bool covered = true;
        for (int qz = 0; qz < 2; qz++)
        {
            for (int qx = 0; qx < 2; qx++)
            {
                long ck = LodWorld.ChildKey(key, qx, qz);
                if (!world.HasDataSet.Contains(ck)) continue;
                if (!LodTraversalPolicy.NodeInView(frustum, ck,
                    camPos.X, camPos.Y, camPos.Z, worldHeight)) continue;

                // A resident section with nothing captured will never get a mesh --
                // RequestMesh refuses it, by design. Counting it as uncovered pins the
                // parent at its own level permanently, which is not the transient
                // wait-for-mesh this gate exists for: it showed up as hard-edged coarse
                // plates over ground whose finer data was already loaded, with the whole
                // pipeline idle. Treat it like an absent child instead.
                if (world.Sections.TryGetValue(ck, out LodSection? child) && child.CapturedColumns == 0)
                {
                    continue;
                }

                // Gate meshes are load-bearing even when never drawn. Residency is
                // maintained independently by the distance-based eviction sweep.
                if (!HasAnyMesh(ck))
                {
                    // Missing gate: re-request (evicted, or never built) so descent
                    // can resume; the parent keeps covering meanwhile.
                    RequestMesh(ck);
                    covered = false;
                    CountCoarseWait(ck);
                }
            }
        }
        return covered;
    }

    /// <summary>
    /// Records why a child had no mesh, and therefore why its parent is still drawing the
    /// area coarsely. Human testing reported terrain becoming coarser than expected during
    /// fast flight, and the four causes want four different fixes: waiting on storage,
    /// waiting on a mesh worker, waiting for a scheduling slot, or nothing pending at all,
    /// which would mean the obligation was dropped. Guessing between them from a frame rate
    /// is how the wrong one gets optimised.
    ///
    /// Only reached on the uncovered path, which is rare once terrain settles, and the
    /// lookups sit beside a RequestMesh call that already costs more.
    /// </summary>
    void CountCoarseWait(long childKey)
    {
        if (world.LoadsInFlight.Contains(childKey)) CoarseWaitingLoad++;
        else if (meshJobInFlight.Contains(childKey)) CoarseWaitingMesh++;
        else if (world.RenderDirty.Contains(childKey)) CoarseWaitingSchedule++;
        else CoarseWaitingOther++;
    }

    bool CollectDrawNodes(long key)
    {
        // A rejected parent contains every descendant, so the conservative p-vertex box
        // test makes it safe to skip all draw selection, mesh demand, and child traversal.
        if (!LodTraversalPolicy.NodeInView(frustum, key,
            camPos.X, camPos.Y, camPos.Z, worldHeight))
        {
            traversalCulledThisFrame++;
            return false;
        }

        bool hasMesh = HasAnyMesh(key);
        int level = LodWorld.KeyLevel(key);
        int wanted = LodWorld.WantedLevelForSq(NearestDistanceSqTo(key));

        // Demand-driven meshing: request ONLY at the level the walk actually wants
        // here. Descending through meshless parents must not request every leaf the
        // recursion happens to reach - coarser/finer nodes on the path stay unmeshed
        // until the wanted level for their own distance says otherwise.
        if (!hasMesh && level == wanted) RequestMesh(key);

        if (level > 0 && ((level > wanted && AllVisibleChildrenCovered(key)) || !hasMesh))
        {
            bool anyChildDrew = false;
            for (int qz = 0; qz < 2; qz++)
            {
                for (int qx = 0; qx < 2; qx++)
                {
                    long ck = LodWorld.ChildKey(key, qx, qz);
                    if (world.HasDataSet.Contains(ck)) anyChildDrew |= CollectDrawNodes(ck);
                }
            }
            if (anyChildDrew || !hasMesh) return anyChildDrew;
        }

        if (hasMesh)
        {
            drawList.Add(key);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Refresh the live tint table and snow line (~every 4s). Each tint slot is sampled
    /// at two altitudes and interpolated per vertex, because the climate maps are keyed
    /// by temperature and temperature falls with height; the snow line extrapolates the
    /// same lapse rate to where it hits freezing.
    /// </summary>
    void RefreshSeasonalState()
    {
        if (frameCounter - lastSeasonRefreshFrame < 240) return;
        lastSeasonRefreshFrame = frameCounter;

        int px = (int)camPos.X;
        int pz = (int)camPos.Z;

        // Every registered colour-map pair at once: leaves are per species (oak turns
        // while pine stays green) and water has its own map, so one shared foliage tint
        // was never going to be right.
        tints.Refresh(capi.World, px, pz);

        try
        {
            int seaLevel = capi.World.SeaLevel;
            climatePos.Set(px, seaLevel, pz);
            ClimateCondition? low = capi.World.BlockAccessor.GetClimateAt(climatePos);
            climatePos.Set(px, seaLevel + 150, pz);
            ClimateCondition? high = capi.World.BlockAccessor.GetClimateAt(climatePos);

            if (low == null || high == null || low.Temperature <= high.Temperature)
            {
                snowLineY = 99999; // no usable lapse rate → snow line disabled
            }
            else
            {
                float lapsePerBlock = (low.Temperature - high.Temperature) / 150f;
                snowLineY = seaLevel + (low.Temperature - (-1f)) / lapsePerBlock;
                snowLineY = GameMath.Clamp(snowLineY, seaLevel - 64, 99999);
            }
        }
        catch
        {
            snowLineY = 99999;
        }
    }


    /// <summary>Demand-driven (re)meshing: the selection walk is the load queue (Voxy's idea, CPU-side).</summary>
    void RequestMesh(long key)
    {
        if (meshJobInFlight.Contains(key)) return;

        // RAM-evicted sections still count: HasDataSet says whether the subtree has
        // data at all; the scheduler reloads the row from disk when it picks the job.
        if (world.Sections.TryGetValue(key, out LodSection? section))
        {
            if (section.CapturedColumns == 0) return;
        }
        else if (!world.HasDataSet.Contains(key))
        {
            return;
        }

        world.RenderDirty.Add(key);
    }

    void EvictStaleMeshes()
    {
        if (frameCounter % EvictSweepInterval != 0) return;

        evictBatch.Clear();
        foreach ((long key, MeshRef _) in sectionMeshes)
        {
            if (ShouldEvictMesh(key))
            {
                evictBatch.Add(key);
            }
        }
        foreach ((long key, MeshRef _) in waterMeshes)
        {
            if (!sectionMeshes.ContainsKey(key)
                && ShouldEvictMesh(key))
            {
                evictBatch.Add(key);
            }
        }

        foreach (long key in evictBatch)
        {
            RemoveMeshes(key);
            lastResidencyFrame.Remove(key);
            EvictedTotal++;
        }
    }

    bool ShouldEvictMesh(long key)
    {
        if (LodTraversalPolicy.WithinResidencyBand(key, camPos.X, camPos.Z))
        {
            lastResidencyFrame[key] = frameCounter;
            return false;
        }

        return !lastResidencyFrame.TryGetValue(key, out long last)
            || frameCounter - last > EvictAfterFrames;
    }

    // ---- Mesh job scheduling + result upload ----

    /// <summary>
    /// Drop meaningless render-dirty entries: no live mesh AND finer than the level
    /// the walk wants there - meshing those wastes work. Entries at wanted level or
    /// COARSER must survive: they are draw targets or gate meshes the walk descends
    /// through (pruning gates stalls descent and freezes approached terrain at the
    /// coarse level it was first meshed at). New keys are indexed incrementally;
    /// existing priorities refresh only when the camera crosses a coarse cell.
    /// </summary>
    void PruneRenderDirty()
    {
        dirtyScheduler.Refresh(world.RenderDirty, camPos.X, camPos.Z,
            LodWorld.DetailDistance, keepRenderDirty);
    }

    bool KeepRenderDirty(long key) => HasAnyMesh(key)
        || LodWorld.KeyLevel(key) >= LodWorld.WantedLevelForSq(NearestDistanceSqTo(key));

    bool RenderDirtyBlocked(long key) => meshJobInFlight.Contains(key)
        || world.LoadsInFlight.Contains(key);

    void ScheduleMeshJobs()
    {
        if (world.RenderDirty.Count == 0 || worker.PendingMeshes >= maxWorkerMeshBacklog) return;

        // Two budgets. Starting a background reload costs this thread almost nothing
        // (enqueue a key), whereas building a mesh snapshot is real work, so charging a
        // reload against the mesh budget throttled join fill-in badly: every section
        // needed two passes to appear and only four could be touched per frame.
        int meshBudget = MeshSchedulesPerFrame;
        int loadBudget = MeshLoadRequestsPerFrame;
        var snapshotBudget = new LodDrainBudget(
            MeshSnapshotMaxBytesPerFrame, MeshSnapshotMaxMillisecondsPerFrame);

        // Empty/missing candidates charge neither work budget, so keep a separate hard
        // examination limit. Priority lookup itself stays independent of dirty-set size.
        int examinations = MeshSchedulesPerFrame + MeshLoadRequestsPerFrame;
        int priorityExaminationBudget = examinations + meshJobInFlight.Count + world.LoadsInFlight.Count;
        while (meshBudget > 0 && loadBudget > 0 && examinations-- > 0
            && dirtyScheduler.TryTake(world.RenderDirty, keepRenderDirty,
                renderDirtyBlocked, priorityExaminationBudget,
                out long best))
        {
            // Non-blocking: an evicted section starts a background reload and is
            // re-requested by the selection walk once it lands, rather than stalling
            // this frame on a decompress.
            if (!world.TryGetForRender(best, out LodSection section))
            {
                if (world.LoadsInFlight.Contains(best))
                {
                    loadBudget--; // a reload is now under way; the walk re-requests it
                }
                else
                {
                    RemoveMeshes(best);
                }
                continue;
            }

            if (section.CapturedColumns == 0)
            {
                RemoveMeshes(best);
                continue;
            }

            var neighborSections = new LodSection?[4];
            long estimatedBytes = SectionSnapshot.EstimateRetainedBytes(section);
            for (int d = 0; d < 4; d++)
            {
                long nk = LodWorld.NeighborKey(best, d == 0 ? -1 : d == 1 ? 1 : 0, d == 2 ? -1 : d == 3 ? 1 : 0);
                if (world.Sections.TryGetValue(nk, out LodSection? nb))
                {
                    neighborSections[d] = nb;
                    estimatedBytes = SectionSnapshot.SaturatingAdd(
                        estimatedBytes, SectionSnapshot.EstimateRetainedBytes(nb));
                }
            }

            // TryTake transferred the exact dirty obligation to us. If this frame has
            // spent its snapshot time/byte allowance, restore that obligation before
            // stopping. The first eligible snapshot always progresses even if oversized.
            if (!snapshotBudget.TryStart(estimatedBytes))
            {
                world.RenderDirty.Add(best);
                break;
            }

            var neighbors = new SectionSnapshot?[4];
            for (int d = 0; d < 4; d++)
            {
                if (neighborSections[d] != null) neighbors[d] = SectionSnapshot.Of(neighborSections[d]!);
            }

            meshBudget--;
            meshJobInFlight.Add(best);
            worker.EnqueueMesh(new MeshJob
            {
                Key = best,
                Self = SectionSnapshot.Of(section),
                Neighbors = neighbors,
                EstimatedRetainedBytes = estimatedBytes,
                ReadyAtMilliseconds = Environment.TickCount64,
            });
        }


        MeshSnapshotItems += snapshotBudget.Items;
        MeshSnapshotBytes = SectionSnapshot.SaturatingAdd(
            MeshSnapshotBytes, snapshotBudget.Bytes);
    }

    void UploadFinishedMeshes()
    {
        int itemBudget = MeshUploadsPerFrame;
        var uploadBudget = new LodDrainBudget(
            MeshUploadMaxBytesPerFrame, MeshUploadMaxMillisecondsPerFrame);
        while (itemBudget-- > 0
            && worker.MeshResults.TryPeek(out MeshResult? waiting)
            && uploadBudget.TryStart(waiting.EstimatedUploadBytes)
            && worker.MeshResults.TryDequeue(out MeshResult? result))
        {
            meshJobInFlight.Remove(result.Key);

            bool hadMesh = HasAnyMesh(result.Key);
            MeshRef? newOpaque = null;
            MeshRef? newWater = null;
            try
            {
                if (result.IndexCount > 0)
                {
                    newOpaque = Upload(result.Xyz, result.Rgba, result.Indices,
                        result.VertexCount, result.IndexCount);
                }

                if (result.WaterIndexCount > 0 && result.WaterXyz != null)
                {
                    newWater = Upload(result.WaterXyz, result.WaterRgba!, result.WaterIndices!,
                        result.WaterVertexCount, result.WaterIndexCount);
                }
            }
            catch
            {
                // A partial replacement is not a replacement. Keep the old opaque/water
                // pair live, free anything newly created, and restore the dirty obligation.
                DisposeMeshRef(newOpaque);
                DisposeMeshRef(newWater);
                world.RenderDirty.Add(result.Key);
                throw;
            }

            ReplaceMeshRefs(result.Key, newOpaque, newWater);

            bool hasMesh = HasAnyMesh(result.Key);
            if (!hadMesh && hasMesh) meshBounds.Include(result.Key);
            else if (hadMesh && !hasMesh) meshBounds.Remove(result.Key);

            // Fresh uploads get an age grace period. Later retention depends on distance,
            // never on whether the current camera happens to see the mesh.
            lastResidencyFrame[result.Key] = frameCounter;
        }

        MeshUploadItems += uploadBudget.Items;
    }

    void RemoveMeshes(long key)
    {
        if (!HasAnyMesh(key)) return;
        DisposeMeshRefs(key);
        meshBounds.Remove(key);
    }

    void DisposeMeshRefs(long key)
    {
        if (sectionMeshes.Remove(key, out MeshRef? mesh)) DisposeMeshRef(mesh);
        if (waterMeshes.Remove(key, out MeshRef? water)) DisposeMeshRef(water);
    }

    void ReplaceMeshRefs(long key, MeshRef? newOpaque, MeshRef? newWater)
    {
        sectionMeshes.TryGetValue(key, out MeshRef? oldOpaque);
        waterMeshes.TryGetValue(key, out MeshRef? oldWater);

        if (newOpaque != null) sectionMeshes[key] = newOpaque;
        else sectionMeshes.Remove(key);
        if (newWater != null) waterMeshes[key] = newWater;
        else waterMeshes.Remove(key);

        // Publish the complete replacement first. If an engine-side disposal ever
        // throws, the new mesh pair is still the live pair and will not leak or vanish.
        DisposeMeshRef(oldOpaque);
        DisposeMeshRef(oldWater);
    }

    void DisposeMeshRef(MeshRef? mesh)
    {
        if (mesh == null) return;
        LodPhaseStart phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        try
        {
            mesh.Dispose();
        }
        finally
        {
            MeshDisposeCost.Add(phaseStart);
        }
    }

    MeshRef Upload(float[] xyz, byte[] rgba, int[] indices, int vertCount, int indexCount)
    {
        // What UploadMesh is asked to transfer, not backing-array capacity. Mesher pools
        // can deliberately hand back arrays larger than the live vertex/index counts.
        MeshUploadBytes = SectionSnapshot.SaturatingAdd(
            MeshUploadBytes, vertCount * 16L + indexCount * sizeof(int));
        var mesh = new MeshData(false);
        mesh.SetVerticesCount(vertCount);
        mesh.SetIndicesCount(indexCount);
        mesh.xyz = xyz;
        mesh.Rgba = rgba;
        mesh.Indices = indices;
        LodPhaseStart phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        try
        {
            return capi.Render.UploadMesh(mesh);
        }
        finally
        {
            GlUploadCost.Add(phaseStart);
        }
    }

    // ---- Frame ----

    float ApprovedViewDistance()
    {
        var playerData = capi.World.Player.WorldData;
        float distance = playerData.DesiredViewDistance;
        if (playerData.LastApprovedViewDistance > 0)
            distance = Math.Min(distance, playerData.LastApprovedViewDistance);
        return Math.Max(0, distance);
    }

    void UpdateReadinessShadow(float viewDistance)
    {
        if (readinessDisabled) return;
        if (capi.World.Player.Entity.Pos.Dimension != 0)
        {
            readiness?.Clear();
            readinessSeedCursor = readinessBoundaryCursor = readinessInteriorCursor = 0;
            readinessOwnsHandoff = false;
            // Ownership belongs to the world it was measured in. Leaving the default
            // dimension must return every cell to the cache rather than keep suppressing
            // terrain with another world's readiness.
            readinessMask?.Clear();
            readinessMaskActive = false;
            return;
        }

        try
        {
            var blockAccessor = capi.World.BlockAccessor;
            int verticalChunks = Math.Max(1, (blockAccessor.MapSizeY + 31) / 32);
            VanillaReadinessWindow window = VanillaRenderReadiness.CalculateWindow(
                camPos.X, camPos.Z, viewDistance,
                blockAccessor.MapSizeX, blockAccessor.MapSizeZ, ReadinessGuardChunks);

            if (readiness == null || readiness.VerticalChunks != verticalChunks
                || readiness.HorizontalCapacity < window.RequiredCapacity)
            {
                readiness = new VanillaRenderReadiness(window.RequiredCapacity, verticalChunks);
                readinessSeedCursor = readinessBoundaryCursor = readinessInteriorCursor = 0;
                readinessHandoffStale = true;
                ReadinessResizes++;
                if (chunkMaskRequested && !readinessMaskFailed)
                {
                    readinessMask = new VanillaReadinessMask(window.RequiredCapacity, verticalChunks);
                    DisposeReadinessMaskTexture();
                }
            }

            if (chunkMaskRequested && !readinessMaskFailed && readinessMask == null)
            {
                readinessMask = new VanillaReadinessMask(
                    readiness.HorizontalCapacity, readiness.VerticalChunks);
                readiness.WriteMask(readinessMask);
            }

            if (readiness.SetWindow(window.MinX, window.MinZ, window.Width, window.Depth,
                frameCounter))
            {
                // Deliberately not resetting the discovery cursor. Restarting it on every
                // chunk crossing meant that during flight it swept the same near cells over
                // and over and never reached the frontier, which SetWindow now queues
                // directly. The boundary and interior cursors are cheap ring scans and
                // may restart.
                // The boundary cursor may restart because the whole shell is swept below on
                // this same frame. The interior cursor may not: it is the safety net that
                // eventually revisits every cell, one slice per frame, and restarting it on
                // every chunk crossing meant that while flying it never advanced past its
                // first slice - so stale ownership behind the camera was never revisited by
                // the one sweep guaranteed to reach it.
                readinessBoundaryCursor = 0;
                readinessHandoffStale = true;
                readinessSweepShellNow = true;
                ReadinessWindowChanges++;
                // Departing columns were cleared inside the tracker; rebuilding is what
                // keeps a reused ring slot from carrying another place's ownership.
                if (readinessMask != null) readiness.WriteMask(readinessMask);
            }

            readiness.SeedUnknownCells(
                ref readinessSeedCursor, ReadinessSeedCellsPerFrame, frameCounter);
            readiness.QueueReadyShell(window.CenterX, window.CenterZ,
                Math.Max(0, window.VanillaRadius - ReadinessGuardChunks), window.OuterRadius,
                ref readinessBoundaryCursor,
                readinessSweepShellNow
                    ? ReadinessBoundarySweepOnCrossing
                    : ReadinessBoundaryCellsPerFrame,
                frameCounter);
            readinessSweepShellNow = false;
            readiness.QueueMaintenanceCells(
                ref readinessInteriorCursor, ReadinessInteriorCellsPerFrame, frameCounter);

            long now = capi.ElapsedMilliseconds;
            if (now - readinessLastFullRevalidateMs >= ReadinessFullRevalidateMilliseconds)
            {
                readinessLastFullRevalidateMs = now;
                ReadinessFullRevalidations++;
                readiness.QueueAllReadyCells(frameCounter);
            }
            readiness.PromoteObserved(frameCounter, ReadinessProbeCatchUpItemsPerFrame);

            bool catchingUp = readiness.PendingCandidates >= ReadinessProbeCatchUpQueueDepth;
            int maxProbes = catchingUp
                ? ReadinessProbeCatchUpItemsPerFrame
                : ReadinessProbeMaxItemsPerFrame;
            double maxMilliseconds = catchingUp
                ? ReadinessProbeCatchUpMillisecondsPerFrame
                : ReadinessProbeMaxMillisecondsPerFrame;

            long started = Stopwatch.GetTimestamp();
            long maxTicks = Math.Max(1,
                (long)Math.Ceiling(maxMilliseconds * Stopwatch.Frequency / 1000.0));
            int probed = 0;
            while (probed < maxProbes)
            {
                if (probed > 0 && Stopwatch.GetTimestamp() - started >= maxTicks) break;
                if (!readiness.TryDequeueCandidate(out VanillaChunkCell cell)) break;

                bool rendered;
                try
                {
                    readinessProbePos.Dimension = 0;
                    readinessProbePos.SetPos(
                        (cell.X + 0.5) * 32.0,
                        (cell.Y + 0.5) * 32.0,
                        (cell.Z + 0.5) * 32.0);
                    rendered = capi.IsChunkRendered(readinessProbePos);
                }
                catch
                {
                    // An unknown/error result must restore or retain cached ownership.
                    rendered = false;
                    ReadinessProbeErrors++;
                }

                probed++;
                ReadinessProbes++;
                if (rendered) ReadinessTrueResults++;
                else ReadinessFalseResults++;

                if (!readiness.Observe(cell, rendered, frameCounter,
                    out VanillaReadinessPublication publication)) continue;

                // Phase 1 has no texture. Accepting into this shadow model lets diagnostics
                // validate aggregate transitions without changing any pixel or draw call.
                if (!readiness.ResolvePublication(publication, accepted: true)) continue;
                readinessMask?.Set(publication.Cell, publication.Ready);
                readinessHandoffStale = true;
                if (publication.Ready) ReadinessReadyTransitions++;
                else ReadinessLostTransitions++;
            }

            // Ownership the tracker can actually prove, converted into the radius the
            // fragment shader already understands. Uncertainty anywhere - an unowned
            // column, an untracked window edge, an unconverged join - pulls this in and
            // returns coverage to the cache rather than opening a hole.
            //
            // The measurement only changes when committed ownership changes or the camera
            // moves, so a settled view reuses it instead of rescanning every column every
            // frame. Both triggers are conservative: either one rescans.
            if (readinessHandoffStale
                || Math.Abs(camPos.X - readinessHandoffCameraX) >= ReadinessHandoffRecheckBlocks
                || Math.Abs(camPos.Z - readinessHandoffCameraZ) >= ReadinessHandoffRecheckBlocks)
            {
                double nearestIncomplete = readiness.NearestIncompleteColumnBlocks(
                    camPos.X, camPos.Z, out _);
                readinessOwnedRadius =
                    (float)Math.Max(0, nearestIncomplete - ReadinessHandoffMarginBlocks);
                readinessHandoffCameraX = camPos.X;
                readinessHandoffCameraZ = camPos.Z;
                readinessHandoffStale = false;
            }

            readinessHandoffDistance = nearHandoff.Update(
                Math.Min(readinessOwnedRadius, viewDistance), capi.ElapsedMilliseconds);
            readinessOwnsHandoff = true;
            PublishReadinessMask();
        }
        catch (Exception e)
        {
            readiness?.Clear();
            readiness = null;
            readinessDisabled = true;
            readinessOwnsHandoff = false;
            DisableReadinessMask();
            if (!readinessFailureReported)
            {
                readinessFailureReported = true;
                capi.Logger.Warning(
                    "[VintageHorizons] vanilla-readiness shadow tracker disabled; radial handoff remains active: {0}",
                    e.Message);
            }
        }
    }

    /// <summary>
    /// Per-cell ownership on or off. Turning it off restores the measured handoff radius
    /// immediately and releases the texture; turning it on rebuilds the mask from state the
    /// tracker already holds, so neither direction waits for readiness to reconverge.
    /// A failed mask stays off: the failure disabled it for a reason.
    /// </summary>
    public bool ChunkMaskEnabled
    {
        get => chunkMaskRequested && !readinessMaskFailed;
        set
        {
            chunkMaskRequested = value;
            if (value) return;
            readinessMask = null;
            DisposeReadinessMaskTexture();
        }
    }

    public bool ChunkMaskFailed => readinessMaskFailed;

    /// <summary>
    /// Walks the line of sight and reports the first cell whose ownership would produce a
    /// hole, with the distance it was found at. Asking about one fixed distance was not
    /// usable: the hole a player is looking at is wherever it happens to be, and a report
    /// about some other chunk looks identical to a report about the right one.
    ///
    /// Sampling stops at the first cell the mask suppresses while the engine says it is not
    /// drawing that chunk, because that is the combination that leaves nothing on screen.
    /// If the ray finds no such cell, the first place with no cached section is reported
    /// instead, which is the other way a hole appears and is not the mask's doing.
    /// </summary>
    public string ExplainViewRay(double startX, double startY, double startZ,
        float lookX, float lookY, float lookZ, int maxBlocks)
    {
        if (readiness == null) return "no readiness tracker; the distance handoff is drawing";

        var previous = new VanillaChunkCell(int.MinValue, int.MinValue, int.MinValue);
        string? firstEmpty = null;
        int examined = 0;
        int verticalChunks = Math.Max(1, (worldHeight + 31) / 32);

        for (int distance = 0; distance <= maxBlocks && examined < 512; distance += 4)
        {
            double x = startX + lookX * distance;
            double y = startY + lookY * distance;
            double z = startZ + lookZ * distance;
            if (y < 0 || y >= worldHeight) break;

            var cell = new VanillaChunkCell(
                VanillaRenderReadiness.ChunkCoordinate(x),
                VanillaRenderReadiness.ChunkCoordinate(y),
                VanillaRenderReadiness.ChunkCoordinate(z));
            if (cell == previous) continue;
            previous = cell;
            examined++;
            if (cell.Y < 0 || cell.Y >= verticalChunks) continue;

            bool rendered;
            try
            {
                readinessProbePos.Dimension = 0;
                readinessProbePos.SetPos((cell.X + 0.5) * 32.0, (cell.Y + 0.5) * 32.0, (cell.Z + 0.5) * 32.0);
                rendered = capi.IsChunkRendered(readinessProbePos);
            }
            catch
            {
                rendered = false;
            }

            bool suppressed = readinessMaskActive && readinessMask != null && readinessMask.IsReady(cell);

            // Ground can legitimately be drawn by a coarser ancestor, so asking only the
            // finest level reports "no mesh" for terrain that is on screen. Walk up until
            // something covers this spot, and report the level that actually would draw it.
            bool resident = false;
            bool meshed = false;
            int drawnLevel = -1;
            for (int level = 0; level <= LodWorld.MaxLevel; level++)
            {
                int footprint = LodWorld.KeyFootprintBlocks(LodWorld.SectionKey(level, 0, 0));
                long key = LodWorld.SectionKey(level,
                    (int)Math.Floor(x / footprint), (int)Math.Floor(z / footprint));
                resident |= world.Sections.ContainsKey(key);
                if (!HasAnyMesh(key)) continue;
                meshed = true;
                drawnLevel = level;
                break;
            }

            if (suppressed && !rendered)
            {
                return $"STALE OWNERSHIP {distance} blocks out, chunk {cell.X},{cell.Y},{cell.Z}: "
                    + $"the mask hides cached terrain here but the engine is not drawing this chunk. "
                    + $"We say {readiness.State(cell)}. Cached terrain is "
                    + $"{(resident ? "resident" : "absent")} and "
                    + $"{(meshed ? $"meshed at level {drawnLevel}" : "unmeshed at every level")}. "
                    + "This one is ours.";
            }

            if (firstEmpty == null && !rendered && !resident)
            {
                firstEmpty = $"no terrain of either kind {distance} blocks out, chunk "
                    + $"{cell.X},{cell.Y},{cell.Z}: the engine is not drawing this chunk and no cached "
                    + "section is held for it, so there is nothing to show. The mask is not involved; "
                    + "this ground was never captured.";
            }
        }

        if (firstEmpty != null) return firstEmpty;
        return $"nothing wrong along {maxBlocks} blocks of view: every chunk sampled is either "
            + "drawn by the engine or backed by cached terrain that is allowed to draw.";
    }

    /// <summary>
    /// What the mod believes about the ground at a world position, against what the client
    /// says right now. Written for standing in front of a hole and asking why it is there:
    /// if ownership reads committed while the engine reports the chunk unrendered, the mask
    /// is suppressing cached terrain nothing else draws, and that is ours. If ownership is
    /// already cache-owned, the hole is missing cached data or mesh, and the mask is not
    /// involved.
    /// </summary>
    public string DescribeOwnershipAt(double worldX, double worldY, double worldZ)
    {
        if (readiness == null) return "no readiness tracker";

        var cell = new VanillaChunkCell(
            VanillaRenderReadiness.ChunkCoordinate(worldX),
            VanillaRenderReadiness.ChunkCoordinate(worldY),
            VanillaRenderReadiness.ChunkCoordinate(worldZ));

        bool renderedNow;
        try
        {
            readinessProbePos.Dimension = 0;
            readinessProbePos.SetPos((cell.X + 0.5) * 32.0, (cell.Y + 0.5) * 32.0, (cell.Z + 0.5) * 32.0);
            renderedNow = capi.IsChunkRendered(readinessProbePos);
        }
        catch
        {
            renderedNow = false;
        }

        VanillaReadinessState state = readiness.State(cell);
        bool suppressed = readinessMaskActive && readinessMask != null && readinessMask.IsReady(cell);
        long sectionKey = LodWorld.SectionKey(0,
            (int)Math.Floor(worldX / LodSection.SectionBlocks),
            (int)Math.Floor(worldZ / LodSection.SectionBlocks));

        return $"chunk {cell.X},{cell.Y},{cell.Z}: we say {state}, engine says "
            + $"{(renderedNow ? "rendered" : "not rendered")}, mask {(suppressed ? "suppresses" : "allows")} "
            + $"cached terrain here; L0 section {(world.Sections.ContainsKey(sectionKey) ? "resident" : "absent")}, "
            + $"{(HasAnyMesh(sectionKey) ? "meshed" : "no mesh")}"
            + (state == VanillaReadinessState.VanillaReady && !renderedNow
                ? " -- STALE OWNERSHIP: the mask is hiding cached terrain the engine is not drawing"
                : "");
    }

    /// <summary>
    /// Uploads the whole mask when it differs from what the GPU holds. The public client
    /// API has no subregion update, so partial changes still cost a full upload; coalescing
    /// to one upload per frame is what keeps that affordable. A failed upload disables the
    /// mask outright and leaves the measured radius as the only ownership decision, because
    /// a stale mask would suppress cached terrain vanilla has not replaced.
    /// </summary>
    void PublishReadinessMask()
    {
        if (readinessMask == null || readinessMaskFailed) return;
        if (!readinessMask.Dirty && readinessMaskActive) return;

        long started = Stopwatch.GetTimestamp();
        try
        {
            // The client creates when TextureId is 0 or the pixel count disagrees with the
            // stored size, and otherwise does a plain TexSubImage2D of the same extent, so
            // a correctly sized LoadedTexture makes one call cover both. Mipmaps are built
            // on the creation branch only, and texelFetch ignores filtering regardless.
            LoadedTexture texture = readinessMaskTexture
                ?? new LoadedTexture(capi, 0, readinessMask.Width, readinessMask.Height);
            capi.Render.LoadOrUpdateTextureFromBgra(readinessMask.Texels,
                linearMag: false, clampMode: 0, ref texture);
            if (texture.TextureId <= 0)
                throw new InvalidOperationException("Readiness mask texture was not created.");
            readinessMaskTexture = texture;

            readinessMask.MarkUploaded();
            readinessMaskActive = true;
        }
        catch (Exception e)
        {
            DisableReadinessMask();
            capi.Logger.Warning(
                "[VintageHorizons] chunk readiness mask disabled; the measured handoff radius remains: {0}",
                e.Message);
        }
        finally
        {
            readinessMaskUploadUs =
                (Stopwatch.GetTimestamp() - started) * 1_000_000 / Stopwatch.Frequency;
        }
    }

    /// <summary>
    /// The near-field floor described on <see cref="MaskNearFloorBlocks"/>. Zero unless the
    /// camera's own ownership cell is committed ready, so uncertainty still fails toward
    /// cached coverage.
    /// </summary>
    float MaskNearFloor()
    {
        if (readiness == null) return 0f;

        var cell = new VanillaChunkCell(
            VanillaRenderReadiness.ChunkCoordinate(camPos.X),
            VanillaRenderReadiness.ChunkCoordinate(camPos.Y),
            VanillaRenderReadiness.ChunkCoordinate(camPos.Z));
        return readiness.State(cell) == VanillaReadinessState.VanillaReady
            ? MaskNearFloorBlocks
            : 0f;
    }

    void DisableReadinessMask()
    {
        readinessMaskFailed = true;
        readinessMaskActive = false;
        readinessMask = null;
        DisposeReadinessMaskTexture();
    }

    void DisposeReadinessMaskTexture()
    {
        if (readinessMaskTexture == null) return;
        try { capi.Render.GLDeleteTexture(readinessMaskTexture.TextureId); }
        catch { /* teardown must not throw over a texture the driver already released */ }
        readinessMaskTexture = null;
        readinessMaskActive = false;
    }

    public string DescribeReadiness()
    {
        if (readinessDisabled) return "shadow disabled (radial fallback active)";
        if (readiness == null) return "shadow not initialized";
        readiness.GetStateCounts(out int unknown, out int pending, out int observed, out int ready);
        long candidateFrame = readiness.OldestCandidateFrame();
        long observedFrame = readiness.OldestObservedFrame();
        long oldestAge = Math.Max(
            candidateFrame < 0 ? 0 : frameCounter - candidateFrame,
            observedFrame < 0 ? 0 : frameCounter - observedFrame);
        if (readinessColumnReady.Length < readiness.VerticalChunks)
            readinessColumnReady = new int[readiness.VerticalChunks];
        readiness.GetColumnReadiness(readinessColumnReady, out int columnsTracked,
            out int fullColumns, out int partialColumns, out int maxReadyPerColumn);
        readinessColumnText.Clear();
        for (int y = 0; y < readiness.VerticalChunks; y++)
        {
            if (y > 0) readinessColumnText.Append('/');
            readinessColumnText.Append(readinessColumnReady[y]);
        }

        double nearestIncomplete = readiness.NearestIncompleteColumnBlocks(
            camPos.X, camPos.Z, out double nearestUnready);
        // Kept out of the format literal: an interpolation hole containing a quoted string
        // is not machine-readable, and the benchmark gate parses this exact line.
        float radialHandoff = LodNearHandoff.InnerDiscardRadius(ApprovedViewDistance());
        // Report what the shader actually receives. A live mask drives the radius to zero,
        // and printing the radius it would otherwise have used reads as suppression that
        // is not happening.
        bool maskOwnsPixels = readinessMaskActive && readinessMask != null;
        float appliedHandoff = maskOwnsPixels ? 0f
            : readinessOwnsHandoff ? readinessHandoffDistance : radialHandoff;
        string handoffSource = maskOwnsPixels ? "mask"
            : readinessOwnsHandoff ? "readiness" : "radial";
        string maskState = readinessMaskFailed ? "failed"
            : readinessMask == null ? "off"
            : readinessMaskActive
                ? $"{readinessMask.ReadyTexels} owned/{readinessMask.Bytes / 1024} KiB/"
                  + $"{readinessMask.Uploads} uploads/last {readinessMaskUploadUs}us"
                : "pending";

        return $"shadow {readiness.ActiveWidth}x{readiness.VerticalChunks}x{readiness.ActiveDepth}, "
            + $"{ready} ready/{observed} observed/{pending} pending/{unknown} unknown, "
            + $"{readiness.PendingCandidates} probes/{readiness.PendingObservations} observations queued, "
            + $"oldest work {oldestAge} frames, {readiness.TrackedArrayBytes / 1024.0:0.0} KiB arrays, "
            + $"interval {ReadinessProbes} probes ({ReadinessTrueResults} true/{ReadinessFalseResults} false), "
            + $"{ReadinessReadyTransitions} ready/{ReadinessLostTransitions} lost transitions, "
            + $"{ReadinessProbeErrors} errors, {ReadinessWindowChanges} window changes/{ReadinessResizes} resizes, "
            + $"{ReadinessFullRevalidations} full revalidations, "
            + $"events {readiness.CandidateEventsAccepted} accepted/{readiness.CandidateEventsCoalesced} coalesced/"
            + $"{readiness.CandidateEventsDropped} dropped, "
            + $"sweeps {readiness.ScheduledCandidatesAccepted} accepted/"
            + $"{readiness.ScheduledCandidatesCoalesced} coalesced, "
            + $"columns {columnsTracked} tracked/{fullColumns} full/{partialColumns} partial, "
            + $"max {maxReadyPerColumn}/{readiness.VerticalChunks} ready per column, "
            + $"ready per Y {readinessColumnText}, "
            + $"nearest incomplete {nearestIncomplete:0} blocks/unready {nearestUnready:0} blocks, "
            + $"handoff {appliedHandoff:0} blocks ({handoffSource}, radial {radialHandoff:0}), "
            + $"mask {maskState}, {VanillaOwnedDrawsSkipped} owned draws skipped";
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (AutoUnpause && capi.IsGamePaused) capi.PauseGame(false);

        var rapi = capi.Render;
        if (rapi.FrameWidth == 0) return;

        camPos = capi.World.Player.Entity.CameraPos;
        frameCounter++;
        float viewDistance = ApprovedViewDistance();

        LodPhaseStart phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        UpdateReadinessShadow(viewDistance);
        ReadinessCost.Add(phaseStart);

        if (prog == null || !shaderOk || prog.LoadError) return;

        // Timed apart: pruning/index refresh is normally incremental but deliberately
        // rebuilds when the camera crosses a coarse cell; scheduling then consumes a
        // bounded nearest-first queue without scanning the complete dirty set.
        phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        PruneRenderDirty();
        PruneCost.Add(phaseStart);

        phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        ScheduleMeshJobs();
        ScheduleCost.Add(phaseStart);

        phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        UploadFinishedMeshes();
        UploadCost.Add(phaseStart);

        phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        EvictStaleMeshes();
        EvictCost.Add(phaseStart);

        phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        RefreshSeasonalState();
        SeasonalCost.Add(phaseStart);
        if (sectionMeshes.Count == 0 && waterMeshes.Count == 0) return;

        phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        UpdateEffectiveFarDistance(viewDistance);
        FarDistanceCost.Add(phaseStart);
        ApplyZFar();

        // The traversal and draw cull use the exact matrices handed to the shader. This
        // must happen after ApplyZFar, because that can rebuild the projection matrix.
        worldHeight = capi.World.BlockAccessor.MapSizeY;
        frustum.Update(rapi.CurrentProjectionMatrix, rapi.CameraMatrixOriginf);
        traversalCulledThisFrame = 0;
        culledThisFrame = 0;

        phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        drawList.Clear();
        foreach (long top in world.TopLevelKeys) CollectDrawNodes(top);
        WalkCost.Add(phaseStart);
        LastDrawCount = drawList.Count;
        LastTraversalCulledCount = traversalCulledThisFrame;
        if (drawList.Count == 0)
        {
            LastCulledCount = 0;
            return;
        }

        prog.Use();
        rapi.GlDisableCullFace();

        prog.UniformMatrix("viewMatrix", rapi.CameraMatrixOriginf);
        prog.UniformMatrix("projectionMatrix", rapi.CurrentProjectionMatrix);

        prog.Uniform("sunPosition", capi.World.Calendar.SunPositionNormalized);
        prog.Uniform("sunColor", capi.World.Calendar.SunColor);
        prog.Uniform("dayLight", Math.Max(0, capi.World.Calendar.DayLightStrength));

        prog.Uniform("rgbaFogIn", capi.Ambient.BlendedFogColor);
        prog.Uniform("fogDensityIn", capi.Ambient.BlendedFogDensity);
        prog.Uniform("fogMinIn", capi.Ambient.BlendedFogMin);
        prog.Uniform("horizonFog", capi.Ambient.BlendedCloudDensity);

        prog.Uniform("viewDistance", viewDistance);
        prog.Uniform("farViewDistance", EffectiveFarDistance);
        // Per-cell ownership supersedes the radius entirely: the mask knows which
        // individual cells vanilla owns, so leaving a radius active would suppress cached
        // terrain the mask says is still ours. With the mask off, the measured radius (or
        // the established constant) remains the only ownership decision.
        bool maskOwnsPixels = readinessMaskActive && readinessMask != null;
        prog.Uniform("cacheHandoffDistance", maskOwnsPixels
            ? MaskNearFloor()
            : readinessOwnsHandoff
                ? readinessHandoffDistance
                : LodNearHandoff.InnerDiscardRadius(viewDistance));
        prog.Uniform("maskEnabled", maskOwnsPixels ? 1 : 0);
        if (maskOwnsPixels && readiness != null && readinessMaskTexture != null)
        {
            prog.Uniform("maskMinX", readiness.ActiveMinChunkX);
            prog.Uniform("maskMinZ", readiness.ActiveMinChunkZ);
            prog.Uniform("maskWidth", readiness.ActiveWidth);
            prog.Uniform("maskDepth", readiness.ActiveDepth);
            prog.Uniform("maskCapacity", readinessMask!.Width);
            prog.Uniform("maskVerticalChunks", readinessMask.VerticalChunks);
            // Unit 6 keeps clear of the sampler slots the shared includes already use for
            // glow, sky, liquid depth, and the two shadow maps.
            prog.BindTexture2D("readinessMask", readinessMaskTexture.TextureId, 6);
        }

        // Uniforms persist in the program between Use() calls, so re-upload only when
        // the table actually changed (every ~240 frames) rather than every frame.
        if (uploadedTintVersion != tints.Version)
        {
            uploadedTintVersion = tints.Version;
            prog.Uniforms4("tintsLow", LodTintRegistry.MaxSlots, tints.TintsLow);
            prog.Uniforms4("tintsHigh", LodTintRegistry.MaxSlots, tints.TintsHigh);
            prog.Uniform("tintYLow", tints.SampleYLow);
            prog.Uniform("tintYHigh", tints.SampleYHigh);
        }
        prog.Uniform("snowLineY", snowLineY);

        float cullDistSq = float.MaxValue;
        if (FarViewDistanceCap > 0)
        {
            float cull = FarViewDistanceCap + LodSection.SectionBlocks;
            cullDistSq = cull * cull;
        }

        phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);

        // Pass 1: opaque terrain.
        foreach (long key in drawList)
        {
            if (!sectionMeshes.TryGetValue(key, out MeshRef? mesh)) continue;
            if (SkipVanillaOwnedSection(key)) continue;
            if (!SetupSectionTransform(key, cullDistSq)) continue;
            capi.Render.RenderMesh(mesh);
        }

        LastCulledCount = culledThisFrame; // opaque pass only: water covers a subset

        // Pass 2: water, alpha-blended over the terrain.
        rapi.GlToggleBlend(true);
        foreach (long key in drawList)
        {
            if (!waterMeshes.TryGetValue(key, out MeshRef? mesh)) continue;
            if (SkipVanillaOwnedSection(key)) continue;
            if (!SetupSectionTransform(key, cullDistSq)) continue;
            capi.Render.RenderMesh(mesh);
        }
        rapi.GlToggleBlend(false);

        // Submission only. RenderMesh queues work for the GPU and returns, so this
        // measures the CPU cost of the draw loop -- the uniform uploads, the culling and
        // the dictionary probes -- and not what the GPU then does with it.
        DrawCost.Add(phaseStart);

        rapi.GlEnableCullFace();
        prog.Stop();
    }

    /// <summary>
    /// True when every ownership cell this section covers is committed vanilla-ready, so
    /// the mask would discard all of its fragments anyway. Skipping here removes the
    /// uniform uploads, the transform and the draw call itself, which is the only part of
    /// this design that saves CPU rather than shading.
    ///
    /// It requires the mask to be live: the aggregate and the texture are committed by the
    /// same publication, so a skip can never run ahead of what the GPU would have drawn.
    /// Residency is deliberately untouched - the mesh stays resident and warm so vanilla
    /// unloading restores cached coverage without a reload or a remesh.
    /// </summary>
    bool SkipVanillaOwnedSection(long key)
    {
        if (!readinessMaskActive || readiness == null) return false;
        if (readiness.Classify(key) != VanillaSectionOwnership.VanillaOnly) return false;
        VanillaOwnedDrawsSkipped++;
        return true;
    }

    bool SetupSectionTransform(long key, float cullDistSq)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        double originX = LodWorld.KeySx(key) * (double)footprint;
        double originZ = LodWorld.KeySz(key) * (double)footprint;

        double dx = originX + footprint / 2.0 - camPos.X;
        double dz = originZ + footprint / 2.0 - camPos.Z;
        if (dx * dx + dz * dz > cullDistSq) return false;

        // Camera-relative box, matching the model matrix below. Y spans the whole
        // world: sections don't track their vertical extent, and the wins that matter
        // (sections behind or beside the camera) come from the side planes anyway.
        double relX = originX - camPos.X;
        double relZ = originZ - camPos.Z;
        if (!frustum.BoxInView(relX, -camPos.Y, relZ, relX + footprint, worldHeight - camPos.Y, relZ + footprint))
        {
            culledThisFrame++;
            return false;
        }

        modelMat.Identity().Translate(relX, -camPos.Y, relZ);
        prog!.UniformMatrix("modelMatrix", modelMat.Values);
        prog.Uniform("columnBlocks", (float)LodWorld.ColumnStepBlocks(LodWorld.KeyLevel(key)));

        // Projection/fog use camera-relative coordinates, but colour noise needs the
        // section's stable world origin or a fixed patch changes colour while flying.
        // This uniform does not affect geometry, so float precision at extreme world
        // coordinates can only soften the cosmetic variation, never move terrain.
        prog.Uniform("noiseOrigin", (float)originX, (float)originZ, 0f, 0f);

        // Ownership addressing uses this integer origin plus the section-local position,
        // never a summed world coordinate, so a fragment at a chunk edge cannot round onto
        // its neighbour's ownership at large world coordinates.
        if (readinessMaskActive)
        {
            prog.Uniform("maskSectionOrigin", new Vec2i(
                (int)(originX / VanillaReadinessMask.ChunkBlocks),
                (int)(originZ / VanillaReadinessMask.ChunkBlocks)));
        }

        // Sides that border on never-captured area, so the shader can dissolve them
        // into the horizon instead of leaving a cliff at the edge of what we've seen.
        prog.Uniform("sectionSize", (float)footprint);
        prog.Uniform("openEdges",
            HasNeighbourData(key, -1, 0) ? 0f : 1f,
            HasNeighbourData(key, 1, 0) ? 0f : 1f,
            HasNeighbourData(key, 0, -1) ? 0f : 1f,
            HasNeighbourData(key, 0, 1) ? 0f : 1f);
        return true;
    }

    int culledThisFrame;
    int traversalCulledThisFrame;

    /// <summary>
    /// Whether the neighbouring section holds (or covers) data. Checked at the drawn
    /// section's own level: a coarse section's neighbour is coarse too, and its
    /// presence in HasDataSet means something in that subtree was captured.
    /// </summary>
    bool HasNeighbourData(long key, int dx, int dz) =>
        world.HasDataSet.Contains(LodWorld.NeighborKey(key, dx, dz));

    public void ClearMeshes()
    {
        foreach (MeshRef meshRef in sectionMeshes.Values) meshRef.Dispose();
        foreach (MeshRef meshRef in waterMeshes.Values) meshRef.Dispose();
        sectionMeshes.Clear();
        waterMeshes.Clear();
        meshBounds.Clear();
        farPlaneState.Reset();
        appliedZFar = 0;
        EffectiveFarDistance = LodFarDistance.MinimumProjectionDistance;
        meshJobInFlight.Clear();
        lastResidencyFrame.Clear();
        ClearReadiness();
    }

    void ClearReadiness()
    {
        readiness?.Clear();
        readiness = null;
        readinessSeedCursor = readinessBoundaryCursor = readinessInteriorCursor = 0;
        readinessDisabled = false;
        readinessFailureReported = false;
    }

    public void Dispose()
    {
        // Our own resources first. UnregisterRenderer refuses to run off the main thread,
        // and the game's shutdown crash path disposes mods from another one, so putting
        // the engine call first meant a crashing client freed none of its GPU meshes.
        ClearMeshes();
        // Same reason as the meshes: the mask texture is ours, and a shutdown that never
        // reaches the engine call must still not leak it.
        DisposeReadinessMaskTexture();
        capi.Event.ChunkDirty -= OnReadinessChunkDirty;
        capi.Event.ReloadShader -= LoadShader;
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
    }
}
