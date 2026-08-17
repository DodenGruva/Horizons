using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace VintageHorizons.Net;

/// <summary>
/// Server half of the optional assist (DESIGN.md §10). Stage 1 answers the handshake and
/// nothing else, which is the point: it proves the mod can go Universal without changing
/// anything for anyone, before any terrain is put on the wire.
///
/// This is a separate ModSystem rather than a branch inside the client one. The client
/// system casts World to ClientMain, compiles shaders and registers a renderer; the
/// robust guarantee that none of that runs on a server is that the code is not there,
/// not a side check that one refactor could get wrong.
/// </summary>
public class LodAssistServerSystem : ModSystem
{
    ICoreServerAPI sapi = null!;
    IServerNetworkChannel channel = null!;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        channel = api.Network.RegisterChannel(LodAssist.ChannelName)
            .RegisterMessageType<AssistHello>()
            .RegisterMessageType<AssistWelcome>()
            .RegisterMessageType<AssistKeyManifest>()
            .RegisterMessageType<AssistSectionRequest>()
            .RegisterMessageType<AssistSection>()
            .SetMessageHandler<AssistHello>(OnHello)
            .SetMessageHandler<AssistSectionRequest>(OnSectionRequest);

        // Accumulate the configured per-second rates across normal ticks. Each allowance
        // is capped at one tick's share, so a slow tick cannot cause a catch-up burst.
        api.Event.RegisterGameTickListener(_ => ServePending(), LodTickAllowance.TickMilliseconds);

        // Every five seconds, which is slow next to the serve loop on purpose: this walks
        // the whole key snapshot per player, and the thing it is chasing (a pregen, a
        // sweep, another player exploring) takes minutes, not milliseconds.
        api.Event.RegisterGameTickListener(_ => OfferNewKeys(), 5000);

        api.Event.PlayerDisconnect += OnPlayerDisconnect;

        api.ChatCommands.Create("vhserver")
            .WithDescription("VintageHorizons server assist status")
            .RequiresPrivilege(Privilege.controlserver)
            .HandleWith(_ =>
            {
                LodServerCaptureSystem? capture = api.ModLoader.GetModSystem<LodServerCaptureSystem>();
                LodServerConfig config = capture?.Config ?? new LodServerConfig();
                return TextCommandResult.Success(
                    $"[VintageHorizons] {config.Describe()}. Cache: {capture?.SectionCount ?? 0} sections, "
                    + $"{capture?.ColumnsCaptured ?? 0} columns captured. Served {sectionsServed} sections "
                    + $"({bytesServed / 1e6:0.0} MB, {(sectionsServed > 0 ? blobReadMs / sectionsServed : 0):0.00}ms avg read), "
                    + $"{sectionsOutsideRadius} refused as out of radius, {sectionsRefused} refused unserved, "
                    + $"{pendingByPlayer.Count} players waiting, "
                    + $"{manifestDeltasSent} follow-up offers sent to connected players. "
                    + (capture?.SweepStatus is string sw ? sw + ". " : "")
                    + (capture?.PregenStatus is string pg ? pg + ". " : "")
                    + (capture?.GenerateStatus is string gn ? gn + ". " : "")
                    + "Settings live in ModConfig/vintagehorizons-server.json (restart to apply).");
            });

