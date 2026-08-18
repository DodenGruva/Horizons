# Session 14 — completed generation and saturated assist

**Date:** `2026-08-18`
**Branch/commit:** `codex/main-thread-performance`, working tree based on `8e2cf1f`
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. This session changes benchmark orchestration and a
> server request-lifetime bug; it does not change wire, blob, or schema compatibility.

## Context and investigation

Session 13 left two dedicated-server observability scenarios open: completed transient
generation and saturated live assist. Existing runner controls could start generation or
require an arbitrary log string, but they could not prove that absent terrain was really
generated, that the active server cache was populated, or that saturated requests became
installed sections.

The first new assist run completed its frame CSV while transferring nothing. Its semantic
postcondition caught 16 requested / 0 received / 0 installed, and server telemetry showed
zero blob reads or sends. Source tracing narrowed this to the 50 ms server loop: a request
can follow the assist handshake before `PlayerByUid` exposes the client as `Playing`, and
the loop removed that bounded queue as if the player had disconnected.

## Work narrative

1. Added active warm/cold server-cache proof to the Windows runner, parallel to its client
   cache proof. Scenario JSON records both pre-launch database metadata and the active
   world's reported section counts.
2. Added a semantic generation parser requiring a terminal run, at least one transiently
   generated column, zero timeout/height-map failures, and a non-empty absence sample in
   which every checked position remains absent.
3. Added a cumulative client request-slot high-water mark and an assist scenario parser.
   Saturation requires the configured peak plus real received and installed sections.
4. Added pinned completed-generation and saturated-assist configs/routes with focused
   assertions. The full fast tier increased from 911 to 933 assertions.
5. Completed radius-8 generation at block 520000,520000: all 289 columns were generated,
   zero failed, and 256/256 sampled absent positions remained absent. Server pipeline and
   generation-work maxima were 6.551/5.706 ms with no reported 25 ms hitch.
6. The first assist run proved a 514-section active server cache and a cold active client
   cache, filled all 16 request slots, then failed the new receive/install guard.
7. Changed the serve loop to retain requests through transient join state. Real
   disconnect cleanup remains in `OnPlayerDisconnect`, so the queue stays bounded and has
   one authoritative lifetime end.
8. The unchanged rerun requested, received, and installed 395 sections with zero declines.
   Client publication backlog peaked at 2 items / 0.62 MiB / 62 ms and drained by 30
   seconds; client game-tick maximum was 7.789 ms with no 25 ms hitch.
9. The saturated run measured the next server tail: synchronous blob reads reached
   3.75/17.5/68.755 ms p95/p99/max and assist service reached 68.879 ms maximum. The
   elapsed budget cannot preempt one admitted SQLite call.

---

## Delivered

- Semantic generation-completion, server-cache, and assist-transfer guards in the Windows
  isolated runner.
- Reproducible pinned configs and fixed routes for both scenarios.
- A cumulative request-slot saturation counter with fast checks.
- A fix for early-join assist requests being removed without a reply.
- Generation plus assist before/after CSVs and scenario proofs under
  `bench/results/2026-08-18-generation-assist`.
- Clean Debug build, 933 passing fast assertions, and documentation checks.

## Decisions

- A frame CSV is never sufficient evidence that the named server operation happened.
  Generation and assist now have structured semantic postconditions.
- A transient non-`Playing` lookup does not authorize queue removal. The server disconnect
  event owns actual departure cleanup.
- The next server implementation slice is a dedicated read-only blob connection with
  explicit ownership and preserved send order; the 68.755 ms owning-thread read is now
  measured rather than hypothetical.

## Traps

- A handshake can complete before the joining player appears as `Playing` to the next
  server tick. Treating that transition as a disconnect silently strands client slots.
- Raising the assist rate to 64/s is useful saturation pressure but not a default-rate FPS
  sample. Separate the single-read latency evidence from aggregate stress throughput.

## Flagged and unverified

- No person watched either route in motion.
- Generation has one radius-8 dedicated-server sample, not a default-radius, long, or
  integrated-singleplayer soak.
- Assist has one saturated 64/s sample. Default 8/s serving, multiple players, connection
  interruption, and long soak remain unmeasured.
- The assist run measured foreign publication under concurrent cold-client capture/save
  load; it did not isolate storage-worker decode cost by itself.
