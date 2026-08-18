# Session 23 — server-assist tail attribution and progress-log fix

**Date:** `2026-08-18`
**Branch/commit:** `codex/main-thread-performance` (session-close commit)
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

Session 15 moved server-assist SQLite reads off the owning thread, but its saturated
64-sections/s run retained one unexplained 32.450 ms assist-service outlier while the
reader and individual-send maxima stayed below 0.25 ms. The raw server log for that old
sample was not preserved, so this session treated packet publication, managed GC,
process scheduling, and other work inside `ServePending` as live alternatives rather
than assigning the old sample after the fact.

The user first requested a Release playtest package from the clean session-22 branch.
That exact build passed 1,056 assertions and was packaged separately before the
investigation changed source.

## Work narrative

1. Repeated the guarded cold-client/warm-server saturated-assist scenario at the pinned
   64/s rate. It transferred 277 sections and reproduced a 12.779 ms assist-service
   maximum with a 12.075 ms send maximum.
2. Split the owning-thread callback into setup, completed-result publication, and request
   admission timings. Added per-callback send time/bytes and managed-allocation totals,
   classified callbacks and sends by generation collection-count changes, and emitted a
   correlated line when the 2 ms serve budget was crossed. This attribution runs only
   when `VINTAGEHORIZONS_STATS=1`; ordinary servers retain the existing fast timing path.
3. Two attributed repeats transferred and installed 273 sections each, saturated all 16
   client slots, and declined nothing. Across 273 sends in each run, no managed collection
   crossed a send or serve callback. The clearest tail was 3.655 ms: 3.573 ms fell in the
   admission phase while two sends totalled 0.075 ms, and it coincided exactly with the
   synchronous `Assist served 201 sections` notification.
4. Removed the every-200-sections progress notification from the 50 ms owning-thread
   serve path. Cumulative served sections and bytes remain in `/vhserver` and were added
   to the opt-in 15-second stats line.
5. Repeated the same guarded scenario after the fix. It transferred and installed 273
   sections with all 16 slots exercised and zero declines. The active-transfer serve
   maximum was 2.061 ms, individual sends stayed below 0.647 ms, no managed collection
   crossed any measured callback or send, and the former 200-section log boundary was
   absent. The process-initialization callback reached 2.136 ms before a player joined.
6. Added a static regression check that inspects the assist admission method and rejects
   synchronous logger calls. The final Release tier passes 1,058 assertions.

---

## Delivered

- Benchmark-only server-assist phase, per-tail, and GC-crossing attribution with an
  unchanged stats-disabled fast path.
- Removal of the synchronous every-200-sections owning-thread progress notification,
  with cumulative totals preserved in existing status surfaces.
- A static hot-path logging regression guard and 1,058 passing Release assertions.
- Accepted baseline, attribution, and fix scenario/CSV evidence under
  `bench/results/2026-08-18-assist-tail-attribution`.
- `dist/vintagehorizons_0.2.1-playtest-assist-tail-fix.zip`, a verified Release package
  with SHA-256
  `4AED0403357777F3FEC3452B74CE27FDCF59A51CAB9FF8B5B1202B0379298C42`.

## Decisions

- A progress log is not worth synchronous I/O inside a 50 ms owning-thread callback;
  `/vhserver` and opt-in interval stats already provide durable cumulative visibility.
- GC attribution uses process-wide collection-count changes only in explicit stats
  sessions. It establishes whether a collection crossed a measured call; it does not
  claim CPU attribution or native-allocation visibility.
- The original 32.450 ms sample remains consistent with the reproduced progress-log
  boundary but is not relabelled as proven because its raw correlated log is unavailable.
- No assist protocol, section blob, or database schema change was needed.

## Traps

- A logger call can block on formatting, sinks, console output, or file I/O. Placing a
  periodic progress notification inside a frame- or tick-critical callback makes that
  latency part of gameplay even when the surrounding work is bounded.
- Separate phase maxima do not identify one slow callback. Correlated per-call timing and
  collection deltas were required to distinguish send work, GC, and the progress log.

## Flagged and unverified

- The old 32.450 ms sample cannot be attributed conclusively without its raw server log.
- The verified fix run covers one machine, one player, one GPU/driver environment, and an
  intentionally elevated 64/s serving configuration rather than ordinary default rate.
- The final Release zip was content-verified but not launched as the actual zip; the user
  will supply its human in-game playtest.
