# Vintage Horizons — status

> Tier 2: current state, regenerated as a coherent document at session close. Durable design lives in `dev/ARCHITECTURE.md`; open work lives in `dev/TODO.md`.

**Status date:** 2026-08-17
**Mod version:** `0.2.1`
**Target:** Vintage Story 1.22.5+, .NET 10
**Source files:** `32` C# files under `VintageHorizons/src`
**Assist protocol:** `1`
**Blob format:** `4`
**Database schema:** `6`

## 1. Repository state

`origin` points to the user's fork at `https://github.com/DodenGruva/Horizons`. The supplied source was code-equivalent to fork commit `27e5e6a`; the active branch is `codex/main-thread-performance`, descends from `origin/master` release 0.2.1 at `f8d4b03`, and tracks the same-named origin branch.

The working branch contains the lifetime-tiered documentation workflow, portability and benchmark-harness work, expanded performance instrumentation, versioned asynchronous mip propagation, incremental local/network key discovery with retry-safe request transitions, and cached renderer bounds with stable projection changes. Private research and benchmark sandboxes remain ignored.

## 2. Product and architecture state

The client captures received chunk columns, converts them into persistent 3D RLE sections, builds a mip pyramid, meshes selected sections on workers, and renders them beyond vanilla view distance. An optional server installation can capture collectively explored terrain, sweep existing savegame columns, generate transient terrain on request, and offer stored sections to clients.

Capture, meshing, mip boundary construction, compression, storage writes, demand-load decompression, and integrated-singleplayer sibling-cache key discovery have background workers. The sibling-cache scanner owns a separate read-only unpooled SQLite connection and publishes bounded immutable key deltas. `LodWorld`, block-registry/palette resolution, revision validation, mip publication, local blob adoption, GPU upload, selection, and draw setup remain on their owning game or render threads.

Mip jobs carry a world epoch, child identity, and content revision. The child remains `MipDirty` and its parent remains RAM-pinned until a matching result commits. Failed, stale, or cross-world results cannot clear the durable `ApplyToParent` obligation.

## 3. Completed local performance work

1. The full 0.2.1 fast tier now runs against the complete game installation. The Windows SQLite stale-version fixture disables connection pooling for its direct schema edit, preventing the old pooled connection from surviving file replacement.
2. Client telemetry records p95/p99/max and 25/50/100 ms hitch counts for total game tick, assist/local-offer/pipeline work, pipeline subphases, and render subphases. It also counts projection resets and uploaded mesh bytes.
3. A Windows-native isolated benchmark runner launches a hidden server and rendered client with pidfile/command-line safety checks. It never force-kills a process and now sends the graceful server-stop command before publishing its completion marker.
4. Expensive mip boundary collection, sorting, occupancy selection, and run merging moved from the owning tick to a dedicated bounded worker.
5. Content revisions, world epochs, parent pins, in-flight limits, explicit failed results, and stale-result retry protect asynchronous mip publication.
6. Lower-core machines reserve capacity for game render/simulation work by accounting for the dedicated capture and mip threads when selecting mesh-worker count.
7. Sibling-cache key enumeration moved from the game tick to a dedicated coarse-cadence reader; it publishes newly discovered keys in batches of at most 2,048 and unchanged scans publish nothing.
8. Server manifest ingestion applies one protocol-bounded chunk per tick and sends only that chunk's new keys into the pipeline instead of re-enumerating the retained manifest every tick.
9. Local and network transfer failures now end in explicit installed, retryable, or unavailable state. Retryable server replies restore the pipeline request under a 7.5-second cooldown and bounded roughly one-minute retry window.
10. Opaque and water mesh footprints maintain cached world-space bounds. Ordinary frames calculate the farthest required distance in O(1); the camera projection grows immediately in safe 512-block steps and shrinks only after a lower step remains stable for five seconds.

## 4. Measured diagnosis and result

Two short active-exploration route runs on unmodified 0.2.1 showed pipeline time tracking total game-tick time almost exactly. Synchronous mip propagation reached 20–22.5 ms p95, 32.5–35 ms p99, and 103.1 ms maximum. Total game ticks reached 107.9 ms, while every measured render phase stayed below the 25 ms hitch threshold. This established mip propagation as the dominant reproduced owning-thread spike in that scenario.

Two same-route runs after the asynchronous change ended with zero mip errors, zero pending/in-flight mip work, zero unsaved sections, and zero game ticks at or above 25 ms. Mip apply/schedule maxima were 1.93/0.43 ms. Mean worst-1%-frame time across the two runs on each side changed as follows:

