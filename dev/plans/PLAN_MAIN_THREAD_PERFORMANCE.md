# Plan — main-thread stutter and renderer scaling

**Status:** In progress. Reconciliation, client/server instrumentation, the Windows benchmark route, versioned asynchronous mip work, incremental key discovery/request correctness, cached bounds/stable projection, tick-smoothed server work, bounded client installs and capture publication, ordered off-thread server-assist blob reads, visibility-aware traversal, incremental render-dirty priority scheduling, boundary-budgeted mesh snapshots/GPU uploads, revision-acknowledged persistence, opaque back-face culling, front-to-back opaque submission, and post-vanilla depth rejection are implemented and verified. Regional submission and remaining renderer scaling follow the accepted visibility work on `codex/gpu-overdraw-culling`.
**Review baseline:** Supplied source snapshot, code-equivalent to fork commit `27e5e6a` (0.2.0 development line).
**Working baseline:** Fork master at commit `4496948`, branch `codex/gpu-overdraw-culling`.
**Primary evidence:** Source-traced review recorded in `dev/sessions/SESSION_1.md`.
**Open work authority:** `dev/TODO.md`.

## Reconciliation note

The fork already implemented several relevant improvements after the reviewed snapshot:

- Cold-cache capture waits for background reload instead of synchronously reading and inflating on the game tick.
- Startup key lookup uses an index; a measured 691 MB cache fell from 931 ms to 13 ms.
- Local sibling-cache discovery was reduced from twice per frame to once per second.
- Dirty scheduling uses one bounded nearest-candidate pass instead of repeated full-set scans, and the render walk removed per-node square roots/logarithms.
- Mesher scratch allocation was pooled, and basic renderer phase average/maximum timing exists.

These are the new baseline, not work to reimplement. At plan approval, they did not close the plan: the once-per-second local scan was still a full main-thread query; the network manifest was still re-enumerated each tick; far-plane calculation and projection resets remained movement-sensitive; periodic server work remained bursty; installs and uploads lacked elapsed-time/byte ceilings; mip propagation was synchronous; and traversal/scheduling still scanned whole collections. The measured re-prioritization and phase status notes below record the items since completed locally.

## Measured re-prioritization — 2026-08-17

Phase 1 telemetry changed the implementation order. On two short active-exploration route runs against 0.2.1, total game-tick p95/p99 reached 22.5–27.5 / 32.5–40 ms and the synchronous mip phase reached 20–22.5 / 32.5–35 ms, with a 103.1 ms maximum. That made versioned mip work the first runtime fix rather than Phase 6 chronologically. The route's camera pitch was later proven to be incorrectly zero-centred, so its render-phase timings were sky-biased and do not validate terrain-rendering cost; the teleport/capture pipeline attribution and same-route mip comparison remain applicable.

The initial asynchronous mip implementation is now locally complete and measured twice on the same route. Boundary sorting and merge construction run on a dedicated bounded worker; owning-thread results are world-epoch and content-revision validated. Across the five waypoints, the mean worst-1%-frame time from two before and two after runs changed as follows:

| Waypoint | Before | After | Reduction |
|---|---:|---:|---:|
| spawn-horizon | 17.86 ms | 3.62 ms | 79.8% |
| spawn-look-south | 2.48 ms | 2.23 ms | 10.1% |
| ridge-east | 24.08 ms | 2.18 ms | 90.9% |
| valley-north | 10.99 ms | 1.60 ms | 85.5% |
| high-overlook | 11.79 ms | 2.38 ms | 79.8% |

After the change, two telemetry runs reported zero game ticks at or above 25 ms, zero mip errors, and zero mip backlog/in-flight work at interval close. Mip apply/schedule maxima were 1.93/0.43 ms; capture apply is now the largest measured owning-thread pipeline phase at 14.08 ms maximum. These are controlled harness results, not yet a human playtest.

## 1. Outcome

Reduce recurring lag spikes and sustained FPS loss attributable to Vintage Horizons while preserving:

- Client-only operation against unmodified servers.
- Optional server assist and local singleplayer cache adoption.
- Persistent cache correctness across eviction and restart.
- Hole-free LOD transitions.
- Seasonal tinting, water, and current terrain fidelity.
- Explicit failure and retry semantics under partial work.

The goal is not merely higher average FPS. The work must improve frame-time consistency during movement, exploration, joining, sweeping, and terrain transfer.

## 2. Governing constraints

