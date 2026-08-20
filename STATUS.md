# Vintage Horizons — status

> Tier 2: current state, regenerated as a coherent document at session close. Durable design lives in `dev/ARCHITECTURE.md`; open work lives in `dev/TODO.md`.

**Status date:** 2026-08-20
**Mod version:** `0.3.37` (in development; `0.2.1` is the released version, and test builds increment the patch number)
**Target:** Vintage Story 1.22.5+, .NET 10
**Source files:** `43` C# files under `VintageHorizons/src`
**Assist protocol:** `1`
**Blob format:** `4`
**Database schema:** `6`

## 1. Repository state

`origin` points to the user's fork at `https://github.com/DodenGruva/Horizons`. The supplied source was code-equivalent to fork commit `27e5e6a`; the active branch is `codex/gpu-overdraw-culling`, branched from `origin/master` at `4496948`, and is intended to track the same-named origin branch.

The working branch contains the lifetime-tiered documentation workflow, portability and benchmark-harness work, deterministic moving/rotating routes with corrected PI-centred camera pitch, clean-cache capture-frontier and warm-join routes, pinned completed-sweep/generation and saturated-assist scenarios, expanded client/server performance and allocation instrumentation, versioned asynchronous mip propagation, revision-acknowledged persistence with retry/coalescing, incremental local/network key discovery with retry-safe request transitions, cached renderer bounds with stable projection changes, visibility-aware traversal with independent residency, incremental render-dirty priority scheduling, boundary-budgeted mesh snapshots and GPU uploads, tick-smoothed server work, time/byte-bounded client installs and capture publication, storage-owned foreign structural decode, ordered off-thread server-assist blob reads, and correlated server-assist setup/publication/admission/send/GC diagnostics. Synchronous periodic assist progress logging no longer runs inside the 50 ms owning-thread callback. The Windows runner can prove active client/server cache state, semantic generation completion, assist saturation and installation, final client mip/persistence convergence, durable mip interruption/recovery, integrated-singleplayer sibling retry/adoption, a fresh zero-obligation postcheck, pin fresh-server configuration, require terminal server state, install the server mod, and perform genuine stats-disabled comparisons. Private research and benchmark sandboxes remain ignored.

Current rendering edits world-anchor cached-terrain noise, remove the near-transition
geometry sink, and replace the old outer cutoff with ownership the mod can actually prove.
Two mechanisms exist. **Since 0.3.17 the default is per-cell ownership**: one marker per
32x32x32 vanilla chunk decides each fragment, and a cached section whose every cell is owned
is not submitted at all. The fallback, reachable with `.vhmask off` and now a saved setting,
is a near handoff whose radius is measured rather than assumed: the distance to the nearest
vanilla chunk column the client has not finished rendering, less one chunk, driving the
existing `cacheHandoffDistance` uniform. `VINTAGEHORIZONS_CHUNK_MASK` overrides the saved
setting in either direction (`0` off, `1` on) so the benchmark harness can pin one path.
Every phase of `dev/plans/PLAN_CHUNK_AWARE_VANILLA_HANDOFF.md` is built and, since 0.3.6,
actually running; since 0.3.16 without the band that had kept it opt-in.

**Opaque cached terrain now rejects avoidable GPU work** (0.3.27). All six solid face
directions have outward counter-clockwise winding, the opaque pass uses back-face culling,
and selected opaque sections submit front-to-back from a reusable allocation-free order.
Water and thin/cutout surfaces remain two-sided, and water keeps traversal order. The owner
accepted both defaults after isolated in-game toggles: back-face culling raised 218 to 260
FPS (about 0.74 ms per frame) and front-to-back raised 149 to 173 FPS (about 0.93 ms), with no
visible difference. `.vhbackface off` and `.vhfront off` remain session-only fallbacks.
Since 0.3.30 the whole cached pass also runs immediately after vanilla terrain, at opaque
order 0.38 rather than 0.36 around vanilla's 0.37. Current hills therefore populate depth
before hidden cached fragments reach the shader. The owner measured 148 to 179 FPS in a
valley (about 1.17 ms saved) and 590 to 651 while looking down (about 0.16 ms), with minute
distant changes judged entirely acceptable. `.vhocclusion off` restores the old order.
A preceding same-frame query prototype was removed after 83% hidden boxes changed 156 FPS
to 155 FPS; rejection count alone did not pay its proxy/query/submission cost, and vanilla
terrain was not yet in depth at the query point.

**Delayed exact-geometry occlusion now skips later opaque submissions** (0.3.37). An
ordinary cached terrain draw after vanilla depth doubles as an asynchronous GPU visibility
probe. The renderer consumes a result only after it is available; a zero-sample result skips
later `RenderMesh` calls, and periodic real-mesh probes restore visibility without proxy
geometry or a same-frame wait. The owner's roughly 4,000-block hill view rose from about
170 FPS to nearly 500 stationary. After motion tuning, sampled movement/turning produced
roughly 250-350 FPS. Those are sequential human playtests rather than a controlled benchmark.

Aggressive is default-on. It keeps answers through rotation, invalidates after two blocks of
translation, and shortens hidden probes to four frames while turning. Mixed vanilla/cache
ownership sections always draw, and a narrow horizontal screen-edge band draws while
turning. Extreme deliberately removes that guard and showed visible fringe distortion under
very fast yaw; aggressive retained very good performance with the distortion nearly
unnoticeable, and the owner judged the final 0.3.37 guard acceptable. `.vhtemporal off` and
`.vhtemporalprofile safe|aggressive|extreme` are live, unsaved controls.

A location/pause failure was global invalidation churn, not failed GPU visibility. In a
streaming area, mesh/readiness updates erased all answers and held an enclosed view near
190 FPS; pausing stopped the updates and allowed about 500. Mesh replacement now invalidates
only its section, while chunk-dirty events and mask uploads preserve unrelated answers.
Pending results carry a view epoch and stale answers fail toward drawing. `.vhinfo` reports
skipped draws, seam/turning-edge protection, accepted hidden results, stale results, global
invalidations and pending queries. See G55-G56 and session 34.

**The band is closed** (0.3.16, confirmed in game by the owner on 2026-08-19). It was the
engine's own per-frame range cull, and it is the reason six releases of ownership rules
built on four per-chunk signals could not reach it. `ModelDataPoolLocation.IsVisible` ends
in `FrustumCulling.InFrustumAndRange`, a horizontal distance test against a per-LOD bound of
at most `viewDistance * viewDistance + 400`, applied to every terrain mesh every frame after
all four chunk signals have said yes. Nothing about the chunk changes when it fails:
`quantityDrawn` only rises, the mesh stays pooled, `Hide` stays false, and
`ChunkCuller.CullInvisibleChunks` early-returns while the camera stays in one chunk, so
`CullVisible` is frozen rather than merely stale. Committed cells in the annulus between the
view-distance circle and the tracked window edge therefore stayed committed forever while
the mask discarded cached terrain there against nothing — on the trailing side only, because
the leading side never had chunks loaded that far out, and permanent while standing still,
because standing still is when nothing is re-evaluated. Every CPU diagnostic reported health
throughout because all of them consumed the same incomplete signal. The cube-granularity
theory that session 28 left standing is retired.

**The mask became the default in 0.3.17**, on the owner's decision after playing 0.3.16: the
band was gone and the seam overlap the draw-range threshold accepts went unnoticed. That is
product acceptance, and it is what the remaining gate was waiting on. The evidence behind it
is deliberately thin and should be read as such - one machine, one world, one view distance,
no benchmark since 0.3.9, and a visual matrix that has not been re-run since the mask started
working. `.vhmask off` restores the measured radius and persists, so a player who dislikes it
is one command from the previous default.

**Defects found and fixed in session 28**, all present in earlier builds:

- `.vhwhy` was registered twice. `ChatCommandApi.Create` throws on a duplicate, the throw
  propagated out of `StartClientSide`, and every command declared after it silently did not
  exist while the mod kept running - `.vhdetail` was absent from 0.3.4 entirely. G41.
- `maskSectionOrigin` was a `uniform ivec2` set through
  `ShaderProgramBase.Uniform(string, Vec2i)`, which reaches `glUniform2f`; an integer
  uniform rejects that with `GL_INVALID_OPERATION`. The origin never left the CPU, so every
  fragment tested ownership against chunk (0,0) and the per-fragment mask discarded almost
  nothing from 0.3.0 through 0.3.5, while raising a GL error every frame. Two isolated
  sandbox runs on one stationary scene: 19,126 errors with `-ChunkMask`, zero without, zero
  after the fix, at 456.3 fps against 455.7. G42.
- `VanillaReadinessMask.ClearColumn` had no callers from the feature's first version through
  0.3.12. The atlas is a wrapped ring, so a column leaving the window surrendered its texels
  to whichever column wrapped onto the same slot, which then inherited ownership it never
  had. `ClearSlot` now raises `ColumnEvicted`. G44.

