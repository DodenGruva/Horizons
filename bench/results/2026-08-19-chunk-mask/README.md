# Per-cell ownership mask and whole-mesh skip (Phases 2 and 3) — 2026-08-19

Runtime evidence for Phases 2 and 3 of `dev/plans/PLAN_CHUNK_AWARE_VANILLA_HANDOFF.md`,
on branch `codex/chunk-aware-mask`. The mask is off unless `VINTAGEHORIZONS_CHUNK_MASK=1`
is set, which `scripts/bench-windows.ps1 -ChunkMask` does.

Vintage Story 1.22.7, dedicated sandbox server, warm client cache reporting 360 sections,
256-block vanilla view distance, `bench/routes/steady-stationary.txt` at
`512020,190,512010`, 75-second settle, 60-second measurement, stats telemetry on.

## Why the settle is 75 seconds

The first mask run used a 30-second settle and reported 536.7 FPS against 438.6 for the
radius — a 22% "win" that was entirely an artifact. Its stats showed `0 meshes, 0 selected`
for the first three intervals: the client was still loading, so the measurement window
partly covered a period with no cached terrain drawn at all. Every run below settles long
enough to reach full mesh residency first, and mesh counts are reported so the comparison
can be checked rather than trusted.

## Controlled comparison

| Metric | Baseline (measured radius) | Mask only | Mask + whole-mesh skip |
|---|---|---|---|
| FPS average | 436.2 | 433.2 | **467.9** |
| FPS median | 434.1 | 430.9 | **464.4** |
| FPS 1% low | 297.0 | 300.7 | **315.5** |
| Frame ms average | 2.29 | 2.31 | 2.14 |
| Sections resident | 258 | 263 | 255 |
| Meshes / evicted | 235 / 0 | 247 / 0 | 231 / 0 |
| Selected nodes | 149 | 149 | 149 |
| Owned draws skipped per frame | n/a | 0 | about 42 of 149 |
| Mask owned cells / bytes / upload | n/a | 1,608 / 32 KiB / 3 µs | 1,608 / 32 KiB / 3 µs |

The mask alone is performance-neutral, which is what the design predicts: it replaces one
fragment discard with another, and the fragments are rasterized either way. The gain comes
from Phase 3 refusing to submit a section whose every ownership cell is committed vanilla
ready — roughly 28% of submissions here — which removes the uniform uploads, the transform
and the draw call itself.

Mask ownership matched the tracker's committed cell count exactly (1,608) in every sample,
uploads cost 3 µs, and a settled view uploads nothing at all because the buffer stops
changing.

## Turning it on in game

`.vhmask [on|off]` toggles per-cell ownership at runtime, so the build can be evaluated
without an environment variable or a rebuild. A run that launched with the mask off and
issued `.vhmask on` mid-session reported "chunk mask on. The change applies on the next
frame", then owned 1,608 cells and skipped about 297,500 draws per interval, confirming the
mask is built from state the tracker already holds rather than waiting for reconvergence.

That run also separated the two upload costs: creating the texture takes about 325
microseconds once, because that branch calls glTexImage2D and builds mipmaps, while every
later change is a 3-microsecond sub-image update. A settled view uploads nothing.

## What this does not establish

- **No person has seen the mask.** An addressing error would hide the wrong ground and
  would also raise frame rate, so the visual check is the load-bearing evidence and it does
  not exist yet. Nothing here should be read as visual acceptance.
- One run per configuration, one stationary scenario, one machine. No alternating repeats.
- The moving frontier, teleport, and view-distance change are unmeasured with the mask on.
  Those are where mask rebuild cost and ownership churn would show up.
- Water was not separately verified: opaque and water share one lookup by construction and
  by static check, but no run isolated a water boundary.
- GPU time is not measured. The frame-rate gain is consistent with removed CPU submission,
  but the split between CPU and GPU is unestablished.
- The 1% low improved by 6.2%, which is the number most likely to move with unrelated
  system noise on a single run.

## Files

- `maskoff-settled-2026-08-19-*` — baseline with the measured radius owning pixels.
- `maskon-settled-2026-08-19-*` — mask active, no whole-mesh skip.
- `maskskip-settled-2026-08-19-*` — mask active with the whole-mesh skip, plus its
  readiness telemetry samples.
- `mask-stationary-2026-08-19-*` — the discarded 30-second-settle run, kept because its
  `0 meshes` state is the evidence for why the settle was lengthened.
