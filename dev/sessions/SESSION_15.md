# Session 15 — off-thread server-assist blob reads

**Date:** `2026-08-18`
**Branch/commit:** `codex/main-thread-performance`, working tree based on `b40ba7e`
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> This session changes internal connection ownership and scheduling only. It does not
> change assist protocol, stored blob format, or database schema compatibility.

## Context and investigation

Session 14 measured one synchronous server-assist SQLite blob read at 68.755 ms and the
owning-thread assist phase at 68.879 ms. The elapsed serving deadline could stop later
reads but could not preempt the admitted call. Source tracing confirmed the assist called
the writable `LodStore` connection directly from `ServePending`.

## Work narrative

1. Added one dedicated single-consumer assist reader. The reader creates, prepares, uses,
   and disposes its own unpooled read-only SQLite connection on its worker thread.
2. Bounded queued, executing, and completed reads under one 16-item allowance. Every
   accepted request produces a blob, miss, or explicit failed result.
3. Tagged work with player uid and connection session. Disconnect removes owning-thread
   responsibility; a late result cannot be sent to a later connection with the same uid.
4. Preserved response order with per-player ordered read batches. Later immediate refusals
   wait behind accepted reads, while the elevated 64/s test configuration may still admit
   its multi-item tick allowance.
5. Removed the shared writable-store `LoadBlob` path. The capture system now exposes only
   the active database path, not a synchronous read method.
6. Added real SQLite checks for FIFO results, combined outstanding cap, exact bytes,
   ordinary misses, failed opens, retryable failure state, and unpooled disposal. The full
   fast tier passes 964 assertions.
7. Repeated the saturated route twice while refining order/throughput policy. The retained
   batched run proved a cold client, 514-section warm server cache, all 16 request slots,
   395 requested/received/installed sections, zero declines, and graceful shutdown.
8. One 17.481 ms background reader call occurred in an interval whose owning-thread assist
   maximum was 0.989 ms. A later 32.450 ms assist outlier occurred with 0.179 ms reader and
   0.249 ms individual-send maxima, so it is explicitly not attributed to SQLite.

---

## Delivered

- Bounded dedicated server-assist reader with explicit SQLite ownership.
- Session-safe, ordered owning-thread response publication and retryable read failures.
- Removal of the synchronous writable-store assist read door.
- 964 passing fast assertions, clean Debug build, retained route CSV/scenario proof, and
  a passing documentation check.

## Decisions

- Reader work and completed blobs share one cap; bounding only the request queue would let
  completed large blobs accumulate behind a stalled game thread.
- Per-player batches preserve configured rate capacity without allowing later synchronous
  outcomes to overtake earlier accepted reads.
- Reader latency and owning-thread service latency remain separately reported. Moving a
  slow call does not make the call fast; it removes that latency from the server tick.

## Traps

- A task running on a worker is not safe if its SQLite connection or prepared command was
  created or used by another thread. Connection ownership must be end-to-end.
- Serializing one read per player preserves order but unintentionally caps an elevated
  configuration at the 20 Hz server cadence. Ordered batches preserve both properties.

## Flagged and unverified

- One later 32.450 ms assist-service sample remains unattributed; its interval had
  sub-0.2 ms reads and sub-0.25 ms individual sends.
- The route is one player, warm server cache, cold client cache, and 64/s stress. Default
  rate, multiple players, disconnect during active reads, long soak, and human observation
  remain untested.
