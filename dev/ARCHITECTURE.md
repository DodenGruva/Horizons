# Vintage Horizons — architecture

> Tier 1: durable design and settled invariants. Change this document when the design changes, not when a task merely advances.

## Product boundary

Vintage Horizons extends visible terrain beyond Vintage Story's normal chunk view distance by maintaining a persistent level-of-detail cache.

The client must remain useful against an unmodified server. It may only derive terrain from chunk data the server actually sent. An optional server installation can build its own cache, index previously generated terrain, generate transient terrain on request, and offer stored sections to clients.

The mod does not replace the vanilla near-terrain renderer. Its distant terrain currently
keeps a conservative fallback overlap, hands a close radial core back to vanilla as a
playtest stopgap, and fades into fog or the edge of known coverage. Cached geometry no
longer sinks near the transition.

## System flow

```text
client ChunkDirty / server ChunkColumnLoaded / transient generation
    -> column-key deduplication
    -> owning-thread chunk and rain-map snapshot
    -> capture worker: blocks -> vertical RLE runs
    -> owning-thread palette registration and leaf merge
    -> immutable mip snapshot -> mip worker -> revision-validated owning-thread publication
    -> immutable mesh snapshot -> mesh workers
    -> render-thread GPU upload and draw
    -> immutable save snapshot -> storage worker -> SQLite
```

Optional server assist adds:

```text
server cache key manifest
    -> client remote-key skeleton
    -> visibility-driven section requests
    -> bounded dedicated server read-only SQLite reader
    -> ordered owning-thread packet publication
    -> stored compressed section blobs on the client
    -> bounded storage-owner inflation and structural decode
    -> owning-thread block-code resolution, recolor, install, and local persistence
```

Integrated singleplayer sibling-cache discovery adds a dedicated read-only SQLite reader.
It scans at a coarse cadence, computes key deltas on its own thread, and publishes bounded
immutable batches for owning-thread registration. Visibility-driven blob reads use a
separate connection that remains owned by the game thread.

Owning-thread section installation is FIFO and bounded by elapsed time and content bytes.
The oldest item is allowed to exceed a ceiling once so an unusually large section cannot
starve itself and every result behind it. Foreign decoder acceptance transfers
responsibility but does not mean installation: deferred world and transport request state
remains in flight until the owning thread publishes or terminally rejects that item.

## Thread ownership

### Client or server owning thread

The owning game thread is the sole mutator of `LodWorld` and live `LodSection` state. It owns:

- Section dictionaries and quadtree key sets.
- Dirty sets and load-in-flight bookkeeping.
- Block registry access and block-code resolution.
- Client palette color and tint classification.
- Publication of completed worker results.

### Capture worker

The capture worker reads chunk references supplied by the owning thread and produces raw
block-id runs. Jobs/results carry a world epoch; the owning thread rejects a result from
a job that completed after world teardown. Publication is bounded at result boundaries
by elapsed time, estimated run bytes, and item count. Queued/in-progress jobs, completed
results, and reload-deferred results share one backpressure limit. Chunk lifetime is controlled by the
engine, so capture tolerates disposal races and records failures rather than mutating game
state.

### Mesh workers

Mesh workers receive immutable section snapshots. They build CPU vertex/index arrays only. They never call OpenGL or mutate `LodWorld`.

### Mip worker

The mip worker receives immutable child-section arrays, a copied captured-column mask and palette, the child identity/revision, and a world epoch. It performs boundary collection, sorting, occupancy selection, and merged-run construction. Its output still uses child palette ids. The owning thread rejects stale epochs/revisions, remaps palette ids into the live parent, publishes the quadrant, and only then clears the child's durable `ApplyToParent` obligation.

### Storage worker

The storage worker serializes immutable save snapshots, compresses them, and writes SQLite rows. Background loads deserialize block codes without touching the live block registry; the owning thread resolves those codes before publication.

