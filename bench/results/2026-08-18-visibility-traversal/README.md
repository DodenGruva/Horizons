# Visibility-aware traversal benchmark — 2026-08-18

This directory preserves the dedicated-client/server runtime evidence for the Phase 7
visibility-aware quadtree traversal change. The test used Vintage Story 1.22.7, the
terrain-facing `bench/routes/moving-rotation.txt` route, and a warm client cache that
reported 601 Vintage Horizons sections. Both sides of the controlled comparison held
543 meshes resident and reported zero mesh evictions.

## Functional route

The production implementation first completed one warm-up lap and two measured laps:

```powershell
pwsh -NoProfile -File scripts/bench-windows.ps1 `
  -Label visibility-traversal-2026-08-18 `
  -Route bench/routes/moving-rotation.txt -Measure 30 -Laps 2 `
  -WarmupLaps 1 -ClientCache Warm -Cooldown 30 -RequireMipConvergence
```

All four waypoints completed without a settle timeout. Across the measured/cooldown
renderer samples, selection averaged 130.3 nodes and 32.8 whole subtrees were rejected
before descent. The traversal phase averaged about 54 microseconds, all 543 meshes stayed
resident, no mesh was evicted, and no Vintage Horizons game tick reached 25 ms. The final
capture, mip, render-dirty, save, asynchronous-load, and storage guards were all zero.

The four ignored route screenshots were inspected. They showed the same pre-existing
coverage-edge cliffs/fog as earlier route captures and no obvious new camera-edge hole.
Endpoint screenshots cannot establish how the route looked in motion, so a human
in-motion clipping review remains open.

## Controlled same-cache comparison

The comparison temporarily disabled only early subtree rejection and visible-child
filtering for the `off` run. The independent distance/age residency change remained in
place, isolating traversal visibility from mesh eviction. Production source was restored
before the `on` run and no temporary baseline code remains.

Both runs used the same archived prelaunch client cache, byte for byte: 30,015,488 bytes,
timestamp `2026-08-18T15:06:34.8794406Z`, with 601 sections reported from cache. Each run
used one warm-up lap, one measured lap, 30-second waypoint measurements, a 30-second
cooldown, stats enabled, and the same route and settings.

| Metric | Visibility off | Visibility on | Change |
|---|---:|---:|---:|
| Mean selected nodes per interval | 360.1 | 128.9 | -64.2% |
| Weighted mean traversal time | 66.0 us | 52.9 us | -19.8% |
| Weighted mean draw-submission time | 107.8 us | 97.8 us | -9.3% |
| Whole subtrees rejected per interval | 0.0 | 35.1 | structural proof |
| Resident meshes | 543 | 543 | unchanged |
| Mesh evictions | 0 | 0 | unchanged |
| Vintage Horizons ticks >=25 ms | 0 | 0 | unchanged |
| Mean of waypoint average FPS | 463.4 | 459.4 | -0.9% |
| Mean of waypoint 1% low FPS | 292.4 | 292.3 | effectively unchanged |

The controlled result supports the intended causal claim: when much of the cache was
outside the view, the renderer selected substantially fewer nodes and spent less average
CPU time walking and submitting them without a turn-around eviction storm. It does not
support an aggregate FPS improvement claim. This is one ordered pair at an uncapped
roughly 400–480 FPS, where microsecond phase savings are easily masked by run-to-run
noise; tail maxima also contain isolated outliers and are not used as improvement claims.

## Files and limitations

- `visibility-controlled-off-2026-08-18*` and
  `visibility-controlled-on-2026-08-18*` are the controlled frame summaries and semantic
  scenario proofs.
- `visibility-controlled-renderer-intervals.csv` contains 14 matched 15-second renderer
  telemetry samples from each controlled run.
- `visibility-traversal-2026-08-18*` preserves the longer production-only functional run.

The result covers one machine, dedicated client/server processes, and a 601-section cache,
not the eventual thousands-of-sections target. Allocation telemetry was enabled. A human
was unavailable to watch the controlled route in motion. Broader scaling, subjective
clipping/turn-around review, dirty scheduling, and time/byte-bounded uploads remain open.
