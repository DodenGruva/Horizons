# Managed-allocation telemetry overhead

Six alternating warm-stationary runs compare the benchmark's normal stats mode with
`-DisableStats`. The first stats-on run was the first game process after the rebuild and
was a clear process/OS warm-up outlier, so it is preserved but excluded from the warmed
pair summary rather than silently blended into it.

## Scenario

- Date: 2026-08-17
- Working tree: based on `2c752d0`, with server phase telemetry and a real stats-off
  runner control
- Vintage Story: 1.22.7 Stable
- Mod: Vintage Horizons 0.2.1, client-only against the isolated unmodified server
- Route: `bench/routes/steady-stationary.txt`
- Cache: reused sandbox cache at the fixed `spawn-horizon` view
- Command shape: `pwsh -NoProfile -File scripts/bench-windows.ps1 -Route
  bench/routes/steady-stationary.txt -Settle 20 -SettleMax 60 -Measure 45
  -WarmupLaps 0`, with `-DisableStats` on off samples
- Vanilla view distance: 256 blocks; frame limit/vsync: unlimited/off
- SSAA: 1; shadow quality: 1; god rays: off; bloom: on
- Vintage Horizons configuration: defaults (`FarViewDistanceCap=0`,
  `DetailDistance=512`)
- CPU: AMD Ryzen 7 9800X3D, 8 cores / 16 logical processors
- GPU: AMD Radeon RX 9070 XT, driver 32.0.31035.1003
- Memory: 64 GB nominal (66,095,271,936 bytes reported)

## Results

| Pair | Stats | Avg FPS | Median FPS | 1% low FPS | Settled |
|---|---|---:|---:|---:|---:|
| first/outlier | on | 298.3 | 298.3 | 262.1 | 33 s |
| first/outlier | off | 465.5 | 464.3 | 343.6 | 21 s |
| warmed 1 | on | 439.8 | 440.1 | 359.4 | 36 s |
| warmed 1 | off | 443.0 | 445.0 | 287.5 | 21 s |
| warmed 2 | on | 446.1 | 450.4 | 275.0 | 21 s |
| warmed 2 | off | 449.2 | 454.2 | 329.6 | 21 s |

Across the two warmed pairs, stats-on averaged 442.95 FPS versus 446.10 off: a 0.7%
average-FPS reduction. Median FPS averaged 445.25 versus 449.60, a 1.0% reduction. The
1% lows moved in opposite directions between pairs and do not establish a tail-latency
effect. This is evidence that allocation telemetry has a small measurable steady-state
throughput cost on this uncapped high-FPS scenario, not evidence for a player-visible
change at ordinary capped frame rates.

The first pair demonstrates why the process/order warm-up cannot be ignored: its apparent
36% gap did not reproduce. All six runs completed with zero settle timeouts and graceful
isolated client/server shutdown.

## Integrated telemetry smoke

One additional short run installed Vintage Horizons on the isolated server and exercised
the default sweep. The server emitted 15-second pipeline, sweep probe issue/publication,
assist service/offer, hitch, and per-phase allocation lines. It observed a 26.722 ms
maximum server pipeline tick while initial capture publication was active, then settled
to 12 us maximum in a later idle interval. Sweep probe issue/publication telemetry was
live; no section requests arrived, so blob-read and section-send costs remained zero.
This is integration evidence for telemetry emission and graceful shutdown, not a sweep or
assist performance acceptance run.
