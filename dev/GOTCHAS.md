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

**Implemented:** Session 20 applies job-boundary time/retained-byte limits to mesh
snapshots and result-boundary time/upload-byte limits to GPU work. The first item still
progresses, frame-local budget state is allocation-free, and old GPU resources survive
until a complete replacement is live.

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

**Trigger:** changing foreign adoption, remote fallbacks, eviction, save coalescing, or
shutdown routing.

**Trap:** clearing dirty state at snapshot enqueue lets a failed write disappear, and a
success for revision N can erase a newer mutation at N+1. Removing a foreign source at
install has the same fault: eviction can route a reload to a local row whose write is
still pending or failed. Draining only the accepted queue at close also misses dirty
revisions held back by its capacity ceiling.

**Do:** give stored state its own revision, retain dirty membership through enqueue, and
clear it only on exact successful acknowledgement. Coalesce only pending same-key
snapshots, retry failures with bounded delay, and make close alternate drain/ack/enqueue
until clean or an exact unresolved report. Retain the foreign fallback until any local row
is durably acknowledged.

**Found:** revisioned persistence hardening, Session 21.

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

### G26 — A value type's optional constructor is not its default constructor

**Trigger:** converting a helper class with optional constructor parameters to a struct.

**Trap:** `new Helper()` can zero-initialize a struct instead of invoking the overload that
declares only optional parameters. Reference fields that the old constructor initialized
remain null, and the failure may not appear until the second budget item uses them.

**Do:** add an explicit parameterless constructor that delegates to the intended defaults,
remove optional defaults from the parameterized overload, and check the exact production
form (`new Helper()` followed by multiple operations).

**Found:** `LodDrainBudget` runtime validation, Session 21.

### G27 — A runtime runner can deploy a stale build artifact

**Trigger:** a harness copies an existing Debug/Release output instead of building source.

**Trap:** a complete, plausible benchmark can exercise yesterday's DLL. Source-only
telemetry is then absent, but aggregate FPS and clean shutdown make the run look valid.

**Do:** build the exact configuration immediately before the run and verify a source-
specific log field or artifact freshness before accepting evidence. The Windows runner
now rejects assemblies older than project/C# inputs; preserve any stale run only as a
diagnostic, never as result evidence.

**Found:** renderer-budget runtime validation, Session 21.

### G28 — Integrated client and server test hooks share one environment

**Trigger:** using an environment-controlled storage, persistence, or interruption hook
inside integrated singleplayer.

**Trap:** client and server pipelines live in one process and inherit the same environment.
Whichever storage worker reaches a process-global marker first can appear to prove the
other database's behavior. A durable server `ApplyToParent` row therefore cannot establish
that the client cache will recover.

**Do:** scope the hook at pipeline construction, preserve the database-side identity in
the scenario, and add a check that the excluded owner cannot publish the marker.

**Found:** integrated mip interruption/recovery, Session 22.

### G29 — Progress logging is owning-thread work

**Trigger:** adding periodic progress, throughput, queue, or diagnostic messages inside a
frame-, tick-, or callback-critical path.

**Trap:** logger calls may synchronously format and publish to console/file sinks. An
every-200-sections assist notification reproduced multi-millisecond server-service tails;
correlated attribution measured 3.573 ms at that boundary while the callback's two sends
totalled 0.075 ms and no managed collection crossed the call. Rate-limited logging is
still unbounded latency when it executes inside the rate-limited work.

**Do:** accumulate counters in the critical path and report them from an existing status
command or explicitly opt-in coarse telemetry callback. Add a source or structural check
when a proven hot path must remain free of synchronous logger calls.

**Found:** server-assist tail attribution, Session 23.

### G30 — Camera-relative geometry coordinates are not stable appearance coordinates

**Trigger:** adding world-space noise, tint variation, material breakup, or another
appearance effect to camera-relative LOD geometry.

**Trap:** camera-relative positions intentionally change as the camera moves. Sampling a
noise field from those positions makes a fixed terrain point change color in flight even
though its mesh and stored color are unchanged.

**Do:** keep geometry camera-relative for projection precision, but pass a stable section
world origin and derive appearance coordinates from section-local vertices. At extreme
world coordinates, precision loss may soften cosmetic variation but must never move
geometry.

**Found:** cached-terrain color alternation, Session 24.

### G31 — View distance is not per-chunk render readiness

**Trigger:** handing cached terrain to the vanilla renderer near its configured view edge.

**Trap:** a radial cutoff cannot tell whether one replacement chunk has actually rendered.
Moving it outward opens a band while streaming lags; moving it inward leaves approximate
cached and vanilla meshes overlapping and z-fighting. Sinking or fading the cached mesh
still leaves two owners. The public `IsChunkRendered` signal is closer, but in the 1.22.5
client its counter advances during tessellation before completed mesh upload. The exact
1.22.7 trace confirms the same ordering: `ChunkDirty(NewlyLoaded)` fires earlier still,
and the public client API exposes neither the internal post-upload callback nor a
chunk-unload event for the observed removal path.

**Do:** use exclusive chunk ownership. Treat dirty/load signals as candidates, require
render-frame-separated confirmation before readiness gain, and revalidate ready cells
under a boundary-first elapsed-time/item budget so unload restores cached coverage.
Publish CPU/GPU readiness atomically, skip wholly replaced meshes, and mask only mixed
frontier sections. Keep visibility and residency separate so ownership changes do not
cause remesh thrash.

