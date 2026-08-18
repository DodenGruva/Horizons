# Active mip interruption and recovery

This directory preserves a deliberate crash/restart proof for the asynchronous mip
pipeline. The test used the Windows runner's guarded `-InterruptWhenPersistedMip` and
`-RequireMipRecovery` modes.

## Scenario

- Date: 2026-08-18
- Vintage Story: 1.22.7 Stable
- Mod: Vintage Horizons 0.2.1, client-only against an isolated unmodified server
- Hardware/settings: the same Ryzen 7 9800X3D, Radeon RX 9070 XT, 64 GB system and
  uncapped graphics settings recorded for the other 2026-08-17/18 Windows routes
- Starting cache: one 29,995,008-byte client database reporting 601 cached sections
- Interruption route: `uncached-frontier.txt`, warm cache, no warm-up
- Recovery route: `warm-cache-join.txt`, 15-second measurement and 45-second cooldown
- Postcheck route: a fresh server/client process, 5-second measurement and 30-second
  cooldown

The interruption hook is inert unless the runner supplies two private environment paths.
The storage worker writes its marker only after SQLite accepts a row with
`ApplyToParent=1`, then pauses that writer so a later clearing snapshot cannot overtake
the runner. The runner revalidates the client PID against the sandbox command line before
terminating that one process. It leaves the isolated server alive only for the explicit
`-ReuseServer` recovery run.

## Results

The first process wrote a durable level-0 obligation for section `8004,8000` at
`2026-08-18T14:22:29.1364146Z` and was interrupted before the clearing write could run.
The recovery process reported `1 persisted mip obligations` while opening the same cache.
Its final guarded sample had zero pending columns, capture jobs/results, mip jobs,
in-flight or dirty mip work, unsaved sections, asynchronous loads, storage backlog, and
worker/storage errors. The client loaded 601 sections from cache and the measured view
averaged 453.5 FPS with a 326.2 FPS 1% low; frame rate is incidental rather than the
acceptance criterion.

A third fresh server/client process reopened the resulting 29,999,104-byte cache and
reported `0 persisted mip obligations`. It again reached every guarded convergence field
at zero with no errors. This proves that the interrupted obligation survived on disk,
was consumed after restart, and that its cleared state was itself persisted for a later
process.

## Evidence limits

This is dedicated-server/client evidence for a client cache. It deliberately proves a
hard client interruption, not graceful shutdown. It does not yet exercise the same
interruption inside an integrated-singleplayer process, sibling-cache adoption, or human
visual review during recovery. The isolated server was shut down gracefully after each
completed recovery/postcheck run, and no sandbox pidfiles remained.
