# Vintage Horizons — gotchas and reversals

> Tier 1: durable traps that already cost time or are directly proven by source. Read by trigger before changing the affected system.

## Traps

### G1 — Never read the live block registry from a worker

**Trigger:** adding background deserialization, palette work, capture classification, or storage processing.

**Trap:** apparently harmless `GetBlock` calls may lazily mutate an engine dictionary. Parallel reads are therefore not guaranteed to be read-only.

**Do:** retain block codes off-thread and resolve ids, flags, and tint slots on the owning game thread before publishing a section.

### G2 — A background database does not help if the main thread scans its whole index every tick

**Trigger:** changing local server-cache discovery or remote manifests.

**Trap:** 0.2.1 reduced `PumpLocalOffers` from twice per frame to once per second, but it still runs a full `SELECT Detail, SX, SZ FROM Section` on the game thread. The network path also re-enumerates the complete remote key set each tick. Cost grows with explored history even when nothing changed.

**Do:** discover keys off-thread at a coarse cadence and publish only deltas. Apply each manifest chunk once.

**Found:** source-traced performance review, Session 1.

### G3 — An item count is not a frame-time budget

**Trigger:** adding `PerTick`, `PerFrame`, or queue-drain limits.

**Trap:** one section may mean a tiny palette or hundreds of kilobytes of runs; one mesh may mean one upload or two large GPU buffers. Fixed item counts cannot bound latency.

**Do:** measure elapsed time and bytes in addition to items, stop when the frame/tick allowance is spent, and resume later.

### G4 — Once-per-second rate limits create once-per-second stutters

**Trigger:** implementing server sweep, generation, assist serving, or any token bucket.

**Trap:** issuing an entire per-second allowance in one callback creates periodic CPU, I/O, callback, and allocation bursts. Integrated singleplayer puts those bursts in the same process as rendering.

**Do:** accumulate allowance and spend it across normal ticks under an elapsed-time ceiling.

### G5 — Camera-relative far distance must not reset projection for ordinary movement

**Trigger:** changing `EffectiveFarDistance`, camera limits, mesh bounds, or eviction.

**Trap:** recomputing the farthest mesh distance relative to the camera changes the exact float while the player moves. Exact equality then makes `Reset3DProjection` run on moving frames, and the scan itself is O(mesh count).

**Do:** cache world-space bounds, compute distance in O(1), and quantize the applied far plane with hysteresis.

**Implemented:** Session 5 tracks both opaque and water footprints, grows the projection
immediately in safe 512-block steps, and requires a lower step to remain stable for five
seconds before shrinking. Preserve the separation between the continuous shader far edge
and the quantized camera projection.

### G6 — A request slot must end in success, explicit refusal, or retryable state

**Trigger:** changing local offers, server assist, load failures, queue caps, or packet drops.

**Trap:** removing a key from the wanted set without clearing `LoadsInFlight` strands it permanently. Likewise, a server that silently drops a request fills every client in-flight slot.

**Do:** model request state explicitly. Mark requested only when responsibility actually
transferred; answer server requests even when refusing. Background decoder acceptance is
transferred responsibility, not success, so retain the transport/world slot until
owning-thread publication or terminal rejection. Give bounded retries a monotonic
cooldown, or an attempt ceiling can be exhausted over consecutive ticks without spanning
the transient failure.

### G7 — Async section work needs revision validation

**Trigger:** moving mip generation, capture publication, decode, save, or mesh preparation off-thread.

**Trap:** a worker snapshot can finish after capture or another child update changed the section. Publishing it unconditionally overwrites newer truth.

**Do:** carry world identity plus section identity/revision where same-world mutation can
race, reject stale results, and requeue when a newer revision still needs work. Queue
clearing is not sufficient: an in-progress worker job may publish after the clear.

### G8 — Visibility and eviction are not the same signal

**Trigger:** adding frustum culling to quadtree traversal.

**Trap:** if invisible means unselected and unselected means evict, turning around forces reloading and remeshing the world behind the camera.

**Do:** use visibility to skip traversal/draw/request work, while residency uses its own distance and age policy.

### G9 — Keep SQLite connection ownership explicit

**Trigger:** moving local cache discovery or server blob reads off-thread.

**Trap:** moving a query delegate to a worker does not make a connection safely concurrent. Shared prepared commands and connection state can still cross threads.

**Do:** use a dedicated read-only connection owned by the reader thread, or serialize all access through one storage owner.

**Implemented:** Session 15 gives server assist a bounded, unpooled, read-only connection
created, queried, and disposed by one reader thread. Results retain player session and
request order until owning-thread packet publication.

### G10 — Shader source must remain pure ASCII

**Trigger:** editing `.vsh`, `.fsh`, or included shader comments.

**Trap:** the engine's OpenTK marshaling can truncate source when UTF-8 byte count differs from character count, producing misleading shader failures.

**Do:** keep shader bytes ASCII and preserve the static check.