| Waypoint | Before | After | Reduction |
|---|---:|---:|---:|
| spawn-horizon | 17.86 ms | 3.62 ms | 79.8% |
| spawn-look-south | 2.48 ms | 2.23 ms | 10.1% |
| ridge-east | 24.08 ms | 2.18 ms | 90.9% |
| valley-north | 10.99 ms | 1.60 ms | 85.5% |
| high-overlook | 11.79 ms | 2.38 ms | 79.8% |

The route is intentionally short and teleport-driven. It is strong evidence for the isolated spike class, not yet proof of long-session behavior or subjective play quality.

## 5. Remaining performance findings

1. Capture-result publication is now the largest measured owning-thread pipeline phase, reaching 11.3–14.1 ms maximum in the after runs.
2. Sweep, transient generation, and assist serving still release per-second allowances in one-second callbacks.
3. Assist arrivals and background load results are drained without elapsed-time or byte ceilings. Local foreign-blob read/decode/recolour/install remains owning-thread work under an item-count budget.
4. Dirty pruning, scheduling, and quadtree work still scale with whole collections; GPU upload remains limited by mesh count rather than time/bytes.

The approved and now evidence-reordered sequence is `dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md`.

## 6. Remaining correctness and durability findings

- Dirty sections leave `SaveDirty` when queued rather than after a storage acknowledgement. Failed writes do not automatically restore the exact dirty revision.
- Shutdown can encounter dirty state after the storage enqueue cap is full; save revisions/acknowledgements remain open work.
- Asynchronous mip propagation still needs long soak, interrupted restart, and integrated-server validation beyond its regression checks and two short routes.
- Incremental sibling-cache discovery and retry-safe local/server request transitions are harness-tested but not yet integrated-game-tested.

## 7. Current open work

1. Spread sweep and assist allowances across ticks, then time/byte-budget owning-thread installs; this is the next implementation phase.
2. Add continuous movement/camera-rotation, warm join, sweep, and server-assist scenarios plus queue-age/allocation telemetry.
3. Time-budget capture publication and GPU uploads where longer measurement warrants.
4. Move foreign structural decode off-thread with owning-thread registry resolution.
5. Make traversal/scheduling visibility-aware without coupling visibility to residency.
6. Add storage save revisions, acknowledgements, retry, and shutdown durability.

Detailed tasks and human decisions are in `dev/TODO.md`.

## 8. Verification evidence

### Source-traced

- Supplied code matched fork commit `27e5e6a` with zero source/asset project mismatches.
- The active branch descends from fork release 0.2.1 at `f8d4b03`.
- The complete sibling-cache key query exists only on the dedicated discovery worker; the owning tick consumes at most one immutable delta batch.
- Manifest ingestion publishes each chunk's new keys once; ordinary ticks no longer pass the retained `RemoteKeys` set through the pipeline.
- The steady far-distance path no longer enumerates resident mesh dictionaries. Mesh arrival/removal owns bounds updates, and only an extreme removal requests a later exact rebuild.
- Burst callbacks, unbounded drains, count-only uploads, and save acknowledgement gaps remain visible in source.

### Harness-tested

- `dev/DocCheck.ps1` passes 174 checks under both Windows PowerShell 5.1 and PowerShell 7.
- The full game-backed fast tier passes 758 assertions across all 19 suites, including 30 cached-bounds/far-plane assertions, SQLite discovery/delta, remote-request state, server-assist retry, blob, and 64 mip assertions.
- Debug builds of the mod, checks, and benchmark harness succeed with zero warnings and errors.
- Four isolated route artifacts exist locally: two before and two after. Both after runs converged with no mip queue/in-flight backlog and no mip errors.
- The corrected Windows harness completed client and server shutdown without force termination.

### Not yet established

- No human playtest has evaluated the runtime change.
- No long continuous-movement, rotation, join, sweep, or assist soak has been compared before/after.
- No integrated game process has yet exercised the sibling-cache discovery worker or end-to-end retryable server response.
- No moving-camera game process has yet measured projection-reset frequency or visually checked the cached-bounds far plane for clipping.
- GPU shader/fill cost remains unseparated from CPU submission cost.

## 9. Known uncertainty

- The reported recurring spikes may have multiple CPU and GPU causes. The current route proves one major synchronous source, not exclusivity.
- Teleport-driven exploration emphasizes capture/propagation and may underrepresent steady traversal, projection, and shader costs.
- Memory readings in the short routes are noisy and were not used to claim an improvement.
- Incremental discovery removes whole-set owning-thread work by construction, but its in-game frame-time effect has not been isolated in a before/after run.
- Cached bounds remove the steady mesh scan by construction, but the 512-block projection step, five-second shrink cooldown, and conservative rectangular overestimate have not yet been evaluated in continuous play.
- A practical default far-distance cap remains a product decision requiring benchmark and playtest evidence.

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
| `CHANGELOG.md` | Released player-visible history |
| `README.md` | Player and contributor-facing overview |