**Corrections to durable documentation.** G40 claimed the client's `IWorldChunk.Empty` is a
stale flag. It is not: `ClientWorldMap.LoadChunkFromPacket` assigns it from the server's
chunk packet, and the tessellator reads exactly it to skip meshing. 0.3.3 therefore failed
on its rule, not its input - a column counts as owned only when all of its chunks do, every
column has sky, so refusing air excluded every column at once. G40 also claimed that
suppressing cached geometry above the real surface was correct because vanilla draws the
ground below; that reasoning does not hold for geometry standing above the surface, where no
lower cell draws anything, and 0.3.15 reverses it. G43 claimed until 0.3.16 that
`ClientChunk.CullVisible[ClientChunk.bufIndex]` was the *only* signal meaning "the engine is
drawing this now". It is necessary, not sufficient: `ModelDataPoolLocation.IsVisible` also
range-culls every terrain pool location per frame through
`FrustumCulling.InFrustumAndRange`, whose per-LOD bound is at most
`viewDistance * viewDistance + 400`, and `ChunkCuller.CullInvisibleChunks` early-returns -
freezing `CullVisible` outright - while the camera stays in one chunk. Beyond the view
distance every chunk signal stays latched at "drawing" while nothing is drawn. `IsChunkRendered`
is `quantityDrawn > 0`, a counter that only rises; mesh residency is closer but the engine
keeps a mesh while declining to submit it. Human testing established the culler divergence in
unmodded vanilla: fly high enough and the engine stops drawing the ground directly beneath
while the counter still claims it.

**Current ownership rules**, all gated on `ChunkMaskEnabled` and individually switchable:

- A chunk the engine claims while not actually drawing it does not own its cell
  (`.vhgeom`, on by default). Empty and unmeasurable chunks count as drawn, so a wrong
  answer costs the correction rather than uncovering live terrain.
- A wholly owned cached section may be dropped before drawing (`.vhskip`, on by default).
- No cell further from the camera than the approved view distance owns anything, air
  included, because the engine range-culls every terrain mesh per frame regardless of what
  the chunk reports (0.3.16; counted as `denied beyond view distance`). The threshold is the
  column's nearest face against `viewDistance - 46`, where 46 rounds up the in-chunk diagonal
  `32*sqrt(2)`: the engine measures from the mesh's geometry-midpoint sphere centre, which can
  sit anywhere in the chunk, so anything less leaves a residual stale ring. The cost is cached
  terrain overlapping the outermost chunk and a half of live vanilla terrain.
- Air chunks stay owned in the tracker, preserving the column aggregate and the radial
  handoff, and are excluded from the mask texel so they can never suppress a fragment. The
  exclusion is stored per cell, so the wholesale atlas rebuild on every window change honours
  it too (0.3.16); before that, travel reintroduced air ownership every 32 blocks.
- The atlas is rebuilt from committed tracker state once per second and the corrections are
  counted, bounding any mirror desync to one second rather than permanently.

**Diagnostics.** `.vhwhy` searches to the full draw distance and reports every engine signal
per cell; `.vhholes` sweeps the window for owned cells the engine is not drawing, needing no
aiming; `.vhpaint` paints hidden fragments red and suspends whole-mesh skipping so nothing
escapes the paint; `.vhcoarse` explains coarse draws; `.vhgeom` and `.vhskip` isolate the two
consumers of ownership. `VanillaChunkGeometry` binds `ClientChunk`'s internal mesh pool
arrays by reflection and reports unavailable rather than guessing; the check tier asserts
every binding, and the public culler fields, against the installed assembly.

## 2. Product and architecture state

The client captures received chunk columns, converts them into persistent 3D RLE sections, builds a mip pyramid, meshes selected sections on workers, and renders them beyond vanilla view distance. An optional server installation can capture collectively explored terrain, sweep existing savegame columns, generate transient terrain on request, and offer stored sections to clients.

Capture, meshing, mip boundary construction, compression, storage writes, demand-load decompression, foreign blob inflation/structural parsing, and integrated-singleplayer sibling-cache key discovery have background workers. The sibling-cache scanner owns a separate read-only unpooled SQLite connection and publishes bounded immutable key deltas. `LodWorld`, block-registry/palette resolution, revision validation, mip publication, foreign recolouring/publication, GPU upload, selection, and draw setup remain on their owning game or render threads.

Sweep, transient generation, and server-assist allowances accrue across normal 50 ms ticks instead of releasing a full second's work in one callback. Client assist arrivals, decoded foreign publication, completed background-load publication, and capture-result publication use 2 ms / 512 KiB boundary drains. One oldest item always progresses even if it alone exceeds a ceiling; later work waits. Queued/in-progress capture jobs plus completed/deferred results share one 24-item backpressure cap. Server and sibling-cache decode have separate outstanding limits, and concrete result queues expose pending items/bytes and oldest age.

Server-assist blob reads use one dedicated unpooled read-only SQLite connection owned by a
single reader thread. Queued, executing, and completed blobs share a 16-item cap. Results
carry player-session identity and publish in per-player request order on the server thread;
read failures produce explicit retryable replies rather than silence.
Stats sessions can correlate setup, completed-result publication, request admission,
individual sends, managed allocations, and collection-count crossings. Cumulative served
sections and bytes remain visible without logging from the service callback.

The Windows runner can wait until the storage owner has durably written an
`ApplyToParent` row, verify and interrupt only its sandbox client, require a recovery
process to report persisted mip work, and then apply the ordinary semantic convergence
guard. The interruption hook has no path and no behavior in ordinary processes.

Capture jobs/results carry a world epoch, estimated raw-run bytes, and ready time. The owning thread rejects cross-world results, performs live palette registration and section mutation, and records publication throughput/backlog. A queue clear is not treated as a teardown identity because an in-progress worker job may publish after it.

Foreign decode results carry a world epoch, key, source, deferred palette codes, estimated bytes, and ready time. Network/world request state survives decoder acceptance until owning-thread publication or terminal rejection. A resident local section wins both pre- and post-resolution checks. The foreign reload route remains available until a save acknowledgement proves a local row durable.

Mip jobs carry a world epoch, child identity, and content revision. The child remains `MipDirty` and its parent remains RAM-pinned until a matching result commits. Failed, stale, or cross-world results cannot clear the durable `ApplyToParent` obligation.

Section rows carry a runtime-only persistence revision separate from mip content revision.
`SaveDirty` remains set through snapshot enqueue and clears only when the storage owner
acknowledges that exact current revision. Failed writes retain dirty state under bounded
retry; newer pending snapshots for one key coalesce; foreign fallback retires after the
first durable local row. Close alternates drain, acknowledgement publication, and
remaining-dirty enqueue until clean or a 15-second deadline, then reports exact unresolved
keys/revisions. Database schema 6, blob 4, and assist protocol 1 are unchanged.

Render-dirty membership publishes new-key deltas into a nearest-first priority index.
Existing priorities rebuild after a 256-block camera-cell crossing, detail-distance change,
or world clear rather than every movement frame. Stale entries validate exact membership,
and temporarily busy mesh/load keys retain their dirty obligation under a finite scan cap.

Mesh snapshot production stops at job boundaries after 1 ms, 2 MiB of estimated retained
section arrays, or four jobs. Completed GPU results stop after 2 ms, 4 MiB of live
vertex/index data, or four results. One first item progresses even when oversized. A
complete opaque/water replacement becomes live before the old pair is disposed; partial
upload failure retains old terrain and restores dirty work. Telemetry reports throughput,
pending bytes, oldest age, direct GL upload time, and disposal time.

A block's cached colour is now one value per block id, averaged over many draws and cached,
because `Block.GetColorWithoutTint` is not a function of the block: grass-covered ground
answers it with a random pixel of the grass texture, and a palette entry is registered once
per section. The 2026-08-19 cache holds 38 different colours for `soil-low-normal` across
1,041 sections against sd 0 for deterministic blocks, which is the reported green/brown tile
patchwork. Sections already on disk are corrected as they load. Blocks carrying an entity
keep the per-position answer, which is what chiselled blocks need. See G46. Source and
check evidence only; no person has seen it yet.

Grass-covered ground is now built the way `chunktopsoil.fsh` builds it - composite the
untinted dirt with the partly transparent grass overlay, and dilute the tint slot by the
untinted share - and read through `GetAverageColor` rather than the engine's
`GetColorWithoutTint`, which for these blocks returns `GetRandomColor` values in the opposite
channel order and so had red and blue exchanged. See G48 and G49. Since 0.3.21 the two layers are measured over the whole
texture rather than through `GetAverageColor`, which samples four pixels and read the grass
layer's coverage as 0.573 against a true 0.687. With the true means the LOD reproduces
vanilla's own blend exactly for `soil-low-normal` at midsummer - rgb(80.5, 88.1, 22.8) either
side - where 0.3.20 gave rgb(86.5, 89.2, 29.8) and 0.3.19 gave rgb(61.7, 86.3, 7.1). The
owner has seen 0.3.20 and called it better but not perfect; 0.3.21 is unseen.

