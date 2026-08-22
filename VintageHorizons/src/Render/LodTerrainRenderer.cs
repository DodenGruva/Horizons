using System.Diagnostics;
using System.Text;
using OpenTK.Graphics.OpenGL4;
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
    internal const double PreVanillaRenderOrder = 0.36;
    internal const double PostVanillaRenderOrder = 0.38;
    public double RenderOrder => postVanillaDepthCulling
        ? PostVanillaRenderOrder
        : PreVanillaRenderOrder;
    public int RenderRange => 9999;

    const int MeshSchedulesPerFrame = 4;
    const int MeshUploadsPerFrame = 4;
    // Initial frame-local ceilings. Item caps remain as a final safety rail, while live
    // telemetry records enough time/byte/backlog evidence to tune these without guessing.
    internal const long MeshSnapshotMaxBytesPerFrame = 2 * 1024 * 1024;
    internal const double MeshSnapshotMaxMillisecondsPerFrame = 1.0;
    internal const long MeshUploadMaxBytesPerFrame = 4 * 1024 * 1024;
    internal const double MeshUploadMaxMillisecondsPerFrame = 2.0;
    internal const double SafeTemporalTranslationBlocks = 0.25;
    internal const float SafeTemporalRotationMatrix = 0.0005f;
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
    readonly LodGpuTelemetry gpuTelemetry;
    readonly Func<long> currentWorldEpoch;
    readonly string? gpuRendererPreference;
    readonly LodGpuShadowRenderPath gpuShadow;
    readonly LodRenderPathCoordinator renderPaths;
    bool renderPathConfigured;
    float renderCullDistanceSquared = float.MaxValue;
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
    public long OpaqueDrawCalls { get; private set; }
    public long WaterDrawCalls { get; private set; }
    public long OpaqueDrawVertices { get; private set; }
    public long OpaqueDrawIndices { get; private set; }
    public long WaterDrawVertices { get; private set; }
    public long WaterDrawIndices { get; private set; }
    public long LiveGpuMeshBytes { get; private set; }
    public long LiveOpaqueVertices { get; private set; }
    public long LiveOpaqueIndices { get; private set; }
    public long LiveWaterVertices { get; private set; }
    public long LiveWaterIndices { get; private set; }
    public bool GpuTimingActive => gpuTelemetry.TimingActive;
    public bool GpuTimingRequested => gpuTelemetry.TimingRequested;
    public LodPhaseCost GpuOpaqueCost => gpuTelemetry.OpaqueCost;
    public LodPhaseCost GpuWaterCost => gpuTelemetry.WaterCost;
    public int GpuTimerPendingResults => gpuTelemetry.PendingResults;
    public int GpuTimerUnavailableSlots => gpuTelemetry.UnavailableSlots;
    public int GpuTimerTargetBusy => gpuTelemetry.TargetBusy;
    public long TemporalOcclusionQueries { get; private set; }
    public long TemporalOcclusionResults { get; private set; }
    public long TemporalOcclusionHiddenResults { get; private set; }
    public long TemporalOcclusionStaleResults { get; private set; }
    public long TemporalOcclusionGlobalInvalidations { get; private set; }
    public long TemporalOcclusionDrawsSkipped { get; private set; }
    public int LastTemporalOcclusionDrawsSkipped { get; private set; }
    public int LastTemporalOcclusionSeamDraws { get; private set; }
    public int LastTemporalOcclusionEdgeDraws { get; private set; }
    public long ReadinessProbes { get; private set; }
    public long ReadinessTrueResults { get; private set; }
    public long ReadinessFalseResults { get; private set; }
    public long ReadinessReadyTransitions { get; private set; }
    public long ReadinessLostTransitions { get; private set; }
    public long ReadinessProbeErrors { get; private set; }
    public long ReadinessWindowChanges { get; private set; }
    public long ReadinessResizes { get; private set; }
    public long ReadinessFullRevalidations { get; private set; }
    public long ReadinessAggregateRepairs { get; private set; }
    public long ReadinessStaleCommittedFound { get; private set; }
    public long ReadinessEmptyChunksSeen { get; private set; }
    public long ReadinessDrawnWithoutGeometry { get; private set; }
    public long ReadinessOwnedWithoutGeometry { get; private set; }
    public long ReadinessOwnershipDeniedNoGeometry { get; private set; }

    /// <summary>
    /// Probes refused ownership because the cell sits outside the engine's own draw range.
    /// A permanently non-zero figure here while standing still is the trailing annulus:
    /// chunks the player walked away from, still loaded and still reporting every "drawing"
    /// signal, that the engine range-culls at draw time.
    /// </summary>
    public long ReadinessOwnershipDeniedBeyondViewDistance { get; private set; }
    public long ReadinessMaskResyncs { get; private set; }

    /// <summary>
    /// Whether a chunk the engine claims while holding no mesh may own its cell.
    /// On by default and only meaningful with the chunk mask enabled. Exposed as a
    /// runtime toggle so a hole can be judged both ways in one place without a
    /// rebuild: the rule un-suppresses cached terrain, so if it makes holes worse it
    /// is doing so by adding mesh work, not by hiding more, and that is a question
    /// only a person looking at the band can settle.
    /// </summary>
    public bool GeometryOwnershipRule { get; set; } = true;

    /// <summary>
    /// Whether a wholly owned cached section may be dropped before it is drawn at all.
    ///
    /// Ownership has two consumers and they fail differently. The shader discards single
    /// fragments in owned cells; this drops an entire cached section on the CPU. Both are
    /// live only with the mask on, so a hole that appears with the mask could come from
    /// either, and no amount of reasoning about ownership separates them once ownership
    /// itself is known good. Turning this off leaves the per-pixel mask working alone.
    /// </summary>
    public bool WholeMeshSkip { get; set; } = true;

    /// <summary>
    /// Paint fragments the mask would hide bright red instead of hiding them, and stop
    /// dropping whole owned sections so nothing escapes the paint. A gap that turns red is
    /// the mask's doing; a gap that stays empty never was, and no further argument about
    /// ownership is needed to tell them apart.
    /// </summary>
    public bool MaskDebugPaint { get; set; }

    /// <summary>
    /// Apply vanilla's own rule that an up-facing surface never darkens as the sun drops:
    /// `getBrightnessFromNormal` floors its shade at `normal.y * 0.95`, and the liquid shader
    /// does not shade by normal at all. Without it cached ground fell to 0.55 at dawn and
    /// dusk beside vanilla ground still at 0.95, which is why the colour matched at midday
    /// and not at other times once the albedo itself was exact. On by default; `.vhtoplight
    /// off` restores the old shading for comparison.
    /// </summary>
    public bool FlatTopLight { get; set; } = true;

    /// <summary>
    /// Opaque terrain has outward counter-clockwise winding and uses hardware back-face
    /// rejection; blended water and thin cover stay two-sided. Human testing across cliffs,
    /// caves, overhangs, and high/low views found no visual difference, while a same-view
    /// A/B improved 218 to 260 FPS. `.vhbackface off` remains the immediate comparison and
    /// compatibility fallback.
    /// </summary>
    public bool OpaqueBackfaceCulling { get; set; } = true;

    /// <summary>
    /// Submit opaque cached sections nearest-first so mountain depth can reject farther
    /// hidden fragments before their shader runs. Water keeps the traversal order because
    /// alpha blending has different ordering rules. A same-view human A/B improved 149 to
    /// 173 FPS with no visible change; `.vhfront off` remains the immediate fallback.
    /// </summary>
    public bool OpaqueFrontToBack { get; set; } = true;

    /// <summary>
    /// Delayed exact-geometry occlusion. Ordinary opaque draws are sampled
    /// asynchronously after vanilla has populated depth. A zero-sample result skips later
    /// submissions. Profile/projection and selected camera-threshold changes invalidate
    /// globally; streamed scene changes invalidate only affected pieces and rely on the
    /// periodic exact probe for convergence. Camera turns shorten that probe cadence.
    /// Unlike the rejected prototype this has no proxy boxes, conditional rendering, or
    /// same-frame dependency. The aggressive profile is default-on after in-game testing
    /// found a substantial FPS gain; mixed ownership and a narrow turning-edge band are
    /// deliberately protected from delayed hiding.
    /// </summary>
    public bool TemporalOcclusionEnabled { get; set; } =
        Environment.GetEnvironmentVariable("VINTAGEHORIZONS_TEMPORAL_OCCLUSION") != "0";

    double temporalOcclusionTranslationLimitBlocks = 2.0;
    float temporalOcclusionRotationMatrixLimit = float.PositiveInfinity;
    int temporalOcclusionVisibleQueryIntervalFrames = 8;
    int temporalOcclusionHiddenProbeIntervalFrames = 16;
    int temporalOcclusionTurningProbeIntervalFrames = 4;
    double temporalOcclusionEdgeGuard = 0.06;
    public string TemporalOcclusionProfileName { get; private set; } = "aggressive";

    public bool SetTemporalOcclusionProfile(string profile)
    {
        switch (profile.ToLowerInvariant())
        {
            case "safe":
                temporalOcclusionTranslationLimitBlocks = SafeTemporalTranslationBlocks;
                temporalOcclusionRotationMatrixLimit = SafeTemporalRotationMatrix;
                temporalOcclusionVisibleQueryIntervalFrames = 4;
                temporalOcclusionHiddenProbeIntervalFrames = 8;
                temporalOcclusionTurningProbeIntervalFrames = 8;
                temporalOcclusionEdgeGuard = 0;
                break;
            case "aggressive":
                temporalOcclusionTranslationLimitBlocks = 2.0;
                temporalOcclusionRotationMatrixLimit = float.PositiveInfinity;
                temporalOcclusionVisibleQueryIntervalFrames = 8;
                temporalOcclusionHiddenProbeIntervalFrames = 16;
                temporalOcclusionTurningProbeIntervalFrames = 4;
                temporalOcclusionEdgeGuard = 0.06;
                break;
            case "extreme":
                temporalOcclusionTranslationLimitBlocks = double.PositiveInfinity;
                temporalOcclusionRotationMatrixLimit = float.PositiveInfinity;
                temporalOcclusionVisibleQueryIntervalFrames = 16;
                temporalOcclusionHiddenProbeIntervalFrames = 32;
                temporalOcclusionTurningProbeIntervalFrames = 8;
                temporalOcclusionEdgeGuard = 0;
                break;
            default:
                return false;
        }

        TemporalOcclusionProfileName = profile.ToLowerInvariant();
        InvalidateTemporalOcclusionScene();
        return true;
    }

    /// <summary>
    /// Render cached terrain just after vanilla terrain so the ordinary depth test can
    /// reject fragments hidden behind current chunks. The established order stays the
    /// default after an owner A/B measured a substantial gain and found only minute,
    /// acceptable distant changes. Changing this property re-registers the renderer because
    /// the engine sorts render order only during registration. The pre-vanilla order remains
    /// the immediate compatibility fallback.
    /// </summary>
    public bool OcclusionCullingEnabled
    {
        get => postVanillaDepthCulling;
        set
        {
            if (postVanillaDepthCulling == value) return;
            if (!rendererRegistered)
            {
                postVanillaDepthCulling = value;
                return;
            }

            bool previous = postVanillaDepthCulling;
            capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
            rendererRegistered = false;
            postVanillaDepthCulling = value;
            InvalidateTemporalOcclusionScene();
            try
            {
                RegisterRenderer();
            }
            catch (Exception orderError)
            {
                postVanillaDepthCulling = previous;
                try
                {
                    RegisterRenderer();
                }
                catch (Exception restoreError)
                {
                    throw new AggregateException(
                        "Changing cached-terrain render order failed, and restoring its previous order also failed.",
                        orderError, restoreError);
                }
                capi.Logger.Warning(
                    "[VintageHorizons] Post-vanilla depth culling unavailable; restored ordinary render order: {0}",
                    orderError.Message);
            }
        }
    }
    bool postVanillaDepthCulling = true;
    bool rendererRegistered;
    public double ReadinessOwnedWithoutGeometryNearest { get; private set; }
    public double ReadinessOwnedWithoutGeometryFarthest { get; private set; }
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
        OpaqueDrawCalls = 0;
        WaterDrawCalls = 0;
        OpaqueDrawVertices = 0;
        OpaqueDrawIndices = 0;
        WaterDrawVertices = 0;
        WaterDrawIndices = 0;
        gpuTelemetry.ResetInterval();
        TemporalOcclusionQueries = 0;
        TemporalOcclusionResults = 0;
        TemporalOcclusionHiddenResults = 0;
        TemporalOcclusionStaleResults = 0;
        TemporalOcclusionGlobalInvalidations = 0;
        TemporalOcclusionDrawsSkipped = 0;
        ReadinessProbes = 0;
        ReadinessTrueResults = 0;
        ReadinessFalseResults = 0;
        ReadinessReadyTransitions = 0;
        ReadinessLostTransitions = 0;
        ReadinessProbeErrors = 0;
        ReadinessWindowChanges = 0;
        ReadinessResizes = 0;
        ReadinessFullRevalidations = 0;
        ReadinessAggregateRepairs = 0;
        ReadinessStaleCommittedFound = 0;
        ReadinessEmptyChunksSeen = 0;
        ReadinessDrawnWithoutGeometry = 0;
        ReadinessOwnedWithoutGeometry = 0;
        ReadinessOwnershipDeniedNoGeometry = 0;
        ReadinessOwnershipDeniedBeyondViewDistance = 0;
        ReadinessMaskResyncs = 0;
        ReadinessOwnedWithoutGeometryNearest = -1;
        ReadinessOwnedWithoutGeometryFarthest = -1;
        Array.Clear(readinessNoGeometryByY);
        VanillaOwnedDrawsSkipped = 0;
        CoarseWaitingLoad = 0;
        CoarseWaitingMesh = 0;
        CoarseWaitingSchedule = 0;
        CoarseWaitingOther = 0;
        SeamRepairsQueued = 0;
        readiness?.ResetTelemetry();
        readinessMask?.ResetTelemetry();
    }

    readonly Dictionary<long, MeshRef> sectionMeshes = new();
    readonly Dictionary<long, MeshRef> waterMeshes = new();
    readonly Dictionary<long, LodLiveMeshStats> liveMeshStats = new();

    readonly record struct LodLiveMeshStats(
        int OpaqueVertices, int OpaqueIndices, int WaterVertices, int WaterIndices)
    {
        public long Bytes => OpaqueVertices * 16L + OpaqueIndices * sizeof(int)
            + WaterVertices * 16L + WaterIndices * sizeof(int);
    }
    readonly LodMeshBounds meshBounds = new();
    readonly LodFarPlaneState farPlaneState = new();
    readonly HashSet<long> meshJobInFlight = new();
    readonly LodRenderDirtyScheduler dirtyScheduler = new();
    readonly Predicate<long> keepRenderDirty;
    readonly Predicate<long> renderDirtyBlocked;
    // Visibility is intentionally absent from this state. The camera may stop walking
    // an off-screen subtree, but distance-based residency still keeps appropriate meshes
    // warm for a turn-around (G8).
    readonly Dictionary<long, long> lastResidencyMs = new();
    readonly Queue<long> meshEvictionOrder = new();
    readonly HashSet<long> meshEvictionQueued = new();
    long frameCounter;

    sealed class TemporalOcclusionQuery
    {
        public int QueryId;
        public readonly LodTemporalOcclusionState State = new();
    }

    readonly Dictionary<long, TemporalOcclusionQuery> temporalOcclusionQueries = new();
    readonly List<long> temporalOcclusionPending = new();
    readonly float[] temporalOcclusionView = new float[16];
    readonly float[] temporalOcclusionProjection = new float[16];
    readonly float[] temporalOcclusionPreviousFrameView = new float[16];
    double temporalOcclusionCameraX;
    double temporalOcclusionCameraY;
    double temporalOcclusionCameraZ;
    long temporalOcclusionEpoch = 1;
    bool temporalOcclusionHasView;
    bool temporalOcclusionHasPreviousFrameView;
    bool temporalOcclusionTurningThisFrame;
    bool temporalOcclusionQueryTargetAvailable;
    bool temporalOcclusionFailed;
    bool temporalOcclusionFailureReported;
    int temporalOcclusionQueriesIssuedThisFrame;
    const int TemporalOcclusionQueryIssuesPerFrame = 8;
    const int TemporalOcclusionResultChecksPerFrame = 16;

    /// <summary>
    /// Sides (W, E, N, S as bits 0-3) whose live mesh was built against a neighbour that
    /// held stored data but was not in RAM. Only these owe a repair mesh when the
    /// neighbour lands - which is the whole point of recording them, because re-meshing
    /// all four neighbours of every arriving section would roughly double the mesh work
    /// of a warm join. Keys with nothing assumed are not stored.
    /// </summary>
    readonly Dictionary<long, byte> meshedWithoutNeighbor = new();

    /// <summary>Repair meshes queued because a guessed-at neighbour finally arrived.</summary>
    public int SeamRepairsQueued { get; private set; }

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
    readonly StringBuilder readinessNoGeometryText = new();
    readonly StringBuilder levelReport = new();
    readonly LodNearHandoffState nearHandoff = new();
    // Per-cell ownership, on by default since 0.3.17: the draw-range clause closed the band
    // that had kept it opt-in, and a player confirmed both the closure and the seam overlap
    // it accepts. The saved setting is applied over this at startup.
    //
    // The environment variable is now an override in both directions, because the benchmark
    // harness has to be able to measure the radial path after it stopped being the default:
    // `0` forces the mask off, `1` forces it on, anything else leaves the setting alone.
    bool chunkMaskRequested =
        Environment.GetEnvironmentVariable("VINTAGEHORIZONS_CHUNK_MASK") != "0";
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
    int[] readinessNoGeometryByY = Array.Empty<int>();
    bool readinessHandoffStale = true;
    bool readinessSweepShellNow;
    long readinessLastFullRevalidateMs;
    bool readinessAggregateReported;

    /// <summary>Meshes outside the residency band for one minute get evicted.</summary>
    const long EvictAfterMs = 60_000;
    const int MeshEvictionChecksPerFrame = 4;

    public int EvictedTotal { get; private set; }
    readonly Matrixf modelMat = new();
    readonly List<long> drawList = new();
    readonly List<LodOpaqueDrawEntry> opaqueFrontToBack = new();
    IShaderProgram? prog;
    bool shaderOk;
    float appliedZFar;
    Vec3d camPos = new();

    /// <summary>Dev/testing: keep the game unpaused even without window focus.</summary>
    public bool AutoUnpause;

    // Live seasonal state, refreshed incrementally and fed to the shader as uniforms.
    const long SeasonalRefreshIntervalMs = 30_000;
    float snowLineY = 99999;
    float pendingSnowLineY = 99999;
    long lastSeasonRefreshMs;
    bool seasonalStateInitialized;
    bool seasonalRefreshActive;
    int seasonalRefreshSlot;
    int seasonalRefreshX;
    int seasonalRefreshZ;
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

    public LodTerrainRenderer(
        ICoreClientAPI capi,
        LodWorld world,
        LodWorker worker,
        LodTintRegistry tints,
        Func<long>? currentWorldEpoch = null)
    {
        this.capi = capi;
        this.world = world;
        this.worker = worker;
        this.tints = tints;
        this.currentWorldEpoch = currentWorldEpoch ?? (() => 0);
        bool gpuTimingRequested =
            Environment.GetEnvironmentVariable("VINTAGEHORIZONS_GPU_STATS") == "1";
        gpuRendererPreference =
            Environment.GetEnvironmentVariable("VINTAGEHORIZONS_GPU_RENDERER");
        gpuTelemetry = new LodGpuTelemetry(
            gpuTimingRequested,
            LodRenderPathPolicy.RequestsRuntimeValidation(gpuRendererPreference),
            message => capi.Logger.Notification("{0}", message),
            message => capi.Logger.Warning("{0}", message));
        gpuShadow = new LodGpuShadowRenderPath();
        renderPaths = new LodRenderPathCoordinator(
            new LodLegacyRenderPath(
                PublishLegacy,
                RemoveMeshesLegacy,
                PrepareLegacyFrame,
                DrawLegacyOpaque,
                DrawLegacyWater,
                ClearMeshesLegacy),
            gpuShadow,
            message => capi.Logger.Warning("{0}", message));
        keepRenderDirty = KeepRenderDirty;
        renderDirtyBlocked = RenderDirtyBlocked;
        maxWorkerMeshBacklog = worker.MeshThreads * MeshBacklogPerThread;
        world.SectionBecameResident += OnSectionBecameResident;

        capi.Event.ReloadShader += LoadShader;
        capi.Event.ChunkDirty += OnReadinessChunkDirty;
        LoadShader();
        RegisterRenderer();
    }

    void RegisterRenderer()
    {
        capi.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "vintagehorizons-lod");
        rendererRegistered = true;
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
    /// Refresh the live tint table and snow line every 30 seconds. Each tint slot is sampled
    /// at two altitudes and interpolated per vertex, because the climate maps are keyed
    /// by temperature and temperature falls with height; the snow line extrapolates the
    /// same lapse rate to where it hits freezing.
    /// </summary>
    void RefreshSeasonalState()
    {
        long now = Environment.TickCount64;
        if (!seasonalRefreshActive)
        {
            if (seasonalStateInitialized
                && now - lastSeasonRefreshMs < SeasonalRefreshIntervalMs) return;

            seasonalRefreshActive = true;
            seasonalRefreshSlot = 1;
            seasonalRefreshX = (int)camPos.X;
            seasonalRefreshZ = (int)camPos.Z;
            tints.BeginRefresh(capi.World);
            pendingSnowLineY = CalculateSnowLine(seasonalRefreshX, seasonalRefreshZ);
        }

        // Game climate and colour-map APIs are owning-thread state. Keep them here, but
        // spread the calls over frames and publish only after the whole table is ready.
        if (seasonalRefreshSlot < tints.SlotCount)
        {
            tints.RefreshSlot(capi.World, seasonalRefreshX, seasonalRefreshZ,
                seasonalRefreshSlot++);
            return;
        }

        tints.CompleteRefresh();
        snowLineY = pendingSnowLineY;
        seasonalRefreshActive = false;
        seasonalStateInitialized = true;
        lastSeasonRefreshMs = now;
    }

    float CalculateSnowLine(int px, int pz)
    {
        float calculated = 99999;

        try
        {
            int seaLevel = capi.World.SeaLevel;
            climatePos.Set(px, seaLevel, pz);
            ClimateCondition? low = capi.World.BlockAccessor.GetClimateAt(climatePos);
            climatePos.Set(px, seaLevel + 150, pz);
            ClimateCondition? high = capi.World.BlockAccessor.GetClimateAt(climatePos);

            if (low == null || high == null || low.Temperature <= high.Temperature)
            {
                calculated = 99999; // no usable lapse rate → snow line disabled
            }
            else
            {
                float lapsePerBlock = (low.Temperature - high.Temperature) / 150f;
                calculated = seaLevel + (low.Temperature - (-1f)) / lapsePerBlock;
                calculated = GameMath.Clamp(calculated, seaLevel - 64, 99999);
            }
        }
        catch
        {
            calculated = 99999;
        }
        return calculated;
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
        int available = meshEvictionOrder.Count;
        for (int n = 0; n < MeshEvictionChecksPerFrame && n < available; n++)
        {
            long key = meshEvictionOrder.Dequeue();
            if (!HasAnyMesh(key))
            {
                meshEvictionQueued.Remove(key);
                lastResidencyMs.Remove(key);
                continue;
            }
            if (!ShouldEvictMesh(key))
            {
                meshEvictionOrder.Enqueue(key);
                continue;
            }

            RemoveMeshes(key);
            meshEvictionQueued.Remove(key);
            lastResidencyMs.Remove(key);
            EvictedTotal++;
        }
    }

    bool ShouldEvictMesh(long key)
    {
        if (LodTraversalPolicy.WithinResidencyBand(key, camPos.X, camPos.Z))
        {
            lastResidencyMs[key] = Environment.TickCount64;
            return false;
        }

        return !lastResidencyMs.TryGetValue(key, out long last)
            || Environment.TickCount64 - last > EvictAfterMs;
    }

    void QueueMeshEvictionCheck(long key)
    {
        if (!meshEvictionQueued.Add(key)) return;
        meshEvictionOrder.Enqueue(key);
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
            LodWorld.DetailPolicyRevision, keepRenderDirty);
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

            // A neighbour that is absent from RAM is NOT automatically the edge of
            // explored space. HasDataSet is the question the mesher actually wants
            // answered - the shader's openEdges has always asked it - and residency was
            // standing in for it. Sections are meshed nearest-first, so the outward side
            // of nearly every one of them is scheduled before its neighbour has finished
            // loading, and an ocean answered that with a seabed-deep sheet of translucent
            // water down every section boundary that never healed.
            //
            // Deliberately no load request here: a neighbour outside the draw set is
            // meant to end in nothing, and forcing it resident to prove that would pull
            // the whole cache into memory one ring at a time.
            byte assumedCovered = 0;
            for (int d = 0; d < 4; d++)
            {
                long nk = LodWorld.NeighborKey(best, d == 0 ? -1 : d == 1 ? 1 : 0, d == 2 ? -1 : d == 3 ? 1 : 0);
                if (world.Sections.TryGetValue(nk, out LodSection? nb))
                {
                    neighborSections[d] = nb;
                    estimatedBytes = SectionSnapshot.SaturatingAdd(
                        estimatedBytes, SectionSnapshot.EstimateRetainedBytes(nb));
                }
                else if (world.HasDataSet.Contains(nk) && !world.LoadFailed.Contains(nk))
                {
                    assumedCovered |= (byte)(1 << d);
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
                AssumedCoveredSides = assumedCovered,
            });
        }


        MeshSnapshotItems += snapshotBudget.Items;
        MeshSnapshotBytes = SectionSnapshot.SaturatingAdd(
            MeshSnapshotBytes, snapshotBudget.Bytes);
    }

    /// <summary>
    /// A section arrived in RAM. Any neighbour whose live mesh left the side facing it
    /// open - because this section's data was on disk rather than in memory when that
    /// mesh was built - now owes one re-mesh, and only that neighbour: the bit is cleared
    /// as the obligation is handed over so an arrival can never queue the same repair
    /// twice, and a section that guessed at nothing costs a dictionary miss.
    /// </summary>
    void OnSectionBecameResident(long key)
    {
        if (meshedWithoutNeighbor.Count == 0) return;

        for (int d = 0; d < 4; d++)
        {
            long nk = LodWorld.NeighborKey(key, d == 0 ? -1 : d == 1 ? 1 : 0, d == 2 ? -1 : d == 3 ? 1 : 0);
            if (!meshedWithoutNeighbor.TryGetValue(nk, out byte sides)) continue;

            // Our neighbour to the west has us to ITS east: W/E and N/S are the low bit
            // of the direction, so the facing side is d ^ 1.
            int facing = d ^ 1;
            if ((sides & (1 << facing)) == 0) continue;

            sides &= (byte)~(1 << facing);
            if (sides == 0) meshedWithoutNeighbor.Remove(nk);
            else meshedWithoutNeighbor[nk] = sides;

            world.RenderDirty.Add(nk);
            SeamRepairsQueued++;
        }
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

            renderPaths.Publish(
                currentWorldEpoch(),
                result.Key,
                newOpaque,
                newWater,
                result.VertexCount,
                result.IndexCount,
                result.WaterVertexCount,
                result.WaterIndexCount,
                result.AssumedCoveredSides,
                // Borrowed for the call only. The shadow arenas copy what they need before
                // this returns; nothing may retain the mesher's arrays.
                new LodRenderGeometry(result.Xyz, result.Rgba, result.Indices));
        }

        MeshUploadItems += uploadBudget.Items;
    }

    void RemoveMeshes(long key)
    {
        renderPaths.Remove(currentWorldEpoch(), key);
    }

    void RemoveMeshesLegacy(long worldEpoch, long key)
    {
        meshedWithoutNeighbor.Remove(key);
        RemoveTemporalOcclusionQuery(key);
        if (!HasAnyMesh(key)) return;
        DisposeMeshRefs(key);
        meshBounds.Remove(key);
    }

    void DisposeMeshRefs(long key)
    {
        RemoveLiveMeshStats(key);
        if (sectionMeshes.Remove(key, out MeshRef? mesh)) DisposeMeshRef(mesh);
        if (waterMeshes.Remove(key, out MeshRef? water)) DisposeMeshRef(water);
    }

    void PublishLegacy(LodRenderPublication publication)
    {
        long key = publication.Identity.SectionKey;
        bool hadMesh = HasAnyMesh(key);
        PublishLiveMeshStats(publication);
        ReplaceMeshRefs(key, publication.Opaque, publication.Water);

        // Tracks the mesh that is actually live, so a repair can only ever be owed by
        // geometry that is on screen.
        if (publication.AssumedCoveredSides != 0)
            meshedWithoutNeighbor[key] = publication.AssumedCoveredSides;
        else
            meshedWithoutNeighbor.Remove(key);

        bool hasMesh = HasAnyMesh(key);
        if (!hadMesh && hasMesh) meshBounds.Include(key);
        else if (hadMesh && !hasMesh) meshBounds.Remove(key);

        // Fresh uploads get an age grace period. Later retention depends on distance,
        // never on whether the current camera happens to see the mesh.
        if (hasMesh)
        {
            lastResidencyMs[key] = Environment.TickCount64;
            QueueMeshEvictionCheck(key);
        }
    }

    void PublishLiveMeshStats(LodRenderPublication publication)
    {
        long key = publication.Identity.SectionKey;
        RemoveLiveMeshStats(key);
        var stats = new LodLiveMeshStats(
            Math.Max(0, publication.OpaqueVertices),
            Math.Max(0, publication.OpaqueIndices),
            Math.Max(0, publication.WaterVertices),
            Math.Max(0, publication.WaterIndices));
        if (stats.OpaqueIndices == 0 && stats.WaterIndices == 0) return;

        liveMeshStats[key] = stats;
        LiveOpaqueVertices += stats.OpaqueVertices;
        LiveOpaqueIndices += stats.OpaqueIndices;
        LiveWaterVertices += stats.WaterVertices;
        LiveWaterIndices += stats.WaterIndices;
        LiveGpuMeshBytes += stats.Bytes;
    }

    void RemoveLiveMeshStats(long key)
    {
        if (!liveMeshStats.Remove(key, out LodLiveMeshStats stats)) return;
        LiveOpaqueVertices -= stats.OpaqueVertices;
        LiveOpaqueIndices -= stats.OpaqueIndices;
        LiveWaterVertices -= stats.WaterVertices;
        LiveWaterIndices -= stats.WaterIndices;
        LiveGpuMeshBytes -= stats.Bytes;
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
        // The replaced section itself must draw until remeasured. Do not globally reset
        // everything behind it: active exploration can publish several meshes per frame,
        // which previously kept every query stale forever. Hidden terrain already performs
        // periodic exact probes, so a changed occluder converges without defeating culling.
        if (temporalOcclusionQueries.TryGetValue(key, out TemporalOcclusionQuery? query))
            query.State.Invalidate(temporalOcclusionEpoch);
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
                readiness.ColumnEvicted = (chunkX, chunkZ) => readinessMask?.ClearColumn(chunkX, chunkZ);
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
                ResyncReadinessMask();

                // The counts the whole-mesh skip trusts are derived state that nothing else
                // re-derives. A single drift high hides a section permanently, so they are
                // checked against the cell states each pass and any repair is reported: a
                // non-zero number here is a bug in this class, not a tuning problem.
                int repaired = readiness.AuditReadyCounts();
                if (repaired > 0)
                {
                    ReadinessAggregateRepairs += repaired;
                    if (!readinessAggregateReported)
                    {
                        readinessAggregateReported = true;
                        capi.Logger.Warning(
                            "[VintageHorizons] ownership audit repaired {0} section counts; a section "
                            + "was reporting itself fully covered by vanilla terrain when it was not. "
                            + "Please report this with the surrounding log.", repaired);
                    }
                }
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

                if (!rendered && readiness.State(cell) == VanillaReadinessState.VanillaReady)
                    ReadinessStaleCommittedFound++;

                // Measured, not acted on; see G40 for what happened the one time a rule
                // like this reached the draw path from reasoning instead of evidence.
                if (readinessNoGeometryByY.Length < readiness.VerticalChunks)
                    readinessNoGeometryByY = new int[readiness.VerticalChunks];

                // Both the measurement and the rule below need a chunk lookup, which takes
                // the client's chunk lock - the same lock IsChunkRendered just took, and the
                // same one the loader threads want while a world is coming up. Ask only when
                // this observation can actually change ownership: the second of the two true
                // observations a commit needs, or a re-confirmation of a committed cell.
                // Every other probe at join is a first sighting that decides nothing.
                VanillaReadinessState decisionState = readiness.State(cell);
                bool atOwnershipDecision = decisionState is VanillaReadinessState.ObservedRendered
                    or VanillaReadinessState.VanillaReady;

                if (rendered && atOwnershipDecision) MeasureDrawnChunk(cell, camPos.X, camPos.Z);

                // Beyond the view distance the engine draws nothing here, whatever every
                // other signal says. It range-culls each terrain pool location per frame
                // against the view distance, and the signals ownership is built on do not
                // follow: the drawn counter never resets, the mesh stays in the pool, Hide
                // stays clear, and the culler freezes its verdict entirely while the camera
                // holds still in one chunk. So a cell the player walked away from stays
                // committed forever, and the mask discards cached terrain there against
                // nothing - a band at the seam, on the trailing side only, that never heals.
                //
                // Air is denied here too, unlike the geometry rule below. Beyond this range
                // vanilla owns nothing at all, so whole columns must demote together; that
                // is also what releases the section aggregates the whole-mesh skip reads.
                //
                // Gated on the mask for the same reason as the geometry rule: the default
                // radial-handoff path is benchmarked as it stands, and demoting the annulus
                // would pull its single global radius in for every column.
                if (rendered && ChunkMaskEnabled
                    && VanillaRenderReadiness.BeyondVanillaDrawRange(
                        cell.X, cell.Z, camPos.X, camPos.Z, viewDistance))
                {
                    rendered = false;
                    ReadinessOwnershipDeniedBeyondViewDistance++;
                }

                // The confirmed hole: the engine's drawn counter is set once and never
                // cleared, so a chunk it claims can hold no terrain at all - and where it
                // holds none, suppressing the cache leaves nobody drawing that ground. A
                // cell like that is not owned, whatever the counter says.
                //
                // Only while the mask is on. The same signal also feeds the column
                // aggregate behind the radial handoff, and a buried chunk with no exposed
                // faces holds no mesh either - harmless per cell, because cached terrain
                // underground is invisible, but it would pull the radial radius in for
                // every column. Containing it to the feature under evaluation keeps the
                // default path exactly as measured. Widen it only on evidence: read
                // "nearest incomplete" and "owned draws skipped" from a mask-on run.
                if (rendered && atOwnershipDecision && ChunkMaskEnabled && GeometryOwnershipRule
                    && VanillaChunkGeometry.Available
                    && !VanillaChunkHoldsDrawableGround(cell))
                {
                    rendered = false;
                    ReadinessOwnershipDeniedNoGeometry++;
                }

                if (!readiness.Observe(cell, rendered, frameCounter,
                    out VanillaReadinessPublication publication)) continue;

                // Phase 1 has no texture. Accepting into this shadow model lets diagnostics
                // validate aggregate transitions without changing any pixel or draw call.
                if (!readiness.ResolvePublication(publication, accepted: true)) continue;
                // Recorded, not only applied. The mask is also rebuilt wholesale on every
                // window change, and a rebuild has no chunk to ask - so the exclusion has to
                // live in the tracker or every 32 blocks of travel puts air ownership back
                // into the atlas until the next resync scrubs it out again.
                bool air = VanillaChunkIsAir(publication.Cell);
                readiness.SetMaskExcluded(publication.Cell, air);
                readinessMask?.Set(publication.Cell, publication.Ready && !air);
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
    /// What the player asked for, regardless of whether this session's mask is working.
    /// Persistence must read this and never <see cref="ChunkMaskEnabled"/>: that getter
    /// reports the effective state, so saving it would write `false` after any transient
    /// texture failure and turn the feature off for every future session.
    /// </summary>
    public bool ChunkMaskRequested => chunkMaskRequested;

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
        // Captured once: the field is nullable and can be cleared by the failure path, and
        // a diagnostic must not be the thing that throws while explaining a problem.
        VanillaRenderReadiness? tracker = readiness;
        if (tracker == null) return "no readiness tracker; the distance handoff is drawing";

        var previous = new VanillaChunkCell(int.MinValue, int.MinValue, int.MinValue);
        string? firstEmpty = null;
        int examined = 0;
        int verticalChunks = Math.Max(1, (worldHeight + 31) / 32);
        float viewDistance = ApprovedViewDistance();

        for (int distance = 0; distance <= maxBlocks && examined < 4096; distance += 4)
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

            // "Rendered" is not "drawing". The engine's counter advances once for a chunk of
            // air and is never reset, so a chunk it claims can hold no terrain at all: freed
            // on overload, or never meshed because nothing in it has an exposed face. Without
            // this the search walks straight past the one case that leaves ground undrawn
            // while we suppress the cache over it, and answers "nothing wrong".
            IWorldChunk? vanillaChunk = null;
            try { vanillaChunk = capi.World.BlockAccessor.GetChunk(cell.X, cell.Y, cell.Z); }
            catch { /* a diagnostic never throws out of itself */ }
            // Any reason the engine will not submit this chunk counts, not just a missing
            // mesh: it can hold one and still be flagged not to draw. Frustum state is
            // deliberately excluded - a chunk behind the camera is not a hole.
            bool engineHoldsNothing = vanillaChunk is { Empty: false }
                && VanillaChunkGeometry.TryIsVanillaDrawing(vanillaChunk, out bool drawingHere)
                && !drawingHere;

            // The other way the engine declines to draw a chunk it still holds, and the one
            // that used to make this report say "engine is drawing this chunk" about ground
            // nothing was drawing: every terrain pool location is range-culled per frame
            // against the view distance, and none of the flags above follow.
            bool beyondDrawRange = VanillaRenderReadiness.BeyondVanillaDrawRange(
                cell.X, cell.Z, startX, startZ, viewDistance);

            // Ground can be drawn by any level, so report all of them. A single verdict has
            // twice now been read as evidence when it was describing a different level than
            // the one that would actually have drawn the ground in question.
            bool resident = false;
            bool meshed = false;
            bool skipped = false;
            levelReport.Clear();
            for (int level = 0; level <= LodWorld.MaxLevel; level++)
            {
                int footprint = LodWorld.KeyFootprintBlocks(LodWorld.SectionKey(level, 0, 0));
                long key = LodWorld.SectionKey(level,
                    (int)Math.Floor(x / footprint), (int)Math.Floor(z / footprint));
                bool hasSection = world.Sections.ContainsKey(key);
                bool hasMesh = HasAnyMesh(key);
                bool wasSkipped = skippedLastFrame.Contains(key);
                resident |= hasSection;
                meshed |= hasMesh;
                skipped |= wasSkipped;

                if (!hasSection && !hasMesh) continue;
                if (levelReport.Length > 0) levelReport.Append(", ");
                levelReport.Append('L').Append(level).Append(' ')
                    .Append(hasSection ? "resident" : "absent")
                    .Append(hasMesh ? " meshed" : " unmeshed");
                levelReport.Append(' ').Append(tracker.Classify(key));
                if (wasSkipped) levelReport.Append(" SKIPPED-AS-OWNED");
            }
            if (levelReport.Length == 0) levelReport.Append("no cached section at any level");

            if ((!rendered || engineHoldsNothing || beyondDrawRange) && (suppressed || skipped))
            {
                string engineSays = !rendered
                    ? "engine not drawing"
                    : beyondDrawRange
                        ? $"engine claims this chunk but it is beyond the {viewDistance:0}-block draw range"
                        : "engine claims this chunk but is not drawing it";
                return $"OURS at {distance} blocks, chunk {cell.X},{cell.Y},{cell.Z}: {engineSays}, "
                    + $"we say {tracker.State(cell)}, mask {(suppressed ? "suppresses" : "allows")}, "
                    + $"draw {(skipped ? "skipped as owned" : "not skipped")} | "
                    + $"{VanillaChunkGeometry.DescribeSignals(vanillaChunk)} | {levelReport}";
            }

            if (firstEmpty == null && !rendered && !meshed)
            {
                firstEmpty = $"NO CACHED TERRAIN at {distance} blocks, chunk {cell.X},{cell.Y},{cell.Z}: "
                    + $"engine not drawing, we say {tracker.State(cell)}, "
                    + $"mask {(suppressed ? "suppresses" : "allows")} | "
                    + $"{VanillaChunkGeometry.DescribeSignals(vanillaChunk)} | {levelReport}";
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

        // What the engine actually holds here, which "rendered" does not answer: its drawn
        // counter advances for a chunk of air and is never reset, so it reports whether the
        // chunk was ever tesselated, not whether terrain exists in the buffers right now.
        IWorldChunk? vanillaChunk = null;
        try { vanillaChunk = capi.World.BlockAccessor.GetChunk(cell.X, cell.Y, cell.Z); }
        catch { /* a diagnostic never throws out of itself */ }

        bool geometryKnown = VanillaChunkGeometry.TryIsVanillaDrawing(vanillaChunk, out bool drawingNow);
        bool holdsGeometry = drawingNow;

        // Distance outranks every flag on the chunk. The engine range-culls each terrain
        // pool location per frame, so a chunk this far out is not drawn however loaded,
        // meshed, unhidden and cull-visible it reports itself to be - which is exactly what
        // this report used to describe as "engine is drawing this chunk".
        float viewDistance = ApprovedViewDistance();
        bool beyondDrawRange = VanillaRenderReadiness.BeyondVanillaDrawRange(
            cell.X, cell.Z, camPos.X, camPos.Z, viewDistance);
        string vanillaHolds = vanillaChunk == null ? "chunk not loaded"
            : vanillaChunk.Empty ? "chunk is empty (air)"
            : beyondDrawRange
                ? $"chunk is beyond the {viewDistance:0}-block draw range, so the engine range-culls it"
            : !geometryKnown ? "engine draw state unavailable"
            : drawingNow ? "engine is drawing this chunk"
            : "engine is NOT drawing this chunk";

        return $"chunk {cell.X},{cell.Y},{cell.Z}: we say {state}, engine says "
            + $"{(renderedNow ? "rendered" : "not rendered")}, {vanillaHolds}, "
            + $"mask {(suppressed ? "suppresses" : "allows")} "
            + $"cached terrain here; L0 section {(world.Sections.ContainsKey(sectionKey) ? "resident" : "absent")}, "
            + $"{(HasAnyMesh(sectionKey) ? "meshed" : "no mesh")}"
            + (state == VanillaReadinessState.VanillaReady && !renderedNow
                ? " -- STALE OWNERSHIP: the mask is hiding cached terrain the engine is not drawing"
                : "")
            // The boundary case worth naming outright: we suppress the cache, the engine
            // says it drew this chunk, the chunk is not air, and the engine holds no mesh
            // for it. Nothing draws that ground, and nothing will correct it on its own.
            + (suppressed && beyondDrawRange
                ? " -- NOTHING DRAWS THIS: suppressed cache beyond the range the engine draws at all"
                : "")
            + (suppressed && !beyondDrawRange && renderedNow && vanillaChunk is { Empty: false }
                    && geometryKnown && !holdsGeometry
                ? " -- NOTHING DRAWS THIS: suppressed cache, and the engine claims the chunk without holding terrain"
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
            // Mixed-ownership seam sections bypass temporal hiding altogether. A mask
            // upload therefore needs no global reset: newly mixed sections draw normally,
            // while cache-only hidden sections keep their periodic exact probes.
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

    /// <summary>
    /// Whether the engine holds this chunk with no blocks in it. Separate from the rendered
    /// query on purpose: that one cannot distinguish "drew terrain" from "drew nothing".
    /// A chunk the engine does not hold at all is not empty, it is absent, and the rendered
    /// query has already answered for that case.
    /// </summary>
    /// <summary>
    /// Separates the two ways a chunk can report drawn while drawing nothing.
    ///
    /// <para>
    /// "Empty" is the ordinary one and is not a fault: the flag arrives from the server in
    /// the chunk packet, the tesselator reads exactly it to skip meshing, and every column
    /// has sky above it. Refusing ownership to those cells is what broke 0.3.3 - a column
    /// counts as owned only when all of its chunks do, so excluding air excluded every
    /// column at once.
    /// </para>
    /// <para>
    /// The one worth finding is the second: a chunk that is not empty, that the engine
    /// says it drew, and for which the engine holds no mesh at all. That is ground the
    /// player should be able to see and nothing is drawing, and it is the only shape of
    /// discrepancy that can leave a hole standing still. Zero here retires the theory.
    /// </para>
    /// </summary>
    /// <summary>
    /// Whether the engine holds this chunk with no blocks in it.
    ///
    /// <para>
    /// An air chunk keeps its ownership in the tracker, because a column counts as owned
    /// only when every one of its chunks does and every column has sky above it - refusing
    /// air there is what collapsed 0.3.3. But it must never suppress a fragment. Cached
    /// terrain is an approximation and stands taller than the real world in places, so its
    /// geometry sits inside air cells; vanilla draws nothing in an air cell, so discarding
    /// there removes the only thing that was drawing and cuts the top off the cached
    /// horizon. That is the band: painted red by `.vhpaint`, invisible to `.vhholes`
    /// because the tracker is right, and unaffected by every ownership rule tried, because
    /// ownership was never wrong.
    /// </para>
    /// <para>
    /// The cost is the overlap this was originally meant to remove: cached terrain may show
    /// above real ground where the approximation overshoots. A hole outranks an overlap.
    /// </para>
    /// </summary>
    bool VanillaChunkIsAir(VanillaChunkCell cell)
    {
        try
        {
            IWorldChunk? chunk = capi.World.BlockAccessor.GetChunk(cell.X, cell.Y, cell.Z);
            return chunk is { Empty: true };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Rewrites the whole ownership atlas from the tracker's committed state.
    ///
    /// <para>
    /// The atlas is fed incrementally, by publication, and an incremental mirror is only
    /// ever as correct as the completeness of its update paths. One missing path - a column
    /// scrolling out of the ring without clearing its texels - held cached terrain suppressed
    /// over ground nothing was drawing, and no CPU-side check could see it, because every CPU
    /// structure was right and only the mirror was wrong. `.vhholes` in particular cannot
    /// find this class of fault at all: it walks cells the tracker owns, and the fault is a
    /// texel claiming ownership the tracker does not have.
    /// </para>
    /// <para>
    /// Rather than keep hunting for the next missing path, the mirror is rebuilt from the
    /// authority once a second. It costs one pass over the window and at most one upload that
    /// was already budgeted, and it bounds any future desync to a second by construction.
    /// Disagreements are counted, because a non-zero figure here means an update path is
    /// still missing and the resync is masking it.
    /// </para>
    /// </summary>
    void ResyncReadinessMask()
    {
        if (readiness == null || readinessMask == null || readinessMaskFailed) return;

        for (int z = readiness.ActiveMinChunkZ; z < readiness.ActiveMinChunkZ + readiness.ActiveDepth; z++)
        for (int x = readiness.ActiveMinChunkX; x < readiness.ActiveMinChunkX + readiness.ActiveWidth; x++)
        for (int y = 0; y < readiness.VerticalChunks; y++)
        {
            var cell = new VanillaChunkCell(x, y, z);
            // The chunk lookup stays authoritative here. The stored bit is a cache of this
            // answer for the paths that cannot ask - a rebuild, and the shader mirror - and
            // a chunk that stops being air after its publication would otherwise keep the
            // stale bit forever. Refreshing it here bounds that to one resync interval.
            bool air = VanillaChunkIsAir(cell);
            readiness.SetMaskExcluded(cell, air);
            bool owned = readiness.State(cell) == VanillaReadinessState.VanillaReady && !air;
            if (readinessMask.Set(cell, owned)) ReadinessMaskResyncs++;
        }
    }

    /// <summary>
    /// Sweeps every committed cell in the tracked window and reports the ones the engine is
    /// not actually drawing. Written because aiming a ray at a hole is unreliable - a band
    /// behind the player is not something a view ray finds, and the ground visible through
    /// a hole answers for itself. This needs no aiming and no hole in view.
    ///
    /// One pass over the window on demand, never on a frame budget.
    /// </summary>
    public string ExplainOwnedButNotDrawn(double cameraX, double cameraZ, int maxListed)
    {
        VanillaRenderReadiness? tracker = readiness;
        if (tracker == null) return "no readiness tracker; the distance handoff is drawing";

        int owned = 0;
        int notDrawn = 0;
        int unknown = 0;
        int beyondRange = 0;
        float viewDistance = ApprovedViewDistance();
        var worst = new List<(double Distance, string Text)>();

        for (int z = tracker.ActiveMinChunkZ; z < tracker.ActiveMinChunkZ + tracker.ActiveDepth; z++)
        for (int x = tracker.ActiveMinChunkX; x < tracker.ActiveMinChunkX + tracker.ActiveWidth; x++)
        for (int y = 0; y < tracker.VerticalChunks; y++)
        {
            var cell = new VanillaChunkCell(x, y, z);
            if (tracker.State(cell) != VanillaReadinessState.VanillaReady) continue;
            owned++;
            double distance = VanillaRenderReadiness.ColumnDistanceBlocksFrom(x, z, cameraX, cameraZ);

            // Tested before the chunk is even looked up. The engine range-culls every
            // terrain pool location per frame, so out here its answer is no regardless of
            // what the chunk's own flags say, and reading them would only produce the
            // reassuring verdict that hid this case in the first place.
            if (VanillaRenderReadiness.BeyondVanillaDrawRange(x, z, cameraX, cameraZ, viewDistance))
            {
                notDrawn++;
                beyondRange++;
                if (worst.Count < maxListed)
                {
                    worst.Add((distance, $"chunk {x},{y},{z} at {distance:0} blocks | "
                        + $"beyond the {viewDistance:0}-block draw range"));
                }
                continue;
            }

            IWorldChunk? chunk;
            try { chunk = capi.World.BlockAccessor.GetChunk(x, y, z); }
            catch { continue; }

            if (!VanillaChunkGeometry.TryIsVanillaDrawing(chunk, out bool drawing)) { unknown++; continue; }
            if (drawing) continue;

            notDrawn++;
            if (worst.Count < maxListed)
            {
                worst.Add((distance, $"chunk {x},{y},{z} at {distance:0} blocks | "
                    + VanillaChunkGeometry.DescribeSignals(chunk)));
            }
        }

        if (owned == 0) return "nothing is owned yet; the tracker has not committed a single cell.";
        if (notDrawn == 0)
        {
            return $"all {owned} owned cells are being drawn by the engine"
                + (unknown > 0 ? $" ({unknown} unmeasurable)" : "")
                + ". Ownership is not suppressing anything the engine has abandoned.";
        }

        worst.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return $"{notDrawn} of {owned} owned cells are NOT being drawn by the engine"
            + (beyondRange > 0 ? $" ({beyondRange} beyond the {viewDistance:0}-block draw range)" : "")
            + (unknown > 0 ? $" ({unknown} unmeasurable)" : "")
            + " -- cached terrain is suppressed there and nothing replaces it. Nearest: "
            + string.Join(" ;; ", worst.Select(w => w.Text));
    }

    /// <summary>
    /// Whether the engine holds ground here that it could actually draw. An empty chunk
    /// qualifies: there is nothing to draw and nothing for the cache to cover, and refusing
    /// it ownership is what collapsed 0.3.3, because every column has sky. A chunk that is
    /// not empty and holds no mesh does not qualify - that is ground with nothing drawing
    /// it. An unmeasurable chunk qualifies, so a game update can only cost the fix, never
    /// open the cache over live terrain.
    /// </summary>
    bool VanillaChunkHoldsDrawableGround(VanillaChunkCell cell)
    {
        try
        {
            IWorldChunk? chunk = capi.World.BlockAccessor.GetChunk(cell.X, cell.Y, cell.Z);
            if (chunk == null) return true;
            return !VanillaChunkGeometry.TryIsVanillaDrawing(chunk, out bool drawing) || drawing;
        }
        catch
        {
            return true;
        }
    }

    void MeasureDrawnChunk(VanillaChunkCell cell, double cameraX, double cameraZ)
    {
        try
        {
            IWorldChunk? chunk = capi.World.BlockAccessor.GetChunk(cell.X, cell.Y, cell.Z);
            if (chunk == null) return;

            if (chunk.Empty)
            {
                ReadinessEmptyChunksSeen++;
                return;
            }

            if (!VanillaChunkGeometry.TryIsVanillaDrawing(chunk, out bool drawingNow)) return;
            if (drawingNow) return;

            ReadinessDrawnWithoutGeometry++;

            // Only a cell we have actually committed can suppress cached terrain, so this
            // is the subset that could be showing the player a hole. Where it sits decides
            // which explanation survives: a buried chunk with no exposed faces also holds
            // no mesh, and that is invisible underground, while the same condition out at
            // the handoff ring is ground the player can see with nothing drawing it.
            if (readiness == null || readiness.State(cell) != VanillaReadinessState.VanillaReady) return;

            ReadinessOwnedWithoutGeometry++;
            if (cell.Y >= 0 && cell.Y < readinessNoGeometryByY.Length) readinessNoGeometryByY[cell.Y]++;

            double distance = VanillaRenderReadiness.ColumnDistanceBlocksFrom(
                cell.X, cell.Z, cameraX, cameraZ);
            if (ReadinessOwnedWithoutGeometryNearest < 0 || distance < ReadinessOwnedWithoutGeometryNearest)
                ReadinessOwnedWithoutGeometryNearest = distance;
            if (distance > ReadinessOwnedWithoutGeometryFarthest)
                ReadinessOwnedWithoutGeometryFarthest = distance;
        }
        catch
        {
            // A diagnostic must never be able to change ownership, including by throwing
            // into a handler that treats failure as "vanilla is not drawing here".
        }
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
        readinessNoGeometryText.Clear();
        for (int y = 0; y < readiness.VerticalChunks; y++)
        {
            if (y > 0) readinessColumnText.Append('/');
            readinessColumnText.Append(readinessColumnReady[y]);
            if (y > 0) readinessNoGeometryText.Append('/');
            readinessNoGeometryText.Append(readinessNoGeometryByY[y]);
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
            + $"{ReadinessStaleCommittedFound} stale committed found/{ReadinessAggregateRepairs} count repairs, "
            + $"{ReadinessEmptyChunksSeen} drawn-but-empty/{ReadinessDrawnWithoutGeometry} drawn-without-geometry chunks, "
            + $"owned without geometry {ReadinessOwnedWithoutGeometry} at {ReadinessOwnedWithoutGeometryNearest:0}-{ReadinessOwnedWithoutGeometryFarthest:0} blocks per Y {readinessNoGeometryText}, {ReadinessOwnershipDeniedNoGeometry} ownership denied no-geometry/{ReadinessOwnershipDeniedBeyondViewDistance} denied beyond view distance/{ReadinessMaskResyncs} mask resyncs, "
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
        gpuTelemetry.BeginFrame();
        ConfigureRenderPaths();
        ApplyGpuShadowRequest();
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
        UpdateTemporalOcclusionView(rapi.CameraMatrixOriginf, rapi.CurrentProjectionMatrix,
            camPos.X, camPos.Y, camPos.Z);
        ResolveTemporalOcclusionQueries();
        PrepareTemporalOcclusionQueries();
        traversalCulledThisFrame = 0;
        culledThisFrame = 0;
        LastTemporalOcclusionDrawsSkipped = 0;
        LastTemporalOcclusionSeamDraws = 0;
        LastTemporalOcclusionEdgeDraws = 0;

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
        if (OpaqueBackfaceCulling)
        {
            // The public API exposes the enable switch but not the concrete client's
            // GlCullFaceBack helper. The opaque stage owns the ordinary back-face mode;
            // use that state rather than binding to an internal render implementation.
            rapi.GlEnableCullFace();
        }
        else
        {
            rapi.GlDisableCullFace();
        }

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
        prog.Uniform("maskDebug", MaskDebugPaint ? 1 : 0);
        prog.Uniform("flatTopLight", FlatTopLight ? 1 : 0);
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

        renderPaths.PrepareFrame(new LodRenderFrame(
            currentWorldEpoch(), frameCounter, drawList.Count, cullDistSq));

        phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);

        skippedLastFrame.Clear();

        // Pass 1: opaque terrain. The experimental order computes distance once per
        // selected section and reuses list capacity; sorting is included in DrawCost so
        // its CPU price stays visible beside any GPU-side gain. The GPU timer ends after
        // command submission and is read only after a later frame reports it available.
        renderPaths.DrawOpaque();

        LastCulledCount = culledThisFrame; // opaque pass only: water covers a subset

        // Pass 2: water and thin cover remain two-sided. Water must be visible from below,
        // and a plant mat has no opposite face to take over when the camera is underneath.
        renderPaths.DrawWater();

        // Submission only. RenderMesh queues work for the GPU and returns, so this
        // measures the CPU cost of the draw loop -- the uniform uploads, the culling and
        // the dictionary probes -- and not what the GPU then does with it.
        DrawCost.Add(phaseStart);

        rapi.GlEnableCullFace();
        prog.Stop();
    }

    void ConfigureRenderPaths()
    {
        if (renderPathConfigured || !gpuTelemetry.ProbeAttempted) return;
        renderPathConfigured = true;
        LodRenderPathSelection selection = LodRenderPathPolicy.Evaluate(
            gpuRendererPreference, gpuTelemetry.Decision);
        renderPaths.Configure(selection);
        capi.Logger.Notification(
            "[VintageHorizons] Renderer path: visible {0}; {1}.",
            selection.VisiblePath,
            selection.Reason);
        if (selection.ShadowEnabled) AttachShadowArenas();
    }

    /// <summary>
    /// Phase 2 regional arenas. They exist only beneath an already validated shadow, hold a
    /// copy of opaque geometry that nothing draws, and are bounded by their own ceiling so
    /// dual residency cannot double an unbounded cache.
    /// </summary>
    void AttachShadowArenas()
    {
        LodGpuArenaMode mode = LodGpuArenaPolicy.Parse(
            Environment.GetEnvironmentVariable("VINTAGEHORIZONS_GPU_ARENA"));
        if (mode is LodGpuArenaMode.Off or LodGpuArenaMode.Invalid)
        {
            capi.Logger.Notification(
                "[VintageHorizons] GPU arena shadow off; the renderer shadow stays metadata-only.");
            return;
        }

        AttachShadowArenas(mode);
    }

    void AttachShadowArenas(LodGpuArenaMode mode)
    {
        LodGpuArenaPolicy.ConfigurePageBytes(
            Environment.GetEnvironmentVariable("VINTAGEHORIZONS_GPU_ARENA_PAGE_MB"));
        long ceiling = LodGpuArenaPolicy.CeilingBytes(
            Environment.GetEnvironmentVariable("VINTAGEHORIZONS_GPU_ARENA_MB"));
        try
        {
            var backend = new LodGpuOpenGlArenaBackend(
                message => capi.Logger.Warning("{0}", message));
            gpuShadow.AttachMirror(new LodGpuGeometryMirror(
                backend, ceiling, verify: mode == LodGpuArenaMode.Verify));
            shadowBuilder = new LodGpuIndirectBuilder();
            capi.Logger.Notification(
                "[VintageHorizons] GPU arena shadow on: {0} MiB ceiling, {1} MiB vertex pages, "
                + "{2} page sets, content verification {3}. Indirect commands are built for "
                + "measurement only; nothing is drawn from these buffers.",
                ceiling / (1024 * 1024),
                LodGpuArenaPolicy.VertexPageBytes / (1024 * 1024),
                LodGpuArenaPolicy.PageSets(ceiling),
                mode == LodGpuArenaMode.Verify ? "on" : "off");
        }
        catch (Exception e)
        {
            capi.Logger.Warning(
                "[VintageHorizons] GPU arena shadow could not start; visible legacy rendering "
                + "is unchanged: {0}", e.Message);
        }
    }

    /// <summary>Arena occupancy for the periodic report, or null when no arena is attached.</summary>
    public string? DescribeGpuArena() => gpuShadow.Mirror?.Describe();

    // ---- In-game GPU shadow switch ----

    LodGpuArenaMode? requestedShadowMode;

    /// <summary>
    /// Asks for the measurement shadow to be turned on or off. Called from a chat command,
    /// so it only records the request: capability probing, GL resource creation and teardown
    /// all belong to the render thread and happen at the start of the next frame.
    /// </summary>
    public bool RequestGpuShadow(string mode)
    {
        LodGpuArenaMode parsed = LodGpuArenaPolicy.Parse(mode);
        if (parsed == LodGpuArenaMode.Invalid) return false;

        requestedShadowMode = parsed;
        if (parsed != LodGpuArenaMode.Off) gpuTelemetry.RequestRuntimeValidation();
        return true;
    }

    void ApplyGpuShadowRequest()
    {
        if (requestedShadowMode is not LodGpuArenaMode mode) return;
        if (!gpuTelemetry.ProbeAttempted) return;
        requestedShadowMode = null;

        LodRenderPathSelection selection = LodRenderPathPolicy.Evaluate(
            mode == LodGpuArenaMode.Off ? "off" : "shadow", gpuTelemetry.Decision);

        if (mode == LodGpuArenaMode.Off)
        {
            renderPaths.SetShadowEnabled(false, selection);
            gpuShadow.DetachMirror();
            shadowBuilder = null;
            ShadowIndirectCommands = 0;
            ShadowIndirectBatches = 0;
            ShadowIndirectDropped = 0;
            ShadowIndirectMissing = 0;
            ShadowIndirectCoverage = 0;
            capi.Logger.Notification(
                "[VintageHorizons] GPU measurement shadow off; every arena buffer released.");
            return;
        }

        if (!renderPaths.SetShadowEnabled(true, selection))
        {
            capi.Logger.Warning(
                "[VintageHorizons] GPU measurement shadow refused: {0}", selection.Reason);
            return;
        }

        AttachShadowArenas(mode);
        RemeshForShadowBackfill();
    }

    /// <summary>
    /// The arenas only ever see geometry as it is published, so a shadow switched on
    /// mid-session would otherwise measure whatever happens to be remeshed afterwards.
    /// Marking the live sections dirty replays them through the ordinary publication path,
    /// at the ordinary budget, until the mirror matches what is actually on screen.
    /// </summary>
    void RemeshForShadowBackfill()
    {
        int queued = 0;
        foreach (long key in sectionMeshes.Keys)
        {
            world.RenderDirty.Add(key);
            queued++;
        }
        capi.Logger.Notification(
            "[VintageHorizons] GPU measurement shadow on; {0} live sections queued for "
            + "re-mesh so the arenas match what is drawn. Give it a moment before reading "
            + "the numbers.", queued);
    }

    /// <summary>One line for the in-game switch, describing what is measured right now.</summary>
    public string DescribeGpuShadow()
    {
        LodGpuGeometryMirror? mirror = gpuShadow.Mirror;
        if (mirror == null)
        {
            return gpuTelemetry.ProbeAttempted && !gpuTelemetry.Decision.Tier1RuntimeValidated
                ? "off - this driver did not pass the GPU capability checks: "
                    + gpuTelemetry.Decision.Reason
                : "off";
        }

        return $"on{(mirror.Verifying ? " with content verification" : "")}. "
            + $"{mirror.Count} sections mirrored, {mirror.LiveBytes / (1024.0 * 1024.0):0.0} MiB live. "
            + $"Last frame {ShadowIndirectCommands} of {ShadowIndirectCommands + ShadowIndirectMissing} "
            + $"drawn sections would have been {ShadowIndirectBatches} multi-draw batches "
            + $"({ShadowIndirectCoverage:P0} of drawn terrain covered"
            + (ShadowIndirectDropped > 0 ? $", {ShadowIndirectDropped} spans stale" : "")
            + "). Nothing is drawn from the arenas.";
    }

    void PrepareLegacyFrame(LodRenderFrame frame)
    {
        renderCullDistanceSquared = frame.CullDistanceSquared;
    }

    void DrawLegacyOpaque()
    {
        gpuTelemetry.BeginOpaque();
        shadowBuilder?.Begin();
        try
        {
            if (OpaqueFrontToBack)
            {
                LodOpaqueDrawOrder.FillFrontToBack(
                    opaqueFrontToBack, drawList, camPos.X, camPos.Z);
                foreach (LodOpaqueDrawEntry entry in opaqueFrontToBack)
                {
                    long key = entry.Key;
                    if (!sectionMeshes.TryGetValue(key, out MeshRef? mesh)) continue;
                    if (SkipVanillaOwnedSection(key)) continue;
                    if (!SetupSectionTransform(key, renderCullDistanceSquared)) continue;
                    RenderOpaqueMesh(key, mesh);
                }
            }
            else
            {
                foreach (long key in drawList)
                {
                    if (!sectionMeshes.TryGetValue(key, out MeshRef? mesh)) continue;
                    if (SkipVanillaOwnedSection(key)) continue;
                    if (!SetupSectionTransform(key, renderCullDistanceSquared)) continue;
                    RenderOpaqueMesh(key, mesh);
                }
            }
        }
        finally
        {
            gpuTelemetry.EndOpaque();
            EndShadowCommands();
        }
    }

    /// <summary>
    /// Closes the shadow command list for this frame. It is only ever read as telemetry:
    /// no buffer is uploaded and no draw is issued from it in this phase.
    /// </summary>
    void EndShadowCommands()
    {
        LodGpuGeometryMirror? mirror = gpuShadow.Mirror;
        if (shadowBuilder == null || mirror == null) return;
        try
        {
            shadowBuilder.End(mirror.VertexArena, mirror.IndexArena);
            ShadowIndirectCommands = shadowBuilder.CommandCount;
            ShadowIndirectBatches = shadowBuilder.Batches.Count;
            ShadowIndirectDropped = shadowBuilder.CandidatesDropped;
            ShadowIndirectMissing = shadowBuilder.MissingSections;
            ShadowIndirectCoverage = shadowBuilder.Coverage;
        }
        catch (Exception e)
        {
            shadowBuilder = null;
            capi.Logger.Warning(
                "[VintageHorizons] Shadow indirect command building disabled; visible legacy "
                + "rendering is unchanged: {0}", e.Message);
        }
    }

    public int ShadowIndirectCommands { get; private set; }
    public int ShadowIndirectBatches { get; private set; }
    public int ShadowIndirectDropped { get; private set; }
    public int ShadowIndirectMissing { get; private set; }
    public double ShadowIndirectCoverage { get; private set; }

    void DrawLegacyWater()
    {
        var rapi = capi.Render;
        rapi.GlDisableCullFace();
        rapi.GlToggleBlend(true);
        gpuTelemetry.BeginWater();
        try
        {
            foreach (long key in drawList)
            {
                if (!waterMeshes.TryGetValue(key, out MeshRef? mesh)) continue;
                if (SkipVanillaOwnedSection(key)) continue;
                if (!SetupSectionTransform(key, renderCullDistanceSquared)) continue;
                SubmitWaterMesh(key, mesh);
            }
        }
        finally { gpuTelemetry.EndWater(); }
        rapi.GlToggleBlend(false);
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
        // A skipped section never reaches the shader, so it could never be painted.
        if (MaskDebugPaint) return false;
        if (!WholeMeshSkip) return false;
        if (!readinessMaskActive || readiness == null) return false;
        if (readiness.Classify(key) != VanillaSectionOwnership.VanillaOnly) return false;
        VanillaOwnedDrawsSkipped++;
        skippedLastFrame.Add(key);
        return true;
    }

    readonly HashSet<long> skippedLastFrame = new();

    public string DescribeOcclusionCulling() => OcclusionCullingEnabled
        ? "on: cached terrain renders after vanilla for ordinary depth rejection"
        : "off: cached terrain renders before vanilla";

    public string DescribeTemporalOcclusion()
    {
        if (temporalOcclusionFailed) return "failed; drawing all terrain";
        if (!TemporalOcclusionEnabled) return "off";
        if (!OcclusionCullingEnabled) return "suspended until post-vanilla order is on";
        return $"on ({TemporalOcclusionProfileName}): {LastTemporalOcclusionDrawsSkipped} hidden opaque draws skipped last frame, "
            + $"{LastTemporalOcclusionSeamDraws} seam draws protected, "
            + $"{LastTemporalOcclusionEdgeDraws} turning-edge draws protected, "
            + $"{TemporalOcclusionHiddenResults}/{TemporalOcclusionResults} hidden results, "
            + $"{TemporalOcclusionStaleResults} stale results/{TemporalOcclusionGlobalInvalidations} global invalidations, "
            + $"{temporalOcclusionPending.Count} queries pending";
    }

    bool TemporalOcclusionActive => TemporalOcclusionEnabled
        && OcclusionCullingEnabled && !temporalOcclusionFailed;

    void RenderOpaqueMesh(long key, MeshRef mesh)
    {
        if (!TemporalOcclusionActive)
        {
            SubmitOpaqueMesh(key, mesh);
            return;
        }

        if (!temporalOcclusionQueries.TryGetValue(key, out TemporalOcclusionQuery? query))
        {
            query = new TemporalOcclusionQuery();
            temporalOcclusionQueries.Add(key, query);
        }

        // A mixed section is the exact vanilla/cache ownership seam: some of its
        // fragments are deliberately discarded for ready vanilla chunks and the rest
        // complete cached coverage. Never let one zero-sample result hide that entire
        // section, because the small cache-owned remainder is precisely the seam the
        // player notices. Cache-only sections beyond it retain full temporal culling.
        if (readinessMaskActive && readiness != null
            && readiness.Classify(key) == VanillaSectionOwnership.Mixed)
        {
            query.State.Invalidate(temporalOcclusionEpoch);
            LastTemporalOcclusionSeamDraws++;
            SubmitOpaqueMesh(key, mesh);
            return;
        }

        // A previous-frame hidden answer is least reliable where a fast yaw is bringing
        // terrain onto the screen. Protect only a narrow side band, and only while the
        // view is actually turning. The centre retains temporal culling, stationary views
        // pay nothing, and the extreme profile deliberately disables this guard so the
        // visual limit remains available for comparison.
        if (temporalOcclusionTurningThisFrame && temporalOcclusionEdgeGuard > 0
            && TemporalOcclusionNearSideEdge(key))
        {
            query.State.Invalidate(temporalOcclusionEpoch);
            LastTemporalOcclusionEdgeDraws++;
            SubmitOpaqueMesh(key, mesh);
            return;
        }

        int hiddenProbeInterval = temporalOcclusionTurningThisFrame
            ? Math.Min(temporalOcclusionHiddenProbeIntervalFrames,
                temporalOcclusionTurningProbeIntervalFrames)
            : temporalOcclusionHiddenProbeIntervalFrames;
        if (!query.State.ShouldDraw(frameCounter, temporalOcclusionEpoch,
            hiddenProbeInterval))
        {
            TemporalOcclusionDrawsSkipped++;
            LastTemporalOcclusionDrawsSkipped++;
            return;
        }

        if (!query.State.ShouldIssueQuery(frameCounter, temporalOcclusionEpoch,
            temporalOcclusionVisibleQueryIntervalFrames,
            hiddenProbeInterval))
        {
            SubmitOpaqueMesh(key, mesh);
            return;
        }

        // GL queries belong to the render context and cannot move to a worker. A hard
        // per-frame issue budget prevents many sections whose intervals align from all
        // adding driver commands to the same frame.
        if (temporalOcclusionQueriesIssuedThisFrame >= TemporalOcclusionQueryIssuesPerFrame)
        {
            SubmitOpaqueMesh(key, mesh);
            return;
        }

        // A renderer earlier in this stage may deliberately span a query across us.
        // Beginning the same target would fail, and blindly ending afterwards could close
        // the other renderer's query. Check once per frame and simply draw when occupied.
        if (!temporalOcclusionQueryTargetAvailable)
        {
            SubmitOpaqueMesh(key, mesh);
            return;
        }

        try
        {
            if (query.QueryId == 0) query.QueryId = GL.GenQuery();
            GL.BeginQuery(QueryTarget.AnySamplesPassed, query.QueryId);
        }
        catch (Exception e)
        {
            DisableTemporalOcclusion(e);
            // Query failure is an optimization failure, never a reason to lose terrain.
            SubmitOpaqueMesh(key, mesh);
            return;
        }

        query.State.BeginQuery(frameCounter, temporalOcclusionEpoch);
        temporalOcclusionPending.Add(key);
        temporalOcclusionQueriesIssuedThisFrame++;
        TemporalOcclusionQueries++;
        try
        {
            // This is both the ordinary visible draw and the exact visibility probe.
            // A hidden section therefore adds only two query commands, not proxy
            // geometry plus a conditional submission of the real mesh.
            SubmitOpaqueMesh(key, mesh);
        }
        finally
        {
            try { GL.EndQuery(QueryTarget.AnySamplesPassed); }
            catch (Exception e) { DisableTemporalOcclusion(e); }
        }
    }

    void SubmitOpaqueMesh(long key, MeshRef mesh)
    {
        OpaqueDrawCalls++;
        if (liveMeshStats.TryGetValue(key, out LodLiveMeshStats stats))
        {
            OpaqueDrawVertices += stats.OpaqueVertices;
            OpaqueDrawIndices += stats.OpaqueIndices;
        }
        RecordShadowCommand(key);
        capi.Render.RenderMesh(mesh);
    }

    void SubmitWaterMesh(long key, MeshRef mesh)
    {
        WaterDrawCalls++;
        if (liveMeshStats.TryGetValue(key, out LodLiveMeshStats stats))
        {
            WaterDrawVertices += stats.WaterVertices;
            WaterDrawIndices += stats.WaterIndices;
        }
        capi.Render.RenderMesh(mesh);
    }

    void ResolveTemporalOcclusionQueries()
    {
        if (!TemporalOcclusionActive || temporalOcclusionPending.Count == 0) return;

        try
        {
            int checkedThisFrame = 0;
            for (int i = temporalOcclusionPending.Count - 1;
                i >= 0 && checkedThisFrame < TemporalOcclusionResultChecksPerFrame;
                i--, checkedThisFrame++)
            {
                long key = temporalOcclusionPending[i];
                if (!temporalOcclusionQueries.TryGetValue(key, out TemporalOcclusionQuery? query)
                    || query.QueryId == 0)
                {
                    temporalOcclusionPending.RemoveAt(i);
                    continue;
                }

                GL.GetQueryObject(query.QueryId, GetQueryObjectParam.QueryResultAvailable,
                    out int available);
                if (available == 0) continue;

                GL.GetQueryObject(query.QueryId, GetQueryObjectParam.QueryResult, out int samples);
                if (query.State.CompleteQuery(samples != 0, temporalOcclusionEpoch))
                {
                    TemporalOcclusionResults++;
                    if (samples == 0) TemporalOcclusionHiddenResults++;
                }
                else
                {
                    TemporalOcclusionStaleResults++;
                }
                temporalOcclusionPending.RemoveAt(i);
            }
        }
        catch (Exception e)
        {
            DisableTemporalOcclusion(e);
        }
    }

    bool TemporalOcclusionNearSideEdge(long key)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        double relX = LodWorld.KeySx(key) * (double)footprint - camPos.X;
        double relZ = LodWorld.KeySz(key) * (double)footprint - camPos.Z;
        return frustum.BoxNearSideEdge(
            relX, -camPos.Y, relZ,
            relX + footprint, worldHeight - camPos.Y, relZ + footprint,
            temporalOcclusionEdgeGuard);
    }

    void PrepareTemporalOcclusionQueries()
    {
        temporalOcclusionQueriesIssuedThisFrame = 0;
        temporalOcclusionQueryTargetAvailable = false;
        if (!TemporalOcclusionActive) return;

        try
        {
            GL.GetQuery(QueryTarget.AnySamplesPassed, GetQueryParam.CurrentQuery,
                out int activeQuery);
            temporalOcclusionQueryTargetAvailable = activeQuery == 0;
        }
        catch (Exception e)
        {
            DisableTemporalOcclusion(e);
        }
    }

    void UpdateTemporalOcclusionView(float[] view, float[] projection,
        double cameraX, double cameraY, double cameraZ)
    {
        temporalOcclusionTurningThisFrame = temporalOcclusionHasPreviousFrameView
            && ViewRotationChanged(view, temporalOcclusionPreviousFrameView);
        Array.Copy(view, temporalOcclusionPreviousFrameView, 16);
        temporalOcclusionHasPreviousFrameView = true;

        bool changed = !temporalOcclusionHasView
            || TranslationExceeded(cameraX, cameraY, cameraZ,
                temporalOcclusionCameraX, temporalOcclusionCameraY, temporalOcclusionCameraZ,
                temporalOcclusionTranslationLimitBlocks)
            || ViewRotationExceeded(view, temporalOcclusionView,
                temporalOcclusionRotationMatrixLimit)
            || ProjectionChanged(projection, temporalOcclusionProjection);
        if (!changed) return;

        Array.Copy(view, temporalOcclusionView, 16);
        Array.Copy(projection, temporalOcclusionProjection, 16);
        temporalOcclusionCameraX = cameraX;
        temporalOcclusionCameraY = cameraY;
        temporalOcclusionCameraZ = cameraZ;
        temporalOcclusionHasView = true;
        InvalidateTemporalOcclusionScene();
    }

    internal static bool TranslationExceeded(double x, double y, double z,
        double anchorX, double anchorY, double anchorZ, double limit)
    {
        double dx = x - anchorX;
        double dy = y - anchorY;
        double dz = z - anchorZ;
        return dx * dx + dy * dy + dz * dz >= limit * limit;
    }

    internal static bool ViewRotationExceeded(float[] current, float[] previous, float limit)
    {
        if (current.Length < 16) return true;
        // CameraMatrixOriginf has no useful world translation for cached geometry. Only
        // compare the 3x3 orientation basis; translation has its own block-space bound.
        ReadOnlySpan<int> rotation = [0, 1, 2, 4, 5, 6, 8, 9, 10];
        foreach (int i in rotation)
        {
            if (Math.Abs(current[i] - previous[i])
                >= limit) return true;
        }
        return false;
    }

    internal static bool ViewRotationChanged(float[] current, float[] previous)
    {
        if (current.Length < 16) return true;
        ReadOnlySpan<int> rotation = [0, 1, 2, 4, 5, 6, 8, 9, 10];
        foreach (int i in rotation)
        {
            if (BitConverter.SingleToInt32Bits(current[i])
                != BitConverter.SingleToInt32Bits(previous[i])) return true;
        }
        return false;
    }

    internal static bool ProjectionChanged(float[] current, float[] previous)
    {
        if (current.Length < 16) return true;
        for (int i = 0; i < 16; i++)
        {
            if (BitConverter.SingleToInt32Bits(current[i])
                != BitConverter.SingleToInt32Bits(previous[i])) return true;
        }
        return false;
    }

    void InvalidateTemporalOcclusionScene()
    {
        TemporalOcclusionGlobalInvalidations++;
        temporalOcclusionEpoch++;
        if (temporalOcclusionEpoch == long.MaxValue)
        {
            temporalOcclusionEpoch = 1;
            foreach (TemporalOcclusionQuery query in temporalOcclusionQueries.Values)
                query.State.Invalidate(temporalOcclusionEpoch);
        }
    }

    void DisableTemporalOcclusion(Exception error)
    {
        temporalOcclusionFailed = true;
        if (temporalOcclusionFailureReported) return;
        temporalOcclusionFailureReported = true;
        capi.Logger.Warning(
            "[VintageHorizons] delayed occlusion disabled; all cached terrain remains visible: {0}",
            error.Message);
    }

    void DisposeTemporalOcclusionQueries()
    {
        foreach (TemporalOcclusionQuery query in temporalOcclusionQueries.Values)
        {
            if (query.QueryId == 0) continue;
            try { GL.DeleteQuery(query.QueryId); }
            catch { /* teardown must not throw over a query the driver already released */ }
        }
        temporalOcclusionQueries.Clear();
        temporalOcclusionPending.Clear();
        temporalOcclusionHasView = false;
        temporalOcclusionHasPreviousFrameView = false;
        temporalOcclusionTurningThisFrame = false;
    }

    void RemoveTemporalOcclusionQuery(long key)
    {
        if (!temporalOcclusionQueries.Remove(key, out TemporalOcclusionQuery? query)) return;
        temporalOcclusionPending.Remove(key);
        if (query.QueryId == 0) return;
        try { GL.DeleteQuery(query.QueryId); }
        catch { /* eviction must not fail over an optional driver object */ }
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
        //
        // Set as two integer uniforms, never as one Vec2i: that overload reaches
        // glUniform2f, which an integer uniform rejects with GL_INVALID_OPERATION, so the
        // origin silently stayed at zero and the per-fragment mask addressed the wrong
        // cells for every section in the world. See G42.
        if (readinessMaskActive)
        {
            prog.Uniform("maskSectionOriginX", (int)(originX / VanillaReadinessMask.ChunkBlocks));
            prog.Uniform("maskSectionOriginZ", (int)(originZ / VanillaReadinessMask.ChunkBlocks));
        }

        // Sides that border on never-captured area, so the shader can dissolve them
        // into the horizon instead of leaving a cliff at the edge of what we've seen.
        prog.Uniform("sectionSize", (float)footprint);
        byte openEdges = (byte)(
            (HasNeighbourData(key, -1, 0) ? 0 : LodGpuSectionFacts.OpenMinusX)
            | (HasNeighbourData(key, 1, 0) ? 0 : LodGpuSectionFacts.OpenPlusX)
            | (HasNeighbourData(key, 0, -1) ? 0 : LodGpuSectionFacts.OpenMinusZ)
            | (HasNeighbourData(key, 0, 1) ? 0 : LodGpuSectionFacts.OpenPlusZ));
        prog.Uniform("openEdges",
            (openEdges & LodGpuSectionFacts.OpenMinusX) != 0 ? 1f : 0f,
            (openEdges & LodGpuSectionFacts.OpenPlusX) != 0 ? 1f : 0f,
            (openEdges & LodGpuSectionFacts.OpenMinusZ) != 0 ? 1f : 0f,
            (openEdges & LodGpuSectionFacts.OpenPlusZ) != 0 ? 1f : 0f);

        // The same values a multi-draw would have to read from a buffer instead. Captured
        // here so the shadow command list is built from the traversal's real decisions,
        // not from a second, possibly disagreeing, walk of the draw list.
        if (shadowBuilder != null)
        {
            lastSectionFacts = new LodGpuSectionFacts(
                key,
                (float)relX, (float)-camPos.Y, (float)relZ,
                footprint,
                (float)originX, (float)originZ,
                LodWorld.ColumnStepBlocks(LodWorld.KeyLevel(key)),
                (int)(originX / VanillaReadinessMask.ChunkBlocks),
                (int)(originZ / VanillaReadinessMask.ChunkBlocks),
                openEdges);
        }
        return true;
    }

    LodGpuIndirectBuilder? shadowBuilder;
    LodGpuSectionFacts lastSectionFacts;

    /// <summary>
    /// Records what a regional multi-draw would have submitted for a section the legacy
    /// path is drawing right now. It runs after every CPU decision the visible path makes -
    /// ownership skip, distance cap, frustum, temporal occlusion - so the command list is
    /// the one Phase 3 would actually issue, and the batch count it reports is the real
    /// answer to how far draw calls would fall.
    /// </summary>
    void RecordShadowCommand(long key)
    {
        // Ordered so the visible path pays one null field read per drawn section when the
        // shadow is off, which is the ordinary case.
        if (shadowBuilder == null) return;
        LodGpuGeometryMirror? mirror = gpuShadow.Mirror;
        if (mirror == null) return;
        if (!mirror.TryGet(key, out LodGpuGeometryMirror.MirroredSection section))
        {
            // Drawn, but the arenas never managed to hold it. Counted rather than ignored:
            // batches over a partial mirror are not a draw-call reduction, and silence here
            // is exactly what made the first measured run look better than it was.
            shadowBuilder.AddMissing();
            return;
        }
        shadowBuilder.Add(section, lastSectionFacts);
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
        renderPaths.Clear(currentWorldEpoch());
    }

    void ClearMeshesLegacy(long worldEpoch)
    {
        foreach (MeshRef meshRef in sectionMeshes.Values) meshRef.Dispose();
        foreach (MeshRef meshRef in waterMeshes.Values) meshRef.Dispose();
        sectionMeshes.Clear();
        waterMeshes.Clear();
        liveMeshStats.Clear();
        LiveOpaqueVertices = LiveOpaqueIndices = 0;
        LiveWaterVertices = LiveWaterIndices = 0;
        LiveGpuMeshBytes = 0;
        meshedWithoutNeighbor.Clear();
        meshBounds.Clear();
        farPlaneState.Reset();
        appliedZFar = 0;
        EffectiveFarDistance = LodFarDistance.MinimumProjectionDistance;
        meshJobInFlight.Clear();
        lastResidencyMs.Clear();
        meshEvictionOrder.Clear();
        meshEvictionQueued.Clear();
        seasonalRefreshActive = false;
        seasonalStateInitialized = false;
        seasonalRefreshSlot = 0;
        snowLineY = pendingSnowLineY = 99999;
        DisposeTemporalOcclusionQueries();
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
        renderPaths.Dispose();
        gpuTelemetry.Dispose();
        // Same reason as the meshes: the mask texture is ours, and a shutdown that never
        // reaches the engine call must still not leak it.
        DisposeReadinessMaskTexture();
        world.SectionBecameResident -= OnSectionBecameResident;
        capi.Event.ChunkDirty -= OnReadinessChunkDirty;
        capi.Event.ReloadShader -= LoadShader;
        if (rendererRegistered)
        {
            capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
            rendererRegistered = false;
        }
    }
}
