# Session 11 — uncached capture-frontier evidence

**Date:** `2026-08-17`
**Branch/commit:** `codex/main-thread-performance` on `8874d4600fea161a6b6c8d9f54c5052ac715db59` plus benchmark and documentation changes
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

Session 10 made a genuinely uncached movement benchmark the next performance task. The
isolated sandbox contained one 24.5 MB VH database. It was moved to a recoverable ignored
archive, leaving zero active `.db` files; the game independently reported `0 sections from
cache` after each clean launch.

Two preliminary zero-warm-up runs used the existing 400-block-square movement route. The
human observer pointed out that later legs remained near terrain traversed by earlier legs.
That mattered: with a 256-block streaming radius, the square mixes continued capture-frontier
work with coverage produced earlier in the same lap even though the cache was empty before
launch. Those runs were excluded from the primary uncached evidence.

## Work narrative

A dedicated one-way route now moves 1,600 blocks in 120 seconds while preserving the old
route's 13.3 blocks/s speed, 12 degrees/s camera rotation, and corrected -12 degree pitch.
After the initial streaming footprint, roughly 1,344 blocks continue outward without a
lap or leg returning toward earlier coverage.

The first one-way run showed that the benchmark completed immediately at the endpoint,
before its final telemetry interval could establish queue convergence. The harness gained
an opt-in cooldown phase that pins the final endpoint after the CSV and screenshot are
recorded, keeps normal game ticks running, and delays the existing graceful `/stop` and
done marker. The default is zero, so existing benchmark behavior is unchanged. A second
clean-cache one-way run used a 45-second cooldown.

Both one-way runs had zero VH game ticks at or above 25 ms. Their worst VH ticks were
15.790 and 10.950 ms; capture publication reached 9.028 and 5.469 ms, and mip publication
reached 15.748 and 8.474 ms. Capture backlog peaked at 20 results / 1.61 MiB / 234 ms and
11 results / 0.89 MiB / 62 ms. During the second run's cooldown, all capture, mip, render,
save, and storage work reached zero with no errors before later stationary chunk arrivals
briefly queued one small capture result.

The first run generated server-save terrain beyond the old benchmark square; the second
reused it. Both client caches were empty, but their aggregate FPS and upload totals are
not a controlled A/B comparison. The endpoint screenshots face terrain and show the finite
new coverage edge without an obvious near-camera projection cutoff.

---

## Delivered

- Added the one-way `bench/routes/uncached-frontier.txt` scenario.
- Added an opt-in `VHBENCH_COOLDOWN` / Windows `-Cooldown` endpoint-hold phase without
  changing existing benchmark defaults.
- Preserved two clean-client-cache CSVs plus hardware, settings, cache proof, telemetry,
  scenario correction, and limitations under `bench/results/2026-08-17-uncached-frontier`.
- Restored the original sandbox cache byte-for-byte after archiving all generated benchmark
  databases; no private sandbox cache was deleted.
- Built the mod and benchmark harness with zero warnings/errors, passed all 900 fast
  assertions, parsed the PowerShell runner successfully, and passed 194 documentation
  checks before final documentation reconciliation.

## Decisions

- Use one long outward trajectory for cold capture-frontier evidence instead of treating
  a compact loop as uniformly uncached.
- Keep the first approximate streaming radius in the scenario and name it explicitly;
  the benchmark represents realistic exploration, not an impossible state where terrain
  ahead of the player remains uncaptured until the player occupies it.
- Keep cooldown opt-in and outside recorded frame samples, so it can establish convergence
  without improving or hiding measurement-time performance.
- Treat phase timings and bounded queue age as stronger evidence than aggregate FPS because
  server terrain state changed between the two independent client-cache resets.

## Traps

- An empty cache before launch does not make every later leg of a compact route cold. The
  route populates its own future footprints; account for streaming radius and prior legs.
- Immediate benchmark completion can hide whether a cold workload converges. Hold the
  endpoint after measurement when final queue state is part of the acceptance evidence.

## Flagged and unverified

- The new route is a client-only scenario on one machine and one server save. Warm join,
  sweep, assist, integrated-server load, and interrupted restart remain open.
- The endpoint images were inspected, but no person watched the new route in motion; the
  earlier positive human review applies only to the warm-cache loop.
- Allocation telemetry remained enabled. Its disabled-versus-enabled overhead still needs
  a stationary comparison.
- One admitted capture or mip result remains non-preemptible; the clean runs bounded the
  observed tails but do not prove a universal maximum.