Since 0.3.22 cached ground follows vanilla's own rule that an up-facing surface never darkens
as the sun drops - `getBrightnessFromNormal` floors at `normal.y * 0.95`, and the liquid
shader does not shade by normal at all - instead of the mod's `0.55 + 0.45 * sunAngle`, which
took flat ground to 0.55 at dawn and dusk beside vanilla's 0.95. Applied as a maximum, so
cliffs and side faces are unchanged. `.vhtoplight off` restores the old shading for
comparison and does not persist, so a restart always returns to the corrected lighting.
Source-traced from the engine's shaders, and **human-confirmed on 2026-08-20**: the owner
evaluated it across the daylight cycle and asked for it to stay as the default. What that
covers is the look of distant ground through a day on one machine and one world; cached water
takes the same floor and was not judged separately. See G50.

Since 0.3.23 a cached section edge is walled off only where the CACHE has no data beyond
it, rather than wherever the neighbouring section is absent from RAM. The two had diverged
since the mesher was written: sections load and mesh nearest-first, so the outward side of
nearly every section was meshed before its neighbour arrived and was walled off permanently,
because neither `InstallLoaded` nor `MarkChanged` re-meshes on a mere load. On land the wall
is hidden behind the neighbouring terrain; on water, drawn at 66% alpha, it read through the
surface as a dark vertical line at every chunk boundary. Measured offline on a synthetic
ocean section: 1 water quad with the neighbour resident, 65 without it, 64 of them a
3,200 block^2 sheet on the shared plane. A side whose neighbour merely has not loaded is now
left open for water and still walled for solids - a missing solid wall would open a
see-through gap at a cliff, a missing water wall costs nothing - and the section is re-meshed
when the neighbour lands, tracked per side so the repair costs one mesh per side that
actually guessed rather than a mesh per arrival. `seam repairs` on the periodic log line
counts them. **Human-confirmed on 2026-08-20**, ocean, shores and cliffs. See G51 and
session 31.

One known mismatch remains and is unquantified: the mod takes its tint from
`ApplyColorMapOnRgba`, while terrain is drawn by `chunkopaque`/`chunktopsoil` through
`calcColorMapUvs`, and the two compute the climate/season blend weight differently - the C#
path's altitude and low-temperature terms are integer divisions that evaluate to zero, and
the shader's are live floats. It biases grass and leaves together rather than grass alone.
The season-row difference between the two was measured and is not worth acting on: at most
9/255 in a channel, 1/255 at midsummer.

The live tint is now averaged over 64 positions rather than sampled at one. A seasonal
colour map is a strip of sixteen shades per point in the year and the engine picks the row
from a hash of each block's own position, so a field is all sixteen mixed; one sample took
one row and painted every distant field with it, up to a quarter off in red at midsummer
and re-rolling as the player moved. Human-confirmed as "slightly off" on 0.3.18 and
corrected in 0.3.19; the correction itself has not been seen yet.

Cached-terrain color variation now combines section-local geometry with a stable section
world origin instead of camera-relative render coordinates. The vertex shader no longer
sinks cached geometry near the vanilla transition. The current playtest suppresses a
radius derived from measured vanilla readiness. A stationary route moved the applied
handoff from 64 to 192 blocks at a 256-block vanilla view distance, shrinking the band in
which both terrains draw the same ground from about 192 blocks to between 22 and 96.

The renderer now maintains a shadow-only 32x32x32 readiness window in fixed tagged-ring
storage. `ChunkDirty` feeds duplicate-coalesced candidates; unknown discovery, a
boundary-first ready shell, slower interior maintenance, and `IsChunkRendered` probing are
item-bounded, with probes additionally capped at 0.25 ms. Two render-frame-separated true
observations are required for readiness gain, the first false proposes immediate loss,
and generation-tagged publication tokens prevent stale window/world results from changing
L0-L6 aggregates. Periodic logs and `.vhinfo` expose cost, state, queues/ages, bytes,
events, transitions, windows, errors, the vertical distribution of ownership, and the
nearest column that is not wholly owned. Interior maintenance is state-agnostic: a
committed-only sweep could lose ownership but never gain it, because the client announces a
dirty chunk before that chunk finishes tessellating.

Per-section ownership classification still does not reach the draw path. The single radius
is the only pixel decision readiness drives: it shrinks in the same frame that measured
ownership shrinks, waits 500 ms before growing, applies the smallest radius seen while
waiting, and returns to the established constant if the tracker is unavailable or the
player leaves the default dimension. Later phases add atomic GPU masking and CPU whole-mesh
skips while preserving independent cache residency.

The per-cell mask is a 2D atlas of Y slices, 32 KiB for a 256-block window, addressed by
the same wrapped ring the tracker uses and rebuilt from committed state whenever the window
moves. Ownership is derived from an integer section origin plus the section-local offset,
never a summed world coordinate, because float32 rounds a fragment near a chunk edge onto
its neighbour at large world coordinates. A failed upload, a non-default dimension, or an
unavailable tracker restores the measured radius, and cached terrain stays suppressed
within 48 blocks of the camera only while the camera's own cell is confirmed drawn.

Ownership acquisition follows movement. The window queues the columns it gains as it gains
them, a measured backlog raises the probe ceilings to 1,024 items and 1 ms, and the
loss-detection shell sweeps completely whenever the camera crosses a chunk boundary. Those
three exist because human testing at far above normal flight speed found cached terrain
drawn over real terrain and, flying backwards, a band of missing world where vanilla had
unloaded chunks the mod still believed were drawn.

## 3. Completed local performance work

