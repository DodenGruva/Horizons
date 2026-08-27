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

### G54 — A high occlusion-query rejection rate can save no time

**Trigger:** a nearby vanilla hill hides kilometres of cached terrain, while the cached
renderer runs immediately before vanilla terrain and GPU work still changes sharply with
view direction.

**Trap:** same-frame `GL_SAMPLES_PASSED` boxes eventually reported 83% hidden, yet conditional
rendering changed 156 FPS to 155 FPS. The percentage counted boxes rather than work, the CPU
still prepared and submitted every real mesh, and proxy raster/query dependencies replaced
the fragment work they suppressed. More fundamentally, Vintage Horizons queried at opaque
order 0.36 and vanilla terrain drew at 0.37, so the current hill the player expected to be
the occluder did not exist in depth yet.

**Do:** put the established occluder into depth first. Cached terrain now registers at 0.38,
then ordinary depth testing rejects its fragments behind vanilla terrain without proxy
geometry or query synchronization. Re-register when toggling because the engine sorts
`IRenderer.RenderOrder` only during registration. Keep `.vhocclusion off` at 0.36 as the
overlap/compatibility fallback, and keep visibility independent from residency.

**Evidence:** post-vanilla order raised the owner's valley view from 148 to 179 FPS (+20.9%,
about 6.76 to 5.59 ms, a 1.17 ms reduction). Looking down at vanilla ground raised 590 to
651 FPS (+10.3%) but saved only about 0.16 ms because the entire frame was already below
1.7 ms. The owner saw minute distant changes only through immediate toggling and judged
them entirely acceptable.

**Know:** FPS percentage exaggerates small savings at very high rates; compare frame time.
The accepted result is one-machine human evidence, not a GPU-timer attribution or a portable
benchmark.

**Found:** 2026-08-20, from the rejected query playtest and accepted render-order A/B.

### G55 — Global invalidation can make delayed occlusion appear to work only while paused

**Trigger:** reusing an asynchronous visibility result across frames while terrain,
readiness state, or a GPU ownership mask is still changing.

**Trap:** treating every local scene update as a reason to invalidate every query prevents
the state from ever becoming old enough to suppress a draw. During active exploration,
mesh uploads and chunk-dirty/readiness events can arrive every frame. The feature then looks
healthy in a settled test area and inert in a new one. Pausing stops the update stream, so
the same view suddenly accelerates; that is invalidation churn, not pause-specific GPU
behavior. In the owner's new-area test, the enclosed view stayed near 190 FPS while running
and rose to about 500 FPS while paused.

**Do:** scope invalidation to the identity that changed. Replacing a mesh invalidates that
section's result; mask upload and chunk-dirty notification do not erase unrelated answers.
Let periodic fail-open exact-geometry probes discover changed occlusion, and reject any
pending answer whose view epoch is stale. Report accepted, stale, and globally invalidated
results separately so a zero-skip state can be attributed.

**Found:** 0.3.35, after the same feature worked in one settled area, stopped in a streaming
area, and resumed only when the game was paused.

### G56 — Keeping visibility through camera turns needs explicit disocclusion guards

**Trigger:** preserving delayed hidden results while the camera rotates so culling does not
collapse under normal mouse steering.

**Trap:** invalidating every turn is visually conservative but gives away most of the gain;
retaining every answer indefinitely produces brief missing terrain where a fast turn reveals
the side of the old view. A whole-section answer is also too coarse at the mixed
vanilla/cache ownership seam: a small cache-owned remainder can disappear because the rest
of its section passed no samples.

**Do:** fail toward drawing at known disocclusion boundaries. Never suppress a mixed-
ownership seam section. Shorten hidden probe cadence while turning, and protect only a
narrow angular band at the left/right frustum edges while rotation is occurring. Keep an
unguarded profile available to expose the limit, but make the accepted default the guarded
policy. Visibility must still not evict or rebuild the resident mesh.

**Evidence:** the owner found the unguarded extreme profile visibly distorted the edge of
the screen during very fast turns. Aggressive retained substantial performance with the
artifact nearly unnoticeable; the final turning-edge guard was judged acceptable. An earlier
occasional seam loss motivated the mixed-section bypass.

**Found:** 0.3.33-0.3.37, through owner-run seam, motion, rapid-turn, and profile comparisons.

### G57 — Changed playable artifacts require a new patch identity

**Trigger:** creating or installing a zip, mod folder, playtest package, or other playable
artifact after source changes.

**Trap:** overwriting an existing version with a different binary makes logs, screenshots,
installed archives, hashes, and player reports disagree while all claiming to be the same
build. A filename and `modinfo.json` version are artifact identity, not merely release
decoration. A hash can prove two copies match, but it cannot make two different binaries
with one advertised version understandable later.

**Do:** before packaging or installing changed code, increment the current patch component
by exactly one in both `VintageHorizons/modinfo.json` and
`VintageHorizons/VintageHorizons.csproj`. Ordinary compile/check runs do not consume a
version. Use a larger minor or major jump only when the owner explicitly chooses one. Never
overwrite an already-used version with changed binaries.

**Found:** 2026-08-21, after a local performance build reused the existing 0.3.40 artifact
identity and the owner established the smallest-increment rule.

### G58 — An occlusion benchmark needs an occluder, and an uncapped run needs cap proof

**Trigger:** comparing cached-terrain occlusion modes in a visually demanding landscape or
using an automated client mode that keeps the game watched/focused.

**Trap:** terrain variety and cache size do not establish occlusion opportunity. A high
panoramic camera can expose kilometres of cached terrain while placing essentially none of
it behind vanilla foreground, so an on/off pair measures open-horizon cost and noise rather
than the feature being toggled. Separately, watch/focus behavior can engage the game's
refresh-rate/VSync limit; a stable figure near monitor refresh is not an uncapped baseline.

**Do:** inspect the actual depth relationship from the measured camera: the target cached
terrain must project behind a nearer vanilla hill, wall, cliff or equivalent foreground.
Record the launch/watch mode and verify observed FPS is not pinned to refresh before calling
the run uncapped. Preserve a mis-targeted pair only for the narrower evidence it actually
contains.

**Evidence:** the first Bodanboys watched route held near 170 FPS and was capped rather than
an uncapped renderer baseline. The corrected aerial temporal-on/off pair ran uncapped and
produced useful stable open-horizon GPU timings, but the owner observed that essentially no
cached terrain was behind vanilla terrain; its small FPS differences therefore cannot
measure temporal occlusion.

**Found:** 2026-08-21, during the Phase 0 Bodanboys GPU feasibility routes.

### G59 — A multi-draw batch is one buffer pair, so paired allocation is a correctness rule

**Trigger:** designing regional GPU arenas, or any allocator whose spans will later be drawn
by `glMultiDraw*Indirect`.

**Trap:** grouping pages by world region alone looks sufficient and is not. One indirect
batch binds exactly one vertex buffer and one index buffer, so a section whose vertices land
in one page and whose indices land in another can never be submitted with its neighbours.
The allocator passes every test that does not draw, and the defect only surfaces when the
draw path is designed - by which time the allocator has a phase's worth of work on top of it.

