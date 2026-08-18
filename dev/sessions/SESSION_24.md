# Session 24 - cached-terrain transition diagnosis and hybrid handoff plan

**Date:** `2026-08-18`
**Branch/commit:** `codex/main-thread-performance` (session-close commit)
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

The user reported three visible artifacts while flying around cached terrain: a broad
render-radius-shaped gap while vanilla chunks were still streaming, distant cached colors
alternating as the camera moved, and cached terrain shrinking downward at a fixed approach
distance. After a first playtest package improved the presentation, the user identified a
fourth problem: cached geometry remained mixed with already rendered vanilla terrain.

This session source-traced the current shaders and renderer, inspected the installed
Vintage Story 1.22.5 client readiness API/implementation, produced two Release playtest
packages, and then separated the immediate radial mitigation from a proposed exact
chunk-aware design. The user explicitly reserved in-game testing; no Vintage Story process
was launched by the assistant.

## Work narrative

1. The old fragment shader discarded cached terrain inside a distance derived from 78.5%
   of configured vanilla view distance. That boundary knew nothing about streaming state,
   so it could remove fallback before the replacement terrain existed and form the
   reported band.
2. Cosmetic terrain noise sampled camera-relative `worldPos`. The same terrain point
   therefore moved through the noise field as the camera moved, producing alternating
   distant colors. The renderer now supplies a stable section world origin while geometry,
   projection, and fog remain camera-relative.
3. The vertex shader deliberately pushed cached terrain down by as much as five blocks
   across a 110-block approach band. Removing that deformation eliminates the source of
   the reported downward shrink.
4. Removing the old outer cutoff retained fallback but allowed cached and vanilla surfaces
   to overlap after vanilla caught up. The immediate follow-up uses a conservative inner
   radial handoff: no farther than half the approved vanilla distance and with at least 192
   blocks of cached fallback overlap. This is a playtest stopgap, not exact chunk ownership.
5. The installed public `ICoreClientAPI.IsChunkRendered(EntityPos)` query resolves a
   32x32x32 client chunk and tests `quantityDrawn > 0`. Client source inspection found that
   this counter advances during tessellation before the completed mesh is consumed/uploaded,
   so a true result is useful but not by itself a post-upload guarantee.
6. A finest LOD section spans 64x64 horizontally while vanilla chunks span 32x32. Splitting
   every cached mesh into vanilla-sized GPU objects would multiply draw calls, mesh
   references, and vertices and weaken greedy merging. Rebuilding meshes as vanilla chunks
   stream would add still more churn.
7. The accepted direction is a hybrid ownership system: bounded event-fed readiness,
   CPU skipping for wholly vanilla-owned sections, the unchanged fast path for wholly
   cache-owned sections, and a compact GPU readiness mask only for mixed frontier meshes.
   The detailed implementation, failure, seam, instrumentation, and performance gates are
   recorded in `dev/plans/PLAN_CHUNK_AWARE_VANILLA_HANDOFF.md`.

---

## Delivered

- World-anchored cached-terrain color noise that no longer follows the camera.
- Removal of the five-block near-transition sink.
- A conservative radial near-handoff stopgap that retains a broad streaming fallback while
  suppressing the close core.
- Twenty-three focused assertions added for handoff arithmetic and cross-stage shader/
  renderer invariants. They have not been rerun since these edits; the last established
  full fast-tier result remains 1,058 assertions.
- Two Release playtest packages. The latest is
  `dist/vintagehorizons_0.2.1-playtest-near-handoff.zip`, SHA-256
  `89A20E1B48865869FC18D3689EF34C86FD9954BC4A80926B92011F353815BD0D`.
- An implementation-ready chunk-aware hybrid plan covering 3D ownership cells, bounded
  readiness tracking, atomic CPU/GPU publication, coarse fallback masking, seam policy,
  telemetry, benchmarks, rollout, and human acceptance.
- Updated Tier 1/2/3 documentation with 321 passing documentation checks.

## Decisions

- Cached and vanilla terrain should ultimately use exclusive 32x32x32 ownership rather
  than depth bias, sinking, broad fades, or persistent overlap.
- The persistent cache and resident meshes remain independent from current draw ownership.
  Keeping nearby fallback warm avoids unload/reload/remesh thrash.
- A radial distance rule is retained only as a guarded fallback until chunk-aware ownership
  is implemented and accepted.
- Whole cached meshes should be skipped on the CPU when fully replaced. Only mixed sections
  should pay for a readiness-mask lookup, making a performance gain possible rather than
  accepting an unconditional shader cost.
- No assist protocol, section blob, database schema, or persistent cache meaning changes.

## Traps

- Camera-relative coordinates are appropriate for projection precision but are not stable
  input for a world-anchored color or material pattern.
- Configured view distance is policy, not proof that an individual vanilla chunk has a
  drawable mesh. Moving a radial cutoff trades holes against overlap rather than solving
  ownership.
- `IsChunkRendered` is not automatically equivalent to "the replacement mesh has already
  appeared in this frame"; its exact frame ordering must be respected.
- Hiding cached geometry in a fragment shader does not unload it. Mesh buffers remain
  resident and the renderer still pays traversal, uniforms, draw submission, vertex work,
  rasterization, and the early fragment discard.

## Flagged and unverified

- The user described the first render-fixes package as better but reported cached/vanilla
  mixing. That is useful human evidence for the remaining overlap, not separate acceptance
  of every color, geometry, or fallback correction.
- The latest conservative near-handoff package has not yet received human in-game results.
- The hybrid design is approved direction only. No readiness tracker, GPU mask, or CPU
  whole-mesh skip has been implemented.
- The hybrid's visual seam behavior, one-time handoff pop, CPU cost, GPU cost, and net FPS/
  frame-time effect remain unmeasured.
- A reliable public client post-upload/unload notification has not yet been established.
  The plan requires source proof or a bounded polling fallback before ownership code begins.