1. The full 0.2.1 fast tier now runs against the complete game installation. The Windows SQLite stale-version fixture disables connection pooling for its direct schema edit, preventing the old pooled connection from surviving file replacement.
2. Client telemetry records p95/p99/max and 25/50/100 ms hitch counts for total game tick, assist/local-offer/pipeline work, pipeline subphases, and render subphases. It also counts projection resets and uploaded mesh bytes. Explicit stats/benchmark sessions add per-phase managed-allocation totals and worst-call deltas without enabling unused integrated-server sampling.
3. A Windows-native isolated benchmark runner launches a hidden server and rendered client with pidfile/command-line safety checks. It never force-kills a process and now sends the graceful server-stop command before publishing its completion marker.
4. Expensive mip boundary collection, sorting, occupancy selection, and run merging moved from the owning tick to a dedicated bounded worker.
5. Content revisions, world epochs, parent pins, in-flight limits, explicit failed results, and stale-result retry protect asynchronous mip publication.
6. Lower-core machines reserve capacity for game render/simulation work by accounting for the dedicated capture and mip threads when selecting mesh-worker count.
7. Sibling-cache key enumeration moved from the game tick to a dedicated coarse-cadence reader; it publishes newly discovered keys in batches of at most 2,048 and unchanged scans publish nothing.
8. Server manifest ingestion applies one protocol-bounded chunk per tick and sends only that chunk's new keys into the pipeline instead of re-enumerating the retained manifest every tick.
9. Local and network transfer failures now end in explicit installed, retryable, or unavailable state. Retryable server replies restore the pipeline request under a 7.5-second cooldown and bounded roughly one-minute retry window.
10. Opaque and water mesh footprints maintain cached world-space bounds. Ordinary frames calculate the farthest required distance in O(1); the camera projection grows immediately in safe 512-block steps and shrinks only after a lower step remains stable for five seconds.
11. Sweep and transient-generation load issuance use fractional 50 ms allowances, 1 ms deadlines, 16-probe per-tick publication caps, and the existing 256-probe in-flight ceiling. Delayed ticks discard catch-up credit without reducing ordinary long-run rates.
12. Server assist uses fair per-player/global tick allowances and a 2 ms serving deadline. Client foreign/background install paths stop at 2 ms or 512 KiB, retain FIFO request state until actual publication, guarantee oldest-item progress, and report queue bytes/age.
13. Network and sibling-cache foreign blobs transfer to separately bounded storage-owner queues for inflation and structural parsing. The owning thread performs live resolution, recolouring, filtering, stale/local-win rejection, publication, and persistence scheduling.
14. Benchmark routes can interpolate deterministic position and camera trajectories. Route pitch uses a conventional zero-degree horizon and is translated to Vintage Story's PI-centred camera representation while both mouse axes are pinned.
15. Capture-result publication stops at result boundaries after 2 ms, 512 KiB, or eight results. Scheduling applies one combined 24-item cap to queued/in-progress jobs and completed/deferred results; world epochs reject work that finishes after teardown.
16. A 1,600-block one-way benchmark continues the cold capture frontier beyond the initial streaming footprint. An opt-in endpoint cooldown observes queue convergence after frame measurement without changing existing route defaults.
17. Server stats distinguish capture-pipeline phases, sweep probe publication/load issue,
generation probe/work issue, and assist service/blob/send/offer costs. They also report
assist request depth/oldest age and opt-in per-phase managed allocations.
18. The Windows runner snapshots client/server cache provenance, validates active-world
warm/cold cache state, installs pinned configuration only on fresh isolated servers, and
can fail a run whose server operation never reaches its required terminal log line.
19. Server-assist queues survive the transient interval where a joining player has
completed the assist handshake but is not yet exposed by `PlayerByUid` as `Playing`; the
disconnect event remains the sole owner of real request-queue removal.
20. Server-assist SQLite blob reads moved to a bounded dedicated read-only connection.
Session-tagged ordered batches preserve response order, prevent reconnect crossover, and
retain elevated-rate capacity; packet publication remains on the server thread.
21. The Windows runner can require a fresh post-route client sample with no pending
capture prerequisites/results, worker errors, mip obligations, unsaved sections,
asynchronous loads, or storage backlog/errors, and preserves the parsed proof beside the
frame CSV.
22. A guarded interruption route now waits for a durable `ApplyToParent` write before
terminating the pidfile-verified sandbox client. Recovery must report persisted mip work
and converge cleanly; a later fresh process verifies that the cleared state reached disk.
23. Quadtree traversal rejects conservative off-screen node bounds before descending or
requesting meshes. Visible refinement gates only on visible children. Mesh residency uses
an independent distance/age policy with a one-detail-level grace band, so view direction
does not control eviction. A controlled same-cache route reduced selected nodes 64.2%,
weighted average traversal time 19.8%, and weighted average draw-submission time 9.3%.
24. Render-dirty pruning and nearest-job selection use an incremental priority index.
Ordinary frames consume new-key deltas; coarse camera movement or policy/world changes
perform the deliberate whole-set reindex. Busy and stale entries cannot lose exact dirty
state.
25. Mesh snapshot production and GPU result upload are time/byte/item boundary-budgeted.
Retained and uploaded payloads are measured separately, frame-local budget state is
allocation-free, and old GPU resources survive until a complete replacement is live.
26. Section persistence uses exact revision acknowledgements. Stale success and failure
cannot clear newer dirty state, pending same-key snapshots coalesce, failed writes retry
with bounded delay, and shutdown repeatedly exposes and drains remaining dirty revisions.
27. Automated renderer validation exercised 601-section and 3,132-section caches. The
large route processed 94,285 snapshot/upload items with bounded queues and no 25 ms
renderer phase; a separate acknowledged-persistence run wrote 138 revisions and converged
to zero unsaved/backlog/errors.
28. Integrated-singleplayer validation uses its own nested sandbox. A clean-cache command-
generation run discovered 211 sibling keys, forced one exact-key retryable miss, installed
that key plus 62 others, and converged. A client-scoped hard interruption recovered one
persisted mip obligation; a third fresh integrated process required zero obligations.
29. Correlated server-assist diagnostics isolated a synchronous every-200-sections
progress notification inside request admission. Removing it kept cumulative status
visibility while a repeat 273-section transfer held active service to 2.061 ms maximum,
individual sends below 0.647 ms, and crossed no managed collection in measured callbacks.
30. Cached-terrain noise is world-anchored, the near approach no longer deforms vertices,
and a conservative radial handoff playtest retains a broad fallback band.
31. The renderer's vanilla-readiness model tracks exact 32x32x32 cells with bounded
event-fed discovery, frame-separated gain stabilization, boundary-first loss revalidation,
stale-publication rejection, L0-L6 aggregates, and diagnostics.
32. That model is runtime-validated. Five isolated runs recorded no probe errors, no
dropped events, no renderer phase reaching 25 ms, 18-28 microseconds of average frame cost,
and zero steady-state allocation after one 377 KiB construction. The runs exposed and then
confirmed the fix for a maintenance sweep that could lose ownership but never gain it.
33. The near handoff radius is measured rather than assumed, and 232 of 441 tracked columns
reach complete vertical ownership with a flat per-Y distribution, so the planned CPU
whole-mesh skip is reachable without a geometry-derived aggregate.
34. Opaque mesh winding is outward counter-clockwise in all six directions, enabling safe
GPU back-face culling while water and thin/cutout geometry remain two-sided. Six-direction
regression coverage pins the convention.
35. Opaque selected sections submit nearest-first from reusable storage, letting nearer
depth reject farther fragments without per-frame allocation. Water retains traversal order.
The owner accepted both GPU changes after +19.3% and +16.1% same-view comparisons with no
visual difference.
36. Cached terrain registers immediately after vanilla terrain so current hills populate
depth first. The owner accepted the default after +20.9% in a valley and +10.3% while looking
down, with minute distant changes judged entirely acceptable. A same-frame query prototype
that reported 83% hidden boxes but no FPS gain was removed.
37. Delayed exact-geometry queries now reuse available results to skip later opaque mesh
submissions without a same-frame wait. Local streaming invalidation, mixed-seam bypass,
turning probes and a narrow horizontal edge guard preserve convergence and accepted visuals.
The owner observed about 170 to nearly 500 FPS stationary and roughly 250-350 FPS in sampled
motion; aggressive is default-on in 0.3.37.

## 4. Measured diagnosis and result

Two short active-exploration route runs on unmodified 0.2.1 showed pipeline time tracking total game-tick time almost exactly. Synchronous mip propagation reached 20–22.5 ms p95, 32.5–35 ms p99, and 103.1 ms maximum. Total game ticks reached 107.9 ms. The route's camera pitch was later proven to be incorrectly zero-centred, so its render-phase timings were sky-biased and are not representative terrain-rendering evidence. The owning-thread attribution remains applicable because the route still teleported, captured columns, propagated mips, and measured pipeline time directly.

Two same-route runs after the asynchronous change ended with zero mip errors, zero pending/in-flight mip work, zero unsaved sections, and zero game ticks at or above 25 ms. Mip apply/schedule maxima were 1.93/0.43 ms. Mean worst-1%-frame time across the two runs on each side changed as follows:

| Waypoint | Before | After | Reduction |
|---|---:|---:|---:|
| spawn-horizon | 17.86 ms | 3.62 ms | 79.8% |
| spawn-look-south | 2.48 ms | 2.23 ms | 10.1% |
| ridge-east | 24.08 ms | 2.18 ms | 90.9% |
| valley-north | 10.99 ms | 1.60 ms | 85.5% |
| high-overlook | 11.79 ms | 2.38 ms | 79.8% |

The route is intentionally short and teleport-driven. Its same-route pipeline/tick comparison is strong evidence for the isolated mip spike class, but its old screenshots and renderer load are invalid because the camera looked mostly sky. It is not proof of long-session behavior, terrain-rendering cost, or subjective play quality.

The corrected 1,600-block moving/rotating route then ran with 30-second legs, one warm-up lap, and two measured laps before and after capture publication was boundary-budgeted. Every route coordinate already had VH coverage, so this was warm-cache traversal with recapture activity rather than first-time exploration. The baseline reproduced capture publication at 12.038 ms maximum and total game tick at 12.062 ms. The follow-up reduced those maxima to 5.732/5.749 ms, kept capture backlog within 9 results / 0.70 MiB / 93 ms old, and again had zero ticks at or above 25 ms. Average FPS stayed within 0.2% at all four waypoints; 1% lows improved 0.7-6.6%. Five projection resets occurred in each measured run. The sandbox cache evolved between runs, so aggregate FPS deltas are supporting evidence; the phase maximum, low queue age, and source-level multi-result bound are stronger.

Two independently reset client-cache runs then followed a one-way 1,600-block capture frontier with no warm-up. Both launched with zero active VH databases and reported zero sections loaded from cache. Neither produced a VH game tick at or above 25 ms; their worst ticks were 15.790 and 10.950 ms. Capture publication reached 9.028/5.469 ms, mip publication 15.748/8.474 ms, and capture backlog 20 results / 1.61 MiB / 234 ms and 11 results / 0.89 MiB / 62 ms. The second run's endpoint cooldown reached zero capture, mip, render, save, and storage work with no errors. Its first pass generated server-save terrain and the second reused that terrain, so aggregate FPS differences are not controlled A/B evidence.

Three alternating warm-stationary stats-on/off pairs then measured allocation-telemetry
overhead. The first stats-on process was a non-reproducing warm-up outlier. Across the two
warmed pairs, stats-on averaged 442.95 FPS versus 446.10 off (0.7% lower); median FPS was
1.0% lower. The 1% lows reversed direction and support no tail-latency claim. A separate
server-mod smoke emitted pipeline, sweep probe/publication, idle assist, queue, hitch, and
allocation telemetry and shut down gracefully. No assist section request arrived, so live
blob-read/send telemetry remains unexercised.