1. Block registry access remains on the owning game thread.
2. OpenGL resource creation, upload, draw, and disposal remain on the render thread.
3. Workers receive immutable snapshots and publish versioned results.
4. Visibility, residency, and persistence remain separate policies.
5. Work queues stay bounded before retaining large data.
6. Latency-sensitive work is budgeted by elapsed time or bytes, not only item counts.
7. No optimization is accepted without a reproducible before/after scenario.
8. Review findings are hypotheses until measured; source certainty and runtime causation are labeled separately.

## 3. Phase 0 — repository and benchmark baseline

**Implementation status:** Complete locally. Fork ancestry, full game-backed fast checks, isolated Windows runner, and reproducible route artifacts are established.

### Work

- Fetch the fork history and compare `origin/master` with the supplied snapshot.
- Preserve both sides and reconcile without overwriting local or remote work.
- Create a dedicated performance branch after ancestry is correct.
- Record the baseline commit, game version, graphics settings, view distance, world/save, route, CPU/GPU, and mod configuration.
- Repair fast-check portability so an extracted tree and Windows SQLite fixture run reliably.

### Acceptance

- The working branch has correct fork ancestry.
- The worktree is clean except for intentional documentation/implementation changes.
- The complete fast tier has a trustworthy pass/fail result.
- Baseline benchmark artifacts are immutable and identifiable.

## 4. Phase 1 — observability and reproduction

**Implementation status:** Client tick/pipeline/render and server pipeline/sweep/generation/assist percentiles, hitch counts, allocation totals, and relevant queue telemetry are implemented. The active-exploration teleport route reproduced the issue and attributed its largest spike to mip work, but its old screenshots/render load were sky-biased by an incorrect pitch mapping. The corrected warm-cache continuous movement/rotation route now has a full baseline, same-route capture-budget follow-up, and positive human smoothness/clipping review. Two clean-client-cache one-way capture-frontier runs also completed with bounded, convergent backlog and no ≥25 ms VH ticks. Two warmed stationary A/B pairs measured about 0.7% average-FPS overhead from allocation telemetry. Proven warm-join, completed dedicated-server sweep, completed transient-generation, and saturated live-assist scenarios now exist. The repeated assist scenario proves ordered off-thread reads; integrated-singleplayer sweep/join remains open.

### Client instrumentation

Measure separately:

- `OnGameTick` total.
- Local-offer discovery, blob read, decode, recolor, and install.
- Server-assist manifest processing, arrival decode/install, sorting, and request selection.
- Pipeline background-load installation, capture scheduling, capture apply, propagation, and save snapshot creation.
- Renderer dirty pruning, nearest-job selection, snapshot creation, mesh-result upload, eviction, seasonal refresh, far-plane work, quadtree traversal, and draw preparation.
- Projection-reset count.
- Mesh upload bytes and dispose count.

### Server instrumentation

Measure separately:

- Sweep probe issuance and callback publication.
- Sweep chunk-load issuance.
- Generation probe/work issuance.
- Assist queue service, blob reads, and packet sends.
- Server pipeline phases.

### Metrics

- Calls, total, average, p95, p99, and maximum milliseconds.
- Items and bytes processed.
- Queue depths and oldest-item age.
- Managed allocations and GC collection deltas where practical.
- Frames/ticks above 25, 50, and 100 ms.

Instrumentation should be cheap when disabled and available through an explicit development setting or environment variable.

### Benchmark scenarios

1. Warm stationary view for continuity with the existing benchmark.
2. Continuous movement through already cached terrain.
3. Movement plus steady camera rotation.
4. Cold join with a large local cache.
5. Warm join with a large local cache.
6. Active exploration and capture.
7. Integrated singleplayer with default savegame sweeping.
8. Server assist receiving its maximum normal in-flight batch.
9. Large synthetic or preserved cache with thousands of sections.

### Acceptance

- Each suspected phase can be distinguished in telemetry.
- A moving/exploration scenario reproduces the user's class of stutter or establishes what additional evidence is needed.
- Disabled instrumentation does not materially change the steady baseline.

## 5. Phase 2 — incremental key discovery and request correctness

**Implementation status:** Complete locally. Sibling-cache SQL enumeration runs on a dedicated read-only connection and publishes bounded deltas; network manifests publish each chunk once; local and server failure paths preserve explicit retryable or terminal state with cooldown. The full fast tier covers these transitions, a saturated live-assist run covers successful request/publication state, and an integrated command-generation run forced one sibling miss and proved that exact key later installed. A natural live miss and retryable server response remain unobserved.

### Local sibling cache

- Give local cache discovery a dedicated read-only connection owned by its reader thread.
- Poll at a coarse cadence rather than every 50 ms.
- Compute deltas off-thread and publish only newly discovered keys.
- Start with a safe background full scan if necessary; add a monotonic SQL cursor only when its restart semantics are proven.
- Do not touch `LodWorld` from the reader.

