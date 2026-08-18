# Warm-cache join and completed sweep

These artifacts establish the two benchmark scenarios added in Session 13. They were
copied from the ignored `.testdata/bench` sandbox after graceful isolated shutdown.

## Shared context

- Date: 2026-08-18
- Working tree: based on `c78b431`, with cache-state/server-completion runner guards
- Vintage Story: 1.22.7 Stable
- Mod: Vintage Horizons 0.2.1
- Vanilla view distance: 256 blocks; frame limit/vsync: unlimited/off
- SSAA: 1; shadow quality: 1; god rays: off; bloom: on
- Vintage Horizons client configuration: defaults (`FarViewDistanceCap=0`,
  `DetailDistance=512`)
- CPU: AMD Ryzen 7 9800X3D, 8 cores / 16 logical processors
- GPU: AMD Radeon RX 9070 XT
- Memory: 64 GB nominal

## Warm-cache join

The client-only run used `bench/routes/warm-cache-join.txt`, no warm-up lap, and the
runner's `-ClientCache Warm` requirement. The pre-launch client database was 27,668,480
bytes and the active world reported 558 sections from cache. The first 15-second interval
had a game-tick maximum of 11.180 ms and zero ticks at or above 25 ms. Background-load
backlog reached 181 sections / 51.93 MiB / 11.531 s old, then reached zero by the 30-second
report. The settled 15-second sample averaged 438.0 FPS with a 270.1 FPS 1% low and no
settle timeout.

This is one warm join, not a cold/warm A/B or integrated-singleplayer result. The frame
CSV measures the settled view; join work is represented by the retained interval
telemetry summarized here.

## Completed sweep

The server-mod run used `bench/configs/completed-sweep.json`, which enables capture and a
24-chunk, 32-column/s sweep while disabling serving and transient generation. The
dependency-aware probe radius examined 3,249 positions. The sweep finished in about 68
seconds: 1,018 existing columns loaded, 377 frontier columns skipped, nothing generated,
and all 256 sampled absent positions remained absent.

Across the reported intervals, the server pipeline tick reached 17.874 ms maximum with
zero ticks at or above 25 ms. Sweep probe/load issue maxima were 3.945/6.004 ms. The
client's worst Vintage Horizons game tick was 10.072 ms, also with zero ticks at or above
25 ms. Its settled sample averaged 437.6 FPS with a 302.9 FPS 1% low and no timeout.

The server cache was already warm (14,729,216 bytes) because an intentionally rejected
radius-48 calibration run had partially populated it. This is dedicated server/client
evidence for completion and tick cadence, not an integrated-singleplayer run, cold-cache
comparison, default-radius acceptance, or human visual review.
