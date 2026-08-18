# Vintage Horizons — completed development work

> Tier 3: append-only completion history moved out of `dev/TODO.md`. Released player-visible behavior also belongs in `CHANGELOG.md`.

## 2026-08-17 — documentation foundation

- Reviewed the supplied source with a main-thread performance lens.
- Associated the working directory with the user's GitHub fork as `origin`.
- Located and adopted the lifetime-tiered documentation workflow used by the Layout project.
- Preserved the supplied design and M4/M5 status documents in a dated superseded archive.
- Established the working agreement, architecture, gotchas, current status, TODO, compatibility ledger, plan, session records, and documentation checker.

## 2026-08-17 — fork reconciliation

- Matched the supplied source to fork commit `27e5e6a` by file hashes.
- Attached `codex/main-thread-performance` to `origin/master` release 0.2.1 at `f8d4b03`.
- Preserved pre-reconciliation files, restored the fork's newer source/tests/scripts, and retained the tiered documentation changes.
- Rebased the performance plan and current status around fixes already present in 0.2.1.
- Made the documentation checker portable across Windows PowerShell 5.1 and PowerShell 7; both hosts pass 153 checks.

## 2026-08-17 — main-thread instrumentation and asynchronous mip propagation

- Repaired the Windows SQLite stale-version fixture and completed all fast suites against the installed game.
- Added low-overhead percentile/hitch telemetry for client tick, pipeline, and render phases plus projection-reset and upload-byte counters.
- Added a Windows-native isolated rendered benchmark runner with pidfile safety and graceful client/server shutdown.
- Reproduced synchronous mip propagation at 20–22.5 ms p95, 32.5–35 ms p99, and 103.1 ms maximum on the game tick.
- Moved mip boundary sorting/merge construction to a bounded dedicated worker with world epochs, content revisions, stale/failure retry, and parent pins.
- Added regression checks for immutable worker construction and child mutation during in-flight work.
- Completed two before and two after route runs; after runs ended with no ≥25 ms game-tick hitches and no mip backlog/errors.

## 2026-08-17 — incremental discovery and retry-safe requests

- Moved integrated-singleplayer sibling-cache key enumeration to a dedicated read-only SQLite connection and below-normal reader thread.
- Added coarse background scans that publish only newly discovered keys in bounded immutable batches; unchanged scans produce no game-thread work.
- Applied each incoming server manifest chunk once and stopped re-enumerating the retained remote-key set every tick.
- Replaced implicit local-offer bookkeeping with explicit installed, retryable-miss, and unavailable outcomes.
- Restored retryable server requests to the owning pipeline with a monotonic cooldown and bounded roughly one-minute retry window.
- Cleared queued manifests between worlds and stopped the local reader during both normal world leave and mod disposal.
- Expanded the full game-backed fast tier to 728 passing assertions.

## 2026-08-17 — cached bounds and stable projection

- Replaced the per-frame scan of every opaque mesh with cached horizontal world-space
  bounds covering both opaque and water mesh keys.
- Expanded bounds in constant time on installation and deferred one exact rebuild after an
  extreme mesh removal; ordinary far-distance calculation is independent of mesh count.
- Quantized the applied camera far plane upward in 512-block steps, with immediate growth
  and a five-second stable cooldown before shrink.
- Preserved the continuous shader far edge, vanilla-view safety margin, explicit `.vhfar`
  cap, and world teardown reset.
- Added 30 focused assertions and expanded the full game-backed fast tier to 758 passing
  assertions across 19 suites.
- Built and structurally verified an ignored local Release ZIP for human playtesting.

## 2026-08-17 — smoothed periodic work and bounded client installs

- Replaced one-second sweep and transient-generation batches with 50 ms fractional
  allowances, 1 ms issue deadlines, and at most 16 new probes per tick.
- Replaced one-second server-assist serving with fair per-player/global allowances and a
  2 ms serving deadline; explicit unavailable refusals now drain gradually too.
- Added delayed-tick-safe fractional credit so configured rates remain accurate without
  releasing catch-up work after a slow tick.
- Time/byte-bounded server arrivals, integrated-singleplayer foreign blobs, and completed
  background loads at 2 ms / 512 KiB per owning-thread pass.
