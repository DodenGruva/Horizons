using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using VintageHorizons.Net;

namespace VintageHorizons;

public class VintageHorizonsConfig
{
    /// <summary>0 = unlimited.</summary>
    public int FarViewDistanceCap = 0;

    /// <summary>Legacy first threshold used to migrate configs without LodThresholds.</summary>
    public int DetailDistance = 512;

    /// <summary>
    /// L1-L6 transition distances. Null migrates an older config by doubling
    /// DetailDistance; saved configs always contain the explicit thresholds.
    /// </summary>
    public int[]? LodThresholds;

    /// <summary>
    /// Give each vanilla chunk its own ground instead of using one measured distance.
    /// On by default since 0.3.17. `VINTAGEHORIZONS_CHUNK_MASK` overrides this in either
    /// direction for benchmarking; see <see cref="LodTerrainRenderer.ChunkMaskEnabled"/>.
    /// </summary>
    public bool ChunkMask = true;

    /// <summary>
    /// Draw even when another LOD mod is installed and switched on. The escape hatch for
    /// the mods whose own switch we cannot read; see <see cref="OtherLodMods"/>. Read at
    /// startup only, because turning the mod on mid-session is not something the startup
    /// path supports.
    /// </summary>
    public bool IgnoreOtherLodMods = false;

    /// <summary>
    /// The GPU render path switches, remembered per install.
    ///
    /// Saved rather than defaulted on. Shipping them on would trade a measured win for an
    /// unmeasured one: batching suspends the delayed occlusion queries, those were worth
    /// 170 to 500 FPS on a hill view in session 34, and depth culling does not yet pay for
    /// itself while cached terrain cannot occlude cached terrain. So the default stays off
    /// for everyone, and someone who has decided to run them does not have to say so again
    /// every session.
    ///
    /// All three default false, which is exactly what an install with no config file gets.
    /// </summary>
    public bool GpuArenas = false;
    public bool IndirectDraw = false;
    public bool DepthCull = false;

    /// <summary>
    /// Take the depth picture at the end of the frame and cull against it on the next one, so
    /// cached terrain can hide cached terrain. Saved alongside the others because it is the
    /// same kind of choice: off for everyone by default, remembered for whoever turns it on.
    /// </summary>
    public bool LateDepthPicture = false;
}

/// <summary>
/// Client entry point: wires the shared <see cref="LodPipeline"/> to client events and
/// owns everything the server has no equivalent of - the renderer, tint registry, chat
/// commands and telemetry. Chunk columns arrive from `ChunkDirty` and go straight to the
/// pipeline, which does the capture, mip and persistence work.
/// See DESIGN.md at the repo root.
/// </summary>
public class VintageHorizonsModSystem : ModSystem
{

    ICoreClientAPI capi = null!;
    LodPipeline pipeline = null!;
    LodTerrainRenderer renderer = null!;

    /// <summary>Block -> live tint slot; shared by capture, cache loads and the renderer.</summary>
    readonly LodTintRegistry tints = new();
    long tickListenerId;

    readonly BlockPos paletteSamplePos = new(0, 0, 0);

    // Dev auto-explore (unattended stress testing): teleport along an expanding
    // spiral so fresh chunks stream through the pipeline continuously.
    bool autoExplore;
    int exploreLeg;      // spiral leg counter
    int exploreStep;     // steps taken on current leg
    int exploreDirX = 1, exploreDirZ;
    double exploreX, exploreZ;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    /// <summary>Everything the config file holds, so a partial save cannot drop a setting.</summary>
    VintageHorizonsConfig config = new();
    VintageHorizonsConfigDialog? configDialog;

    /// <summary>Set when another LOD mod is drawing; we then stay out of its way.</summary>
    string? deferringTo;

    /// <summary>
    /// Optional server assist (DESIGN.md §10). Created even while deferring, because the
    /// channel has to be registered before the handshake either way; it simply never
    /// greets, so it stays silent.
    /// </summary>
    LodAssistClient? assist;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        allocationTelemetryEnabled =
            Environment.GetEnvironmentVariable("VINTAGEHORIZONS_STATS") == "1";

        try
        {
            config = capi.LoadModConfig<VintageHorizonsConfig>("vintagehorizons.json") ?? new VintageHorizonsConfig();
        }
        catch
        {
            config = new VintageHorizonsConfig();
        }

        // Before anything that can return early, and before the connection handshake:
        // the server learns we speak this channel at handshake time and never again. A
        // client that skips the registration makes the game log "Server sends me channel
        // name vintagehorizons, but no client side mod registered it" on every join,
        // which reads like a broken install. The channel stays inert until Greet(), and
        // only the active path reaches that.
        assist = new LodAssistClient(capi, Mod.Logger, Mod.Info.Version);
        assist.Register();

        // Two LOD mods both extend the camera's far plane and both draw terrain out
        // there, so they z-fight over the same ground. Defer rather than fight. What we
        // must not do is defer to a mod that is installed but switched off, which leaves
        // the player with nothing at all.
        deferringTo = ChooseDeferralTarget();
        if (deferringTo != null)
        {
            Mod.Logger.Notification(
                "'{0}' is installed and switched on, so VintageHorizons stays idle. Two LOD mods "
                + "draw over each other and fight for the camera far plane. Switch '{0}' off in its "
                + "own settings, then restart the game to use VintageHorizons instead. The mod "
                + "decides this once at startup: if you switch the other mod off now, nothing "
                + "changes until the next start.",
                deferringTo);
            RegisterCommands();

            // The idle path reaches no LevelFinalize handler of its own, so the dev hook
            // is wired straight to the event here. Without it the matrix cannot drive
            // '.vhdefer off' from the state that command exists for.
            capi.Event.LevelFinalize += RegisterAutoCommand;
            return;
        }

        LodWorld.SetLevelThresholds(LodWorld.NormalizeLevelThresholds(
            config.LodThresholds, config.DetailDistance));

        pipeline = new LodPipeline(capi, Mod.Logger, DescribePalette,
            block => (byte)TintSlotOf(block),
            block => block.EntityClass != null ? 0 : StableColorOf(block));
        pipeline.TrackPhaseAllocations = allocationTelemetryEnabled;

        // Repairs a cache written while a block lookup was poisoned, which saved sections
        // with no palette colour at all and drew them as black ground. Client-side only:
        // it needs the texture atlas, and a server stores 0 on purpose.
        pipeline.RepairUncoloredPalette = section => LodPaletteRepair.Fill(section, AtlasColorOf);
        pipeline.RecolorForeignSection = RecolorForeignSection;
        renderer = new LodTerrainRenderer(
            capi, pipeline.World, pipeline.Worker, tints, () => pipeline.WorldEpoch)
        {
            AutoUnpause = Environment.GetEnvironmentVariable("VINTAGEHORIZONS_AUTOUNPAUSE") == "1",
            TrackPhaseAllocations = allocationTelemetryEnabled,
            FarViewDistanceCap = config.FarViewDistanceCap,
            OpaqueBackfaceCulling =
                Environment.GetEnvironmentVariable("VINTAGEHORIZONS_BACKFACE_CULLING") != "0",
            OpaqueFrontToBack =
                Environment.GetEnvironmentVariable("VINTAGEHORIZONS_FRONT_TO_BACK") != "0",
            OcclusionCullingEnabled =
                Environment.GetEnvironmentVariable("VINTAGEHORIZONS_OCCLUSION_CULLING") != "0",
            SectionHeightCulling =
                Environment.GetEnvironmentVariable("VINTAGEHORIZONS_SECTION_HEIGHT_CULLING") != "0",
            // Off unless asked for, matching the command's default. Pinned by the harness
            // rather than typed in game, because its whole gate is a controlled A/B and a
            // comparison whose switches were set by hand is how session 40 lost a run.
            DepthPyramidEnabled =
                Environment.GetEnvironmentVariable("VINTAGEHORIZONS_DEPTH_PYRAMID") == "1",
        };

        // The saved setting applies unless the environment variable has taken a side, which
        // is how the benchmark harness pins one path for a controlled comparison. The
        // renderer has already resolved the override, so only an absent variable defers here.
        if (Environment.GetEnvironmentVariable("VINTAGEHORIZONS_CHUNK_MASK") == null)
            renderer.ChunkMaskEnabled = config.ChunkMask;

        // The GPU path someone chose last session, restored in the order the pieces depend
        // on each other: arenas hold the geometry batching draws from, batching provides the
        // commands culling zeroes, and culling needs a pyramid to test against. Restoring
        // them out of order would leave a switch on with nothing under it, which is exactly
        // the state that wasted a playtest.
        //
        // An environment variable still wins, so a benchmark run pins its own path rather
        // than inheriting whatever the last session happened to leave saved.
        if (config.GpuArenas && Environment.GetEnvironmentVariable("VINTAGEHORIZONS_GPU_ARENAS") == null)
        {
            renderer.RequestGpuShadow("on");

            if (config.IndirectDraw
                && Environment.GetEnvironmentVariable("VINTAGEHORIZONS_GPU_INDIRECT") == null)
            {
                renderer.IndirectDrawEnabled = true;

                if (config.DepthCull
                    && Environment.GetEnvironmentVariable("VINTAGEHORIZONS_DEPTH_PYRAMID") == null)
                {
                    renderer.DepthPyramidEnabled = true;
                    renderer.GpuCullEnabled = true;

                    // Last, and inside the cull branch: the second same-frame picture only
                    // means anything to something that reads it.
                    if (config.LateDepthPicture) renderer.LateDepthPyramid = true;
                }
            }
        }

        capi.Event.ChunkDirty += OnChunkDirty;
        capi.Event.LevelFinalize += OnLevelFinalize;
        capi.Event.LeaveWorld += OnLeaveWorld;

        tickListenerId = capi.Event.RegisterGameTickListener(OnGameTick, 50);

        RegisterCommands();

