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
    readonly LodGpuFailureInjection gpuFailureInjection;
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

    /// <summary>
    /// Arms the delayed GPU timers for a session that did not start under the benchmark
    /// harness. The depth phases are gated on GPU time and nothing else, so a report that
    /// prints 0.0us because nobody set an environment variable is indistinguishable from a
    /// pass. Returns whether timing is on now; see LodGpuTelemetry.DescribeTiming for why
    /// not.
    /// </summary>
    public bool EnableGpuTiming() => gpuTelemetry.RequestTiming();

    /// <summary>Plain-language state of the GPU timers, for any report that prints their figures.</summary>
    public string DescribeGpuTiming() => gpuTelemetry.DescribeTiming();
    public LodPhaseCost GpuOpaqueCost => gpuTelemetry.OpaqueCost;
    public LodPhaseCost GpuSplitNearCost => gpuTelemetry.SplitNearCost;
    public LodPhaseCost GpuSplitFarCost => gpuTelemetry.SplitFarCost;

    /// <summary>GPU time to copy the depth buffer and reduce every pyramid level.</summary>
    public LodPhaseCost GpuHzbCost => gpuTelemetry.HzbGpuCost;
    public LodPhaseCost GpuSplitHzbCost => gpuTelemetry.SplitHzbGpuCost;

    /// <summary>
    /// GPU time for the classification dispatch alone. Separate from the build because
    /// only this one scales with the sampling width, and the width was widened ninefold
    /// in fetch count; a combined figure could hide that completely.
    /// </summary>
    public LodPhaseCost GpuClassifyCost => gpuTelemetry.ClassifyGpuCost;
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
    /// Shade by the engine's own light vector rather than by the sun. `LightPosition3D` is
    /// the sun position lerped toward the moon as moonlight overtakes sunlight, and vanilla
    /// terrain shades by it; without this, cached slopes are lit all night by a sun that is
    /// below the horizon while the vanilla slopes beside them are lit by the moon. Identical
    /// to the old behaviour while the sun is up. `.vhlight moondir off` to compare.
    /// </summary>
    public bool LightMoonDirection { get; set; } = true;

    /// <summary>
    /// Use vanilla's shade ramp, `max(0.45, 0.5 + 0.5 * dot)`, instead of
    /// `0.55 + 0.45 * max(0, dot)`. The old floor left every away-facing slope 22% brighter
    /// than the vanilla slope beside it, at every time of day. `.vhlight ramp off` compares.
    /// </summary>
    public bool LightVanillaRamp { get; set; } = true;

    /// <summary>
    /// Light by `Ambient.BlendedAmbientColor`, which is exactly what vanilla's terrain
    /// shader multiplies by, instead of `SunColor * DayLightStrength`. Vanilla derives its
    /// ambient from `ReflectColor`, floored at a blue night colour once the sun is down;
    /// `SunColor` has no such floor and stays orange. Decoding the engine's own sunlight
    /// ramp offline showed the two hues invert after sundown - vanilla turns blue-grey while
    /// cached terrain turns orange - which is the reported "sunset and night look
    /// substantially different". `.vhlight ambient off` to compare.
    /// </summary>
    public bool LightAmbientColor { get; set; } = true;

    /// <summary>
    /// Apply vanilla's daylight brightening, `1 + max(0, shadowIntensity * 2 - 1.66) / 1.5`.
    /// The engine uploads `shadowIntensity` from `DropShadowIntensity`, which holds at 1
    /// while the sun is high and falls to 0 as it sets, so vanilla terrain is 22.7% brighter
    /// than cached terrain at midday and equal to it at dusk. This is the only one of the
    /// four that changes broad daylight, which is the case the owner reports as already
    /// looking right, so it has its own switch. `.vhlight boost off` to compare.
    /// </summary>
    public bool LightDayBoost { get; set; } = true;

    /// <summary>
    /// Fade the far edge of the cache toward the sky the engine actually draws. Every engine
    /// shader that calls `getSkyColorAt` passes `SkyDaylight`, which is
    /// `1.25 * max(DayLightStrength - MoonLightStrength / 2, 0.05)` attenuated above 1,000
    /// blocks over sea level - not `DayLightStrength`, which is what this shader passed. The
    /// band was therefore dissolving terrain into a sky a quarter too dim by day and too dark
    /// at night, with the error moving through the day like the terrain terms above.
    /// `.vhlight sky off` to compare.
    /// </summary>
    public bool LightSkyDayLight { get; set; } = true;

    /// <summary>
    /// The engine's `SkyDaylight`, reconstructed. `DefaultShaderUniforms.SkyDaylight` is
    /// `internal`, so a mod cannot read the value the engine computed; every input to it is
    /// public, and the formula is transcribed from `SystemRenderSky.OnRenderFrame3D`.
    /// </summary>
    float SkyDayLight()
    {
        IClientGameCalendar calendar = capi.World.Calendar;
        float light = 1.25f * Math.Max(calendar.DayLightStrength - calendar.MoonLightStrength / 2f, 0.05f);
        // Thin air high above the world dims the sky rather than brightening it. Sea level
        // comes from the world, not from a constant: it moves with world configuration.
        double above = (capi.World.Player.Entity.Pos.Y - capi.World.SeaLevel - 1000.0) / 30000.0;
        float attenuation = 1f - (float)Math.Clamp(above, 0.0, 1.0);
        return Math.Max(0f, light * attenuation);
    }

    /// <summary>
    /// Opaque terrain has outward counter-clockwise winding and uses hardware back-face
    /// rejection; blended water and thin cover stay two-sided. Human testing across cliffs,
    /// caves, overhangs, and high/low views found no visual difference, while a same-view
    /// A/B improved 218 to 260 FPS. `.vhbackface off` remains the immediate comparison and
    /// compatibility fallback.
    /// </summary>
    public bool OpaqueBackfaceCulling { get; set; } = true;

    /// <summary>
    /// Cull each section with the vertical extent of its own mesh rather than a
    /// bedrock-to-sky box. Sections never recorded how tall the terrain inside them was,
    /// so looking up or down kept every section the side planes did not reject. The gate
    /// is one-sided - a bound that is too tight deletes terrain a player can see - so
    /// `.vhheight off` restores the full-height box for an immediate same-view comparison.
    /// </summary>
    public bool SectionHeightCulling { get; set; } = true;

    /// <summary>
    /// Cull whole quadtree SUBTREES with the aggregate extent of the meshes resident under
    /// them rather than a bedrock-to-sky box. The per-section bound above only tightens the
    /// leaf a section draws with; a coarse node still had to be traversed with a box
    /// spanning the world, and rejecting a coarse node rejects everything beneath it.
    /// Same one-sided gate, same fallback rule: a subtree that cannot supply an aggregate
    /// keeps the full-height box. `VINTAGEHORIZONS_SUBTREE_HEIGHT_CULLING=0` restores it
    /// everywhere for a controlled comparison.
    /// </summary>
    public bool SubtreeHeightCulling { get; set; } = true;

    /// <summary>
    /// Do not build underground geometry the surface-light envelope cannot reach. Measured
    /// offline at about 61% of estimated subterranean geometry, and unlike the culling
    /// switches above it removes the
    /// geometry rather than deciding whether to draw it - so it also takes memory, mesh
    /// time and upload bandwidth with it.
    ///
    /// Default ON after the surface-only rule and its tunnel guards were accepted in game.
    /// `.vhcavecull off` remains the immediate fallback; changing it re-meshes the world so the
    /// switch can be judged in one place without relogging. The environment can pin it off.
    /// </summary>
    public bool CaveCulling { get; set; } =
        Environment.GetEnvironmentVariable("VINTAGEHORIZONS_CAVE_CULLING") != "0";

    int caveCullReach = LodCaveCull.DefaultReach;

    /// <summary>How far daylight spreads before a cavity counts as unreachable, in blocks.</summary>
    public int CaveCullReach
    {
        get => caveCullReach;
        set => caveCullReach = Math.Clamp(value, 1, 200);
    }

    /// <summary>
    /// Rebuild every resident mesh. Cave culling decides what geometry EXISTS rather than
    /// what is drawn, so flipping it changes nothing until the meshes are made again - and
    /// a switch whose effect only arrives on the next relog cannot be A/B'd by a person.
    /// </summary>
    public int RemeshAll()
    {
        int queued = 0;
        foreach (long key in sectionMeshes.Keys.Concat(waterMeshes.Keys).Distinct().ToArray())
        {
            world.RenderDirty.Add(key);
            queued++;
        }
        return queued;
    }

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
        frameTimeline.Reset();
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
        VerticalCulledSections = 0;
        VerticalCulledMax = 0;
        SubtreeCulledNodes = 0;
        SubtreeCulledMeshes = 0;
        SubtreeCulledMax = 0;
        // Through the shared reset rather than by repeating it. The depth report prints CPU
        // time, GPU time, hidden share and the picture's own frame counts on adjacent lines,
        // and if the periodic reset cleared some of those and not others, the reader would be
        // comparing figures from different intervals with nothing on screen saying so.
        ResetDepthPyramidCounters();
        HzbHiddenBeyondQueries = 0;
        HzbAgreedHidden = 0;
        HzbVerdictsAcrossViewChange = 0;
        sectionHeightStats.Reset();
        readiness?.ResetTelemetry();
        readinessMask?.ResetTelemetry();
    }

    readonly Dictionary<long, MeshRef> sectionMeshes = new();
    readonly Dictionary<long, MeshRef> waterMeshes = new();
    readonly Dictionary<long, LodLiveMeshStats> liveMeshStats = new();

    readonly record struct LodLiveMeshStats(
        int OpaqueVertices, int OpaqueIndices, int WaterVertices, int WaterIndices,
        LodSectionHeights Heights)
    {
        public long Bytes => OpaqueVertices * 16L + OpaqueIndices * sizeof(int)
            + WaterVertices * 16L + WaterIndices * sizeof(int);
    }
    readonly LodSubtreeHeights subtreeHeights = new();
    readonly LodMeshBounds meshBounds = new();
    readonly LodFarPlaneState farPlaneState = new();
    readonly HashSet<long> meshJobInFlight = new();
    readonly LodRenderDirtyScheduler dirtyScheduler = new();
    // The first LOD band is where the eye judges sharpness first. Give it three quarters
    // of the unchanged global admission/request allowance while reserving enough outward
    // capacity to keep all eight lanes moving. This changes priority, not total pressure.
    internal const int FoundationDemandOutstanding = 24;
    internal const int FoundationDemandRequestsPerFrame = 6;
    internal const int OutwardDemandOutstanding = 8;
    internal const int OutwardDemandRequestsPerFrame = 2;
    readonly LodRadialDemandPlanner foundationDemand = new(
        outstandingLimit: FoundationDemandOutstanding,
        requestsPerFrame: FoundationDemandRequestsPerFrame);
    readonly LodRadialDemandPlanner radialDemand = new(
        outstandingLimit: OutwardDemandOutstanding,
        requestsPerFrame: OutwardDemandRequestsPerFrame);
    readonly Predicate<long> keepRenderDirty;
    readonly Predicate<long> renderDirtyBlocked;
    readonly Predicate<long> radialDemandReady;
    readonly Predicate<long> radialDemandPending;
    readonly Predicate<long> radialDemandTerminal;
    readonly System.Func<long, bool> startRadialDemand;
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

    /// <summary>
    /// The same shader source compiled with VH_INDIRECT, so its per-section values come
    /// from the record buffer instead of uniforms. Not a second shader: one body file,
    /// two programs, which is what keeps "the fast path draws identical pixels" a claim
    /// the two paths cannot quietly fall out of.
    /// </summary>
    IShaderProgram? indirectProg;
    IShaderProgram? packedProg;
    bool shaderOk;
    bool indirectShaderOk;
    bool packedShaderOk;
    bool reportedIndirectShaderOk;
    bool reportedPackedShaderOk;
    IShaderProgram? hzbProg;
    bool hzbShaderOk;

    /// <summary>
    /// Build the private depth pyramid each frame. Phase 4 shadow work: it copies, reduces
    /// and times itself, and decides nothing. Off by default because it costs GPU time for
    /// no picture change, and because its cost against the drawing it could remove is
    /// exactly what has to be measured before anything is allowed to act on it.
    /// `.vhhzb on` turns it on for a session.
    /// </summary>
    public bool DepthPyramidEnabled
    {
        get => depthPyramidEnabled;
        set
        {
            // Switching it on restarts the counts. Otherwise the first reading a person
            // takes is diluted by however long the interval had already been running with
            // the feature off, and reads as a much smaller hidden share than it is.
            if (value && !depthPyramidEnabled) ResetDepthPyramidCounters();

            // A pyramid that is on is a pyramid being measured. Its cost is GPU time and
            // nothing else, and until now the timers armed only for a session started under
            // the benchmark harness - so an ordinary run reported 0.0us, which reads as
            // "free" rather than "nobody measured". Here rather than in the chat command,
            // because .vhcull on turns the pyramid on by assignment and would otherwise
            // leave the clock stopped (G72).
            if (value) gpuTelemetry.RequestTiming();

            depthPyramidEnabled = value;
        }
    }

    bool depthPyramidEnabled;

    /// <summary>
    /// Split cached opaque terrain at the measured 512-block boundary. The near bucket draws
    /// first, a fresh depth picture is taken, and the far bucket is culled and drawn against
    /// that picture in the SAME frame. Phase 6.
    ///
    /// The config and command retain their old "late" names so an existing saved opt-in now
    /// selects the safe replacement automatically. No verdict crosses a frame boundary.
    /// </summary>
    public bool LateDepthPyramid
    {
        get => lateDepthPyramid;
        set
        {
            if (lateDepthPyramid == value) return;
            lateDepthPyramid = value;

            ResetDepthPyramidCounters();
        }
    }

    bool lateDepthPyramid =
        Environment.GetEnvironmentVariable("VINTAGEHORIZONS_GPU_LATE") != "0";
    readonly float[] explainViewProjection = new float[16];
    public long SplitDepthFrames { get; private set; }
    public long SplitDepthNearCommands { get; private set; }
    public long SplitDepthFarCommands { get; private set; }
    public long SplitDepthMidBuilds { get; private set; }

    /// <summary>
    /// Starts a fresh measurement interval for everything the depth work is judged on.
    ///
    /// The GPU timers are in here, and that is the point. The phase's gate is GPU time and
    /// nothing else, and without this a figure read after flipping a switch is an average over
    /// BOTH arrangements - which looks entirely plausible and answers nothing. Resetting the
    /// ring also advances its epoch, so results still in flight from the old arrangement are
    /// discarded rather than landing in the new interval's total.
    ///
    /// Opaque and water GPU time reset too, deliberately: opaque time is the number that should
    /// FALL when culling works, so it belongs to the arrangement being measured just as much as
    /// the pyramid's own cost does.
    /// </summary>
    public void ResetDepthPyramidInterval() => ResetDepthPyramidCounters();

    void ResetDepthPyramidCounters()
    {
        HzbCost.Reset();
        gpuTelemetry.ResetInterval();
        depthPyramid?.ResetInterval();
        hzbClassifier?.ResetInterval();
        cullPass?.ResetInterval();
        SplitDepthFrames = 0;
        SplitDepthNearCommands = 0;
        SplitDepthFarCommands = 0;
        SplitDepthMidBuilds = 0;
        HzbHiddenButQuerySawIt = 0;
        HzbMissedWhatQueryHid = 0;
        HzbVerdictsChecked = 0;
        hzbLastVerdict.Clear();
    }

    LodGpuDepthPyramid? depthPyramid;

    /// <summary>CPU time spent asking for the pyramid, separate from the GPU time it takes.</summary>
    public LodPhaseCost HzbCost;
    internal LodHzbBuildResult LastHzbBuild { get; private set; }

    /// <summary>Owns the vertex array and the per-frame command and record buffers.</summary>
    LodGpuIndirectDrawer? indirectDrawer;
    LodGpuIndirectDrawer? packedDrawer;
    LodGpuCullPass? cullPass;
    readonly float[] cullViewProjection = new float[16];

    /// <summary>
    /// Whether depth verdicts may stop a cached section being drawn.
    ///
    /// OFF by default and not saved, like every switch that can change what reaches the
    /// screen. This is the first one whose failure mode is missing terrain rather than a
    /// slower frame, so it turns on only when someone asks for it, and only for that session.
    /// It needs the indirect path: culling works by zeroing an indirect command, and the
    /// established path has no commands to zero.
    /// </summary>
    public bool GpuCullEnabled { get; set; } =
        Environment.GetEnvironmentVariable("VINTAGEHORIZONS_GPU_CULL") != "0";

    /// <summary>
    /// Sections the indirect pass could not batch: the arenas do not hold them, or a
    /// multi-draw failed. They are drawn exactly as the established path draws them, in a
    /// second sub-pass, so partial arena coverage costs submissions rather than terrain.
    /// </summary>
    readonly List<long> indirectLeftovers = new();

    /// <summary>Far-bucket leftovers draw only after the mid-frame picture is taken.</summary>
    readonly List<long> indirectFarLeftovers = new();

    /// <summary>Keys written into this frame's command list, kept for the same reason.</summary>
    readonly List<long> indirectRecorded = new();

    /// <summary>Keys recorded into the far command list, retained for fail-open redraw.</summary>
    readonly List<long> indirectFarRecorded = new();

    /// <summary>True only inside the walk of an indirect opaque pass.</summary>
    bool indirectPassActive;

    /// <summary>
    /// Decided once, before anything this frame reads it. A path chosen halfway through a
    /// frame would draw some sections twice and others not at all, and occlusion queries
    /// resolved under one path would be applied under the other.
    /// </summary>
    bool indirectDrawingThisFrame;

    /// <summary>
    /// Fixed once per frame. The split is meaningful only when both indirect drawing and
    /// depth culling are active; otherwise one ordinary command list is the complete path.
    /// </summary>
    bool depthSplitThisFrame;
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

    /// <summary>
    /// Frames that returned before the selection walk because no mesh existed yet. On an
    /// ordinary join this is a handful; a large number means the bootstrap never started.
    /// </summary>
    public long FramesWithoutMeshes { get; private set; }

    /// <summary>Whole quadtree subtrees rejected before descent this frame.</summary>
    public int LastTraversalCulledCount { get; private set; }

    readonly LodFrustum frustum = new();
    int worldHeight = 1024;

    /// <summary>Blocks from bedrock to build limit, as the block accessor reports it.</summary>
    public int WorldHeight => worldHeight;


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
    int uploadedIndirectTintVersion = -1;
    int uploadedPackedTintVersion = -1;

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
        gpuFailureInjection = LodGpuFailureInjection.Parse(
            Environment.GetEnvironmentVariable(LodGpuFailureInjection.EnvironmentVariable));
        if (gpuFailureInjection.Stage == LodGpuFailureStage.Invalid)
        {
            capi.Logger.Warning(
                "[VintageHorizons] Ignoring unknown {0} value '{1}'. Expected arena, shader, "
                + "depth-copy, draw, or off.",
                LodGpuFailureInjection.EnvironmentVariable,
                gpuFailureInjection.Requested);
        }
        else if (gpuFailureInjection.Armed)
        {
            capi.Logger.Warning(
                "[VintageHorizons] Phase 9 GPU failure injection armed for {0}; this is a "
                + "test-only session and rendering must fail back safely.",
                gpuFailureInjection.Stage);
        }
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
        radialDemandReady = HasAnyMesh;
        radialDemandPending = RadialDemandPending;
        radialDemandTerminal = RadialDemandTerminal;
        startRadialDemand = RequestMesh;
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

    /// <summary>
    /// How many times shaders have been asked for. The first ask is ours, from mod start,
    /// and it happens BEFORE the engine has filled its shader-include table from mod
    /// assets - so it always fails to splice, always fails to compile, and used to log two
    /// errors saying cached terrain would not draw. The engine then fires ReloadShader
    /// ("Reloaded shaders now with mod assets") and the second attempt succeeds.
    ///
    /// That noise is worse than useless: it is indistinguishable from the genuine failure
    /// it was written to report, and a session that read it concluded the indirect variant
    /// does not compile on hardware when in fact it does. The first attempt is therefore
    /// provisional - it says what it is waiting for, at notification level - and every
    /// later attempt reports at full volume.
    /// </summary>
    int shaderLoadAttempts;

    bool ShaderLoadIsProvisional => shaderLoadAttempts <= 1;

    public bool LoadShader()
    {
        shaderLoadAttempts++;
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
        uploadedIndirectTintVersion = -1;
        uploadedPackedTintVersion = -1;
        WarnIfIncludeMissing(prog, "lodterrain");
        shaderOk = prog.Compile();
        if (!shaderOk && !ShaderLoadIsProvisional)
            capi.Logger.Error("[VintageHorizons] lodterrain shader failed to compile; LOD rendering disabled");

        // The indirect variant. Its .vsh and .fsh are three lines each: a version, a
        // define, and an include of the same body this program compiled. A driver that
        // refuses it costs the fast path and nothing else, so this is a warning rather
        // than an error and the established renderer never notices.
        indirectProg = capi.Shader.NewShaderProgram();
        indirectProg.AssetDomain = "vintagehorizons";
        indirectProg.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
        indirectProg.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
        capi.Shader.RegisterFileShaderProgram("lodterrainindirect", indirectProg);
        WarnIfIncludeMissing(indirectProg, "lodterrainindirect");

        // Same body again, with the packed input block enabled. Per-section attributes,
        // every fragment calculation and every uniform remain shared with the already
        // played indirect path; only vertex acquisition changes.
        packedProg = capi.Shader.NewShaderProgram();
        packedProg.AssetDomain = "vintagehorizons";
        packedProg.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
        packedProg.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
        capi.Shader.RegisterFileShaderProgram("lodterrainpacked", packedProg);
        WarnIfIncludeMissing(packedProg, "lodterrainpacked");
        // The depth-pyramid reduction. It shares nothing with the terrain shaders - no
        // include, no tint constant, no vertex format - because it draws one attributeless
        // triangle and writes depth. A driver that refuses it costs the pyramid and
        // nothing else, and the established renderer never learns it existed.
        hzbProg = capi.Shader.NewShaderProgram();
        hzbProg.AssetDomain = "vintagehorizons";
        hzbProg.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
        hzbProg.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
        capi.Shader.RegisterFileShaderProgram("hzbreduce", hzbProg);
        hzbShaderOk = hzbProg.Compile();
        if (!hzbShaderOk && !ShaderLoadIsProvisional)
            capi.Logger.Warning(
                "[VintageHorizons] the hzbreduce shader failed to compile; depth-pyramid "
                + "occlusion is unavailable and nothing else changes");

        indirectShaderOk = indirectProg.Compile();
        if (!indirectShaderOk && !ShaderLoadIsProvisional)
            capi.Logger.Warning(
                "[VintageHorizons] the indirect lodterrain variant failed to compile; "
                + "cached terrain keeps drawing through the established path");

        packedShaderOk = packedProg.Compile();
        if (!ShaderLoadIsProvisional
            && gpuFailureInjection.Take(LodGpuFailureStage.FastShaders))
        {
            indirectShaderOk = false;
            packedShaderOk = false;
            capi.Logger.Warning(
                "[VintageHorizons] Phase 9 injected the fast terrain shader failure; cached "
                + "terrain stays on the established renderer until a later successful reload.");
        }
        if (!packedShaderOk && !ShaderLoadIsProvisional)
            capi.Logger.Warning(
                "[VintageHorizons] the packed lodterrain variant failed to compile; "
                + "the expanded regional path remains available");

        // Said once, when it actually worked, so the log carries positive evidence rather
        // than only the absence of a complaint. This line is the answer to "does the
        // indirect variant compile on this driver at all".
        if (indirectShaderOk && !reportedIndirectShaderOk)
        {
            reportedIndirectShaderOk = true;
            capi.Logger.Notification(
                "[VintageHorizons] the indirect lodterrain variant compiled after {0} "
                + "attempt(s); batched drawing is available behind .vhgpu on and .vhindirect on",
                shaderLoadAttempts);
        }

        return shaderOk;
    }

    /// <summary>
    /// Both programs are three-line files whose real content arrives through one #include,
    /// and the engine resolves those against a table it fills once, at startup, from every
    /// loaded asset. If our body were ever missing from that table the engine would log a
    /// bare "Include file not found. Ignoring." and hand the compiler an empty shader -
    /// which then fails for a reason that names nothing.
    ///
    /// Naming it here costs one substring search per shader load and turns that into a
    /// sentence someone can act on.
    /// </summary>
    void WarnIfIncludeMissing(IShaderProgram program, string name)
    {
        string? vertex = program.VertexShader?.Code;
        string? fragment = program.FragmentShader?.Code;
        bool spliced = vertex != null && vertex.Contains("TINT_SLOTS", StringComparison.Ordinal)
            && fragment != null && fragment.Contains("TINT_SLOTS", StringComparison.Ordinal);
        if (spliced) return;

        // The first ask runs before the engine has read mod assets, so a missing body
        // there is the expected order of events rather than a fault. Saying so is still
        // worth one line: if the reload never arrives, this is the only trace of why.
        if (ShaderLoadIsProvisional)
        {
            capi.Logger.Notification(
                "[VintageHorizons] {0}: the engine has not registered mod shader includes "
                + "yet. The reload that follows mod asset loading is the one that matters.",
                name);
            return;
        }

        capi.Logger.Error(
            "[VintageHorizons] the {0} shader body was not spliced in: the engine did not "
            + "find lodterrainbody.vsh/.fsh among its shader includes. Cached terrain will "
            + "not draw. This is ours, not your setup.", name);
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
                // Full height here, deliberately, while the walk below uses the aggregate.
                // This gate does not decide what is drawn; it decides whether the parent
                // may STOP drawing its own coarse mesh over this quadrant. Those are not
                // the same question, and the aggregate cannot answer the second one: it
                // covers the meshes resident under the child, not the parent's coarser
                // rendition of the same ground, which has its own vertical extent. A child
                // holding nothing but a deep cave mesh would be rejected vertically, the
                // parent would descend believing the quadrant covered, and the surface the
                // parent was drawing there would become a hole.
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
                    // The radial planner owns demand eligibility. The parent keeps
                    // covering until that orientation-independent wave reaches this gate.
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
        LodHeightSpan subtree = SubtreeSpan(key);
        if (!LodTraversalPolicy.NodeInView(frustum, key,
            camPos.X, camPos.Y, camPos.Z, worldHeight, subtree))
        {
            traversalCulledThisFrame++;

            // Only on the reject path, and only for nodes the aggregate is what rejected:
            // how many of them the old full-height box would have traversed, and how many
            // resident meshes went with them. The node count alone cannot separate
            // rejecting one leaf from rejecting a whole L6 quadrant, and that difference
            // is the entire question this work is funded on.
            if (subtree.HasGeometry
                && LodTraversalPolicy.NodeInView(frustum, key,
                    camPos.X, camPos.Y, camPos.Z, worldHeight))
            {
                subtreeCulledThisFrame++;
                subtreeCulledMeshesThisFrame += subtreeHeights.Of(key).Meshes;
            }
            return false;
        }

        bool hasMesh = HasAnyMesh(key);
        int level = LodWorld.KeyLevel(key);
        int wanted = LodWorld.WantedLevelForSq(NearestDistanceSqTo(key));

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
            if (!LodTintRegistry.RefreshDue(
                seasonalStateInitialized,
                tints.HasUnreadySlots,
                now - lastSeasonRefreshMs,
                SeasonalRefreshIntervalMs)) return;

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


    /// <summary>Transfer one planner or content-change obligation into exact dirty ownership.</summary>
    bool RequestMesh(long key)
    {
        if (meshJobInFlight.Contains(key)) return false;

        // RAM-evicted sections still count: HasDataSet says whether the subtree has
        // data at all; the scheduler reloads the row from disk when it picks the job.
        if (world.Sections.TryGetValue(key, out LodSection? section))
        {
            if (section.CapturedColumns == 0) return false;
        }
        else if (!world.HasDataSet.Contains(key))
        {
            return false;
        }

        return world.RenderDirty.Add(key);
    }

    bool RadialDemandPending(long key) => world.RenderDirty.Contains(key)
        || world.LoadsInFlight.Contains(key)
        || meshJobInFlight.Contains(key);

    bool RadialDemandTerminal(long key) => world.LoadFailed.Contains(key)
        || (world.Sections.TryGetValue(key, out LodSection? section)
            && section.CapturedColumns == 0);

    void PlanRadialDemand()
    {
        // The first configured transition is exactly the area whose final target is L0.
        // Prepare it under the player even while vanilla suppresses the cache: movement
        // may expose those meshes later, and discovering a coarse fallback at that point
        // looks like terrain regressed. Its independent planner lets this foundation keep
        // refining while outward coverage also advances.
        int foundationRadius = (int)Math.Ceiling(LodWorld.ThresholdForLevel(1));
        int outer = FarViewDistanceCap > 0
            ? FarViewDistanceCap
            : LodRadialDemandPlanner.UnlimitedPlanningRadius;
        foundationDemand.Pump(
            world.AvailableDataSet,
            world.AvailableDataRevision,
            LodWorld.DetailPolicyRevision,
            camPos.X,
            camPos.Z,
            0,
            foundationRadius,
            radialDemandReady,
            radialDemandPending,
            radialDemandTerminal,
            startRadialDemand);
        radialDemand.Pump(
            world.AvailableDataSet,
            world.AvailableDataRevision,
            LodWorld.DetailPolicyRevision,
            camPos.X,
            camPos.Z,
            foundationRadius,
            outer,
            radialDemandReady,
            radialDemandPending,
            radialDemandTerminal,
            startRadialDemand);
    }

    public string DescribeRadialDemand() => $"inner {foundationDemand.Describe()}; "
        + $"outward {radialDemand.Describe()}";

    public string DescribeTintReadiness() => seasonalRefreshActive
        ? $"{tints.ReadySlotCount}/{tints.SlotCount} slots ready (sampling)"
        : $"{tints.ReadySlotCount}/{tints.SlotCount} slots ready";

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

    bool RenderDirtyBlocked(long key)
    {
        if (meshJobInFlight.Contains(key) || world.LoadsInFlight.Contains(key)) return true;

        // Palette resolution can discover a climate/season tint after the last completed
        // refresh. Keep this exact mesh obligation queued until every slot it uses has a
        // published value. A coarser parent remains live during replacement; on a cold
        // start, a few incremental tint frames are preferable to revealing white-tinted
        // ground and recolouring the whole horizon thirty seconds later.
        return world.Sections.TryGetValue(key, out LodSection? section)
            && !tints.SectionTintsReady(section);
    }

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
            // re-requested by the radial planner once it lands, rather than stalling
            // this frame on a decompress.
            if (!world.TryGetForRender(best, out LodSection section))
            {
                if (world.LoadsInFlight.Contains(best))
                {
                    loadBudget--; // a reload is under way; radial demand retains responsibility
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

            // Eight, not four. The mesher's side-face culling only ever asks about the four
            // edges, but cave culling floods light through a window around the section, and
            // an absent DIAGONAL leaves a section-sized block of assumed-open air touching
            // the corner. Light pours in from it and rescues caves that are genuinely dark -
            // measured at roughly half the saving. The corners are read for light only and
            // are never asked whether a side is covered.
            var neighborSections = new LodSection?[8];
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

            // The four corners, for light only.
            for (int d = 4; d < 8; d++)
            {
                int cornerX = (d == 4 || d == 6) ? -1 : 1;
                int cornerZ = d < 6 ? -1 : 1;
                long ck = LodWorld.NeighborKey(best, cornerX, cornerZ);
                if (!world.Sections.TryGetValue(ck, out LodSection? corner)) continue;
                neighborSections[d] = corner;
                estimatedBytes = SectionSnapshot.SaturatingAdd(
                    estimatedBytes, SectionSnapshot.EstimateRetainedBytes(corner));
            }

            // TryTake transferred the exact dirty obligation to us. If this frame has
            // spent its snapshot time/byte allowance, restore that obligation before
            // stopping. The first eligible snapshot always progresses even if oversized.
            if (!snapshotBudget.TryStart(estimatedBytes))
            {
                world.RenderDirty.Add(best);
                break;
            }

            var neighbors = new SectionSnapshot?[neighborSections.Length];
            for (int d = 0; d < neighborSections.Length; d++)
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
                CaveCullReach = CaveCulling ? caveCullReach : 0,
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
                new LodRenderGeometry(
                    result.Xyz, result.Rgba, result.Indices,
                    result.PackedOpaqueQuads, result.PackedOpaqueQuadCount,
                    result.ClusteredPackedOpaqueQuads, result.PackedOpaqueClusters),
                result.Heights);
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
            Math.Max(0, publication.WaterIndices),
            publication.Heights);
        if (stats.OpaqueIndices == 0 && stats.WaterIndices == 0) return;

        liveMeshStats[key] = stats;
        // Both passes in one span: the traversal box precedes the split into opaque and
        // water draws, so it has to hold whichever of them the walk goes on to select.
        subtreeHeights.SetMesh(key, stats.Heights.Either);
        NoteSectionHeights(stats.Heights);
        LiveOpaqueVertices += stats.OpaqueVertices;
        LiveOpaqueIndices += stats.OpaqueIndices;
        LiveWaterVertices += stats.WaterVertices;
        LiveWaterIndices += stats.WaterIndices;
        LiveGpuMeshBytes += stats.Bytes;
    }

    void RemoveLiveMeshStats(long key)
    {
        subtreeHeights.RemoveMesh(key);
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

    /// <summary>
    /// Times the whole frame around the real work. The body below has several early
    /// returns, and every one of them still has to close the frame, or the mod's share
    /// would be recorded only for the frames where it did the most.
    /// </summary>
    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        long frameStart = frameTimeline.Begin();
        try
        {
            RenderFrame(deltaTime, stage);
        }
        finally
        {
            frameTimeline.End(frameStart);
        }
    }

    /// <summary>Per-frame measurement of the client's own frame interval against ours.</summary>
    public LodFrameTimeline FrameTimeline => frameTimeline;

    readonly LodFrameTimeline frameTimeline = new();

    void RenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (AutoUnpause && capi.IsGamePaused) capi.PauseGame(false);

        var rapi = capi.Render;
        if (rapi.FrameWidth == 0) return;

        camPos = capi.World.Player.Entity.CameraPos;
        frameCounter++;
        gpuTelemetry.BeginFrame();
        ConfigureRenderPaths();
        ApplyGpuShadowRequest();
        ApplyGpuDrawFailureInjection();
        float viewDistance = ApprovedViewDistance();

        // Fixed before anything reads it, and before the occlusion queries are resolved:
        // the two paths disagree about whether per-section queries are issued at all, so
        // the choice must not change between resolving a query and acting on it.
        bool packedWanted = PackedDrawAvailable;
        bool clustersWanted = packedWanted && ClusterDrawAvailable;
        bool indirectWanted = packedWanted || IndirectDrawAvailable;
        if (indirectWanted != indirectDrawingThisFrame)
        {
            // Entering the batched path also gives up the query objects themselves. They are
            // allocated lazily, one GL query per section that was ever tested, and while
            // batching is on not one of them is issued or read - so they are pure retention:
            // driver objects, plus a dictionary the per-tick eviction sweep still walks. They
            // come back lazily if suppression ever returns to the CPU.
            //
            // Released BEFORE the invalidation below, because the release clears the pending
            // list too and there is nothing left to mark stale afterwards.
            if (indirectWanted) DisposeTemporalOcclusionQueries();

            // Every previous-frame occlusion answer was taken under the other path, and
            // under batching no new ones are taken at all. Keeping them across the switch
            // would let a section that was hidden several seconds and one camera move ago
            // stay hidden after switching back - which would look like the fast path
            // losing terrain, in the exact A/B this switch exists for.
            InvalidateTemporalOcclusionScene();
        }

        if (packedShaderOk && !reportedPackedShaderOk)
        {
            reportedPackedShaderOk = true;
            capi.Logger.Notification(
                "[VintageHorizons] the packed lodterrain variant compiled after {0} "
                + "attempt(s); 12-byte quad drawing is available behind .vhpacked on",
                shaderLoadAttempts);
        }
        indirectDrawingThisFrame = indirectWanted;
        packedDrawingThisFrame = packedWanted;
        clusterDrawingThisFrame = clustersWanted;
        depthSplitThisFrame = LateDepthPyramid
            && GpuCullEnabled
            && DepthPyramidEnabled
            && indirectDrawingThisFrame
            && ActiveFarBuilder != null;

        LodPhaseStart phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        UpdateReadinessShadow(viewDistance);
        ReadinessCost.Add(phaseStart);

        if (prog == null || !shaderOk || prog.LoadError) return;

        // Demand exists independently of draw visibility. This runs before the empty-mesh
        // return below, breaking the old no-mesh -> no-walk -> no-request join cycle.
        PlanRadialDemand();

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
        if (sectionMeshes.Count == 0 && waterMeshes.Count == 0)
        {
            // Nothing to draw, so traversal is skipped. Radial demand and scheduling have
            // already run above; this count now distinguishes a bounded warm-up from an
            // unexpected failure to produce the first mesh.
            FramesWithoutMeshes++;
            return;
        }

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

        // Here, and not later, because "after vanilla terrain" is what makes the depth
        // buffer worth copying: this renderer runs in the opaque stage after the engine has
        // drawn its own world, so what sits in the depth attachment right now is exactly
        // the set of near occluders a distant section would have to be behind. Copied
        // before we draw a single cached section, so cached terrain cannot occlude itself
        // by accident - that is Phase 6 and it is a separate decision.
        // The split still needs this first picture for its near bucket. A second build after
        // the near draw adds cached occluders for the far bucket; both are same-frame.
        BuildDepthPyramid();
        hzbBoxes.Clear();
        hzbCollecting = DepthPyramidEnabled;

        traversalCulledThisFrame = 0;
        subtreeCulledThisFrame = 0;
        subtreeCulledMeshesThisFrame = 0;
        culledThisFrame = 0;
        verticalCulledThisFrame = 0;
        LastTemporalOcclusionDrawsSkipped = 0;
        LastTemporalOcclusionSeamDraws = 0;
        LastTemporalOcclusionEdgeDraws = 0;

        phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        drawList.Clear();
        foreach (long top in world.TopLevelKeys) CollectDrawNodes(top);
        WalkCost.Add(phaseStart);
        LastDrawCount = drawList.Count;
        LastTraversalCulledCount = traversalCulledThisFrame;
        LastSubtreeCulledCount = subtreeCulledThisFrame;
        SubtreeCulledNodes += subtreeCulledThisFrame;
        SubtreeCulledMeshes += subtreeCulledMeshesThisFrame;
        if (subtreeCulledThisFrame > SubtreeCulledMax)
            SubtreeCulledMax = subtreeCulledThisFrame;
        if (drawList.Count == 0)
        {
            LastCulledCount = 0;
            LastVerticalCulledCount = 0;
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

        ApplyFrameUniforms(prog, viewDistance, ref uploadedTintVersion);

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

        // The split's mid-frame picture deliberately excludes water: water is blended but
        // writes depth, so treating it as an occluder could remove visible ground beneath it.
        // Classification reads the unchanged private pyramid, not the live depth attachment.
        hzbCollecting = false;
        ClassifyAgainstDepthPyramid();

        // Both passes cull, so this is summed after the second one rather than beside
        // LastCulledCount, which is deliberately opaque-only.
        LastVerticalCulledCount = verticalCulledThisFrame;
        VerticalCulledSections += verticalCulledThisFrame;
        if (verticalCulledThisFrame > VerticalCulledMax)
            VerticalCulledMax = verticalCulledThisFrame;

        // Submission only. RenderMesh queues work for the GPU and returns, so this
        // measures the CPU cost of the draw loop -- the uniform uploads, the culling and
        // the dictionary probes -- and not what the GPU then does with it.
        DrawCost.Add(phaseStart);

        rapi.GlEnableCullFace();
        prog.Stop();
    }

    /// <summary>
    /// Everything the shader needs that is the same for every section this frame.
    ///
    /// Taken as a parameter rather than read from the field because the indirect path
    /// draws through a second program built from the same source, and both must be handed
    /// identical values: any divergence here would show up as a pixel difference and be
    /// read as a bug in the fast path rather than as a missing upload.
    /// </summary>
    void ApplyFrameUniforms(
        IShaderProgram prog, float viewDistance, ref int uploadedTintVersion)
    {
        var rapi = capi.Render;
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
        prog.Uniform("lightMoonDir", LightMoonDirection ? 1 : 0);
        prog.Uniform("lightVanillaRamp", LightVanillaRamp ? 1 : 0);
        prog.Uniform("lightAmbientColor", LightAmbientColor ? 1 : 0);
        prog.Uniform("lightDayBoost", LightDayBoost ? 1 : 0);
        // `lightPosition` and `shadowIntensity` need no upload here: the shader includes
        // fogandlight.fsh, and the engine's own Use() uploads both to any program that
        // does. Only the ambient colour has no such carrier.
        prog.Uniform("rgbaAmbientIn", capi.Ambient.BlendedAmbientColor);
        prog.Uniform("lightSkyDayLight", LightSkyDayLight ? 1 : 0);
        prog.Uniform("skyDayLight", SkyDayLight());
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
    /// Regional arenas for the validated GPU path. They retain only its selected geometry
    /// representation and stay bounded by a draw-distance-derived ceiling.
    /// </summary>
    void AttachShadowArenas()
    {
        LodGpuArenaMode mode = LodGpuArenaPolicy.Parse(
            Environment.GetEnvironmentVariable("VINTAGEHORIZONS_GPU_ARENA"));
        if (mode is LodGpuArenaMode.Off or LodGpuArenaMode.Invalid)
        {
            capi.Logger.Notification(
                "[VintageHorizons] GPU regional arenas off; cached terrain stays on the established renderer.");
            return;
        }

        AttachShadowArenas(mode);
    }

    void AttachShadowArenas(LodGpuArenaMode mode)
    {
        LodGpuArenaPolicy.ConfigurePageBytes(
            Environment.GetEnvironmentVariable("VINTAGEHORIZONS_GPU_ARENA_PAGE_MB"));
        // Sized from the player's own cached-terrain distance rather than a fixed number, so
        // the pool can hold whatever their settings ask the mod to draw. A cap of zero means
        // unlimited and gets a generous horizon instead. Read here rather than per frame
        // because the arenas are real GL buffers; changing the distance mid-session does not
        // resize them, and the log line below says which distance this pool was built for.
        double sizedFor = FarViewDistanceCap > 0
            ? FarViewDistanceCap
            : LodGpuArenaPolicy.UnlimitedDrawDistanceBlocks;
        long ceiling = LodGpuArenaPolicy.CeilingBytesFor(
            sizedFor, Environment.GetEnvironmentVariable("VINTAGEHORIZONS_GPU_ARENA_MB"));
        try
        {
            if (gpuFailureInjection.Take(LodGpuFailureStage.ArenaSetup))
                throw new InvalidOperationException(
                    "Phase 9 injected a regional arena setup failure");

            var backend = new LodGpuOpenGlArenaBackend(
                message => capi.Logger.Warning("{0}", message));
            LodGpuGeometryRetention retention = LodGpuGeometryMirror.SelectRetention(
                PackedDrawEnabled, ClusterDrawEnabled);
            gpuShadow.AttachMirror(new LodGpuGeometryMirror(
                backend, ceiling, verify: mode == LodGpuArenaMode.Verify,
                retention: retention));
            shadowBuilder = new LodGpuIndirectBuilder();
            splitFarBuilder = new LodGpuIndirectBuilder();
            packedBuilder = new LodGpuIndirectBuilder(packed: true);
            packedFarBuilder = new LodGpuIndirectBuilder(packed: true);
            clusterBuilder = new LodGpuIndirectBuilder(packed: true, clustered: true);
            clusterFarBuilder = new LodGpuIndirectBuilder(packed: true, clustered: true);
            // .vhgpu can be run again while a drawer already exists - on to verify, for
            // instance. The mirror disposes its own predecessor; this one has to be told.
            indirectDrawer?.Dispose();
            packedDrawer?.Dispose();
            cullPass?.Dispose();
            cullPass = new LodGpuCullPass(
                message => capi.Logger.Warning(message));
            indirectDrawer = new LodGpuIndirectDrawer(
                new LodGpuOpenGlDrawBackend(message => capi.Logger.Warning("{0}", message)),
                message => capi.Logger.Warning("{0}", message));
            packedDrawer = new LodGpuIndirectDrawer(
                new LodGpuOpenGlPackedDrawBackend(message => capi.Logger.Warning("{0}", message)),
                message => capi.Logger.Warning("{0}", message));
            long packedCeiling = LodGpuArenaPolicy.PackedLimits(ceiling).CeilingBytes;
            long clusterCeiling = LodGpuArenaPolicy.PackedClusterLimits(ceiling).CeilingBytes;
            capi.Logger.Notification(
                "[VintageHorizons] GPU arenas on: retaining {0} geometry only; ceilings are "
                + "{1} MiB expanded, {2} MiB packed and {3} MiB clustered, with {4} MiB "
                + "vertex pages and {5} page sets; content verification {6}. Sized for a "
                + "{7} draw distance ({8} sections); each ceiling "
                + "is a cap, not a reservation, so pages are only "
                + "committed as sections arrive.",
                retention,
                ceiling / (1024 * 1024),
                packedCeiling / (1024 * 1024),
                clusterCeiling / (1024 * 1024),
                LodGpuArenaPolicy.VertexPageBytes / (1024 * 1024),
                LodGpuArenaPolicy.PageSets(ceiling),
                mode == LodGpuArenaMode.Verify ? "on" : "off",
                FarViewDistanceCap > 0
                    ? FarViewDistanceCap + "-block"
                    : "an unlimited (modelled as "
                        + LodGpuArenaPolicy.UnlimitedDrawDistanceBlocks.ToString("0") + "-block)",
                LodGpuArenaPolicy.SectionsWithin(sizedFor).ToString("0"));
        }
        catch (Exception e)
        {
            capi.Logger.Warning(
                "[VintageHorizons] GPU arena shadow could not start; visible legacy rendering "
                + "is unchanged: {0}", e.Message);
        }
    }

    void ApplyGpuDrawFailureInjection()
    {
        if (indirectDrawer == null && packedDrawer == null) return;
        if (!gpuFailureInjection.Take(LodGpuFailureStage.IndirectDraw)) return;

        const string reason = "Phase 9 injected an indirect draw failure";
        indirectDrawer?.InjectFailure(reason);
        packedDrawer?.InjectFailure(reason);
    }

    /// <summary>Arena occupancy for the periodic report, or null when no arena is attached.</summary>
    public string? DescribeGpuArena() => gpuShadow.Mirror?.Describe();

    // ---- In-game GPU shadow switch ----

    LodGpuArenaMode? requestedShadowMode;

    /// <summary>
    /// The arena mode someone last ASKED for, which is what gets saved.
    ///
    /// The request rather than the effective state, matching the chunk mask: a path the
    /// capability probe refused this session reports itself off, and writing that back would
    /// turn it off permanently on a machine where a driver update might enable it.
    /// </summary>
    public bool GpuShadowRequested { get; private set; }

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
        GpuShadowRequested = parsed != LodGpuArenaMode.Off;
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
            splitFarBuilder = null;
            packedBuilder = null;
            packedFarBuilder = null;
            clusterBuilder = null;
            clusterFarBuilder = null;
            // The drawer's buffers reference nothing the arenas own, but its whole reason
            // to exist goes away with them, and a stale vertex array would outlive the
            // pages its batches name.
            IndirectDrawEnabled = false;
            indirectDrawingThisFrame = false;
            indirectDrawer?.Dispose();
            indirectDrawer = null;
            packedDrawer?.Dispose();
            packedDrawer = null;
            cullPass?.Dispose();
            cullPass = null;
            GpuCullEnabled = false;
            ShadowIndirectCommands = 0;
            ShadowIndirectSections = 0;
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
    /// The arenas only ever see geometry as it is published, so the GPU path switched on
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
            "[VintageHorizons] GPU regional path on; {0} live sections queued for "
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
            + $"Last frame {ShadowIndirectCommands} draw commands for {ShadowIndirectSections} "
            + $"sections would have been {ShadowIndirectBatches} multi-draw batches "
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
        if (!depthSplitThisFrame) gpuTelemetry.BeginOpaque();
        ActiveNearBuilder?.Begin();
        if (depthSplitThisFrame) ActiveFarBuilder?.Begin();
        indirectPassActive = indirectDrawingThisFrame;
        indirectLeftovers.Clear();
        indirectFarLeftovers.Clear();
        indirectRecorded.Clear();
        indirectFarRecorded.Clear();
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

            // The walk is over, so the command list is complete and the batches can be
            // formed. Closing it here rather than in the finally is what lets the pass
            // draw from it; a walk that threw simply leaves the builder to be reset by
            // the next frame's Begin.
            indirectPassActive = false;
            EndShadowCommands();
            // The real walk already recorded every box the classifier should see. Legacy
            // fallback redraws must not add the same section a second time.
            hzbCollecting = false;
            if (indirectDrawingThisFrame) DrawIndirectOpaque();
        }
        finally
        {
            indirectPassActive = false;
            if (!depthSplitThisFrame) gpuTelemetry.EndOpaque();
        }
    }

    /// <summary>
    /// Issues this frame's multi-draws, then draws whatever they could not cover.
    ///
    /// The established program keeps its own uniform state while it is not in use, so the
    /// switch costs one Use/Stop pair plus this frame's frame-uniforms for the second
    /// program - about thirty uploads against the eighty to one hundred and eighty
    /// per-section draws the batches replace.
    /// </summary>
    void DrawIndirectOpaque()
    {
        LastIndirectBatches = 0;
        LastIndirectCommands = 0;
        LastIndirectLeftovers = indirectLeftovers.Count + indirectFarLeftovers.Count;
        LodGpuIndirectBuilder? nearBuilder = ActiveNearBuilder;
        LodGpuIndirectDrawer? activeDrawer = ActiveDrawer;
        IShaderProgram? activeProgram = ActiveIndirectProgram;
        if (nearBuilder == null || activeDrawer == null || activeProgram == null
            || prog == null)
        {
            indirectLeftovers.AddRange(indirectRecorded);
            indirectFarLeftovers.AddRange(indirectFarRecorded);
            DrawIndirectLeftovers(indirectLeftovers);
            DrawIndirectLeftovers(indirectFarLeftovers);
            LastIndirectLeftovers = indirectLeftovers.Count + indirectFarLeftovers.Count;
            return;
        }

        if (depthSplitThisFrame && ActiveFarBuilder != null)
        {
            DrawSplitIndirectOpaque();
            return;
        }

        DrawIndirectBucket(nearBuilder, indirectRecorded, indirectLeftovers,
            applyFrameUniforms: true);
        DrawIndirectLeftovers(indirectLeftovers);
        LastIndirectLeftovers = indirectLeftovers.Count;
    }

    /// <summary>
    /// Draws near terrain, refreshes the pyramid while the terrain shader is released,
    /// then draws the far bucket against that same-frame picture. Each bucket has its own
    /// fail-open list, so a refused or partially issued multi-draw is repaired before the
    /// frame leaves this method.
    /// </summary>
    void DrawSplitIndirectOpaque()
    {
        SplitDepthFrames++;
        LodGpuIndirectBuilder nearBuilder = ActiveNearBuilder!;
        LodGpuIndirectBuilder farBuilder = ActiveFarBuilder!;
        SplitDepthNearCommands += nearBuilder.CommandCount;
        SplitDepthFarCommands += farBuilder.CommandCount;

        bool nearDrewAnything = nearBuilder.CommandCount > 0 || indirectLeftovers.Count > 0;
        bool farHasAnything = farBuilder.CommandCount > 0 || indirectFarLeftovers.Count > 0;

        if (nearDrewAnything)
        {
            gpuTelemetry.BeginSplitNear();
            try
            {
                DrawIndirectBucket(nearBuilder, indirectRecorded, indirectLeftovers,
                    applyFrameUniforms: true);
                DrawIndirectLeftovers(indirectLeftovers);
            }
            finally
            {
                gpuTelemetry.EndSplitNear();
            }
        }

        if (nearDrewAnything && farHasAnything)
        {
            // Between opaque buckets, never after water. Water writes depth even though it is
            // blended, so including it would allow transparent surfaces to hide solid ground.
            // G78: the engine refuses the reduction shader while lodterrain is still active.
            prog!.Stop();
            try
            {
                BuildDepthPyramid(splitMid: true);
                if (LastHzbBuild.Built) SplitDepthMidBuilds++;
            }
            finally
            {
                prog.Use();
            }
        }

        if (farHasAnything)
        {
            gpuTelemetry.BeginSplitFar();
            try
            {
                DrawIndirectBucket(farBuilder, indirectFarRecorded, indirectFarLeftovers,
                    applyFrameUniforms: true);
                DrawIndirectLeftovers(indirectFarLeftovers);
            }
            finally
            {
                gpuTelemetry.EndSplitFar();
            }
        }

        LastIndirectLeftovers = indirectLeftovers.Count + indirectFarLeftovers.Count;
    }

    /// <summary>Issues one complete command bucket or repairs it through legacy draws.</summary>
    void DrawIndirectBucket(
        LodGpuIndirectBuilder builder,
        List<long> recorded,
        List<long> leftovers,
        bool applyFrameUniforms)
    {

        bool drew = false;
        LodGpuIndirectDrawer drawer = builder.Packed ? packedDrawer! : indirectDrawer!;
        IShaderProgram drawProgram = builder.Packed ? packedProg! : indirectProg!;
        prog!.Stop();
        try
        {
            drawProgram.Use();
            if (applyFrameUniforms)
            {
                if (builder.Packed)
                    ApplyFrameUniforms(
                        drawProgram, ApprovedViewDistance(), ref uploadedPackedTintVersion);
                else
                    ApplyFrameUniforms(
                        drawProgram, ApprovedViewDistance(), ref uploadedIndirectTintVersion);
            }
            LodGpuCullBucket cullBucket = !depthSplitThisFrame
                ? LodGpuCullBucket.General
                : ReferenceEquals(builder, ActiveFarBuilder)
                    ? LodGpuCullBucket.SplitFar
                    : LodGpuCullBucket.SplitNear;
            drew = drawer.Draw(builder, BuildCullRequest(cullBucket));
        }
        catch (Exception e)
        {
            capi.Logger.Warning(
                "[VintageHorizons] the indirect opaque pass threw; this frame's cached "
                + "terrain is drawn the established way: {0}", e.Message);
        }
        finally
        {
            drawProgram.Stop();
            prog.Use();
        }

        if (drew)
        {
            LastIndirectBatches += drawer.LastBatches;
            LastIndirectCommands += drawer.LastCommands;
            // A multi-draw is a draw call; counting it keeps the periodic report's
            // submission figure meaning the same thing on both paths.
            OpaqueDrawCalls += drawer.LastBatches;
        }
        else
        {
            // Nothing was drawn from the arenas, so every section that was batched still
            // has to reach the screen. Falling back within the same frame is what keeps a
            // driver failure invisible rather than a frame of missing horizon.
            leftovers.AddRange(recorded);
        }
    }

    /// <summary>
    /// What this frame can offer the cull, or nothing at all.
    ///
    /// Every condition here is a reason to draw everything rather than a reason to guess. The
    /// switch is off, the pyramid was not built this frame, the shader never compiled - each
    /// returns a request the drawer will decline, and declining means every command the CPU
    /// approved is drawn. There is deliberately no branch that culls on partial information.
    ///
    /// The matrix is copied from the frustum rather than kept from earlier in the frame,
    /// because it must be the one the boxes were built against. The frustum was updated at
    /// the top of this frame from the engine's own matrices and the boxes are camera-relative
    /// against that same camera, so the two agree by construction.
    /// </summary>
    LodGpuCullRequest BuildCullRequest(LodGpuCullBucket bucket)
    {
        if (!GpuCullEnabled || !DepthPyramidEnabled || cullPass == null) return default;

        // Ahead of the picture checks, not behind them. Creation used to sit last, so a
        // session where no picture had been built yet never attempted it - and the status line
        // then said "the cull shader has not been created", which reads like a driver refusing
        // it. In the 2026-08-23 log that line was printed one second before the first picture
        // existed and was never asked again, leaving no evidence either way for the whole run.
        // Attempting it here means one rendered frame settles the question for good.
        if (!cullPass.Available && !cullPass.TryCreate()) return default;

        if (depthPyramid == null || !depthPyramid.Allocated) return default;
        if (!LastHzbBuild.Built) return default;

        frustum.CopyViewProjection(cullViewProjection);

        return new LodGpuCullRequest(
            cullPass,
            cullViewProjection,
            depthPyramid.TextureName,
            depthPyramid.Width,
            depthPyramid.Height,
            depthPyramid.Levels,
            LodHzbProjection.OcclusionDepthBias,
            bucket);
    }

    /// <summary>
    /// Draws the sections the batches did not cover, exactly as the established path
    /// draws them. Their per-section uniforms were deliberately not uploaded during the
    /// walk, so the transform is set up again here - for these sections only.
    /// </summary>
    void DrawIndirectLeftovers(List<long> leftovers)
    {
        if (leftovers.Count == 0) return;
        foreach (long key in leftovers)
        {
            if (!sectionMeshes.TryGetValue(key, out MeshRef? mesh)) continue;
            if (!SetupSectionTransform(key, renderCullDistanceSquared)) continue;
            OpaqueDrawCalls++;
            capi.Render.RenderMesh(mesh);
        }
    }

    /// <summary>
    /// Closes the shadow command list for this frame. It is only ever read as telemetry:
    /// no buffer is uploaded and no draw is issued from it in this phase.
    /// </summary>
    void EndShadowCommands()
    {
        LodGpuGeometryMirror? mirror = gpuShadow.Mirror;
        LodGpuIndirectBuilder? nearBuilder = ActiveNearBuilder;
        LodGpuIndirectBuilder? farBuilder = ActiveFarBuilder;
        if (nearBuilder == null || mirror == null) return;
        try
        {
            nearBuilder.End(mirror);
            if (depthSplitThisFrame && farBuilder != null)
                farBuilder.End(mirror);

            ShadowIndirectCommands = nearBuilder.CommandCount
                + (depthSplitThisFrame ? farBuilder?.CommandCount ?? 0 : 0);
            ShadowIndirectBatches = nearBuilder.Batches.Count
                + (depthSplitThisFrame ? farBuilder?.Batches.Count ?? 0 : 0);
            ShadowIndirectDropped = nearBuilder.CandidatesDropped
                + (depthSplitThisFrame ? farBuilder?.CandidatesDropped ?? 0 : 0);
            ShadowIndirectMissing = nearBuilder.MissingSections
                + (depthSplitThisFrame ? farBuilder?.MissingSections ?? 0 : 0);
            int addedSections = nearBuilder.AddedSections
                + (depthSplitThisFrame ? farBuilder?.AddedSections ?? 0 : 0);
            ShadowIndirectSections = addedSections;
            int total = addedSections + ShadowIndirectMissing;
            ShadowIndirectCoverage = total == 0 ? 1 : addedSections / (double)total;
        }
        catch (Exception e)
        {
            shadowBuilder = null;
            splitFarBuilder = null;
            packedBuilder = null;
            packedFarBuilder = null;
            clusterBuilder = null;
            clusterFarBuilder = null;
            capi.Logger.Warning(
                "[VintageHorizons] Shadow indirect command building disabled; visible legacy "
                + "rendering is unchanged: {0}", e.Message);
        }
    }

    public int ShadowIndirectCommands { get; private set; }
    public int ShadowIndirectSections { get; private set; }
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
                if (!SetupSectionTransform(key, renderCullDistanceSquared, waterPass: true)) continue;
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
        && OcclusionCullingEnabled && !temporalOcclusionFailed
        && !indirectDrawingThisFrame;

    /// <summary>
    /// Draw opaque cached terrain from the regional arenas instead of one call per
    /// section. Session-only and off by default: this is the first thing in the mod that
    /// puts a pixel on screen from a buffer the established renderer never touched.
    /// </summary>
    /// <remarks>
    /// Default-off, and settable from the environment so the benchmark harness can pin one
    /// side of the comparison for a whole run. Phase 3's gate is a controlled A/B, and
    /// session 40 lost a run to a comparison whose switches were set by hand and did not
    /// match; a chat command cannot be part of a scripted route.
    /// </remarks>
    public bool IndirectDrawEnabled { get; set; } =
        Environment.GetEnvironmentVariable("VINTAGEHORIZONS_GPU_INDIRECT") != "0";

    /// <summary>
    /// Pull exact twelve-byte greedy quads instead of expanded vertices/indices. It is an
    /// opt-in Phase 7 comparison layered on the same CPU candidates, section records,
    /// culling and fallback as the accepted indirect path.
    /// </summary>
    public bool PackedDrawEnabled { get; set; } =
        Environment.GetEnvironmentVariable("VINTAGEHORIZONS_GPU_PACKED") != "0";

    /// <summary>
    /// Phase 8 comparison: draw the packed stream as moderate 4x4 spatial clusters, each
    /// with its own conservative geometry bounds and cull command. Session-only and
    /// default-off until the extra commands and split quads beat their measured cost.
    /// </summary>
    public bool ClusterDrawEnabled { get; set; } =
        Environment.GetEnvironmentVariable("VINTAGEHORIZONS_GPU_CLUSTERS") != "0";

    /// <summary>
    /// Everything that has to hold before a frame may take the fast path. Read once per
    /// frame into <see cref="indirectDrawingThisFrame"/>.
    /// </summary>
    bool IndirectDrawAvailable => IndirectDrawEnabled
        && indirectShaderOk && indirectProg != null
        && indirectDrawer is { Ready: true }
        && shadowBuilder != null
        && gpuShadow.Mirror != null;

    bool PackedDrawAvailable => IndirectDrawEnabled && PackedDrawEnabled
        && packedShaderOk && packedProg != null
        && packedDrawer is { Ready: true }
        && packedBuilder != null
        && gpuShadow.Mirror != null;

    bool ClusterDrawAvailable => ClusterDrawEnabled
        && clusterBuilder != null
        && gpuShadow.Mirror != null;

    bool packedDrawingThisFrame;
    bool clusterDrawingThisFrame;

    public string DescribePackedDraw()
    {
        LodGpuGeometryMirror? mirror = gpuShadow.Mirror;
        if (!PackedDrawEnabled)
            return mirror == null
                ? "off"
                : $"off: expanded batching uses {mirror.LiveBytes / (1024.0 * 1024.0):0.0} MiB; "
                    + "unselected packed copies are not retained";
        if (!IndirectDrawEnabled)
            return "on, but idle: turn batched terrain drawing on with .vhindirect on";
        if (!packedShaderOk) return "unavailable: the packed shader variant did not compile";
        if (mirror == null || packedDrawer == null)
            return "unavailable: the regional arenas are not attached. Turn them on with .vhgpu on";
        if (packedDrawer.Failed)
            return "failed; established per-section rendering remains active: "
                + packedDrawer.FailureReason;
        if (!mirror.RetainsPacked)
            return "on through clustered packed geometry; whole-section packed and expanded "
                + "regional copies are not retained";
        return $"on: 12-byte quads, {mirror.PackedLiveBytes / (1024.0 * 1024.0):0.0} MiB; "
            + "the expanded regional copy is not retained. "
            + $"Last frame {LastIndirectBatches} multi-draws covered {LastIndirectCommands} sections, "
            + $"{LastIndirectLeftovers} used the established fallback";
    }

    public string DescribeClusterDraw()
    {
        LodGpuGeometryMirror? mirror = gpuShadow.Mirror;
        if (!ClusterDrawEnabled)
            return mirror == null
                ? "off"
                : "off: unselected clustered geometry is not retained";
        if (!PackedDrawEnabled || !IndirectDrawEnabled)
            return "on, but idle: turn on .vhindirect and .vhpacked first";
        if (mirror == null || clusterBuilder == null)
            return "unavailable: the regional arenas are not attached. Turn them on with .vhgpu on";
        return $"on: each section is up to {LodPackedClusterBuilder.CellCount} exact draw clusters. "
            + $"Last frame {LastIndirectCommands} cluster commands in {LastIndirectBatches} "
            + $"multi-draws, {LastIndirectLeftovers} whole sections used the established fallback. "
            + $"Cluster geometry uses {mirror.ClusteredPackedLiveBytes / (1024.0 * 1024.0):0.0} MiB; "
            + "other regional geometry copies are not retained";
    }

    /// <summary>
    /// What depth culling is doing, in the terms someone standing in the world can act on.
    ///
    /// It reports whether the cull actually RAN last frame, not merely whether it is switched
    /// on. Those come apart constantly - no pyramid yet, the shader refused, the indirect path
    /// is off - and a switch that says "on" while nothing happens is how a person concludes a
    /// feature does nothing when it was never running.
    /// </summary>
    public string DescribeGpuCull()
    {
        if (!GpuCullEnabled) return "off: every section the CPU approves is drawn";
        if (cullPass == null)
            return "on, but idle: the regional arenas are not attached. Turn them on with .vhgpu on";
        if (!IndirectDrawEnabled)
            return "on, but idle: culling zeroes a batched draw command, and batching is off. "
                + "Turn it on with .vhindirect on";
        if (!DepthPyramidEnabled)
            return "on, but idle: there is no depth pyramid to test against. "
                + "Turn it on with .vhhzb on";
        if (!cullPass.Available)
        {
            // The two are completely different answers and used to print the same words. One
            // is a driver saying no; the other is "you asked before a single frame had been
            // drawn", which is the normal state for the instant a chat command replies.
            return cullPass.LastFailure.Length > 0
                ? "unavailable: " + cullPass.LastFailure
                : "on, but not started yet: no frame has been drawn since you switched it on. "
                    + "Play for a moment and run .vhcull again";
        }

        string ran = ActiveDrawer is { Culled: true }
            ? "last frame's commands were culled on the card"
            : "nothing was culled last frame";
        return "on: " + cullPass.Describe() + ". " + ran + ".";
    }

    public string DescribeIndirectDraw()
    {
        if (!indirectShaderOk) return "unavailable: the indirect shader variant did not compile";
        if (indirectDrawer == null || gpuShadow.Mirror == null)
            return "unavailable: the regional arenas are not attached. Turn them on with .vhgpu on";
        if (indirectDrawer.Failed) return indirectDrawer.Describe();
        if (!IndirectDrawEnabled)
            return "off: every visible section is drawn on its own, as before";
        return "on: " + indirectDrawer.Describe()
            + $". Last frame {LastIndirectBatches} multi-draws covered {LastIndirectCommands} "
            + $"sections, {LastIndirectLeftovers} drawn the established way. "
            + "Delayed occlusion is suspended while this is on"
            + (GpuCullEnabled
                ? "; depth culling has taken over suppression."
                : ", and nothing has replaced it. Turn on .vhcull to suppress hidden terrain, "
                    + "or leave it off to measure batching on its own.");
    }

    public int LastIndirectBatches { get; private set; }
    public int LastIndirectCommands { get; private set; }
    public int LastIndirectLeftovers { get; private set; }

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
        if (liveMeshStats.TryGetValue(key, out LodLiveMeshStats stats))
        {
            OpaqueDrawVertices += stats.OpaqueVertices;
            OpaqueDrawIndices += stats.OpaqueIndices;
        }

        // In an indirect pass a section is submitted by writing its command; the draws
        // happen once, after the walk. A section the arenas do not hold produces no
        // command, so it falls through to the second sub-pass and is drawn the
        // established way - which is why partial coverage costs submissions, not terrain.
        bool recorded = RecordShadowCommand(key, out bool farBucket);
        if (indirectPassActive)
        {
            List<long> target = farBucket ? indirectFarRecorded : indirectRecorded;
            List<long> fallback = farBucket ? indirectFarLeftovers : indirectLeftovers;
            if (recorded) target.Add(key);
            else fallback.Add(key);
            return;
        }

        OpaqueDrawCalls++;
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

    bool SetupSectionTransform(long key, float cullDistSq, bool waterPass = false)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        double originX = LodWorld.KeySx(key) * (double)footprint;
        double originZ = LodWorld.KeySz(key) * (double)footprint;

        double dx = originX + footprint / 2.0 - camPos.X;
        double dz = originZ + footprint / 2.0 - camPos.Z;
        if (dx * dx + dz * dz > cullDistSq) return false;
        float horizontalDistance = (float)Math.Sqrt(dx * dx + dz * dz);

        // Camera-relative box, matching the model matrix below. The Y range is the mesh's
        // own, from the pass being drawn: opaque and water are submitted separately and a
        // shoreline's water surface sits nowhere near the seabed under it. A section whose
        // bounds are unknown - anything published without them - keeps the old
        // bedrock-to-sky box, so a missing measurement can only ever draw too much.
        double relX = originX - camPos.X;
        double relZ = originZ - camPos.Z;
        double boxMinY = -camPos.Y;
        double boxMaxY = worldHeight - camPos.Y;
        LodHeightSpan span = SectionSpan(key, waterPass);
        if (span.HasGeometry)
        {
            boxMinY = span.MinY - camPos.Y;
            boxMaxY = span.MaxY - camPos.Y;
        }

        if (!frustum.BoxInView(relX, boxMinY, relZ, relX + footprint, boxMaxY, relZ + footprint))
        {
            culledThisFrame++;

            // Only for sections the real box rejected: how many of them the old
            // full-height box would have kept. That figure is the whole justification for
            // this work, and inferring it from a frame rate is not available.
            if (span.HasGeometry
                && frustum.BoxInView(relX, -camPos.Y, relZ,
                    relX + footprint, worldHeight - camPos.Y, relZ + footprint))
            {
                verticalCulledThisFrame++;
            }
            return false;
        }

        // Recorded once the section has survived every cheaper rejection - distance and
        // frustum - so the population is exactly the sections this renderer would draw.
        // Collected before the ownership skip and the occlusion skip further down, because
        // the question being measured is what DEPTH alone would remove, and mixing in
        // sections another rule already dropped would flatter the answer.
        //
        // Opaque only: the water pass walks a subset of the same sections and would count
        // them twice, and water is not an occluder.
        if (hzbCollecting && !waterPass
            && (!depthSplitThisFrame || !LodGpuDepthSplitPolicy.IsNear(horizontalDistance)))
        {
            hzbBoxes.Add(new LodHzbSectionBox(
                key,
                (float)relX, (float)boxMinY, (float)relZ,
                (float)(relX + footprint), (float)boxMaxY, (float)(relZ + footprint),
                horizontalDistance));
        }

        // Sides that border on never-captured area, so the shader can dissolve them
        // into the horizon instead of leaving a cliff at the edge of what we've seen.
        byte openEdges = (byte)(
            (HasNeighbourData(key, -1, 0) ? 0 : LodGpuSectionFacts.OpenMinusX)
            | (HasNeighbourData(key, 1, 0) ? 0 : LodGpuSectionFacts.OpenPlusX)
            | (HasNeighbourData(key, 0, -1) ? 0 : LodGpuSectionFacts.OpenMinusZ)
            | (HasNeighbourData(key, 0, 1) ? 0 : LodGpuSectionFacts.OpenPlusZ));

        // Six uniform uploads per section, and they are most of what the indirect path
        // exists to remove. During an indirect walk they are skipped: the same values go
        // into the section record instead. A section that ends up in the leftover pass
        // comes back through here with the flag clear and is set up normally.
        if (!indirectPassActive)
        {
            modelMat.Identity().Translate(relX, -camPos.Y, relZ);
            prog!.UniformMatrix("modelMatrix", modelMat.Values);
            prog.Uniform("columnBlocks", (float)LodWorld.ColumnStepBlocks(LodWorld.KeyLevel(key)));

            // Projection/fog use camera-relative coordinates, but colour noise needs the
            // section's stable world origin or a fixed patch changes colour while flying.
            // This uniform does not affect geometry, so float precision at extreme world
            // coordinates can only soften the cosmetic variation, never move terrain.
            prog.Uniform("noiseOrigin", (float)originX, (float)originZ, 0f, 0f);

            // Ownership addressing uses this integer origin plus the section-local
            // position, never a summed world coordinate, so a fragment at a chunk edge
            // cannot round onto its neighbour's ownership at large world coordinates.
            //
            // Set as two integer uniforms, never as one Vec2i: that overload reaches
            // glUniform2f, which an integer uniform rejects with GL_INVALID_OPERATION, so
            // the origin silently stayed at zero and the per-fragment mask addressed the
            // wrong cells for every section in the world. See G42.
            if (readinessMaskActive)
            {
                prog.Uniform("maskSectionOriginX",
                    (int)(originX / VanillaReadinessMask.ChunkBlocks));
                prog.Uniform("maskSectionOriginZ",
                    (int)(originZ / VanillaReadinessMask.ChunkBlocks));
            }

            prog.Uniform("sectionSize", (float)footprint);
            prog.Uniform("openEdges",
                (openEdges & LodGpuSectionFacts.OpenMinusX) != 0 ? 1f : 0f,
                (openEdges & LodGpuSectionFacts.OpenPlusX) != 0 ? 1f : 0f,
                (openEdges & LodGpuSectionFacts.OpenMinusZ) != 0 ? 1f : 0f,
                (openEdges & LodGpuSectionFacts.OpenPlusZ) != 0 ? 1f : 0f);
        }

        // The same values a multi-draw reads from the record buffer instead. Captured
        // here so the command list is built from the traversal's real decisions, not from
        // a second, possibly disagreeing, walk of the draw list.
        if (shadowBuilder != null)
        {
            lastSectionHorizontalDistance = horizontalDistance;
            lastSectionFacts = new LodGpuSectionFacts(
                key,
                (float)relX, (float)-camPos.Y, (float)relZ,
                footprint,
                (float)originX, (float)originZ,
                LodWorld.ColumnStepBlocks(LodWorld.KeyLevel(key)),
                (int)(originX / VanillaReadinessMask.ChunkBlocks),
                (int)(originZ / VanillaReadinessMask.ChunkBlocks),
                openEdges,
                // The cull box carried alongside, taken from the same locals the frustum
                // test above just used rather than recomputed. A second derivation could
                // disagree with the first by a mesh span the pass no longer has, and a box
                // that disagrees with the one the CPU judged is how the two paths end up
                // drawing different terrain.
                (float)boxMinY,
                (float)boxMaxY);
        }
        return true;
    }

    LodGpuIndirectBuilder? shadowBuilder;
    LodGpuIndirectBuilder? splitFarBuilder;
    LodGpuIndirectBuilder? packedBuilder;
    LodGpuIndirectBuilder? packedFarBuilder;
    LodGpuIndirectBuilder? clusterBuilder;
    LodGpuIndirectBuilder? clusterFarBuilder;

    LodGpuIndirectBuilder? ActiveNearBuilder =>
        clusterDrawingThisFrame ? clusterBuilder
        : packedDrawingThisFrame ? packedBuilder
        : shadowBuilder;

    LodGpuIndirectBuilder? ActiveFarBuilder =>
        clusterDrawingThisFrame ? clusterFarBuilder
        : packedDrawingThisFrame ? packedFarBuilder
        : splitFarBuilder;

    LodGpuIndirectDrawer? ActiveDrawer =>
        packedDrawingThisFrame ? packedDrawer : indirectDrawer;

    IShaderProgram? ActiveIndirectProgram =>
        packedDrawingThisFrame ? packedProg : indirectProg;
    LodGpuSectionFacts lastSectionFacts;
    float lastSectionHorizontalDistance;

    /// <summary>
    /// Records what a regional multi-draw would have submitted for a section the legacy
    /// path is drawing right now. It runs after every CPU decision the visible path makes -
    /// ownership skip, distance cap, frustum, temporal occlusion - so the command list is
    /// the one Phase 3 would actually issue, and the batch count it reports is the real
    /// answer to how far draw calls would fall.
    /// </summary>
    bool RecordShadowCommand(long key, out bool farBucket)
    {
        farBucket = false;

        // Ordered so the visible path pays one null field read per drawn section when the
        // shadow is off, which is the ordinary case.
        LodGpuIndirectBuilder? nearBuilder = ActiveNearBuilder;
        if (nearBuilder == null) return false;

        // Fail open before even choosing a bucket. A stale distance is no safer than stale
        // box facts, and could otherwise put the fallback on the wrong side of the rebuild.
        if (lastSectionFacts.SectionKey != key)
        {
            nearBuilder.AddMissing();
            return false;
        }

        farBucket = depthSplitThisFrame
            && !LodGpuDepthSplitPolicy.IsNear(lastSectionHorizontalDistance);
        LodGpuIndirectBuilder? farBuilder = ActiveFarBuilder;
        LodGpuIndirectBuilder builder = farBucket && farBuilder != null
            ? farBuilder
            : nearBuilder;
        LodGpuGeometryMirror? mirror = gpuShadow.Mirror;
        if (mirror == null) return false;
        if (!mirror.TryGet(key, out LodGpuGeometryMirror.MirroredSection section))
        {
            // Drawn, but the arenas never managed to hold it. Counted rather than ignored:
            // batches over a partial mirror are not a draw-call reduction, and silence here
            // is exactly what made the first measured run look better than it was.
            builder.AddMissing();
            return false;
        }
        // The facts were written by the SetupSectionTransform call for this same section, one
        // step earlier in the walk. That coupling is ordering alone, and it used to be cheap
        // to get wrong: a mismatch skewed a measurement nobody was acting on. It is not cheap
        // now. The cull tests THIS record's box and zeroes THIS command, so stale facts would
        // hide a section on the strength of a different section's position, and the symptom
        // would be scattered terrain missing in some views - the exact fault this renderer
        // has already spent two sessions chasing once.
        //
        // The packed representation is optional during Phase 7 publication. A section whose
        // packed span was refused must enter this frame's legacy leftovers, not disappear from
        // both lists merely because the expanded mirror record exists.
        return builder.Add(section, lastSectionFacts);
    }

    int culledThisFrame;
    int traversalCulledThisFrame;
    int subtreeCulledThisFrame;
    int subtreeCulledMeshesThisFrame;
    int verticalCulledThisFrame;

    /// <summary>
    /// Sections rejected this interval by the mesh's own height that the old
    /// bedrock-to-sky box would have kept, summed over frames, and the worst single frame.
    /// This is the established renderer's share of Phase 3b, and it is a saving whether or
    /// not the GPU fast path is ever adopted.
    /// </summary>
    public long VerticalCulledSections { get; private set; }
    public int VerticalCulledMax { get; private set; }
    public int LastVerticalCulledCount { get; private set; }

    /// <summary>Quadtree nodes rejected this interval that a full-height box would have traversed.</summary>
    public long SubtreeCulledNodes { get; private set; }

    /// <summary>Resident meshes those rejected subtrees held, which is what the rejection saved.</summary>
    public long SubtreeCulledMeshes { get; private set; }
    public int SubtreeCulledMax { get; private set; }
    public int LastSubtreeCulledCount { get; private set; }
    public int SubtreeBoundsNodes => subtreeHeights.TrackedNodes;

    public LodSectionHeightStats SectionHeights => sectionHeightStats;
    readonly LodSectionHeightStats sectionHeightStats = new();

    void NoteSectionHeights(LodSectionHeights heights) =>
        sectionHeightStats.Add(heights.Either);

    /// <summary>
    /// The vertical extent of the geometry this pass would draw for the section, or an
    /// empty span when nothing recorded it. Read from the live mesh record the publication
    /// already maintains, so no second dictionary has to be kept in step with residency.
    /// </summary>
    /// <summary>
    /// Copies this frame's depth and reduces it into the pyramid. Nothing reads the result
    /// yet: this phase exists to establish what the pyramid COSTS, because Phase 4's gate is
    /// that copy and build time stay under the drawing they could remove, and no amount of
    /// source reading answers that.
    ///
    /// Failure is not an error state. A frame that could not build one simply has no
    /// pyramid, and a later phase with no pyramid must draw everything - the same direction
    /// every other fallback in this renderer fails in.
    /// </summary>
    void BuildDepthPyramid(bool splitMid = false)
    {
        if (!DepthPyramidEnabled || !hzbShaderOk) return;

        LodPhaseStart phase = LodPhaseCost.Start(TrackPhaseAllocations);
        bool timing = false;
        try
        {
            if (gpuFailureInjection.Take(LodGpuFailureStage.DepthCopy))
                throw new InvalidOperationException("Phase 9 injected a depth-copy failure");

            depthPyramid ??= new LodGpuDepthPyramid(capi, () => hzbShaderOk ? hzbProg : null);

            // Probed per frame rather than cached: the engine is free to change which
            // framebuffer it draws into, and a pyramid copied out of last frame's
            // attachment would be wrong in exactly the way nobody would notice.
            LodGpuDepthFacts depth = LodGpuCapabilities.ProbeActiveDepth();

            timing = splitMid
                ? gpuTelemetry.BeginSplitHzb()
                : gpuTelemetry.BeginHzb();
            LastHzbBuild = depthPyramid.Build(depth);

            if (!LastHzbBuild.Built && !reportedHzbFailure)
            {
                reportedHzbFailure = true;
                capi.Logger.Warning(
                    "[VintageHorizons] the depth pyramid could not be built and nothing "
                    + "depends on it yet: {0}", LastHzbBuild.FailureReason);
            }
            else if (LastHzbBuild.Built && !reportedHzbBuilt)
            {
                reportedHzbBuilt = true;
                capi.Logger.Notification(
                    "[VintageHorizons] depth pyramid built: {0}x{1}, {2} levels.",
                    LastHzbBuild.Width, LastHzbBuild.Height, LastHzbBuild.Levels);
            }
        }
        catch (Exception e)
        {
            LastHzbBuild = default;
            DepthPyramidEnabled = false;
            capi.Logger.Warning(
                "[VintageHorizons] depth pyramid disabled for this session; rendering is "
                + "unchanged: {0}", e.Message);
        }
        finally
        {
            // A build that did not happen - a minimised window, a refused depth attachment -
            // must not leave a near-zero sample in an average of real builds.
            if (timing)
            {
                if (splitMid)
                {
                    if (LastHzbBuild.Built) gpuTelemetry.EndSplitHzb();
                    else gpuTelemetry.DiscardSplitHzb();
                }
                else
                {
                    if (LastHzbBuild.Built) gpuTelemetry.EndHzb();
                    else gpuTelemetry.DiscardHzb();
                }
            }
            HzbCost.Add(phase);
        }
    }

    bool reportedHzbFailure;
    bool reportedHzbBuilt;
    bool reportedHzbClassifier;

    LodHzbClassifier? hzbClassifier;
    Action<long, uint>? hzbVerdictObserver;
    long hzbDispatchEpoch = -1;
    long hzbVerdictEpoch = -1;
    bool hzbCollecting;

    /// <summary>
    /// Sections the pyramid called hidden that a completed occlusion query had positively
    /// SEEN. This is the number Phase 4's correctness gate is about, and it must be zero:
    /// every one of these is terrain a player could see that a depth test would have
    /// deleted. Counted rather than trusted, because the shader cannot be reasoned into
    /// being right about this and the symptom - terrain missing in some views - is exactly
    /// what this renderer has already lost sessions to.
    /// </summary>
    public long HzbHiddenButQuerySawIt { get; private set; }

    /// <summary>
    /// The safe disagreement: the query proved it hidden and the pyramid did not. Nothing
    /// is wrong with these, and their count is the honest measure of how much the pyramid
    /// is leaving on the table against a test that observes real pixels.
    /// </summary>
    public long HzbMissedWhatQueryHid { get; private set; }

    /// <summary>Verdicts that could be checked against a current query result at all.</summary>
    public long HzbVerdictsChecked { get; private set; }

    /// <summary>
    /// Sections the pyramid hid that the existing delayed occlusion had no opinion on.
    /// THIS is the phase's real prize: the renderer already skips much of what the pyramid
    /// finds, so an overall hidden percentage flatters it badly. What is genuinely new is
    /// only what nothing else had measured.
    /// </summary>
    public long HzbHiddenBeyondQueries { get; private set; }

    /// <summary>Both agreed it was hidden - correct, but not a new saving.</summary>
    public long HzbAgreedHidden { get; private set; }

    /// <summary>Comparisons refused because the two answers came from different views.</summary>
    public long HzbVerdictsAcrossViewChange { get; private set; }

    /// <summary>
    /// How stale an occlusion-query answer may be and still be treated as describing the
    /// same view as a pyramid verdict. Four frames: the verdict is already one frame old by
    /// the time it is read, and at 400 fps four frames is 10 ms, which is short enough that
    /// the camera cannot have moved far and long enough that ordinary query cadence still
    /// supplies comparisons. This bounds the comparison, not the renderer - a refused
    /// comparison costs a sample, never a draw.
    /// </summary>
    const int HzbComparisonMaxQueryAgeFrames = 4;

    /// <summary>
    /// Checks one verdict against the delayed occlusion query for the same section.
    ///
    /// Only current, completed query results count as evidence. A query that is still
    /// pending, or whose answer was thrown away by a view change or a mesh replacement,
    /// says nothing - and treating its silence as "visible" would invent disagreements
    /// that are really just missing data.
    /// </summary>
    // Last verdict per section, kept only so a person can ask about one. Cleared with the
    // interval, so it can never grow past the sections a single reporting window touched.
    readonly Dictionary<long, uint> hzbLastVerdict = new();

    void ObserveHzbVerdict(long key, uint verdict)
    {
        hzbLastVerdict[key] = verdict;

        if (!temporalOcclusionQueries.TryGetValue(key, out TemporalOcclusionQuery? query)) return;

        // Both answers have to describe the same view, and the occlusion epoch is too coarse
        // a test for that: it advances on a global invalidation, not on ordinary mouse
        // movement. Measured - a run with ZERO epoch changes still produced seven sections
        // the pyramid called hidden that a query had seen. So the age of the query's own
        // answer is checked as well: a result taken many frames ago describes a camera that
        // has since moved, whatever the epoch says.
        if (hzbVerdictEpoch != temporalOcclusionEpoch)
        {
            HzbVerdictsAcrossViewChange++;
            return;
        }

        long queryAge = frameCounter - query.State.LastQueryFrame;
        if (queryAge < 0 || queryAge > HzbComparisonMaxQueryAgeFrames)
        {
            HzbVerdictsAcrossViewChange++;
            return;
        }

        bool queryKnowsVisible = query.State.KnownVisible;
        bool queryKnowsHidden = query.State.Occluded;
        if (!queryKnowsVisible && !queryKnowsHidden)
        {
            // No query opinion at all. This is where the pyramid earns its keep over the
            // existing delayed occlusion: a section it can hide that nothing has measured
            // yet is a saving the current renderer does not already have.
            if (verdict == LodHzbClassifier.VerdictOccluded) HzbHiddenBeyondQueries++;
            return;
        }

        HzbVerdictsChecked++;

        if (verdict == LodHzbClassifier.VerdictOccluded && queryKnowsVisible)
        {
            HzbHiddenButQuerySawIt++;
            return;
        }
        if (verdict == LodHzbClassifier.VerdictOccluded && queryKnowsHidden)
        {
            HzbAgreedHidden++;
            return;
        }
        if (verdict != LodHzbClassifier.VerdictOccluded && queryKnowsHidden)
        {
            HzbMissedWhatQueryHid++;
        }
    }
    readonly List<LodHzbSectionBox> hzbBoxes = new(1024);
    readonly float[] hzbViewProjection = new float[16];

    /// <summary>Shadow-mode verdicts for the interval. Nothing drawn depends on them.</summary>
    internal LodHzbVerdicts HzbVerdicts => hzbClassifier?.Interval ?? default;

    /// <summary>
    /// Hands this frame's section boxes to the card and collects last frame's verdicts.
    ///
    /// Runs after the opaque walk, so the boxes are the ones the renderer actually decided
    /// on rather than a second walk's reconstruction of them, and after the pyramid exists.
    /// The verdicts are counted and nothing else: the established path has already drawn
    /// every one of these sections by the time the answer arrives.
    /// </summary>

    /// <summary>
    /// Creates the classifier and its compute program, once, outside any timed phase.
    /// Returns false while it is unavailable, which costs the measurement and nothing else.
    /// </summary>
    bool PrepareClassifier()
    {
        if (hzbClassifier == null)
        {
            hzbClassifier = new LodHzbClassifier(
                message => capi.Logger.Warning("{0}", message));
            hzbVerdictObserver ??= ObserveHzbVerdict;
            hzbClassifier.VerdictObserver = hzbVerdictObserver;
        }

        if (!hzbClassifier.Available && !hzbClassifier.TryCreate())
        {
            if (!reportedHzbClassifier)
            {
                reportedHzbClassifier = true;
                capi.Logger.Warning(
                    "[VintageHorizons] depth-pyramid classification is unavailable and "
                    + "nothing that draws depends on it: {0}", hzbClassifier.LastFailure);
            }
            return false;
        }

        if (!reportedHzbClassifier)
        {
            reportedHzbClassifier = true;
            capi.Logger.Notification(
                "[VintageHorizons] depth-pyramid classification running in shadow: it "
                + "counts what a depth test would hide and hides nothing.");
        }

        return true;
    }

    void ClassifyAgainstDepthPyramid()
    {
        if (!DepthPyramidEnabled || depthPyramid == null || !depthPyramid.Allocated) return;
        if (!LastHzbBuild.Built) return;

        // Built before the clock starts. Compiling and linking a compute program is a
        // one-time cost of about 160 ms on this driver, and timing it as part of the phase
        // reported a 160,690 us maximum for work that happens once and never again - which
        // is worse than useless in a figure whose whole job is to say what the pyramid
        // costs every frame.
        if (!PrepareClassifier()) return;

        LodPhaseStart phase = LodPhaseCost.Start(TrackPhaseAllocations);
        bool timing = false;
        try
        {
            // Timed apart from the pyramid build, because the two scale with different
            // things and only this one scales with the sampling width. Started after
            // PrepareClassifier for the same reason the build is: a one-time shader compile
            // inside the clock reports a per-frame cost that never happens again.
            timing = gpuTelemetry.BeginClassify();

            // The epoch these boxes were projected under. Verdicts arrive a frame later and
            // are compared against occlusion queries whose answers carry their own epoch;
            // without this the two can be from different views, and a camera turn between
            // them manufactures disagreements that are really just a mismatch. Measured:
            // zero unsafe disagreements over 2.9 million stationary checks, then 46 the
            // moment the camera turned.
            // The batch about to be READ was dispatched under the previous epoch; the one
            // about to be dispatched belongs to this one. Swapped in that order because
            // Classify reads before it dispatches.
            // Ahead of the epoch swap, because the swap is a hand-off: it says the batch about
            // to be dispatched belongs to this epoch. Advancing it and then not dispatching
            // would pair the next frame's verdicts with the wrong epoch entirely.
            frustum.CopyViewProjection(hzbViewProjection);

            hzbVerdictEpoch = hzbDispatchEpoch;
            hzbDispatchEpoch = temporalOcclusionEpoch;
            hzbClassifier!.Classify(
                hzbBoxes,
                hzbViewProjection,
                depthPyramid.TextureName,
                depthPyramid.Width,
                depthPyramid.Height,
                depthPyramid.Levels);
        }
        catch (Exception e)
        {
            capi.Logger.Warning(
                "[VintageHorizons] depth-pyramid classification stopped; rendering is "
                + "unchanged: {0}", e.Message);
            hzbClassifier?.Dispose();
            hzbClassifier = null;
        }
        finally
        {
            if (timing) gpuTelemetry.EndClassify();
            HzbCost.Add(phase);
        }
    }


    /// <summary>
    /// Look at a piece of distant terrain and ask why the depth test did or did not hide
    /// it.
    ///
    /// The counters say how often something happens; this says why it happened HERE, which
    /// is the only form a person can check against what is on their screen. It reports the
    /// card's verdict beside the CPU's own projection of the same box - and because the two
    /// implement the same rules, the CPU half can name the specific reason the card could
    /// only encode as a number.
    /// </summary>
    public string ExplainDepthPyramid(double startX, double startY, double startZ,
        float lookX, float lookY, float lookZ, int maxBlocks)
    {
        if (!hzbShaderOk) return "the hzbreduce shader is not available, so no pyramid exists";
        if (!DepthPyramidEnabled) return "the depth pyramid is off. Turn it on with .vhhzb on";
        if (depthPyramid == null || !depthPyramid.Allocated) return "no pyramid has been built yet";

        // Its own array, and not the one the cull writes: this runs on the chat thread while
        // the render thread is using that one. Filled once, and every projection below reads
        // it, so the whole explanation describes a single picture rather than drifting between
        // frames as it walks.
        frustum.CopyViewProjection(explainViewProjection);

        long key = 0;
        bool found = false;
        double hitDistance = 0;

        // A section adjacent to the camera has corners behind the camera plane, so the only
        // answer it can ever give is "declined". The first two attempts at this command both
        // landed on one and told nobody anything. Keep the first such section as a fallback,
        // then keep walking for one the test can actually judge.
        long fallbackKey = 0;
        bool haveFallback = false;
        double fallbackDistance = 0;

        for (int distance = 32; distance <= maxBlocks && !found; distance += 32)
        {
            double x = startX + lookX * distance;
            double y = startY + lookY * distance;
            double z = startZ + lookZ * distance;
            if (y < 0 || y >= worldHeight) break;

            // Finest first. A point sits inside sections at every level at once, and the
            // one being drawn there is the finest that holds a mesh - coarser parents exist
            // over the same ground and stop being drawn once their children are ready.
            // Searching coarsest-first reported a 2048-block L5 section 32 blocks away,
            // which was simply the largest box containing the camera and told nobody
            // anything about what they were looking at.
            for (int level = 0; level <= LodWorld.MaxLevel && !found; level++)
            {
                int footprint = LodSection.SectionBlocks << level;
                long candidate = LodWorld.SectionKey(
                    level, (int)Math.Floor(x / footprint), (int)Math.Floor(z / footprint));
                if (!liveMeshStats.ContainsKey(candidate)) continue;

                // Never the section the camera is standing in: its box encloses the near
                // plane, so the only thing it can ever report is that the test declined.
                double sectionOriginX = LodWorld.KeySx(candidate) * (double)footprint;
                double sectionOriginZ = LodWorld.KeySz(candidate) * (double)footprint;
                if (camPos.X >= sectionOriginX && camPos.X < sectionOriginX + footprint
                    && camPos.Z >= sectionOriginZ && camPos.Z < sectionOriginZ + footprint)
                {
                    continue;
                }

                // Judgeable? Ask before settling on it.
                int candidateSize = LodWorld.KeyFootprintBlocks(candidate);
                double candidateOriginX2 = LodWorld.KeySx(candidate) * (double)candidateSize;
                double candidateOriginZ2 = LodWorld.KeySz(candidate) * (double)candidateSize;
                LodHeightSpan candidateSpan = SectionSpan(candidate, waterPass: false);
                double candidateMinY = candidateSpan.HasGeometry
                    ? candidateSpan.MinY - camPos.Y : -camPos.Y;
                double candidateMaxY = candidateSpan.HasGeometry
                    ? candidateSpan.MaxY - camPos.Y : worldHeight - camPos.Y;

                LodHzbScreenBounds probe = LodHzbProjection.Project(
                    explainViewProjection,
                    candidateOriginX2 - camPos.X, candidateMinY, candidateOriginZ2 - camPos.Z,
                    candidateOriginX2 - camPos.X + candidateSize, candidateMaxY,
                    candidateOriginZ2 - camPos.Z + candidateSize);

                if (!probe.Usable)
                {
                    if (!haveFallback)
                    {
                        fallbackKey = candidate;
                        fallbackDistance = distance;
                        haveFallback = true;
                    }
                    continue;
                }

                key = candidate;
                hitDistance = distance;
                found = true;
            }
        }

        if (!found && haveFallback)
        {
            key = fallbackKey;
            hitDistance = fallbackDistance;
            found = true;
        }

        if (!found)
            return $"nothing cached is drawn along that line within {maxBlocks} blocks";

        int size = LodWorld.KeyFootprintBlocks(key);
        double originX = LodWorld.KeySx(key) * (double)size;
        double originZ = LodWorld.KeySz(key) * (double)size;
        double relX = originX - camPos.X;
        double relZ = originZ - camPos.Z;

        LodHeightSpan span = SectionSpan(key, waterPass: false);
        double boxMinY = span.HasGeometry ? span.MinY - camPos.Y : -camPos.Y;
        double boxMaxY = span.HasGeometry ? span.MaxY - camPos.Y : worldHeight - camPos.Y;

        LodHzbScreenBounds bounds = LodHzbProjection.Project(
            explainViewProjection, relX, boxMinY, relZ, relX + size, boxMaxY, relZ + size);

        var report = new System.Text.StringBuilder();
        report.Append($"L{LodWorld.KeyLevel(key)} section at {originX:0},{originZ:0}, ");
        report.Append($"{size} blocks across, about {hitDistance:0} blocks away. ");
        report.Append(span.HasGeometry
            ? $"Its terrain runs from y={span.MinY:0} to y={span.MaxY:0} ({span.Height:0} tall). "
            : "It has no recorded height, so it is bounded bedrock to sky. ");

        if (!bounds.Usable)
        {
            report.Append($"The test declined to judge it: {bounds.Reason}. ");
            report.Append("That always means draw it.");
            return report.ToString();
        }

        int levelUsed = LodHzbProjection.LevelFor(
            bounds.WidthPixels(depthPyramid.Width),
            bounds.HeightPixels(depthPyramid.Height),
            depthPyramid.Levels);
        report.Append($"On screen it covers about {bounds.WidthPixels(depthPyramid.Width):0} by ");
        report.Append($"{bounds.HeightPixels(depthPyramid.Height):0} pixels, which is read at ");
        report.Append($"pyramid level {levelUsed} of {depthPyramid.Levels - 1}. ");

        if (hzbLastVerdict.TryGetValue(key, out uint verdict))
        {
            report.Append(verdict switch
            {
                LodHzbClassifier.VerdictOccluded =>
                    "The card found everything in front of it nearer, so a depth test would hide it. ",
                LodHzbClassifier.VerdictBackground =>
                    "The card refused it because its rectangle includes sky, where nothing was "
                    + "drawn. No box overlapping open sky can ever be hidden, however buried its "
                    + "terrain is - which is a sign the box is too big rather than that the "
                    + "terrain is in view. ",
                LodHzbClassifier.VerdictFailedOpen =>
                    "The card declined to judge it and it is drawn. ",
                LodHzbClassifier.VerdictNearPlane =>
                    "The card declined: part of its box sits at or behind the camera plane, "
                    + "which happens to any section close enough to stand beside. It is drawn. ",
                LodHzbClassifier.VerdictOffScreen =>
                    "The card declined: it projects entirely off screen, which is the frustum "
                    + "test's business rather than the depth test's. It is drawn. ",
                LodHzbClassifier.VerdictDegenerate =>
                    "The card declined: its projection produced a value nothing can be judged "
                    + "from. It is drawn. ",
                _ => "The card found something behind the scene here, so it is genuinely in view. ",
            });
        }
        else
        {
            report.Append("The card has not returned a verdict for it in this reporting window. ");
        }

        if (temporalOcclusionQueries.TryGetValue(key, out TemporalOcclusionQuery? query))
        {
            report.Append(query.State.KnownVisible
                ? "An occlusion query has actually seen pixels of it, so it is really visible."
                : query.State.Occluded
                    ? "An occlusion query found it hidden too."
                    : "No current occlusion query result exists to check that against.");
        }
        else
        {
            report.Append("No occlusion query is tracking it.");
        }

        return report.ToString();
    }


    /// <summary>
    /// The whole depth-pyramid measurement, on demand.
    ///
    /// It exists because the periodic report fires once, thirty seconds after joining -
    /// which is before anyone has had a chance to turn this on, so in an ordinary session
    /// every figure below was collected and never shown. A measurement that can only be
    /// read by restarting with an environment variable set is not one a person will use.
    ///
    /// No angle brackets anywhere in here: this reaches the chat window, which parses its
    /// text as VTML, and a leading one silently swallows the whole reply (G67).
    /// </summary>
    /// <summary>
    /// What the aggregate subtree bound has actually rejected, on demand.
    ///
    /// The periodic report carries the same figures, but it fires once thirty seconds
    /// after a world loads unless continuous telemetry is switched on - which is before
    /// anybody has pointed the camera anywhere on purpose, and while terrain is still
    /// streaming. The number this work is judged on comes from looking up, looking down,
    /// or flying, so it has to be readable at the moment somebody is doing that.
    /// </summary>
    public string ReportSubtreeCulling()
    {
        if (!SubtreeHeightCulling)
        {
            return "subtree height culling off. Turn it on with .vhsubtree on, look up or "
                + "down or fly for a few seconds, then run .vhsubtree again";
        }

        var report = new System.Text.StringBuilder();
        report.Append($"subtree cull: {SubtreeCulledNodes} quadtree nodes rejected since the "
            + $"last reset that a full-height box would have traversed, holding "
            + $"{SubtreeCulledMeshes} resident meshes; worst single frame {SubtreeCulledMax} "
            + $"nodes, last frame {LastSubtreeCulledCount}");

        report.AppendLine();
        report.Append($"coverage: {SubtreeBoundsNodes} nodes carry an aggregate over "
            + $"{MeshCount} resident meshes | this frame the walk rejected "
            + $"{LastTraversalCulledCount} subtrees in total, drew {LastDrawCount}");

        report.AppendLine();
        report.Append(SubtreeCulledNodes == 0
            ? "nothing has been rejected vertically yet. That is a real answer, not a "
                + "failure: it would mean the bounds are correct and never tight enough to "
                + "reject. Run .vhsubtree reset, then look straight up or straight down for "
                + "a few seconds and read it again"
            : "run .vhsubtree reset before a specific view to measure that view alone");
        return report.ToString();
    }

    /// <summary>
    /// The floor and ceiling of every mesh RESIDENT RIGHT NOW, on demand.
    ///
    /// The periodic `section heights:` line measures a different population: the meshes
    /// PUBLISHED during an interval. Those are two different questions, and only one of
    /// them can be asked after a world settles - once terrain stops arriving nothing is
    /// published, so the periodic line reports an empty interval exactly when the answer
    /// matters. Worse, the only interval it has ever reported is the first thirty seconds
    /// after joining, which is the noisiest possible sample: a section meshed before its
    /// neighbour arrives walls its open side down to bedrock, and that wall is in the
    /// bounds until the seam repair rebuilds it.
    ///
    /// So this walks the live set instead. The floor bands are the point of it: a mean
    /// cannot tell "the terrain here really does run deep" apart from "most floors are
    /// honest and a few are pinned to bedrock", and those want opposite responses. The
    /// count of meshes still carrying a guessed edge is beside them, because that is the
    /// population the phantom walls can come from - if the floors are low while that count
    /// is near zero, missing neighbours are not the cause and the next look goes elsewhere.
    /// </summary>
    public string ReportLiveSectionHeights()
    {
        if (liveMeshStats.Count == 0) return "no meshes are resident yet";

        var stats = new LodSectionHeightStats();
        Span<int> floorBands = stackalloc int[5];
        int unknown = 0;
        float lowestFloor = float.PositiveInfinity;

        foreach (LodLiveMeshStats mesh in liveMeshStats.Values)
        {
            LodHeightSpan span = mesh.Heights.Either;
            if (!span.HasGeometry) { unknown++; continue; }
            stats.Add(span);
            if (span.MinY < lowestFloor) lowestFloor = span.MinY;
            floorBands[
                span.MinY <= 8f ? 0
                : span.MinY <= 32f ? 1
                : span.MinY <= 64f ? 2
                : span.MinY <= 128f ? 3
                : 4]++;
        }

        if (stats.Samples == 0)
            return $"{liveMeshStats.Count} meshes are resident and none of them reported bounds";

        var report = new System.Text.StringBuilder();
        report.Append("live section heights: ").Append(stats.Describe(worldHeight));
        if (unknown > 0) report.Append($" | {unknown} resident meshes reported no bounds");

        report.AppendLine();
        report.Append($"floors of the {stats.Samples} resident meshes: ");
        report.Append($"y0-8: {floorBands[0] * 100.0 / stats.Samples:0}%, ");
        report.Append($"y8-32: {floorBands[1] * 100.0 / stats.Samples:0}%, ");
        report.Append($"y32-64: {floorBands[2] * 100.0 / stats.Samples:0}%, ");
        report.Append($"y64-128: {floorBands[3] * 100.0 / stats.Samples:0}%, ");
        report.Append($"y128+: {floorBands[4] * 100.0 / stats.Samples:0}% ");
        report.Append($"(lowest y={lowestFloor:0})");

        report.AppendLine();
        report.Append($"edges: {meshedWithoutNeighbor.Count} resident meshes still carry a side "
            + $"guessed at because the neighbour was not in memory; {SeamRepairsQueued} seam "
            + "repairs have been queued since joining");

        report.AppendLine();
        report.Append(floorBands[0] * 2 > stats.Samples
            ? "most floors sit at bedrock. If this reading was taken after terrain settled, "
                + "the walls are being left behind rather than repaired, and every box-shaped "
                + "test is being told the terrain is far deeper than it is"
            : "most floors sit above bedrock, so the bounds describe terrain rather than the "
                + "whole world column");
        return report.ToString();
    }

    /// <summary>Zero the subtree-cull counters so one view can be measured on its own.</summary>
    public void ResetSubtreeCullingInterval()
    {
        SubtreeCulledNodes = 0;
        SubtreeCulledMeshes = 0;
        SubtreeCulledMax = 0;
    }

    public string ReportDepthPyramid()
    {
        if (!hzbShaderOk) return "the hzbreduce shader is not available, so no pyramid exists";
        if (!DepthPyramidEnabled)
            return "depth pyramid off. Turn it on with .vhhzb on, play for a few seconds, then run .vhhzb again";

        var report = new System.Text.StringBuilder();
        report.Append("depth pyramid: ").Append(DescribeDepthPyramid());

        report.AppendLine();
        report.Append($"cost: gpu {GpuHzbCost.AvgUs:0.0}us avg / {GpuHzbCost.MaxUs:0.0}us max");
        report.Append($" over {GpuHzbCost.Calls} timed builds");
        if (LateDepthPyramid)
        {
            report.Append($" | mid-frame gpu {GpuSplitHzbCost.AvgUs:0.0}us avg / "
                + $"{GpuSplitHzbCost.MaxUs:0.0}us max over {GpuSplitHzbCost.Calls} builds");
        }
        report.Append($" | classify gpu {GpuClassifyCost.AvgUs:0.0}us avg / "
            + $"{GpuClassifyCost.MaxUs:0.0}us max over {GpuClassifyCost.Calls} dispatches");
        report.Append($" | cpu {HzbCost.AvgUs:0.0}us avg / {HzbCost.MaxUs:0.0}us max");
        report.Append(" | gpu timing ").Append(DescribeGpuTiming());

        report.AppendLine();
        report.Append("picture: ").Append(DescribeLateDepthPyramid());

        report.AppendLine();
        report.Append("actual live commands: ")
            .Append(cullPass?.DescribeLiveTelemetry()
                ?? "the live cull pass has not been created");

        string byDistance = DescribeDepthPyramidByDistance();
        if (byDistance.Length > 0)
        {
            report.AppendLine();
            report.Append("hidden by distance: ").Append(byDistance);
        }

        string subdivision = DescribeDepthPyramidSubdivision();
        if (subdivision.Length > 0)
        {
            report.AppendLine();
            report.Append("headroom: ").Append(subdivision);
        }

        string undecided = DescribeDepthPyramidUndecided();
        if (undecided.Length > 0)
        {
            report.AppendLine();
            report.Append("undecided because: ").Append(undecided);
        }

        if (HzbVerdictsChecked > 0)
        {
            report.AppendLine();
            report.Append($"against occlusion queries: {HzbVerdictsChecked} checked, ");
            report.Append($"{HzbHiddenButQuerySawIt} called hidden that a query SAW ");
            report.Append(HzbHiddenButQuerySawIt == 0 ? "(good, must be zero)" : "(BAD, must be zero)");
            report.Append($", {HzbAgreedHidden} both hid");
            report.Append($", {HzbMissedWhatQueryHid} the query hid and the pyramid did not");
            report.AppendLine();
            report.Append($"new saving: {HzbHiddenBeyondQueries} hidden that no query had measured");
            report.Append(" - this is what the pyramid adds over the occlusion already running");
            report.Append($" | {HzbVerdictsAcrossViewChange} comparisons refused across a view change");
        }
        else
        {
            report.AppendLine();
            report.Append("against occlusion queries: nothing checked yet - no section has both ");
            report.Append("a pyramid verdict and a current query result");
        }

        return report.ToString();
    }

    /// <summary>
    /// Whether the second, same-frame depth opportunity is active and whether its mid-frame
    /// builds are actually succeeding.
    /// </summary>
    public string DescribeLateDepthPyramid()
    {
        if (!LateDepthPyramid)
            return "taken after the game's own terrain and before cached terrain, so only the "
                + "game's nearby hills can hide anything - cached hills cannot. "
                + "Switch with .vhlate on";

        if (SplitDepthFrames == 0)
            return "same-frame near/far split requested, but no split frame has drawn yet";

        return $"same-frame split at {LodGpuDepthSplitPolicy.NearRadiusBlocks:0} blocks "
            + $"with the established {LodHzbProjection.OcclusionDepthBiasSteps}-step safety "
            + $"margin on both buckets: "
            + $"{SplitDepthFrames} frames, {SplitDepthNearCommands} near commands and "
            + $"{SplitDepthFarCommands} far commands, {SplitDepthMidBuilds} mid-frame "
            + "pictures completed. A failed picture draws the far bucket without culling.";
    }

    /// <summary>Hidden share per distance band; empty until something has been classified.</summary>
    public string DescribeDepthPyramidByDistance() => hzbClassifier?.DescribeByDistance() ?? "";

    /// <summary>Why the test declined, when it did; empty when it never declined.</summary>
    public string DescribeDepthPyramidUndecided() => hzbClassifier?.DescribeUndecided() ?? "";

    /// <summary>
    /// What a finer draw unit would add. Measured, not argued: the question of whether
    /// cluster subdivision should move ahead of the rest of the plan is the owner's, and
    /// this is the evidence it should be decided on.
    /// </summary>
    public string DescribeDepthPyramidSubdivision() => hzbClassifier?.DescribeSubdivision() ?? "";

    /// <summary>One line for the periodic report and for `.vhhzb`.</summary>
    public string DescribeDepthPyramid()
    {
        if (!hzbShaderOk) return "the hzbreduce shader is not available";
        if (!DepthPyramidEnabled) return "off (.vhhzb on)";
        if (depthPyramid == null) return "on, not yet built";
        string classifier = hzbClassifier == null
            ? "classification not started"
            : hzbClassifier.Describe();
        return depthPyramid.Describe() + " || " + classifier;
    }

    LodHeightSpan SectionSpan(long key, bool waterPass) =>
        SectionHeightCulling && liveMeshStats.TryGetValue(key, out LodLiveMeshStats stats)
            ? (waterPass ? stats.Heights.Water : stats.Heights.Opaque)
            : LodHeightSpan.Empty;

    /// <summary>
    /// The box a quadtree node may be traversed with, or the empty span when it must keep
    /// the full-height one. Unknown aggregates and the switch being off both arrive here as
    /// empty, so there is one place where a subtree can lose its real bounds.
    /// </summary>
    LodHeightSpan SubtreeSpan(long key) =>
        SubtreeHeightCulling ? subtreeHeights.Of(key).CullingSpan : LodHeightSpan.Empty;

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
        subtreeHeights.Clear();
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
        tints.InvalidateReadiness();
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
        indirectDrawer?.Dispose();
        indirectDrawer = null;
        packedDrawer?.Dispose();
        packedDrawer = null;
        cullPass?.Dispose();
        cullPass = null;
        depthPyramid?.Dispose();
        depthPyramid = null;
        hzbClassifier?.Dispose();
        hzbClassifier = null;
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
