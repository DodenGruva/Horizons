# Corrected continuous movement/rotation route

This directory preserves the first full run of the corrected terrain-facing trajectory
route and a same-route follow-up after capture publication was time/byte bounded. The
baseline is evidence for `codex/main-thread-performance` at commit
`8dfdac973171fd8850b87aec90d0406985ab800f`; the follow-up used the working-tree capture
budget change built on that commit.

## Scenario

- Date: 2026-08-17
- Vintage Story: 1.22.7 Stable
- Mod: Vintage Horizons 0.2.1, client-only against the isolated unmodified server
- Route: `bench/routes/moving-rotation.txt`
- Command: `pwsh -NoProfile -File scripts/bench-windows.ps1 -Label moving-rotation-2026-08-17-long-1 -Route bench/routes/moving-rotation.txt -Measure 30 -Laps 2 -WarmupLaps 1`
- Route speed: 400 blocks per 30-second leg (13.3 blocks/s)
- Camera: one complete horizontal turn per leg, conventional -12 degree pitch mapped to
  Vintage Story's PI-centred pitch
- Warm-up: one four-leg lap; measurement: two four-leg laps
- Vanilla view distance: 256 blocks
- Frame limit/vsync: unlimited/off
- SSAA: 1; shadow quality: 1; god rays: off; bloom: on
- Vintage Horizons configuration: defaults (`FarViewDistanceCap=0`,
  `DetailDistance=512`); allocation telemetry enabled by the benchmark runner
- CPU: AMD Ryzen 7 9800X3D, 8 cores / 16 logical processors
- GPU: AMD Radeon RX 9070 XT, driver 32.0.31035.1003
- Memory: 64 GB nominal (66,095,271,936 bytes reported)

## Baseline result

All twelve legs, including warm-up, passed the settle gate after 21 seconds. The two
measured laps had zero settle timeouts. Per-leg spread between the measured laps was
0.1-0.3% for average FPS and 0.8-4.5% for the 1% low.

Across the 28 telemetry intervals beginning with measured lap 1:

- total game tick worst interval p95/p99/max: 1.75/4.00/12.062 ms;
- capture publication worst interval p95/p99/max: 1.50/3.75/12.038 ms;
- mip publication worst interval p95/p99/max: 0.025/0.350/0.599 ms;
- zero game ticks and zero individual render phases reached 25 ms;
- five projection resets occurred over the measured route;
- 527.85 MiB of mesh data was uploaded;
- the final report had zero capture/mesh/mip errors, zero pending captures, zero mip
  backlog or in-flight jobs, zero unsaved sections, and zero storage errors/backlog.

Capture publication therefore reproduced the earlier 11-14 ms maximum on a long,
continuous route and accounted for essentially all of the worst measured game tick. It
did not create a 25 ms hitch in this run, but it meets the plan's threshold for adding an
elapsed-time/byte budget at result boundaries.

The four endpoint screenshots in the ignored `.testdata/bench` sandbox were inspected.
They face rendered terrain rather than sky and show no obvious near-camera projection
cutoff. They also show fog/white void and finite captured-terrain edges in places, which a
static endpoint image cannot distinguish from missing cache coverage. A human-watched run
is still required to judge transient clipping and turn-around stalls during rotation.

## Capture-budget follow-up

The follow-up used the same command except for label
`moving-rotation-capture-budget-2026-08-17-long-1`. Capture publication admitted work at
result boundaries under the shared 2 ms / 512 KiB drain policy, retained the existing
eight-result item ceiling, and counted queued/in-progress jobs plus completed/deferred
results as capture backpressure.

| Waypoint | Baseline average | Budget average | Baseline 1% low | Budget 1% low |
|---|---:|---:|---:|---:|
| east-full-turn | 475.2 | 475.2 | 281.3 | 299.9 |
| south-full-turn | 456.9 | 456.0 | 270.5 | 282.3 |
| west-full-turn | 434.1 | 433.4 | 280.9 | 283.0 |
| north-full-turn | 489.8 | 489.0 | 301.9 | 313.8 |

Across its 28 measured telemetry intervals, the follow-up reported:

- total game tick worst interval p95/p99/max: 1.50/2.50/5.749 ms;
- capture publication worst interval p95/p99/max: 1.50/2.25/5.732 ms;
- mip publication worst interval p95/p99/max: 0.025/0.275/0.490 ms;
- zero game ticks at or above 25 ms;
- a maximum capture-publication backlog of 9 results / 0.70 MiB / 93 ms oldest;
- five projection resets;
- zero capture/mesh/mip errors, zero mip backlog/in-flight work, and zero unsaved
  sections or storage errors/backlog at the final report.

The result-boundary policy cannot preempt one admitted publication, so the 5.732 ms
maximum is expected and identifies the remaining single-result tail. The baseline's
12.038 ms capture maximum fell by 52.4%, while average FPS remained within 0.2% at every
waypoint and each 1% low improved by 0.7-6.6%. The sandbox cache and captured terrain
continued evolving between runs, so those aggregate FPS changes are supporting evidence,
not a clean causal estimate. The source-level multi-result bound, low measured queue age,
and phase maximum are the stronger acceptance evidence.

## Limitations

This is one machine, one generated sandbox save, and one client-only route. It does not
exercise sibling-cache adoption, server assist, savegame sweep, or an integrated server
installation. Allocation telemetry was enabled, and its overhead has not yet been
measured against a disabled stationary baseline.