The same owner inflates and structurally deserializes compressed sections received from
server assist or the integrated-singleplayer sibling cache. Network and local producers
have separate bounded outstanding allowances. Results carry their world epoch, key,
source, deferred block codes, estimated content bytes, and ready time. The owning thread
rejects cross-world, corrupt, or local-win results, then resolves live ids/flags/tints,
recolours client data, filters skipped runs, and publishes valid sections.

### Local-offer discovery worker

The integrated-singleplayer sibling-cache scanner exclusively owns its read-only SQLite
connection and its discovered-key set. It never touches `LodWorld`; the owning thread
applies each published key batch once. Its separate visibility-driven blob connection is
used only by the owning thread.

### Server-assist blob reader

The assist reader exclusively owns one unpooled read-only SQLite connection and prepared
blob command. Its single consumer preserves FIFO request order. Queued, executing, and
completed reads share one bounded outstanding allowance. The server owning thread checks
current player/radius policy before admission, publishes packets under its serving
deadline, and turns a reader failure into an explicit retryable response. Per-player
session identity prevents a completed read from crossing disconnect/reconnect, and an
ordered in-flight batch prevents a later refusal from overtaking an earlier read.

### Render thread

The render thread schedules mesh demand, accepts bounded completed mesh data, owns GPU
resources, traverses the LOD tree, and issues draw calls. Snapshot production is bounded
at job boundaries by 1 ms, 2 MiB of estimated retained arrays, and four jobs. Completed
GPU work is bounded at result boundaries by 2 ms, 4 MiB of live vertex/index data, and
four results. One first item may exceed a ceiling so it cannot starve. The complete new
opaque/water pair is uploaded and published before the previous pair is disposed. Work
here is frame-critical even if the game exposes it separately from game ticks.

## Data model

`LodSection` is a 64×64 grid of vertical columns. Each column contains packed RLE runs. A section-local palette maps run ids to live block ids, untinted colors, material flags, and tint slots.

Level 0 stores the finest LOD representation. Higher levels double horizontal column size. A parent section receives one quadrant from each child. `ApplyToParent`/`MipDirty` preserves propagation obligations across persistence and restart.

Packed section keys contain level, section X, and section Z. `HasDataSet` includes real rows and synthesized ancestors so the quadtree can descend without loading every section into RAM.

## Persistence

The cache uses SQLite with one row per level/section coordinate. Palette block codes, not numeric block ids, are stored because ids are world- and registry-dependent.

The serialized blob contains:

- Palette codes, colors, and material flags.
- Per-column run counts and packed runs.
- Captured-column bits.

Database schema meaning, blob format, and assist protocol are independent compatibility numbers. Their authoritative history is `dev/WIRE_HISTORY.md`.

Unreadable or semantically outdated cache data may be discarded and rebuilt from future capture. Any such destructive compatibility action must be explicit and measurable.

## Mip propagation

Each group of up to four child columns is merged by vertical boundary slices. A slice survives by majority occupancy, with a reduced threshold at incomplete captured frontiers. Adjacent slices of the same block merge back into a run.

Propagation keeps the child's pending flag set while work is queued or executing. A content revision increments whenever `MarkChanged` accepts a live section mutation. Worker results carry that revision and a world epoch; failed, stale, or cross-world results cannot clear the obligation. Parents are pinned in RAM until their in-flight child results complete. The expensive boundary sweep is off-thread; palette remapping and atomic column-array replacement remain on the owning thread.

## Rendering

The renderer uses a quadtree over section levels. Distance selects the desired detail level. A parent remains as coverage until the required child slots are ready, preventing holes during level transitions.

Opaque and translucent terrain use separate mesh buffers and passes. Seasonal/climate tint data is refreshed from live game color maps and applied in the shader. The camera uses relative section transforms so large world coordinates do not enter mesh vertex data. Cosmetic terrain noise combines section-local vertices with a stable section world origin; it must not sample camera-relative geometry coordinates.