### Network manifest

- Apply each incoming manifest chunk once during bounded pump work.
- Publish newly offered keys directly to `LodRemoteKeySet`.
- Stop passing the complete `RemoteKeys` set through `AddRemoteKeys` every tick.

### Retry correctness

- Add a key to the successfully-taken batch only after a blob has transferred responsibility or explicit permanent failure was recorded.
- A transient local read miss must remain wanted or return to a retryable state.
- Clear in-flight identity on failure paths that do not retain responsibility.
- Add regression checks for miss, later success, local-win race, refusal, and parse failure.

### Acceptance

- No full SQLite key enumeration occurs on the client thread.
- No complete remote manifest enumeration occurs after ingestion.
- Idle discovery performs near-zero main-thread work.
- Transient misses eventually install or remain visibly retryable; no key is stranded.

## 6. Phase 3 — cached bounds and stable projection

**Implementation status:** Complete locally at the source and harness levels. Opaque and
water mesh footprints maintain a cached world-space rectangle; ordinary frames compute the
required distance in O(1). The applied projection rounds upward to 512-block steps, grows
immediately, and shrinks after a stable five-second cooldown. Thirty isolated assertions
cover bounds, caps, quantization, hysteresis, and reset. A corrected short moving-camera
smoke, controlled long warm-cache run, and two clean-cache one-way routes now exist; human
review found the warm route smooth with no noticed clipping or turn-around stalls. Human
in-motion review of cold coverage arrival remains open before broad runtime acceptance.

### Work

- Maintain mesh world-space bounds when section meshes are installed and removed.
- Calculate the farthest required distance from the bounds in O(1) per frame.
- Mark bounds dirty when an extreme mesh is removed; recompute only then or during a coarse maintenance pass.
- Quantize the applied far plane into measured steps with hysteresis.
- Grow immediately when geometry requires it; shrink only after cooldown and meaningful reduction.
- Reset projection only when the applied quantized value changes.
- Preserve explicit `.vhfar` behavior.

### Acceptance

- Ordinary movement does not reset projection every frame.
- Far-distance calculation is independent of mesh count in steady state.
- No distant terrain clips during movement or mesh arrival.
- Projection-reset frequency and far-plane behavior are covered by isolated tests and a moving-camera run.

## 7. Phase 4 — smooth periodic server and client work

**Implementation status:** Complete for the measured dedicated-process slices. Sweep and
transient generation run on 50 ms fractional allowances with bounded probe issuance;
server assist uses fair per-player/global allowances and an elapsed deadline. Assist,
sibling-cache, and background-load installs use FIFO 2 ms / 512 KiB drains with oldest-age
telemetry and one-item progress. Completed generation and saturated assist now have
dedicated-process evidence. Server blob reads now run on a bounded dedicated read-only
connection with ordered owning-thread publication. A repeated saturated run transferred
395/395 sections; one 17.481 ms reader call coincided with only 0.989 ms maximum assist
service, directly proving separation. Integrated command generation and sibling adoption
now have guarded runtime evidence; default savegame-sweep cadence in integrated
singleplayer remains open.

### Sweep and generation

- Replace once-per-second batch callbacks with normal-tick servicing and token accumulation.
- Limit issuance by elapsed time, outstanding work, and retained memory.
- Spread main-thread callback publication rather than allowing hundreds of completions to land together.

### Server assist

- Accumulate per-player and global allowances continuously.
- Serve bounded work each tick instead of the whole allowance once per second.
- Move blob reads to a dedicated reader when connection ownership and packet-send ordering are safe.
- Preserve fairness and explicit refusal.

### Client install

- Limit assist arrival and background-load installation by elapsed time and bytes.
- Do not drain a queue solely because results are available.
- Track oldest-item age so a small budget cannot starve old work forever.

### Acceptance

- Telemetry shows no one-second Vintage Horizons burst in the sweep or assist scenarios.
- A slow frame spends less budget rather than accumulating an even larger synchronous follow-up.
- Queues remain bounded and make forward progress.

## 8. Phase 5 — off-thread foreign decode

### Work

- Inflate and structurally deserialize foreign blobs on a worker or storage-owned decoder.
- Retain palette codes and persisted flags without live registry access.
- Publish a decoded immutable result to the owning thread.
- Resolve block ids, live flags, tint slots, and atlas colors under a time budget.
- Filter newly skipped runs only after live classification is available.
- Reject results when local capture became authoritative while decoding.

