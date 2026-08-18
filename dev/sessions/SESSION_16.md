# Session 16 — mip convergence soak and restart proof

**Date:** `2026-08-18`
**Branch/commit:** `codex/main-thread-performance`, working tree based on `99d570f`
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> This session adds benchmark proof and documentation only. It does not change runtime
> terrain behavior, assist protocol, stored blob format, or database schema compatibility.

## Context and investigation

Asynchronous mip construction had strong short-route and regression evidence, but the
approved P1 gate still lacked a reusable semantic assertion for long-run convergence and
fresh-process restart. The Windows runner could finish a frame CSV without proving that
capture, mip, save, load, and storage queues had drained.

## Work narrative

1. Added `-RequireMipConvergence` to the Windows runner. It requires stats, at least 30
   seconds of endpoint cooldown, and a parseable final client pipeline/storage sample.
2. The guard fails on pending capture input/results, capture/mesh/mip worker errors,
   queued/in-flight/dirty mip work, unsaved sections, asynchronous loads, or storage
   backlog/errors. The parsed state is retained in scenario JSON.
3. Ran the 1,600-block, 120-second frontier trajectory against a proven warm client cache.
   It loaded 405 sections, captured 2,401 columns, recorded no 25 ms Vintage Horizons
   tick, and converged every guarded field to zero during a 45-second cooldown.
4. Started fresh server and client processes against the resulting 29,982,720-byte cache.
   The client reported 601 cached sections and again converged all guarded fields to zero.
5. Preserved both CSVs and semantic scenario records under
   `bench/results/2026-08-18-mip-soak` with their warm-cache, graceful-shutdown, and
   separate-process limitations.
6. The full game-backed fast tier passed 964 assertions. PowerShell syntax and invalid
   cooldown validation passed, and the documentation check passed 238 checks.

---

## Delivered

- Reusable semantic client mip/persistence convergence guard in the Windows runner.
- Long warm-cache movement/capture convergence evidence and fresh-process restart proof.
- Tracked CSVs, scenario records, reproducibility context, and explicit evidence limits.
- 964 passing fast assertions and 238 passing documentation checks.

## Decisions

- Renderer-dirty state is recorded but not part of mip/persistence convergence. Visibility
  and mesh demand can legitimately leave render work without implying lost durable mip
  obligations.
- The guard checks capture and load prerequisites as well as mip state. A zero mip queue
  is not meaningful if earlier work that can create new mip obligations is still pending.
- At least 30 seconds of cooldown is required so the 15-second periodic telemetry cadence
  produces a post-route sample rather than reusing a measurement-time snapshot.

## Traps

- A completed frame CSV proves only that the route finished. It does not prove that
  background capture, propagation, or persistence caught up before shutdown.
- Process recovery is safe only through the runner's pidfile and command-line validation.
  The interrupted first orchestration call left its isolated server running; `-ReuseServer`
  resumed that verified process without broad process termination.

## Flagged and unverified

- Both runs used a warm client cache and separate server/client processes.
- Shutdown was graceful and occurred after convergence. Interruption while mip work is
  active, restart recovery from persisted `ApplyToParent`, and integrated-singleplayer
  load remain open.
- No person watched either route during this session; the evidence is semantic and
  performance telemetry, not visual-quality review.