- Guaranteed one oldest FIFO item can progress even when it alone exceeds the byte limit.
- Added interval item/byte, pending-byte, and oldest-age telemetry for concrete install
  queues plus stable structural byte estimates for background-loaded sections.
- Added 29 focused assertions and expanded the full game-backed fast tier to 802 passing
  assertions across 21 suites.

## 2026-08-17 — off-thread foreign decode and allocation telemetry

- Moved network and integrated-singleplayer foreign blob inflation and structural parsing
  from the game tick to separately bounded queues on the storage owner.
- Kept live block resolution, classification, recolouring, skip filtering, and publication
  on the owning thread under the 2 ms / 512 KiB install policy.
- Added world epochs, failure isolation, local-win rejection, and request-slot retention
  through actual publication.
- Preserved the foreign reload fallback until storage acknowledgements can prove an
  adopted row durable.
- Added opt-in, per-owner managed-allocation totals and worst-call deltas for client tick,
  pipeline, and render phases without charging counter reads to phase elapsed time.
- Added 75 focused assertions since Session 6 and expanded the full game-backed fast tier
  to 877 passing assertions across 21 suites.
- Built a local test ZIP; a brief human playtest reported a noticeable subjective
  improvement, without a controlled before/after measurement.

## 2026-08-17 — continuous benchmark route and camera correction

- Extended the benchmark route format with elapsed-time position and camera trajectories
  while preserving fixed-waypoint compatibility.
- Added a four-leg, 1,600-block continuous movement route with one full camera turn per
  leg and regression checks for interpolation, angle mapping, and loop continuity.
- Corrected the harness from a false zero-centred pitch assumption to Vintage Story's
  PI-centred camera representation and pinned both mouse axes.
- Reclassified old sky-biased route evidence: its capture/pipeline/mip comparison remains
  useful, but its render load, screenshots, and visual claims do not.
- Completed corrected terrain-facing static and moving integration smokes with graceful
  isolated shutdown and no mod errors or tick hitches.
- Preserved the four short mip before/after CSVs under `bench/results` with their evidence
  limits and kept the large reproducible sandbox ignored.
- Made the Windows runner's PowerShell 7 requirement explicit.
- Added 23 benchmark-route assertions and expanded the full fast tier to 900 assertions
  across 22 suites.

## 2026-08-17 — continuous route evidence and bounded capture publication

- Ran and preserved the corrected 30-second-leg movement/rotation route with one warm-up
  and two measured laps, including hardware, graphics, view-distance, mod-config, and CSV
  context.
- Reproduced capture publication at 12.038 ms maximum and established it as essentially
  the whole worst measured game tick on that route.
- Time/byte-bounded capture publication at result boundaries under the shared 2 ms /
  512 KiB policy while retaining the eight-result ceiling and oldest-item progress.
- Counted queued/in-progress capture jobs plus completed/deferred results under one
  24-item backpressure cap and made ordinary scheduling respect the exact remaining capacity.
- Added result item/byte/age telemetry, worker-side raw-run byte estimates, and world-epoch
  rejection for results published after teardown.
- Repeated the full route: capture maximum fell to 5.732 ms, backlog stayed within 9
  results / 0.70 MiB / 93 ms, and zero ticks reached 25 ms.
- Preserved both full-route CSVs and their evidence limitations under `bench/results`.

## 2026-08-17 — warm-cache route classification and human review

- Established that the full Session 9 route crossed terrain already present in the VH
  cache and reclassified its evidence as warm-cache traversal rather than new exploration.
- Preserved the measured capture-publication comparison because both runs directly
  reported live capture results and owning-thread publication cost.
- Recorded the human verdict that movement and rotation looked good and smooth with no
  noticed clipping or turn-around stalls on that route.
- Closed the warm-cache visual-review task and replaced it with explicit unseen-terrain
  validation whose pre-run cache absence must be proven.

## 2026-08-17 — uncached capture-frontier evidence

- Replaced a self-overlapping cold-cache loop scenario with a one-way 1,600-block route
  that continues the capture frontier for roughly 1,344 blocks beyond its initial streaming
  footprint.
- Added an opt-in post-measurement endpoint cooldown so queue convergence can be observed
  without changing recorded frame samples or existing benchmark defaults.
