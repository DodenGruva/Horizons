# Completed generation and saturated assist — 2026-08-18

These artifacts close the two remaining dedicated-server observability scenarios. Both
used the Windows isolated runner, Vintage Story 1.22.7, stats enabled, and graceful
client/server shutdown. Private screenshots and complete sandbox logs remain ignored.

## Completed transient generation

`completed-generation.csv` and its scenario proof used the pinned generation config and
`/vhgen start 8 520000 520000`, far from the stationary client. All 289 work columns were
transiently generated; none loaded from the savegame, timed out, or lacked a height map.
The terminal verifier found all 256 sampled absent positions still absent. Generation work
issue reached 5.706 ms maximum, the server capture pipeline reached 6.551 ms maximum, and
neither reported a 25 ms hitch.

## Saturated assist and early-join race

Both assist runs started with no client database, a 514-section active server cache,
serving enabled at 64 sections/s, no radius cap, and sweep/generation disabled. The first
run (`assist-before*`) filled the client's 16 normal request slots but received and
installed nothing; server blob-read and send telemetry remained zero. The semantic guard
rejected it even though its frame CSV completed.

Source tracing found that the 50 ms serve loop removed a bounded request queue whenever
`PlayerByUid` had not yet exposed the joining player as `Playing`. The request can arrive
during that transition, while the disconnect event already handles a real departure. The
fix retains the queue through transient join state.

The unchanged rerun (`assist-after*`) requested, received, and installed 395 sections,
reached the required 16-slot peak, declined none, and drained transfer/publication backlog
by the 30-second report. Client game ticks reached 7.789 ms maximum with no 25 ms hitch;
foreign publication peaked at 2.639 ms and 2 queued items / 0.62 MiB / 62 ms old.

The run also establishes the next server bottleneck. Synchronous owning-thread blob reads
reached 3.75/17.5/68.755 ms p95/p99/max and assist service reached 68.879 ms maximum. The
elapsed budget stops later work but cannot preempt one admitted SQLite read. This saturated
64/s scenario is intentionally above the default 8/s per-player rate, so its throughput
and aggregate FPS are stress evidence rather than ordinary-play expectations; the
single-read tail still warrants isolated connection ownership work.
