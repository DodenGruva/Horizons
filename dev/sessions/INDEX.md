# Vintage Horizons — session record index

> Tier 3 history. Newest first. Read this index; open individual sessions only when a row is relevant.

| # | Date | Version | Compatibility change | Summary |
|---|---|---|---|---|
| 31 | 2026-08-20 | 0.3.23 | - | The water seams were never colour. The mesher walls off a section edge whose neighbour is missing, but decided "missing" from what is in RAM rather than from what the cache holds, and sections mesh nearest-first - so the outward side of nearly every one was walled off before its neighbour loaded, and nothing repaired it. Invisible on land, a permanent 3,200 block2 sheet of translucent water on an ocean. Human-confirmed fixed, shores and cliffs included. |
| 30 | 2026-08-19 | 0.3.22 | - | Four faults in one colour, all diagnosed offline from the owner's cache, the engine's shaders and one screenshot: a block's colour was a random pixel drawn once per section, the seasonal tint was one row of a sixteen-row map, grass-covered ground had red and blue exchanged and was missing the untinted dirt vanilla shows through it, and flat ground was shaded by sun angle where vanilla refuses to darken it. Albedo now reproduces vanilla's own blend exactly. |
| 29 | 2026-08-19 | 0.3.17 | - | Closed the band: the engine range-culls every terrain mesh per frame against the view distance, which no per-chunk signal reflects, so ownership held the trailing annulus forever. Human-confirmed fixed, and the per-cell mask became the default. |
| 28 | 2026-08-19 | 0.3.15 | - | Fixed a silent startup fault, made the per-cell mask actually work after an integer uniform the driver had rejected since 0.3.0, corrected G40 from the game IL, and disproved five explanations of the missing band, which remains open. |
| 27 | 2026-08-19 | 0.3.0-dev | — | Gave each vanilla chunk its own ground behind an opt-in gate, skipped fully replaced cached meshes for +7.3% frame rate, and fixed ownership latency found by playtesting at flight speed. |
| 26 | 2026-08-19 | 0.2.1 | — | Runtime-validated the readiness tracker, fixed a sweep that could lose ownership but never gain it, and gave the near handoff a measured radius; 1,225 assertions pass. |
| 25 | 2026-08-18 | 0.2.1 | — | Source-traced the installed 1.22.7 chunk-render lifecycle and added a bounded, pixel-neutral vanilla-readiness shadow tracker; 1,176 assertions pass. |
| 24 | 2026-08-18 | 0.2.1 | — | Source-fixed camera-relative color noise and near-transition sinking, added a conservative radial playtest handoff, and approved a bounded chunk-aware hybrid ownership plan. |
| 23 | 2026-08-18 | 0.2.1 | — | Attributed the remaining server-assist tail to synchronous hot-path progress logging, removed it, added GC/phase diagnostics, and passed 1,058 checks plus a guarded 273-section fix run. |
| 22 | 2026-08-18 | 0.2.1 | — | Proved integrated sibling-cache exact-key retry/adoption and hard mip interruption/recovery through a fresh zero-obligation postcheck; 1,056 checks pass. |
| 21 | 2026-08-18 | 0.2.1 | — | Proved renderer budgets through 3,132 cached sections; added revision-acknowledged saves, retry/coalescing/shutdown draining, and 1,050 passing checks. |
| 20 | 2026-08-18 | 0.2.1 | — | Time/byte-budgeted mesh snapshots and GPU uploads; safe mesh replacement and queue/GL telemetry; 1,002 checks pass. |
| 19 | 2026-08-18 | 0.2.1 | — | Replaced per-frame whole-dirty-set scans with an incremental coarse-cell priority index; 995 checks and a 601-section functional route converged cleanly. |
| 18 | 2026-08-18 | 0.2.1 | — | Rejected invisible quadtree subtrees without visibility-driven eviction; a controlled 601-section pair cut selected nodes 64.2% and average traversal 19.8%. |
| 17 | 2026-08-18 | 0.2.1 | — | Interrupted after a durable mip flag, recovered one obligation to clean convergence, and proved a later fresh process loaded zero obligations. |
| 16 | 2026-08-18 | 0.2.1 | — | Added semantic mip/persistence convergence proof; a 2,401-column movement run and 601-section fresh restart both drained cleanly. |
| 15 | 2026-08-18 | 0.2.1 | — | Moved server-assist blobs to an ordered read-only worker; 395 sections transferred and a 17.481 ms read no longer blocked assist service. |
| 14 | 2026-08-18 | 0.2.1 | — | Added semantic generation/assist scenarios; fixed an early-join request drop; 395 live sections transferred and synchronous server reads reached 68.755 ms. |
| 13 | 2026-08-18 | 0.2.1 | — | Added cache-state and server-completion benchmark proofs; warm join and a 3,249-position sweep completed without ≥25 ms VH/server ticks. |
| 12 | 2026-08-17 | 0.2.1 | — | Added server pipeline/sweep/generation/assist telemetry and real stats A/B controls; warmed stationary pairs measured about 0.7% average-FPS overhead. |
| 11 | 2026-08-17 | 0.2.1 | — | Added a one-way clean-cache capture-frontier route and endpoint cooldown; two runs had no ≥25 ms VH ticks and bounded, convergent backlog. |
| 10 | 2026-08-17 | 0.2.1 | — | Reclassified the long route as warm-cache traversal; recorded a positive human smoothness/clipping review; made uncached-terrain validation explicit. |
| 9 | 2026-08-17 | 0.2.1 | — | Ran the full corrected movement/rotation route; bounded capture publication by time/bytes; added result backpressure, epoch rejection, and before/after evidence. |
| 8 | 2026-08-17 | 0.2.1 | — | Added deterministic moving/rotating benchmark routes; corrected PI-centred camera pitch; narrowed old sky-biased evidence and preserved four mip CSVs. |
| 7 | 2026-08-17 | 0.2.1 | — | Moved foreign inflation/structural decode to the storage owner; retained request state until publication; added opt-in per-phase allocation telemetry. |
| 6 | 2026-08-17 | 0.2.1 | — | Smoothed sweep/generation/assist allowances across ticks; time/byte-bounded owning-thread installs with FIFO progress and queue telemetry. |
| 5 | 2026-08-17 | 0.2.1 | — | Cached opaque/water mesh bounds; made steady far-distance calculation O(1); stabilized projection with quantized growth and delayed shrink. |
| 4 | 2026-08-17 | 0.2.1 | — | Moved sibling-cache key discovery to a dedicated delta publisher; applied manifests once; made local/server failures explicitly retryable or terminal. |
| 3 | 2026-08-17 | 0.2.1 | — | Instrumented and reproduced game-tick spikes; moved versioned mip construction off-thread; two before/after routes eliminated measured ≥25 ms tick hitches. |
| 2 | 2026-08-17 | 0.2.1 | — | Supplied snapshot matched to fork history; working branch rebased onto release 0.2.1; documentation and performance plan reconciled with existing fork fixes. |
| 1 | 2026-08-17 | 0.2.0 | — | Source-traced main-thread performance review; GitHub fork association; lifetime-tiered documentation workflow established; remediation plan approved and preserved. |