- Completed two independently reset client-cache runs with zero VH game ticks at or above
  25 ms; worst ticks were 15.790 and 10.950 ms.
- Kept capture backlog within 20 results / 1.61 MiB / 234 ms and 11 results / 0.89 MiB /
  62 ms; the cooldown established zero pending capture, mip, render, save, and storage work.
- Preserved both CSVs and scenario limitations under `bench/results`, including the human
  correction that compact later legs can overlap coverage produced earlier in the same run.

## 2026-08-17 — server telemetry and instrumentation overhead

- Added p95/p99/max, hitch, managed-allocation, queue-depth, and oldest-age telemetry for
  the server capture pipeline, sweep, transient generation, and assist serving.
- Added isolated runner controls for server-mod scenarios, auto-command generation starts,
  and genuine stats-disabled A/B runs.
- Separated auto-unpause from allocation telemetry so unattended stats-off runs are real.
- Added a stationary route and preserved three alternating on/off pairs. The two warmed
  pairs measured about 0.7% lower average FPS and 1.0% lower median FPS with stats at
  roughly 445 uncapped FPS; inconsistent 1% lows support no tail claim.
- Completed a server-mod sweep smoke that emitted the new interval lines and shut down
  gracefully; assist blob/send and generation paths remain runtime-unexercised.

## 2026-08-18 — warm-cache join and completed sweep evidence

- Added non-destructive warm/cold client-cache guards, pinned server-config installation,
  required server terminal-text validation, and portable scenario-provenance JSON to the
  Windows isolated runner.
- Added fixed-view warm-join and completed-sweep routes plus a 24-chunk, 32-column/s sweep
  configuration that excludes assist serving and transient generation.
- Completed a warm join with 558 cached sections, no 25 ms Vintage Horizons tick, and a
  181-section / 51.93 MiB background backlog that drained by 30 seconds.
- Completed a 3,249-position sweep in about 68 seconds: 1,018 existing columns loaded,
  377 frontier columns skipped, nothing generated, and 256/256 sampled absent positions
  remained absent. Reported server ticks stayed below 25 ms.
- Preserved both CSVs, scenario records, context, and evidence limits under
  `bench/results/2026-08-18-join-sweep`.
- Expanded the full game-backed fast tier to 911 passing assertions.

## 2026-08-18 — completed generation and saturated assist evidence

- Added semantic runner guards for active warm/cold server cache state, completed
  transient-generation counters, and saturated live-assist receipt/installation.
- Completed a radius-8 transient run with 289 generated columns, zero failures, and
  256/256 sampled absent positions still absent from the savegame.
- Reproduced an early-join server-assist stall: 16 sections requested, zero received, and
  zero server blob/send work despite a 514-section active server cache.
- Fixed the 50 ms serve loop to retain bounded requests while a joining player is not yet
  exposed as `Playing`; the disconnect event remains responsible for real cleanup.
- Repeated the unchanged scenario with 395 requested/received/installed sections, zero
  declines, and fully drained client transfer/publication queues by 30 seconds.
- Measured server blob reads at 3.75/17.5/68.755 ms p95/p99/max and preserved before/after
  scenario proofs under `bench/results/2026-08-18-generation-assist`.
- Expanded the full game-backed fast tier to 933 passing assertions.

## 2026-08-18 — off-thread server-assist blob reads

- Replaced owning-thread server SQLite blob reads with a bounded dedicated reader that
  owns an unpooled read-only connection and prepared command.
- Preserved per-player request/send order with session-tagged ordered batches; stale
  results cannot cross disconnect/reconnect, and failures return explicit retryable state.
- Removed the shared writable-store blob command and exposed only the server cache path to
  the assist reader.
- Added FIFO, cap, exact-byte, miss, failure, and handle-lifetime regression coverage;
  the full game-backed fast tier increased to 964 passing assertions.
- Repeated the cold-client/warm-server 64/s scenario: 395 sections were requested,
  received, and installed with zero declines and all 16 request slots exercised.
- During a 17.481 ms reader call, owning-thread assist service peaked at 0.989 ms, proving
  the database wait no longer blocks that thread. A separate 32.450 ms service outlier
  occurred with sub-0.2 ms reads and remains a different attribution target.
