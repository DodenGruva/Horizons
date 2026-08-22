# Session 39 — Regional arenas, indirect groundwork, and the measured draw-call gate

**Date:** `2026-08-22` (Phase 2 work began 2026-08-21)
**Branch/commit:** `render-overhaul` at base commit `b42c6d3` (session changes committed at close)
**Mod version:** `0.3.50`; `0.3.48` and `0.3.49` were built and installed during the session
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

The owner approved Phase 2 of the GPU renderer plan and deferred the Phase 1 runtime
equivalence check to his next ordinary play session. Phase 2 asked for regional opaque
arenas holding the existing expanded geometry, proven for allocation, replacement and
lifetime without changing vertex semantics. The session then continued into Phase 3's
non-drawing half and, at the owner's request, ran the measurement itself.

Nothing about how terrain is drawn changed at any point. Legacy remained the only visible
path throughout.

## Work narrative

### 1. Phase 2 built the arenas and the mirror

Opaque vertex and index arenas hold fixed-size pages with a coalescing free list per page.
Replacement allocates and fills the new spans, publishes the record, then retires the old
pair behind a GPU fence; reclamation is bounded per frame and never waits. Releasing a span
that is already free throws rather than handing the same bytes out twice, and the mirror's
fault isolation turns that into a disabled mirror rather than a corrupted one.

The mirror copies the mesher's arrays during publication and never retains them. A section
whose allocation is refused is dropped outright rather than left holding superseded
geometry, so the mirror is always a subset of the truth and never a stale copy of it.

Transfers use `GL_COPY_WRITE_BUFFER` so array and element-array bindings are never touched,
and each call captures and restores that binding through the shared state guard. The Phase 1
rule that the shadow may own no GL resources was replaced with the stricter and more useful
one: the shadow may own buffers, and the coordinator has no route to its draw methods.

### 2. Phase 3's design corrected the Phase 2 allocator

Pages were first grouped by world region only, with a region free to spill across as many
pages as it needed. Designing the draw showed that cannot be submitted: one indirect batch
binds one vertex buffer and one index buffer. Pages are now allocated in sets — one vertex
page paired with one index page — and both halves of a section must fit the same set. See
G59; the same reasoning corrected the ceiling split later in the session.

### 3. Phase 3 groundwork, measured rather than assumed

The 64-byte section record carries every per-section value the established shader receives
as a uniform, at asserted offsets. The 20-byte indirect command zeroes `instanceCount` for a
rejected section so slots never shift. The builder turns the traversal's own ordered
candidates into contiguous command runs per page set, with sets emitted in the order their
nearest section arrived.

The builder is fed from `SubmitOpaqueMesh`, so it sees exactly what the visible path draws
after every CPU decision. That made the phase's draw-call gate measurable before any shader
existed.

### 4. Three sandbox runs, two instrument defects, one answer

Run one reported 6x-8x and was wrong. A 256 MiB ceiling against 851 MiB of live geometry
meant two thirds of the world never entered the arenas, and the coverage counter could not
see its own misses (G60). Run two, with the ceiling fitted and coverage reported, gave
3.7x-4.6x at 100% coverage — short of the phase's 90% requirement.

The diagnosis was arithmetic rather than architectural. A batch is one page set; sections
average about 780 KiB; about a quarter of mirrored sections are on screen at once. An 8 MiB
page therefore contributed about four drawn sections per batch. Run three at 32 MiB pages
gave 10.6x-11.5x, clearing the gate. Page size became a runtime setting so further tuning
costs a run rather than a build.

Frame rates matched the shadow-off run in all six views across all three runs.

### 5. Verification

Warning-free Release build. The fast tier passes 1,841 assertions, including 130 arena and
69 indirect-layout checks covering encoding, allocation, ceiling and oversize refusal,
fenced retirement, bounded reclamation, coalescing, page convergence, region assignment at
extreme coordinates, content equality, replacement, world-clear teardown, coverage
accounting, command and record layout, batching and ordering, page pairing, and a 3,000-step
streaming stress with an overlap invariant. `dev/DocCheck.ps1` passes 1,447 checks.

---

## Delivered

- Regional opaque vertex/index arenas with paired page sets, coalescing free lists, fenced
  retirement and bounded per-frame reclamation.
- A geometry mirror covering all live opaque sections, with an explicit memory ceiling,
  optional byte-exact readback verification, and fault isolation.
- An OpenGL arena backend transferring through `GL_COPY_WRITE_BUFFER` under the shared state
  guard, with the guard extended to that binding.
- The 64-byte section record, the 20-byte indirect command, and the batch builder, all with
  asserted layouts.
- Live shadow measurement of submissions against batches, with coverage.
- `.vhgpu off|on|verify`, a runtime switch that turns the measurement on mid-session and
  re-meshes live sections into it.
- `-GpuRenderer`, `-GpuArena`, `-GpuArenaMb` and `-GpuArenaPageMb` on the Windows bench
  runner, recorded in the scenario proof.
- 0.3.50 packaged and installed; 0.3.48 and 0.3.49 superseded within the session.

## Decisions

- Pages are allocated in pairs and a section may not straddle two sets. This corrects Phase
  2 rather than extending it.
- A shared ceiling splits by page size, not by the byte ratio of the geometry.
- Page size is a setting, not a constant: it is the measured lever on batch count and
  finding its best value is a measurement.
- Searching a full page set is not an allocation failure and is no longer counted as one.
- The Phase 1 "shadow owns no GL resources" rule is replaced by "the coordinator can never
  draw the shadow", which survives Phase 2 and is structurally enforced.
- 32 MiB pages clear the gate but commit roughly twice the bytes they hold; 16 MiB is
  untested and may be the better trade. Not settled.

## Traps

- A multi-draw batch is one buffer pair, so paired allocation is a correctness rule and not
  a packing preference. Promoted to G59.
- An instrument that returns early on what it cannot find will report success over whatever
  it happened to cover. Promoted to G60.
- A section count is not a batch count. The reduction follows from how many *drawn* sections
  fit one page, which is page size divided by section size times the drawn fraction — none
  of which is obvious from the region shape that looks like the natural knob.
- Another LOD mod installed on the live profile makes this mod idle by design, so a
  measurement session on the live install would have produced nothing. The sandbox profile
  is unaffected.

## Flagged and unverified

- Phase 1 runtime equivalence remains unobserved; the owner defers it to ordinary play.
- The 91% figure counts submissions removed, not time saved. Whether it converts to frame
  time is unmeasured and is owner-run evidence; G52 applies directly.
- No visual evidence exists for anything in this session, because nothing visible changed.
- 16 MiB pages, other drivers, world swaps, shader reload and long sessions are untested.
- The re-mesh churn recorded in `dev/TODO.md` — about seven re-publications per section in
  six stationary minutes — has an unestablished cause.
- No regional draw, HZB, GPU culling, packed geometry or visible fast path exists.