A proven warm-cache join then launched against a 27,668,480-byte client database and
reported 558 sections from the active cache. Its first interval reached 11.180 ms maximum
Vintage Horizons game-tick time with no tick at or above 25 ms. Background-load backlog
reached 181 sections / 51.93 MiB / 11.531 s old and drained by the 30-second report. The
settled 15-second sample averaged 438.0 FPS with a 270.1 FPS 1% low and no timeout.

A pinned dedicated-server sweep examined 3,249 dependency-aware positions and finished in
about 68 seconds. It loaded 1,018 existing columns, skipped 377 frontier columns,
generated nothing, and verified that 256/256 sampled absent positions remained absent.
Server pipeline ticks reached 17.874 ms maximum with no tick at or above 25 ms; sweep
probe/load issue maxima were 3.945/6.004 ms. The connected client also had no 25 ms
Vintage Horizons tick and its settled sample averaged 437.6 FPS / 302.9 FPS 1% low.

A pinned radius-8 command-generation run then targeted block 520000,520000, far from the
stationary client. It transiently generated all 289 work columns, loaded none from the
savegame, reported zero frontier skips, missing height maps, or timeouts, and verified
256/256 sampled absent positions remained absent. Generation work issue reached 5.706 ms
maximum and the server capture pipeline reached 6.551 ms, with no reported 25 ms hitch.

The first cold-client/warm-server saturated-assist run proved a 514-section active server
cache and filled all 16 client slots, but received and installed nothing; server blob/send
telemetry stayed zero. Source tracing found the 50 ms loop removing requests while the
joining player was not yet exposed as `Playing`. After retaining that bounded queue until
the disconnect event reports a real departure, the unchanged run requested, received,
and installed 395 sections with no declines. Client game ticks reached 7.789 ms maximum;
foreign publication peaked at 2.639 ms and 2 queued / 0.62 MiB / 62 ms old, then drained
by 30 seconds. Server blob reads reached 3.75/17.5/68.755 ms p95/p99/max and assist service
68.879 ms maximum. This 64/s stress configuration is above the default 8/s per-player
rate, but the non-preemptible single-read tail is directly measured.

The same guarded scenario was repeated after moving blob reads to the dedicated reader.
It again filled all 16 request slots and requested, received, and installed 395 sections
with zero declines. One 17.481 ms background reader call coincided with only 0.989 ms
maximum owning-thread assist service, directly proving that the database wait no longer
blocks that thread. A later interval recorded a 32.450 ms assist-service outlier while
reader calls stayed at 0.179 ms maximum and individual sends at 0.249 ms maximum. Its raw
server log was not retained, so later work does not retroactively assign that exact sample.

A new baseline reproduced a smaller 12.779 ms service tail, then correlated setup,
publication, admission, send, allocation, and managed-collection diagnostics were added
for stats sessions. Two attributed repeats installed 273 sections each, filled all 16
slots, declined nothing, and crossed no managed collection during a measured callback or
send. The clearest tail was 3.655 ms total: 3.573 ms was request admission, two sends
totalled 0.075 ms, and the same callback emitted the synchronous every-200-sections
progress notification. Removing that notification produced another 273-section passing
run with 2.061 ms maximum active-transfer service and sub-0.647 ms individual sends.

A later 120-second, 1,600-block movement run started from a proven 405-section warm
client cache, captured 2,401 columns, and recorded no Vintage Horizons tick at or above
25 ms. A 45-second cooldown ended with zero capture input/results, worker errors, mip
queue/in-flight/dirty work, unsaved sections, asynchronous loads, and storage
backlog/errors. The resulting cache grew from 20,672,512 to 29,982,720 bytes. Fresh server
and client processes then reported 601 cached sections and reached the same guarded zero
state. This establishes graceful sustained-work convergence and persisted restart in
separate processes, not interruption while work is active or integrated singleplayer.

A deliberate interruption run then waited until level-0 section `8004,8000` had been
written with `ApplyToParent=1` and terminated only the verified isolated client. The
recovery process opened the same cache with one persisted mip obligation, loaded 601
sections, and reached zero pending capture input/results, worker errors, mip queue/
in-flight/dirty work, unsaved sections, asynchronous loads, and storage backlog/errors.
A third fresh server/client process opened the resulting 29,999,104-byte cache with zero
persisted mip obligations and reached the same clean state. This establishes durable
client-cache recovery from a hard process interruption in dedicated server/client mode.

A dedicated visibility-traversal route then exercised a 601-section warm client cache
through full turns at four waypoints. In a controlled same-cache pair, early subtree
rejection reduced mean selected nodes from 360.1 to 128.9 (-64.2%), weighted mean
traversal time from 66.0 to 52.9 microseconds (-19.8%), and weighted mean draw-submission
time from 107.8 to 97.8 microseconds (-9.3%). Both sides retained 543 meshes with zero
evictions, reported no 25 ms Vintage Horizons tick, and converged all guarded queues and
durability state to zero. Mean waypoint average FPS changed from 463.4 to 459.4 and mean
1% low from 292.4 to 292.3, so no aggregate FPS improvement is claimed from this one
ordered pair. Static endpoint screenshots showed no obvious new camera-edge holes, but
they do not replace human review in motion or a thousands-section scaling run.

A later functional-only warm-cache route exercised the incremental dirty scheduler against
601 cached sections. All four moving/full-turn waypoints settled, 543 meshes became
resident with no evictions, and the final sample reported zero capture, mesh, mip,
render-dirty, save, load, or storage backlog/errors before graceful shutdown. This one
short run establishes lifecycle convergence, not a controlled performance improvement.

The renderer-budget follow-up processed 900 snapshots / 1,158.52 MiB and 900 uploads /
907.27 MiB on the 601-section cache with no sampled queue, no 25 ms renderer phase, and
2.721 ms maximum direct GL upload. A sustained 12,800-block corridor then captured 36,928
columns and grew the cache from 601 to 3,132 rows (30,023,680 to 157,724,672 bytes). It
processed 94,285 snapshots/uploads; sampled backlog stayed within 18 snapshots / 26.12
MiB / 157 ms and four uploads / 2.76 MiB / 16 ms. Direct upload peaked at 6.861 ms and no
renderer phase reached 25 ms. One 36.883 ms game tick was attributed to a 36.289 ms atomic
capture publication, a separate remaining tail. Every guarded field converged to zero.

After revision-acknowledged persistence was added, a fresh process reopened all 3,132
rows, moved/rotated through the same cache, wrote 138 section revisions, and ended with
zero unsaved sections, write backlog, and write errors before graceful client/server
shutdown. Deterministic checks separately injected a failed first write and successful
retry, rejected a stale acknowledgement after repeated mutation, coalesced a superseded
pending snapshot, drained 300 keys, and reopened SQLite to read the newest revision.

An integrated-singleplayer clean-cache run then completed radius-12 command generation:
625 positions produced 414 transient columns and 211 frontier skips with no timeout or
height-map failure. The client discovered 211 sibling keys. A guarded hook forced key
`2,2000,2001` through the real retry cooldown; that exact key later reached owning-thread
installation. Sixty-three sibling sections installed in total, zero remained wanted, and
all client convergence fields reached zero. A later hard interruption retained client
level-0 obligation `7999,8002`; recovery loaded one obligation and converged, and a third
fresh integrated process required zero persisted obligations and converged again.

## 5. Remaining performance findings

1. Capture-result publication remains a major measured owning-thread pipeline phase, but aggregate publication is boundary-budgeted. One admitted result remains non-preemptible and reached 9.028 ms on the clean-cache frontier route.
2. Foreign live block resolution, recolouring, filtering, and publication remain owning-thread work. Aggregate work is bounded, but one admitted publication cannot be preempted once started.
3. Server-assist packet publication remains owning-thread and atomic. Correlated stress
runs found no managed-collection crossing and held individual sends below 0.647 ms after
removing the synchronous progress notification. The old 32.450 ms sample cannot be
conclusively relabelled because its raw server log was not retained.
4. Ordinary render-dirty pruning/scheduling no longer scales with the whole collection;
the index deliberately rebuilds after coarse camera/policy/world changes. Mesh snapshot
creation and GPU upload are boundary-budgeted and have bounded 3,132-section runtime
queue/driver evidence. Visibility-aware traversal has a controlled 601-section reduction;
the thousands-section run is functional scaling evidence rather than a controlled causal
comparison.
5. The default handoff still submits suppressed cached meshes and discards their fragments
early, so it saves no draw calls. The opt-in per-cell mask does: a controlled stationary
pair measured 467.9 FPS against 436.2 while skipping about 42 of 149 submissions per frame,
with residency, selected nodes and evictions unchanged. The mask alone was neutral; the
gain is the whole-mesh skip. The readiness tracker's integrated convergence and
cost are now measured; GPU mask publication, mixed-mesh sampling, CPU skipping, and any
resulting net frame-time effect remain open.
6. View direction exposed a large cached-terrain GPU remainder. Back-face culling and
front-to-back ordering recovered about 0.74 and 0.93 ms in sampled views. Moving cached
terrain after vanilla recovered another 1.17 ms in the owner's valley case and 0.16 ms while
looking down. Delayed exact-geometry occlusion now skips opaque mesh submission after an
available zero-sample result; the owner saw about 170 to nearly 500 FPS stationary and
roughly 250-350 in sampled motion. A controlled alternating 0.3.37 comparison, final edge-
guard cost, GPU timer breakdown and cross-driver evidence remain absent.

