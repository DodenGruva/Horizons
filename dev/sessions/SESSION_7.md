# Session 7 — off-thread foreign decode and allocation telemetry

**Date:** 2026-08-17
**Branch/commit:** `codex/main-thread-performance`, changes based on `500e57e`
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

## 1. Context and investigation

The next P1 item after bounded installs was foreign section inflation and structural
parsing. Both server-assist arrivals and integrated-singleplayer sibling-cache blobs still
called the storage deserializer from the game tick. A large compressed section therefore
remained capable of consuming an uninterruptible part of that tick even though aggregate
installation already had elapsed-time and byte ceilings.

Source tracing preserved two hard boundaries: the live block registry and client atlas
remain owning-thread-only, and local observed data must win if it becomes authoritative
while a foreign result is pending. The existing storage owner already had the correct
lifetime and error-isolation model for compressed section work, so a new standalone thread
was unnecessary.

After a brief test build, the human reported a noticeable subjective improvement. The run
was not controlled or long enough to quantify the change, so it is recorded as a brief
human test rather than benchmark evidence.

Capture-result publication remains the largest phase seen in the earlier short route, but
budgeting it was deliberately deferred until a longer movement run reproduces the
11–14 ms maxima. Work continued on the independent managed-allocation telemetry item
instead.

## 2. Storage-owned foreign structural decode

Server and sibling-cache blobs now transfer to bounded queues owned by
`LodStorageThread`. The worker inflates and structurally deserializes them with a null world
accessor, retaining palette block codes and persisted flags without reading the live block
registry. Server and local producers have separate outstanding limits, so sibling-cache
work cannot consume the protocol-sized reservation for network replies.

Decoded results carry their world epoch, section key, source, estimated content bytes, and
ready time. Owning-thread publication uses the existing 2 ms / 512 KiB progress-guaranteed
drain policy. It resolves live block ids and policy flags, applies client atlas colours,
filters newly skipped runs, installs the section, and queues it for local persistence.
Corrupt and future blobs fail one result without stopping the storage worker.

The commit path checks for a resident local section before and after owning-thread
resolution. If capture or another local load won while decode was pending, the foreign
result cannot overwrite it or poison its later reload. The foreign source remains a reload
fallback after adoption until save acknowledgements exist; declaring the row durable at
save enqueue would create a race with eviction and the still-pending write.

Server transport slots now remain in flight after packet arrival and decoder acceptance.
They release only when owning-thread publication succeeds or rejects the result. Local
sibling-cache requests likewise leave the wanted set at decoder acceptance while retaining
their world in-flight marker until publication, preventing duplicate submissions without
stranding the key.

## 3. Opt-in per-phase allocation telemetry

`LodPhaseCost` samples `GC.GetAllocatedBytesForCurrentThread` around existing phase
boundaries when explicit stats or benchmark auto-unpause is enabled. It records interval
allocated bytes, maximum single-call allocation, sample count, and average bytes. Counter
calls sit outside the measured elapsed-time boundary so enabling allocation telemetry does
not charge the counter read to the phase being studied.

Tracking is configured per owner rather than through a global static switch. Client tick,
client pipeline, and renderer phases opt in together; an integrated server pipeline does
not silently collect unused allocation samples merely because its client requested stats.
When disabled, the phase path does not call the GC allocation counter.

Periodic logs now report interval MiB and maximum-call KiB for total tick work, assist,
local offers, pipeline subphases, and renderer subphases. The enabled-versus-disabled
steady-state overhead comparison remains open and belongs with the longer benchmark work.

## 4. Verification and handoff

- The storage fixture proves separate local queue bounds, FIFO identity, world epochs,
  deferred palette codes, zero worker-resolved block ids, future-blob rejection, and worker
  survival after a rejected blob.
- Server-assist checks prove decoder acceptance retains transport slots and that only
  owning-thread success counts as received.
- Remote-key checks prove local decoder acceptance prevents duplicate submissions while
  retaining responsibility and that local-win rejection does not poison resident data.
- Phase checks cover deterministic allocation totals/maxima/averages, reset behavior,
  disabled counter omission, enabled sampling, and a live known allocation.
- The full Release fast tier passes 877 assertions across 21 suites.
- Debug and Release mod builds against Vintage Story 1.22.5 succeed with zero warnings and
  errors.
- `git diff --check` passes. `dev/DocCheck.ps1` passes 182 checks under both Windows
  PowerShell 5.1 and PowerShell 7.

---

## Delivered

- Bounded storage-owned inflation and structural parsing for network and sibling-cache
  foreign sections.
- World-epoch results, owning-thread live resolution/recolour/publication, and local-win
  rejection.
- Request-slot retention until actual publication and separate network/local decoder
  capacity.
- Foreign publication timing, queue-byte, queue-age, and processed-byte telemetry.
- Opt-in per-phase managed-allocation totals and worst-call deltas for client tick,
  pipeline, and render work.
- Seventy-five additional fast assertions since Session 6, for 877 total.
- A locally packaged test ZIP; the human's brief playtest reported a noticeable subjective
  improvement.
- Updated changelog, architecture, gotchas, TODO/DONE, status, session history, and
  documentation working agreement.

## Decisions

- Reuse the storage owner for foreign structural decode rather than add another thread.
  The work needs no SQLite access, but this owner already provides bounded lifetime,
  below-normal priority, serialization error isolation, and orderly teardown.
- Reserve decoder capacity separately by source. Local sibling-cache adoption must not
  crowd out the network protocol's bounded in-flight replies.
- Treat decoder enqueue as transferred responsibility, not installation. Transport and
  world in-flight state end only at owning-thread publication or terminal rejection.
- Keep the remote fallback after successful adoption until revisioned storage
  acknowledgements can prove the local row durable.
- Defer capture-publication budgeting until a longer movement run reproduces the earlier
  maximum. Continue with allocation telemetry because it is independently verifiable and
  improves that future diagnosis.
- Make allocation tracking opt-in and per owner. Global enablement would make an integrated
  server pay for unused samples when only the client requested telemetry.
- Record significant completed session changes under `CHANGELOG.md` Unreleased even when
  no version is released; release finalization remains a separate mandatory update.

## Traps

- Packet arrival is not completed installation. Freeing a request slot before background
  decode and owning-thread publication weakens backpressure and hides terminal parse
  failure behind a false success count.
- A foreign section queued for persistence is not yet a durable local row. Removing its
  remote fallback before a write acknowledgement can make eviction race the pending save.
- A global allocation-telemetry switch crosses integrated client/server ownership. Scope
  sampling to the pipeline or renderer whose measurements will actually be reported.
- Allocation-counter calls placed inside the elapsed interval distort the phase timing
  they are intended to explain. Read the starting counter before the starting timestamp
  and the ending timestamp before the ending counter.

## Flagged and unverified

- The brief human test reported a noticeable improvement, but no controlled before/after
  assist or sibling-cache scenario quantified frame time, decode backlog, or throughput.
- Live integrated singleplayer has not yet proved sibling-cache decode thread ownership or
  local-win behavior end to end.
- The initial foreign publication budget and separate queue limits are conservative policy
  values; backlog-age telemetry must guide any tuning.
- Allocation sampling is harness-tested, but its enabled-versus-disabled overhead has not
  been measured in a steady stationary game scenario.
- Capture publication still needs a longer continuous-movement reproduction before it is
  time-budgeted.
- No compatibility number or public release was changed.
