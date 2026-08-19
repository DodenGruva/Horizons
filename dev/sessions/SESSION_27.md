# Session 27 — per-chunk terrain ownership

**Date:** `2026-08-19`
**Branch/commit:** `codex/chunk-aware-mask`, branched from the pushed checkpoint `493ae1f` on `codex/main-thread-performance`
**Mod version:** `0.3.0-dev` (0.2.1 remains the released version)
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

Session 26 left the near handoff as a single measured radius and the readiness tracker
runtime-validated. The user evaluated that build in game, found it acceptable, and chose
the per-cell mask over both stopping there and a cheaper CPU-only intermediate step. The
checkpoint was committed and pushed first, and this work branched from it, so abandoning
the direction costs a branch switch rather than an unpick.

Everything here is behind `VINTAGEHORIZONS_CHUNK_MASK=1` or the `.vhmask` command and is
off by default.

## Work narrative

1. **Engine capability, traced rather than assumed.** The public client API binds 2D
   textures and uploads whole textures only; there is no integer 3D texture and no
   subregion update. Decompiling the client's upload path showed it creates when the pixel
   count disagrees with the stored size and otherwise issues `TexSubImage2D` for the whole
   extent, with mipmaps built only on the creation branch. `texelFetch` ignores filtering,
   so a steady-state change is one sub-image call at 3 microseconds, and creation costs
   about 325 microseconds once.
2. **The mask.** One BGRA texel per 32x32x32 cell in a 2D atlas of Y slices stacked down
   the texture, 32 KiB for a 256-block window, addressed by the same wrapped ring the
   tracker uses. Window movement rebuilds it from committed state rather than patching it,
   because a ring slot reused by different world coordinates would otherwise carry another
   place's ownership. Upload failure disables the mask and restores the measured radius.
3. **The saving.** The mask alone measured performance-neutral, exactly as the design
   predicts: it replaces one fragment discard with another and the fragments are rasterized
   either way. Refusing to submit a section whose every cell is committed ready is what
   pays - about 42 of 149 submissions per frame - and a controlled stationary pair measured
   467.9 FPS against 436.2, with residency, selected nodes and evictions unchanged.
4. **A near-miss on the performance claim.** The first mask run reported 536.7 FPS against
   438.6, a 22% "gain" that was entirely an artifact: its own telemetry read `0 meshes,
   0 selected` for three intervals because the client was still loading during measurement.
   Every later run settles to full mesh residency first and reports mesh counts so the
   comparison can be checked rather than trusted.
5. **Two defects found by checks, not by luck.** Ownership was derived from a summed world
   position, and at 512,000 blocks a float32 rounds a fragment 0.03 blocks below a chunk
   edge onto the next chunk, which then owns it. The substring check comparing the shader
   and C# addressing passed throughout, because both expressions are written the same way
   and only their arithmetic differs; a numeric check found 12 disagreements in 72 sampled
   points. Separately, leaving the default dimension cleared the tracker but left the mask
   texels and their authority flag intact.
6. **Human playtest, and what it taught.** The user tested at far above normal flight
   speed. Cached terrain drew over real terrain, terrain occasionally reverted to cached at
   certain angles, and a clear band of missing world appeared when flying backwards.
   Decompilation settled what these were not: `quantityDrawn` only ever increments, so a
   re-tessellation can never make a chunk read as unrendered. All of it was ownership
   latency.
7. **Acquisition.** Every window movement reset the discovery cursor to the start of the
   window, so at flight speed the sweep restarted over near cells and never reached the
   columns the window had just gained - the ground directly ahead. The window now queues
   those columns itself. The probe budget was also shaped for a settled view: a probe costs
   well under a microsecond, so the 256-item cap ended frames with most of the 0.25 ms
   ceiling unused, and that cap only binds while moving. A measured backlog now raises both
   ceilings.
8. **Loss.** Flying backwards outran loss detection, which walked a cursor at 128 cells per
   frame and needed tens of frames to circle the camera, so cells kept reading owned after
   vanilla had unloaded them and the mask suppressed cached terrain nothing else drew. The
   shell now sweeps completely on the frame the camera crosses a chunk boundary.
9. **Near field.** Driving the handoff radius to zero under the mask removed the
   unconditional near suppression the radius had provided, and a benchmark frame showed
   coarse cached geometry intruding where the baseline frame had none. A 48-block floor now
   applies, gated on the camera's own cell being committed ready.

10. **Playtest cycle, four builds.** The user tested per-cell ownership and reported it
   clearly better, with three artifacts, all at far above normal flight speed. Fixing them
   took four builds and the version moved to an incrementing patch number so a symptom
   could be tied to the build that produced it. Ownership acquisition was rebuilt around
   movement (0.3.0), staleness was bounded by a one-second full re-confirmation rather than
   by cursors that kept failing (0.3.1), the diagnostic command was rewritten twice and
   then abandoned as the wrong tool (0.3.2), an empty-chunk ownership rule broke ownership
   entirely (0.3.3), and that was reverted to a measurement (0.3.4).