The approved and now evidence-reordered sequence is `dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md`.

## 6. Remaining correctness and durability findings

- Exact persistence revisions, success/failure acknowledgements, bounded retry,
  same-key pending coalescing, and repeated shutdown drain/ack/enqueue are implemented.
  Persistent-failure timeout reporting has deterministic/source evidence but has not been
  forced in a game process.
- Asynchronous mip propagation now has a longer movement/capture convergence run,
  graceful restart, deliberate interruption after a durable `ApplyToParent` write,
  successful recovery, and fresh-process zero-obligation postchecks in both dedicated and
  integrated process layouts.
- Incremental sibling-cache discovery and retry-safe local misses now have an integrated
  command-generation run with exact-key retry/installation proof. Natural miss frequency
  and default-sweep behavior remain unmeasured.
- The color-noise and sink causes are source-traced and corrected. A distance-only handoff
  remains unable to express individual vanilla readiness: one unowned column near the camera
  pulls the global radius in and restores overlap everywhere, and that case has not been
  deliberately reproduced. Exact per-cell draw ownership and the cached/vanilla handoff seam
  remain open - a different thing from the mesher's chunk-boundary seams, which 0.3.23 fixed.

## 7. Current open work

1. Measure what direction-dependent renderer cost remains after delayed exact-geometry
occlusion before selecting regional buffers, multi-draw, or instancing. Repeat a controlled
alternating 0.3.37 comparison in settled and streaming views, quantify the final edge guard,
and cover other drivers, multiplayer, vertical look transitions, caves/structures, teleports
and long sessions. The same-frame proxy-query prototype remains rejected evidence; it is not
the accepted delayed real-draw design. Visibility must remain fail-open and independent from
residency and persistence.
2. The per-cell mask default is settled. The frame-rate question was answered in game on
2026-08-20 - about 1.6% cost at render distance 320 and about 5.6% gain at 1024, the sign
flip being CPU-bound against GPU-bound rather than a difference in how much is culled (G52).
What remains is coverage rather than the decision: no controlled benchmark since 0.3.9, and
boundary flicker, approach popping, cave and structure have never been individually
confirmed. `.vhmask off` restores the measured radial handoff, which has no holes.
3. Complete ordinary coverage of clipping, turn-around behavior, visual mesh replacement,
boundary flicker, cave/structure handoff, multiplayer, other drivers and long sessions.
4. Keep extreme fast-flight coarseness and brief approach overlap recorded at low priority.
The owner has played extensively without seeing either in normal gameplay, and those speeds
are not normally achievable. `.vhcoarse` remains ready if the symptom becomes practical.
5. Keep the recovered join fill-in regression under observation: one 0.3.23 join reached
100 meshes in 6.6 s against the old 6.1 s baseline, reversing the 36.4 s regression, but it
is still one sample.
6. Select a practical far-distance cap and decide whether regional buffers/multi-draw are
warranted after delayed-occlusion evidence and cross-driver testing.

Detailed tasks and human decisions are in `dev/TODO.md`.

## 8. Verification evidence

### Source-traced

- Supplied code matched fork commit `27e5e6a` with zero source/asset project mismatches.
- The active branch descends from fork release 0.2.1 at `f8d4b03`.
- The complete sibling-cache key query exists only on the dedicated discovery worker; the owning tick consumes at most one immutable delta batch.
- Manifest ingestion publishes each chunk's new keys once; ordinary ticks no longer pass the retained `RemoteKeys` set through the pipeline.
- The steady far-distance path no longer enumerates resident mesh dictionaries. Mesh arrival/removal owns bounds updates, and only an extreme removal requests a later exact rebuild.
- Sweep, generation, and assist serving now spend capped fractional allowances across 50 ms ticks; delayed ticks cannot release full-second catch-up work.
- Assist arrivals, sibling-cache blobs, and background-load results all have elapsed-time and byte ceilings with FIFO oldest-item progress.
- Foreign inflation and structural parsing occur only on the storage owner; owning-thread results resolve live palette state and reject cross-world, corrupt, or local-win arrivals.
- Decoder acceptance retains request responsibility through publication. The adopted
  section keeps its foreign fallback until a successful row acknowledgement; save enqueue
  is never treated as durability.
- Persistence revisions are independent of mip content revisions. Dirty membership
  survives enqueue, stale/failed acknowledgements cannot clear it, pending same-key
  snapshots coalesce, and shutdown repeats drain/ack/enqueue before exact timeout report.
- Allocation counter reads are opt-in per measured owner and sit outside the elapsed-time interval.
- Server pipeline, sweep, generation, and assist phase costs are independently timed;
  allocation reads remain opt-in, assist queues report exact oldest-head age, and stats
  sessions correlate assist setup/publication/admission/send work with collection-count
  crossings. The serve callback contains no synchronous logger call.
- Mesh snapshot and GPU upload drains have elapsed-time, retained/uploaded-byte, and item
  ceilings with one-first-item progress. Replacement publishes before old-resource
  disposal, and render telemetry exposes both queues and direct GL/disposal timing.
- Capture publication is result-boundary time/byte/item bounded; queued/in-progress jobs and completed/deferred results share backpressure, and cross-world results are rejected by epoch.
- Server-assist blob SQL exists only in the dedicated read-only reader; the server thread
  admits ordered session-tagged work and publishes completed packets.
- Quadtree nodes are frustum-tested before descent; invisible subtrees cannot request
  meshes. Visible-child coverage remains conservative, while eviction uses a separate
  distance/age residency timestamp that camera visibility never updates.
- Exact render-dirty membership feeds only new-key deltas into a nearest-first heap on
  ordinary frames. Camera-cell and detail-policy changes plus world clears own full
  reindexing; stale and busy heap entries cannot clear exact membership.
- Integrated client and server storage workers share one process environment; the guarded
  mip interruption marker is enabled only for the unsuffixed client pipeline, while the
  local-offer miss hook requires an explicit sandbox marker and exact-key install proof.
- Cached-terrain noise now derives from a stable section origin plus local vertices; the
  approach sink is absent. The radial handoff is bounded to no more than half the approved
  vanilla distance while retaining at least 192 blocks of fallback.
- The exact installed 1.22.7 libraries were hash-matched to the checked references.
  `ChunkDirty(NewlyLoaded)` follows world-map installation but precedes tessellation;
  `IsChunkRendered` tests `quantityDrawn`, which advances before completed-result upload;
  the post-upload callback is internal; and the unload path removes the chunk without a
  public client event. Supported public shader wrappers expose 2D/cube binding rather than
  a portable integer 3D texture update path.

### Harness-tested

- `dev/DocCheck.ps1` passes in the current PowerShell environment; cross-shell portability
  was previously established under Windows PowerShell 5.1 and PowerShell 7.
- The full game-backed Release tier passes 1,503 assertions, including delayed-occlusion
  state transitions, stale-epoch rejection, camera/profile thresholds, exact turn detection,
  mixed-seam invalidation, horizontal edge guards and static GL/query/default wiring; the water-seam
  frontier coverage added in 0.3.23 (four wall states plus the opposite-side pairing the
  repair depends on) and the 240-assertion
  readiness suite with the draw-range no-hole sweep and the mask-exclusion regression, 44
  persistence assertions for exact/stale/failure acknowledgements, pending coalescing,
  bounded retry, 300-key drain, and newest-row restart; 20
  render-dirty-scheduling assertions, 7 visibility-traversal/residency,
  53 benchmark-route/config/camera-mapping, the durable-mip
  client-scoped interruption marker, exact-key sibling retry, foreign queue/deferred-palette/failure isolation, assist-reader
  FIFO/cap/miss/failure/handle lifetime, async request-slot retention and saturation
  accounting, 15 tick-allowance, 23 drain-budget, 30 cached-bounds/far-plane, SQLite
  discovery/delta, remote-request state, server-assist, blob, and 64 mip assertions.
  This result also includes the 23 conservative-handoff/shader assertions from Session 24,
  89 readiness-model assertions, and static guards for event subscription, probe budgets,
  and the Phase 1 shadow state's exclusion from draw classification.
- The 2026-08-20 client log on 0.3.23 reads `Fill-in: 100 meshes after 6.6s` against the same
  3,016-key manifest that produced `36.4s` on 0.3.7 and `6.1s` on 0.3.4. The join regression
  therefore appears to have been fixed by the 0.3.8 probe-lock restriction, on one join, one
  machine and one world. The same line reads `974 seam repairs` beside `599 meshes` at the
  30-second mark: the water-seam repair path is active and did not cost visible fill-in time.
  Whether that count settles is unknown, because `Stats after 30s` fires once per session.