**Found:** cached/vanilla handoff investigation, Session 24. Detailed implementation plan:
`dev/plans/PLAN_CHUNK_AWARE_VANILLA_HANDOFF.md`.

### G32 — Stabilization work must not re-enter its active queue in the same frame

**Trigger:** implementing a frame-delayed readiness check, retry, debounce, or publication
confirmation inside a bounded queue drain.

**Trap:** immediately requeueing an item that is not yet old enough lets the same item be
dequeued repeatedly in one frame. It can consume the entire nominal item/time budget
without making progress and defeats the intended render-boundary delay.

**Do:** retain not-yet-eligible work in a separate fixed deferred queue, promote it only
after the required frame boundary, and track oldest age for both active and deferred work.

**Found:** vanilla-readiness shadow tracker, Session 25.

### G33 — A revalidation sweep filtered to committed state can only lose

**Trigger:** writing a bounded maintenance sweep for state that can be both gained and
lost, such as readiness, residency, or reachability.

**Trap:** filtering the sweep to cells already committed makes it a confirmation loop. It
detects loss promptly and can never detect gain, so progress depends entirely on whatever
external event happens to requeue a cell. Worse, the wasted budget looks like healthy
activity: a measured 15-second interval spent 432,744 probes re-confirming 1,727 owned
cells and none on the 1,801 cells awaiting an answer, while every counter read normally.

**Do:** sweep without a state filter and let coalescing suppress duplicates. Assert in a
runtime gate that probes actually reach non-committed cells, because a healthy-looking
probe count proves nothing about where the probes went.

**Found:** vanilla-readiness interior maintenance, Session 26.

### G34 — Gate on retained state, not on counters that reset every interval

**Trigger:** writing an automated acceptance gate against periodic telemetry.

**Trap:** rates and states fail in opposite directions. A gate on "ready transitions this
interval" failed a correct, fully settled system that had nothing left to transition, while
the same gate passed a broken one that churned continuously. Reported maxima have the
mirror problem: a single blocking engine call the code cannot preempt fails a tight maximum
that says nothing about steady-state cost.

**Do:** gate on retained state and on a percentile that reflects the steady path, report
rates and maxima as evidence, and when a defect is found, encode the specific invariant it
violated rather than only fixing the code.

**Found:** readiness convergence gate, Session 26.

### G35 — A summed world coordinate is not exact enough to own a chunk

**Trigger:** deciding which cell, chunk or region a fragment belongs to inside a shader,
from a world position built by adding a section origin to a local offset.

**Trap:** the sum is a float32. At a 512,000-block world coordinate its ulp is 0.0625, so a
fragment 0.03 blocks below a chunk boundary rounds up onto the next chunk and reads that
chunk's ownership. Nothing crashes and nothing looks obviously wrong; a sliver of ground at
chunk edges simply obeys the wrong owner, and the error grows with world size. A substring
check comparing the shader and the C# addressing passes happily, because both expressions
are written the same way - only their arithmetic differs.

**Do:** pass the section origin as an integer in cell units and add the floor of the small
local offset, which is exact. Evaluate the shader's arithmetic in the check tier against
the real addressing over a grid that includes large origins and boundary-adjacent offsets,
rather than comparing the two expressions as text.

**Found:** chunk ownership mask, Session 27.

### G36 — Renderer state measured in one world must not survive into another

**Trigger:** caching per-world render state - readiness, ownership, visibility - beside a
resource the shader samples every frame.

**Trap:** clearing the model on a world or dimension change is not enough if the GPU
resource and its "active" flag are left alone. The shader keeps sampling the old world's
data, so terrain stays suppressed using ownership measured somewhere else entirely. The
model looks correct in every log line while the pixels are wrong.

**Do:** treat the model, the GPU resource, and the flag that says the resource is
authoritative as one unit. Clear all three on the same path, and default the cleared state
to the safe direction - here, every cell returning to the cache.

**Found:** chunk ownership mask, Session 27.

### G37 — A discovery cursor that restarts on movement never reaches the frontier

**Trigger:** sweeping a camera-following window with a cursor, and resetting that cursor
whenever the window moves.

**Trap:** the cells that matter while moving are the ones the window just gained, and they
sit at the far end of the scan. Restarting the cursor on every movement means the sweep
re-covers ground it already knows and never arrives, so the state ahead of the player is
the last thing ever established. It looks correct while standing still and fails only in
motion, in proportion to speed.

**Do:** queue what the window gains at the moment it gains it, and let the cursor cover the
interior at its own pace. Budget the work by elapsed time rather than an item count chosen
against a settled view, because a count that is generous when the queue is empty is the
binding limit exactly when it is not.

**Found:** vanilla-readiness acquisition under flight speed, Session 27.

### G38 — A renderer benchmark that starts before residency settles measures nothing

**Trigger:** comparing frame rates between two renderer configurations.

**Trap:** a run whose meshes are still loading draws almost nothing and posts a large
apparent gain. One measured 536.7 FPS against a 438.6 baseline, a 22% "improvement" whose
own telemetry read zero meshes and zero selected nodes for the first three intervals.

**Do:** settle to full mesh residency before measuring, and report mesh and selected-node
counts beside the frame rate so a reader can check the comparison instead of trusting it.
Treat any large unexplained win as a measurement bug until those counts match.

**Found:** chunk ownership mask, Session 27.

### G39 — State that hides something must be re-confirmed on a clock, not by a cursor

