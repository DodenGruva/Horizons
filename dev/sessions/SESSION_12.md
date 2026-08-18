# Session 12 — server telemetry and overhead baseline

**Date:** `2026-08-17`
**Branch/commit:** `codex/main-thread-performance`, working tree based on `2c752d0`
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. This session changes diagnostics and benchmark
> controls only; it does not change wire, blob, or schema compatibility.

## Context and investigation

The next open observability work was server-side phase attribution and a controlled
enabled-versus-disabled allocation-telemetry comparison. The client already exposed
phase costs, but sweep, transient generation, server assist, and the server capture
pipeline did not. The Windows runner also installed the mod only on the client and always
enabled stats, so it could neither exercise the server phases nor create a real off sample.

Inspection found that `VINTAGEHORIZONS_AUTOUNPAUSE=1` implicitly enabled allocation
telemetry even when `VINTAGEHORIZONS_STATS` was absent. Since every unattended benchmark
sets auto-unpause, simply adding a nominal off switch to the runner would have produced a
false comparison. Stats ownership was separated from window-focus control before measuring.

## Work narrative

1. Added owning-thread p95/p99/max, hitch, managed-allocation, and resettable interval
   costs for the server pipeline; sweep probe issuance, probe callback publication, and
   load issuance; generation probe issuance/publication and work issuance; and assist
   service, blob reads, section sends, and follow-up offer scans.
2. Added assist request queue depth and exact oldest-head age to the server interval line.
3. Extended the Windows benchmark runner with server-mod installation, an auto-command
   seam for generation scenarios, and a real stats-disable switch. The portable runner
   gained a matching no-stats control and exports the mode before server startup.
4. Added a one-view stationary route and ran three alternating on/off pairs. The first
   stats-on process was a clear warm-up outlier. Across the two warmed pairs, stats-on
   averaged 442.95 FPS versus 446.10 off (0.7% lower); median FPS was 1.0% lower. The 1%
   lows reversed direction between pairs and support no tail claim.
5. Ran an isolated server-mod smoke with the default sweep. Server phase/allocation lines
   emitted on schedule and shutdown was graceful. No assist section request arrived, so
   blob-read and section-send runtime cost remain unexercised.

---

## Delivered

- Server phase and allocation telemetry across capture, sweep, generation, and assist.
- Exact server-assist pending-request depth and oldest age.
- Reproducible stats-on/off and server-mod controls in both benchmark runners.
- `bench/routes/steady-stationary.txt` and seven preserved CSV artifacts with context.
- Clean Debug builds and the full 900-assertion fast tier.

## Decisions

- Elapsed timing remains always active; managed-allocation counters and continuous logs
  remain opt-in through `VINTAGEHORIZONS_STATS` alone.
- The first on/off pair is preserved but excluded from the warmed summary because its
  apparent 36% gap did not reproduce after process/OS warm-up.
- Server telemetry emission is called integration-tested, while assist blob/send cost is
  left explicitly unverified until requests actually traverse those branches.

## Traps

- A benchmark control is not off merely because its named environment variable is off;
  every alias that can enable the feature must be traced before an A/B run.

## Flagged and unverified

- Warm join, completed sweep, and saturated server-assist scenarios remain open.
- Generation telemetry is built and source-traced but was not exercised in a game process.
- The measured 0.7% throughput cost is an uncapped ~445 FPS result on one machine; it is
  not evidence of a visible difference at ordinary capped frame rates.
- Assist request oldest-age, blob-read, and section-send telemetry still need a live
  transfer scenario.
