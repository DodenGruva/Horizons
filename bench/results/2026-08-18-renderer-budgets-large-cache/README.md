# Renderer budgets and large-cache scaling — 2026-08-18

This directory preserves the first real-game evidence for the 1 ms / 2 MiB mesh-snapshot
and 2 ms / 4 MiB GPU-upload boundary budgets, followed by a sustained route that grew the
isolated client cache beyond three thousand persisted sections.

Both passing runs used Vintage Story 1.22.7, the corrected terrain-facing camera mapping,
allocation telemetry, an unmodified isolated server, and the same Ryzen 7 9800X3D,
Radeon RX 9070 XT, and 64 GB system recorded for the other 2026-08-17/18 routes. The
runner loaded the current Debug build after the runtime constructor fix described below.

## 601-section renderer-budget route

`renderer-budgets-fixed-2026-08-18` used `bench/routes/moving-rotation.txt`, one warm-up
lap, one measured lap, 30-second legs, and a 45-second semantic-convergence cooldown. It
reported 601 sections from the 30,023,680-byte starting cache. All four waypoints settled
without timeout; average FPS ranged from 418.7 to 471.9 and 1% low from 266.3 to 294.2.
Those aggregate rates are supporting context rather than a controlled before/after claim.

Across 30 telemetry intervals, the renderer processed 900 snapshots / 1,158.52 MiB of
estimated retained arrays and 900 uploads / 907.27 MiB of live vertex/index data. Neither
snapshot nor upload had sampled queue depth or age. Direct GL upload p95/p99 reached at
most 225/400 microseconds; the absolute maximum was 2,721 microseconds. Disposal peaked
at 61 microseconds. No renderer phase reached 25 ms. One 33.093 ms game tick was entirely
attributed to resident eviction (32.894 ms) while the interval's renderer upload maximum
was 245 microseconds.

The final guarded sample had zero pending capture input/results, mesh jobs, mip work,
render-dirty sections, unsaved sections, asynchronous loads, storage backlog, and worker/
storage errors. Client and server shut down normally and left no pidfiles.

## Revision-acknowledged persistence route

After persistence revisions, exact write acknowledgements, retry retention, same-key
coalescing, and two-stage shutdown draining were implemented, the corrected moving route
reopened the 157,724,672-byte cache and reported all 3,132 sections. It froze and durably
wrote 138 section revisions while moving. The final guarded sample had zero unsaved
sections, zero write backlog, and zero write errors; both isolated processes shut down
normally and left no pidfiles. The database remained 157,724,672 bytes and had SHA-256
`060ADA9C046D2752CA221B67F7FA6CF1AF37FBCC5CE3AB69C12D805E7F89831C` after close.

The corresponding Release fast tier passed 1,050 assertions across 25 suites. Its 44
persistence assertions include an injected first-write failure followed by a successful
retry, repeated-mutation stale-ack rejection, same-key pending-snapshot coalescing, a
300-key worker drain, and a real SQLite close/reopen that reads the newest revision.

## 3,132-section growth route

`large-cache-growth-2026-08-18` began with the same 601-section cache and moved 12,800
blocks through a new corridor over 600 seconds at about 21.3 blocks/s, rotating the camera
eight times. A 60-second cooldown followed. The route captured 36,928 columns and grew
the database from 30,023,680 bytes to 157,724,672 bytes. A read-only post-run query counted
3,132 `Section` rows. Runtime residency remained bounded: the final sample had 707
resident sections and 2,312 RAM evictions.

Across 45 telemetry intervals, the renderer processed 94,285 snapshots / 110,669.20 MiB
of estimated retained arrays and the same number of uploads / 78,901.48 MiB of live data.
The maximum sampled backlogs were:

- Snapshots: 18 items / 26.12 MiB / 157 ms oldest.
- Uploads: 4 items / 2.76 MiB / 16 ms oldest.
- Capture publication: 21 items / 2.21 MiB / 297 ms oldest.

Direct GL upload p95/p99 reached at most 225/350 microseconds. Absolute GL upload and
disposal maxima were 6,861 and 3,173 microseconds. Renderer scheduling, upload phase,
traversal, and draw-submission maxima were 12,261 / 6,904 / 6,508 / 10,598 microseconds.
No renderer phase reached 25 ms. The route averaged 521.8 FPS with a 150.0 FPS 1% low;
this combines first-pass server generation, client capture, persistence, and rendering
and is not a controlled renderer-only comparison.

One 36.883 ms game tick occurred. Pipeline attribution put 36.289 ms in one atomic
capture-apply result, with snapshot/upload backlogs still bounded. This is evidence for a
remaining capture-publication tail under faster first-pass exploration, not a failure of
the renderer budgets. The final cooldown nevertheless converged every guarded field to
zero with no errors, and both isolated processes shut down normally.

## Runtime bug found before the passing runs

The first current-code launch crashed because converting `LodDrainBudget` to a struct
made `new LodDrainBudget()` use zero-initialization instead of the overload whose arguments
were optional. The second queued item dereferenced the uninitialized clock delegate. An
explicit parameterless constructor and a production-form regression check fixed the issue;
the full fast tier then passed 1,004 assertions before either passing route ran.

An earlier route completed against a stale pre-change Debug artifact because the Windows
runner deploys the existing build output. Its numbers are intentionally not preserved or
used here.

## Evidence limits

No person watched either passing route in motion. This evidence establishes automated
runtime queue convergence, driver-call timing, bounded residency, and thousands-section
scaling on one machine. It does not establish subjective clipping or turn-around quality,
a product far-distance default, GPU shader/fill attribution, or behavior on other drivers.