The settled near handoff is exclusive per-vanilla-render-chunk ownership. Cached terrain
covers uncertain/unready cells, vanilla owns confirmed render-ready cells, fully replaced
cached meshes are skipped before submission, and only mixed frontier meshes pay for a GPU
readiness mask. Per-cell ownership is the default; the measured radial cutoff remains the
explicit fallback. Visibility/ownership remains separate from mesh residency and
persistence; see `dev/plans/PLAN_CHUNK_AWARE_VANILLA_HANDOFF.md`.

Opaque terrain faces use outward counter-clockwise winding and render with GPU back-face
culling enabled. Water and thin/cutout surfaces stay two-sided. Selected opaque sections are
then submitted front-to-back from a reusable allocation-free ordering list so nearer depth
can reject farther fragments; the translucent water pass retains traversal order. These are
GPU overdraw reductions, not terrain occlusion. The renderer still has no section-level
occlusion query, hierarchical depth test, or conservative horizon rejection, and any future
visibility result must remain independent from residency and persistence.

The renderer maintains a horizontal world-space rectangle over all opaque and water mesh
keys. Additions expand it in constant time; removing an extreme marks it for one rebuild
from the surviving keys. Ordinary frames derive the farthest required distance from the
rectangle without scanning resident meshes. The camera far plane rounds upward in
512-block steps, grows immediately, and shrinks only after a lower step remains stable for
five seconds. The shader's effective far edge remains continuous, and `.vhfar` remains an
explicit culling/far-edge cap rather than a different residency policy.

Render-dirty membership is exact owning-thread state, while scheduling uses a separate
nearest-first priority index. New dirty keys enter incrementally. Existing priorities
rebuild only after the camera crosses a 256-block cell, detail distance changes, or the
world is cleared. A dequeued heap entry must still exist in the exact set, and a key with
mesh or load work already in flight is restored rather than losing its dirty obligation.
The scheduler examines only a finite prefix sized to include the bounded in-flight work.

Mesh jobs estimate every array they retain, including immutable run and column arrays
shared with live sections. Completed results estimate only live vertex/index counts, not
pooled backing capacity. Telemetry records scheduled/uploaded items and bytes, pending
bytes and oldest age, direct GL upload calls, and resource disposal separately. The
frame-local budget state is a value type so enforcing the limits does not create steady
render-thread heap traffic.

Visibility, residency, and persistence are different concerns:

- Visibility decides what is traversed and drawn now.
- Residency decides which CPU/GPU resources remain available.
- Persistence decides whether a section can be reconstructed after eviction.

They must not share one timestamp or state flag if doing so makes turning the camera trigger remesh storms.

Persistence has its own monotonically increasing per-section revision, separate from the
content revision used by mip jobs. This distinction is required because clearing the
persisted `ApplyToParent` flag changes a row without changing terrain content. Snapshot
enqueue reserves a revision but leaves `SaveDirty` set. The storage owner coalesces a
newer still-pending snapshot for the same key, executes at most one older plus one pending
revision, and publishes a success/failure acknowledgement for every executed write. Only
a successful acknowledgement matching the current persistence revision clears dirty
state; stale success and all failure paths retain it. Failures retry with bounded
exponential delay. A successful local write retires a foreign fallback only after that
acknowledgement.

Shutdown applies the same protocol rather than using a separate best-effort path: drain
accepted writes, publish their acknowledgements, queue remaining dirty revisions, and
repeat until clean or the fixed deadline. A timed-out close reports every unresolved key
and revision before world state is cleared. Persistence revisions are runtime identity,
not part of the SQLite schema, section blob, or assist protocol.

## Optional server behavior

Server capture and serving are additive. Players without the mod remain unaffected. Client-local capture wins over a server-offered section because it represents terrain that client actually observed, including edits.

Savegame sweeping may load only terrain already known to exist with a safe generated neighborhood. Transient generation uses `PeekChunkColumn` and must not write generated terrain to the savegame. Runtime absence verification measures this promise.

