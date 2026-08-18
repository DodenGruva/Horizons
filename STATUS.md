# Vintage Horizons — status

> Tier 2: current state, regenerated as a coherent document at session close. Durable design lives in `dev/ARCHITECTURE.md`; open work lives in `dev/TODO.md`.

**Status date:** 2026-08-18
**Mod version:** `0.2.1`
**Target:** Vintage Story 1.22.5+, .NET 10
**Source files:** `35` C# files under `VintageHorizons/src`
**Assist protocol:** `1`
**Blob format:** `4`
**Database schema:** `6`

## 1. Repository state

`origin` points to the user's fork at `https://github.com/DodenGruva/Horizons`. The supplied source was code-equivalent to fork commit `27e5e6a`; the active branch is `codex/main-thread-performance`, descends from `origin/master` release 0.2.1 at `f8d4b03`, and tracks the same-named origin branch.

The working branch contains the lifetime-tiered documentation workflow, portability and benchmark-harness work, deterministic moving/rotating routes with corrected PI-centred camera pitch, clean-cache capture-frontier and warm-join routes, pinned completed-sweep/generation and saturated-assist scenarios, expanded client/server performance and allocation instrumentation, versioned asynchronous mip propagation, incremental local/network key discovery with retry-safe request transitions, cached renderer bounds with stable projection changes, tick-smoothed server work, time/byte-bounded client installs and capture publication, storage-owned foreign structural decode, and ordered off-thread server-assist blob reads. The Windows runner can prove active client/server cache state, semantic generation completion, assist saturation and installation, final client mip/persistence convergence, pin fresh-server configuration, require terminal server state, install the server mod, and perform genuine stats-disabled comparisons. Private research and benchmark sandboxes remain ignored.

## 2. Product and architecture state

The client captures received chunk columns, converts them into persistent 3D RLE sections, builds a mip pyramid, meshes selected sections on workers, and renders them beyond vanilla view distance. An optional server installation can capture collectively explored terrain, sweep existing savegame columns, generate transient terrain on request, and offer stored sections to clients.

Capture, meshing, mip boundary construction, compression, storage writes, demand-load decompression, foreign blob inflation/structural parsing, and integrated-singleplayer sibling-cache key discovery have background workers. The sibling-cache scanner owns a separate read-only unpooled SQLite connection and publishes bounded immutable key deltas. `LodWorld`, block-registry/palette resolution, revision validation, mip publication, foreign recolouring/publication, GPU upload, selection, and draw setup remain on their owning game or render threads.

Sweep, transient generation, and server-assist allowances accrue across normal 50 ms ticks instead of releasing a full second's work in one callback. Client assist arrivals, decoded foreign publication, completed background-load publication, and capture-result publication use 2 ms / 512 KiB boundary drains. One oldest item always progresses even if it alone exceeds a ceiling; later work waits. Queued/in-progress capture jobs plus completed/deferred results share one 24-item backpressure cap. Server and sibling-cache decode have separate outstanding limits, and concrete result queues expose pending items/bytes and oldest age.

Server-assist blob reads use one dedicated unpooled read-only SQLite connection owned by a
single reader thread. Queued, executing, and completed blobs share a 16-item cap. Results
carry player-session identity and publish in per-player request order on the server thread;
read failures produce explicit retryable replies rather than silence.

Capture jobs/results carry a world epoch, estimated raw-run bytes, and ready time. The owning thread rejects cross-world results, performs live palette registration and section mutation, and records publication throughput/backlog. A queue clear is not treated as a teardown identity because an in-progress worker job may publish after it.

Foreign decode results carry a world epoch, key, source, deferred palette codes, estimated bytes, and ready time. Network/world request state survives decoder acceptance until owning-thread publication or terminal rejection. A resident local section wins both pre- and post-resolution checks. The foreign reload route remains available until future save acknowledgements can prove the adopted local row durable.