### G11 — Vintage Story client tests must use the isolation scripts

**Trigger:** launching or stopping a test client/server.

**Trap:** the client uses a global named pipe. A test launch can forward its connection request into the user's already-running game even with a separate data path; broad process-name termination can kill the wrong instance.

**Do:** use `scripts/test-client.sh`, `scripts/test-server.sh`, `scripts/test-stop.sh`, or the Windows-native `scripts/bench-windows.ps1`. Preserve their private temporary directory, pidfile, and command-line checks.

### G12 — Send shutdown commands before publishing benchmark completion

**Trigger:** changing benchmark completion or Windows client/server orchestration.

**Trap:** the runner closes the client as soon as the done marker appears. A delayed `/stop` callback registered after writing the marker may never execute, leaving the isolated server alive.

**Do:** make result artifacts durable, send the graceful server command, then publish the done marker. Never compensate with broad process termination.

The Windows runner requires PowerShell 7: it uses `ConvertFrom-Json -AsHashtable` and
`Start-Process -Environment`, neither of which Windows PowerShell 5.1 supports. Keep the
explicit `#Requires` guard and invoke it through `pwsh`.

### G13 — SQLite disposal does not defeat connection pooling

**Trigger:** replacing or directly editing a SQLite fixture file and then reopening it in the same process.

**Trap:** disposing a pooled connection can return it to the pool. A later open may observe the old file handle/schema even though the path was replaced.

**Do:** set `Pooling=false` on deliberate stale-file/schema-edit connections, or explicitly clear the relevant pool before replacement.

### G14 — A tick burst cap must retain fractional rate credit

**Trigger:** converting a per-second allowance into fixed-cadence tick grants.

**Trap:** clamping all stored credit to the whole-item tick cap before spending discards
fractional overflow whenever the configured rate does not divide the tick frequency. At
20 Hz, a first implementation made an 8/s allowance run at roughly 6.7/s.

**Do:** cap the actual whole-item grant, retain less than one token of normal fractional
overflow, and discard old credit only when a delayed tick could create catch-up work.

### G15 — A byte ceiling can starve an oversized FIFO head forever

**Trigger:** adding byte or elapsed-time limits to a result queue.

**Trap:** if the oldest item is larger than the byte limit and admission is checked before
any work, every tick rejects the same item and nothing behind it can progress.

**Do:** admit at least one oldest item from a non-empty drain, record its actual/estimated
bytes, then stop before later work once a ceiling is exceeded. Track oldest age so lack of
progress remains visible.

### G16 — A queued save is not durable local data

**Trigger:** changing foreign adoption, remote fallbacks, eviction, or save routing before
storage acknowledgements exist.

**Trap:** a foreign section is marked dirty when installed, but current save state clears
when its snapshot is queued rather than when the exact row write is acknowledged. Removing
the foreign source immediately can let eviction route a reload to a local row whose write
is still pending or has failed.

**Do:** retain the foreign fallback until revisioned storage acknowledgements can prove the
local row durable. Do not infer persistence from RAM installation or save enqueue.

### G17 — Vintage Story camera pitch is PI-centred

**Trigger:** changing benchmark camera control or route pitch conventions.

**Trap:** route files describe a conventional zero-degree horizon with negative angles
looking down, but Vintage Story's mouse/camera pitch is centred at PI radians and clamps
around PI/2 through 3PI/2. Passing route radians directly, or merely flipping their sign,
looks into the sky while producing plausible frame-time CSVs.

**Do:** map route pitch to `PI - routePitch`, pin both `MousePitch` and `CameraPitch`, and
visually inspect a rendered screenshot before accepting route evidence.

### G18 — Capture activity does not prove a route is uncached

**Trigger:** using a movement route to claim first-time exploration or cold-cache capture
behavior.

**Trap:** a persistent benchmark sandbox may already contain VH coverage for the whole
route. Vanilla chunks can still load and produce capture/publication work there, so active
capture telemetry does not prove the distant cache is being populated for the first time.

**Do:** record cache state as part of the scenario. For an uncached-terrain benchmark,
start from a clean isolated VH cache or verify every route coordinate is absent before
launch. Label populated-cache runs as warm-cache traversal even when recapture is active.

### G19 — A clean cache does not keep a compact route cold

**Trigger:** designing a multi-leg or multi-lap capture-frontier benchmark.

**Trap:** an empty database proves pre-launch absence, but the route immediately populates
coverage around itself. With a 256-block streaming radius, later legs of a 400-block square
substantially overlap footprints captured by earlier legs in the same run.

**Do:** account for the streaming radius. Use a long one-way trajectory, widely separated
fresh-cache legs, or reset the cache per leg. Describe the initial settled footprint and
do not label every later frame cold merely because the database started empty.

### G20 — A named stats switch may not be the only enable path

**Trigger:** measuring enabled-versus-disabled diagnostic or instrumentation overhead.

