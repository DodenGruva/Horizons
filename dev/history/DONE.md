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