### Acceptance

- Deflate inflation and run-array parsing disappear from the client owning-thread profile.
- A corrupt or future blob fails one section without killing the worker.
- Local data still wins every race.
- Wire and storage round-trip/rejection tests pass.

## 9. Phase 6 — versioned asynchronous mip work

**Implementation status:** The runtime slice is complete for dedicated client/server
evidence. Content revisions, world epochs, bounded in-flight jobs, a dedicated worker,
stale/failure retry, parent pins, owning-thread palette remap/publication, telemetry,
regression checks, controlled before/after routes, a long convergence soak, graceful
restart, deliberate interruption after a durable `ApplyToParent` write, recovery, and a
fresh-process zero-obligation postcheck are complete in both dedicated and integrated
process layouts. Any further optimization of the owning-thread publication tail remains
open.

### Revision model

- Add a monotonic content revision to each live section.
- Increment it whenever captured columns or parent quadrant content changes.
- A mip job records child key, child revision, parent key, parent quadrant, and immutable child data.
- Deduplicate in-flight jobs by child key/revision.
- If a child changes during work, preserve a rerun obligation.

### Worker output

- Perform vertical boundary collection, sorting, occupancy selection, and merged-run construction off-thread.
- Return quadrant column data with enough palette identity for safe owning-thread remap.
- Avoid touching the live parent from the worker.

### Commit

- Validate child revision before applying.
- Discard stale results and requeue the newest obligation.
- Remap palette entries and replace the parent quadrant on the owning thread.
- Clear the child's persisted `ApplyToParent` obligation only after a valid commit.
- If final `ReplaceColumns` remains expensive, profile a copy-on-write or per-column immutable section representation as a separate change.

### Acceptance

- Boundary sorting and merge construction no longer appear in owning-thread time.
- Stale results cannot overwrite newer capture.
- Repeated edits converge and leave no stuck `MipDirty` keys.
- Restart during pending propagation preserves obligations.
- Existing mip correctness checks and new concurrency checks pass.

## 10. Phase 7 — visibility-aware renderer and bounded uploads

**Implementation status:** Visibility-aware traversal and its separate distance/age mesh
residency policy are source-, harness-, and controlled-runtime-complete at 601 cached
sections. The same-cache pair reduced selected nodes 64.2%, weighted average traversal
19.8%, and weighted average draw submission 9.3%, while retaining 543 meshes with zero
evictions. Aggregate FPS was unchanged within run noise and is not claimed. Incremental
render-dirty priority scheduling is source-, harness-, and functional-smoke-complete at
601 cached sections. Mesh-snapshot and GPU-upload boundary budgets are source-, build-,
and harness-complete. A 3,132-section scale run now supplies runtime queue/timing evidence;
human clipping/turn-around review remains open.

### Traversal

- Frustum-test a node's world-space bounds before descending.
- Skip invisible subtree draw preparation and mesh demand.
- Keep coverage rules for visible nodes unchanged.

### Residency

- Track visibility separately from residency need.
- Do not make one camera turn evict every hidden gate mesh.
- Use distance, memory pressure, and independent age for eviction.

### Scheduling

- Replace repeated full `RenderDirty` nearest scans with spatial buckets or a priority structure refreshed when the camera crosses a coarse cell.
- Preserve deduplication and background-load routing.
- Bound snapshot creation by elapsed time and estimated retained bytes.

**Implementation status:** Exact dirty membership now feeds new-key deltas to a
nearest-first heap. Priorities rebuild on 256-block camera-cell crossings, detail-policy
changes, and world clears. Stale entries validate membership; busy mesh/load keys are
restored under a finite examination ceiling. Twenty focused assertions and one
601-section functional route establish correctness and convergence. Snapshot production
now stops at job boundaries after 1 ms, 2 MiB of estimated retained arrays, or four jobs;
one first item always progresses. Controlled large-cache timing remains open.

### GPU upload

- Limit uploads by elapsed time and vertex/index bytes.
- Retain the old mesh until replacement upload succeeds.
- Track GL upload time, dispose time, bytes, and queue age.

**Implementation status:** Completed results stop at boundaries after 2 ms, 4 MiB of
live vertex/index data, or four results, with one-first-item progress. The new
opaque/water pair becomes live before the old pair is disposed; partial failure retains
the old pair and restores dirty work. Telemetry reports upload/disposal timing, bytes,
backlog, and oldest age. A 601-section route and sustained growth to 3,132 persisted
sections bounded sampled queues to 18 snapshots/four uploads, measured direct GL upload
below 6.9 ms, recorded no 25 ms renderer phase, and converged. Human visual and
cross-driver validation remain open.

