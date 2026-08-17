# Session 6 — smoothed server work and bounded client installs

**Date:** 2026-08-17
**Branch/commit:** `codex/main-thread-performance`, phase based on `8589b641f1cb`
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

## 1. Context and investigation

`STATUS.md` identified periodic sweep/assist bursts and unbounded owning-thread install
drains as the next P1 phase. Source tracing found three one-second server callbacks: the
savegame sweep, transient `/vhgen` work, and server-assist serving. The client separately
drained every arrived assist section and every completed background load in one tick;
integrated-singleplayer sibling blobs had only a four-item limit even though compressed
section sizes vary widely.

The change preserved all existing ownership boundaries. Block registry and palette work
remain on the owning thread, SQLite connection ownership did not change, and no network,
blob, or database meaning changed.

## 2. Tick-smoothed server allowances

`LodTickAllowance` converts per-second settings into 50 ms tick grants. The whole-item
grant is capped at one rounded-up tick share. Normal fractional overflow is retained so a
rate such as 8/s remains 8/s at a 20 Hz polling cadence. A tick delayed long enough to earn
the complete cap discards older credit rather than scheduling catch-up work afterward.

Sweep and transient-generation listeners now run on the normal 50 ms cadence. Load or
generation issuance consumes the configured allowance under a 1 ms owning-thread
deadline. Probe publication is spread with the same deadline and a 16-probe per-tick cap
while preserving the existing 256 in-flight ceiling.

Server assist now keeps a continuously accrued global allowance and a separate allowance
per player. A rotating start preserves fairness when the global rate is smaller than
combined demand. Serving and explicit refusals share a 2 ms deadline, and disabled or
not-yet-open serving no longer clears an entire queued request set in one callback.

## 3. Time/byte-bounded client installation

`LodDrainBudget` gives each owning-thread install path a 2 ms and 512 KiB ceiling. Network
and sibling-cache paths budget compressed blob bytes. Background-load publication budgets
a stable estimate derived from section arrays, palette entries, and unresolved block-code
strings. The local path retains its four-item safety cap in addition to time and bytes.

All three paths remain FIFO. The first item is always admitted even if it alone exceeds a
limit, after which the drain stops. This guarantees progress for an oversized oldest
section instead of permanently blocking it and everything behind it. Deferred assist
replies continue holding their request slots until actually processed, preserving the
existing explicit success/retry/refusal state machine.

Telemetry now reports items and MiB processed during the interval plus pending item/byte
counts and oldest age for assist arrivals and completed background loads. Local sibling
work reports attempted items and compressed bytes; it has no separate arrival queue.

## 4. Verification

- Fifteen focused allowance assertions cover low and high rates, normal fractional carry,
  idle/delayed caps, clock reversal, and overspend rejection.
- Fourteen focused drain assertions cover exact byte limits, elapsed expiry, late-known
  sizes, oversized oldest-item progress, and structural section-byte estimates.
- The server-assist fixture now has 128 assertions, including FIFO deferral, byte backlog,
  oldest age, request-slot retention, eventual progress, and telemetry reset.
- The complete Release fast tier passed 802 assertions across all 21 suites.
- A Debug mod build against Vintage Story 1.22.5 succeeded with zero warnings and errors.
- `dev/DocCheck.ps1` passed 180 checks under both Windows PowerShell 5.1 and PowerShell 7.

No game process was run. The cadence, ceilings, fairness, and state transitions are
source-traced and harness-tested; their effect on integrated sweep/assist frame times and
the visual acceptability of temporary coarseness are not yet established.

---

## Delivered

- Fractional, delayed-tick-safe allowances for sweep, transient generation, and assist.
- Normal-tick sweep/generation servicing with bounded probe publication.
- Fair per-player/global assist serving and gradual explicit refusals.
- Time/byte-bounded assist, sibling-cache, and background-load installation.
- FIFO oldest-item progress and interval queue age/byte telemetry.
- Twenty-nine new focused assertions and an 802-assertion full fast-tier pass.
- Updated changelog, architecture, gotchas, plan, TODO/DONE, status, and session history.

## Decisions

- Cap actual whole-item grants, not all stored fractional credit. This preserves configured
  long-run rates while still preventing a delayed tick from releasing a full-second batch.
- Use 2 ms and 512 KiB as conservative initial install ceilings shared by the three client
  paths. They are policy values to validate in game, not claimed optimal defaults.
- Guarantee one oldest item per non-empty drain. A hard pre-admission byte ceiling would
  make one oversized section a permanent FIFO barrier.
- Keep server blob reads synchronous for this slice. Their issue count and elapsed time are
  now bounded; moving the connection requires a separately owned reader and ordering work.
- Keep local capture authoritative and release request state only when the corresponding
  deferred result is actually processed.

## Traps

- Clamping stored credit to a whole one-tick token cap before spending discards fractional
  overflow. At 20 Hz, the first implementation reduced an 8/s rate to roughly 6.7/s.
- Rejecting an oversized FIFO head before doing any work starves the entire queue. Permit
  one oldest item, record the overage, and stop before the next item.
- Freeing an assist in-flight slot when its packet is merely queued defeats client-side
  backpressure. Free it only when the owning thread processes the reply and publishes its
  terminal or retryable state.

## Flagged and unverified

- Integrated singleplayer sweep and live server-assist scenarios must confirm that the
  prior one-second cadence disappears from telemetry.
- Fast travel may temporarily retain coarser coverage while install queues catch up. Human
  playtesting must decide whether the initial 2 ms / 512 KiB policy is visually acceptable.
- Oldest-age telemetry is implemented for concrete result queues, but queue-age alerting or
  adaptive budgets are not justified without runtime backlog evidence.
- Foreign blob inflation and structural parsing remain on the owning thread; the new budget
  limits aggregate work but cannot preempt one slow decode already in progress.
- No compatibility bump, public release, push, or game-process test was made.