- Debug builds of the mod, checks, and benchmark harness succeed with zero warnings and errors.
- The Session 24 rendering source was built in Release and packaged as
  `vintagehorizons_0.2.1-playtest-near-handoff.zip`; no game process was launched by the
  assistant for this playtest.
- Four old-route CSV artifacts are tracked under `bench/results/2026-08-17-mip-worker`: two before and two after. Both after runs converged with no mip queue/in-flight backlog and no mip errors, but their screenshots/render load were sky-biased.
- Two full corrected warm-cache-route CSVs and their machine/settings context are tracked under `bench/results/2026-08-17-moving-rotation`. The baseline/follow-up completed all measured legs with zero settle timeouts, zero tick hitches, and graceful isolated shutdown. Large screenshots and sandbox logs remain intentionally ignored.
- Two one-way clean-client-cache CSVs and their scenario correction are tracked under `bench/results/2026-08-17-uncached-frontier`. Both had zero ≥25 ms VH ticks and bounded capture backlog; the cooldown run established queue convergence before graceful shutdown.
- Six alternating stationary CSVs and one server-mod smoke CSV are tracked under
  `bench/results/2026-08-17-telemetry-overhead`. The warmed pairs measured about 0.7%
  average-FPS stats overhead; the integrated smoke emitted server pipeline/sweep/assist
  telemetry and shut down gracefully.
- Warm-join and completed-sweep CSVs, scenario proofs, settings, results, and limitations
  are tracked under `bench/results/2026-08-18-join-sweep`. Both had zero reported 25 ms
  Vintage Horizons/server ticks and graceful isolated shutdown; the sweep's terminal line
  proves completion and absence preservation.
- Completed-generation and saturated-assist CSVs, semantic scenario proofs, the rejected
  pre-fix transfer, and the passing unchanged rerun are tracked under
  `bench/results/2026-08-18-generation-assist`. Generation preserved savegame absence;
  assist transferred and installed 395 sections and exposed the synchronous blob-read tail.
- The repeated off-thread-reader CSV and semantic proof are tracked under
  `bench/results/2026-08-18-assist-reader`. It transferred the same 395 sections and
  directly separated a 17.481 ms reader call from 0.989 ms owning-thread assist service.
- Four correlated baseline/attribution/fix CSVs and scenario proofs are
  tracked under `bench/results/2026-08-18-assist-tail-attribution`. The fix run installed
  273 sections with all 16 slots exercised, zero declines, 2.061 ms maximum active service,
  sub-0.647 ms sends, and no measured managed-collection crossing.
- The long warm-cache mip soak and fresh-process restart CSV/scenario proofs are tracked
  under `bench/results/2026-08-18-mip-soak`. Both passed semantic capture/mip/save/load/
  storage convergence; the long run captured 2,401 columns and the restart loaded 601
  cached sections.
- The guarded interruption, recovery, and fresh-process postcheck scenario proofs are
  tracked under `bench/results/2026-08-18-mip-interruption`. One durable obligation
  survived termination, loaded and converged after restart, and the next process reported
  zero persisted obligations.
- The production functional route and controlled same-cache visibility off/on pair are
  tracked under `bench/results/2026-08-18-visibility-traversal`. Across 14 matched
  intervals per side, selected nodes fell 64.2%, weighted average traversal time 19.8%,
  and weighted average draw submission 9.3%; both sides retained 543 meshes with zero
  evictions and no reported 25 ms Vintage Horizons tick.
- A short isolated warm-cache functional route exercised the incremental scheduler with
  601 cached sections and 543 resident meshes. Every waypoint settled, all guarded queues
  converged, and shutdown was graceful; no before/after performance claim is attached.
- Per-cell mask evidence is tracked under `bench/results/2026-08-19-chunk-mask`. The mask
  owns exactly the tracker's committed cells, costs 3 microseconds per update and about 325
  microseconds once at creation, and its performance pair is one controlled stationary
  comparison. Benchmark screenshots later showed in-game weather differing between runs,
  which is an uncontrolled variable in every pair measured to date.
- Readiness tracker and near-handoff evidence is tracked under
  `bench/results/2026-08-18-readiness-shadow` and
  `bench/results/2026-08-18-readiness-handoff`. Five isolated runs cover moving and
  stationary routes, the defect one of them exposed, its fix, the measurement that justified
  the derived handoff, and the handoff itself holding 192 blocks with zero fallback samples.
- Renderer-budget, cache-growth, and acknowledged-persistence evidence is tracked under
  `bench/results/2026-08-18-renderer-budgets-large-cache`. The growth route expanded 601
  rows to 3,132, processed 94,285 snapshot/upload items with bounded queues, and had no
  25 ms renderer phase. The persistence follow-up reopened all 3,132 rows, wrote 138
  revisions, and ended with zero unsaved sections, write backlog, or write errors.
- Integrated sibling retry, interruption, recovery, and fresh-process postcheck evidence
  is tracked under `bench/results/2026-08-18-integrated-singleplayer`. The client
  discovered 211 sibling keys, installed the exact forced-miss key plus 62 others, loaded
  one interrupted obligation on recovery, and loaded zero obligations in the postcheck;
  every accepted completed run converged all guarded fields.
- The corrected Windows harness completed dedicated and integrated shutdown without force
  termination except for the deliberate PID-verified interruption phases.

### Human-tested

- The human observer watched the corrected warm-cache movement/rotation route and reported
  that it looked good and smooth, with no noticed transient clipping or turn-around stalls.
  This is qualitative evidence for that populated-cache scenario, not a controlled
  comparison or an unseen-terrain verdict.
- The user described the first Session 24 render-fixes package as better, then reported
  cached terrain still mixed into proper terrain. This establishes the remaining overlap
  in that package, not acceptance of the latest radial package or every individual fix.
- The user confirmed on 2026-08-19 that 0.3.16 closes the band of missing terrain under
  `.vhmask on`. Their testing also produced the observation that identified the cause: flying
  backwards put the band ahead of them, it never filled while stationary, and it had become a
  consistent band where earlier builds showed scattered holes. Permanence ruled out every
  mechanism that operates between periodic repairs. This is acceptance of the band's closure
  on one machine, one world, one view distance and one flight speed; the seam overlap the fix
  accepts was not separately judged, and no performance verdict is attached.

- The owner confirmed on 2026-08-20 that 0.3.23 removes the water chunk seams, and
  confirmed shores and cliffs specifically - the one case the water-only frontier rule could
  have broken. This is acceptance of the seam fix on one machine, one world and one view
  distance.
- The owner measured the chunk mask in game on 2026-08-20 using the client's own average-FPS
  readout, at two render distances: about 315 FPS off against 310 on at 320, and about 180
  off against 190 on at 1024. Their verdict was that the difference is negligible either way
  while the picture is much better, and that the default stays. This settles the ship/shelve
  decision the mask has been carrying since 0.3.17. It is NOT a benchmark: two samples, one
  per condition, no alternation, and an average that may include the mask texture rebuild -
  which penalises the "on" side, so the 1024 figure is if anything understated. See G52 for
  why the sign flips and why a single-distance test would have condemned the feature.
- The owner toggled opaque back-face culling in the same view on 2026-08-20: 218 FPS off
  against 260 on (+19.3%, about 0.74 ms saved), with no visual difference across cliffs,
  caves, overhangs, high views or low views. They accepted it as the default.
- The owner toggled front-to-back opaque submission in the same view on 2026-08-20: 149 FPS
  off against 173 on (+16.1%, about 0.93 ms saved), again with no visual change. They
  accepted it as the default.
- The owner reported about 150 FPS while facing roughly 4,000 blocks of cached mountainous
  terrain and more than 300 FPS when facing away. This establishes a large view-dependent
  rendering opportunity on that machine, not its exact GPU phase or a portable result.
- A same-frame GPU query prototype eventually reported 83% hidden boxes but changed 156 FPS
  to 155 FPS. The owner then tested cached terrain after vanilla terrain: the same broad
  valley case rose from 148 to 179 FPS (+20.9%, about 1.17 ms saved), and looking down rose
  from 590 to 651 FPS (+10.3%, about 0.16 ms saved). Minute distant changes were detectable
  only through immediate toggling and were judged entirely acceptable. This accepts the
  post-vanilla default and rejects the query implementation on that machine.
- The owner then tested delayed queries around the real opaque draw. The established
  roughly 4,000-block hill view rose from about 170 FPS to nearly 500 while stationary.
  Motion tuning produced roughly 250-350 FPS while moving/turning in sampled areas. In a
  different streaming area an enclosed view stayed near 190 FPS while running and rose to
  about 500 paused; localizing invalidation restored the feature and was reported much
  better. Extreme exposed visible screen-edge distortion during very fast yaw; aggressive
  retained very good performance with the artifact nearly unnoticeable, and the final
  turning-edge guard was judged acceptable. The observations establish product acceptance
  on one machine, not a controlled benchmark or portable effect size.