**Trigger:** caching an expensive observation - readiness, visibility, ownership - and using
it to suppress a fallback that would otherwise draw.

**Trap:** a cursor sweeping a camera-following window is not a bound. It restarts when the
window moves, it falls behind when the camera moves faster than it scans, and either way
the ground it has not reached keeps hiding its fallback. Three separate cursor fixes each
looked correct and each left holes that outlived the movement causing them, because none of
them could state how long a wrong answer could survive.

**Do:** re-confirm the entire suppressing set on a fixed interval, and size the set so that
is affordable - here roughly 1,700 cells against a probe costing well under a microsecond.
The interval then is the staleness bound, and it holds no matter how the camera moved.
Keep the cursors for discovery, where being late costs coverage rather than correctness.

**Found:** chunk ownership mask, Session 27.

### G40 — "The engine drew this chunk" means the counter moved, not that anything was drawn

**Trigger:** using `IsChunkRendered`, or any counter the tessellator advances, to decide
that vanilla terrain now covers an area.

**Trap:** `ICoreClientAPI.IsChunkRendered` compiles to nothing more than
`ClientChunk.quantityDrawn > 0`, and `ChunkTesselatorManager.TesselateChunk` advances that
counter and returns before meshing whenever the chunk reports empty. A chunk full of air
answers exactly like a chunk full of mountain. Both facts are read from the installed
game's IL, not inferred.

**Do not** conclude from that that an empty chunk should be refused ownership *in the
tracker*. 0.3.3 did exactly that and removed ownership from the entire world in one step. The reason is the
column rule, not the flag: a column counts as owned only when all of its vertical chunks
do, every column has sky above it, and sky is legitimately empty. Roughly two of every five
probed cells that report drawn also report empty, so excluding them leaves no complete
column anywhere, `NearestIncompleteColumnBlocks` collapses to about zero, and cached
terrain draws over everything.

**Correcting the original entry:** this gotcha first recorded that `IWorldChunk.Empty` is a
stale client-side flag that "does not mean this chunk has no blocks". That is wrong.
`ClientWorldMap.LoadChunkFromPacket` assigns `chunk.Empty = packet.Empty`, so the value is
the server's own answer delivered with the chunk, and the block-set accessors maintain it
afterwards. The client tessellator trusts it to decide whether to mesh at all. The flag was
never the problem; the rule built on it was.

**Do:** ask whether the engine holds any geometry, which is a different question from
whether the chunk is empty. `ClientChunk.centerModelPoolLocations` and
`edgeModelPoolLocations` are the mesh's index and vertex ranges in the shared pool; a chunk
with neither has nothing in the world's buffers. `VanillaChunkGeometry` binds them by
reflection and reports unavailable rather than guessing if a game update moves them, and
the check tier asserts the binding against the installed assembly.

**Do** also keep any such query inside its own handler and let it change nothing. The probe
loop treats a thrown query as "vanilla is not drawing here", so a diagnostic that throws
does not merely fail - it silently hands the whole world back to the cache. A measurement
that cannot change ownership can be added safely; a rule that can must be established first.

**But do refuse it in the mask.** An air cell must never suppress a fragment. Cached terrain
is an approximation and overshoots the real surface, so its geometry lies inside air chunks;
vanilla draws nothing there, so the discard removes the only draw and shears the top off the
cached horizon. The two rules coexist: air owns its cell for the column aggregate, which is
what 0.3.3 needed, and never reaches the atlas texel, which is what the picture needs.

0.3.15 established this from `.vhpaint`, after this entry had claimed in 0.3.5 that
suppressing cached geometry above the real surface was correct because the real ground below
is drawn by vanilla. That reasoning fails for geometry standing *above* the real surface:
there is no lower cell drawing anything at that height, so nothing replaces what is
discarded. Three ownership rules were written against this wrong conclusion.

**Found:** chunk ownership mask, Session 27, from play reporting holes that would not close,
and then from breaking ownership outright while trying to fix them. Corrected in 0.3.5 by
reading the game's IL instead of reasoning about the flag, and again in 0.3.15 by painting
the discarded fragments instead of reasoning about them at all.

### G41 — A duplicate chat command name switches off the rest of the mod's startup, quietly

**Trigger:** adding a chat command, especially one that replaces an older command's job
while the older registration is left in place.

**Trap:** `ChatCommandApi.Create` throws on a name that already exists, and the throw
propagates out of `StartClientSide`. The game logs "Failed to start system" and carries on:
everything registered before the collision works, everything after it silently does not
exist, and the mod keeps drawing, so a playtest looks normal. 0.3.4 shipped with `.vhwhy`
registered twice, which cost `.vhdetail` entirely and left the coarse-draw report
unreachable. The names are strings, so the compiler sees nothing.

**Do:** keep one name per command, and let
`StaticAssetChecks.ChatCommandNamesAreUnique` fail the check tier if that ever stops being
true. When a new command supersedes an old one, rename the old one rather than reusing the
name, or delete it deliberately.

**Found:** 0.3.5, by reading the client log of the 0.3.4 playtest rather than the game.

### G42 — An integer uniform set through the Vec2i overload never arrives, and only the log says so

**Trigger:** passing a pair of integers to a shader, or reaching for
`prog.Uniform(name, new Vec2i(...))`.