### GPU visibility and later draw-call work

Back-face rejection and front-to-back opaque submission are complete and human-tested.
On one machine and fixed views they raised 218 to 260 FPS and 149 to 173 FPS respectively,
with no visible change. The fallbacks remain `.vhbackface off` and `.vhfront off`.

The next measured problem was hidden cached terrain behind mountainous foreground. A
same-frame bounding-box query prototype was rejected despite eventually reporting 83% hidden
boxes: 156 FPS became 155, its work ran before the nearby vanilla hill existed in depth, and
conditional rendering retained CPU submission plus proxy/query cost. The accepted design
instead moves cached terrain from opaque order 0.36 to 0.38, just after vanilla terrain at
0.37, and lets the ordinary depth test reject hidden fragments. The owner measured 148 to
179 FPS in the valley (about 1.17 ms saved) and 590 to 651 while looking down (about 0.16 ms),
with only minute acceptable distant changes. It is default-on in 0.3.30 and retains
`.vhocclusion off`.

After that evidence:

- Evaluate regional combined buffers.
- Evaluate multi-draw or instancing with per-section metadata.

### Acceptance

- CPU traversal and mesh demand fall when most cached terrain is outside the view.
- Costs scale primarily with visible radius rather than total explored history.
- Turning around does not create holes or a large remesh storm.
- One large completed mesh cannot consume an unrestricted frame.

## 11. Phase 8 — persistence acknowledgements and shutdown

### Work

- Give save snapshots section identity and revision.
- Track queued, completed, failed, and superseded revisions.
- Clear durable dirty state only when the required revision is acknowledged, or retain an explicit newer obligation.
- Retry transient write failures with bounded backoff.
- Coalesce queued snapshots superseded before write.
- During close, drain existing backlog, enqueue remaining dirty revisions, and drain again until clean or a reported timeout.
- Never silently drop work because the initial close call found `MaxStorageBacklog` already full.

### Acceptance

- Injected write failure does not lose dirty state.
- Repeated mutation while a save is queued persists the newest revision.
- Close with a full backlog either persists everything or reports exact unresolved keys/revisions.
- Cache rows remain readable after restart.

**Implementation status:** Complete in source and deterministic checks. Runtime-only
persistence revisions are distinct from content revisions; exact success/failure
acknowledgements retain newer/failed dirty state, pending same-key snapshots coalesce,
retries use 250 ms exponential delay capped at 30 seconds, and close repeats
drain/ack/enqueue until clean or a 15-second exact unresolved report. Injected failure,
repeated mutation, 300-key drain, and newest-row SQLite restart checks pass. A
3,132-section game cache reopened, wrote 138 revisions, and reached zero unsaved/backlog/
errors before graceful shutdown. Persistent-failure timeout logging remains unforced in
a game process.

## 12. Final validation matrix

Run and preserve results for:

- Documentation checks.
- Fast correctness checks.
- Smoke client/server test.
- Install/admin configuration matrix.
- Cold and warm joins.
- Moving and rotating camera routes.
- Active exploration.
- Integrated-server sweep.
- Server-assist transfer.
- Large-cache steady state.
- Shutdown during backlog.

Compare average, median, 1% low, p95/p99/max frame time, hitch counts, phase maxima, projection resets, allocations, queue age, CPU memory, and GPU mesh counts against baseline.

## 13. Structural completion criteria

- No whole-cache SQLite scan runs on the client thread.
- No complete remote manifest is reprocessed every tick.
- Ordinary movement does not reset projection continuously.
- No queue drain on an owning/render thread is unbounded by elapsed time or bytes.
- Server rate limits do not release a full second of work in one callback.
- Mip merge computation is off-thread and stale-safe.
- Renderer traversal rejects invisible subtrees without coupling visibility to eviction.
- GPU upload has byte/time ceilings.
- Failed storage writes retain retryable dirty state.
- Every performance claim names its scenario and evidence level.

## 14. Implementation and commit boundaries

Keep changes independently reviewable:

1. Portability fixes and baseline artifacts.
2. Instrumentation and benchmark scenarios.
3. Key discovery and retry correctness.
4. Far-plane bounds and hysteresis.
5. Sweep/assist smoothing and install budgets.
6. Foreign decode worker.
7. Versioned mip worker.
8. Visibility-aware traversal and upload budgets.
9. Persistence acknowledgements.
10. Final tuning and documented defaults.

Do not combine asynchronous mip redesign with renderer traversal changes in one patch. Each changes failure modes enough to deserve its own measurements and regression review.