### Not yet established

- One brief human playtest reported a noticeable subjective improvement after off-thread foreign decode; it was not a controlled or thorough comparison.
- No person has watched the clean-cache frontier, warm-join, completed-sweep,
  completed-generation, or saturated-assist routes in motion; long join/sweep/assist
  soaks remain untested.
- Integrated command generation now exercises sibling discovery and an injected exact-key
  retry through installation. Natural sibling misses and retryable live server responses
  remain unobserved.
- Visibility-aware subtree traversal and independent mesh residency have source, harness,
  controlled 601-section evidence, and automated 3,132-section scaling. No human has
  watched the thousands-section build for clipping or subjective turn-around quality.
- Incremental render-dirty scheduling has source, 20 focused assertions, a 601-section
  functional route, and an automated 3,132-section run. Its causal frame-time effect is
  still unmeasured.
- Mesh snapshot/upload boundary logic and byte accounting have source, build, fast-tier,
  601-section, and 3,132-section game evidence. The large run bounded sampled queues to
  18 snapshots/four uploads, measured GL upload below 6.9 ms, and converged, but no person
  watched replacement behavior and no second driver has been measured.
- Persistence hardening has deterministic failure/retry, repeated-mutation, coalescing,
  full-worker-drain, and restart evidence plus a clean 3,132-section game run. A persistent
  write failure has not been injected into a game process to observe the final exact
  unresolved-timeout log.
- The completed sweep ran in separate dedicated-server/client processes with a warm
  server cache, a calibrated 24-chunk radius, serving disabled, and generation disabled.
  Integrated-singleplayer cadence, cold-cache throughput, and default-radius behavior
  remain unmeasured.
- Capture publication's warm-cache follow-up peaked at 9 queued results / 0.70 MiB / 93 ms oldest and 5.732 ms for one admitted result. Unseen terrain, interrupted shutdown, and integrated-server capture remain untested.
- Dedicated and integrated interruption/recovery each used one guarded warm client cache.
  Integrated default-sweep interaction and repeated/long interruption soaks remain
  untested.
- Saturated assist runs now correlate server setup, publication, admission, send, and
  managed-collection crossings, but they do not isolate client worker-decode cost from
  surrounding cold-client capture/save load. No controlled sibling-cache, default-rate,
  multiplayer, or long-soak comparison exists.
- Allocation telemetry's uncapped steady-state average-FPS overhead measured about 0.7%
  across two warmed pairs; ordinary capped-frame-rate effect and tail impact remain unknown.
- The three isolated GL-state/order toggles plus the accepted delayed-occlusion playtests
  strongly establish avoidable GPU overdraw on the owner's machine, but shader, raster,
  bandwidth, query and driver costs remain unseparated. The final 0.3.37 edge guard has no
  isolated cost. There are no GPU timers, repeated alternating temporal comparisons, second
  driver or second machine.
- The per-cell ownership band is **resolved** in 0.3.16 and confirmed in game; it moved to
  the human-tested section above. What remains unestablished about it is everything the fix
  did not measure: its frame-rate cost, whether the accepted seam overlap reads acceptably at
  the horizon, and whether the closure holds at other view distances, speeds, and worlds. One
  human report on one machine is the whole of the confirming evidence.
- Extreme fast-flight coarseness and brief cached/vanilla approach overlap remain
  unexplained, but are demoted: the owner has played extensively without seeing them in
  normal gameplay, and those speeds are not normally achievable. The backward-flight band
  and water seams from the same report are resolved and human-confirmed.
- The readiness-driven handoff is accepted overall on one machine, one world and one view
  distance. Boundary flicker, approach popping, cave, structure, multiplayer, other drivers
  and long sessions remain ordinary coverage rather than blockers.

## 9. Known uncertainty

- The reported recurring spikes may have multiple CPU and GPU causes. The current route proves one major synchronous source, not exclusivity.
- Teleport-driven exploration emphasizes capture/propagation, and the original route's incorrect sky-facing pitch underrepresented terrain traversal, draw, and shader costs.
- Memory readings in the short routes are noisy and were not used to claim an improvement.
- Incremental discovery removes whole-set owning-thread work by construction, but its in-game frame-time effect has not been isolated in a before/after run.
- Cached bounds remove the steady mesh scan by construction. The corrected long warm-cache trajectory exercised continuous movement and stable reset counts, and human review found it smooth with no noticed clipping. Cold coverage arrival, the conservative rectangular overestimate under other routes, and broader visual conditions remain unjudged.
- The priority scheduler removes ordinary whole-set dirty scans by construction, but a
coarse-cell/detail/world transition still performs an intentional reindex. The current
functional route proves convergence only; it does not establish large-cache rebuild cost
or an aggregate FPS effect.
- The 2 ms / 512 KiB install/capture ceilings are conservative initial policy. Compressed foreign bytes, estimated in-memory background-section bytes, and raw capture-run bytes are intentionally path-local measures, not directly comparable throughput figures. One admitted item may exceed the elapsed or byte ceiling.
- The 1 ms / 2 MiB snapshot and 2 ms / 4 MiB upload ceilings are likewise conservative
initial policy. Snapshot-retained arrays and live GPU-transfer bytes are different
measures, and one admitted job/result remains atomic even after crossing a ceiling.
- The first clean-cache frontier run generated server-save terrain and the second reused it. Both client caches were empty, but FPS and upload-volume differences between them combine client and server-save state and are not causal comparisons.
- The brief subjective improvement is consistent with removing inflation and run parsing from the game tick, but it does not establish effect size or exclude unrelated run-to-run variation.
- Per-phase allocation counters identify managed bytes attributed to the current owning thread; they do not attribute native allocations or prove that a later GC pause belongs to one phase.
- The warm join and completed sweep are single samples. The sweep's server cache had been
  partially populated by the deliberately rejected radius-48 calibration run, so its
  timings are warm-cache cadence evidence rather than a cold-cache throughput baseline.
- The longer mip route began with 405 cached sections and added new capture work; it is
  sustained warm-cache convergence evidence, not a controlled cold-cache performance
  comparison or proof of crash recovery.
- A practical default far-distance cap remains a product decision requiring benchmark and playtest evidence.
- The saturated assist runs deliberately raised the serving rate to 64/s. Their queue and
throughput numbers are stress evidence, not default-rate expectations. The old 68.755 ms
read was a direct owning-thread observation; later runs prove reads are now off-thread and
reproduce/fix a progress-log tail without managed-collection crossings. The raw log for
the old 32.450 ms sample is unavailable, so that exact sample remains unproven rather than
being retroactively relabelled. The fix run is one-machine, one-player evidence.
- Shader-only suppression is not free: resident mesh buffers, traversal, uniforms, draw
  submission, vertex processing, rasterization, and the early fragment discard remain.
  The proposed hybrid may remove much of that work for fully replaced sections, but a
  performance gain is not established until paired CPU/GPU-aware evidence exists.
- The readiness tracker's 256-probe and 0.25-ms ceilings were never the limiting factor in
  measured runs: queues drained every interval, oldest work stayed at or below three frames,
  and average frame cost was 18-28 microseconds. The scheduling policy, not the budget, was
  what limited ownership before Session 26.
- The readiness-driven handoff's join and post-teleport convergence window is unmeasured.
  The derived radius starts small, showing more cached terrain near the camera than the old
  constant did until the tracker converges; failing toward coverage makes that the safe
  direction, but its duration and visibility are unknown.
- Frame-rate effect of state-agnostic maintenance and the derived handoff is neutral within
  run-to-run noise rather than proven neutral. Per-waypoint averages moved +1.3%, -2.6%,
  +0.1%, and -1.4% against the previous run, with intra-run lap spread reaching 1.9%, from
  one run per side.

## 10. Documentation map

| Document | Role |
|---|---|
| `AGENTS.md` | Codex bridge to the canonical agreement |
| `CLAUDE.md` | Model-neutral working agreement and task routing |
| `STATUS.md` | Current state, evidence, and uncertainty |
| `dev/ARCHITECTURE.md` | Durable architecture and settled decisions |
| `dev/GOTCHAS.md` | Traps, reversals, and disproved claims |
| `dev/TODO.md` | Open work, decisions, and verification debt |
| `dev/WIRE_HISTORY.md` | Protocol/blob/schema compatibility ledger |
| `dev/plans/` | Approved and historical implementation plans |
| `dev/sessions/` | Append-only consequential-work records and index |
| `dev/history/DONE.md` | Completed work removed from TODO |
| `dev/archive/` | Frozen superseded snapshot documents |
| `DESIGN.md` | Historical design journal, measurements, and provenance |
| `CHANGELOG.md` | Released history plus significant established changes accumulating under Unreleased |
| `README.md` | Player and contributor-facing overview |