**Trap:** the client's only pair-of-integers setter calls `GL.Uniform2(location, float,
float)`. Against a `uniform ivec2` the driver answers `GL_INVALID_OPERATION` and leaves the
uniform at its previous value - zero. Nothing throws, the shader compiles and links, the
draw succeeds, and the check tier's own re-implementation of the address arithmetic still
agrees with the shader, because the arithmetic was never the thing that broke. The only
symptom is `after final compo - OpenGL threw an error: InvalidOperation` once per frame,
which is easy to read as somebody else's problem in a log with ten mods in it.

This shipped in 0.3.0 through 0.3.5. `maskSectionOrigin` stayed at (0,0), so every
fragment tested its ownership against chunk (0,0) instead of its own section's origin, the
window bounds test then rejected nearly everything, and the per-cell mask discarded almost
no fragments at all. Four attempts to explain the reported holes with ownership latency,
sweeps and staleness were all looking at CPU state that was working.

**Do:** split it into scalar `uniform int`s. `Uniform(name, int)` reaches `glUniform1i` and
is correct. `StaticAssetChecks.NoIntegerVectorUniforms` now fails the check tier on any
`ivec`/`uvec` uniform in our shaders, and the mask checks assert the two scalars are set.

**Do** treat a per-frame GL error as a defect with an address, not as noise. Two isolated
sandbox runs on the same scene separated it in about two minutes: 19,126 errors with
`-ChunkMask`, zero without.

**Trap while diagnosing it:** setting `glDebugMode` in the sandbox `clientsettings.json`
makes the client crash rather than annotate, because the debug callback throws
"No detailed debug message due to a non-debug context" when the GL context was not created
as a debug context. That crash is still the fastest answer available - its stack names the
exact `Uniform` call - but expect to clean up afterwards: the client leaves
`VSCrashReporter.exe` holding `launch.log`, which blocks the next run's log rotation, and
the sandbox server stays up for `scripts/test-stop.ps1`.

**Found:** 0.3.6, from a controlled pair of sandbox runs after noticing tens of thousands
of GL errors in an ordinary playtest log.

### G43 — "Is the engine drawing this chunk" is decided at draw time, and distance is half the answer

**Trigger:** deciding that vanilla covers an area, from any per-chunk signal - including
`ClientChunk.CullVisible`.

**Trap:** four signals look like they answer it and none of them do on their own.
`ICoreClientAPI.IsChunkRendered` is `quantityDrawn > 0`, a counter that only ever goes up,
so it means "tesselated at some point" (G40). Mesh pool locations are closer, but the engine
keeps a chunk's mesh while choosing not to submit it, so their presence is not submission.
`IWorldChunk.Empty` is accurate but answers a different question. And `CullVisible` is the
culler's occlusion verdict, which is necessary but not sufficient.

The real per-frame test lives on the pool location, not on the chunk.
`ModelDataPoolLocation.IsVisible` in `CullNormal` mode is
`!Hide && CullVisible[VisibleBufIndex] && culler.InFrustumAndRange(sphere, ..., LodLevel)`,
and `FrustumCulling.InFrustumAndRange` ends with `playerPos.HorDistanceSqTo(sphere.x,
sphere.z)` - the mesh's bounding-sphere centre, which is the geometry extents midpoint and
not the chunk centre - against a per-LOD bound that is at most `ViewDistanceSq` - itself
`viewDistance * viewDistance + 400` (`UpdateViewDistance`). **So the engine range-culls every
terrain mesh against the current camera, every frame, after all four chunk signals have said
yes.** A chunk that is loaded, meshed, unhidden and cull-visible but further away than the
view distance is silently never drawn.

Nothing about the chunk changes when that happens, which is what makes it invisible to a
tracker. Worse, `ChunkCuller.CullInvisibleChunks` early-returns when the camera has not
changed chunk (and the regen-traversal queue has not moved by 10), so `CullVisible` is not
merely stale out there - it is frozen for as long as the player stands still. That is the
exact condition under which the band was reported: committed cells in the annulus between the
view-distance circle and the tracked window edge stayed committed forever, the mask discarded
cached terrain there, and nothing drew that ground. Only on the trailing side, because the
leading side never had chunks loaded beyond the view distance to latch in the first place.

The culler also carries an `isAboveHeightLimit` branch: fly high enough and vanilla stops
drawing the ground directly below, in an unmodded client, while every other signal still
claims it.

**Do:** read `CullVisible[bufIndex]` **and** apply the distance clause. All the culler members
are public API - no reflection - and `VanillaReadinessChecks.ChunkGeometryBinding` pins them
against the installed assembly. The distance clause needs no engine access at all: it is
column distance against the approved view distance, done in pure arithmetic, which matters
because the probe loop is hot and a chunk lookup there takes the client's chunk lock.

**Do** clear the whole chunk when picking the threshold, and check which direction the slop
falls in. The engine measures range from the mesh's bounding-sphere centre, and `TesselatedChunk`
builds that centre as the geometry extents midpoint (`positionX + (xMax + xMin) / 2f`) - so it
sits wherever the blocks in that chunk happen to be, anywhere across the 32-block span, not at
the chunk centre. Comparing the column's *nearest face* against the plain view distance sounds
conservative and is conservative in the overlap direction only: a chunk whose near face is just
inside the view distance can have its sphere centre well outside it, so the engine culls it
while every latched signal still says "drawing". That leaves a residual permanently-stale ring
up to `32 * sqrt(2)` wide - a thinner copy of the same band.

Subtract the full in-chunk horizontal diagonal instead: deny when the nearest face exceeds
`viewDistance - 46`. Since the engine culls no earlier than `sqrt(viewDistance^2 + 400) >=
viewDistance`, and centre and face lie inside one column so differ by at most 45.26, culled
implies denied for every possible midpoint placement. The cost is cached terrain drawn over the
outermost chunk and a half of live vanilla terrain - a seam, not a gap.
`VanillaReadinessChecks.OwnershipStopsAtTheEnginesDrawRange` asserts the no-hole direction
directly, sweeping every cell whose farthest admissible sphere centre reaches the engine's
bound; it fails on the nearest-face-versus-plain-view-distance threshold.

**Do** treat empty and unknown as drawn, so a wrong answer costs the correction rather than
uncovering live terrain.

**Do** expect the culler verdict to be camera-dependent. It is occlusion: a chunk behind a
mountain is not drawn, so its cell is released and cached terrain draws there, hidden behind
the same mountain. Correct, and not free - the cost is unmeasured.

**Found:** the culler half in 0.3.10, from a player testing the altitude case in vanilla with
no mods after three ownership rules built on the wrong signal failed to close the holes. The
distance half in 0.3.16, by reading `ModelDataPoolLocation.IsVisible` and `InFrustumAndRange`
in the game's IL after this entry had claimed for six releases that the culler's verdict was
the *only* thing that decided.

### G44 — A mirror of a wrapped ring must be told when a slot changes hands

**Trigger:** keeping a second copy of ring-addressed state - a GPU texture, a cache, any
mirror - alongside `VanillaRenderReadiness`.

**Trap:** the ring is fixed size and scrolls with the camera. A column leaving the window
surrenders its slot to whichever column wraps onto the same address. `ClearSlot` wiped the
tracker and nothing wiped the mirror, so the arriving column read the departed column's
value. On the ownership atlas that meant `ReadyTexel` for cells nothing owned, and the
shader discarded cached fragments over ground vanilla was not drawing.

The failure has a shape worth recognising: a band rather than scattered holes, at the
trailing edge of movement, stable while standing still, unrepaired by any amount of
re-probing. Every CPU-side diagnostic said the system was healthy, because it was - the
tracker, the column aggregate, the section counts and the ownership audit were all correct,
and only the mirror was stale. Three ownership rules were written and discarded chasing it.

**Do:** funnel every wipe through one method and raise from there.
`VanillaRenderReadiness.ClearSlot` raises `ColumnEvicted`; the renderer clears the atlas
column. Test it by proving the arriving column reuses the departed column's exact texel
index and does not inherit its value - a test that only asserts "the mask was cleared"
passes against an addressing scheme where the two never collided.

**Do not** trust "the routine exists" as evidence it runs.
`VanillaReadinessMask.ClearColumn` was written with a correct summary explaining exactly why
it was required, and had no callers from the feature's first version through 0.3.12.

**Found:** 0.3.13, after a player's on/off comparisons ruled out ownership selection and
then whole-mesh skipping, leaving only the GPU mirror.

### G45 — A chunk lookup inside the readiness work takes the lock the loader threads want

**Trigger:** calling `IBlockAccessor.GetChunk` (or anything else reaching the client's chunk
store) from the readiness probe loop, from a diagnostic inside it, or from the wholesale
atlas rebuild.

**Trap:** the lookup takes `ClientWorldMap.chunksLock` — the same lock `IsChunkRendered` has
just taken, and the same one the chunk loader threads want. The probe queue is at its
longest exactly while a world is coming up, so the cost lands where it hurts most. A
drawn-without-geometry diagnostic added in 0.3.5 called it once per probe and is the
standing suspect for cached terrain taking 36.4 s to appear after joining against 6.1 s on
the release before it.

The rebuild path is the same trap wearing different clothes. `WriteMask` runs at mask
creation and on **every** window change — every 32 blocks of travel — over the whole
window, so a per-cell chunk lookup there is thousands of lock acquisitions per crossing.

**Do:** restrict any such lookup to the observations that can actually change a decision.
0.3.8 narrowed the diagnostic to the second of the two true observations a commit needs, or
a re-confirmation of an already-committed cell; every other probe at join is a first
sighting that decides nothing.

**Do:** store the answer in the tracker when a lockless path needs it later. 0.3.16 records
the air exclusion as a per-cell flag at publication time, so `WriteMask` consumes a bit
instead of asking the chunk store, and the once-per-second resync — which already holds a
budget for the lookup — keeps the bit authoritative.

**Do:** prefer pure arithmetic in the probe loop outright where the question allows it. The
draw-range clause in G43 needs no engine access at all.

**Do:** treat a join-time slowdown as a suspect for any diagnostic added since the last
known-good join, before bisecting residency.

**Found:** 0.3.5 through 0.3.8, from a reported join-time regression; recorded here in
0.3.16 after the same shape recurred in the atlas rebuild and was designed out rather than
measured again.

### G46 — `Block.GetColorWithoutTint` is not a function of the block

**Trigger:** asking the engine for a block's colour once and storing the answer as if it
described the block, rather than that call.

**Trap:** two things vary underneath it. `BlockWithGrassOverlay` — which is every
grass-covered soil, peat and clay, so most ground a player ever sees — answers with
`BlockTextureAtlas.GetRandomColor`, one of thirty pixels drawn out of the grass texture at
random per call. And the base implementation hands the question to whatever DECOR sits on
the up face, so the answer also depends on what is standing on that particular block.

A palette entry is registered once per section, so one draw decided the colour of every
instance of that block across a whole 64-block section, and the section beside it drew
again. Measured on a real 1,041-section cache: **38 different stored colours for
`soil-low-normal` alone**, scattered with no relation to terrain, climate or height, with
a per-section standard deviation of 30-39 per channel. On the ground that is flat green
and flat brown tiles meeting at a hard edge, and no amount of blending at section borders
would have fixed it, because the step was in the data.

Blocks that answer deterministically — gravel, rock, forest floor — showed a standard
deviation of exactly zero, which is what made the randomized ones stand out in the cache.

**Do:** compute one colour per block id and cache it, by averaging many draws so a
randomized answer collapses to the texture's own mean. A deterministic block averages to
what it already returned, so nothing else moves.

**Do:** probe at a position nothing can stand on — the sky above the world origin — so the
decor branch cannot make one snowy sample decide a block's colour everywhere.

**Do:** keep the real block position for blocks that carry an entity: a chiselled block
averages the materials in its own entity there and genuinely differs per position. That is
the case the position argument exists for, and it is why the rule is `EntityClass != null`
rather than a list of block codes.

**Do:** correct stored colours on load rather than bumping the schema. Colours are already
re-derived for flags and tint slots on the way in; a cache is worth weeks of exploration.

**Found:** 0.3.18, from a player report of neighbouring cached tiles rendering as
dramatically different flat colours. Diagnosed entirely offline, from the stored cache and
the decompiled engine, with no game run.

### G47 — A colour map is sampled per block, so one sample is a draw and not the colour

**Trigger:** calling `ApplyColorMapOnRgba` once and treating the result as "the tint" for a
whole landscape.

**Trap:** a seasonal map is two-dimensional. `seasonalGrass` is 128x16: X is the point in the
year, and **Y is chosen from a hash of the individual block's position**
(`seasonYPixelRel = MurmurHash3Mod(posX, posY, posZ, 100) / 100f`). That is what makes a real
meadow subtly mottled instead of a flat sheet. One sample returns one of the sixteen rows,
and at LOD distance that row was painted across every field in view.

Measured on the shipped map: at midsummer the rows run `#628100` to `#97B825` about a mean of
`#7B9C0D` - up to a quarter off in red, an eighth in green - and the mod re-rolled to a
different row whenever the player moved far enough to change the hash. The owner reported it
as "the green is slightly off" and named the season as the suspect before the map was opened.

