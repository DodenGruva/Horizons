# Session 36 — checkpointed persistence and periodic-work smoothing

**Date:** `2026-08-21`
**Branch/commit:** `master` at base commit `0c1f28a1600bce3ac16fecefdc1782e2d74ae032` (working tree changes uncommitted)
**Mod version:** `0.3.40` source metadata; the next playable artifact must be `0.3.41`
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

The owner reported tiny recurring lag spikes several times per second and larger spikes
every three to six seconds, asked how often the mod wrote to disk, and set the product
direction that ordinary LOD persistence should be RAM-first with disk writes no more often
than roughly the game's natural autosave cadence. They also asked to reduce seasonal and
mesh sweeps and move every safe operation off the game's main thread.

Source tracing separated snapshot creation from actual disk I/O. Clean idle produced no
row writes, but active capture could admit six snapshots every 50 ms tick (120 per second
per active pipeline). The storage worker compressed and wrote off-thread, yet it executed
each row independently without an explicit batch transaction. Snapshot freezing remained
owning-thread work because it must read one coherent revision of live section and registry
state.

Other interval matches were independent of persistence: seasonal tint sampling every 240
frames, full GPU mesh scans every 300 frames, full CPU resident-section scans every 100
ticks, and a complete server-manifest snapshot every five seconds. Temporal OpenGL query
issue/result work could align many sections on one frame. Integrated singleplayer key scans
were already backgrounded, but up to four visibility-driven sibling-cache blob `SELECT`s per
tick still used a game-thread connection.

## Work narrative

### 1. Persistence became a bounded RAM checkpoint

Ordinary mutation no longer wakes the SQLite writer. Every active pipeline waits 30 seconds,
captures a fixed prefix of at most 256 dirty keys, and freezes no more than one snapshot per
game tick under the existing elapsed-time/byte admission guard. Dirty state remains the
authority throughout. Mutations after a snapshot carry a newer persistence revision and
therefore survive the older acknowledgement for the next checkpoint.

The storage owner now keeps those immutable snapshots coalesced in RAM. Publication
serializes/compresses the batch and commits every row in one explicit SQLite transaction.
Overflow keys remain dirty in live RAM instead of causing an extra transaction or unbounded
snapshot retention. Close still alternates enqueue, checkpoint publication and exact
acknowledgement until clean or the established timeout.

### 2. Safe I/O moved off the game thread

The integrated-singleplayer sibling cache gained a second read-only worker dedicated to
visibility-driven blob reads. It owns its connection, caps outstanding requests at sixteen,
applies miss cooldowns, and publishes immutable key/blob results. The game thread only orders
requests and transfers completed blobs to the existing structural decoder. The synchronous
production blob API and its otherwise-unused game-thread connection were removed.

### 3. Collection sweeps became rolling maintenance

Seasonal/climate tables now start at a 30-second cadence, fill staging arrays one registered
tint slot per frame, and publish low/high tables atomically. These game colour-map APIs stay
on the owning thread because their thread-safety is not established.

Mesh eviction now checks four queued keys each render frame rather than enumerating both
mesh dictionaries every 300 frames. Its grace period is a real 60 seconds rather than 3,600
frames. CPU section eviction checks two rolling resident keys every game tick instead of
walking the dictionary every five seconds. Delayed reclamation is safe; view direction still
does not decide residency.

OpenGL visibility queries cannot move away from the render context, so the renderer caps
them at eight issues and sixteen availability/result checks per frame. Server follow-up
manifest scans moved from five seconds to 30 seconds, matching the earliest time new cache
rows normally become durable and servable.

### 4. Artifact identity rule

Before this rule was requested, the working changes were packaged over the already-used
`0.3.40` local artifact and copied into the owner's Mods folder. Both copies were verified as
SHA-256 `533085DFFD477410B2A8F16E77AD6693F2E30C157183C08E74C2469DC74C57B3`, but the reuse makes
the advertised version ambiguous relative to the earlier 0.3.40 binary. The owner then
established the durable correction: every new playable, packaged, or installed artifact
increments the patch component by exactly one. Ordinary compile/test runs do not consume a
version. The next playable artifact is therefore 0.3.41.

---

## Delivered

- 30-second, 256-key, one-snapshot-per-tick RAM checkpoint scheduling.
- One storage-thread SQLite transaction per published checkpoint.
- Worker-owned integrated sibling-cache blob reads with bounded in-flight work and retry.
- Incremental seasonal sampling, GPU mesh eviction and CPU section eviction.
- Fixed per-frame temporal-query issue and result-check budgets.
- 30-second server manifest follow-up cadence.
- Focused persistence, async-reader, rolling-residency and static cadence checks.
- Warning-free Release build and 1,555 passing fast-tier assertions.
- Full documentation finalization, including canonical artifact-version identity rules.

## Decisions

- A reconstructible LOD cache accepts up to one checkpoint interval of crash-loss in exchange
  for removing high-frequency durable transactions. World/savegame terrain is unaffected.
- Snapshot freezing remains owning-thread work until sections use an immutable/copy-on-write
  representation; moving reads of live registry/section state to a worker is not safe.
- Reclamation may lag. Small rolling queues are preferred to periodic full scans because
  delayed freeing costs memory while a collection-sized scan costs a visible frame/tick.
- Every active pipeline owns its own checkpoint. Integrated client and server caches are
  separate databases and may each commit once in a checkpoint window when both capture.
- Playable artifact identity always advances by the smallest patch increment unless the
  owner explicitly chooses a larger version jump.

## Traps

- “The storage worker writes off-thread” did not mean write frequency was harmless: every
  snapshot still caused its own SQLite command/transaction behavior.
- Snapshot creation and disk writing are different costs. The former freezes mutable live
  state and cannot simply be moved to a worker; serialization, compression and I/O can.
- Frame-count cadences become machine-dependent time cadences and synchronize unrelated
  work. Use real time for slow state and rolling queues for maintenance.
- A changed binary behind an old version is not repaired by recording a new hash; reports
  still cannot identify which artifact the advertised version meant. See G57.

## Flagged and unverified

- No game process was launched. The source changes and fast checks do not establish that the
  reported several-times-per-second or three-to-six-second spikes are gone.
- Seasonal sampling still performs one slot's 128 game colour-map calls atomically per frame
  during a refresh; its observed cost is unknown.
- Checkpoint freezing admits one oversized first section for progress, so a single unusually
  large section can still exceed the nominal owning-thread time/byte ceiling.
- Readiness probing/mask authority resync, GPU upload and live palette publication remain
  owning-thread work. A residual periodic spike needs telemetry before further cadence edits.
- The revised rolling eviction latency and 30-second persistence crash-loss window have not
  been accepted in game by the owner.
