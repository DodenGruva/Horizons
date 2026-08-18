# Mip convergence soak and restart

This directory preserves a longer warm-cache movement run and its immediate fresh-process
restart. Both used the Windows runner's new semantic `-RequireMipConvergence` guard.

## Scenario

- Date: 2026-08-18
- Vintage Story: 1.22.7 Stable
- Mod: Vintage Horizons 0.2.1, client-only against an isolated unmodified server
- Hardware/settings: the same Ryzen 7 9800X3D, Radeon RX 9070 XT, 64 GB system and
  uncapped graphics settings recorded for the other 2026-08-17/18 Windows routes
- Long run: `uncached-frontier.txt`, 1,600 blocks over 120 seconds, no warm-up, 45-second
  cooldown, warm active client cache required
- Restart: a new server and client process, `warm-cache-join.txt`, 15-second measurement,
  no warm-up, 30-second cooldown, warm active client cache required
- Both runs enabled stats and required zero final capture input/results, worker errors,
  mip queued/in-flight/dirty work, unsaved sections, asynchronous loads, and storage
  backlog/errors.

The first orchestration call was interrupted after starting the pidfile-verified sandbox
server but before launching a client. The recorded long run used the runner's explicit
`-ReuseServer` recovery path; no prior client joined that server process. The restart run
started fresh server and client processes normally.

## Results

The long run loaded 405 sections from the active 20,672,512-byte client cache, captured
2,401 columns, averaged 501.2 FPS with a 231.1 FPS 1% low, and reported zero Vintage
Horizons ticks at or above 25/50/100 ms. Its final semantic record had zero pending
capture, mip, save, load, and storage work and zero worker/storage errors.

The resulting cache grew to 29,982,720 bytes. The fresh-process restart reported 601
sections from that cache, averaged 445.6 FPS with a 377.9 FPS 1% low, and reached the same
zero pipeline/storage state. Nine render-dirty sections remained in its final view; this
is deliberately not part of mip/persistence convergence because renderer demand can stay
dirty independently of durable propagation.

These runs establish sustained warm-cache propagation, graceful shutdown, persisted
restart, and semantic convergence in separate dedicated-server/client processes. They do
not establish interruption while mip work is active, integrated-singleplayer behavior,
cold-cache causality, or human visual quality. Full logs and screenshots remain ignored.