This is G46 in a second guise: two different engine calls that look like properties of a
thing are actually draws from a distribution, and the LOD renderer wants the distribution's
mean because one distant pixel covers many blocks.

**Do:** average the tint over many world positions, not one. 0.3.19 uses 64 positions on an
8-block lattice: far enough apart that the hashes decorrelate, close enough that the climate
under them is still the viewer's own.

**Do not** reach for the `(rain, temp)` overload to sample rows directly. It is public, but
its `seasonYPixelRel` parameter is not exposed through `IClientWorldAccessor`, so it silently
pins every sample to row 0 - and it drops the height-above-sealevel term the two-altitude
tint depends on.

**Do:** clamp sample positions into the map. `GetClimate` answers 0 - freezing and bone dry -
for anything off the world edge, and one such sample drags the whole average.

**Do:** unpack the returned int by hand in a loop like this. `ColorUtil.ToRGBAFloats`
allocates a `float[4]` per call, and red is at bits 16-23, not the low byte, because
`ApplyColorMapOnRgba` flips red and blue by default. Reading red from the low byte turned
every grass tint teal once already.

**Found:** 0.3.19, from a player report on the 0.3.18 build that fixed G46.

### G48 — `GetRandomColor` and `GetAverageColor` return opposite channel orders

**Trigger:** reading a colour out of the block texture atlas and assuming one byte order.