The server must answer every accepted section request, including explicit refusal. Silence strands the client's in-flight slot indefinitely.

## Settled decisions register

1. **Vanilla-server compatibility is a product requirement.** The client pipeline cannot depend on server installation.
2. **Optional server assist is additive.** It may improve coverage but never become required for ordinary client capture.
3. **Block codes are persisted; block ids are resolved live.** Numeric registry ids are not stable storage identity.
4. **The block registry remains on the owning thread.** Some registry reads lazily mutate internal engine state.
5. **OpenGL work remains on the render thread.** Workers produce arrays, not GPU objects.
6. **A parent covers until children are ready.** Detail transitions may be temporarily coarse but must not open holes.
7. **Local observed data wins over remote offered data.** A remote source cannot overwrite newer client truth.
8. **Sweeping must not generate terrain.** Frontier columns without a complete known neighborhood are skipped.
9. **Transient generation must not persist world terrain.** The LOD cache is the only intended output.
10. **Competing LOD mods cause this renderer to defer.** Two systems must not fight over the camera far plane or distant terrain.
11. **Performance work is evidence-driven.** Count budgets are not accepted as frame budgets without elapsed-time measurements.
12. **Owning-thread install drains are FIFO, time/byte bounded, and progress-guaranteed.** One oversized oldest item may exceed a tick's ceiling; later work waits.
13. **Foreign decode acceptance is not publication.** Request responsibility and source
    fallback survive background decode until the owning thread installs or terminally
    rejects the exact result. Local observed data still wins the race.
14. **Capture publication is boundary-budgeted.** The owning thread stops after its
    elapsed-time, raw-run-byte, or item ceiling. One admitted result remains atomic and
    may exceed a ceiling; later results wait under combined job/result backpressure.
15. **Render-dirty membership and priority are separate.** Exact membership owns the
    obligation; a coarse-cell-refreshed heap orders available work. Stale or temporarily
    blocked heap entries cannot clear the exact dirty state.
16. **Mesh production and upload are boundary-budgeted.** Time, retained/uploaded bytes,
    and item caps all apply. One atomic first item may exceed a ceiling; later work waits.
    A replacement becomes live before the previous GPU resources are disposed.
17. **Save enqueue is not durability.** `SaveDirty` remains the exact reconstruction
    obligation until the storage owner acknowledges the current persistence revision.
    Failures retry, superseded pending snapshots coalesce, and close either reaches clean
    state or reports exact unresolved revisions.
18. **Near terrain has one renderer owner per vanilla chunk.** The intended handoff keeps
    uncertain cells cache-owned, transfers confirmed cells exclusively to vanilla, skips
    wholly replaced cached meshes, and masks only mixed frontier meshes. Ownership does
    not itself evict resident cache resources.
19. **Opaque cached terrain rejects back faces and submits nearest first.** Outward CCW
    winding makes culling safe for all six solid face directions; water and thin surfaces
    remain two-sided. Front-to-back ordering affects opaque submission only and reuses its
    storage rather than allocating per frame.

## Concurrency invariants

- No worker mutates `LodWorld` or a live section.
- No background task reads the live block registry.
- Every asynchronous request eventually produces success, explicit failure, cancellation, or retryable state.
- A completed server-assist read cannot cross player-session identity or reorder a later
  response ahead of an earlier accepted read.
- Queue backpressure is applied before retaining large chunk, mesh, or section snapshots.
- Dirty state is cleared only when responsibility for that exact revision has transferred safely.
- A stale result cannot overwrite a newer section revision.
- Every worker result carries a world epoch or an equivalent teardown identity; clearing
  a queue alone cannot stop an in-progress job from publishing afterward.
- Shutdown either persists acknowledged work or reports exactly what remained.
- Diagnostics must not be capable of killing a worker thread.

## What is deliberately derived from source

File inventories, method lists, queue field names, and module counts are not maintained here. They are cheap to obtain accurately with repository search and expensive to keep correct in prose.
