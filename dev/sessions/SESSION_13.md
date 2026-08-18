# Session 13 — warm join and completed sweep scenarios

**Date:** `2026-08-18`
**Branch/commit:** `codex/main-thread-performance`, working tree based on `c78b431`
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. This session changes benchmark orchestration and
> evidence only; it does not change runtime, wire, blob, or schema compatibility.

## Context and investigation

The first two remaining observability scenarios were a proven warm-cache join and a sweep
that actually reached completion. Existing route labels did not establish either: a
supposed warm launch could select an empty or different world database, and the earlier
server smoke ended while the sweep was still probing.

Source inspection identified two exact proof points already emitted by the mod: the
client's level-finalization line reports the active world's cached-section count, and the
sweep's terminal line reports loaded/frontier/generated/absence-verification outcomes.
The runner could therefore validate the scenarios without adding runtime hooks.

## Work narrative

1. Added non-destructive `Any`/`Warm`/`Cold` client-cache requirements to the Windows
   runner. It snapshots pre-launch database metadata, parses the active-world cache count,
   and writes a portable scenario JSON beside the frame CSV.
2. Added pinned server-config installation for fresh isolated servers and a required
   server-log terminal string. A pinned config is rejected with `-ReuseServer`; a missing
   terminal line fails the run while still preserving the scenario record and shutting
   both processes down gracefully.
3. Added fixed-view `warm-cache-join` and `completed-sweep` routes plus a sweep config that
   disables serving and transient generation. Focused checks cover route identity and the
   isolation settings.
4. The warm join proved a 27,668,480-byte database and 558 active cached sections. Its
   first interval reached 11.180 ms maximum game-tick time with no 25 ms hitch; background
   backlog of 181 sections / 51.93 MiB / 11.531 s old reached zero by 30 seconds. The
   settled sample was 438.0 FPS average / 270.1 FPS 1% low.
5. A first 48-chunk sweep correctly failed its completion guard. The four-chunk safety
   neighbourhood expanded it to 11,025 probes; after about 120 seconds it had finished
   probing but reached only 10% of loading. The benchmark radius was calibrated to 24
   chunks rather than turning a completion scenario into a five-minute soak.
6. The final sweep examined 3,249 positions and finished in about 68 seconds: 1,018
   existing columns loaded, 377 frontier columns skipped, nothing generated, and 256/256
   sampled absent positions still absent. Server pipeline ticks peaked at 17.874 ms with
   zero 25 ms hitches; probe/load issue maxima were 3.945/6.004 ms. The client sample was
   437.6 FPS average / 302.9 FPS 1% low with no timeout.

---

## Delivered

- Cache-state and server-completion proof in the Windows isolated runner.
- Reproducible warm-join and completed-sweep route/config artifacts.
- Preserved CSVs, scenario records, context, and evidence limits under `bench/results`.
- Clean builds, 911 passing fast assertions, and documentation checks.

## Decisions

- Scenario labels are not evidence. Warm/cold cache state and server completion are
  explicit runner postconditions with persisted provenance.
- The completed-sweep scenario uses radius 24 at 32 loaded columns/s. It is large enough
  to exercise probe, load, capture, mip, and save phases while completing inside one
  ordinary benchmark run.
- The sweep remains classified as dedicated server/client evidence. It does not close the
  integrated-singleplayer or default-radius verification debt.

## Traps

- A configured sweep radius excludes the four-chunk safety neighbourhood. Probe work is
  `(2 * (radius + 4) + 1)^2`, not `(2 * radius + 1)^2`.
- A fixed-duration route can produce valid frame numbers while the named server operation
  is incomplete. Require the operation's terminal state rather than trusting the label.

## Flagged and unverified

- The warm join is one sample, not a cold/warm A/B, long join soak, or integrated
  singleplayer result.
- The successful sweep began with a 14,729,216-byte server cache after the rejected
  calibration run; it is warm-cache cadence evidence, not cold-cache throughput evidence.
- No person visually reviewed either stationary run.
- Completed transient generation and saturated server-assist transfer remain open.