**Trap:** they disagree, in the engine, by construction:

```csharp
texPos.AvgColor = ColorUtil.ReverseColorBytes(ColorUtil.ColorAverage(pixelsTmp, equalWeight));
...
array[num] = num2;              // raw bmp.GetPixel(..).ToArgb()
texPos.RndColors = array;
```

`ReverseColorBytes(0xAARRGGBB)` is `0xAABBGGRR`, so **`GetAverageColor` puts red in the low
byte and `GetRandomColor` puts it at bits 16-23**. The mod stores one convention, so every
colour that reached it through `GetRandomColor` had red and blue exchanged.

That is not a rare path. `BlockWithGrassOverlay.GetColorWithoutTint` — all grass-covered
soil, peat and clay — answers with `GetRandomColor`, and so does `BlockGroundStorage`. Blocks
that fall through to the base implementation get `GetAverageColor` and are correct. So the
mod's grass was swapped while its leaves and rock were not, which is exactly how the owner
described it: as if the grass and the tree colours had been exchanged.

Proof is in any cache. Group the stored colours for `soil-low-normal`: one population sits at
rgb(105,83,60), which is `fertlow.png`'s four-pixel average arriving correctly through the
repair path, and the other at rgb(128,141,140), which is the grass overlay's mean rgb(148,
149,129) with red and blue exchanged. A cyan-grey where an olive-grey belongs.

**Do:** read colours through `GetAverageColor` and treat `GetColorWithoutTint` as untrusted
for byte order. There is no way to ask a block which of the two paths it took.

**Do:** check any new atlas colour source against a texture whose channels differ visibly.
A greyscale or near-neutral texture hides this completely - the averaged grass base was
rgb(128,141,140), whose red and blue differ by 12, so the swap survived the 0.3.18 averaging
and the 0.3.19 tint work without ever looking obviously wrong.