11. **The diagnostic that did not work.** `.vhwhy` sights along the view vector and reports
   the first cell that would leave nothing on screen. A hole is a screen-space phenomenon,
   so a ray through it mostly passes through legitimately empty air, and the answer depended
   on aim. It produced two readings that were each read as evidence and were describing
   something else. It remains in the build but is not a route worth pursuing.
12. **The failure that mattered most.** 0.3.3 made an empty vanilla chunk own no ground, on
   the reasoning that the tessellator advances the same drawn counter for an empty chunk and
   cached terrain standing taller than the real world therefore sits in cells reported as
   drawn. `IWorldChunk.Empty` is refreshed only when a chunk is modified and the client
   frees block data for packed chunks, so the flag does not mean that on the client. The
   query also sat inside the probe's try block, where a failure is treated as "vanilla is
   not drawing here" - so a misread or a throw did not fail locally, it returned the whole
   world to the cache in one pass. Every cached section drew over vanilla terrain. Reverted
   in 0.3.4 to a counter that cannot influence ownership.

---

## Delivered

- `VanillaReadinessMask`, the renderer's texture lifecycle, per-draw integer origin
  uniforms, and fragment-shader ownership sampling ahead of all shading work.
- Whole-mesh skipping for sections whose every ownership cell is committed ready, in both
  the opaque and water passes, without touching residency or dirty obligations.
- `.vhmask [on|off]`, so the feature can be evaluated without an environment variable.
- Frontier queueing on window movement, a backlog-triggered probe budget, a full
  loss-detection sweep on chunk crossing, and the near-field floor.
- `scripts/test-stop.ps1`, because the shell cleanup script cannot identify processes on
  Windows and silently leaves instances running.
- Evidence under `bench/results/2026-08-19-chunk-mask/`, and the Release tier at 1,307
  assertions.
- Version moved to an incrementing patch number per test build, reaching 0.3.4.
- An ownership audit that checks the per-section counts the whole-mesh skip trusts against
  the cell states they summarise, repairs disagreements, and reports them.
- Periodic telemetry for stale committed cells found, count repairs, and chunks that report
  drawn while also reporting empty.

## Decisions

- Measure before building, twice: whole-column reachability before contemplating the mask,
  and nearest-incomplete distance before rewiring a pixel.
- Branch the risky work off a pushed checkpoint rather than build on top of it.
- Keep the mask off by default. An addressing error would hide the wrong ground and would
  also raise frame rate, so the visual check is the load-bearing evidence.
- Accept brief cached and vanilla fighting on fast approach: it is gain latency failing in
  the safe direction. Reject the equivalent on loss, because a hole outranks an overlap.
- Do not add ownership expiry yet. It would cap hole duration by construction but trades
  that for churn, and the crossing sweep may make it unnecessary.

## Traps

- **Trigger:** comparing a shader expression against its CPU counterpart. **Failure:** a
  text comparison passes while the arithmetic diverges, because float32 rounds differently
  from double at large world coordinates. **Safer:** evaluate the shader's arithmetic in
  the check tier over a grid that includes large origins and boundary-adjacent offsets.
- **Trigger:** caching per-world render state beside a GPU resource. **Failure:** clearing
  the model on a world change while the resource and its authority flag survive. **Safer:**
  clear all three together, defaulting to the safe direction.
- **Trigger:** a discovery cursor over a window that follows the camera. **Failure:**
  restarting it on movement means it never reaches the frontier, which is the only part
  that matters while moving. **Safer:** queue what the window gains, when it gains it.
- **Trigger:** benchmarking a renderer. **Failure:** measuring before residency settles
  reports a large gain for drawing nothing. **Safer:** settle to full mesh residency and
  report mesh counts beside the frame rate.

## Flagged and unverified

Judgement calls awaiting human review:

- The 48-block near floor, the 1024-item / 1 ms backlog ceiling, and the crossing sweep
  size are all chosen, not derived.
- The mask remains off by default; whether it should ship on is unresolved.

Claims lacking their evidence level:

- The +7.3% figure is one controlled pair on one stationary scenario. Benchmark screenshots
  later showed in-game weather differing between runs, which is an uncontrolled variable in
  every pair measured here.
- No moving-route measurement exists with the mask enabled.
- Human testing reports cached water showing chunk-shaped seams with colour differences
  across them, and cached terrain becoming coarser than expected during fast flight.
  Neither is diagnosed; `dev/TODO.md` carries the experiment that separates a mask cause
  from a pre-existing one for each.
- The persistent hole is unresolved. Flying backwards makes a gap that survives standing
  still; `.vhmask off` fills it, so cached terrain is resident and drawable and ownership is
  suppressing it. Four ownership fixes have not closed it. The next signal is telemetry, not
  another guess: `stale committed found`, `count repairs`, and `drawn-but-empty chunks` from
  a session where it reproduces.
- 0.3.4 restored correct behaviour after 0.3.3 broke it, confirmed by the user.