Splitting a shared memory ceiling between the two arenas by the byte ratio of the geometry
fails for the same reason. Pages are created in pairs, so the arena that runs out of pages
first caps both. A 70/30 byte split against 8 MiB and 4 MiB pages let the index arena refuse
new page sets while still 40% empty, capping the mirror at 19 sets and silently limiting
what could be measured.

**Do:** allocate vertex and index pages as a set, require both halves of a section to fit
the same set, and move to another set in the region when they do not. Split any shared
ceiling by page size so both arenas afford the same page count. A half allocated while the
other fails may be released immediately rather than fenced: nothing ever referenced it.

**Found:** 2026-08-22, designing Phase 3 on top of the Phase 2 arenas.

### G60 — A measurement that cannot see what it missed will report success

**Trigger:** instrumenting a shadow or mirror that covers only part of what the real path
does, then reporting a ratio over it.

**Trap:** the natural implementation looks up each item and returns early when it is absent.
That early return is invisible: the ratio is computed over whatever the mirror happened to
hold, and the counter meant to catch it reports zero because it, too, only counts items the
mirror already has. The first arena measurement reported "0 candidates not yet mirrored"
while missing roughly half the drawn sections, and produced a flattering 6x-8x figure over
about a third of the world. Nothing in the run looked wrong.

The underlying condition was ordinary: a 256 MiB ceiling against 851 MiB of live geometry.
The instrument's silence is what made it a trap rather than an obvious limit.

**Do:** count the misses explicitly at the point of the early return, derive coverage from
hits over hits-plus-misses, and print coverage beside every ratio the measurement produces.
Treat a ratio without a coverage figure as unreported, not as an approximation.

**Found:** 2026-08-22, on the first Bodanboys GPU arena measurement run.

### G61 — Rewriting a file on Windows without naming an encoding corrupts what you typed

**Trigger:** any edit that reads a repository file, changes it in memory and writes it back
through tooling whose default encoding is the Windows locale rather than UTF-8.

**Trap:** the round trip is byte-preserving for content it did not touch - cp1252 decodes and
re-encodes existing bytes unchanged - so nothing already in the file is harmed and nothing
looks wrong. Only the characters *newly typed* in that pass are written in the wrong
encoding. One em dash becomes a lone `0x97`, the file stops being valid UTF-8, it still
opens, and `dev/DocCheck.ps1` still passes. This was introduced twice in one session, in a
gotcha heading and a TODO heading, before anything noticed.

**Do:** name the encoding on every read and write, or keep the text ASCII. `StaticAssetChecks`
now fails the fast tier on any tracked text file that does not decode as UTF-8, and names the
byte.

**Repair:** do not decode the whole file as cp1252 and re-encode - that double-encodes every
sequence that was already correct. Decode as UTF-8 with `errors="surrogateescape"`, map the
surrogate-escaped bytes back through cp1252, and re-encode. **Restrict the pass to text
files:** a repair sweep that walked every tracked file mangled `modicon.png`, which was
restored from git.

**Found:** 2026-08-22, while adding G59 and G60.

### G62 — A per-interval rate from a benchmark log means nothing without its period

**Trigger:** quoting MiB/s, uploads/s, or any per-interval figure from a run's telemetry.

**Trap:** a benchmark run is two populations. Warm-up builds every mesh the world needs;
settled steady state builds almost none. Averaging them, or sampling one and describing it
as the other, produces a number that is real but answers a different question.

Measured 2026-08-22 on the frozen `bodanboys` route: warm-up intervals 1-9 carried 1,827
mesh uploads and 2,131 MiB, about **237 MiB per 15 seconds**; settled intervals 10-26
carried 54 uploads and 35 MiB, about **2.1 MiB per 15 seconds**, several doing nothing at
all. A 113x difference between two halves of one run.

`dev/TODO.md` had carried "204-289 MiB built and 76-119 MiB uploaded every 15 seconds,
continuously, at a standstill" as the lead symptom of a re-mesh amplification bug. That
figure reproduces the warm-up column almost exactly: it was a loading measurement recorded
as a steady-state one, and it made re-meshing the leading suspect for micro-hitches it
cannot cause. The same mistake was then made in the opposite direction while investigating
it, by reading the quiet tail intervals and concluding the cost was a measurement artefact.

**Do:** print or total the whole interval series, split it at the point the rate collapses,
and say which period a quoted number belongs to. A single tail sample and a whole-run
average are both wrong.

**Found:** 2026-08-22, session 40, while confirming the change-locality saving.

### G63 — An arithmetic identity is not a mechanism

**Trigger:** an estimate that multiplies out to the observed number.

**Trap:** `dev/TODO.md` explained 5,057 mesh replacements as "~145 change events x a 35-way
fan-out", and the product matched. Neither factor was measured: nothing counted
`MarkChanged` calls at all, and the 35 assumed the neighbour fan-out repeated at every mip
level unconditionally. Adding the counter gave **38-40** change events for the same route
and profile - a number that cannot reach 5,057 even at the old five-way fan-out.

The identity survived review for a whole session because it matched. The document even said
"no counter records actual `MarkChanged` calls; add one before quoting the amplification
factor as measured" - and the factor was then quoted anyway.

**Do:** add the counter before the explanation, not after it. When a document names a term
as unmeasured, that term may not appear in a derivation that is presented as measured.

**Found:** 2026-08-22, session 40.

### G64 — Repository text files carry mixed line endings, sometimes within one file

**Trigger:** editing `.md` or `.cs` files with a script rather than by hand.

**Trap:** `LodWorld.cs` and `LodMip.cs` hold both CRLF and LF lines; `dev/TODO.md` is CRLF
and the shaders are LF. A multi-line pattern written with `
` silently fails to match a
CRLF file, and a fix-up that normalises the whole file rewrites every line, burying the
real change in an unreviewable diff.

**Do:** match and splice whole lines, taking the ending from the line already there. Never
normalise a whole file to make a patch apply. See also G61 on encoding, which has the same
shape: the round trip preserves what it does not touch, so only newly typed text breaks.

**Found:** 2026-08-22, session 40.

### G65 — Cleanup must not run through the guard that protects the work

**Trigger:** a component that disables itself after a failure and also has state to restore.

**Trap:** `LodGpuIndirectDrawer.Try` returns immediately once the drawer has failed, and the
`finally` that restored the captured GL state called it - so restoration was skipped exactly
when a batch had been refused, and the engine's own renderer runs immediately afterwards.
The happy path was perfect and the failure path silently did nothing.

**Do:** run cleanup outside the failure guard, and assert in a check that it happened after a
refusal, not only after a success. A fake backend that records the call order is enough.

**Found:** 2026-08-22, session 41, by the check rather than by a run.

### G66 — A switch that changes how the renderer decides must invalidate what it decided

**Trigger:** adding a runtime toggle that changes the draw path, the render order, or how
visibility is established.

**Trap:** delayed occlusion caches per-section "hidden" answers across frames. Batched
drawing cannot issue those queries at all, so flipping `.vhindirect` back off left the
renderer acting on answers taken seconds earlier under a different path and a different
camera. The symptom would have been terrain missing straight after switching off - read as
the fast path losing terrain, in the exact comparison the switch exists for.

