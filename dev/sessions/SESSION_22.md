# Session 22 — integrated sibling retry and mip recovery

**Date:** `2026-08-18`
**Branch/commit:** `codex/main-thread-performance` (session-close commit)
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

Session 21 left two unattended correctness gaps: live sibling-cache discovery/retry in
integrated singleplayer and interruption/recovery while the client and server pipelines
share one process. Human review of the thousands-section renderer build was unavailable,
so this session advanced those independent automated gates.

The existing Windows runner always launched a separate dedicated server. The older matrix
scenario proved that `/vhgen` could create a sibling cache in singleplayer, but it did not
guard exact-key retry, final pipeline convergence, or hard interruption/restart.

## Work narrative

1. Added an integrated-singleplayer runner mode with its own nested sandbox, deterministic
   world launch, client/server cache-file separation, integrated server-log parsing, and
   the existing PID/command-line shutdown protections.
2. Added cumulative local-offer telemetry for discovered keys, retryable misses, decoder
   acceptance, owning-thread installation, remaining remote-only keys, and wanted keys.
3. Added an opt-in one-miss sibling hook. The runner records the missed key and requires
   that exact key to reach owning-thread installation; ordinary processes have no marker
   path and no changed behavior.
4. Scoped the existing mip interruption hook to the client pipeline. In integrated
   singleplayer both storage workers inherit the same environment, so an unscoped server
   write could otherwise win the marker race and invalidate the client-recovery claim.
5. Ran a clean integrated `/vhgen` scenario. The client discovered 211 sibling keys,
   accepted and installed 63, retried and installed exact key `2,2000,2001`, ended with
   zero wanted keys, and passed every client convergence guard.
6. Hard-interrupted the integrated process after a durable client level-0 obligation,
   reopened one persisted obligation to clean convergence, then required a third fresh
   process to load zero obligations and converge again.
7. The first recovery artifact was rejected only because `-ServerCache Warm` requires an
   active server cache report while the integrated server intentionally stayed idle. The
   interruption/recovery pair was repeated without that unrelated guard and passed.

---

## Delivered

- Integrated-singleplayer support in `scripts/bench-windows.ps1`, including exact
  zero-obligation postcheck and local-offer retry guards.
- Guarded sibling-miss and client-only interruption hooks with deterministic coverage.
- 1,056 passing Release assertions across 25 suites and zero-warning Debug builds of the
  mod and benchmark harness.
- Accepted runtime evidence under
  `bench/results/2026-08-18-integrated-singleplayer`.

## Decisions

- Integrated runs use `.testdata/integrated` so their save, authentication copy, caches,
  logs, pidfiles, and temporary named-pipe directory cannot collide with the established
  dedicated-process benchmark sandbox.
- Retry evidence requires the exact missed key to install; aggregate installed counts are
  supporting telemetry, not identity proof.
- The mip marker remains a client-cache test hook. A server-suffix pipeline never enables
  it, even though both sides share environment variables in integrated singleplayer.
- A warm sibling database is recorded during recovery but not required to open when the
  scenario does no server-side work.

## Traps

- Client and server storage workers in integrated singleplayer inherit the same
  environment. A process-global crash marker can prove the wrong database unless hook
  ownership is scoped explicitly.
- `-ServerCache Warm` means the server must actively open and report its cache, not merely
  that a matching file exists before launch. Do not apply that guard to an intentionally
  idle integrated server.

## Flagged and unverified

- Default-radius integrated savegame sweeping and its cadence remain unmeasured; this run
  used command generation with sweeping disabled.
- The forced miss makes retry deterministic but does not measure how often a natural live
  writer/reader race occurs.
- Human visual review of the 3,132-section renderer route remains open.