**Found:** 0.3.20, from a player screenshot after two earlier colour fixes had landed.

### G49 — Grass-covered ground is composited by vanilla, and a third of it is never tinted

**Trigger:** taking one colour for a block that vanilla draws in the `TopSoil` render pass.

**Trap:** `chunktopsoil.fsh` is explicit:

```glsl
vec4 brownSoilColor = texture(terrainTex, uv) * rgba;
vec4 grassColor = getColorMapped(terrainTexLinear, texture(terrainTex, uv2 + ...)) * rgba;
outColor = brownSoilColor * (1 - grassColor.a) + grassColor * grassColor.a;
```

Only the grass overlay is colour-mapped. The dirt showing through it is drawn exactly as it
is, and the overlay is roughly 69% opaque at full coverage, 54% at sparse, 40% at very
sparse. So about a third of every grassy block is untinted brown — which is what makes
vanilla ground read olive rather than green, and where nearly all of its blue comes from,
because the seasonal tint's blue channel is near zero and anything multiplied by it loses
its blue outright.

Measured for `soil-low-normal` at midsummer: vanilla G/R 1.09 and B/G 0.26, against the
mod's 1.40 and 0.08 before this was handled.

**Do:** store the composite and dilute the SLOT's tint by the share that came from dirt.
`composite * (share + (1 - share) * tint)` equals `soil*(1-a) + grass*a*tint` exactly, which
is why the untinted share is part of the tint-slot key: two blocks on the same colour maps
with different grass coverage need different dilution. `TopSoilColorChecks` holds the
identity over 1,440 combinations.

**Do:** key the case on `block.RenderPass == EnumChunkRenderPass.TopSoil`, not on the
presence of `specialSecondTexture`. The liquids use that texture key for flow animation and
would be composited into mud.

**Do:** read the texture, not `GetAverageColor`. That call is not an average: it samples
exactly four pixels, at 35% and 65% of each axis. For an opaque texture it is close enough,
but for a partly transparent overlay those four pixels also decide how much of the block
below shows through, and it read 146 where the true mean alpha is 175 — a fifth too much
bare dirt, which was the entire residual error after 0.3.20 and was visible in game as
distant ground still not quite matching. `capi.Assets.TryGet` plus
`capi.Render.BitmapCreateFromPng` gives the real thing; take the colour alpha-weighted and
the coverage as the mean alpha, and that pair is exactly what averaging vanilla's per-pixel
blend over the whole face reduces to. Read it through `BitmapExternal.Pixels` rather than
`GetPixel`, which returns an `SKColor` and would put SkiaSharp on the mod's reference list;
both are `0xAARRGGBB`, and the decoder asks for unpremultiplied alpha, so weighting by alpha
is a weighting and not a second application of it.

**Know:** with the true means the LOD reproduces vanilla's own blend exactly — rgb(80.5,
88.1, 22.8) against vanilla's rgb(80.5, 88.1, 22.8) for `soil-low-normal` at midsummer,
where the four-pixel estimate gave rgb(86.5, 89.2, 29.8).

**Found:** 0.3.20, alongside G48; they were two independent faults in the same surface.

### G50 — Matching vanilla's colour is not finished when the albedo matches

**Trigger:** comparing cached terrain against vanilla terrain and concluding from one
screenshot that the colour is right, or that it is wrong.

**Trap:** the final pixel is albedo times lighting, and the mod supplies both halves itself.
0.3.18 to 0.3.21 made the albedo exact, and the owner then reported that the match held at
some times of day and broke at others - which is the signature of the other half.

Vanilla's terrain lighting is not a sun-angle shade. `getBrightnessFromNormal` floors its
value at `normal.y * 0.95`, so an up-facing surface never darkens as the sun drops, with a
comment in the engine source that block tops darker than block sides look uncanny; and
`chunkliquid.fsh` does not shade by normal at all. The mod's `0.55 + 0.45 * sunAngle` took
flat ground to 0.55 at dawn and dusk against vanilla's 0.95, a 40% gap that closes to nothing
at midday.

**Do:** separate the two halves before attributing a mismatch. An error that varies with the
TIME OF DAY at a fixed season is lighting. One that varies with the SEASON at a fixed time of
day is the tint. One that is constant is the albedo. The owner isolated the first of those by
sweeping the daylight cycle with everything else held still, which is worth asking for.

**Do:** expect the remaining lighting difference to be a floor, not a match. Vanilla's colour
also carries per-vertex light values baked into each chunk and a shadow-map term; LOD sections
store neither, and should not. The normal rule is the dominant term and the only one worth
copying.

**Found:** 0.3.22, from a report that the colour matched at some light levels and not others.

### G51 — "Not in RAM" is not "no data there", and the mesher was asking the wrong one

**Trigger:** any mesh decision that reads `LodWorld.Sections` to establish whether a
neighbouring section exists.

**Trap:** `Sections` is residency. It answers what is in memory this instant, and a section
with a perfectly good row on disk is absent from it constantly - evicted by the sweep, or
simply not read yet. `HasDataSet` is the durable question, and the shader's `openEdges` has
always asked it.

The mesher walls off a section edge whose neighbour is missing, which is correct at the
frontier of explored space and wrong everywhere else, and it was deciding that from
residency. Sections load and mesh NEAREST-FIRST, so the outward-facing side of nearly every
section was meshed before its neighbour had landed. Nothing repaired it afterwards:
`MarkChanged` refreshes a neighbour's mesh, but only content changes call it, and
`InstallLoaded` deliberately marks nothing.