**Do:** call `InvalidateTemporalOcclusionScene` whenever the path that produced the answers
changes, and decide the path once per frame before anything resolves a query. The static
check counting invalidation sites is deliberately exact: raise it with a reason rather than
relaxing it.

**Found:** 2026-08-22, session 41, by review before the build was run.

### G67 — Player-visible text is parsed as VTML, so a leading "<" is a markup tag

`.vhheight` reported its buckets as `<=4: 0%, <=16: 0%, ...`. The periodic log line was
fine, but the same string returned as a chat command result was refused outright by the
game's text renderer: `Found closing tag <font> at position 1536 but <=4:> should be closed
first`. The engine parses chat and dialog text as VTML, so `<=4:` opened an element that
was never closed and took the rest of the reply down with it. The command appeared to do
nothing.

**Do:** never let a string that can reach chat or a dialog contain a bare `<`. Write ranges
(`0-4`, `4-16`) rather than comparisons. A check pins it: the distribution's own
`Describe` output must contain no `<` at all.

**Found:** 2026-08-22, session 42, from the owner's client log after a playtest.

### G68 — The first shader load always fails, and saying so as an error hides the real one

`LodTerrainRenderer` calls `LoadShader()` from mod start, which runs before the engine has
filled its shader-include table from mod assets. The body is therefore never spliced, both
programs fail to link, and the mod logged `lodterrain shader failed to compile; LOD
rendering disabled` plus the indirect variant's warning - on every single start. The engine
then fires `ReloadShader` and the second attempt succeeds, which is why terrain drew
anyway.

The cost was not cosmetic. A session read those errors in a client log, concluded the
indirect variant does not compile on the owner's hardware, and would have spent its time on
a shader that was working. The engine's own startup-issue summary replays the same two
errors seconds later, which makes them look like two independent failures rather than one
expected one.

**Do:** treat the first shader attempt as provisional - notification level, naming what it
is waiting for - and report at full volume from the second attempt on. Log the SUCCESS once
too: a log that only ever complains cannot answer "did this work".

**Found:** 2026-08-22, session 42, while reading the owner's playtest log.

### G69 - The engine's shader bookkeeping is separate from the GL binding

**Trigger:** using `IShaderProgram.Use()` anywhere outside the ordinary draw path.

**Trap:** `ShaderProgramBase` records which program is in use in its own static state. A GL
state guard restores `glUseProgram` and leaves that record untouched, so the next
`prog.Use()` throws `Already a different shader (hzbreduce) in use!` and the client dies.
The depth pyramid did exactly this on its first frame on real hardware; every other shader
in the renderer already pairs `Use()` with `Stop()`.

**Do:** pair every `Use()` with a `Stop()` in a `finally`. Restoring raw GL state is not a
substitute. For programs the engine does not need to know about - a compute program, for
instance - create them through raw GL and never enter its bookkeeping at all.

**Found:** 2026-08-22, session 42, on the first benchmark run of the code.

### G70 - `sample` is a reserved word in GLSL 4.x

**Trigger:** naming a local in a shader.

**Trap:** `float sample = texelFetch(...)` fails with `syntax error, unexpected SAMPLE`.
It is an interpolation/storage qualifier. No offline check can catch this: the C# twin that
mirrors the shader logic carries the tests, and C# is perfectly happy with the identifier.

**Do:** avoid `sample`, `filter`, `input`, `output`, `active` and the other qualifier words
as identifiers. Treat a shader compile as something only a run can verify, and make the
failure path warn once and keep rendering rather than throw.

**Found:** 2026-08-22, session 42.

### G71 - Reading an SSBO stalls the render thread unless a fence says otherwise

**Trigger:** `GetBufferSubData` or any map-for-read on a buffer the GPU writes.

**Trap:** "it was written a frame ago" is not a guarantee the driver honours. Reading
per-section classification results every frame turned a 20 us phase into 686 us - 28% of a
2.4 ms frame - because the call blocks until the work lands. The CPU runs several frames
ahead at high frame rates, so one frame of latency is not enough either.

**Do:** fence after the dispatch (`FenceSync`), then `ClientWaitSync` with a **zero
timeout** and skip the sample when it is not ready. Never wait. Note that the skip rate is
load-dependent: the same code skipped 99.8% of frames in a light scene and 2% in a heavy
one, so report reads and skips or the statistics silently rest on a handful of frames.

**Found:** 2026-08-22, session 42.

### G72 - A diagnostic the owner cannot read is not a diagnostic

**Trigger:** adding any telemetry a person is expected to report back.

**Trap:** two owner playtests were spent producing correct numbers that could not be
retrieved. The first put them in the periodic stats block, which fires **once, thirty
seconds after joining** - before anyone has had time to enable the feature it measures. The
second put them in a chat command reply, and **game chat cannot be selected or copied**, so
the only options were retyping long digit strings or a photograph.

**Do:** write every diagnostic into the client log, line by line so each carries a timestamp
and stays greppable, in addition to whatever it shows on screen. Make it available on demand
rather than on an interval. Assume the reader will hand the log to someone else.

**Found:** 2026-08-22, session 42, twice in one session.

### G73 - A cross-check between two asynchronous measurements has a noise floor

**Trigger:** validating one visibility mechanism against another, or any "this count must be
zero" gate whose two sides are sampled at different times.

**Trap:** the depth pyramid judges last frame's depth buffer; the delayed occlusion query
reports an actual draw some frames earlier. When the camera moves, both can be right and
still disagree. Session 42 read seven such disagreements in 2,922 comparisons as a defect,
made it Phase 4's blocking gate, and spent two owner playtests chasing a number that cannot
reach zero by construction. A view-epoch guard was added on the strength of the same
reasoning and refused **zero** comparisons, which should have been the tell.

**Do:** establish correctness from a deterministic offline fixture - for this renderer, a
synthetic occluder with background around it, and boxes that poke out of it by a hair. Keep
the runtime cross-check for spotting gross divergence and for figures it is genuinely good at
(how much one mechanism finds that the other has not measured). Before gating on any count,
ask what its floor is when everything works.

**Found:** 2026-08-23, session 43, after the owner asked whether the work was really blocked.

### G74 - A box clears an occluder only by a whole texel of its own test level

**Trigger:** reasoning about why hierarchical depth culling rejects so little.

**Trap:** the pyramid level is chosen so a box spans at most N texels, so a bigger box is
tested at a coarser level. A texel that overlaps the box also covers everything within its
own footprint - 256 screen pixels at level 8 - so a box sitting inside an occluder by less
than one texel still samples the background beyond it and correctly refuses to hide. The
clearance a box needs therefore grows with the box. A synthetic fixture whose test box was
comfortably inside the occluder in pixels failed for exactly this reason.

**Do:** state the limit as clearance-in-texels, not "sections are too wide". It has two
independent levers - make the boxes smaller (cluster subdivision, which changes what is
drawn) or sample more texels at a finer level (which changes only the test). Measure both
over the same population before choosing; offline, raising the footprint from two texels to
eight found about a quarter more hidden boxes.

**Found:** 2026-08-23, session 43, from a failing offline fixture.

### G75 - A flag whose precondition lives in the shader must not be re-gated by its reader

**Trigger:** changing what a packed result bit means, and leaving the counter that reads it
where it was.

**Trap:** the classifier's wide-sampling bit was inverted so the shader set it only for
sections it HAD hidden. The C# counter still incremented it only for sections that were NOT
hidden, from when the bit meant the opposite. The two conditions became mutually exclusive, so
the figure was structurally zero whatever the terrain did - and it read as "widening the
sampling bought nothing", which is a plausible enough result to believe. It survived a whole
playtest and was only caught because the owner's log printed the zero.

**Do:** count a flag that already carries its own precondition with no gate at all, and pin the
precondition in a check next to the code that reads it. A number that can only be zero is worse
than a missing number: a missing one prompts a question.

### G76 - A status line in a report that fires once cannot observe a switch flipped later

**Trigger:** adding telemetry so a playtest reports its own configuration.

**Trap:** the client's periodic report runs once, thirty seconds after world load, unless
allocation telemetry is switched on. A cull-status line added to it could therefore only ever
capture the state before anyone typed a command, so a playtest of a default-off feature
recorded "off" every time and the run could not be attributed afterwards. This is G72 again in
a new shape: the diagnostic existed, and still could not answer the question it was built for.

**Do:** for anything a person switches on mid-session, log from the command handler itself -
`.vhhzb` already did, `.vhcull` did not. Ask what the reporting cadence is before relying on a
periodic line, and prefer event-driven evidence for state a human controls.

### G77 - An offline harness must reproduce the whole approval chain, not just the test

**Trigger:** rebuilding an in-game measurement offline so it stops costing playtests.

**Trap:** two independent faults, both of which produced clean and believable tables. Omitting
the renderer's frustum cull left the population as the whole world including everything behind
the camera, which the projection then refused as near-plane crossings - 70% of the first run's
"results". Placing each camera at the maximum surface of its own section stood the viewer on a
hilltop every time, where nothing can be occluded by anything; that run reported 7.5% hidden
against 46-88% in game.

**Do:** reproduce every CPU decision that gates the population, and choose viewpoints the way
the terrain is actually occupied - the centre column's surface, not the section's maximum. Then
validate the harness against a real in-game figure at the same settings before trusting it;
this one matched the game's 0-1k band to within a point once corrected.

### G78 - A pass that activates a shader cannot be dropped between two draw passes

**Trigger:** inserting any GL work that binds its own program into the middle of a render pass.

**Trap:** the depth-pyramid build was moved between the opaque and water passes so the picture
would contain cached terrain but not depth-writing water. The engine refuses to activate a second
shader while one is in use - "Already a different shader (lodterrain) in use!" - so the build
failed on **every frame of an entire playtest**: 0 of 4,047. It failed safe, because a frame with
no picture draws everything, which is exactly why nobody would have noticed from the screen.

**Do:** release the current program around the insertion and take it back afterwards. Uniforms
belong to the program object rather than to the binding, so the pass that follows finds its values
untouched. And count the successes: `builds completed` against `builds attempted` is what turned a
silent total failure into a one-line diagnosis.

**Found:** 2026-08-24, session 45.

### G79 - A fence asked once and then deleted throws the work away

**Trigger:** any GPU readback polled with a zero timeout.

**Trap:** the classifier fenced each dispatch, asked once on the following frame, and deleted the
fence when the answer was "not yet" - abandoning that dispatch's results forever. It read **2 of
2,212** dispatches while paying 149us a frame for all of them, six times the cost of the pyramid it
was reporting on, and most of a 30 FPS drop. G71 says to ask and never wait, which is right; it
does not say to discard.

**Do:** keep the fence and ask again next frame, and do not start another dispatch while a result
is still outstanding. That alone makes the pass self-throttling at the readback rate. Report reads
against skips so a pass that is collecting almost nothing says so.

**Found:** 2026-08-24, session 45.

### G80 - Timing a pass that did not run dilutes the number it exists to produce

**Trigger:** a delayed GPU timer wrapped around work that can be skipped.

**Trap:** the pyramid timer opened and closed around builds that never happened - a minimised
window, a refused depth attachment - filing a near-zero sample each time. The client reported
**4.8us over 256,368 timed builds when 50,733 had actually run**, dragging a genuine 24us down
fivefold. That figure is the gate for the whole phase, and it was wrong in the flattering
direction.

**Do:** close the query but discard its result when the work did not happen, and print the sample
count beside the average so the two can be compared. A ring already tracking a measurement epoch
can discard by stamping the slot stale, which reuses a path that is already tested.

**Found:** 2026-08-24, session 45.

### G81 - A resource ceiling that silently refuses turns every measurement into a measurement of the ceiling

**Trigger:** any fixed pool the renderer falls back from gracefully.

**Trap:** the GPU arena's fixed 256 MiB refused 322 sections and left **35% of drawn terrain**
outside the batched path. Everything still drew, correctly, by the slower route - so the only
symptom was that batching and depth culling could not affect two thirds of the screen, and two
sessions of A/B comparisons through it measured the pool rather than the feature. Raising it took
coverage to 100% and the owner from 300 to 480 FPS.

**Do:** size the pool from the setting that determines the work, not from a constant, and check
the refusal counter before trusting any comparison taken through it. A ceiling that is a cap with
demand-driven commitment costs nothing when generous, so there is no reason to run one tight.

**Found:** 2026-08-24, session 45.

### G82 - Never infer where on screen a visual defect appeared

**Trigger:** the owner reports terrain flickering, vanishing or tearing.

**Trap:** a previous-frame depth test produced flicker while the camera turned. The obvious
mechanism is screen-edge clamping - the projection clamps a box's rectangle to the visible part
and applies that verdict to the whole section - and a guard was designed, built, checked and
shipped on that theory. The owner had seen the failures in the **middle** of the screen, and said
so as soon as the assumption was stated. The guard was aimed at the wrong place entirely, and the
inference had also been used to argue the underlying design was salvageable.

**Do:** ask where on the screen, at what distance, and whether it recovers on its own, before
proposing a mechanism. He answers precisely and immediately; the question costs one exchange and
this one would have saved a build, a guard, a set of checks and a wrong conclusion about the
design. Keep an exact-angle capture view-wide and label projected regions. Do not require the
defect under the crosshair: the angle that exposes it generally points the crosshair somewhere
else. In the current Phase 8 case, most affected meshes are inside visible terrain rather than at
the sky silhouette.

**Found:** 2026-08-24, session 45.

### G83 - A model that agrees with a sparse world is not validated by it

**Trigger:** checking a capacity or coverage model against this machine's caches.

**Trap:** the arena sizing model predicted 560 sections at the owner's draw distance and his
client reported 580 resident, which was written up as validation and into a check. It was the
opposite: his world is a partly-explored corridor rather than a full circle, so a model that
matches a **sparse** world's residency necessarily falls short of a populated one. The same
mistake had already been made once in the same session, reasoning about how much Phase 6 would
buy from band populations that a grown world would not have.

**Do:** state what the model counts and what the measurement counted before comparing them. Every
cache on this machine is far-band starved - the most developed covers 16,384 x 12,288 blocks at L6
- so no local measurement can confirm a model of a mature world, and the honest move is a margin
with the reason recorded rather than a number presented as derived.

**Found:** 2026-08-24, session 45.

### G84 - An std430 `uvec3` array does not have a twelve-byte stride

**Trigger:** packing three 32-bit words into an SSBO record.

**Trap:** the struct looks twelve bytes wide in source, but std430 arrays round a three-component
vector's stride to sixteen bytes. Using `uvec3[]` would add 33% to the packed arena while the CPU,
tests and telemetry continued calling the format twelve bytes.

**Do:** pull the buffer as scalar `uint[]` and index three words per record, or explicitly accept
and report a sixteen-byte GPU format. Pin the shader declaration as well as the CPU constants.

**Found:** 2026-08-24, session 46.

### G85 - A shared compute command layout constrains a new indirect draw type

**Trigger:** changing from indexed indirect draws to draw-arrays indirect draws while reusing a
compute cull that edits command slots.

**Trap:** `DrawElementsIndirectCommand` is five words and `DrawArraysIndirectCommand` is four.
Packing the latter naturally changes every later slot address, while the cull shader still writes
`commands[index * 5 + 1]`. The result is suppression editing the wrong commands.

**Do:** retain the five-word stride and pad draw-arrays commands until the producer, compute shader,
draw call and deterministic layout checks are deliberately migrated together.

**Found:** 2026-08-24, session 46.

### G86 - Geometry-format savings are not process-memory savings during dual publication

**Trigger:** validating a packed format beside the established representation.

**Trap:** twelve bytes versus eighty-eight is an 86.4% representation reduction, but publishing
both regional forms temporarily increases total arena memory, and legacy meshes remain resident
for fallback. Reporting the format ratio as total memory saved would claim a benefit the process
does not yet realize.

**Do:** report packed and expanded regional bytes separately during the A/B. After visual and timing
acceptance, stop retaining the expanded regional copy on the selected product path; measure total
process memory separately.

**Found:** 2026-08-24, session 46.

### G87 - Packing bytes can lose if it increases unique vertex work

**Trigger:** replacing indexed mesh vertices with procedural vertex pulling.

**Trap:** 0.3.85 reduced each regional opaque quad from 88 bytes to 12 and matched the picture, but
used draw-arrays expansion for six full shader invocations. The accepted indexed mesh shades four
unique corners and reuses two of them. On the primary driver the same scene fell from about 300 to
260 FPS—roughly 0.51 ms or 13% slower—so the bandwidth win did not pay for 50% more decoding.

**Do:** compare unique post-transform vertices, not only stored bytes or triangle vertices. For
packed quads, keep one reusable `0,1,2,0,2,3` index pattern and rebase four virtual corners per
record; retain the expanded path until total frame time proves the replacement.

**Evidence:** 0.3.86 made exactly that topology change. In the owner's same scene it remained
visually identical and restored packed performance to the exact same FPS as expanded batching.
The result isolates the extra unique vertex work as the 0.3.85 regression and establishes that the
12-byte decoder itself is neutral in this view; it does not establish a standalone FPS gain.

**Found:** 2026-08-24, session 46.

### G88 - Strict depth inequality is not a conservative occlusion verdict

**Trigger:** an HZB test compares a projected box's nearest depth with rasterized scene depth,
especially after one section becomes several independently culled clusters.

**Trap:** projection and triangle rasterization reach depth through different floating-point and
fixed-point steps. Treating every `nearest > farthest` result as hidden lets a representable rounding
difference cancel a coplanar draw. The failure looks like a normally shaped terrain piece flickering
at a very precise camera angle, including while stationary. A 4x4 cluster path multiplied the number
of independent decisions and made several more pieces show it; `.vhcull off` stopped all old and new
cases while clusters remained enabled, clearing the geometry ranges.

**Do:** reserve an explicit fail-open depth band and pin it in the CPU reference and shader. Start
from the actual depth representation rather than an arbitrary world-space distance, then measure
how much valid rejection remains. A pre-existing offline fixture reproduced the exact one-ULP
self-occlusion and should remain the regression case. A borderline verdict draws.

**Found:** 2026-08-24, Phase 8 owner playtest.

### G89 - A dependent experiment needs one authoritative switch

**Trigger:** a player-facing A/B requires several feature flags whose meaning depends on each
other.

**Trap:** Phase 8 accumulated seven controls as separately measurable stages: arenas, batching,
packed geometry, HZB, culling, same-frame cached depth, and clusters. A 0.3.88 follow-up looked like
the conservative depth margin had erased performance, but the log showed late depth initially off
and clusters `on, but idle` because packed drawing was off. Every individual command behaved as
documented; the experiment as a whole was still invalid, and asking the owner to maintain the
dependency graph was the defect.

**Do:** expose one master command that changes every prerequisite atomically, reports `ON`, `OFF`,
or `MIXED`, and logs the same state. Keep component commands for diagnosis, not ordinary setup. A
performance conclusion taken from a mixed state does not count.

**Found:** 2026-08-24, 0.3.88 Phase 8 follow-up.

### G90 - One command does not mean one experimental variable

**Trigger:** a master command spans several stages that were built and measured separately.

**Trap:** `.vhphase8 on|off` fixed hidden operator state, but it collapsed batching, HZB culling,
same-frame cached depth, packed geometry, and clusters into one comparison. When the complete stack
flickered and lost to legacy, that result could not identify which later addition erased the
substantial wins measured earlier. The state was valid; the experiment was too broad.

**Do:** keep one authoritative command, but give it named cumulative presets in implementation
order. Every preset must assign every dependent flag, not merely enable its new feature. Compare
adjacent settled presets and record the first visual or performance regression; only then narrow to
component diagnostics.

**Found:** 2026-08-24, 0.3.89 complete-stack owner comparison.

### G91 - An idempotent-looking preset can restart expensive publication

**Trigger:** a preset assigns the same enabled resource state already requested.

**Trap:** `RequestGpuShadow("on")` is intentionally an active rebuild request, not a harmless
boolean assignment. The first preset ladder called it for every active stage, so selecting `late`
while arenas were already on queued all 1,678 live sections for re-mesh. The flags were correct,
but the FPS comparison measured warm-up and every adjacent step would have repeated it.

**Do:** compare the requested resource state before invoking transition methods. Cumulative presets
may freely assign cheap feature flags, but they must preserve shared filled resources until the
preset actually crosses their ownership boundary. Log queued rebuild counts and treat any nonzero
unexpected count as a contaminated performance interval.

**Found:** 2026-08-24, first 0.3.90 preset-ladder test.

### G92 - A demand-driven producer cannot require its own first product

**Trigger:** bootstrapping persisted terrain through the ordinary render-selection walk.

**Trap:** stored cache rows register only key metadata, while the renderer skips selection until at
least one mesh exists. Selection is the path that requests meshes; mesh scheduling is the path that
starts asynchronous loads. With an empty dirty set the cycle produces no work at all. On a
5,317-section cache it logged 0 dirty/loads/mesh jobs at ten and thirty seconds, then waited 75.1
seconds for newly captured data to create the first mesh accidentally.

**Do:** give key-only persisted state an explicit bounded bootstrap planner that runs without an
existing mesh. Reuse async loading and all owning-thread budgets; do not mistake a zero-work period
for a throughput problem or solve it by raising queue limits.

**Found:** 2026-08-24, ordinary 0.3.90 join and source trace.

### G93 - Frustum culling before demand makes the camera a load switch

**Trigger:** combining visibility traversal with load/mesh request generation.

**Trap:** `CollectDrawNodes` rejects an out-of-frustum subtree before `RequestMesh` or child descent.
Residency is correctly independent of visibility, but demand is not: terrain behind the camera
remains unloaded or coarse until the player turns to look at it. A nearest-first scheduler cannot
repair obligations it was never given.

**Do:** keep the frustum authoritative for drawing, not for whether persisted coverage/refinement
can ever become eligible. Make orientation absent from demand ordering; service radial lanes fairly
and let turning change only which already-planned terrain is drawn.

**Found:** 2026-08-24, owner loading report and source trace.

### G94 - Appearance readiness can lag behind data readiness

**Trigger:** making persisted terrain load and mesh earlier than its client-only tint palette.

**Trap:** the first seasonal refresh can complete before any cache palette has registered its
grass, foliage, or water slots. New slots default to identity white, but a time-only 30-second
cadence treats the table as current. Geometry then appears promptly with visibly wrong colours and
snaps to the correct climate/season palette at the next refresh. Faster loading exposed the race;
it did not create the colour error.

**Do:** make newly registered appearance state invalidate the cadence immediately, keep its work
incremental and atomically published, and retain each affected mesh obligation until every tint it
uses is ready. Data-resident is not necessarily reveal-ready.

**Found:** 2026-08-24, first accepted 0.3.93 radial-loading playtest.

### G95 - A verdict transition is not necessarily a draw-state transition

**Trigger:** ranking commands that alternate among several diagnostic verdicts in a GPU cull
capture.

**Trap:** `visible` and `background` are different explanations but both leave the command drawn.
The 0.3.99 capture ranked 6,592 of those harmless transitions ahead of 8,666
`occluded/background` transitions that actually switched drawing off and on. A plausible sky-edge
story then dominated the investigation even though the owner's visible failures were mostly
inside terrain.

**Do:** define the externally observable state first. Count explanatory verdict changes for
context, but rank only cull/draw transitions; separately track whether a command identity appeared
or disappeared before compute. Capture every upstream bucket that can change the downstream depth
picture, and retain stable identities and screen regions so the report can be matched to the
owner's observation.

**Found:** 2026-08-24, 0.3.99 flicker capture and 0.3.100 diagnostic correction.

### G96 - A mip texel covers 2^level pixels, not one over the level's texel count

**Trigger:** converting a screen-space rectangle into texel indices at a chosen pyramid level.

**Trap:** mip dimensions halve with floor, and the reduction folds any leftover odd row or column
into the LAST texel, so a level-L texel stands for exactly `2^L` source pixels with the final texel
absorbing the remainder. Scaling normalized bounds by the level's texel COUNT assumes an even
`1/count` share instead, and the two agree only while `levelSize * 2^level == screenSize`. The
computed index is never larger than the true one, so the far edge of the sampled rectangle falls one
texel SHORT - and the texel dropped that way is the one holding whatever lies beyond an occluder's
silhouette. Terrain peeking over a ridge was judged hidden, which the conservatism rule forbids
outright.

**Work the arithmetic for the real display, not an assumed one.** The drift begins at the first
level whose size has gone odd, and the two axes generally differ. On the owner's 1920x1080 the
height chain reaches 135 and then 67, so `67 * 16 = 1072` against 1080 and every level from **4**
up is affected; the width chain reaches 15 and then 7, so levels **8** and above are affected too.
Sessions 50 and 51 wrote this up for an assumed 2560x1440, where the onset is level 6 vertically and
width happens to divide exactly - which understated how much of the pyramid was involved. A
dimension that does divide exactly shows nothing at all, so a single tested resolution can hide the
fault completely.

**Do:** anchor the rectangle in screen pixels first, then shift right by the level, then clamp the
far edge to `levelSize - 1` - that clamp is what honours the fold, because the leftover pixels
genuinely live in the last texel. Expect a box to touch one more texel than the level was sized for
and keep any span guard strictly-greater-than, or the anchoring converts real culls into fail-opens.

**Found:** 2026-08-24, session 50, the cause of the Phase 8 precise-angle flicker. Resolution
arithmetic corrected 2026-08-25 from the owner's log, after two write-ups assumed the wrong display.

### G97 - A reference implementation that mirrors a shader cannot catch a shared assumption

**Trigger:** relying on a CPU twin of GPU logic, deliberately written statement-for-statement, to
prove that logic correct.

**Trap:** the arrangement is designed to keep the two identical, so it detects divergence and
nothing else. G96 lived in both copies at once and 5,073 assertions passed over it for several
sessions, because every check compared the test against its own twin rather than against the
artifact the test is supposed to describe. The pyramid's real texel footprints were never tied to
the projection's arithmetic anywhere.

**Do:** make at least one check cross the boundary - build the other artifact for real (reduce an
actual pyramid at an actual screen size) and assert the verdict, not the intermediate arithmetic.
Choose dimensions that are hostile rather than convenient: a size that divides exactly at every
level cannot expose an alignment fault. And when a fix is proposed for a defect a mirror missed,
run the fixture against the UNFIXED code first; a fixture that does not fail before the change is
not evidence that the change was needed.

**Found:** 2026-08-24, session 50.

### G98 - A figure measured over randomly placed cameras carries seed noise; quote the spread

**Trigger:** citing a single percentage produced by an offline harness that samples camera
positions and yaws.

**Trap:** `HzbField`'s widening result was recorded as "eight hides 25.0%" and quoted that way for
sessions, including inside a source comment justifying a shipped constant. Re-running it on
2026-08-24 gave 18.4% and 25.6% from two camera seeds under otherwise identical conditions - a
seven-point spread from the seed alone, larger than the effect of the 0.3.101 mapping correction or
of a 350-versus-192-block vanilla distance. The decimal place implied a precision the measurement
never had, and a later session could easily have "detected a regression" that was only a different
seed. The same run also showed the recorded "sixteen adds about 2.5 points" was really 3.9 to 5.4.

**Do:** run at least two seeds and report the range, not a single value. Prefer the property that
is stable across seeds - here the ORDERING of wide-4, wide-8 and wide-16, which held in every run
and is what the choice of eight actually rests on - and gate decisions on that rather than on a
midpoint. When quoting such a figure in source or docs, name the seed, the cache and the modelled
view distance beside it, because all three move it. See also G73 on cross-checks whose noise floor
is above zero by construction.

**Found:** 2026-08-24, re-measuring the widening figure after the 0.3.101 texel-mapping fix.

### G99 - Shipping ahead of a validation gate does not complete the gate

**Trigger:** a feature is made the product default on an explicit owner decision before its plan's
hardening or portability matrix is complete.

**Trap:** later summaries naturally treat "shipped and accepted" as "the plan finished." That
silently converts unrun checks into implied evidence, or lets a couple of visible evidence gaps
replace the actual remaining phase. Retiring one of those checks later has the same risk: retirement
removes work from scope; it does not make the result portable, measured, or passed.

**Do:** mark every affected gate explicitly as PASSED, RETIRED, or OPEN. Preserve the evidence
boundary for retired checks, and keep the phase open until its remaining work is completed or the
owner explicitly retires the phase itself.

**Found:** 2026-08-25, session 54, when the accepted 0.4.0 default obscured the still-open Phase 9
hardening boundary.

### G100 - A lower development version cannot outrank a preserved higher test zip

**Trigger:** returning a development line from a premature minor promotion to the next patch on
the earlier minor line while copy-only rollback artifacts remain in the normal Mods folder.

**Trap:** Vintage Story selects the highest version. Copying 0.3.104 beside the preserved 0.4.0
looks like a successful install but an ordinary launch still loads 0.4.0, so any observation would
be attributed to the wrong binary. The repository rule correctly forbids silently deleting or
moving the rollback zip; version demotion and ordinary Mods-folder selection are therefore
incompatible without a separate explicit owner decision.

**Do:** run the lower version through the repository's isolated add-mod-path runner, which selects
the current build independently. If ordinary play must select it, ask the owner explicitly what to
do with the higher rollback artifact; never hide that operation inside packaging.

**Found:** 2026-08-25, session 55, after packaging and copy-installing 0.3.104 beside 0.4.0.

### G101 - A dependent feature can invalidate a format-only comparison

**Trigger:** comparing packed and expanded geometry while leaving clustered drawing enabled.

**Trap:** clusters consume packed records. Turning packing off therefore makes clusters idle too,
so the pair changes both geometry decoding and command granularity. The results may look stable and
still be incapable of answering the format-cost question.

**Do:** pin dependent stages to the same effective state in both halves. The valid Phase 9 ABBA
matrix held clusters off and changed only packing; record effective-path telemetry beside requested
switches.

**Found:** 2026-08-25, session 56.

### G102 - A completed benchmark can still have silently empty telemetry

**Trigger:** a log sentence evolves while the benchmark parser still matches its older shape.

**Trap:** route timing and CSV publication can succeed even when GPU timer and arena arrays are
empty. Treating completion as parser validation loses the exact evidence the run was meant to
collect.

**Do:** assert expected sample cardinality before interpreting a run, and preserve the raw log so a
parser correction can recover it. The Phase 9 runner now understands split-near/split-far timers,
upload tails, and per-format live/committed arena bytes.

**Found:** 2026-08-25, session 56.

### G103 - An aggregate bound over descendants is redundant where a per-item bound already exists

**Trigger:** adding a bound that unions over children when each child already carries its own.

**Trap:** at a leaf the aggregate IS the item's own bound, and usually a looser version of it - the
subtree box unions the passes the per-section box splits. A looser box can only reject a subset of
what the tighter test already rejects, so the new work is redundant exactly where it fires most
often. Session 57 measured three of five views rejecting only single-mesh nodes, which removed
draws the per-section test was already removing.

**Do:** establish that the new bound is tighter than the existing one somewhere that matters before
funding it, and measure the cost of the thing being optimised first. The quadtree walk this was
aimed at costs 44.3 us of a roughly 2000 us frame, so 2% was the whole ceiling.

**Found:** 2026-08-26, session 57.

### G104 - Assuming unknown space is open makes it a source, not just a hole

**Trigger:** any flood, light or spread where absent data is resolved toward "open" for safety.

**Trap:** open air is also an emitter. In session 57 the cave rule seeded daylight from the edge of
its working window and from absent neighbours, both of which are the conservative choice - and both
became false illuminants that rescued caves the rule existed to remove. A window margin equal to
the light's own reach put its wall exactly in range, and a mesh job with no diagonal neighbours had
four section-sized blocks of imaginary sky against its corners. Together they were the difference
between removing 15.5% of the geometry and 32.7%.

**Do:** keep an assumed-open boundary further away than the effect can travel, and supply real data
for every direction the effect can arrive from - including diagonals, which the mesher never needed
and the flood does.

**Found:** 2026-08-26, session 57.

### G105 - A harness built beside the feature it measures can agree with it and still be wrong

**Trigger:** validating an optimisation against an offline model rather than against the game.

**Trap:** session 57's cave harness and the shipping code agreed with each other across every
sample, level and configuration, and both disagreed with the game by SIGN: the harness measured a
third of the geometry removed, the game measured 2.9% added. Two rounds were spent trusting the
harness over the owner's report.

**Do:** make the harness call the shipping function rather than a model of it (`cavefield
--shipping`), and treat any harness that has never been reconciled against an in-game number as an
unvalidated instrument. When a person's observation contradicts a measurement, instrument the
place they are standing.

**Found:** 2026-08-26, session 57.

### G106 - A direction-sampled visibility test must be shown to converge before it is quoted

**Trigger:** deciding what is visible by testing a finite set of ray directions.

**Trap:** too few directions miss real sight lines and over-remove; denser lattice directions that
step more than one cell per move let sight slip through one-block walls and under-remove. Session
57 measured 51.1%, 42.4% and 38.9% over 26, 98 and 290 directions - the two errors bracket the
answer rather than converging on it, and any single figure from that curve is an artefact of its
sampling.

**Do:** use traversal that visits every cell a line passes through, and demonstrate the answer
stops moving as the direction count rises before quoting it. Diffuse propagation is the safe
fallback: a straight path is one of the paths a spread takes, so a budgeted flood can never remove
something a straight ray of the same length could have reached.

**Found:** 2026-08-26, session 57.

### G107 - Post-transform geometry must use one effective coverage view on both sides

**Trigger:** transforming a section transiently for one mesh build while adjacent coverage still
comes from canonical snapshots.

**Trap:** Session 57's cave pass rebuilt the current section with hidden air filled, but vertical
face collection read the old `MeshJob.Self` and immutable neighbour snapshots. Every filled column
therefore saw the cave air that used to be beside it and emitted a wall; multi-column cavities
became lattices of invented faces and cave culling added 2.9% geometry in game. A harness that
pre-filled before meshing concealed the ownership split.

**Do:** one operation must own one coherent effective-coverage result. Session 58's
`LodCaveCull.Prepared` supplies both the rebuilt self and matching immediate-neighbour boundary
classification; the shipping harness enters through `LodMesher.BuildMesh`. Pin internal and
cross-section cases. The human-proven result changed the same 1,730 meshes from 88.6M to 66.4M
opaque vertices instead of 91.1M.

**Found:** 2026-08-26, session 58.

### G108 - A bounded fail-open window cannot prove a large component globally enclosed

**Trigger:** deciding whether an air/fluid component is sealed from a local window whose unknown
edge is treated as exterior.

**Trap:** a globally sealed cave network that enters two sides of the window is locally
indistinguishable from a useful through-tunnel into unknown space. Recentring the same window for
every section does not add knowledge: a component larger than the window can escape every local
decision. Increasing light reach changes the band but does not prove global enclosure.

**Do:** either retain such components and state the limitation, or classify connectivity across
section boundaries with explicit unknown-frontier state. Real surface portals must be distinct
from the boundary of current knowledge. Add retained-by-unknown telemetry before arguing which case
dominates.

**Found:** 2026-08-26, session 58, after 0.3.115 left large dry underground networks.

### G109 - A one-cell bridge fixture overstates pruning value in volumetric terrain

**Trigger:** choosing a graph-pruning algorithm from a narrow synthetic tunnel and dead branch.

**Trap:** bridge-tree pruning removes a branch only when one graph edge is its unique connection.
Natural caves are wide: adjacent columns create parallel routes and rooms create loops, so even a
dead-looking branch often belongs to a non-bridge component and is retained. Contracting all dark
heights in one X/Z column can create additional conservative shortcuts inside vertically complex
connected networks. The fixture passes while real-cache and live gain remain nearly unchanged.

**Do:** measure reason/topology distributions in real data and include wide branches, loops,
stacked cave layers, rooms, and complex mouths in fixtures. A passing minimal topology test proves
correctness for that shape, not expected value in worldgen.

**Found:** 2026-08-26, session 58. The 0.3.115 refinement added only about 0.3 percentage points in
the owner's settled scene.

### G110 - Water-filled space is geometry, not air, in the LOD snapshot

**Trigger:** expecting an air-cavity fill to remove flooded caves.

**Trap:** capture reads `BlockLayersAccess.FluidOrSolid`; water is a stored run marked
`FlagWater`, and the cave occupancy builder treats every run as occupied. A flooded chamber
therefore contains no dark air for the culler to replace. Partially flooded caves can lose some air
while water geometry and the terrain faces visible through it remain. Treating water as plain air
would instead let daylight and connectivity leak through oceans.

**Do:** classify fluid components separately. Keep anything connected to an exposed ocean/lake,
real opening, or unknown frontier; only a fully enclosed known fluid component is eligible for
absorption or internal-geometry suppression. Never borrow water as an opaque cave fill.

**Found:** 2026-08-26, session 58.

### G111 - A large fail-open bucket is not the same as a recoverable one

**Trigger:** seeing one conservative rule account for a large share of retained geometry and funding
a fix aimed at that rule.

**Trap:** Session 59's first global prototype kept 29.5% of estimated subterranean geometry as
"unknown frontier", which looked like the obvious target. Replacing component-granularity unknown
handling with pseudo-portals - the stance the shipping rule already takes at its own window wall -
deleted that verdict entirely and gained **0.2 percentage points**. The geometry moved intact into
route retention, which rose from 9.3% to 34.2%, because those components were never undecidable
*because* they touch the frontier: they are colossal networks that also carry many genuine surface
mouths. Removing one excuse simply handed them to the next rule.

**Do:** attribute retention exactly before funding a fix, and check where a bucket's contents would
*go* if the rule were relaxed. Run the classification twice - once with the rule, once without - and
charge the difference, rather than inferring which rule mattered from the size of its share.

**Found:** 2026-08-26, session 59.

### G112 - Keeping every route between entrances keeps deep interior, not passages

**Trigger:** preserving connectivity between cave mouths by retaining the graph structure that joins
them.

**Trap:** a natural cave system is overwhelmingly one 2-edge-connected blob (G109), so "keep every
route between two entrances" keeps its entire core, including regions hundreds of blocks from any
mouth that no sight line could ever cross. Measured over the owner's real cache this held 34.2% of
estimated subterranean geometry - more than the shipping rule removes in total - while the branch
peel that was supposed to trim it shed only about 5% of what it examined.

**Do:** bound routes geometrically, not just topologically. Keeping a route span only within a slack
distance `W` of a shortest mouth-to-mouth path cut route retention to 3.6% and roughly doubled total
removal to 62.9% at reach 32, with the curve flattening below `W` of about 32 - one light-reach of
slack. A cycle does not fool a distance test the way it defeats a bridge test. Note the cost: this
is the one rule in the family that can remove geometry a player could actually see, so it needs
human visual acceptance, not only a geometry total.

**Found:** 2026-08-26, session 59.

### G113 - Daylight reach is bounded by the local window's margin

**Trigger:** raising the cave rule's light reach to protect deeper terrain, such as the underside of
a large overhang.

**Trap:** `LodCaveCull` treats its 3x3 window's outer wall as open sky so the unknown fails open,
which makes that wall a light *source*. The margin is one full section - 64 columns at L0 - so the
reach must stay under it. At reach 64 wall light arrives at the centre-section boundary with exactly
zero budget and is rejected only because the flood discards non-positive values; at reach 128 it
penetrates 64 columns into the centre section and lights all of it. The failure is not visual damage
- false light keeps geometry - but the saving disappears entirely, which is the same effect Session
57 measured as the difference between removing 15% and about 30%.

**Do:** treat reach and margin as one coupled constraint. Keep any mesh-time reach at 64 or less at
L0, and satisfy a larger reach by moving classification out of the window rather than growing it:
the workspace is three arrays over `(grid + 2*margin)^2 * worldHeight` per mesh thread, so a margin
that matches a 128-block reach costs about 2.8x the memory on every mesh thread. Sections without a
classification should simply not be culled. Session 60 measured the accepted surface-only rule on
the same 40-section sample: reach 64 removed 26.88% of 1,554,740 estimated vertices, while reach 96
retained 20,276 more (1.30% of baseline) and reach 128 retained 73,996 more (4.76%). The owner chose
64.

**Found:** 2026-08-26, session 59, when the owner directed reach to expand for overhang safety;
quantified and closed 2026-08-27, session 60.

### G114 - A long visible tunnel can have no exterior seed inside a local window

**Trigger:** preserving a straight passage only when a surface mouth or exterior seed is present in
the current mesh job's bounded data window.

**Trap:** the middle section of a long straight tunnel through a mountain contains neither mouth.
The complete passage is visible from outside, but every local cell looks like part of a sealed
corridor, so a seed-requiring sight pass plugs the middle. A fixture that puts a mouth inside the
centre or neighbour section proves only a short tunnel and misses the actual ambiguity.

**Do:** when a solid-free lattice line crosses the complete available window, fail open and retain
it even if its real mouths are beyond the snapshots. Put both fixture mouths outside the 3x3 window
so the test exercises the same state as the failing middle section. Record the unavoidable cost: a
globally sealed, perfectly straight corridor crossing the local window can also survive. Only a
global classifier can distinguish the two, and the owner accepted the conservative local choice.

**Found:** 2026-08-27, session 60, after version 0.3.118 plugged the owner's intentionally straight
east-west mountain tunnel; fixed in 0.3.119.

### G115 - An exact sightline is thinner than the visible passage around it

**Trigger:** retaining only cells touched by the mathematical line that proves a tunnel visible.

**Trap:** the open core survives, but a one-block floor hump or nearby wall irregularity can sit
just outside the ray and be filled into the tunnel. The visibility proof is correct while the
resulting tunnel shape is visibly wrong.

**Do:** grow a small fixed clearance volume from the proven ray, stop expansion at solid terrain,
and measure the geometry cost. Never use the halo as an unbounded flood or seed it from ordinary
daylight, because either change restores hidden networks. Session 60's accepted four-block,
all-26-direction halo retained about 2,250 estimated vertices on a 1,554,740-vertex real-cache
sample (0.15%) and had effectively unchanged paired harness wall time.

**Found:** 2026-08-27, session 60. Version 0.3.120's one-block vertical guard was deliberately
superseded by 0.3.121's four-block all-direction clearance.

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