**Trap:** the unattended runner set auto-unpause, and the client treated auto-unpause as
an alias for allocation telemetry. A nominal stats-off sample would therefore still call
the per-phase GC allocation counter and could falsely report no overhead.

**Do:** source-trace every enable path before an A/B run and keep unrelated test controls
orthogonal. Here, only `VINTAGEHORIZONS_STATS=1` enables allocation sampling and continuous
stats; auto-unpause controls window-focus behavior only.

### G21 — A scenario label does not prove its precondition or completion

**Trigger:** benchmarking warm/cold joins, sweeps, transient generation, or any operation
whose work can outlive the fixed route.

**Trap:** a database file can belong to another world or contain no sections, and a route
can produce plausible frame numbers while the named server operation is still running. A
48-chunk sweep ended with a valid CSV after roughly two minutes but had reached only 10%
of its load phase. Likewise, a completed frame CSV can leave capture, mip, save, load, or
storage work outstanding; stable frames do not prove durable convergence.

**Do:** validate the active world's reported cache count, require the operation's exact
terminal state, and preserve both pre-launch state and postcondition beside the result.
For mip/persistence soaks, require a cooldown long enough for a fresh stats sample and
fail unless prerequisite capture/load work, mip obligations, unsaved state, and storage
backlog/errors have drained.

### G22 — Sweep and generation radii exclude the safety-neighbourhood probes

**Trigger:** sizing a sweep or transient-generation benchmark by configured chunk radius.

**Trap:** the configured radius describes the load/generation square. Existence probing
extends four chunks farther on every side so frontier decisions have a complete
neighbourhood. Radius 48 therefore means 11,025 probes, not 9,409, and can make an
apparently short completion scenario several minutes long.

**Do:** budget probe work as `(2 * (radius + LodColumnMap.SafeNeighbourhood) + 1)^2`, then
calibrate load/generation time separately from measured evidence.

### G23 — A joining player is not a disconnected player

**Trigger:** servicing a server-assist request queue during connection or world join.

**Trap:** the client can request sections after the assist handshake but before
`PlayerByUid` exposes it as `Playing`. The 50 ms server loop treated that transient state
as a departure, removed the bounded queue without a reply, and stranded all 16 client
request slots. A completed frame CSV hid the failure until the scenario required actual
received and installed sections.

**Do:** retain the bounded queue through transient join state. Let the server's player
disconnect event own real queue cleanup, and require transfer postconditions rather than
inferring success from a handshake or a saturated request counter.

### G24 — Graceful restart and timed termination do not prove crash recovery

**Trigger:** validating persisted asynchronous mip or save obligations across interruption.

**Trap:** graceful close runs the shutdown drain, while a fixed-delay termination can hit
a moment with no durable obligation. Even observing a flagged row can race a later clearing
write and turn the intended crash test into another clean restart.

**Do:** publish a test marker only after the flagged database write succeeds, prevent the
clearing writer from overtaking orchestration, verify the exact isolated PID before
termination, require the restart to report persisted work, and use a later fresh process
to prove the cleared state reached disk.

### G25 — A priority queue needs separate ownership of the obligation

**Trigger:** replacing whole-set nearest searches with a heap or spatial work index.

**Trap:** a removed/re-added key leaves a stale heap entry, while the nearest key may be
temporarily blocked by an in-flight mesh or reload. Treating heap removal as ownership can
either publish stale work or silently lose the newer dirty obligation. Re-ranking every
camera movement frame also recreates the original O(collection) cost under a new name.

**Do:** keep exact dirty membership separate, validate every dequeued entry, restore busy
keys without clearing membership, ingest new keys incrementally, and rebuild priorities
only at a coarse spatial/policy boundary under an explicit examination ceiling.

**Found:** render-dirty priority scheduling, Session 19.

## Reversals and disproved claims

### R1 — Compression and SQLite writes do not belong on the render/game thread

Earlier synchronous save batches measured roughly 10–22 ms average and about 49 ms peak. Compression and writes were moved to `LodStorageThread`; only immutable snapshot creation remains on the owning thread.

Do not reintroduce inline serialization for convenience.

### R2 — Render-demand cache reads should not block a frame

Inline demand loads measured multi-millisecond averages and peaks near 30 ms. Render-driven reloads now go through the storage worker and publish later. Release 0.2.1 also changed cold-cache capture so results wait in order for a background reload; the prior path measured as high as 113 ms on the game tick.

Do not reintroduce synchronous decompression in either path.

### R3 — Draw-time frustum rejection alone is not a complete renderer scalability strategy

Draw-time culling reduced submitted draw calls, but it does not prevent whole-tree traversal, off-screen mesh requests, dirty-set scans, or far-plane scans. Preserve the measured draw win while adding visibility-aware traversal with residency kept separate.

### R4 — Silently dropping server requests is never backpressure

The server previously dropped requests when its cache was unavailable or a queue was full. Because clients retained the in-flight markers, enough silent drops stopped all later terrain transfer. Explicit refusal is the settled behavior.