Mip jobs carry a world epoch, child identity, and content revision. The child remains `MipDirty` and its parent remains RAM-pinned until a matching result commits. Failed, stale, or cross-world results cannot clear the durable `ApplyToParent` obligation.

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
reader calls stayed at 0.179 ms maximum and individual sends at 0.249 ms maximum; that
tail remains unattributed rather than being assigned to SQLite. The client had no reported
25 ms Vintage Horizons tick and the separate server capture pipeline had no 25 ms tick;
the assist phase itself did record that 32.450 ms outlier. Queues drained and shutdown was
graceful.

A later 120-second, 1,600-block movement run started from a proven 405-section warm
client cache, captured 2,401 columns, and recorded no Vintage Horizons tick at or above
25 ms. A 45-second cooldown ended with zero capture input/results, worker errors, mip
queue/in-flight/dirty work, unsaved sections, asynchronous loads, and storage
backlog/errors. The resulting cache grew from 20,672,512 to 29,982,720 bytes. Fresh server
and client processes then reported 601 cached sections and reached the same guarded zero
state. This establishes graceful sustained-work convergence and persisted restart in
separate processes, not interruption while work is active or integrated singleplayer.

## 5. Remaining performance findings

1. Capture-result publication remains a major measured owning-thread pipeline phase, but aggregate publication is boundary-budgeted. One admitted result remains non-preemptible and reached 9.028 ms on the clean-cache frontier route.
2. Foreign live block resolution, recolouring, filtering, and publication remain owning-thread work. Aggregate work is bounded, but one admitted publication cannot be preempted once started.
3. Server-assist packet publication remains owning-thread work. The off-thread-reader run
contained a 32.450 ms service outlier in an interval whose reader and individual-send
maxima were only 0.179/0.249 ms; packet serialization, GC, and process scheduling are not
yet separately attributed.
4. Dirty pruning, scheduling, and quadtree work still scale with whole collections; GPU upload remains limited by mesh count rather than time/bytes.

The approved and now evidence-reordered sequence is `dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md`.

## 6. Remaining correctness and durability findings

- Dirty sections leave `SaveDirty` when queued rather than after a storage acknowledgement. Failed writes do not automatically restore the exact dirty revision.
- Shutdown can encounter dirty state after the storage enqueue cap is full; save revisions/acknowledgements remain open work.
- Asynchronous mip propagation now has one longer warm-cache movement/capture run and a
  clean fresh-process restart. Interruption while propagation is active and
  integrated-singleplayer validation remain open.
- Incremental sibling-cache discovery and retry-safe local misses are harness-tested but
not yet integrated-game-tested. Live server transfer and owning-thread publication now
have dedicated-process evidence.

## 7. Current open work

1. Interrupt asynchronous mip propagation while work is active, restart, and prove
durable `ApplyToParent` convergence; repeat under integrated-singleplayer load.
2. Time/byte-budget GPU uploads and make traversal/scheduling visibility-aware without
coupling visibility to residency.
3. Exercise sibling-cache discovery and retryable local misses in integrated singleplayer.
4. Add storage save revisions, acknowledgements, retry, and shutdown durability.
5. Isolate the remaining server-assist service/send/GC tail if it reproduces.

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
- Decoder acceptance retains request responsibility through publication. The adopted section keeps its foreign fallback because save enqueue is not a durability acknowledgement.
- Allocation counter reads are opt-in per measured owner and sit outside the elapsed-time interval.
- Server pipeline, sweep, generation, and assist phase costs are independently timed;
  allocation reads remain opt-in and assist queues report exact oldest-head age.
- Count-only GPU uploads, owning-thread foreign publication, the single-result capture tail, and save acknowledgement gaps remain visible in source.
- Capture publication is result-boundary time/byte/item bounded; queued/in-progress jobs and completed/deferred results share backpressure, and cross-world results are rejected by epoch.
- Server-assist blob SQL exists only in the dedicated read-only reader; the server thread
  admits ordered session-tagged work and publishes completed packets.

### Harness-tested

