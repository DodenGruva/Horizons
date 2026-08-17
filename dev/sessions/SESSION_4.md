# Session 4 — incremental discovery and retry-safe requests

**Date:** 2026-08-17
**Branch/commit:** `codex/main-thread-performance`, phase based on `3e071bc`
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

## 1. Context and investigation

`STATUS.md` identified incremental local/network key discovery and the stranded local-offer
miss as the next evidence-ordered phase. Source tracing confirmed three owning-thread costs
or state defects: `PumpLocalOffers` synchronously enumerated the complete sibling-cache
index, `PumpServerAssist` passed the retained `RemoteKeys` set through `AddRemoteKeys` on
every tick, and the local path added a key to its `taken` array before a blob read succeeded.

The work followed the existing concurrency boundaries. `LodWorld` and remote-key state
remain owning-thread-only. SQLite discovery received its own read-only connection rather
than moving a shared connection across threads. No protocol field, blob meaning, database
schema, or player-facing default changed.

## 2. Incremental local discovery

`LodLocalOfferSource` now owns two explicitly separated connections. The owning game
thread retains the visibility-driven blob connection. A below-normal discovery thread
creates, uses, and disposes a second read-only unpooled connection entirely on that thread.
It performs a safe full key scan every two seconds, computes newly seen keys off-thread,
and publishes immutable batches of at most 2,048 keys. The game tick applies at most one
batch and does no SQL enumeration.

The scanner commits keys to its known set only after a query reaches EOF. A failed partial
scan therefore cannot mark an unpublished prefix as already known. Unchanged scans publish
nothing. Transient blob misses use a one-second query cooldown, and the reader is stopped
both on ordinary world leave and on mod disposal.

## 3. One-time manifest ingestion

The assist client now applies one protocol-bounded manifest chunk per tick and publishes
only keys newly accepted from that chunk. The pipeline registers that delta directly;
the complete retained manifest is no longer enumerated on ordinary ticks. Repeated chunks
produce no pipeline work. Reset drains queued manifest chunks so bounded ingestion cannot
carry old-world packets into a later world.

## 4. Explicit transfer outcomes and retries

Local sibling-cache reads now finish as `Installed`, `RetryableMiss`, or `Unavailable`.
A miss keeps the key wanted and releases `LoadsInFlight`, because no reader retained
responsibility. Successful installation completes the wanted request. Parse failure and a
local-win race end through the existing unavailable path, which clears the obsolete request
without poisoning a resident local section.

Server arrivals publish either retryable or permanent failure to the owning pipeline.
A retryable response restores the wanted key and releases the completed transport attempt.
Retries use a monotonic 7.5-second cooldown and an eight-attempt ceiling, spanning roughly
one minute instead of exhausting the ceiling over consecutive game ticks. Permanent
refusal, parse failure, and exhausted retry state remain terminal and explicit.

## 5. Verification

- Debug mod build succeeded against the installed Vintage Story 1.22.5 assemblies with
  zero warnings and errors.
- The complete Release fast tier passed 728 assertions across all 18 suites.
- SQLite fixtures cover initial worker publication, later deltas, unchanged scans, and
  connection disposal.
- Remote-key and assist fixtures cover transient miss, later success, local-win race,
  parse/permanent failure, one-time manifest publication, retry restoration, cooldown,
  bounded exhaustion, and refusal.
- `dev/DocCheck.ps1` passed 170 checks under both Windows PowerShell 5.1 and PowerShell 7.

No game process or benchmark route was run. The removal of whole-set owning-thread work is
source-traced and harness-tested, not yet an in-game performance measurement.

---

## Delivered

- Dedicated sibling-cache discovery worker with explicit SQLite connection ownership.
- Coarse background full scans with delta-only, bounded owning-thread publication.
- One-time, one-chunk-per-tick network manifest ingestion.
- Explicit local and network transfer outcomes, retry-state restoration, and cooldown.
- Cross-world manifest cleanup and sibling-reader shutdown coverage.
- Regression checks and updated concurrency architecture.
- Reconciled plan, TODO/DONE, current status, and session history.

## Decisions

- Keep the safe full SQL scan for now; add a monotonic cursor only when deletion, replacement,
  and restart semantics are proven.
- Use separate SQLite connections for discovery and blob reads rather than sharing command
  state across threads.
- Publish bounded immutable key batches and mutate `LodWorld` only on the owning thread.
- Treat retryability as an explicit state transition, not as an empty response whose caller
  must infer whether to forget or retry.
- Add elapsed cooldown as well as an attempt limit so the retry window covers the write gap
  it exists to tolerate.

## Traps

- Updating a scanner's known set while a query is still reading can lose the successfully
  read prefix if the query later fails; publish and commit only after EOF.
- A bounded retry count without elapsed backoff can be consumed in consecutive ticks and
  provide almost no useful retry window.
- Bounding manifest ingestion makes queued chunks capable of surviving until world leave;
  reset must drain them explicitly.

## Flagged and unverified

- Dedicated-reader behavior and retry convergence have not been exercised in an integrated
  singleplayer or live server-assist game process.
- No before/after runtime measurement isolates the main-thread benefit of delta discovery.
- Visibility-driven local blob reads and foreign decode/recolour/install remain on the game
  thread under an item-count budget; elapsed-time and byte budgets remain open work.
- The next implementation phase is cached mesh bounds plus quantized far-plane hysteresis.
- No changelog entry, compatibility bump, release package, or push was produced.
