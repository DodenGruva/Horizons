# Readiness-driven near handoff (Phase 2a) — 2026-08-18

Runtime evidence for Phase 2a of `dev/plans/PLAN_CHUNK_AWARE_VANILLA_HANDOFF.md`, the first
phase in which measured vanilla readiness owns pixels. The fragment shader's existing
`cacheHandoffDistance` uniform is now fed by the tracker instead of a constant fraction of
view distance. No new GPU resource, no shader edit, no mask.

Same conditions as the Phase 1 runs: Vintage Story 1.22.7, dedicated sandbox server, warm
client cache reporting 360 sections, 256-block vanilla view distance, stats telemetry on.

## The measurement that justified building it

Before wiring anything to a pixel, a shadow-only diagnostic reported the distance from the
camera to the nearest column that is not wholly owned. A single global radius is only worth
building if incomplete columns sit at the frontier rather than near the player.

`radius-probe-stationary-2026-08-18` answered it, holding steady across all seven intervals:

```
nearest incomplete 234 blocks/unready 234 blocks, radial handoff 64 blocks
columns 441 tracked/201 full/0 partial
```

234 blocks of provably owned ground against a 64-block constant, and **zero partial
columns** — incompleteness begins cleanly at the streaming frontier. That is exactly the
shape a radial handoff needs.

## What the handoff now does

Radius = distance to the nearest not-wholly-owned column, minus one vanilla chunk of
margin, clamped to the vanilla view distance, quantized down to a whole chunk. A column
counts only when every vertical chunk in it is committed ready, and the active window edge
bounds the result so an untracked frontier cannot read as owned ground. Shrinkage applies
in the same frame; growth waits 500 ms and then applies the *smallest* radius seen during
the hold. The tracker being disabled, absent, or the player leaving the default dimension
restores the established constant immediately.

## Results

| Metric | Phase 1 baseline (radial 64) | Phase 2a (readiness) |
|---|---|---|
| Applied handoff, stationary | 64 blocks | 192 blocks |
| Applied handoff, moving | 64 blocks | 160–192 blocks |
| Nearest owned bound, moving | not measured | 217–252 blocks |
| Radial fallback samples | n/a | 0 |
| Committed ready cells / complete columns | 1,856 / 232 | 1,856 / 232 |
| Probe errors / dropped events | 0 / 0 | 0 / 0 |
| Confirmation-only intervals | 0 | 0 |
| Readiness cost avg / p95 / p99 | 18.4 / 50 / 50 µs | 20.0 / 50 / 100 µs |
| Renderer phase hitches ≥25 ms | 0 | 0 |
| Waypoint FPS average | 482.8 / 480.0 / 447.3 / 505.7 | 489.1 / 467.3 / 447.9 / 498.8 |

At a 256-block view distance, cached terrain previously overlapped vanilla across a
192-block ring. It now overlaps across roughly 22–96 blocks depending on how far ownership
extends, and every block inside the suppressed radius is provably owned by vanilla.

The per-frame cost of re-measuring ownership was 8.4 µs when it rescanned every column
every frame. Re-measuring only when committed ownership changes or the camera moves four
blocks brought it to 1.6 µs over the Phase 1 baseline.

## What this does not establish

- **No person has seen it.** Every visual criterion in the plan's player acceptance section
  is open: transition popping, cliff and water seams, flicker while hovering at a boundary,
  and whether the remaining overlap ring is still objectionable. A playtest build is at
  `dist/vintagehorizons_0.2.1-playtest-readiness-handoff.zip`.
- **Frame rate is neutral within noise, not proven neutral.** Per-waypoint averages moved
  +1.3%, −2.6%, +0.1% and −1.4% against the baseline run, with intra-run lap spread up to
  1.9%. One run per side is not a controlled comparison.
- **No CPU or GPU saving is claimed.** Cached fragments inside the radius are still
  submitted, vertex-processed and rasterized before being discarded. Draw-call and vertex
  savings need the per-cell mask and whole-mesh skip of Phases 2 and 3.
- **The join window is unmeasured.** Before the tracker converges the derived radius starts
  small, which shows more cached terrain near the camera than the old constant did for the
  first seconds after joining. Fail-toward-coverage makes that the safe direction, but how
  long it lasts and whether it is visible has not been measured.
- **The single-pocket weakness is untested in real play.** One unowned column near the
  camera pulls the global radius in and restores overlap everywhere. Neither route
  reproduced it deliberately.
- A multi-millisecond readiness outlier (3,547 µs) recurred, consistent with a blocking
  `IsChunkRendered` call the 0.25 ms deadline cannot preempt. Still inferred, not proven.

## Files

- `radius-probe-stationary-2026-08-18-scenario.json` — the shadow-only measurement that
  justified the phase.
- `handoff-stationary-2026-08-18-scenario.json` / `.csv` — stationary verification.
- `handoff-moving-2026-08-18-scenario.json` / `.csv` / `-samples.txt` — moving route against
  the Phase 1 baseline in `../2026-08-18-readiness-shadow/`.
