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

    /// <summary>Distance at which detail starts halving; see LodWorld.DetailDistance.</summary>
    public int DetailDistance = 512;

    /// <summary>
    /// Draw even when another LOD mod is installed and switched on. The escape hatch for
    /// the mods whose own switch we cannot read; see <see cref="OtherLodMods"/>. Read at
    /// startup only, because turning the mod on mid-session is not something the startup
    /// path supports.
    /// </summary>
    public bool IgnoreOtherLodMods = false;
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

        LodWorld.DetailDistance = GameMath.Clamp(config.DetailDistance,
            (int)LodWorld.MinDetailDistance, (int)LodWorld.MaxDetailDistance);

        pipeline = new LodPipeline(capi, Mod.Logger, DescribePalette, block => (byte)tints.SlotFor(block));
        pipeline.TrackPhaseAllocations = allocationTelemetryEnabled;

        // Repairs a cache written while a block lookup was poisoned, which saved sections
        // with no palette colour at all and drew them as black ground. Client-side only:
        // it needs the texture atlas, and a server stores 0 on purpose.
        pipeline.RepairUncoloredPalette = section => LodPaletteRepair.Fill(section, AtlasColorOf);
        pipeline.RecolorForeignSection = RecolorForeignSection;
        renderer = new LodTerrainRenderer(capi, pipeline.World, pipeline.Worker, tints)
        {
            AutoUnpause = Environment.GetEnvironmentVariable("VINTAGEHORIZONS_AUTOUNPAUSE") == "1",
            TrackPhaseAllocations = allocationTelemetryEnabled,
            FarViewDistanceCap = config.FarViewDistanceCap,
        };

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
    /// transport differs, and there is no in-flight cap because a local file read has no
    /// round trip to protect. The budget per tick is there so a sweep of ten thousand
    /// sections does not try to install all of them in one frame.
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
            if (!loggedLocalOffers)
            {
                loggedLocalOffers = true;
                // Sweeps and /vhgen both fill the sibling cache; this line covers either.
                Mod.Logger.Notification(
                    "Server-side cache offers sections locally. The mod adopts them as "
                    + "the view needs them.");
            }
        }

        long[] wanted = pipeline.RemoteWanted();
        if (wanted.Length == 0) return;

        if (wanted.Length > 1)
        {
            var at = capi.World.Player.Entity.Pos;
            double px = at.X, pz = at.Z;
            Array.Sort(wanted, (a, b) =>
                LodWorld.NearestDistanceSqTo(a, px, pz).CompareTo(LodWorld.NearestDistanceSqTo(b, px, pz)));
        }

        var installBudget = new LodDrainBudget();
        int itemLimit = Math.Min(wanted.Length, LocalOffersPerTick);
        for (int i = 0; i < itemLimit && installBudget.TryStart(0); i++)
        {
            // Keep sibling-cache work within its own bounded decoder allowance. Server
            // assist has a separate reservation, so local adoption cannot crowd it out.
            if (!pipeline.CanQueueForeignBlob(LodForeignSource.LocalOffer)) break;

            long key = wanted[i];

            byte[]? blob = localOffers.Blob(key);
            installBudget.AddBytes(blob?.LongLength ?? 0);
            // A miss is ordinary while the sweep is still running: the key was listed but
            // its row is not written yet. MarkRemoteUnavailable is permanent, so it must
            // not be used for "not yet". It also must not enter the taken batch: no source
            // accepted responsibility, so forgetting it here would strand the key in
            // LodWorld.LoadsInFlight and make a later row permanently invisible.
            if (blob == null || blob.Length == 0)
            {
                pipeline.CompleteLocalOffer(key, LodLocalOfferOutcome.RetryableMiss);
                continue;
            }

            LodForeignQueueOutcome queued = pipeline.QueueForeignBlob(
                key, blob, LodForeignSource.LocalOffer);
            if (queued == LodForeignQueueOutcome.Queued)
            {
                pipeline.MarkLocalOfferAccepted(key);
            }
            else if (queued == LodForeignQueueOutcome.Unavailable)
            {
                // Local data already won before submission, or persistence is gone.
                pipeline.CompleteLocalOffer(key, LodLocalOfferOutcome.Unavailable);
            }
            else break;
        }

        localOfferItemsProcessed += installBudget.Items;
        localOfferBytesProcessed += installBudget.Bytes;
    }

    /// <summary>
    /// Compressed sibling-cache blobs transferred to the decoder per tick. The worker
    /// performs inflation and structural parsing; owning-thread publication has its own
    /// elapsed-time and decoded-byte budget in LodPipeline.
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

            Block block = capi.World.Blocks[entry.BlockId];
            int subId = block.TextureSubIdForBlockColor;
            int color = IsUsableAtlasTexture(subId)
                ? capi.BlockTextureAtlas.GetAverageColor(subId)
                : ColorFromAnyTexture(block, ColorUtil.WhiteArgb);

            // A colour texture that resolved to the unknown.png placeholder is not a
            // colour, it is the absence of one. Chiselled blocks land here: their colour
            // lives in a block entity this path has no position to read (the section came
            // from a server or a peek), so the honest answer is the same neutral grey an
            // unknown block gets - not the placeholder's near-white.
            if (unknownTextureColor != 0 && color == unknownTextureColor)
            {
                color = ColorFromAnyTexture(block, LodPaletteRepair.UnknownBlockColor);
            }
            entry.Color = color;
            section.Palette[i] = entry;
        }
    }

    /// <summary>
    /// The client half of palette registration: the untinted average colour from the
    /// texture atlas, plus which live tint applies. Stored untinted on purpose, so the
    /// shader can follow the calendar instead of freezing the season it was captured in.
    /// A server has no atlas and cannot answer this at all (DESIGN.md §10.4).
    ///
    /// The position is the exact block being described, and that is load-bearing:
    /// GetColorWithoutTint reads the world at pos - chiselled blocks average the
    /// materials in their block entity there, the same way the world map colours them.
    /// A synthetic chunk-centre position made that lookup miss, so every chisel took
    /// the placeholder texture's near-white instead of its own materials.
    /// </summary>
    (int Color, byte TintSlot) DescribePalette(int blockId, int blockX, int blockY, int blockZ)
    {
        Block block = capi.World.Blocks[blockId];
        paletteSamplePos.Set(blockX, blockY, blockZ);
        int color = block.GetColorWithoutTint(capi, paletteSamplePos);

        if (!IsUsableAtlasTexture(block.TextureSubIdForBlockColor)
            // Guarded on non-zero: if the atlas never populated AvgColor we would be
            // comparing against 0 and "fixing" every legitimately black block.
            || (unknownTextureColor != 0 && color == unknownTextureColor))
        {
            color = ColorFromAnyTexture(block, color);
        }
        return (color, (byte)tints.SlotFor(block));
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
    int AtlasColorOf(int blockId)
    {
        if (blockId <= 0) return LodPaletteRepair.UnknownBlockColor;

        Block block = capi.World.Blocks[blockId];
        int subId = block.TextureSubIdForBlockColor;
        int color = IsUsableAtlasTexture(subId)
            ? capi.BlockTextureAtlas.GetAverageColor(subId)
            : ColorFromAnyTexture(block, ColorUtil.WhiteArgb);

        // Same rule as the foreign path: the placeholder's average is not a colour.
        // The repair has only a block id to go on, so a chiselled block repairs to
        // neutral grey here and gets its real materials on the next capture.
        if (unknownTextureColor != 0 && color == unknownTextureColor)
        {
            color = ColorFromAnyTexture(block, LodPaletteRepair.UnknownBlockColor);
        }
        return color;
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
    static readonly int[] FillInMilestones = { 100, 300, 600, 1200 };
    int nextMilestone;

    void ReportFillIn()
    {
        while (nextMilestone < FillInMilestones.Length && renderer.MeshCount >= FillInMilestones[nextMilestone])
        {
            Mod.Logger.Notification("Fill-in: {0} meshes after {1:0.0}s",
                FillInMilestones[nextMilestone], joinClock.Elapsed.TotalSeconds);
            nextMilestone++;
        }
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
            "{0}: {1} sections resident [{2}] ({3} RAM-evicted, {4} from cache), {5} meshes ({6} evicted), " +
            "{7} selected [{8}] minus {9} frustum-culled, {10} columns captured, {11} pending, " +
            "worker: {12} captures / {13} meshes / {14} mips queued / {15}+{16}+{17} errors, " +
            "{18} awaiting mip ({19} in flight), {20} render-dirty, {21} unsaved",
            prefix, world.Sections.Count, world.DescribeLevels(), world.EvictedSectionsTotal, pipeline.CachedSectionsLoaded,
            renderer.MeshCount, renderer.EvictedTotal, renderer.LastDrawCount, renderer.DescribeDrawnLevels(),
            renderer.LastCulledCount, pipeline.ColumnsCaptured, pipeline.PendingColumns,
            worker.PendingCaptures, worker.PendingMeshes, worker.PendingMips,
            worker.CaptureErrors, worker.MeshErrors, worker.MipErrors,
            world.MipDirty.Count, world.MipInFlightCount, world.RenderDirty.Count, world.SaveDirty.Count);

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
                "far distance {3:0.0}/{4:0.0} | quadtree walk {5:0.0}/{6:0.0} | draw submit {7:0.0}/{8:0.0}",
                renderer.WalkCost.Calls,
                renderer.ScheduleCost.AvgUs, renderer.ScheduleCost.MaxUs,
                renderer.FarDistanceCost.AvgUs, renderer.FarDistanceCost.MaxUs,
                renderer.WalkCost.AvgUs, renderer.WalkCost.MaxUs,
                renderer.DrawCost.AvgUs, renderer.DrawCost.MaxUs,
                renderer.PruneCost.AvgUs, renderer.PruneCost.MaxUs);

            Mod.Logger.Notification(
                "  render p95/p99/max us: prune {0:0}/{1:0}/{2:0} | schedule {3:0}/{4:0}/{5:0} | "
                + "upload {6:0}/{7:0}/{8:0} | evict {9:0}/{10:0}/{11:0} | seasonal {12:0}/{13:0}/{14:0} | "
                + "far {15:0}/{16:0}/{17:0} | walk {18:0}/{19:0}/{20:0} | draw {21:0}/{22:0}/{23:0}",
                renderer.PruneCost.P95Us, renderer.PruneCost.P99Us, renderer.PruneCost.MaxUs,
                renderer.ScheduleCost.P95Us, renderer.ScheduleCost.P99Us, renderer.ScheduleCost.MaxUs,
                renderer.UploadCost.P95Us, renderer.UploadCost.P99Us, renderer.UploadCost.MaxUs,
                renderer.EvictCost.P95Us, renderer.EvictCost.P99Us, renderer.EvictCost.MaxUs,
                renderer.SeasonalCost.P95Us, renderer.SeasonalCost.P99Us, renderer.SeasonalCost.MaxUs,
                renderer.FarDistanceCost.P95Us, renderer.FarDistanceCost.P99Us, renderer.FarDistanceCost.MaxUs,
                renderer.WalkCost.P95Us, renderer.WalkCost.P99Us, renderer.WalkCost.MaxUs,
                renderer.DrawCost.P95Us, renderer.DrawCost.P99Us, renderer.DrawCost.MaxUs);

            Mod.Logger.Notification(
                "  render interval: {0} projection resets, {1:0.00} MiB uploaded; phase hitches >=25/50/100ms: {2}/{3}/{4}",
                renderer.ProjectionResetCount, renderer.MeshUploadBytes / (1024.0 * 1024.0),
                renderer.PruneCost.Over25Ms + renderer.ScheduleCost.Over25Ms + renderer.UploadCost.Over25Ms
                    + renderer.EvictCost.Over25Ms + renderer.SeasonalCost.Over25Ms + renderer.FarDistanceCost.Over25Ms
                    + renderer.WalkCost.Over25Ms + renderer.DrawCost.Over25Ms,
                renderer.PruneCost.Over50Ms + renderer.ScheduleCost.Over50Ms + renderer.UploadCost.Over50Ms
                    + renderer.EvictCost.Over50Ms + renderer.SeasonalCost.Over50Ms + renderer.FarDistanceCost.Over50Ms
                    + renderer.WalkCost.Over50Ms + renderer.DrawCost.Over50Ms,
                renderer.PruneCost.Over100Ms + renderer.ScheduleCost.Over100Ms + renderer.UploadCost.Over100Ms
                    + renderer.EvictCost.Over100Ms + renderer.SeasonalCost.Over100Ms + renderer.FarDistanceCost.Over100Ms
                    + renderer.WalkCost.Over100Ms + renderer.DrawCost.Over100Ms);

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
            + "far {10:0.00}/{11:0.0} | walk {12:0.00}/{13:0.0} | draw {14:0.00}/{15:0.0}",
            MiB(renderer.PruneCost.AllocatedBytes), KiB(renderer.PruneCost.MaxAllocatedBytes),
            MiB(renderer.ScheduleCost.AllocatedBytes), KiB(renderer.ScheduleCost.MaxAllocatedBytes),
            MiB(renderer.UploadCost.AllocatedBytes), KiB(renderer.UploadCost.MaxAllocatedBytes),
            MiB(renderer.EvictCost.AllocatedBytes), KiB(renderer.EvictCost.MaxAllocatedBytes),
            MiB(renderer.SeasonalCost.AllocatedBytes), KiB(renderer.SeasonalCost.MaxAllocatedBytes),
            MiB(renderer.FarDistanceCost.AllocatedBytes), KiB(renderer.FarDistanceCost.MaxAllocatedBytes),
            MiB(renderer.WalkCost.AllocatedBytes), KiB(renderer.WalkCost.MaxAllocatedBytes),
            MiB(renderer.DrawCost.AllocatedBytes), KiB(renderer.DrawCost.MaxAllocatedBytes));
    }

    void OnLeaveWorld()
    {
        assist?.Reset();
        // Belongs to the world being left: the next one is a different savegame with a
        // different sibling cache, and holding this open would keep a file handle on a
        // database the server side may want to delete or replace.
        localOffers?.Dispose();
        localOffers = null;
        loggedLocalOffers = false;
        localOfferProbeTicks = 0;
        pipeline.Close();
        while (pipeline.Worker.MeshResults.TryDequeue(out _)) { }
        renderer.ClearMeshes();
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
                $"drawn: {renderer.LastDrawCount} [{renderer.DescribeDrawnLevels()}], " +
                $"columns captured: {pipeline.ColumnsCaptured}, pending: {pipeline.PendingColumns}, " +
                $"worker: {pipeline.Worker.PendingCaptures}c/{pipeline.Worker.PendingMeshes}m/{pipeline.Worker.PendingMips}p, " +
                $"awaiting mip: {pipeline.World.MipDirty.Count} ({pipeline.World.MipInFlightCount} in flight), " +
                $"unsaved: {pipeline.World.SaveDirty.Count}, persistence: {(pipeline.Persisting ? "on" : "off")}, " +
                $"render distance: {(renderer.FarViewDistanceCap > 0 ? renderer.FarViewDistanceCap + " (capped)" : "unlimited")}, " +
                $"current far edge: {(int)renderer.EffectiveFarDistance}, " +
                $"detail distance: {(int)LodWorld.DetailDistance} (.vhdetail to change), " +
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

        // The remaining commands drive the renderer, which does not exist when we are
        // deferring to another LOD mod.
        if (deferringTo != null) return;

        capi.ChatCommands.Create("vhwhy")
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

        capi.ChatCommands.Create("vhdetail")
            .WithDescription("Distance in blocks before LOD detail starts to halve. Default 512. A higher value gives sharper far terrain and costs more VRAM and CPU.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalInt("blocks"))
            .HandleWith(args =>
            {
                if (args.Parsers[0].IsMissing)
                {
                    return TextCommandResult.Success(
                        $"[VintageHorizons] detail distance {(int)LodWorld.DetailDistance} " +
                        $"(full 1-block detail out to {(int)LodWorld.DetailDistance * 2} blocks). " +
                        $"Set between {(int)LodWorld.MinDetailDistance} and {(int)LodWorld.MaxDetailDistance}.");
                }

                LodWorld.DetailDistance = GameMath.Clamp((int)args[0],
                    (int)LodWorld.MinDetailDistance, (int)LodWorld.MaxDetailDistance);
                SaveConfig();
                return TextCommandResult.Success(
                    $"[VintageHorizons] detail distance {(int)LodWorld.DetailDistance} - full detail out to " +
                    $"{(int)LodWorld.DetailDistance * 2} blocks (saved). Terrain re-selects over the next few seconds.");
            });
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
