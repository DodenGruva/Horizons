# Session 38 - GPU feasibility and legacy-only renderer boundary

**Date:** `2026-08-21`
**Branch/commit:** `render-overhaul` at base commit `d86abe02d74f483abd68dc173903182f86ac2fb4` (session changes committed at close)
**Mod version:** `0.3.47` source metadata; the next changed playable artifact must be `0.3.48`
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

The owner approved the measurement-first GPU renderer plan and then Phase 1's safe
renderer boundary. Phase 0 needed to establish whether the primary driver exposes a usable
GL 4.3 compute/SSBO/MDI surface and a copyable current depth texture without letting a
probe change rendered output. Phase 1 then needed to create a narrow dual-path lifecycle
while preserving the established renderer as the only visible authority.

The work followed the renderer, pipeline epoch, mesh publication/removal, world teardown,
shader reload and direct OpenGL probe paths. Several isolated probes used the repository's
sandbox runner after individual owner approvals. The owner later took responsibility for
FPS benchmarking, so the assistant stopped launching performance runs and treated the
existing measurements as feasibility evidence only.

## Work narrative

### 1. Phase 0 measured capability without selecting a renderer

Opt-in delayed timer rings now measure the existing cached opaque and water passes without
waiting for unavailable results. Draw calls, submitted vertices/indices and live mesh bytes
make those timings interpretable. A one-time report separates advertised features from
validated entry points and runtime operations.

The primary Radeon RX 9070 XT runs the game on GL 4.3. Isolated probes validated the
required loaded entry points, a minimal compute dispatch, expected SSBO write/readback,
exact program and generic/indexed SSBO restoration, and an exact-format copy of the active
2,560x1,440 `DEPTH_COMPONENT32` attachment into a disposable 12-level private mip chain.
The framebuffer, active texture and texture-unit-zero bindings were restored and no GL
errors were observed. None of these probes retained a resource or selected a fast path.

### 2. Benchmark evidence was narrowed instead of overclaimed

The Bodanboys save was copied into frozen sandbox seed/working profiles and a reproducible
six-view route was added near display coordinate 0,0. The runner gained frozen seed restore,
save-file seeding, automatic console commands, GPU-render scenario records and explicit GPU
statistics wiring. One initial watched run was refresh-rate capped and cannot serve as an
uncapped FPS baseline.

Matching uncapped temporal-on/off runs produced stable open-horizon GPU timings and clean
shutdowns, but the camera was high enough that essentially no cached terrain sat behind
vanilla terrain. The owner correctly rejected the pair as a temporal-occlusion comparison.
It remains valid open-horizon feasibility evidence only. The owner will run future FPS
benchmarks and provide any performance acceptance evidence.

### 3. Phase 1 made legacy authority structural

Mesh publication/removal, frame preparation, opaque draw, water draw, clear and disposal
now pass through one coordinator. Its visible path must be named `legacy`; construction
rejects a Phase 1 shadow that claims GL ownership, and coordinator draw methods call only
the visible legacy target. `VINTAGEHORIZONS_GPU_RENDERER=off` is the default. `shadow` or
`auto` may activate only a capability-validated CPU mirror of immutable section identities
and geometry counts.

The mirror never retains mesh references or GL handles. A mirror exception disables and
clears it while the already-published legacy resource remains authoritative. World epochs,
globally non-aliasing section-render generations and distinct opaque/water resource
generations reject delayed publications from an older world.

### 4. Direct GL state now has one owner

The compute and depth-copy probes use one capture/restore policy for current program,
generic and indexed SSBO binding zero, draw/read framebuffer bindings, active texture and
the 2D binding on texture unit zero. Restoration order accounts for `BindBufferBase`
changing the generic SSBO binding, and the helper verifies the exact incoming values after
restoration.

### 5. Verification and documentation

The warning-free Release build succeeded. The game-backed fast tier passes 1,627 assertions,
including 72 GPU capability, selection, lifecycle, generation, timer and GL-state checks.
`dev/DocCheck.ps1` passes 1,441 checks. No game process was launched after the owner took
over FPS benchmarks, and Phase 1 runtime equivalence remains owner evidence rather than an
automated or assistant-observed claim.

---

## Delivered

- Opt-in nonblocking GPU pass timers, draw/geometry/live-byte telemetry and benchmark
  scenario wiring.
- Advertised-versus-validated GL capability, entry-point, compute/SSBO and active-depth
  diagnostics.
- A disposable exact-format private depth-copy/mip feasibility probe with exact state
  restoration.
- A legacy-only renderer lifecycle coordinator and CPU-only metadata shadow.
- World, section-render and opaque/water resource generation identities with stale-world
  rejection.
- A shared exact GL-state capture/restoration owner used by both direct OpenGL probes.
- Frozen Bodanboys sandbox seeding and a reproducible open-horizon GPU route.
- Source/harness coverage, current-state documentation and a staged Phase 0/1 plan record.

## Decisions

- Legacy remains the only visible renderer through Phase 1. No environment value can select
  a visible GPU fast path.
- The Phase 1 shadow is opt-in, CPU-only and fail-isolated. Real regional GL resources begin
  only in a separately approved later phase.
- Primary-machine capability feasibility is sufficient to preserve the plan; performance
  acceptance remains owner-run and does not inherit from a capability probe.
- The Bodanboys aerial pair is retained as open-horizon GPU-cost evidence, not as temporal-
  occlusion evidence.
- No protocol, blob or database compatibility number changed.

## Traps

- A demanding landscape is not automatically an occlusion test. Cached terrain must
  actually project behind vanilla foreground at the measured camera angle.
- A benchmark launched with the watch/focus behavior can be refresh-rate capped. Record and
  verify cap state before treating FPS as uncapped evidence.
- `BindBufferBase` changes both indexed and generic SSBO state. Restore indexed first and
  generic last, then verify both.
- An explicit clear must remove identities even when the world epoch is unchanged; epoch
  transition logic alone is insufficient for teardown.

G58 records the reusable benchmark traps. The state-restoration and clear cases are covered
directly by automated checks and the settled architecture.

## Flagged and unverified

- Phase 1 has not received an owner runtime-equivalence pass with `off` against `shadow`.
- FPS/noise-floor comparisons, other drivers, shader reload in a live session, world swaps,
  mod deferral and long-session behavior remain owner/runtime evidence.
- No regional arena, indirect command, HZB classifier, packed geometry, GPU LOD or visible
  fast-path resource exists yet.
- The next changed playable artifact must advance both version declarations from 0.3.47 to
  0.3.48 before packaging, installing or launching it.
