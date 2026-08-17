# Vintage Horizons — status

> Tier 2: current state, regenerated as a coherent document at session close. Durable design lives in `dev/ARCHITECTURE.md`; open work lives in `dev/TODO.md`.

**Status date:** 2026-08-17
**Mod version:** `0.2.1`
**Target:** Vintage Story 1.22.5+, .NET 10
**Source files:** `31` C# files under `VintageHorizons/src`
**Assist protocol:** `1`
**Blob format:** `4`
**Database schema:** `6`

## 1. Repository state

`origin` points to the user's fork at `https://github.com/DodenGruva/Horizons`. The supplied source was code-equivalent to fork commit `27e5e6a`; the active branch is `codex/main-thread-performance`, based on and tracking `origin/master` release 0.2.1 at `f8d4b03`.

The working branch contains the lifetime-tiered documentation workflow, portability and benchmark-harness work, expanded performance instrumentation, and the first measured runtime fix: versioned asynchronous mip propagation. Private research and benchmark sandboxes remain ignored.

## 2. Product and architecture state

The client captures received chunk columns, converts them into persistent 3D RLE sections, builds a mip pyramid, meshes selected sections on workers, and renders them beyond vanilla view distance. An optional server installation can capture collectively explored terrain, sweep existing savegame columns, generate transient terrain on request, and offer stored sections to clients.

Capture, meshing, mip boundary construction, compression, storage writes, and demand-load decompression have background workers. `LodWorld`, block-registry/palette resolution, revision validation, mip publication, GPU upload, selection, and draw setup remain on their owning game or render threads.

Mip jobs carry a world epoch, child identity, and content revision. The child remains `MipDirty` and its parent remains RAM-pinned until a matching result commits. Failed, stale, or cross-world results cannot clear the durable `ApplyToParent` obligation.

## 3. Completed local performance work

1. The full 0.2.1 fast tier now runs against the complete game installation. The Windows SQLite stale-version fixture disables connection pooling for its direct schema edit, preventing the old pooled connection from surviving file replacement.
2. Client telemetry records p95/p99/max and 25/50/100 ms hitch counts for total game tick, assist/local-offer/pipeline work, pipeline subphases, and render subphases. It also counts projection resets and uploaded mesh bytes.
3. A Windows-native isolated benchmark runner launches a hidden server and rendered client with pidfile/command-line safety checks. It never force-kills a process and now sends the graceful server-stop command before publishing its completion marker.
4. Expensive mip boundary collection, sorting, occupancy selection, and run merging moved from the owning tick to a dedicated bounded worker.
5. Content revisions, world epochs, parent pins, in-flight limits, explicit failed results, and stale-result retry protect asynchronous mip publication.
6. Lower-core machines reserve capacity for game render/simulation work by accounting for the dedicated capture and mip threads when selecting mesh-worker count.

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
2. `PumpLocalOffers` still executes a full SQLite section-key scan on the game thread once per second. Network assist also passes the complete `RemoteKeys` set through `AddRemoteKeys` every tick.
3. Every rendered frame scans resident section meshes for camera-relative far distance. Exact float changes can reset projection during movement; active intervals counted 2–19 resets.
4. Sweep, transient generation, and assist serving still release per-second allowances in one-second callbacks.
5. Assist arrivals and background load results are drained without elapsed-time or byte ceilings.
6. Dirty pruning, scheduling, far-plane calculation, and quadtree work still scale with whole collections; GPU upload remains limited by mesh count rather than time/bytes.

The approved and now evidence-reordered sequence is `dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md`.

## 6. Remaining correctness and durability findings

- A transient local sibling-cache blob miss is placed in the `taken` array before read success. `MarkRemoteRequested(taken)` can remove it from the wanted set while `LoadsInFlight` remains set, stranding the key.
- Dirty sections leave `SaveDirty` when queued rather than after a storage acknowledgement. Failed writes do not automatically restore the exact dirty revision.
- Shutdown can encounter dirty state after the storage enqueue cap is full; save revisions/acknowledgements remain open work.
- Asynchronous mip propagation still needs long soak, interrupted restart, and integrated-server validation beyond its regression checks and two short routes.

## 7. Current open work

1. Add continuous movement/camera-rotation, warm join, sweep, and server-assist scenarios plus queue-age/allocation telemetry.
2. Make local/network key discovery incremental and repair transient-miss state transitions.
3. Cache mesh bounds and stabilize far-plane projection changes.
4. Smooth periodic server work and time/byte-budget installs, capture publication, and uploads where measurement warrants.
5. Move foreign structural decode off-thread with owning-thread registry resolution.
6. Make traversal/scheduling visibility-aware without coupling visibility to residency.
7. Add storage save revisions, acknowledgements, retry, and shutdown durability.

Detailed tasks and human decisions are in `dev/TODO.md`.

## 8. Verification evidence

### Source-traced

- Supplied code matched fork commit `27e5e6a` with zero source/asset project mismatches.
- The active branch descends from fork release 0.2.1 at `f8d4b03`.
- Remaining key scans, far-plane scan/reset, burst callbacks, unbounded drains, count-only uploads, transient local miss, and save acknowledgement gaps remain visible in source.

### Harness-tested

- `dev/DocCheck.ps1` passes 168 checks under both Windows PowerShell 5.1 and PowerShell 7.
- The full game-backed fast tier passes 707 assertions across all 18 suites, including SQLite/blob fixtures and 64 mip assertions.
- Debug builds of the mod, checks, and benchmark harness succeed with zero warnings and errors.
- Four isolated route artifacts exist locally: two before and two after. Both after runs converged with no mip queue/in-flight backlog and no mip errors.
- The corrected Windows harness completed client and server shutdown without force termination.

### Not yet established

- No human playtest has evaluated the runtime change.
- No long continuous-movement, rotation, join, sweep, or assist soak has been compared before/after.
- GPU shader/fill cost remains unseparated from CPU submission cost.

## 9. Known uncertainty

- The reported recurring spikes may have multiple CPU and GPU causes. The current route proves one major synchronous source, not exclusivity.
- Teleport-driven exploration emphasizes capture/propagation and may underrepresent steady traversal, projection, and shader costs.
- Memory readings in the short routes are noisy and were not used to claim an improvement.
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
