# Uncached capture-frontier route

This directory preserves two clean-client-cache runs of the corrected terrain-facing
one-way capture-frontier route. Both started from commit
`8874d4600fea161a6b6c8d9f54c5052ac715db59`; the route and optional endpoint cooldown
were working-tree benchmark-harness additions made while establishing this evidence.

## Scenario

- Date: 2026-08-17
- Vintage Story: 1.22.7 Stable
- Mod: Vintage Horizons 0.2.1, client-only against the isolated unmodified server
- Route: `bench/routes/uncached-frontier.txt`
- Trajectory: 1,600 blocks east over 120 seconds (13.3 blocks/s), with four complete
  horizontal camera turns and a conventional -12 degree terrain-facing pitch
- Warm-up: none; measurement: one continuous trajectory
- Cache proof: before each launch the only sandbox VH database was moved to a recoverable
  ignored archive, the active database directory contained zero `.db` files, and the game
  then reported `0 sections from cache`
- Command, first run: `pwsh -NoProfile -File scripts/bench-windows.ps1 -Label
  uncached-frontier-2026-08-17-cold-1 -Route bench/routes/uncached-frontier.txt -Measure
  120 -Laps 1 -WarmupLaps 0`
- Command, second run: the same command with label `uncached-frontier-2026-08-17-cold-2`
  and `-Cooldown 45`
- Vanilla view distance: 256 blocks
- Frame limit/vsync: unlimited/off
- SSAA: 1; shadow quality: 1; god rays: off; bloom: on
- Vintage Horizons configuration: defaults (`FarViewDistanceCap=0`,
  `DetailDistance=512`); allocation telemetry enabled by the benchmark runner
- CPU: AMD Ryzen 7 9800X3D, 8 cores / 16 logical processors
- GPU: AMD Radeon RX 9070 XT, driver 32.0.31035.1003
- Memory: 64 GB nominal (66,095,271,936 bytes reported)

The starting settle can populate roughly the first vanilla streaming radius. The
remaining approximately 1,344 blocks continue outward instead of looping through a
previous leg's footprint. The first run also generated server terrain beyond the old
benchmark square; the second reused that generated save terrain. Both client VH caches
were empty, but aggregate FPS and upload-volume differences are therefore not controlled
A/B evidence.

## Results

| Run | Average FPS | 1% low | Worst VH game tick | Capture apply max | Mip apply max | Save snapshot max |
|---|---:|---:|---:|---:|---:|---:|
| cold-1 | 555.8 | 190.5 | 15.790 ms | 9.028 ms | 15.748 ms | 7.775 ms |
| cold-2 | 543.2 | 232.2 | 10.950 ms | 5.469 ms | 8.474 ms | 8.059 ms |

Both runs had zero Vintage Horizons game ticks at or above 25, 50, or 100 ms, zero
capture/mesh/mip errors, and no render-phase hitch at those thresholds. Capture backlog
peaked at 20 results / 1.61 MiB / 234 ms in cold-1 and 11 results / 0.89 MiB / 62 ms in
cold-2, below the combined 24-item admission cap in both cases.

Cold-2 held the endpoint for 45 seconds after recording its CSV and screenshot. Four
seconds into that cooldown, telemetry reported zero pending captures, zero capture result
backlog, zero mip queue/in-flight work, zero render-dirty and unsaved sections, and zero
storage backlog/errors. Later stationary chunk arrivals briefly produced one queued
0.11 MiB capture result, so the final sampled queue was not monotonically empty, but the
run established bounded progress and convergence before graceful shutdown.

The two endpoint screenshots were visually inspected in the ignored sandbox. They face
terrain rather than sky and show the finite newly captured coverage edge. Neither shows
an obvious near-camera projection cutoff. Large screenshots and full client logs remain
ignored; the two CSV summaries are preserved here.

## Scenario correction and limitations

Two preliminary empty-cache runs used `moving-rotation.txt` with zero warm-up. A human
observer correctly noted that its 400-block square remains within the general vicinity
of earlier legs. With a 256-block streaming radius, later legs mix new capture-frontier
work with coverage produced earlier in the same lap. Those runs helped expose the scenario
problem and are intentionally excluded from the primary evidence above.

This remains one machine, one server save, and one client-only scenario. It does not
exercise sibling-cache adoption, server assist, savegame sweep, integrated-server load,
or interrupted restart. Allocation telemetry was enabled, and its overhead is still not
measured against a disabled stationary baseline.