- `dev/DocCheck.ps1` passes 238 checks under both Windows PowerShell 5.1 and PowerShell 7.
- The full game-backed fast tier passes 964 assertions across all 22 suites, including 53 benchmark-route/config/camera-mapping, foreign queue/deferred-palette/failure isolation, assist-reader FIFO/cap/miss/failure/handle lifetime, async request-slot retention and saturation accounting, 15 tick-allowance, 14 drain-budget, 30 cached-bounds/far-plane, SQLite discovery/delta, remote-request state, server-assist, blob, and 64 mip assertions.
- Debug builds of the mod, checks, and benchmark harness succeed with zero warnings and errors.
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
- The long warm-cache mip soak and fresh-process restart CSV/scenario proofs are tracked
  under `bench/results/2026-08-18-mip-soak`. Both passed semantic capture/mip/save/load/
  storage convergence; the long run captured 2,401 columns and the restart loaded 601
  cached sections.
- The corrected Windows harness completed client and server shutdown without force termination.

### Human-tested

- The human observer watched the corrected warm-cache movement/rotation route and reported
  that it looked good and smooth, with no noticed transient clipping or turn-around stalls.
  This is qualitative evidence for that populated-cache scenario, not a controlled
  comparison or an unseen-terrain verdict.

### Not yet established

- One brief human playtest reported a noticeable subjective improvement after off-thread foreign decode; it was not a controlled or thorough comparison.
- No person has watched the clean-cache frontier, warm-join, completed-sweep,
  completed-generation, or saturated-assist routes in motion; long join/sweep/assist
  soaks remain untested.
- No integrated game process has yet exercised the sibling-cache discovery worker or end-to-end retryable server response.
- The completed sweep ran in separate dedicated-server/client processes with a warm
  server cache, a calibrated 24-chunk radius, serving disabled, and generation disabled.
  Integrated-singleplayer cadence, cold-cache throughput, and default-radius behavior
  remain unmeasured.
- Capture publication's warm-cache follow-up peaked at 9 queued results / 0.70 MiB / 93 ms oldest and 5.732 ms for one admitted result. Unseen terrain, interrupted shutdown, and integrated-server capture remain untested.
- The mip soak and restart used a warm client cache, graceful convergence before shutdown,
  and separate dedicated-server/client processes. Active-work interruption,
  `ApplyToParent` recovery after such an interruption, and integrated-singleplayer load
  remain untested.
- Two saturated assist runs measured transfer and publication throughput/backlog before
  and after the server reader change, but they did not isolate client worker-decode cost
  from surrounding cold-client capture/save load. No controlled sibling-cache run exists.
- Allocation telemetry's uncapped steady-state average-FPS overhead measured about 0.7%
  across two warmed pairs; ordinary capped-frame-rate effect and tail impact remain unknown.
- GPU shader/fill cost remains unseparated from CPU submission cost.

## 9. Known uncertainty

- The reported recurring spikes may have multiple CPU and GPU causes. The current route proves one major synchronous source, not exclusivity.
- Teleport-driven exploration emphasizes capture/propagation, and the original route's incorrect sky-facing pitch underrepresented terrain traversal, draw, and shader costs.
- Memory readings in the short routes are noisy and were not used to claim an improvement.
- Incremental discovery removes whole-set owning-thread work by construction, but its in-game frame-time effect has not been isolated in a before/after run.
- Cached bounds remove the steady mesh scan by construction. The corrected long warm-cache trajectory exercised continuous movement and stable reset counts, and human review found it smooth with no noticed clipping. Cold coverage arrival, the conservative rectangular overestimate under other routes, and broader visual conditions remain unjudged.
- The 2 ms / 512 KiB install/capture ceilings are conservative initial policy. Compressed foreign bytes, estimated in-memory background-section bytes, and raw capture-run bytes are intentionally path-local measures, not directly comparable throughput figures. One admitted item may exceed the elapsed or byte ceiling.
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
read was a direct owning-thread observation; the repeated run proves reads are now
off-thread but leaves one unrelated 32.450 ms service outlier unattributed.

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