        Mod.Logger.Notification("VintageHorizons {0} loaded (client-only)", Mod.Info.Version);
    }

    /// <summary>
    /// A dev hook for the matrix tier: send one chat command shortly after the world is
    /// up. The harness cannot reach a detached server's console, and a second entry point
    /// into a command would test something other than what players type. The delay lets
    /// the handshake and the privilege grant settle first.
    ///
    /// Reachable from the idle path too, and not for symmetry: '.vhdefer off' is the
    /// escape hatch we tell an idle player to use, and it writes the config file from a
    /// state where the renderer does not exist. That is the one command whose failure
    /// would strand exactly the player it is meant to rescue.
    /// </summary>
    void RegisterAutoCommand()
    {
        if (Environment.GetEnvironmentVariable("VINTAGEHORIZONS_AUTOCMD") is not { Length: > 0 } autoCmd)
        {
            return;
        }

        capi.Event.RegisterCallback(_ =>
        {
            Mod.Logger.Notification("Auto-command: {0}", autoCmd);

            // SendChatMessage sends to the SERVER, so it runs '/' commands and silently
            // does nothing with a client-side '.' one: the message goes out, no handler
            // on either side claims it, and the log looks like a success. Client commands
            // have to be dispatched locally.
            if (autoCmd.StartsWith('.'))
            {
                capi.ChatCommands.ExecuteUnparsed(autoCmd, new TextCommandCallingArgs
                {
                    Caller = new Caller
                    {
                        Player = capi.World.Player,
                        Pos = capi.World.Player.Entity.Pos.XYZ,
                        FromChatGroupId = GlobalConstants.GeneralChatGroup,

                        // The game fills these in when a person types the command; a
                        // caller built here starts with none and is refused by its own
                        // privilege test. Wildcard, because this hook exists only to
                        // reproduce what a player does, and a test that cannot reach the
                        // command tests nothing.
                        CallerPrivileges = new[] { "*" },
                    },
                }, result => Mod.Logger.Notification("Auto-command result: {0}", result.StatusMessage));
                return;
            }

            capi.SendChatMessage(autoCmd);
        }, 15000);
    }

    /// <summary>
    /// The mod to stay idle for, or null to draw. Reports what it found either way: a
    /// player who sees no distant terrain needs the log to say which mod stopped us, and
    /// a player whose other mod is switched off needs to know we noticed.
    /// </summary>
    string? ChooseDeferralTarget()
    {
        (string? drawing, string[] switchedOff) =
            OtherLodMods.Inspect(capi.ModLoader.IsModEnabled, ReadOtherModSwitch);

        if (switchedOff.Length > 0)
        {
            Mod.Logger.Notification(
                "Installed but switched off in its own settings, so VintageHorizons is not "
                + "staying idle for it: {0}", string.Join(", ", switchedOff));
        }

        if (drawing != null && config.IgnoreOtherLodMods)
        {
            Mod.Logger.Warning(
                "'{0}' is switched on, and IgnoreOtherLodMods is set, so VintageHorizons is drawing "
                + "anyway. Switch '{0}' off in its own settings, or the two draw over the same ground.",
                drawing);
            return null;
        }

        return drawing;
    }

    /// <summary>Reads another mod's own switch. Null means "could not tell", which counts as on.</summary>
    bool? ReadOtherModSwitch(string file)
    {
        try
        {
            return capi.LoadModConfig<OtherLodModSwitch>(file)?.Enabled;
        }
        catch (Exception e)
        {
            // Another mod's config file is not ours to repair, and guessing "off" would
            // put us on the same ground as a mod that is still drawing.
            Mod.Logger.Warning("Could not read '{0}' to tell whether that mod is switched on: {1}",
                file, e.Message);
            return null;
        }
    }

    void OnChunkDirty(Vec3i chunkCoord, IWorldChunk chunk, EnumChunkDirtyReason reason)
    {
        pipeline.QueueColumn(chunkCoord.X, chunkCoord.Z);
    }

    void OnGameTick(float dt)
    {
        if (!pipeline.Active) return;

        LodPhaseStart tickStart = LodPhaseCost.Start(allocationTelemetryEnabled);
        ReportFillIn();

        LodPhaseStart phaseStart = LodPhaseCost.Start(allocationTelemetryEnabled);
        PumpServerAssist();
        assistTickCost.Add(phaseStart);

        phaseStart = LodPhaseCost.Start(allocationTelemetryEnabled);
        PumpLocalOffers();
        localOfferTickCost.Add(phaseStart);

        phaseStart = LodPhaseCost.Start(allocationTelemetryEnabled);
        pipeline.Tick();
        pipelineTickCost.Add(phaseStart);

        phaseStart = LodPhaseCost.Start(allocationTelemetryEnabled);
        var pos = capi.World.Player.Entity.Pos;
        if (pipeline.MaybeEvictAround(pos.X, pos.Z))
        {
            LodWorld world = pipeline.World;
            Mod.Logger.Notification("Evict sweep at {0},{1}: checked {2}, pinned {3}, cold {4}, total evicted {5}",
                (int)pos.X, (int)pos.Z, world.LastSweepChecked, world.LastSweepPinned,
                world.LastSweepCold, world.EvictedSectionsTotal);
        }
        residentEvictTickCost.Add(phaseStart);
        totalTickCost.Add(tickStart);
    }

    /// <summary>
    /// Adopt whatever the server sent, then ask for what the render path now wants.
    /// Both on the game tick, because both mutate the LodWorld.
    /// </summary>
    void PumpServerAssist()
    {
        // Structural decode finishes in pipeline.Tick. Release the network transport's
        // retained in-flight slot only after that owning-thread publication decision.
        while (pipeline.TryTakeForeignInstallCompletion(out LodForeignInstallCompletion done))
        {
            if (done.Source == LodForeignSource.ServerAssist)
                assist?.CompleteInstall(done.Key, done.Installed);
            else if (done.Source == LodForeignSource.LocalOffer && done.Installed)
            {
                localOfferSectionsInstalled++;
                PublishTestLocalOfferInstall(done.Key);
            }
        }

        if (assist == null || !assist.Available) return;

        int before = pipeline.RemoteOnly.Count;
        assist.PumpAsync(
            (key, blob) => pipeline.QueueForeignBlob(
                key, blob, LodForeignSource.ServerAssist) == LodForeignQueueOutcome.Queued,
            // Manifest keys become quadtree-visible here rather than in the packet
            // handler: HasDataSet belongs to this thread. Pump publishes each chunk's
            // newly accepted keys once, so the complete retained manifest is never
            // re-enumerated on ordinary ticks.
            offered => pipeline.AddRemoteKeys(offered),
            // Every failed transfer ends in one explicit state. "Not written yet"
            // releases the attempt and restores wanted; refusal, parse failure and a
            // local-win race remove the obsolete remote route.
            (key, retryable) =>
            {
                if (retryable) pipeline.MarkRemoteRetryable(key);
                else pipeline.MarkRemoteUnavailable(key);
            });

        // Nearest first. The render path asks for exactly the sections it wants, but far
        // more than the in-flight cap allows at once, and an unordered set hands the
        // network whatever hashes first - so distant terrain arrived while ground in front
        // of the player stayed at its coarse parent, which is what the no-holes rule draws
        // until all four children land. Sorting here rather than in the pipeline keeps the
        // pipeline free of any notion of where the viewer is.
        long[] wanted = pipeline.RemoteWanted();
        if (wanted.Length > 1)
        {
            var at = capi.World.Player.Entity.Pos;
            double px = at.X, pz = at.Z;
            Array.Sort(wanted, (a, b) =>
                LodWorld.NearestDistanceSqTo(a, px, pz).CompareTo(LodWorld.NearestDistanceSqTo(b, px, pz)));
        }
        pipeline.MarkRemoteRequested(assist.Request(wanted));

        if (pipeline.RemoteOnly.Count != before && !loggedRemoteKeys)
        {
            loggedRemoteKeys = true;
            Mod.Logger.Notification(
                "Server assist: {0} sections offered that this client never captured. "
                + "The mod fetches them as the view needs them.", pipeline.RemoteOnly.Count);
        }
    }

    bool loggedRemoteKeys;

    /// <summary>
    /// The server side's cache for this same singleplayer world, when it has swept one.
    /// Null on a dedicated server (the network assist covers that) and on any world that
    /// has never swept.
    /// </summary>
    LodLocalOfferSource? localOffers;
    bool loggedLocalOffers;
    int localOfferProbeTicks;
    int localOfferKeysDiscovered;
    int localOfferRetryableMisses;
    int localOfferSectionsAccepted;
    int localOfferSectionsInstalled;
    readonly string? testLocalOfferMissMarker =
        Environment.GetEnvironmentVariable("VINTAGEHORIZONS_TEST_LOCAL_OFFER_MISS_MARKER");
    readonly string? testLocalOfferInstallMarker =
        Environment.GetEnvironmentVariable("VINTAGEHORIZONS_TEST_LOCAL_OFFER_INSTALL_MARKER");

    /// <summary>
    /// About 5s at the 50ms tick. The retry usually answers with one failed File.Exists,
    /// so it stays cheap; anything shorter is pointless for a cache that grows for
    /// minutes once it appears.
    /// </summary>
    const int LocalOfferProbeIntervalTicks = 100;

    /// <summary>
    /// Adopt sections the server side swept out of the savegame.
    ///
    /// Identical in shape to PumpServerAssist and deliberately so - same remote-key
    /// bookkeeping, same recolour on install, same nearest-first ordering. Only the
    /// transport differs. Reads have a small in-flight cap and publication has its own
    /// budget so a sweep of ten thousand sections cannot turn into either unbounded RAM
    /// or a main-thread installation burst.
    /// </summary>
    void PumpLocalOffers()
    {
        if (localOffers == null)
        {
            // A null here means "not yet", not "never": the server side can open its
            // cache long after this client finalized - /vhgen creates one on demand,
            // and a sweep's file can appear after a slow start. Opening once at
            // LevelFinalize and never looking again left both invisible for the whole
            // session.
            if (++localOfferProbeTicks < LocalOfferProbeIntervalTicks) return;
            localOfferProbeTicks = 0;
            if (pipeline.DbPath is not string dbPath) return;
            localOffers = LodLocalOfferSource.TryOpen(dbPath, Mod.Logger);
            if (localOffers == null) return;
        }

        // A dedicated read-only connection enumerates the sibling cache on its worker.
        // It publishes only new keys, in bounded immutable batches; this thread merely
        // registers at most one such batch per tick in the live LodWorld.
        if (localOffers.TryTakeDiscoveredKeys(out long[] offered))
        {
            pipeline.AddRemoteKeys(offered);
            localOfferKeysDiscovered += offered.Length;
            if (!loggedLocalOffers)
            {
                loggedLocalOffers = true;
                // Sweeps and /vhgen both fill the sibling cache; this line covers either.
                Mod.Logger.Notification(
                    "Server-side cache offers sections locally. The mod adopts them as "
                    + "the view needs them.");
            }
        }

        var installBudget = new LodDrainBudget();
        int completed = 0;
        while (completed < LocalOffersPerTick
            && localOffers.TryPeekBlobResult(out LodLocalOfferSource.BlobResult waiting)
            && installBudget.TryStart(waiting.Blob?.LongLength ?? 0))
        {
            // Keep sibling-cache work within its own bounded decoder allowance. Server
            // assist has a separate reservation, so local adoption cannot crowd it out.
            if (waiting.Blob is { Length: > 0 }
                && !pipeline.CanQueueForeignBlob(LodForeignSource.LocalOffer)) break;
            if (!localOffers.TryTakeBlobResult(out LodLocalOfferSource.BlobResult result)) break;
            completed++;

            // A miss is ordinary while the sweep is still running: the key was listed but
            // its row is not written yet. MarkRemoteUnavailable is permanent, so it must
            // not be used for "not yet". It also must not enter the taken batch: no source
            // accepted responsibility, so forgetting it here would strand the key in
            // LodWorld.LoadsInFlight and make a later row permanently invisible.
            if (result.Blob == null || result.Blob.Length == 0)
            {
                localOfferRetryableMisses++;
                pipeline.CompleteLocalOffer(result.Key, LodLocalOfferOutcome.RetryableMiss);
                continue;
            }

            LodForeignQueueOutcome queued = pipeline.QueueForeignBlob(
                result.Key, result.Blob, LodForeignSource.LocalOffer);
            if (queued == LodForeignQueueOutcome.Queued)
            {
                localOfferSectionsAccepted++;
                pipeline.MarkLocalOfferAccepted(result.Key);
            }
            else if (queued == LodForeignQueueOutcome.Unavailable)
            {
                // Local data already won before submission, or persistence is gone.
                pipeline.CompleteLocalOffer(result.Key, LodLocalOfferOutcome.Unavailable);
            }
            else break;
        }

        localOfferItemsProcessed += installBudget.Items;
        localOfferBytesProcessed += installBudget.Bytes;

        long[] wanted = pipeline.RemoteWanted();
        if (wanted.Length == 0) return;

        if (wanted.Length > 1)
        {
            var at = capi.World.Player.Entity.Pos;
            double px = at.X, pz = at.Z;
            Array.Sort(wanted, (a, b) =>
                LodWorld.NearestDistanceSqTo(a, px, pz).CompareTo(LodWorld.NearestDistanceSqTo(b, px, pz)));
        }

        int requests = 0;
        for (int i = 0; i < wanted.Length && requests < LocalOffersPerTick
            && localOffers.CanRequestBlob; i++)
            if (localOffers.RequestBlob(wanted[i])) requests++;
    }

    void PublishTestLocalOfferInstall(long key)
    {
        if (string.IsNullOrEmpty(testLocalOfferMissMarker)
            || string.IsNullOrEmpty(testLocalOfferInstallMarker)
            || !File.Exists(testLocalOfferMissMarker)) return;

        string described = LodLocalOfferSource.DescribeKey(key);
        if (File.ReadAllText(testLocalOfferMissMarker).Trim() == described
            && !File.Exists(testLocalOfferInstallMarker))
        {
            File.WriteAllText(testLocalOfferInstallMarker, described);
        }
    }

    /// <summary>
    /// Compressed sibling-cache blobs transferred from the I/O reader to the decoder per
    /// tick. Both SQLite and structural parsing run on workers; owning-thread publication
    /// has its own elapsed-time and decoded-byte budget in LodPipeline.
    /// </summary>
    const int LocalOffersPerTick = 4;
    int localOfferItemsProcessed;
    long localOfferBytesProcessed;

    /// <summary>
    /// Fill in palette colours for a section captured by a server, which had no texture
    /// atlas and stored 0 for every one of them (DESIGN.md §10.4). Block ids are already
    /// resolved from codes by the deserializer, so this only needs the atlas.
    /// </summary>
    void RecolorForeignSection(LodSection section)
    {
        for (int i = 0; i < section.Palette.Count; i++)
        {
            LodPaletteEntry entry = section.Palette[i];

            // A code the block registry could not answer for. It still carries the flags
            // the capturing side worked out, so it is still drawn as terrain - and a
            // server stores 0 for every colour, so leaving this one alone drew it at
            // exactly RGB 0,0,0. Black ground, correctly shaped, with nothing anywhere
            // saying why. The shader cannot save it either: shade bottoms out at 0.55 and
            // daylight is clamped to 0.02, but zero times anything is still zero.
            //
            // Neutral grey instead, and counted. Wrong-but-plausible stone beats a hole
            // in the world, and the count is what turns the next report into a log line.
            if (entry.BlockId <= 0)
            {
                entry.Color = LodPaletteRepair.UnknownBlockColor;
                section.Palette[i] = entry;
                uncoloredForeignEntries++;
                continue;
            }

            // The same colour a locally captured section would have stored, so foreign
            // and local ground meet without a seam. A chiselled block has no colour here
            // at all - its materials live in a block entity this path has no position to
            // read, the section having come from a server or a peek - so it takes the
            // same neutral grey an unidentifiable block gets.
            entry.Color = StableColorOf(capi.World.Blocks[entry.BlockId]);
            section.Palette[i] = entry;
        }
    }

    /// <summary>
    /// The client half of palette registration: the untinted colour a block is drawn as
    /// at distance, plus which live tint applies. Stored untinted on purpose, so the
    /// shader can follow the calendar instead of freezing the season it was captured in.
    /// A server has no atlas and cannot answer this at all (DESIGN.md §10.4).
    ///
    /// The exact block position is used ONLY for blocks whose colour genuinely depends on
    /// it, which are the ones carrying a block entity: a chiselled block averages the
    /// materials in its entity there, the same way the world map colours it, and a
    /// synthetic chunk-centre position made that lookup miss. Every other block takes the
    /// section-independent colour instead - see StableColorOf for why that matters.
    /// </summary>
    (int Color, byte TintSlot) DescribePalette(int blockId, int blockX, int blockY, int blockZ)
    {
        Block block = capi.World.Blocks[blockId];

        int color;
        if (block.EntityClass != null)
        {
            paletteSamplePos.Set(blockX, blockY, blockZ);
            int sampled = block.GetColorWithoutTint(capi, paletteSamplePos);

            // Keeping the sampled colour as the last resort, not grey: a chiselled block
            // is exactly the case where the block's own texture is the placeholder and
            // the entity already answered with the materials in it.
            color = RepairPlaceholder(block, sampled, fallback: sampled);
        }
        else
        {
            color = StableColorOf(block);
        }

        return (color, (byte)TintSlotOf(block));
    }

    /// <summary>
    /// One colour per block, identical in every section that ever contains that block.
    ///
    /// This has to be computed rather than simply asked for, because
    /// <c>Block.GetColorWithoutTint</c> is not a function of the block. Grass-covered
    /// ground answers it with `BlockTextureAtlas.GetRandomColor`, which returns one of
    /// thirty pixels sampled out of the grass texture at random, and a palette entry is
    /// registered once per section. So one 64-block section drew its whole surface with
    /// one random pixel and the section beside it drew its whole surface with another:
    /// measured at 38 different stored colours for `soil-low-normal` alone across 1,041
    /// cached sections, scattered with no relation to terrain, climate or height. On the
    /// ground that reads as flat green and flat brown tiles meeting at a hard edge, which
    /// is what it looked like.
    ///
    /// Averaging many draws collapses that to the texture's own mean, which is the colour
    /// the block should read as from far away, and caching it by block id makes every
    /// section agree by construction - no blending across section edges required, because
    /// there is no longer a step to blend. A block that answers deterministically averages
    /// to exactly what it already returned, so nothing else changes.
    ///
    /// The probe position is the sky above the world origin, never the block's own
    /// position, for the same reason: the base implementation hands the question to
    /// whatever DECOR sits on the up face, so sampling real ground would let one snowy or
    /// mossy sample decide the colour of that block everywhere. Nothing sits in the sky,
    /// and blocks that need their real position never reach this path.
    /// </summary>
    int StableColorOf(Block block)
    {
        if (stableColorByBlockId.TryGetValue(block.BlockId, out int cached)) return cached;

        // Grass-covered ground is composited, not painted, and the engine's own answer for
        // it is both incomplete and byte-swapped. Build it from the atlas instead.
        if (TryTopSoilColor(block, out int composite, out _))
        {
            stableColorByBlockId[block.BlockId] = composite;
            return composite;
        }

        colorProbePos.Set(0, capi.World.BlockAccessor.MapSizeY - 1, 0);
        int color = block.GetColorWithoutTint(capi, colorProbePos);

        // A block with no colour texture answers -1 (white) every time; averaging that
        // is meaningless and the placeholder repair below is the real answer for it.
        if (color >= 0)
        {
            long r = 0, g = 0, b = 0;
            for (int i = 0; i < StableColorSamples; i++)
            {
                int sample = block.GetColorWithoutTint(capi, colorProbePos);
                r += sample & 0xFF;
                g += (sample >> 8) & 0xFF;
                b += (sample >> 16) & 0xFF;
            }
            color = unchecked((int)0xFF000000)
                | (int)(b / StableColorSamples) << 16
                | (int)(g / StableColorSamples) << 8
                | (int)(r / StableColorSamples);
        }

        color = RepairPlaceholder(block, color, fallback: LodPaletteRepair.UnknownBlockColor);
        stableColorByBlockId[block.BlockId] = color;
        return color;
    }

    /// <summary>
    /// Thirty is how many random pixels the atlas actually holds per texture, so this
    /// samples each of them about twice. Paid once per block id per session.
    /// </summary>
    const int StableColorSamples = 64;

    readonly Dictionary<int, int> stableColorByBlockId = new();

    /// <summary>Sky above the world origin: guaranteed to carry no decor. See StableColorOf.</summary>
    readonly BlockPos colorProbePos = new(0, 0, 0);

    /// <summary>Tint slot for a block, with the untinted share its colour carries.</summary>
    int TintSlotOf(Block block) =>
        tints.SlotFor(block, TryTopSoilColor(block, out _, out LodUntintedShare share)
            ? share
            : LodUntintedShare.None);

    /// <summary>
    /// Reproduce vanilla's top-soil compositing for a block that has one, from the atlas.
    ///
    /// `chunktopsoil.fsh` draws these as
    /// <c>brownSoil * (1 - grass.a) + grass * grass.a</c>, where the grass overlay is the
    /// block's `specialSecondTexture` and only the overlay is colour-mapped. Two things
    /// follow, and the mod had both of them wrong.
    ///
    /// The bare dirt showing through is a THIRD of the face at full coverage and more at
    /// the sparse ones, and it is never tinted. Dropping it is what made distant ground
    /// read as flat green where vanilla reads olive, and it removed essentially all of the
    /// blue: the seasonal tint's blue channel is near zero, so anything multiplied by it
    /// loses its blue entirely, and in vanilla the dirt is not multiplied by it.
    ///
    /// And the engine's own `GetColorWithoutTint` cannot be used for these blocks at all -
    /// see G48. `BlockWithGrassOverlay` answers with `GetRandomColor`, whose values are raw
    /// `ToArgb` with red at bits 16-23, while `GetAverageColor` is byte-reversed with red
    /// in the low byte. The mod reads one convention, so every grass-covered block came out
    /// with red and blue exchanged. Both texture reads here go through `GetAverageColor`,
    /// so both are in the same order as everything else the mod stores.
    /// </summary>
    bool TryTopSoilColor(Block block, out int composite, out LodUntintedShare share)
    {
        composite = 0;
        share = LodUntintedShare.None;

        if (block.RenderPass != EnumChunkRenderPass.TopSoil) return false;
        if (block.Textures == null
            || !block.Textures.TryGetValue("specialSecondTexture", out CompositeTexture? overlay)) return false;

        int overlayId = overlay?.Baked?.TextureSubId ?? -1;
        if (!IsUsableAtlasTexture(overlayId) || !IsUsableAtlasTexture(block.TextureSubIdForBlockColor)) return false;

        TextureMean soil = MeanOf(TextureFor(block, block.TextureSubIdForBlockColor),
            block.TextureSubIdForBlockColor);
        TextureMean grass = MeanOf(overlay, overlayId);
        if (grass.Coverage <= 0f) return false;

        float a = grass.Coverage;
        composite = unchecked((int)0xFF000000)
            | Channel(soil.B, grass.B, a) << 16 | Channel(soil.G, grass.G, a) << 8 | Channel(soil.R, grass.R, a);
        share = new LodUntintedShare(
            LodTopSoil.UntintedShare(soil.R, grass.R, a),
            LodTopSoil.UntintedShare(soil.G, grass.G, a),
            LodTopSoil.UntintedShare(soil.B, grass.B, a));
        return true;
    }

    static int Channel(float soil, float grass, float a) =>
        Math.Clamp((int)(LodTopSoil.Composite(soil, grass, a) + 0.5f), 0, 255);

    /// <summary>The block's own texture behind a given atlas sub-id, if it has one.</summary>
    static CompositeTexture? TextureFor(Block block, int subId)
    {
        if (block.Textures == null) return null;
        foreach (CompositeTexture tex in block.Textures.Values)
        {
            if ((tex?.Baked?.TextureSubId ?? -1) == subId) return tex;
        }
        return null;
    }

    /// <summary>
    /// A texture's colour and coverage, over every pixel of it.
    ///
    /// <c>GetAverageColor</c> is not an average: the engine samples exactly four pixels, at
    /// 35% and 65% of each axis, and calls that the texture's colour. For an opaque texture
    /// that is close enough. For a partly transparent OVERLAY it is not, because those same
    /// four pixels also decide how much of the block below shows through - measured at 146
    /// against a true 175 for full grass coverage, a fifth too much bare dirt, which was the
    /// whole of the residual error left after 0.3.20 composited the two layers at all.
    ///
    /// So read the texture. The colour is alpha-weighted, because a pixel that is barely
    /// there should barely count, and the pair (alpha-weighted colour, mean alpha) is exactly
    /// what averaging vanilla's per-pixel blend over the whole face reduces to.
    /// </summary>
    readonly record struct TextureMean(float R, float G, float B, float Coverage);

    readonly Dictionary<AssetLocation, TextureMean> textureMeans = new();

    TextureMean MeanOf(CompositeTexture? tex, int atlasSubId)
    {
        AssetLocation? loc = tex?.Base?.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png");
        if (loc != null && textureMeans.TryGetValue(loc, out TextureMean cached)) return cached;

        TextureMean mean = loc != null && TryReadTextureMean(loc, out TextureMean read)
            ? read
            : AtlasMean(atlasSubId);

        if (loc != null) textureMeans[loc] = mean;
        return mean;
    }

    /// <summary>The four-pixel atlas answer, for a texture whose own file cannot be read.</summary>
    TextureMean AtlasMean(int subId)
    {
        int c = capi.BlockTextureAtlas.GetAverageColor(subId);
        return new TextureMean(c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF, ((c >> 24) & 0xFF) / 255f);
    }

    bool TryReadTextureMean(AssetLocation loc, out TextureMean mean)
    {
        mean = default;
        try
        {
            IAsset? asset = capi.Assets.TryGet(loc);
            if (asset?.Data == null) return false;

            using BitmapExternal bmp = capi.Render.BitmapCreateFromPng(asset.Data);

            // Read through Pixels, not GetPixel: the latter returns an SKColor, which would
            // put SkiaSharp on the mod's reference list for no gain. Both are 0xAARRGGBB -
            // red at bits 16-23, the order the atlas reads pixels in before it reverses the
            // bytes for AvgColor and does not for RndColors (G48). The caller puts red back
            // in the low byte. The decoder asks for unpremultiplied alpha, so weighting the
            // colour by it below is a weighting and not a second application of it.
            int[] argb = bmp.Pixels;
            if (argb == null || argb.Length == 0) return false;
            long pixels = argb.Length;

            double r = 0, g = 0, b = 0, alpha = 0;
            foreach (int p in argb)
            {
                double aPixel = ((p >> 24) & 0xFF) / 255.0;
                r += ((p >> 16) & 0xFF) * aPixel;
                g += ((p >> 8) & 0xFF) * aPixel;
                b += (p & 0xFF) * aPixel;
                alpha += aPixel;
            }

            if (alpha <= 0) return false;

            mean = new TextureMean((float)(r / alpha), (float)(g / alpha), (float)(b / alpha),
                (float)(alpha / pixels));
            return true;
        }
        catch (Exception e)
        {
            Mod.Logger.Notification(
                "Could not read texture '{0}' for its true average; using the atlas estimate instead. {1}",
                loc, e.Message);
            return false;
        }
    }

    /// <summary>
    /// A colour texture that resolved to the unknown.png placeholder is not a colour, it
    /// is the absence of one; so is a block-colour texture the atlas never assigned. Both
    /// fall back to another of the block's own textures, and to the caller's stand-in if
    /// the block has none - wrong-but-plausible beats a placeholder or a hole in the world.
    /// </summary>
    int RepairPlaceholder(Block block, int color, int fallback)
    {
        if (!IsUsableAtlasTexture(block.TextureSubIdForBlockColor)
            // Guarded on non-zero: if the atlas never populated AvgColor we would be
            // comparing against 0 and "fixing" every legitimately black block.
            || (unknownTextureColor != 0 && color == unknownTextureColor))
        {
            color = ColorFromAnyTexture(block, fallback);
        }
        return color;
    }

    /// <summary>Average colour of unknown.png (near-white, not magenta - measured).</summary>
    int unknownTextureColor;

    /// <summary>Foreign palette entries drawn as grey because no block matched.</summary>
    int uncoloredForeignEntries;

    /// <summary>
    /// Colour for one block id, for repairing a cache saved without any. Separate from
    /// DescribePalette because that one also answers the tint slot and needs a world
    /// position; here the block is all there is to go on.
    /// </summary>
    public int AtlasColorOf(int blockId)
    {
        if (blockId <= 0) return LodPaletteRepair.UnknownBlockColor;

        // Deliberately the same answer capture gives, so a repaired entry cannot become
        // a colour step against the section next to it. The repair has only a block id
        // to go on, so a chiselled block repairs to neutral grey here and gets its real
        // materials on the next capture.
        return StableColorOf(capi.World.Blocks[blockId]);
    }

    /// <summary>
    /// Whether an atlas sub-id actually names a texture. `GetAverageColor` on an unassigned
    /// or out-of-range sub-id reads whatever the atlas holds there, which is where a
    /// nonsense LOD colour comes from - and unlike the unknown.png case, that does not
    /// require knowing what the placeholder looks like to detect.
    /// </summary>
    bool IsUsableAtlasTexture(int subId)
    {
        if (subId < 0) return false;

        TextureAtlasPosition[] positions = capi.BlockTextureAtlas.Positions;
        return subId < positions.Length && positions[subId] != null;
    }

    readonly Dictionary<int, int> missingTextureColorFallback = new();
    int missingTextureBlocks;
    bool loggedMissingTexture;

    /// <summary>
    /// Salvage a colour for a block whose block-colour texture did not resolve, so it draws
    /// as itself instead of as a placeholder or as whatever the atlas holds at a bogus id.
    ///
    /// Vanilla picks that texture in Block.LoadTextureSubIdForBlockColor: the
    /// 'textureCodeForBlockColor' attribute, else "up", else `Textures.First()` - and that
    /// last step ends in `?? 0`, so a block whose first texture in dictionary order has no
    /// Baked entry silently resolves to atlas subid 0, which is unknown.png. The block's
    /// other faces are baked fine, which is why it looks correct up close and magenta only
    /// in LOD. Measured firing on vanilla 'fruitingbush-wild-blackberry-free', so this is
    /// not a modded-content problem -- content-heavy block packs just hit it more often.
    ///
    /// So: use any of the block's own baked textures instead of the first one. Cached per
    /// block id - the answer cannot change within a session, and a palette entry is
    /// registered once per section, which is thousands of times per world.
    /// </summary>
    int ColorFromAnyTexture(Block block, int fallback)
    {
        // The cache holds only the block's own answer, with 0 for "no usable texture"
        // (no real block averages to 0, the same invariant colour-0 repair rests on).
        // The fallback is the caller's and is applied per call: the capture path passes
        // the probe colour, the foreign path passes grey, and caching whichever caller
        // came first hands one caller's stand-in to the others.
        if (missingTextureColorFallback.TryGetValue(block.BlockId, out int cached))
        {
            return cached != 0 ? cached : fallback;
        }

        int found = 0;
        if (block.Textures != null)
        {
            foreach (CompositeTexture tex in block.Textures.Values)
            {
                int subId = tex?.Baked?.TextureSubId ?? -1;
                if (!IsUsableAtlasTexture(subId)) continue;

                int candidate = capi.BlockTextureAtlas.GetAverageColor(subId);
                if (unknownTextureColor != 0 && candidate == unknownTextureColor) continue;

                found = candidate;
                break;
            }
        }

        missingTextureColorFallback[block.BlockId] = found;
        missingTextureBlocks++;
        if (!loggedMissingTexture)
        {
            loggedMissingTexture = true;
            Mod.Logger.Notification(
                "Block '{0}' has no usable block-colour texture (vanilla resolved it to unknown.png). "
                + "The mod uses another of its own textures instead, so it does not render wrong at distance.",
                block.Code);
        }
        return found != 0 ? found : fallback;
    }

    /// <summary>
    /// Find a block using the standard plant tint, so plants that declare no colour map
    /// (ferns) can borrow it instead of rendering as their greyscale texture.
    /// </summary>
    void ResolvePlantTintFallback()
    {
        foreach (Block block in capi.World.Blocks)
        {
            if (block?.Code == null) continue;
            if (block.SeasonColorMapResolved != null) continue;
            if (block.ClimateColorMapResolved == null) continue;
            if (block.ClimateColorMap != "climatePlantTint") continue;

            tints.PlantTintFallback = block;
            return;
        }
    }

    readonly System.Diagnostics.Stopwatch joinClock = new();
    static readonly int[] FillInMilestones = { 1, 100, 300, 600, 1200 };
    int nextMilestone;
    bool joinStallReported;

    /// <summary>
    /// A join is only ever seen from the outside as "the horizon took a while". Six joins
    /// on record land between 2.3 and 9.1 seconds to the first hundred meshes; one landed
    /// at 60.2, with no meshes at all after thirty seconds and every queue in the mod
    /// empty. Nothing in the log says which stage was not running, so the difference
    /// between "slow" and "not started" cannot be told apart afterwards.
    ///
    /// This reports the bootstrap state once, if the first mesh has not appeared within
    /// ten seconds. The renderer skips its selection walk entirely until a mesh exists,
    /// and the walk is what asks for sections, so the first mesh has to come from the
    /// dirty set instead - which makes "how many sections were dirty, how many loads were
    /// asked for, how many frames were skipped" the three numbers that name the stall.
    /// </summary>
    const double JoinStallSeconds = 10;

    void ReportFillIn()
    {
        while (nextMilestone < FillInMilestones.Length && renderer.MeshCount >= FillInMilestones[nextMilestone])
        {
            Mod.Logger.Notification(
                nextMilestone == 0 ? "Fill-in: first mesh after {1:0.0}s" : "Fill-in: {0} meshes after {1:0.0}s",
                FillInMilestones[nextMilestone], joinClock.Elapsed.TotalSeconds);
            nextMilestone++;
        }

        if (joinStallReported || renderer.MeshCount > 0) return;
        if (joinClock.Elapsed.TotalSeconds < JoinStallSeconds) return;

        joinStallReported = true;
        Mod.Logger.Notification(
            "Join: no cached terrain built after {0:0.0}s. {1} sections known, {2} resident, "
            + "{3} render-dirty, {4} loads in flight, {5} columns captured ({6} pending), "
            + "{7} mesh jobs queued, {8} render frames skipped for want of a mesh. "
            + "Radial demand: {9}.",
            joinClock.Elapsed.TotalSeconds,
            pipeline.CachedSectionsLoaded,
            pipeline.World.Sections.Count,
            pipeline.World.RenderDirty.Count,
            pipeline.World.LoadsInFlight.Count,
            pipeline.ColumnsCaptured,
            pipeline.PendingColumns,
            pipeline.Worker.PendingMeshes,
            renderer.FramesWithoutMeshes,
            renderer.DescribeRadialDemand());
    }

    void OnLevelFinalize()
    {
        ResolvePlantTintFallback();
        // Read once here rather than per palette entry: the atlas exists by now, and a
        // reload would change the position object but not what magenta looks like.
        unknownTextureColor = capi.BlockTextureAtlas.UnknownTexturePosition.AvgColor;
        Mod.Logger.Debug("Missing-texture colour is {0:X8}{1}", unknownTextureColor,
            unknownTextureColor == 0 ? " (zero: magenta-block salvage disabled)" : "");
        renderer.ApplyZFar();
        pipeline.Open("ModData/vintagehorizons");
        joinClock.Restart();
        joinStallReported = false;
        nextMilestone = 0;

        // A singleplayer world whose server side has swept the savegame leaves its results
        // in a sibling cache. Nothing to open on a dedicated server, where the same
        // sections arrive over the network instead.
        if (pipeline.DbPath is string dbPath)
        {
            localOffers = LodLocalOfferSource.TryOpen(dbPath, Mod.Logger);
        }

        // Last, and after the pipeline is live. An exception in a LevelFinalize handler
        // skips everything the handler has left to do, so an optional extra must not sit
        // upstream of the mod's actual job -- it did, and it broke exactly the
        // vanilla-server case it exists to stay out of the way of.
        assist?.Greet();

        Mod.Logger.Notification(
            "Level finalized. LOD capture active (render distance: unlimited, {0} sections from cache{1}).",
            pipeline.CachedSectionsLoaded, renderer.AutoUnpause ? ", auto-unpause on" : "");

        capi.Event.RegisterCallback(_ => LogStats("Stats after 30s"), 30000);

        // Continuous telemetry. Was tied to AutoUnpause, which meant an *attended* session
        // - the only kind where someone can say "this looks wrong" - was the one case with
        // no ongoing numbers to explain it. Its own switch now, so watching and driving are
        // independent.
        if (allocationTelemetryEnabled)
        {
            capi.Event.RegisterGameTickListener(_ => LogStats("Stats"), 15000);
        }

        RegisterAutoCommand();

        autoExplore = Environment.GetEnvironmentVariable("VINTAGEHORIZONS_AUTOEXPLORE") == "1";

        // Creative for any unattended run, not only an exploring one.
        //
        // A survival player is moved by things the test did not ask for: knockback,
        // drowning, hunger, a mob, and at worst a death that respawns them somewhere
        // else entirely. Player position is an INPUT to most of what these scenarios
        // measure - which chunks stream in, where /vhgen centres, and which positions
        // the absence verifier excludes as explainable by a nearby player. A scenario
        // whose subject wanders is measuring the wander.
        //
        // Sent before the auto-command below fires, so a /vhgen run is already centred
        // on a player who will stay put.
        if (autoExplore || Environment.GetEnvironmentVariable("VINTAGEHORIZONS_CREATIVE") == "1")
        {
            capi.Event.RegisterCallback(_ =>
            {
                Mod.Logger.Notification("Test run: entering creative so the player cannot be moved");
                capi.SendChatMessage("/gamemode creative");
            }, 10000);
        }

        if (autoExplore)
        {
            exploreX = capi.World.Player.Entity.Pos.X;
            exploreZ = capi.World.Player.Entity.Pos.Z;
            capi.Event.RegisterGameTickListener(_ => ExploreHop(), 60000);
            Mod.Logger.Notification("Auto-explore active (spiral teleports every 60s)");
        }
    }

    static readonly int ExploreHopBlocks =
        int.TryParse(Environment.GetEnvironmentVariable("VINTAGEHORIZONS_EXPLORE_HOP"), out int h) && h > 0 ? h : 350;

    void ExploreHop()
    {
        int hop = ExploreHopBlocks;

        exploreX += exploreDirX * hop;
        exploreZ += exploreDirZ * hop;

        // Square spiral: legs lengthen every second turn.
        if (++exploreStep >= exploreLeg / 2 + 1)
        {
            exploreStep = 0;
            exploreLeg++;
            (exploreDirX, exploreDirZ) = (-exploreDirZ, exploreDirX);
        }

        int y = capi.World.SeaLevel + 140;
        capi.SendChatMessage($"/tp ={(int)exploreX} {y} ={(int)exploreZ}");
    }

    bool loggedFirstCaptureError, loggedFirstMeshError, loggedFirstMipError, loggedFirstSaveError;
    bool allocationTelemetryEnabled;
    int gen0AtLastReport, gen1AtLastReport, gen2AtLastReport;
    LodPhaseCost totalTickCost, assistTickCost, localOfferTickCost, pipelineTickCost,
        residentEvictTickCost;

    void LogStats(string prefix)
    {
        LodWorld world = pipeline.World;
        LodWorker worker = pipeline.Worker;
        LodStorageThread? storageThread = pipeline.StorageThread;

        if (!loggedFirstCaptureError && worker.FirstCaptureError != null)
        {
            loggedFirstCaptureError = true;
            Mod.Logger.Warning("First capture error was: {0}", worker.FirstCaptureError);
        }
        if (!loggedFirstMeshError && worker.FirstMeshError != null)
        {
            loggedFirstMeshError = true;
            Mod.Logger.Warning("First mesh error was: {0}", worker.FirstMeshError);
        }
        if (!loggedFirstMipError && worker.FirstMipError != null)
        {
            loggedFirstMipError = true;
            Mod.Logger.Warning("First mip error was: {0}", worker.FirstMipError);
        }

        Mod.Logger.Notification(
            "{0}: {1} sections resident [{2}] ({3} RAM-evicted, {4} from cache), {5} meshes ({6} evicted, {23} seam repairs), " +
            "{7} selected [{8}] minus {9} draw-culled ({22} subtrees traversal-culled), {10} columns captured, {11} pending, " +
            "worker: {12} captures / {13} meshes / {14} mips queued / {15}+{16}+{17} errors, " +
            "{18} awaiting mip ({19} in flight), {20} render-dirty, {21} unsaved",
            prefix, world.Sections.Count, world.DescribeLevels(), world.EvictedSectionsTotal, pipeline.CachedSectionsLoaded,
            renderer.MeshCount, renderer.EvictedTotal, renderer.LastDrawCount, renderer.DescribeDrawnLevels(),
            renderer.LastCulledCount, pipeline.ColumnsCaptured, pipeline.PendingColumns,
            worker.PendingCaptures, worker.PendingMeshes, worker.PendingMips,
            worker.CaptureErrors, worker.MeshErrors, worker.MipErrors,
            world.MipDirty.Count, world.MipInFlightCount, world.RenderDirty.Count, world.SaveDirty.Count,
            renderer.LastTraversalCulledCount, renderer.SeamRepairsQueued);

        // The amplification between real changes and mesh rebuilds, counted rather than
        // inferred. A stationary run previously had to estimate it from 64 changed
        // sections against 5,057 mesh replacements with nothing recording the middle term.
        Mod.Logger.Notification(
            "  change locality: {0} content changes made {1} meshes stale ({2:0.00} per change); " +
            "{3} neighbour rebuilds skipped as interior",
            world.MarkChangedCalls, world.MarkChangedRenderDirtied,
            world.MarkChangedCalls > 0 ? (double)world.MarkChangedRenderDirtied / world.MarkChangedCalls : 0,
            world.MarkChangedNeighborsSkipped);

        Mod.Logger.Notification(
            "  storage on main thread since last report: snapshot {0} calls, {1:0.00}ms avg, {2:0.00}ms max | " +
            "inline loads {3} calls, {4:0.00}ms avg, {5:0.00}ms max | storage thread: {6} write backlog, " +
            "{7} written, {8} write errors, {11} read, {9} async loads in flight, {10} read errors",
            pipeline.SaveCalls, pipeline.SaveCalls > 0 ? pipeline.SaveMsTotal / pipeline.SaveCalls : 0, pipeline.SaveMsMax,
            pipeline.LoadCalls, pipeline.LoadCalls > 0 ? pipeline.LoadMsTotal / pipeline.LoadCalls : 0, pipeline.LoadMsMax,
            storageThread?.Backlog ?? 0, storageThread?.SectionsWritten ?? 0, storageThread?.SaveErrors ?? 0,
            world.LoadsInFlight.Count, storageThread?.LoadErrors ?? 0, storageThread?.SectionsRead ?? 0);

        if (assist != null && assist.RemoteKeys.Count > 0)
        {
            Mod.Logger.Notification(
                "  server assist: {0} offered, {1} remote-only, {2} wanted by view, {3} requested, " +
                "{4} received, {5} installed, {6} in flight, peak {7}, {8} declined",
                assist.RemoteKeys.Count, pipeline.RemoteOnly.Count, pipeline.RemoteWanted().Length,
                assist.SectionsRequested, assist.SectionsReceived, pipeline.ForeignSectionsInstalled,
                assist.InFlight, assist.PeakInFlight, assist.SectionsRefused);
        }

        if (localOffers != null || localOfferKeysDiscovered > 0)
        {
            Mod.Logger.Notification(
                "  local sibling offers: {0} discovered, {1} retryable misses, {2} accepted, "
                + "{3} installed, {4} remote-only, {5} wanted",
                localOfferKeysDiscovered, localOfferRetryableMisses, localOfferSectionsAccepted,
                localOfferSectionsInstalled, pipeline.RemoteOnly.Count, pipeline.RemoteWanted().Length);
        }

        // Repairing means the cache on disk was written without colours, which drew as
        // black ground. Worth saying out loud, and worth being able to watch go to zero.
        if (pipeline.PaletteEntriesRepaired > 0)
        {
            Mod.Logger.Notification(
                "  repaired {0} palette entries that were cached with no colour at all. "
                + "They drew as black terrain, and the repair is written back as they load.",
                pipeline.PaletteEntriesRepaired);
        }

        // Warning, not notification: this is terrain drawn as a guess. Naming the codes is
        // the whole point - the previous symptom was black ground and nothing to grep for.
        if (uncoloredForeignEntries > 0)
        {
            string[] codes = pipeline.UnresolvedBlockCodes();
            Mod.Logger.Warning(
                "  {0} palette entries came from blocks this game does not have, and are drawn as "
                + "plain grey. {1}",
                uncoloredForeignEntries,
                codes.Length > 0
                    ? "Codes: " + string.Join(", ", codes.Take(12))
                      + (codes.Length > 12 ? $" and {codes.Length - 12} more" : "")
                    : "No unresolved codes were recorded, so these arrived over the network rather "
                      + "than from the cache.");
        }

        if (renderer != null && renderer.WalkCost.Calls > 0)
        {
            // Microseconds, because that is the scale these are at, and because rounding
            // them to milliseconds would print four zeroes and teach nobody anything.
            // A frame-rate comparison cannot see any of these: the benchmark's own
            // run-to-run spread is wider than all four added together.
            Mod.Logger.Notification(
                "  render thread per frame over {0} frames: prune {9:0.0}us avg / {10:0.0}us max | " +
                "schedule {1:0.0}/{2:0.0} | " +
                "far distance {3:0.0}/{4:0.0} | readiness shadow {11:0.0}/{12:0.0} | "
                + "quadtree walk {5:0.0}/{6:0.0} | draw submit {7:0.0}/{8:0.0}",
                renderer.WalkCost.Calls,
                renderer.ScheduleCost.AvgUs, renderer.ScheduleCost.MaxUs,
                renderer.FarDistanceCost.AvgUs, renderer.FarDistanceCost.MaxUs,
                renderer.WalkCost.AvgUs, renderer.WalkCost.MaxUs,
                renderer.DrawCost.AvgUs, renderer.DrawCost.MaxUs,
                renderer.PruneCost.AvgUs, renderer.PruneCost.MaxUs,
                renderer.ReadinessCost.AvgUs, renderer.ReadinessCost.MaxUs);

            Mod.Logger.Notification(
                "  render p95/p99/max us: prune {0:0}/{1:0}/{2:0} | schedule {3:0}/{4:0}/{5:0} | "
                + "upload {6:0}/{7:0}/{8:0} | evict {9:0}/{10:0}/{11:0} | seasonal {12:0}/{13:0}/{14:0} | "
                + "far {15:0}/{16:0}/{17:0} | readiness {24:0}/{25:0}/{26:0} | "
                + "walk {18:0}/{19:0}/{20:0} | draw {21:0}/{22:0}/{23:0}",
                renderer.PruneCost.P95Us, renderer.PruneCost.P99Us, renderer.PruneCost.MaxUs,
                renderer.ScheduleCost.P95Us, renderer.ScheduleCost.P99Us, renderer.ScheduleCost.MaxUs,
                renderer.UploadCost.P95Us, renderer.UploadCost.P99Us, renderer.UploadCost.MaxUs,
                renderer.EvictCost.P95Us, renderer.EvictCost.P99Us, renderer.EvictCost.MaxUs,
                renderer.SeasonalCost.P95Us, renderer.SeasonalCost.P99Us, renderer.SeasonalCost.MaxUs,
                renderer.FarDistanceCost.P95Us, renderer.FarDistanceCost.P99Us, renderer.FarDistanceCost.MaxUs,
                renderer.WalkCost.P95Us, renderer.WalkCost.P99Us, renderer.WalkCost.MaxUs,
                renderer.DrawCost.P95Us, renderer.DrawCost.P99Us, renderer.DrawCost.MaxUs,
                renderer.ReadinessCost.P95Us, renderer.ReadinessCost.P99Us, renderer.ReadinessCost.MaxUs);

            // The one line that can see the reported micro-hitches at all. Everything
            // above it measures our own phases; this measures the frame the player
            // actually experiences, and how much of it was us.
            Mod.Logger.Notification("  frame timeline: {0}", renderer.FrameTimeline.Describe());

            // How tall sections actually are, and what that already buys. The distribution
            // is the evidence Phase 4 of the GPU plan has to be argued from: a pyramid can
            // only reject a section that does not span the world, and until this line
            // nobody knew whether an ordinary section does.
            Mod.Logger.Notification(
                "  section heights: {0} | vertical cull: {1} section-draws skipped this interval "
                + "that a full-height box would have kept, worst frame {2}",
                renderer.SectionHeights.Describe(renderer.WorldHeight),
                renderer.VerticalCulledSections,
                renderer.VerticalCulledMax);

            // Whether depth verdicts actually stopped anything being drawn. In the log and
            // not only in chat, because chat cannot be copied out of the game and this is
            // the line that says whether a playtest was testing the thing it was meant to.
            // Unconditional: the case worth catching is the switch being on while nothing
            // ran, and a line that only appears when culling works cannot show that.
            Mod.Logger.Notification("  cull: {0}", renderer.DescribeGpuCull());

            // The pyramid's own cost, apart from everything else. Phase 4 lives or dies on
            // whether the GPU figure here is smaller than the drawing a depth test could
            // remove, and CPU time cannot see it because none of the work is on the CPU.
            if (renderer.HzbCost.Calls > 0)
            {
                Mod.Logger.Notification(
                    "  hzb: {0} | cpu {1:0.0}us avg / {2:0.0}us max, p95 {3:0}/p99 {4:0} | "
                    + "gpu {5:0.0}us avg / {6:0.0}us max over {7} timed builds | "
                    + "mid-frame gpu {8:0.0}us avg / {9:0.0}us max over {10} builds | "
                    + "classify gpu {11:0.0}us avg / {12:0.0}us max over {13} dispatches",
                    renderer.DescribeDepthPyramid(),
                    renderer.HzbCost.AvgUs, renderer.HzbCost.MaxUs,
                    renderer.HzbCost.P95Us, renderer.HzbCost.P99Us,
                    renderer.GpuHzbCost.AvgUs, renderer.GpuHzbCost.MaxUs,
                    renderer.GpuHzbCost.Calls,
                    renderer.GpuSplitHzbCost.AvgUs, renderer.GpuSplitHzbCost.MaxUs,
                    renderer.GpuSplitHzbCost.Calls,
                    renderer.GpuClassifyCost.AvgUs, renderer.GpuClassifyCost.MaxUs,
                    renderer.GpuClassifyCost.Calls);

                // Per distance band, because the case for this phase is that the amount of
                // hidden terrain scales with draw distance. One overall average mixes 500
                // blocks with 32,000 and cannot show that either way.
                string byDistance = renderer.DescribeDepthPyramidByDistance();
                if (byDistance.Length > 0)
                    Mod.Logger.Notification("  hzb hidden by distance: {0}", byDistance);

                string subdivision = renderer.DescribeDepthPyramidSubdivision();
                if (subdivision.Length > 0)
                    Mod.Logger.Notification("  hzb headroom: {0}", subdivision);

                // The correctness gate. The left-hand figure must be zero: it counts
                // sections the pyramid called hidden that a query had actually seen pixels
                // of, and every one of those is terrain a player could see. Warning rather
                // than notification when it is not zero, so it cannot scroll past.
                if (renderer.HzbVerdictsChecked > 0)
                {
                    string line = "  hzb against occlusion queries: {0} checked | "
                        + "{1} called hidden that a query SAW (must be zero) | "
                        + "{2} the query hid and the pyramid did not";
                    if (renderer.HzbHiddenButQuerySawIt > 0)
                    {
                        Mod.Logger.Warning(line, renderer.HzbVerdictsChecked,
                            renderer.HzbHiddenButQuerySawIt, renderer.HzbMissedWhatQueryHid);
                    }
                    else
                    {
                        Mod.Logger.Notification(line, renderer.HzbVerdictsChecked,
                            renderer.HzbHiddenButQuerySawIt, renderer.HzbMissedWhatQueryHid);
                    }
                }
            }

            Mod.Logger.Notification("  vanilla readiness: {0}", renderer.DescribeReadiness());

            // Why a coarser parent is covering ground its children could cover. Each cause
            // wants a different fix, so they are reported apart rather than as one number.
            if (renderer.CoarseWaitingLoad + renderer.CoarseWaitingMesh
                + renderer.CoarseWaitingSchedule + renderer.CoarseWaitingOther > 0)
            {
                Mod.Logger.Notification(
                    "  coarse cover waits: {0} on storage, {1} on a mesh worker, {2} on a schedule slot, "
                    + "{3} with nothing pending | backlog {4} render-dirty, {5} meshes queued, {6} loads in flight",
                    renderer.CoarseWaitingLoad, renderer.CoarseWaitingMesh,
                    renderer.CoarseWaitingSchedule, renderer.CoarseWaitingOther,
                    pipeline.World.RenderDirty.Count, pipeline.Worker.PendingMeshes,
                    pipeline.World.LoadsInFlight.Count);
            }

            Mod.Logger.Notification(
                "  render interval: {0} projection resets, {1:0.00} MiB uploaded; phase hitches >=25/50/100ms: {2}/{3}/{4}",
                renderer.ProjectionResetCount, renderer.MeshUploadBytes / (1024.0 * 1024.0),
                renderer.PruneCost.Over25Ms + renderer.ScheduleCost.Over25Ms + renderer.UploadCost.Over25Ms
                    + renderer.EvictCost.Over25Ms + renderer.SeasonalCost.Over25Ms + renderer.FarDistanceCost.Over25Ms
                    + renderer.ReadinessCost.Over25Ms + renderer.WalkCost.Over25Ms + renderer.DrawCost.Over25Ms,
                renderer.PruneCost.Over50Ms + renderer.ScheduleCost.Over50Ms + renderer.UploadCost.Over50Ms
                    + renderer.EvictCost.Over50Ms + renderer.SeasonalCost.Over50Ms + renderer.FarDistanceCost.Over50Ms
                    + renderer.ReadinessCost.Over50Ms + renderer.WalkCost.Over50Ms + renderer.DrawCost.Over50Ms,
                renderer.PruneCost.Over100Ms + renderer.ScheduleCost.Over100Ms + renderer.UploadCost.Over100Ms
                    + renderer.EvictCost.Over100Ms + renderer.SeasonalCost.Over100Ms + renderer.FarDistanceCost.Over100Ms
                    + renderer.ReadinessCost.Over100Ms + renderer.WalkCost.Over100Ms + renderer.DrawCost.Over100Ms);

            Mod.Logger.Notification(
                "  render budgets: snapshots {0} items/{1:0.00} MiB, {2} queued/{3:0.00} MiB, oldest {4}ms | "
                + "uploads {5} items/{6:0.00} MiB, {7} queued/{8:0.00} MiB, oldest {9}ms",
                renderer.MeshSnapshotItems, renderer.MeshSnapshotBytes / (1024.0 * 1024.0),
                worker.PendingMeshes, worker.PendingMeshBytes / (1024.0 * 1024.0), worker.OldestMeshAgeMs,
                renderer.MeshUploadItems, renderer.MeshUploadBytes / (1024.0 * 1024.0),
                worker.PendingMeshResults, worker.PendingMeshResultBytes / (1024.0 * 1024.0),
                worker.OldestMeshResultAgeMs);

            Mod.Logger.Notification(
                "  render gpu calls p95/p99/max us: upload {0:0}/{1:0}/{2:0} | dispose {3:0}/{4:0}/{5:0}",
                renderer.GlUploadCost.P95Us, renderer.GlUploadCost.P99Us, renderer.GlUploadCost.MaxUs,
                renderer.MeshDisposeCost.P95Us, renderer.MeshDisposeCost.P99Us, renderer.MeshDisposeCost.MaxUs);

            Mod.Logger.Notification(
                "  render draw interval: opaque {0} calls, {1} vertices/{2} indices | "
                + "water {3} calls, {4} vertices/{5} indices | live geometry {6:0.00} MiB, "
                + "opaque {7}/{8}, water {9}/{10}",
                renderer.OpaqueDrawCalls, renderer.OpaqueDrawVertices, renderer.OpaqueDrawIndices,
                renderer.WaterDrawCalls, renderer.WaterDrawVertices, renderer.WaterDrawIndices,
                renderer.LiveGpuMeshBytes / (1024.0 * 1024.0),
                renderer.LiveOpaqueVertices, renderer.LiveOpaqueIndices,
                renderer.LiveWaterVertices, renderer.LiveWaterIndices);

            string? arena = renderer.DescribeGpuArena();
            if (arena != null)
            {
                Mod.Logger.Notification("  gpu arena shadow: {0}", arena);
                Mod.Logger.Notification("  packed opaque: {0}", renderer.DescribePackedDraw());
                Mod.Logger.Notification("  packed clusters: {0}", renderer.DescribeClusterDraw());
                // Phase 8 may emit several commands per section, so both counts are named.
                // Coverage stays section-based: a missing clustered section falls back as
                // one complete legacy draw rather than being flattered by command count.
                Mod.Logger.Notification(
                    "  gpu indirect shadow: last frame {0} commands for {1} of {2} drawn "
                    + "sections would be {3} multi-draw batches, coverage {4:P0}, {5} spans stale",
                    renderer.ShadowIndirectCommands,
                    renderer.ShadowIndirectSections,
                    renderer.ShadowIndirectSections + renderer.ShadowIndirectMissing,
                    renderer.ShadowIndirectBatches,
                    renderer.ShadowIndirectCoverage,
                    renderer.ShadowIndirectDropped);
            }

            if (renderer.GpuTimingRequested)
            {
                LodPhaseCost opaqueGpu = renderer.GpuOpaqueCost;
                LodPhaseCost splitNearGpu = renderer.GpuSplitNearCost;
                LodPhaseCost splitFarGpu = renderer.GpuSplitFarCost;
                LodPhaseCost waterGpu = renderer.GpuWaterCost;
                Mod.Logger.Notification(
                    "  delayed GPU pass p95/p99/max us: opaque {0:0}/{1:0}/{2:0} over {3} samples | "
                    + "split near {4:0}/{5:0}/{6:0} over {7} | split far {8:0}/{9:0}/{10:0} over {11} | "
                    + "water {12:0}/{13:0}/{14:0} over {15}; {16} pending, {17} ring-full skips, "
                    + "{18} time-query target conflicts, timing {19}",
                    opaqueGpu.P95Us, opaqueGpu.P99Us, opaqueGpu.MaxUs, opaqueGpu.Calls,
                    splitNearGpu.P95Us, splitNearGpu.P99Us, splitNearGpu.MaxUs, splitNearGpu.Calls,
                    splitFarGpu.P95Us, splitFarGpu.P99Us, splitFarGpu.MaxUs, splitFarGpu.Calls,
                    waterGpu.P95Us, waterGpu.P99Us, waterGpu.MaxUs, waterGpu.Calls,
                    renderer.GpuTimerPendingResults, renderer.GpuTimerUnavailableSlots,
                    renderer.GpuTimerTargetBusy, renderer.GpuTimingActive ? "active" : "inactive");
            }

            // Collections since the last report, beside the phase maxima, because the
            // two are related and the relationship is easy to get backwards. A phase
            // maximum is not a measurement of that phase: the far-distance scan averages
            // 2us over a few hundred meshes and has reported a 1624us maximum, which
            // nothing inside a loop that multiplies and compares can account for. What a
            // maximum records is whatever interrupted the frame, charged to whichever
            // phase was running. These counters say how much of that was collection.
            Mod.Logger.Notification(
                "  gc since last report: {0} gen0, {1} gen1, {2} gen2, {3} MB managed",
                GC.CollectionCount(0) - gen0AtLastReport,
                GC.CollectionCount(1) - gen1AtLastReport,
                GC.CollectionCount(2) - gen2AtLastReport,
                GC.GetTotalMemory(false) / (1024 * 1024));

            gen0AtLastReport = GC.CollectionCount(0);
            gen1AtLastReport = GC.CollectionCount(1);
            gen2AtLastReport = GC.CollectionCount(2);
        }

        if (totalTickCost.Calls > 0)
        {
            Mod.Logger.Notification(
                "  game tick p95/p99/max us over {0} ticks: total {1:0}/{2:0}/{3:0} | assist {4:0}/{5:0}/{6:0} | "
                + "local offers {7:0}/{8:0}/{9:0} | pipeline {10:0}/{11:0}/{12:0} | resident evict {13:0}/{14:0}/{15:0}; "
                + "total hitches >=25/50/100ms: {16}/{17}/{18}",
                totalTickCost.Calls,
                totalTickCost.P95Us, totalTickCost.P99Us, totalTickCost.MaxUs,
                assistTickCost.P95Us, assistTickCost.P99Us, assistTickCost.MaxUs,
                localOfferTickCost.P95Us, localOfferTickCost.P99Us, localOfferTickCost.MaxUs,
                pipelineTickCost.P95Us, pipelineTickCost.P99Us, pipelineTickCost.MaxUs,
                residentEvictTickCost.P95Us, residentEvictTickCost.P99Us, residentEvictTickCost.MaxUs,
                totalTickCost.Over25Ms, totalTickCost.Over50Ms, totalTickCost.Over100Ms);

            Mod.Logger.Notification(
                "  pipeline p95/p99/max us: foreign install {0:0}/{1:0}/{2:0} | load install {3:0}/{4:0}/{5:0} | "
                + "capture schedule {6:0}/{7:0}/{8:0} | capture apply {9:0}/{10:0}/{11:0} | "
                + "mip apply {12:0}/{13:0}/{14:0} | mip schedule {15:0}/{16:0}/{17:0} | "
                + "save snapshot {18:0}/{19:0}/{20:0}",
                pipeline.ForeignInstallCost.P95Us, pipeline.ForeignInstallCost.P99Us, pipeline.ForeignInstallCost.MaxUs,
                pipeline.LoadInstallCost.P95Us, pipeline.LoadInstallCost.P99Us, pipeline.LoadInstallCost.MaxUs,
                pipeline.CaptureScheduleCost.P95Us, pipeline.CaptureScheduleCost.P99Us, pipeline.CaptureScheduleCost.MaxUs,
                pipeline.CaptureApplyCost.P95Us, pipeline.CaptureApplyCost.P99Us, pipeline.CaptureApplyCost.MaxUs,
                pipeline.MipApplyCost.P95Us, pipeline.MipApplyCost.P99Us, pipeline.MipApplyCost.MaxUs,
                pipeline.MipScheduleCost.P95Us, pipeline.MipScheduleCost.P99Us, pipeline.MipScheduleCost.MaxUs,
                pipeline.SaveSnapshotCost.P95Us, pipeline.SaveSnapshotCost.P99Us, pipeline.SaveSnapshotCost.MaxUs);

            Mod.Logger.Notification(
                "  capture publish budget: {0} items/{1:0.00} MiB, {2} queued/{3:0.00} MiB, oldest {4}ms",
                pipeline.CaptureApplyItems, pipeline.CaptureApplyBytes / (1024.0 * 1024.0),
                pipeline.PendingCaptureResults, pipeline.PendingCaptureResultBytes / (1024.0 * 1024.0),
                pipeline.OldestCaptureResultAgeMs);

            Mod.Logger.Notification(
                "  install budgets: assist input {0} items/{1:0.00} MiB, {2} queued/{3:0.00} MiB, oldest {4}ms | "
                + "local input {5} items/{6:0.00} MiB | foreign publish {7} items/{8:0.00} MiB, "
                + "{9} queued/{10:0.00} MiB, oldest {11}ms | background {12} items/{13:0.00} MiB, "
                + "{14} queued/{15:0.00} MiB, oldest {16}ms",
                assist?.ArrivalItemsProcessed ?? 0,
                (assist?.ArrivalBytesProcessed ?? 0) / (1024.0 * 1024.0),
                assist?.PendingArrivals ?? 0,
                (assist?.PendingArrivalBytes ?? 0) / (1024.0 * 1024.0),
                assist?.OldestArrivalAgeMs ?? 0,
                localOfferItemsProcessed, localOfferBytesProcessed / (1024.0 * 1024.0),
                pipeline.ForeignInstallItems, pipeline.ForeignInstallBytes / (1024.0 * 1024.0),
                pipeline.PendingForeignResults, pipeline.PendingForeignResultBytes / (1024.0 * 1024.0),
                pipeline.OldestForeignResultAgeMs,
                pipeline.LoadInstallItems, pipeline.LoadInstallBytes / (1024.0 * 1024.0),
                pipeline.PendingLoadResults, pipeline.PendingLoadResultBytes / (1024.0 * 1024.0),
                pipeline.OldestLoadResultAgeMs);

            if (allocationTelemetryEnabled) LogAllocationStats();
        }

        if (storageThread?.FirstSaveError != null && !loggedFirstSaveError)
        {
            loggedFirstSaveError = true;
            Mod.Logger.Warning("First storage-write error was: {0}", storageThread.FirstSaveError);
        }
        pipeline.ResetStorageStats();
        pipeline.ResetPhaseCosts();
        assist?.ResetPumpStats();
        localOfferItemsProcessed = 0;
        localOfferBytesProcessed = 0;
        totalTickCost.Reset();
        assistTickCost.Reset();
        localOfferTickCost.Reset();
        pipelineTickCost.Reset();
        residentEvictTickCost.Reset();
        renderer?.ResetPhaseCosts();
    }

    void LogAllocationStats()
    {
        static double MiB(long bytes) => bytes / (1024.0 * 1024.0);
        static double KiB(long bytes) => bytes / 1024.0;

        Mod.Logger.Notification(
            "  tick allocation interval MiB/max KiB: total {0:0.00}/{1:0.0} | assist {2:0.00}/{3:0.0} | "
            + "local {4:0.00}/{5:0.0} | pipeline {6:0.00}/{7:0.0} | evict {8:0.00}/{9:0.0}",
            MiB(totalTickCost.AllocatedBytes), KiB(totalTickCost.MaxAllocatedBytes),
            MiB(assistTickCost.AllocatedBytes), KiB(assistTickCost.MaxAllocatedBytes),
            MiB(localOfferTickCost.AllocatedBytes), KiB(localOfferTickCost.MaxAllocatedBytes),
            MiB(pipelineTickCost.AllocatedBytes), KiB(pipelineTickCost.MaxAllocatedBytes),
            MiB(residentEvictTickCost.AllocatedBytes), KiB(residentEvictTickCost.MaxAllocatedBytes));

        Mod.Logger.Notification(
            "  pipeline allocation interval MiB/max KiB: foreign {0:0.00}/{1:0.0} | load {2:0.00}/{3:0.0} | "
            + "capture schedule {4:0.00}/{5:0.0} | capture apply {6:0.00}/{7:0.0} | "
            + "mip apply {8:0.00}/{9:0.0} | mip schedule {10:0.00}/{11:0.0} | save {12:0.00}/{13:0.0}",
            MiB(pipeline.ForeignInstallCost.AllocatedBytes), KiB(pipeline.ForeignInstallCost.MaxAllocatedBytes),
            MiB(pipeline.LoadInstallCost.AllocatedBytes), KiB(pipeline.LoadInstallCost.MaxAllocatedBytes),
            MiB(pipeline.CaptureScheduleCost.AllocatedBytes), KiB(pipeline.CaptureScheduleCost.MaxAllocatedBytes),
            MiB(pipeline.CaptureApplyCost.AllocatedBytes), KiB(pipeline.CaptureApplyCost.MaxAllocatedBytes),
            MiB(pipeline.MipApplyCost.AllocatedBytes), KiB(pipeline.MipApplyCost.MaxAllocatedBytes),
            MiB(pipeline.MipScheduleCost.AllocatedBytes), KiB(pipeline.MipScheduleCost.MaxAllocatedBytes),
            MiB(pipeline.SaveSnapshotCost.AllocatedBytes), KiB(pipeline.SaveSnapshotCost.MaxAllocatedBytes));

        if (renderer == null) return;
        Mod.Logger.Notification(
            "  render allocation interval MiB/max KiB: prune {0:0.00}/{1:0.0} | schedule {2:0.00}/{3:0.0} | "
            + "upload {4:0.00}/{5:0.0} | evict {6:0.00}/{7:0.0} | seasonal {8:0.00}/{9:0.0} | "
            + "far {10:0.00}/{11:0.0} | readiness {16:0.00}/{17:0.0} | "
            + "walk {12:0.00}/{13:0.0} | draw {14:0.00}/{15:0.0}",
            MiB(renderer.PruneCost.AllocatedBytes), KiB(renderer.PruneCost.MaxAllocatedBytes),
            MiB(renderer.ScheduleCost.AllocatedBytes), KiB(renderer.ScheduleCost.MaxAllocatedBytes),
            MiB(renderer.UploadCost.AllocatedBytes), KiB(renderer.UploadCost.MaxAllocatedBytes),
            MiB(renderer.EvictCost.AllocatedBytes), KiB(renderer.EvictCost.MaxAllocatedBytes),
            MiB(renderer.SeasonalCost.AllocatedBytes), KiB(renderer.SeasonalCost.MaxAllocatedBytes),
            MiB(renderer.FarDistanceCost.AllocatedBytes), KiB(renderer.FarDistanceCost.MaxAllocatedBytes),
            MiB(renderer.WalkCost.AllocatedBytes), KiB(renderer.WalkCost.MaxAllocatedBytes),
            MiB(renderer.DrawCost.AllocatedBytes), KiB(renderer.DrawCost.MaxAllocatedBytes),
            MiB(renderer.ReadinessCost.AllocatedBytes), KiB(renderer.ReadinessCost.MaxAllocatedBytes));
    }

    void OnLeaveWorld()
    {
        configDialog?.TryClose();
        assist?.Reset();
        // Belongs to the world being left: the next one is a different savegame with a
        // different sibling cache, and holding this open would keep a file handle on a
        // database the server side may want to delete or replace.
        localOffers?.Dispose();
        localOffers = null;
        loggedLocalOffers = false;
        localOfferProbeTicks = 0;
        localOfferKeysDiscovered = 0;
        localOfferRetryableMisses = 0;
        localOfferSectionsAccepted = 0;
        localOfferSectionsInstalled = 0;
        pipeline.Close();
        while (pipeline.Worker.MeshResults.TryDequeue(out _)) { }
        renderer.ClearMeshes();
    }

    /// <summary>
    /// Writes a chat report into the log as well, one line at a time so each carries its
    /// own timestamp and stays greppable. Game chat cannot be selected or copied, so
    /// without this every diagnostic has to be read off the screen and retyped.
    /// </summary>
    void LogReportLines(string label, string report)
    {
        foreach (string line in report.Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            Mod.Logger.Notification("  {0}: {1}", label, line.Trim());
        }
    }

    /// <summary>
    /// One command for the complete GPU-renderer experiment. The named presets are a
    /// chronological ladder: each adds exactly one accepted or experimental stage to the
    /// previous preset. That retains the convenience of one player-facing command without
    /// collapsing seven variables into an all-or-nothing comparison.
    /// </summary>
    bool TrySetPhase8TestStack(string requested, out string preset)
    {
        preset = requested.Trim().ToLowerInvariant() switch
        {
            "off" or "legacy" => "off",
            "batch" or "batching" => "batch",
            "cull" or "culling" => "cull",
            "late" => "late",
            "packed" => "packed",
            "on" or "cluster" or "clusters" => "clusters",
            _ => "",
        };
        if (preset.Length == 0) return false;

        bool arenas = preset != "off";
        bool batching = preset != "off";
        bool depthCull = preset is "cull" or "late" or "packed" or "clusters";
        bool late = preset is "late" or "packed" or "clusters";
        bool packed = preset is "packed" or "clusters";
        bool clusters = preset == "clusters";

        // The arena request is applied on the render thread next frame; every other flag can
        // be set now and will become effective as soon as those buffers are ready. Every
        // preset assigns all seven controls so a later test cannot inherit a hidden switch.
        // Re-requesting an already-on shadow is not a no-op: it queues every live section
        // for re-meshing. Presets above "off" share the same arenas, so moving between
        // them must preserve the filled mirror or the comparison measures warm-up work.
        if (renderer.GpuShadowRequested != arenas)
            renderer.RequestGpuShadow(arenas ? "on" : "off");
        renderer.IndirectDrawEnabled = batching;
        renderer.PackedDrawEnabled = packed;
        renderer.DepthPyramidEnabled = depthCull;
        renderer.GpuCullEnabled = depthCull;
        renderer.LateDepthPyramid = late;
        renderer.ClusterDrawEnabled = clusters;
        renderer.ResetDepthPyramidInterval();
        return true;
    }

    string DescribePhase8TestStack()
    {
        bool[] flags =
        {
            renderer.GpuShadowRequested,
            renderer.IndirectDrawEnabled,
            renderer.PackedDrawEnabled,
            renderer.DepthPyramidEnabled,
            renderer.GpuCullEnabled,
            renderer.LateDepthPyramid,
            renderer.ClusterDrawEnabled,
        };
        string state = flags.All(value => !value) ? "OFF"
            : flags.SequenceEqual(new[] { true, true, false, false, false, false, false }) ? "BATCH"
            : flags.SequenceEqual(new[] { true, true, false, true, true, false, false }) ? "CULL"
            : flags.SequenceEqual(new[] { true, true, false, true, true, true, false }) ? "LATE"
            : flags.SequenceEqual(new[] { true, true, true, true, true, true, false }) ? "PACKED"
            : flags.All(value => value) ? "CLUSTERS"
            : "MIXED";

        string active = state switch
        {
            "OFF" => "Legacy path: arenas " + renderer.DescribeGpuShadow(),
            "BATCH" => "Batch path: " + renderer.DescribeIndirectDraw(),
            "CULL" => "Cull path: " + renderer.DescribeGpuCull(),
            "LATE" => "Cull path: " + renderer.DescribeGpuCull()
                + " Picture: " + renderer.DescribeLateDepthPyramid(),
            "PACKED" => "Packed path: " + renderer.DescribePackedDraw()
                + " Culling: " + renderer.DescribeGpuCull(),
            "CLUSTERS" => "Cluster path: " + renderer.DescribeClusterDraw()
                + " Culling: " + renderer.DescribeGpuCull(),
            _ => "Cluster path: " + renderer.DescribeClusterDraw(),
        };

        static string Bit(bool value) => value ? "on" : "off";
        return $"{state}: arenas {Bit(flags[0])}, batching {Bit(flags[1])}, "
            + $"packed {Bit(flags[2])}, HZB {Bit(flags[3])}, culling {Bit(flags[4])}, "
            + $"same-frame near/far {Bit(flags[5])}, clusters {Bit(flags[6])}. "
            + active;
    }

    void RegisterCommands()
    {
        capi.ChatCommands.Create("vhinfo")
            .WithDescription("VintageHorizons status")
            .HandleWith(_ => deferringTo != null
                ? TextCommandResult.Success(
                    $"[VintageHorizons] idle: '{deferringTo}' is installed and switched on, so it is "
                    + "drawing the distant terrain. Switch it off in its own settings, or run "
                    + "'.vhdefer off' to draw beside it. Either way, restart the game afterwards: "
                    + "this is decided once at startup.")
                : TextCommandResult.Success(
                $"[VintageHorizons] sections: {pipeline.World.Sections.Count} [{pipeline.World.DescribeLevels()}] " +
                $"({pipeline.CachedSectionsLoaded} from cache), meshes: {renderer.MeshCount}, " +
                $"drawn: {renderer.LastDrawCount} [{renderer.DescribeDrawnLevels()}] " +
                $"({renderer.LastTraversalCulledCount} subtrees traversal-culled, " +
                $"{renderer.LastCulledCount} selected nodes draw-culled), " +
                $"columns captured: {pipeline.ColumnsCaptured}, pending: {pipeline.PendingColumns}, " +
                $"worker: {pipeline.Worker.PendingCaptures}c/{pipeline.Worker.PendingMeshes}m/" +
                $"{pipeline.Worker.PendingMeshResults}u/{pipeline.Worker.PendingMips}p, " +
                $"awaiting mip: {pipeline.World.MipDirty.Count} ({pipeline.World.MipInFlightCount} in flight), " +
                $"unsaved: {pipeline.World.SaveDirty.Count}, persistence: {(pipeline.Persisting ? "on" : "off")}, " +
                $"render distance: {(renderer.FarViewDistanceCap > 0 ? renderer.FarViewDistanceCap + " (capped)" : "unlimited")}, " +
                $"current far edge: {(int)renderer.EffectiveFarDistance}, " +
                $"radial demand: {renderer.DescribeRadialDemand()}, " +
                $"tints: {renderer.DescribeTintReadiness()}, " +
                $"LOD thresholds: {string.Join('/', LodWorld.GetLevelThresholds())} (.vhconfig to change), " +
                $"occlusion order: {renderer.DescribeOcclusionCulling()}, " +
                $"delayed occlusion: {renderer.DescribeTemporalOcclusion()}, " +
                $"readiness: {renderer.DescribeReadiness()}, " +
                $"server assist: {assist?.Status ?? "off"}" +
                (assist != null && assist.RemoteKeys.Count > 0
                    ? $", server offers {assist.RemoteKeys.Count} sections " +
                      $"({pipeline.RemoteOnly.Count} not held locally, {pipeline.ForeignSectionsInstalled} fetched, " +
                      $"{assist.InFlight} in flight, {assist.SectionsRefused} declined)" +
                      (assist.ManifestComplete ? "" : " (manifest still arriving)")
                    : "")));

        // Registered in both states on purpose: the player who most needs this one is the
        // player we are currently idle for.
        capi.ChatCommands.Create("vhdefer")
            .WithDescription("Stay idle when another LOD mod draws. Default on. Off draws anyway.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (args.Parsers[0].IsMissing)
                {
                    return TextCommandResult.Success(
                        $"[VintageHorizons] defer to other LOD mods: {(config.IgnoreOtherLodMods ? "off" : "on")}"
                        + (deferringTo != null ? $" - idle now, because '{deferringTo}' is drawing" : ""));
                }

                config.IgnoreOtherLodMods = !(bool)args[0];
                SaveConfig();

                // Saved, not applied. Starting the mod mid-session would have to register a
                // network channel after the handshake, run a LevelFinalize that has already
                // fired, and capture chunks whose ChunkDirty events are long past.
                return TextCommandResult.Success(config.IgnoreOtherLodMods
                    ? "[VintageHorizons] will draw even when another LOD mod is switched on (saved). "
                      + "Restart the game to apply. Switch the other mod off as well, or the two "
                      + "draw over the same ground."
                    : "[VintageHorizons] will stay idle when another LOD mod is drawing (saved). "
                      + "Restart the game to apply.");
            });

        // Available even while deferring, because these are persisted player settings;
        // they will apply normally after the competing renderer is switched off.
        capi.ChatCommands.Create("vhconfig")
            .WithDescription("Open the Vintage Horizons detail and draw-distance settings")
            .HandleWith(_ =>
            {
                if (configDialog?.IsOpened() == true)
                {
                    configDialog.Focus();
                    return TextCommandResult.Success("[VintageHorizons] configuration window focused.");
                }

                configDialog?.Dispose();
                int[] thresholds = deferringTo == null
                    ? LodWorld.GetLevelThresholds()
                    : LodWorld.NormalizeLevelThresholds(config.LodThresholds, config.DetailDistance);
                int farCap = deferringTo == null ? renderer.FarViewDistanceCap : config.FarViewDistanceCap;
                configDialog = new VintageHorizonsConfigDialog(capi, thresholds, farCap, ApplyGuiConfig);

                return configDialog.TryOpen()
                    ? TextCommandResult.Success("[VintageHorizons] configuration window opened.")
                    : TextCommandResult.Success("[VintageHorizons] configuration window could not open right now.");
            });

        // The remaining commands drive the renderer, which does not exist when we are
        // deferring to another LOD mod.
        if (deferringTo != null) return;

        // Named apart from ".vhwhy" on purpose. This one answers "why is that coarse",
        // the other answers "why is that missing", and registering both under one name
        // threw at startup and cost every command declared after it.
        capi.ChatCommands.Create("vhcoarse")
            .WithDescription("Explain why nearby LOD terrain draws coarser than the detail setting allows")
            .HandleWith(_ =>
            {
                var at = capi.World.Player.Entity.Pos;
                return TextCommandResult.Success(
                    "[VintageHorizons] coarse draws:" + renderer.ExplainCoarseDraws(at.X, at.Z));
            });

        capi.ChatCommands.Create("vhfar")
            .WithDescription("Cap VintageHorizons render distance in blocks (0 = unlimited)")
            .WithArgs(capi.ChatCommands.Parsers.Int("blocks"))
            .HandleWith(args =>
            {
                int blocks = (int)args[0];
                renderer.FarViewDistanceCap = blocks <= 0 ? 0 : GameMath.Clamp(blocks, 1024, 262144);
                SaveConfig();
                return TextCommandResult.Success(renderer.FarViewDistanceCap > 0
                    ? $"[VintageHorizons] render distance capped at {renderer.FarViewDistanceCap} (saved)"
                    : "[VintageHorizons] render distance unlimited (saved)");
            });

        capi.ChatCommands.Create("vhmask")
            .WithDescription("Hand each vanilla chunk its own ground instead of using one distance. On by default; turn it off to fall back to a single measured distance.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[VintageHorizons] no renderer: another LOD mod is drawing.");
                if (renderer.ChunkMaskFailed)
                    return TextCommandResult.Success(
                        "[VintageHorizons] the chunk mask hit an error this session and stays off. " +
                        "The distance handoff is drawing.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        $"[VintageHorizons] chunk mask {(renderer.ChunkMaskEnabled ? "on" : "off")}. " +
                        "On gives each loaded vanilla chunk its own ground and skips cached terrain " +
                        "that is entirely replaced. Off uses a single measured distance.");

                renderer.ChunkMaskEnabled = (bool)args[0];
                SaveConfig();
                return TextCommandResult.Success(
                    $"[VintageHorizons] chunk mask {(renderer.ChunkMaskEnabled ? "on" : "off")} (saved). " +
                    "The change applies on the next frame.");
            });

        capi.ChatCommands.Create("vhwhy")
            .WithDescription("Look at a hole and run this. Searches your line of sight for the first ground that nothing is drawing and says which part of the mod is responsible. Optional argument is how far to search, 512 blocks by default.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalInt("blocksAhead"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[VintageHorizons] no renderer: another LOD mod is drawing.");

                // Searching the whole line of sight rather than one chosen distance: the
                // hole a player is looking at is wherever it is, and a confident report
                // about the wrong chunk reads exactly like a report about the right one.
                // 512 blocks was a guess and it was too short: the cached band runs out to
                // the far distance, so a hole in its outer half sat past the end of the
                // search and the command answered "nothing wrong" about ground it never
                // looked at. Default to the distance the mod actually draws to.
                int range = args.Parsers[0].IsMissing
                    ? GameMath.Clamp((int)renderer.EffectiveFarDistance, 512, 16384)
                    : GameMath.Clamp((int)args[0], 16, 16384);
                Vec3f look = capi.World.Player.Entity.Pos.GetViewVector();
                var camera = capi.World.Player.Entity.CameraPos;

                return TextCommandResult.Success("[VintageHorizons] " + renderer.ExplainViewRay(
                    camera.X, camera.Y, camera.Z, look.X, look.Y, look.Z, range));
            });

        capi.ChatCommands.Create("vhpaint")
            .WithDescription("Paint the terrain the mask is hiding bright red instead of hiding it. Diagnostic.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[VintageHorizons] no renderer: another LOD mod is drawing.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        $"[VintageHorizons] paint mode {(renderer.MaskDebugPaint ? "on" : "off")}.");

                renderer.MaskDebugPaint = (bool)args[0];
                return TextCommandResult.Success(
                    $"[VintageHorizons] paint mode {(renderer.MaskDebugPaint ? "on" : "off")}. " +
                    "Red is terrain the mask is hiding. A gap that stays empty is not the mask.");
            });

        capi.ChatCommands.Create("vhtoplight")
            .WithDescription("Light flat cached ground the way the game lights flat ground, instead of by sun angle. On by default.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[VintageHorizons] no renderer: another LOD mod is drawing.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        $"[VintageHorizons] flat-top lighting {(renderer.FlatTopLight ? "on" : "off")}.");

                renderer.FlatTopLight = (bool)args[0];
                return TextCommandResult.Success(
                    $"[VintageHorizons] flat-top lighting {(renderer.FlatTopLight ? "on" : "off")}. " +
                    "The game never darkens flat ground as the sun drops; off shades it by sun angle " +
                    "as before. Worth comparing at dawn or dusk, not at midday.");
            });

        // Four separate switches rather than one, because the point of shipping them is
        // that a single dusk-to-night sweep can attribute what it sees. Reading both
        // shaders settles what vanilla computes; it cannot settle whether copying it onto
        // terrain kilometres away looks right, and only the owner flipping these while
        // watching can.
        capi.ChatCommands.Create("vhlight")
            .WithDescription("Match cached terrain lighting to the game's. moondir | ramp | ambient | boost | sky | all, on or off. All on by default.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("part"),
                      capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[VintageHorizons] no renderer: another LOD mod is drawing.");

                string DescribeLight() =>
                    $"moondir {(renderer.LightMoonDirection ? "on" : "off")}, " +
                    $"ramp {(renderer.LightVanillaRamp ? "on" : "off")}, " +
                    $"ambient {(renderer.LightAmbientColor ? "on" : "off")}, " +
                    $"boost {(renderer.LightDayBoost ? "on" : "off")}, " +
                    $"sky {(renderer.LightSkyDayLight ? "on" : "off")}";

                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success($"[VintageHorizons] lighting: {DescribeLight()}.");

                if (args.Parsers[1].IsMissing)
                    return TextCommandResult.Error(
                        "[VintageHorizons] use: .vhlight moondir | ramp | ambient | boost | sky | all, then on or off.");

                string part = ((string)args[0]).ToLowerInvariant();
                bool on = (bool)args[1];
                string note;
                switch (part)
                {
                    case "moondir":
                        renderer.LightMoonDirection = on;
                        note = "Shade by the game's light vector, which follows the moon at night. " +
                               "Changes nothing while the sun is up.";
                        break;
                    case "ramp":
                        renderer.LightVanillaRamp = on;
                        note = "Use the game's shade ramp and its darker floor on slopes facing away " +
                               "from the light. A constant difference, visible at any time of day.";
                        break;
                    case "ambient":
                        renderer.LightAmbientColor = on;
                        note = "Light by the game's own ambient colour instead of the sun colour. " +
                               "This is the one to watch through sunset into night.";
                        break;
                    case "boost":
                        renderer.LightDayBoost = on;
                        note = "Apply the game's daylight brightening. This is the only one that " +
                               "changes broad daylight; off is the older, dimmer look.";
                        break;
                    case "sky":
                        renderer.LightSkyDayLight = on;
                        note = "Fade the far edge of the cache into the sky the game actually " +
                               "draws. Look at the horizon band, not the ground.";
                        break;
                    case "all":
                        renderer.LightMoonDirection = on;
                        renderer.LightVanillaRamp = on;
                        renderer.LightAmbientColor = on;
                        renderer.LightDayBoost = on;
                        renderer.LightSkyDayLight = on;
                        note = "Off is the lighting every build before this one used.";
                        break;
                    default:
                        return TextCommandResult.Error(
                            "[VintageHorizons] use: .vhlight moondir | ramp | ambient | boost | sky | all, then on or off.");
                }

                return TextCommandResult.Success($"[VintageHorizons] lighting: {DescribeLight()}. {note}");
            });

        // The player-facing control for the current experiment. The individual phase
        // commands remain useful diagnostics, but the ordinary performance bisect uses one
        // chronological preset ladder and cannot silently inherit a mixed state.
        capi.ChatCommands.Create("vhphase8")
            .WithDescription("Select one GPU terrain test stage. off | batch | cull | late | packed | clusters. 'on' means clusters.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("preset"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[VintageHorizons] no renderer: another LOD mod is drawing.");

                string? applied = null;
                if (!args.Parsers[0].IsMissing)
                {
                    if (!TrySetPhase8TestStack((string)args[0], out applied))
                        return TextCommandResult.Error(
                            "[VintageHorizons] use: .vhphase8 off | batch | cull | late | packed | clusters");
                    SaveConfig();
                }

                string status = DescribePhase8TestStack();
                LogReportLines("phase8", status);
                return TextCommandResult.Success(
                    "[VintageHorizons] Phase 8 test stack " + status
                    + (args.Parsers[0].IsMissing
                        ? " Presets: off, batch, cull, late, packed, clusters. No other GPU commands are needed."
                        : applied == "off"
                            ? " The regional buffers are released on the next frame; this is the complete legacy baseline."
                            : $" Applied the {applied} preset. After switching up from off, let the regional buffers fill and run .vhphase8 once more before measuring."));
            });

        // Measurement only. The shadow copies cached geometry into the regional buffers a
        // future renderer would draw from and reports how far draw calls would fall, while
        // the established renderer keeps drawing every pixel exactly as before.
        capi.ChatCommands.Create("vhgpu")
            .WithDescription("Measure what a regional GPU renderer would submit. off | on | verify. Draws nothing; remembered between sessions.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("mode"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[VintageHorizons] no renderer: another LOD mod is drawing.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        "[VintageHorizons] GPU measurement shadow " + renderer.DescribeGpuShadow());

                string mode = (string)args[0];
                if (!renderer.RequestGpuShadow(mode))
                    return TextCommandResult.Error("[VintageHorizons] use: .vhgpu off | on | verify");

                SaveConfig();
                bool off = mode.Equals("off", StringComparison.OrdinalIgnoreCase);
                return TextCommandResult.Success(off
                    ? "[VintageHorizons] GPU measurement shadow switching off; buffers released next frame."
                    : "[VintageHorizons] GPU measurement shadow switching on"
                        + (mode.Equals("verify", StringComparison.OrdinalIgnoreCase)
                            ? " with content verification" : "")
                        + ". Live sections are re-meshed into it first, so wait a few seconds, "
                        + "then run .vhgpu again for the numbers.");
            });

        // The first switch in the mod that changes where a cached-terrain pixel comes
        // from: with it on, opaque terrain is drawn out of the regional arenas with a few
        // multi-draws instead of one call per section. Off by default, session-only, and
        // it needs .vhgpu on first, because the arenas are what it draws from.
        capi.ChatCommands.Create("vhindirect")
            .WithDescription("Draw distant terrain in a few big batches instead of one call each. Needs .vhgpu on. Off by default; remembered between sessions.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[VintageHorizons] no renderer: another LOD mod is drawing.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        "[VintageHorizons] batched terrain drawing " + renderer.DescribeIndirectDraw());

                renderer.IndirectDrawEnabled = (bool)args[0];
                SaveConfig();
                return TextCommandResult.Success(
                    "[VintageHorizons] batched terrain drawing " + renderer.DescribeIndirectDraw()
                    + " Compare it against off in the same spot: the picture should be "
                    + "identical, and anything that differs is a bug worth reporting.");
            });

        // Phase 7 keeps the accepted expanded batching path beside the packed one for a
        // controlled A/B. This switch is deliberately session-only until the shader-pull
        // path has passed a real-world visual and timing gate on representative hardware.
        capi.ChatCommands.Create("vhpacked")
            .WithDescription("Use 12-byte opaque quad records for batched distant terrain. Needs .vhgpu on and .vhindirect on. Experimental and session-only.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[VintageHorizons] no renderer: another LOD mod is drawing.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        "[VintageHorizons] packed opaque drawing " + renderer.DescribePackedDraw());

                renderer.PackedDrawEnabled = (bool)args[0];
                return TextCommandResult.Success(
                    "[VintageHorizons] packed opaque drawing " + renderer.DescribePackedDraw()
                    + " This switch is not saved yet. Compare the same view against off; "
                    + "the picture must remain identical.");
            });

        // Phase 8 changes the HZB draw unit from one section box to a moderate 4x4 grid
        // of exact packed ranges. It remains session-only so its metadata/command cost can
        // be measured against the accepted whole-section path before any default decision.
        capi.ChatCommands.Create("vhclusters")
            .WithDescription("Split packed distant terrain into 4x4 depth-culling clusters. Needs .vhgpu, .vhindirect and .vhpacked. Experimental and session-only.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[VintageHorizons] no renderer: another LOD mod is drawing.");
                if (args.Parsers[0].IsMissing)
                {
                    string status = renderer.DescribeClusterDraw();
                    LogReportLines("clusters", status);
                    return TextCommandResult.Success(
                        "[VintageHorizons] clustered opaque drawing " + status);
                }

                renderer.ClusterDrawEnabled = (bool)args[0];
                string changed = renderer.DescribeClusterDraw();
                LogReportLines("clusters", changed);
                return TextCommandResult.Success(
                    "[VintageHorizons] clustered opaque drawing " + changed
                    + " This switch is not saved. Compare the same view against off; "
                    + "terrain must never disappear or change shape.");
            });

        // The first switch in this mod that can take terrain OFF the screen rather than
        // change how it gets there. Off by default, not saved, and it says plainly what to
        // look for - because the only failure that matters here is terrain that should be
        // visible and is not, and no counter can see that.
        capi.ChatCommands.Create("vhcull")
            .WithDescription("Let the depth test stop distant terrain being drawn. Needs .vhgpu on and .vhindirect on; turning it on also switches on the depth pyramid. Off by default; remembered between sessions.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[VintageHorizons] no renderer: another LOD mod is drawing.");
                // Logged as well as shown, exactly like .vhhzb. Game chat cannot be copied
                // out, so a status that exists only on screen cannot be handed to anybody -
                // and this is the line that says whether a playtest was testing the thing it
                // was meant to. A run where nobody can tell afterwards is a wasted run.
                if (args.Parsers[0].IsMissing)
                {
                    string status = renderer.DescribeGpuCull();
                    LogReportLines("cull", status);
                    return TextCommandResult.Success("[VintageHorizons] depth culling " + status);
                }

                bool wanted = (bool)args[0];
                renderer.GpuCullEnabled = wanted;
                renderer.ResetDepthPyramidInterval();

                // Culling has no meaning without a pyramid to test against, and asking
                // someone to know that is how a switch ends up on with nothing under it -
                // which is exactly what happened on 0.3.69 and cost a whole playtest. Turning
                // it ON brings the pyramid with it; turning it off leaves the pyramid alone,
                // because the pyramid is also a measurement someone may want on its own.
                if (wanted && !renderer.DepthPyramidEnabled) renderer.DepthPyramidEnabled = true;

                SaveConfig();
                LogReportLines("cull", renderer.DescribeGpuCull());
                return TextCommandResult.Success(
                    "[VintageHorizons] depth culling " + renderer.DescribeGpuCull()
                    + " Look for terrain that should be there and is not, especially while "
                    + "turning: that is the failure this can cause and the only one worth "
                    + "reporting. Turn it off in the same spot to compare.");
            });

        // The Phase 6 switch, and the reason it is a switch rather than a second build: the
        // question it settles is a comparison, and a comparison whose two halves are different
        // builds is one nobody can run while looking at the same hillside.
        capi.ChatCommands.Create("vhlate")
            .WithDescription("Split cached terrain near/far and take a fresh depth picture between them, so cached hills can hide farther terrain in the same frame. Needs .vhcull on. Off by default; remembered between sessions.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[VintageHorizons] no renderer: another LOD mod is drawing.");

                if (args.Parsers[0].IsMissing)
                {
                    string status = renderer.DescribeLateDepthPyramid();
                    LogReportLines("late", status);
                    return TextCommandResult.Success("[VintageHorizons] depth picture " + status);
                }

                renderer.LateDepthPyramid = (bool)args[0];

                // Always, not only when the value changed. Someone typing the command is
                // starting a measurement, and the commonest way to run this comparison is to
                // set the arrangement, play, read, set it again. If setting it to what it
                // already is left the counters running, that second reading would cover both
                // halves and look entirely reasonable.
                renderer.ResetDepthPyramidInterval();

                // Same coupling as .vhcull on, and for the same reason: adding a second
                // picture does nothing at all unless something reads it, and a switch
                // sitting on with nothing underneath it is how a playtest gets spent measuring
                // an instrument (G72).
                if (renderer.LateDepthPyramid && !renderer.DepthPyramidEnabled)
                    renderer.DepthPyramidEnabled = true;

                SaveConfig();
                LogReportLines("late", renderer.DescribeLateDepthPyramid());
                return TextCommandResult.Success(
                    "[VintageHorizons] depth picture " + renderer.DescribeLateDepthPyramid()
                    + (renderer.LateDepthPyramid
                        ? " Watch the frame-time graph for new hitches and compare the same "
                          + "view with it off; every verdict is now from the current frame."
                        : ""));
            });

        capi.ChatCommands.Create("vhskip")
            .WithDescription("While the chunk mask is on: allow dropping a cached piece entirely when the game covers all of it. On by default.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[VintageHorizons] no renderer: another LOD mod is drawing.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        $"[VintageHorizons] whole-piece skipping {(renderer.WholeMeshSkip ? "on" : "off")}. " +
                        "Off keeps the per-pixel mask working but never drops a whole cached piece.");

                renderer.WholeMeshSkip = (bool)args[0];
                return TextCommandResult.Success(
                    $"[VintageHorizons] whole-piece skipping {(renderer.WholeMeshSkip ? "on" : "off")}. " +
                    "Applies on the next frame.");
            });

        capi.ChatCommands.Create("vhbackface")
            .WithDescription("Reject back-facing cached opaque terrain. On by default and not saved.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[VintageHorizons] no renderer: another LOD mod is drawing.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        $"[VintageHorizons] opaque back-face culling {(renderer.OpaqueBackfaceCulling ? "on" : "off")} (on by default, not saved).");

                renderer.OpaqueBackfaceCulling = (bool)args[0];
                return TextCommandResult.Success(
                    $"[VintageHorizons] opaque back-face culling {(renderer.OpaqueBackfaceCulling ? "on" : "off")} (on by default, not saved). " +
                    "Applies on the next frame; water and thin cover remain two-sided.");
            });

        // The one switch that makes this phase's gate answerable by looking: with it off,
        // sections are bounded bedrock-to-sky exactly as before, so anything that vanishes
        // while looking up or down comes straight back.
        // Phase 4 shadow work. It builds the depth pyramid and measures it, and hides
        // nothing at all - so the only thing to look for while it is on is whether the
        // frame rate moves, which is the number that decides whether the phase continues.
        capi.ChatCommands.Create("vhhzb")
            .WithDescription("Build the depth pyramid used to find terrain hidden behind hills. Measurement only; hides nothing. Off by default and not saved. `.vhhzb why` explains the piece you are looking at.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[VintageHorizons] no renderer: another LOD mod is drawing.");
                if (args.Parsers[0].IsMissing)
                {
                    // Logged as well as shown. Game chat cannot be copied out, so a report
                    // that exists only on screen has to be transcribed by hand or
                    // photographed - and these are long lines of digits, which is the worst
                    // possible thing to ask someone to retype. The log is the copy that can
                    // actually be handed to somebody.
                    string report = renderer.ReportDepthPyramid();
                    LogReportLines("hzb", report);
                    return TextCommandResult.Success("[VintageHorizons] " + report);
                }

                string word = ((string)args[0]).ToLowerInvariant();

                // Look at a piece of far terrain and ask about that one. A counter says how
                // often something happens; only this can be checked against what is on the
                // screen in front of the person running it.
                if (word == "why")
                {
                    Vec3f look = capi.World.Player.Entity.Pos.GetViewVector();
                    var camera = capi.World.Player.Entity.CameraPos;
                    int range = GameMath.Clamp((int)renderer.EffectiveFarDistance, 512, 32768);
                    string why = renderer.ExplainDepthPyramid(
                        camera.X, camera.Y, camera.Z, look.X, look.Y, look.Z, range);
                    LogReportLines("hzb why", why);
                    return TextCommandResult.Success("[VintageHorizons] " + why);
                }

                if (word != "on" && word != "off")
                    return TextCommandResult.Error("[VintageHorizons] use: .vhhzb on | off | why");

                renderer.DepthPyramidEnabled = word == "on";
                renderer.ResetDepthPyramidInterval();

                return TextCommandResult.Success(
                    $"[VintageHorizons] depth pyramid {(renderer.DepthPyramidEnabled ? "on" : "off")} "
                    + "(off by default, not saved). It copies the depth buffer and reduces it every "
                    + "frame, and nothing reads the result yet. Turn it on, play, and read the "
                    + "hzb line in the log: the question is what it costs, not what it hides. "
                    + "GPU timing " + renderer.DescribeGpuTiming() + ".");
            });

        capi.ChatCommands.Create("vhheight")
            .WithDescription("Cull cached sections by how tall their terrain actually is. On by default and not saved.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[VintageHorizons] no renderer: another LOD mod is drawing.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        $"[VintageHorizons] section height culling {(renderer.SectionHeightCulling ? "on" : "off")} "
                        + $"(on by default, not saved). {renderer.SectionHeights.Describe(renderer.WorldHeight)}");

                renderer.SectionHeightCulling = (bool)args[0];
                return TextCommandResult.Success(
                    $"[VintageHorizons] section height culling {(renderer.SectionHeightCulling ? "on" : "off")} "
                    + "(on by default, not saved). Applies on the next frame. With it off, every section is "
                    + "bounded from bedrock to sky again, which is what the renderer did before.");
            });

        capi.ChatCommands.Create("vhfront")
            .WithDescription("Submit cached opaque terrain front-to-back. On by default and not saved.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[VintageHorizons] no renderer: another LOD mod is drawing.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        $"[VintageHorizons] opaque front-to-back submission {(renderer.OpaqueFrontToBack ? "on" : "off")} (on by default, not saved).");

                renderer.OpaqueFrontToBack = (bool)args[0];
                return TextCommandResult.Success(
                    $"[VintageHorizons] opaque front-to-back submission {(renderer.OpaqueFrontToBack ? "on" : "off")} (on by default, not saved). " +
                    "Applies on the next frame; water keeps its existing order.");
            });

        capi.ChatCommands.Create("vhocclusion")
            .WithDescription("Draw cached terrain after vanilla so ordinary depth testing rejects hidden pixels. On by default.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[VintageHorizons] no renderer: another LOD mod is drawing.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        $"[VintageHorizons] post-vanilla depth culling {renderer.DescribeOcclusionCulling()}. " +
                        "On by default and not saved.");

                renderer.OcclusionCullingEnabled = (bool)args[0];
                return TextCommandResult.Success(
                    $"[VintageHorizons] post-vanilla depth culling {renderer.DescribeOcclusionCulling()}. " +
                    "Applies on the next frame; on by default and not saved.");
            });

        capi.ChatCommands.Create("vhtemporal")
            .WithDescription("Skip opaque cached meshes proven hidden on later frames. Aggressive profile on by default.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[VintageHorizons] no renderer: another LOD mod is drawing.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        $"[VintageHorizons] delayed exact-geometry occlusion {renderer.DescribeTemporalOcclusion()}. " +
                        "Aggressive profile on by default; not saved.");

                renderer.TemporalOcclusionEnabled = (bool)args[0];
                return TextCommandResult.Success(
                    $"[VintageHorizons] delayed exact-geometry occlusion {renderer.DescribeTemporalOcclusion()}. " +
                    "Applies on the next frame; aggressive profile is the default. Use it with .vhocclusion on. " +
                    "Tune live with .vhtemporalprofile safe|aggressive|extreme.");
            });

        capi.ChatCommands.Create("vhtemporalprofile")
            .WithDescription("Tune delayed occlusion: safe invalidates on turns; aggressive/extreme persist while turning.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWordRange(
                "profile", ["safe", "aggressive", "extreme"]))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[VintageHorizons] no renderer: another LOD mod is drawing.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        $"[VintageHorizons] delayed occlusion profile: {renderer.TemporalOcclusionProfileName}. " +
                        "Safe invalidates on turns; aggressive persists through turns and rechecks quickly; " +
                        "extreme persists through all camera motion. Changes are not saved.");

                string profile = (string)args[0];
                if (!renderer.SetTemporalOcclusionProfile(profile))
                    return TextCommandResult.Error(
                        "[VintageHorizons] profile must be safe, aggressive, or extreme.");
                return TextCommandResult.Success(
                    $"[VintageHorizons] delayed occlusion profile: {renderer.TemporalOcclusionProfileName}. " +
                    "Previous visibility was invalidated; the new profile applies immediately.");
            });

        capi.ChatCommands.Create("vhholes")
            .WithDescription("Find ground the mod has handed to the game that the game is not drawing. No need to look at anything.")
            .HandleWith(_ =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[VintageHorizons] no renderer: another LOD mod is drawing.");

                var at = capi.World.Player.Entity.Pos;
                return TextCommandResult.Success(
                    "[VintageHorizons] " + renderer.ExplainOwnedButNotDrawn(at.X, at.Z, 4));
            });

        capi.ChatCommands.Create("vhgeom")
            .WithDescription("While the chunk mask is on: ignore ground the game claims but holds no terrain for. On by default.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[VintageHorizons] no renderer: another LOD mod is drawing.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        $"[VintageHorizons] geometry rule {(renderer.GeometryOwnershipRule ? "on" : "off")}. " +
                        "On means a chunk the game says it drew, but holds no terrain in, does not " +
                        "hide cached terrain. Only has an effect with .vhmask on.");

                renderer.GeometryOwnershipRule = (bool)args[0];
                return TextCommandResult.Success(
                    $"[VintageHorizons] geometry rule {(renderer.GeometryOwnershipRule ? "on" : "off")}. " +
                    "The change applies on the next frame; ownership settles over the next second or two.");
            });

        capi.ChatCommands.Create("vhdetail")
            .WithDescription("Reset all LOD thresholds to a doubling sequence beginning at this distance")
            .WithArgs(capi.ChatCommands.Parsers.OptionalInt("blocks"))
            .HandleWith(args =>
            {
                if (args.Parsers[0].IsMissing)
                {
                    return TextCommandResult.Success(
                        $"[VintageHorizons] LOD thresholds {string.Join(", ", LodWorld.GetLevelThresholds())}. " +
                        $".vhdetail resets them to a doubling sequence; .vhconfig edits them individually. " +
                        $"Set between {(int)LodWorld.MinDetailDistance} and {(int)LodWorld.MaxDetailDistance}.");
                }

                LodWorld.DetailDistance = GameMath.Clamp((int)args[0],
                    (int)LodWorld.MinDetailDistance, (int)LodWorld.MaxDetailDistance);
                SaveConfig();
                return TextCommandResult.Success(
                    $"[VintageHorizons] LOD thresholds reset to " +
                    $"{string.Join(", ", LodWorld.GetLevelThresholds())} (saved). " +
                    "Terrain re-selects over the next few seconds.");
            });
    }

    void ApplyGuiConfig(int[] thresholds, int farViewDistanceCap)
    {
        int[] normalized = LodWorld.NormalizeLevelThresholds(thresholds, config.DetailDistance);
        config.LodThresholds = normalized;
        config.DetailDistance = normalized[0];
        config.FarViewDistanceCap = farViewDistanceCap;

        if (deferringTo == null)
        {
            LodWorld.SetLevelThresholds(normalized);
            renderer.FarViewDistanceCap = farViewDistanceCap;
        }

        SaveConfig();
    }

    /// <summary>Writes every setting: a partial write would silently reset the others.</summary>
    void SaveConfig()
    {
        // The renderer and the LodWorld statics do not exist while we defer, and what was
        // loaded from the file is still the right thing to write back.
        if (deferringTo == null)
        {
            config.FarViewDistanceCap = renderer.FarViewDistanceCap;
            config.DetailDistance = (int)LodWorld.DetailDistance;
            config.LodThresholds = LodWorld.GetLevelThresholds();
            // The request, never the effective state: a mask that failed this session
            // reports itself disabled, and writing that would turn it off permanently.
            config.ChunkMask = renderer.ChunkMaskRequested;

            // The requested state, never the effective one, for the same reason the mask
            // uses the request: a path that refused to start this session reports itself
            // off, and writing that back would turn it off permanently on a machine where
            // the next driver update might have fixed it.
            config.GpuArenas = renderer.GpuShadowRequested;
            config.IndirectDraw = renderer.IndirectDrawEnabled;
            config.DepthCull = renderer.GpuCullEnabled;
            config.LateDepthPicture = renderer.LateDepthPyramid;
        }

        capi.StoreModConfig(config, "vintagehorizons.json");
    }

    public override void Dispose()
    {
        if (capi == null) return;

        // The engine runs this on whichever thread is tearing the game down, and on the
        // vanilla shutdown crash path ("Can't use a disposed shader" out of a render
        // stage) that is not the main thread. Every engine call below refuses to run off
        // it, and an exception that escapes one step skips every step behind it. Two
        // rounds of this were needed to learn the general rule: the first version lost
        // the storage writer's shutdown, and the second, which guarded only the events,
        // lost the renderer's. So each step stands alone and none of them propagates.
        Quietly(() =>
        {
            capi.Event.ChunkDirty -= OnChunkDirty;
            capi.Event.LevelFinalize -= OnLevelFinalize;
            capi.Event.LevelFinalize -= RegisterAutoCommand;
            capi.Event.LeaveWorld -= OnLeaveWorld;

            // Nothing to unregister while deferring: that path registers no listener.
            if (deferringTo == null) capi.Event.UnregisterGameTickListener(tickListenerId);
        });

        // The sibling-cache reader owns a background connection and must stop before
        // process teardown even if the engine skipped the ordinary LeaveWorld event.
        Quietly(() =>
        {
            localOffers?.Dispose();
            localOffers = null;
        });

        // Stops the storage writer before the connection it writes through.
        Quietly(() => pipeline?.Dispose());
        Quietly(() => renderer?.Dispose());
        Quietly(() => configDialog?.Dispose());
    }

    /// <summary>
    /// One shutdown step, and whatever it throws stays here. Only ever called from
    /// <see cref="Dispose"/>, where the game is already going down and the alternative is
    /// abandoning the steps behind this one.
    /// </summary>
    void Quietly(Action step)
    {
        try
        {
            step();
        }
        catch (Exception e)
        {
            Mod.Logger.Debug("A shutdown step was refused, which changes nothing by now: {0}",
                e.Message);
        }
    }
}