        Mod.Logger.Notification(
            "VintageHorizons {0} server assist listening. Players without the mod are "
            + "unaffected and do not need to install anything.",
            Mod.Info.Version);
    }

    void OnHello(IServerPlayer fromPlayer, AssistHello msg)
    {
        Mod.Logger.Debug("VintageHorizons: assist hello from {0} (client {1}, protocol {2})",
            fromPlayer.PlayerName, msg.ModVersion, msg.Protocol);

        // Answered from the main thread, one tick later, rather than from here. Message
        // handlers do not run on the main thread, and both the key set and its count come
        // from a HashSet the capture pipeline mutates every tick - reading it here is a
        // torn read, and the count would disagree with the manifest that follows it
        // (observed: announced 5634, sent 5638, four sections captured in between).
        sapi.Event.EnqueueMainThreadTask(() => Answer(fromPlayer), "vintagehorizons-hello");
    }

    /// <summary>
    /// Welcome plus the key manifest, from one snapshot so the announced count is a fact
    /// rather than an estimate. Enabled stays false until sections can actually move:
    /// reporting true would leave a client waiting for terrain that is not coming.
    /// </summary>
    void Answer(IServerPlayer player)
    {
        LodServerCaptureSystem? capture = sapi.ModLoader.GetModSystem<LodServerCaptureSystem>();
        LodServerConfig config = capture?.Config ?? new LodServerConfig();
        bool capturing = capture?.Capturing == true;
        bool serving = capturing && config.EnableServing;
        long[] keys = serving ? capture!.SnapshotKeys() : Array.Empty<long>();

        (bool enabled, string status) = AssistGreeting.Describe(
            capturing, serving, keys.Length, config.ServeRadiusBlocks);

        channel.SendPacket(new AssistWelcome
        {
            Protocol = LodAssist.Protocol,
            ModVersion = Mod.Info.Version,
            Enabled = enabled,
            Status = status,
            ManifestKeyCount = keys.Length,
        }, player);

        if (keys.Length > 0) SendManifest(player, keys);

        // Recorded even when the cache was empty just now: that is the case that most
        // needs the follow-up offers.
        ledger.Greet(player.PlayerUID, keys);
    }

    readonly ManifestLedger ledger = new();

    /// <summary>
    /// Tell connected players about sections the cache gained since they joined.
    ///
    /// The manifest used to be sent once, on the greeting, and that was the only one a
    /// client ever got. An admin running /vhgen while people were online therefore built
    /// terrain none of them could ask for: a client only requests keys it has been
    /// offered, so ".vhwhy" reported "no-data" for ground the server had held for hours,
    /// and relogging was the only cure. Reported from the field against 0.2.0. A sweep
    /// that finishes late, and ordinary exploring by other players, did the same.
    ///
    /// The client merges every manifest it receives and only ever adds keys, so a later
    /// message is a delta already and this needs no protocol change.
    /// </summary>
    void OfferNewKeys()
    {
        if (ledger.GreetedCount == 0) return;

        LodServerCaptureSystem? capture = sapi.ModLoader.GetModSystem<LodServerCaptureSystem>();
        if (capture?.Capturing != true || capture.Config.EnableServing != true) return;

        long[] delta = ledger.Delta(capture.SnapshotKeys());
        if (delta.Length == 0) return;

        int told = 0;
        foreach (IPlayer online in sapi.World.AllOnlinePlayers)
        {
            if (online is not IServerPlayer player || !ledger.HasGreeted(player.PlayerUID)) continue;
            SendManifest(player, delta);
            told++;
        }

        if (told == 0) return;
        manifestDeltasSent += told;
        Mod.Logger.Debug("VintageHorizons: offered {0} newly cached sections to {1} player(s)",
            delta.Length, told);
    }

    long manifestDeltasSent;

    void OnPlayerDisconnect(IServerPlayer player)
    {
        ledger.Forget(player.PlayerUID);
        pendingByPlayer.Remove(player.PlayerUID);
        serveAllowanceByPlayer.Remove(player.PlayerUID);
    }

    /// <summary>
    /// Pending section requests, per player, oldest first. Held here rather than answered
    /// inline so the per-second cap has something to meter, and so a player who asks for a
    /// hundred sections gets them steadily instead of in one spike.
    /// </summary>
    readonly Dictionary<string, Queue<long>> pendingByPlayer = new();
    readonly Dictionary<string, LodTickAllowance> serveAllowanceByPlayer = new();
    readonly LodTickAllowance globalServeAllowance = new();
    const double ServeWorkBudgetMs = 2.0;

    void OnSectionRequest(IServerPlayer fromPlayer, AssistSectionRequest msg)
    {
        if (msg.Keys == null || msg.Keys.Length == 0) return;

        // Onto the main thread for the same reason as the manifest: this touches shared
        // state, and the blob read has to be ordered against the capture that writes it.
        long[] keys = msg.Keys;
        string uid = fromPlayer.PlayerUID;
        sapi.Event.EnqueueMainThreadTask(() =>
        {
            if (!pendingByPlayer.TryGetValue(uid, out Queue<long>? queue))
            {
                pendingByPlayer[uid] = queue = new Queue<long>();
            }

            // Bounded: the client is supposed to limit itself, but a server must not
            // depend on a client behaving.
            int room = Math.Max(0, MaxQueuedPerPlayer - queue.Count);
            foreach (long key in keys.Take(room)) queue.Enqueue(key);

            // Anything past the cap is refused OUT LOUD. This used to drop silently,
            // with a comment saying the client would re-ask - it cannot. A client marks
            // a key in flight when it sends it and only forgets it when a reply comes
            // back, so a dropped key is stranded for the session. With a 16-key in-flight
            // cap that is the whole client, permanently stuck. Same rule as M7 stage 4:
            // only keys actually answered may be forgotten.
            foreach (long key in keys.Skip(room)) Refuse(fromPlayer, key);
        }, "vintagehorizons-request");
    }

    const int MaxQueuedPerPlayer = 256;

    /// <summary>
    /// Tell a client we will not serve this key. An empty blob is the refusal, and the
    /// client needs it: silence is indistinguishable from a lost packet, and it leaves
    /// the key in flight forever.
    /// </summary>
    void Refuse(IServerPlayer player, long key)
    {
        channel.SendPacket(new AssistSection { Key = key }, player);
        sectionsRefused++;
    }

    int sectionsRefused;

    /// <summary>
    /// Serve pending requests under continuously accrued per-player and global rates.
    /// A small elapsed-time ceiling also stops after an unexpectedly slow blob read.
    /// </summary>
    void ServePending()
    {
        LodServerCaptureSystem? capture = sapi.ModLoader.GetModSystem<LodServerCaptureSystem>();
        LodServerConfig config = capture?.Config ?? new LodServerConfig();
        long now = sapi.World.ElapsedMilliseconds;
        int globalBudget = globalServeAllowance.Available(now, config.MaxSectionsPerSecondTotal);
        if (pendingByPlayer.Count == 0 || globalBudget == 0) return;
        bool serving = capture?.Capturing == true && config.EnableServing;
        var tickBudget = new LodWorkBudget(ServeWorkBudgetMs);

        // Round-robin from a rotating start, so the global budget below cannot be
        // monopolised by whichever player happens to sort first in the dictionary.
        List<string> uids = pendingByPlayer.Keys.ToList();
        uids.Sort(StringComparer.Ordinal);
        int start = uids.Count == 0 ? 0 : (int)(serveRound++ % (uint)uids.Count);

        int globalSpent = 0;
        List<string>? emptied = null;

        for (int n = 0; n < uids.Count && globalSpent < globalBudget && !tickBudget.Expired; n++)
        {
            string uid = uids[(start + n) % uids.Count];
            Queue<long> queue = pendingByPlayer[uid];

            if (sapi.World.PlayerByUid(uid) is not IServerPlayer player
                || player.ConnectionState != EnumClientState.Playing)
            {
                (emptied ??= new List<string>()).Add(uid);
                continue;
            }

            if (!serveAllowanceByPlayer.TryGetValue(uid, out LodTickAllowance? playerAllowance))
                serveAllowanceByPlayer[uid] = playerAllowance = new LodTickAllowance();
            int playerBudget = playerAllowance.Available(now, config.MaxSectionsPerSecondPerPlayer);
            int playerSpent = 0;
            while (playerSpent < playerBudget && globalSpent < globalBudget
                && queue.Count > 0 && !tickBudget.Expired)
            {
                long key = queue.Dequeue();

                // Refuse gradually rather than clearing every queue in one callback. This
                // path is reachable during join before the cache opens and when serving is
                // disabled; every request still receives the explicit terminal response.
                if (!serving)
                {
                    Refuse(player, key);
                    playerSpent++;
                    globalSpent++;
                    continue;
                }

                // Radius is checked here, against where the player is NOW, rather than when
                // the request was queued: a request that waited in the queue must not be
                // honoured for somewhere the player has since left.
                if (!WithinServeRadius(key, player, config.ServeRadiusBlocks))
                {
                    channel.SendPacket(new AssistSection { Key = key }, player);
                    sectionsOutsideRadius++;
                    playerSpent++;
                    globalSpent++;
                    continue;
                }

                serveClock.Restart();
                byte[] blob = capture!.LoadBlob(key) ?? Array.Empty<byte>();
                blobReadMs += serveClock.Elapsed.TotalMilliseconds;

                // Empty blob rather than silence for a miss: the client needs to know to
                // stop asking, and cannot tell "declined" from "lost" otherwise.
                //
                // Flagged retryable when the miss is only "not written yet", which on a
                // sweeping or generating server is most of them: the manifest carries mip
                // parents that exist in memory before their row does. A flat refusal there
                // costs the player the section permanently.
                channel.SendPacket(new AssistSection
                {
                    Key = key,
                    Blob = blob,
                    Retryable = blob.Length == 0 && capture.ExpectsToHave(key),
                }, player);
                sectionsServed++;
                bytesServed += blob.Length;
                playerSpent++;
                globalSpent++;
            }
            playerAllowance.Spend(playerSpent);

            if (queue.Count == 0) (emptied ??= new List<string>()).Add(uid);
        }

        globalServeAllowance.Spend(globalSpent);

        if (emptied != null) foreach (string uid in emptied) pendingByPlayer.Remove(uid);

        // Report what serving actually costs the tick, so the caps above can be judged
        // against a measurement instead of an estimate.
        if (sectionsServed - lastReportedServed >= 200)
        {
            lastReportedServed = sectionsServed;
            Mod.Logger.Notification(
                "Assist served {0} sections ({1:0.0} MB), blob reads {2:0.00}ms total, {3:0.00}ms avg",
                sectionsServed, bytesServed / 1e6, blobReadMs, blobReadMs / sectionsServed);
        }
    }

    /// <summary>
    /// Nearest-edge distance from the player to the section, not centre-to-centre: an L6
    /// section spans 4096 blocks, so centre distance would refuse sections the player is
    /// standing inside.
    /// </summary>
    /// <summary>
    /// Radius check for a player. Separate from the math below so the deref of a player
    /// who has no entity yet (mid-join) keeps its own answer: with an unlimited radius
    /// there is nothing to compare against, so the absent position does not matter.
    /// </summary>
    static bool WithinServeRadius(long key, IServerPlayer player, int radiusBlocks)
    {
        if (radiusBlocks <= 0) return true;

        var pos = player.Entity?.Pos;
        if (pos == null) return false;

        return WithinServeRadius(key, pos.X, pos.Z, radiusBlocks);
    }

    public static bool WithinServeRadius(long key, double x, double z, int radiusBlocks)
    {
        if (radiusBlocks <= 0) return true;

        int footprint = LodWorld.KeyFootprintBlocks(key);
        double minX = LodWorld.KeySx(key) * (double)footprint;
        double minZ = LodWorld.KeySz(key) * (double)footprint;

        double dx = Math.Max(0, Math.Max(minX - x, x - (minX + footprint)));
        double dz = Math.Max(0, Math.Max(minZ - z, z - (minZ + footprint)));
        return dx * dx + dz * dz <= (double)radiusBlocks * radiusBlocks;
    }

    readonly System.Diagnostics.Stopwatch serveClock = new();
    long sectionsOutsideRadius;
    uint serveRound;
    long sectionsServed, lastReportedServed, bytesServed;
    double blobReadMs;

    /// <summary>Keys the server holds, in chunks. Main thread only.</summary>
    void SendManifest(IServerPlayer player, long[] keys)
    {
        int sent = 0, sequence = 0;
        while (sent < keys.Length)
        {
            int take = Math.Min(LodAssist.ManifestKeysPerMessage, keys.Length - sent);
            var chunk = new long[take];
            Array.Copy(keys, sent, chunk, 0, take);
            sent += take;

            channel.SendPacket(new AssistKeyManifest
            {
                Sequence = sequence++,
                Last = sent >= keys.Length,
                Keys = chunk,
            }, player);
        }

        Mod.Logger.Debug("VintageHorizons: sent {0} keys to {1} in {2} chunks",
            keys.Length, player.PlayerName, sequence);
    }
}