On land the spurious wall is invisible - the neighbouring ground is opaque and hides it,
which is why this survived so long. On water it is the whole bug. Measured on one synthetic
ocean section: 1 water quad with the neighbour present, 65 without it, 64 of them a
3,200 block² sheet of 66%-opaque water standing on the shared plane from the surface to the
seabed. An ocean showed a grid of dark vertical lines that never healed.

**Do:** ask `HasDataSet` when the question is "is there terrain there", and treat a
data-bearing but unloaded neighbour as covered rather than as the frontier. Do NOT force the
neighbour resident to find out - a neighbour outside the draw set is meant to end in nothing,
and loading one to prove it pulls the cache into memory a ring at a time.

**Do:** repair rather than pre-empt. Record which sides a mesh guessed at and re-mesh only
those when the neighbour actually arrives. The blunt version - dirtying all four neighbours
of every arriving section - is three lines and roughly doubles the mesh work of a warm join,
which collides with the join fill-in cost that is already the top open issue.

**Know:** the permissive side is water-only on purpose. A missing solid wall opens a
see-through gap at a cliff for as long as the repair takes; a missing water wall costs
nothing, because at a true frontier the edge dissolves into the sky anyway.

**Found:** 0.3.23, from a report of vertical seams between water chunks. The TODO had it
recorded as a COLOUR fault with three candidates, all of them wrong, and the experiment it
proposed (`.vhmask off`) could not have separated anything - the seams are geometry.

### G52 — A GPU-side saving measured in a CPU-bound scene reports its cost and none of its benefit

**Trigger:** comparing frame rate with and without the chunk mask, or any other change whose
saving is fragment shading rather than draw submission.

**Trap:** the mask does not simply draw less. With it off, cached terrain is suppressed
inside a plain radius; with it on, that radius is pulled in to `MaskNearFloor()` and the
per-cell mask decides per fragment instead. So the mask SUBMITS MORE cached geometry and pays
one texture fetch per fragment to throw most of it away. The saving is GPU-side and only
materialises where vanilla genuinely covers the ground.

Measure that in a scene the CPU is limiting and the benefit is invisible while the overhead
is not, so the change reads as a regression. Measured in game on 0.3.23:

| vanilla render distance | mask off | mask on | |
|---|---|---|---|
| 320 | ~315 FPS | ~310 FPS | CPU-bound: mask costs ~1.6% |
| 1024 | ~180 FPS | ~190 FPS | GPU-bound: mask gains ~5.6% |

Same build, same machine, opposite conclusions. A test run only at 320 would have condemned
the feature.

**Do:** state which resource is limiting the scene before quoting a frame-rate delta, and
measure a fragment-side change where the GPU is the limit - high render distance, vanilla
terrain filling the screen. A number without that context is not portable to another
setting, and the earlier +7.3% stationary pair has the same weakness.

**Do:** remember that the mod exists for long view distances. Where the mask costs anything
is a regime nobody runs it in, which is what makes the default defensible rather than merely
harmless.

**Know:** this compounds a standing limitation - GPU bottleneck attribution has never been
measured on this project, and CPU/render-thread phase counters (`DrawCost`, `WalkCost`) can
report a subsystem healthy while the GPU carries the cost. They cannot see a fragment saving
either.

**Know:** toggling the mask on disposes and rebuilds its texture, so an average that starts
accumulating immediately after `.vhmask on` includes the rebuild and penalises the "on" side.

**Found:** 2026-08-20, from the owner measuring both render distances instead of one.

### G53 — Two-sided or unordered opaque terrain silently spends GPU time

**Trigger:** optimizing distant cached terrain after CPU traversal and draw preparation look
healthy, especially when frame rate changes sharply with view direction.

**Trap:** opaque faces were rendered two-sided, and the mesher's vertical face winding was
not consistently outward. Simply enabling back-face culling would therefore have removed
west/east/north/south faces. After submission, quadtree/hash traversal order also gave the
depth buffer no useful near-first guarantee, so mountains and ground hidden by nearer
terrain could shade before the nearer depth existed.

**Do:** make all six solid face directions outward counter-clockwise before enabling the
engine's opaque-stage back-face state. Keep water and thin/cutout surfaces two-sided. Order
only selected opaque sections nearest-first using reusable storage and a distance computed
once per section; keep translucent water in traversal order. Neither change is true
occlusion, and neither may make visibility control residency.

**Evidence:** on the owner's machine and a fixed view, back-face culling raised 218 FPS to
260 FPS (+19.3%, about 4.59 to 3.85 ms per frame, a 0.74 ms reduction). Front-to-back opaque
submission raised 149 FPS to 173 FPS (+16.1%, about 6.71 to 5.78 ms, a 0.93 ms reduction).
The owner saw no visual difference in either comparison and specifically exercised cliffs,
caves, overhangs, high views and low views for the winding-sensitive change.

**Know:** this is strong causal evidence for wasted raster/fragment work on that machine,
not a portable benchmark. Drivers, scenes and hardware remain uncontrolled. Looking across
roughly 4,000 blocks of cached mountainous terrain still measured about 150 FPS against more
than 300 FPS when facing away, at least a 3.33 ms direction-dependent remainder and the case
future conservative occlusion must address.

**Found:** 2026-08-20, from separate owner-run `.vhbackface` and `.vhfront` comparisons.

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
