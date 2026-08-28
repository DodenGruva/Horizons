# Vintage Horizons — completed development work

> Tier 3: append-only completion history moved out of `dev/TODO.md`. Released player-visible behavior also belongs in `CHANGELOG.md`.

## 2026-08-27 - Terrain-only horizon wall correction accepted in 0.3.127

- Corrected 0.3.125's over-broad product behavior. The ambient-manager patch, default clear-air
  lookup, fog blend arithmetic, and fog-density mutation were removed completely. All vanilla
  atmospheric haze is now untouched.
- A fresh clean-room audit of installed official Vintage Story 1.22.7 assets and renderer call sites
  narrowed the eight shader search matches to five actual terrain passes: `chunkopaque`,
  `chunktopsoil`, `chunktransparent`, `chunkliquid`, and `chunkliquiddepth`. General objects,
  instances, entities, sky, and Vintage Horizons terrain retain their own distance behavior.
- The replacement is the smallest safe whole-block shader distance derived from
  `sqrt(viewDistance^2 + 400)`, half a 32-block chunk diagonal, the official ten-block third-person
  maximum plus sub-block rounding, and the earliest terrain fade at 72.5%. It is independent of
  distant-cache reach and changes no saved setting, culling, streaming, or `viewDistanceLod0`.
- Shader activation and the liquid-depth late setter remain the only hooks. Missing targets,
  uniforms, invalid values, callbacks, or variants fail open per terrain pass and diagnose once;
  teardown becomes inert before exact-ID unpatching. The general uniform path remains unpatched.
- 0.3.126 was packaged before final invalid-value and partial-install hardening, so G57 required
  0.3.127. The intermediate zip remains an unplayed rollback artifact rather than being overwritten.
- Warning-free Release and Debug builds, 5,450 fast assertions, and 1,585 documentation checks pass.
  The verified 14-entry
  0.3.127 archive was copy-installed with matching SHA-256
  `5FBEFFCF790B743100A958EA80ED82E9E3C6FA0D02085C6A37B9B61D1A83B886`. The owner played it and
  accepted both the absent terrain-threshold radius and visually unchanged vanilla atmosphere.
  Assist protocol 1, blob format 4, and database schema 6 are unchanged.

## 2026-08-27 - Vanilla horizon fog wall and smoothing circle removed in 0.3.125

- Investigation used only Vintage Horizons and installed official Vintage Story 1.22.7 assemblies
  and shaders; no third-party fog-removal code was inspected or used.
- The visible vanilla horizon wall proved to be two independent effects: the ambient manager's
  default clear-air fog-density contribution and radial `viewDistance` fading in eight shader
  programs. There is no separate wall renderer.
- Version 0.3.125 removes only the propagated default clear-air contribution after ambient blending,
  preserving weather and local modifiers. It gives only the eight explicit fade programs a farther
  visual distance without changing the saved view-distance setting, engine culling/streaming, or
  `viewDistanceLod0`.
- Hooks sit on low-frequency ambient update, shader activation, and the liquid-depth late setter.
  The general uniform-upload hot path is not patched. Missing targets, invalid values, deferral,
  disposal, and install failure all preserve vanilla behavior.
- The owner played the build and reported that it looked great, accepting the ordinary clear-horizon
  appearance. Weather, underwater, lava, local fog, shader reload, deferral, and unload preservation
  are source- and harness-tested only, not separately human-played.
- Warning-free Release and Debug builds, 5,425 fast assertions, and 1,580 documentation checks pass,
  including 37 focused hook and policy assertions. The verified 14-entry package was copy-installed with matching SHA-256
  `C84DBA0CDB035B7B7B8ADA68D9A16CE4FBFC4832E1AADCAC76679CF5A625E3B1`. Assist protocol 1, blob
  format 4, and database schema 6 are unchanged.

## 2026-08-27 - Stutter debt and unfunded renderer candidates closed in 0.3.123-0.3.124

- The owner's tiny sawtooth frame-time pattern reproduced with every mod disabled. Vintage
  Horizons is therefore not its cause. The existing `LodFrameTimeline` already had sub-millisecond
  bins, but review found one real attribution flaw: interval N was paired with callback N rather
  than the callback that preceded it. Version 0.3.123 carries the corrected pairing and reports
  "preceding callback ours"; it changes no rendering behavior.
- The owner reports no 30-second stutter in extensive ordinary testing and smooth join-time warm-up
  after the loading-protocol rebuild. Both runtime-verification debts are retired on human evidence.
  The old 237-MiB warm-up burst remains arena/load-sizing evidence, not an open smoothness symptom.
- A fresh six-view BodanBoys comparison rejected 16-MiB cluster-arena pages. Against 8 MiB, mean
  frame time changed 2.3433 to 2.3300 ms (+0.58% FPS, within repeat noise), observed batches changed
  only 76 to 74, and final live/committed bytes changed 69.42/200 MiB to 69.39/368 MiB. The two
  fewer pages do not justify 84% more committed memory or utilization falling 34.7% to 18.9%.
- Command, section-record, and cull-box layouts total 116 bytes per clustered command. The busiest
  observed frame carried 2,107 commands, about 239 KiB per frame and under 100 MiB/s around 400 FPS.
  The proposed region-anchored record rewrite is not funded.
- Version 0.3.124 adds nonblocking GPU timestamp pairs directly around ordinary, split-near, and
  split-far compute dispatches. They can run inside the existing whole-pass TIME_ELAPSED query,
  never wait for results, discard abandoned dispatches, and are captured by the Windows benchmark
  scenario proof.
- The six-view 0.3.124 route produced thousands of valid samples per interval with zero ring-full
  skips or target conflicts. Split-far culling measured 25 us at both p95 and p99 in every settled
  interval; the largest interval maximum was 108 us. Even deleting the pass would save only about
  1% of the route's 2.3717-ms average frame, and the proposed two-tier test could save only part of
  it while adding its own whole-section work. Two-tier cluster culling is rejected.
- Warning-free Release and Debug builds pass, along with 5,381 fast assertions and 1,575
  documentation checks. The verified
  14-entry 0.3.124 package contains no PDB and was copy-installed with matching SHA-256
  `484F058D54BDD92408C52C9BA4A4EEDE2411653C1587EC80F948DDFAFF592867`. Assist protocol 1, blob
  format 4, and database schema 6 are unchanged.

## 2026-08-27 - Surface-only cave culling accepted in 0.3.121; command clarified in 0.3.122

- The owner narrowed the product requirement to geometry visible from the ground surface. Hidden
  cave connectivity has no value merely because it joins entrances, so route/backbone retention was
  removed from the runtime decision. The proposed background global graph, masks, global fluid
  classification, and invalidation machinery were closed as superseded rather than implemented.
- The shipping rule now combines a terrain-derived 64-block daylight envelope with exact straight
  surface sight. It makes no sea-level assumption, does not follow bends, and stops sight at solid
  terrain.
- Version 0.3.118's first sight pass plugged the middle of a long straight mountain tunnel because
  both mouths were outside the local 3x3 window. Version 0.3.119 preserves an uninterrupted line
  crossing the complete window, accepting that a globally sealed straight corridor can also fail
  open. Recorded as G114.
- Version 0.3.120 added a one-block vertical guard for uneven tunnel floors. On the owner's
  direction, 0.3.121 superseded it with a four-block solid-stopped halo through all 26 directions.
  The final halo cost about 2,250 estimated vertices, 0.15% of a 1,554,740-vertex real-cache sample,
  with effectively unchanged paired harness wall time. Recorded as G115.
- The final reach sweep on that sample measured 26.88% removal at 64, 25.58% at 96, and 22.12% at
  128. Relative to 64, the larger reaches retained 1.30% and 4.76% of baseline geometry. The owner
  chose 64, closing the last global-plan measurement decision.
- The owner reported one same scene looked visually identical while increasing from about 350 FPS
  to about 400 FPS with culling enabled, accepted the tunnel fixes, and directed cave culling to
  default on. This is qualitative one-machine evidence, not a controlled benchmark.
- Version 0.3.122 renames the command from `.vhcaves` to `.vhcavecull` with no old-name alias. The
  no-argument report, `on`/`off` remesh, `VINTAGEHORIZONS_CAVE_CULLING=0` fallback, and client-log
  retention telemetry remain.
- Warning-free Release builds of the mod and benchmark project, 5,368 fast assertions, and 1,570
  documentation checks pass. The verified 14-entry 0.3.122 package was copy-installed with SHA-256
  `F971FC4643016AC2EB5A07C8702453C2E31C01257425BB826E51B805F05D9800`; no game process was launched.
- Assist protocol 1, blob format 4, and database schema 6 are unchanged.

## 2026-08-26 - Retention telemetry shipped in 0.3.116, so a surviving cave names its own reason

- Session 58 required that reason telemetry precede any further cave funding: the aggregate
  `.vhcaves` line could show that caves survived but not which safety rule kept them, and the
  candidate reasons imply completely different fixes.
- `LodCaveCull` now carries a pooled per-cell provenance byte beside its light values and tallies
  every subterranean cell of each window's centre section into six reasons: removed and filled, lit
  by daylight, backbone between known terminals, backbone with an unknown-territory terminal, no
  opaque fill material, and unclassified water. Each row reports cells, share, a face-area estimate
  and cells per section.
- `.vhcaves` with no argument writes the block to the client log under `caves:` because game chat
  cannot be copied; the toggle resets the tally beside the existing timing reset.
- No cull decision changed. The accounting reads the finished classification, walks only the centre
  section so terrain is not counted nine times, and the single ordering change seeds the same cells
  to the same values. `CaveCullChecks` grew from 23 to 36 assertions pinning exact counts.
- Warning-free build, verified and copy-installed 0.3.116 package; SHA-256
  `AE3291B9CA12A0EC5583A197A284B232630DB75AE287767F12A1046A8A15F7D1`. **Not yet run in game**, and
  its live cost was never isolated.

## 2026-08-26 - The global cave classifier priced offline, and route tightening found the real limit

- Built `CaveSpanGraph`, an offline whole-cache prototype of the architecture Session 58
  recommended: maximal vertical air spans as nodes, edges only where adjacent columns' spans overlap
  in Y, exterior above each captured column, contiguous portal-adjacent spans as one portal
  identity, portal-only daylight flood, and a separate fluid graph. Measured over the owner's real
  cache: 4,637 level-0 sections, 27,456,106 air spans, 5,439,699 water spans, 51,310,867 edges.
- **Iteration 1.** The global rule alone removes 30.8% of estimated subterranean geometry against
  the shipping local rule's 30.3% - only +5.4 pp - while keeping 29.5% as undecidable. Cause:
  "unknown" is a property of a whole component, only 0.2% of captured columns sit beside something
  uncaptured, and the largest of 386,066 components holds 18,466,178 of 27,456,106 spans.
- **Iteration 2.** Treating every frontier contact as a pseudo-portal, the stance the shipping rule
  already takes at its own window wall, deleted the undecidable verdict entirely and gained
  **0.2 pp**: route retention rose 9.3% -> 34.2% as that geometry moved intact into the multi-portal
  branch. Attribution was made exact by running the flood and peel twice, once from real mouths and
  once from all terminals, charging the difference to the frontier. Recorded as G111.
- **Iteration 3.** Bounding a route to spans within slack `W` of a shortest mouth-to-mouth path
  roughly doubles removal: **62.9% at W=32 k=2, reach 32** (63.8% with the local rule), route
  retention down to 3.6%, curve flattening below W~=32, and the local/global safety disagreement
  collapsing 79% from 3,018,736 to about 620,000 cells. Route retention proved to be deep interior
  hundreds of blocks from any mouth, not passages. Recorded as G112.
- The sightline/tortuosity variant measured about +0.2 pp and was dropped from the runtime design;
  its fixtures were kept. Enclosed water is worth 0.4 pp - fluid is 57% of subterranean cells but
  7.9% of the geometry.
- Both frontier stances remain selectable and reproduce their whole-cache numbers byte-identically
  across every refactor, which is what makes the iteration comparisons legitimate. `CaveSpanChecks`
  stands at 98 assertions; the full fast tier passes 5,278 with zero failures.
- All figures are harness-measured floors in an upper-bound unit (`faces x 4`, and the flood never
  charges for climbing). Nothing global has run in game.

## 2026-08-26 - Cave-culling sign defect fixed and human-proven in 0.3.114

- Source-traced Session 57's in-game reversal to inconsistent geometry coverage. `LodCaveCull`
  rebuilt the current section with selected air filled, while `LodMesher` compared vertical faces
  with the original current/neighbour snapshots. Filled adjacent cave columns therefore saw old air
  and manufactured internal and cross-section walls.
- Added `LodCaveCull.Prepared`, which owns the rebuilt self snapshot and the matching 3x3 boundary
  classification for one mesh build. Internal comparisons now read the rebuilt self; external side
  collection folds in effective filled coverage from the immediately adjacent neighbour column.
- Changed `cavefield --shipping` to exercise `LodMesher.BuildMesh` with culling enabled instead of
  pre-filling away the integration boundary. Added multi-column and cross-section seam regressions.
- Human-validated at the same settled 1,730 meshes: 88,606,884 to 66,372,948 opaque vertices
  (-25.1%) and 1,895.7 to 1,427.7 MiB (-468.0 MiB). The owner reported that it works. This closes
  Session 57's top-priority sign defect and supersedes the greedy-colour lead as its diagnosis.
- Warning-free build and verified, copy-installed 0.3.114 package; SHA-256
  `AED61A89F84528B52FFBC1595C1C4EF77255BD1F873A69C42B364E9BE26A7871`.

## 2026-08-26 - Local coarse/backbone cave refinement measured and bounded in 0.3.115

- On new branch `codex/cave-culling-refinement`, extended the light classifier through L4-L6 with
  an approximately four-column effective reach and replaced whole-component restoration with a
  conservative terminal-to-terminal bridge-tree backbone. Added an opaque-only fill safeguard
  after the first L3 audit found one water fill.
- The cave suite passes 23 assertions; the complete Release fast tier passes 5,167 and the build is
  warning-free. Real-cache L4/L5/L6 sampling removed only 1.7%/0.5%/0.3%. No audited coarse fill
  used water or thin material after the safeguard.
- Human live result at the same settled 1,713 meshes: 86,884,340 to 64,791,952 opaque vertices
  (-25.43%) and 1,858.7 to 1,393.7 MiB (-465.0 MiB). This is only roughly 0.3 percentage points
  beyond the accepted 0.3.114 result and matches the owner's report that the refinement made little
  apparent difference.
- First cave-worker cost measurement: 1,713 sections, 44.984 ms mean and 149.648 ms maximum for the
  complete preparation pass, about 77 seconds aggregate worker CPU. It runs in parallel and cannot
  be attributed solely to the refinement because 0.3.114 lacked the same timer.
- Source review of surviving dry/flooded caves established the approach boundary: a recentered
  3x3 fail-open window cannot prove a large network globally sealed; frontier patches are not real
  portal identities; wide natural cave topology rarely exposes removable bridges; and water is
  stored as occupied geometry rather than cavity air. A global cross-section vertical-span graph,
  real surface portal identities, separate fluid connectivity, conservative coarse masks, and
  reason telemetry are the recommended next direction. None is implemented or approved.
- Verified, copy-installed 0.3.115 package; SHA-256
  `69C6E8FAA2B0D3E2AFCAB3B385FC6E68A4398B507167A890FC425F194FEB9671`. Source remains uncommitted.

## 2026-08-26 - Aggregate subtree bounds shipped and answered in 0.3.107

- `LodSubtreeHeights` gives every quadtree node the combined vertical extent of the meshes resident
  beneath it, maintained by a bottom-up recompute of the changed node's ancestor chain on each mesh
  publish and removal - at most seven nodes of four child lookups. Recomputing rather than
  accumulating is what makes removal exact, since a union has no inverse. The aggregate covers
  RESIDENT meshes, which is safe because only a node with a mesh is drawn and mesh demand comes
  from the radial planner rather than this walk (G8).
- Measured in game over five views: 2 to 20 nodes rejected per frame against the 33-74 the walk
  already rejected horizontally, worst single frame 131.
- **Closed as answered rather than pending.** At a leaf the aggregate box contains the per-section
  box shipped in 0.3.58, so it can only reject a subset of what that test already rejects; three of
  five views rejected only single-mesh nodes, and the one that rejected real branches was pointed
  at sky and drew 2 sections. The quadtree walk it speeds up costs 44.3 us of a roughly 2000 us
  frame. Kept because it is free and keeps those leaves out of the draw list. See G103.
- Deliberately NOT used in `AllVisibleChildrenCovered`, which decides whether a parent may stop
  drawing its coarse mesh over a quadrant rather than what is drawn. A child holding only a deep
  cave mesh would be rejected vertically, the parent would descend believing the quadrant covered,
  and the surface would become a hole. Pinned by
  `StaticAssetChecks.SubtreeBoundsStayOutOfTheCoverageGate`.
- `.vhsubtree` reports what the aggregate rejected and resets its counters for a single view;
  `.vhsubtree heights` reports the floor and ceiling of every RESIDENT mesh, which the periodic
  `section heights:` line cannot do because it measures meshes published in an interval and a
  settled world publishes none.

## 2026-08-26 - The tall-culling-box cause is not phantom seam walls

- Settled reading over 1,713 resident meshes: mean 188.9 blocks tall, 49.2% of the 384-block world,
  mean floor y=19.7, and 81% of floors below y=32. Floors rise from y=5.7 during fill-in, so seam
  repairs do work.
- **Disproved:** that walls left by sections meshed before their neighbours arrived were dragging
  the boxes to bedrock. Only 10.6% of resident meshes still carry a guessed edge against 37% with
  floors at bedrock, so missing neighbours cannot account for most of it. The cause is buried cave
  geometry, which is what sent the session to `LodCaveCull`.
- The earlier y=5.7 figure was a warm-up measurement: the periodic line's only ever-reported
  interval was the first thirty seconds after joining.

## 2026-08-25 - Packed timing and selected-only regional retention complete in 0.3.105

- Ran a controlled expanded/packed/packed/expanded matrix with clusters pinned off so packing was
  the only changed renderer stage. All 24 viewpoints settled without timeout. Expanded averaged
  2.4392 ms and packed 2.4367 ms; the -0.10% delta is below repeat variation, establishing parity
  rather than a speed claim.
- Updated the Windows runner for current split-near/split-far GPU telemetry, upload tails, and each
  regional arena's live/committed bytes. Hash-bound CSV evidence is tracked under
  `bench/results/2026-08-25-phase9-packed-memory`.
- On the owner's decision, the regional mirror now retains only the selected expanded,
  whole-packed, or clustered-packed representation. Missing selected data or any allocation,
  upload, shader, or draw refusal uses the ordinary per-section legacy mesh rather than requiring
  another regional copy.
- The route's product-default clustered copy was about 90.33 MiB live against about 833.7 MiB for
  all three old regional copies, removing roughly 89% of duplicate regional live bytes. This is not
  total-process memory savings because the legacy mesh fallback remains resident.
- Warning-free Release build and 5,104 assertions pass. The verified 0.3.105 package has SHA-256
  `13975952ED56D178B0AE61571B9164682F9F36746199954A8F9D1EC1FEFAE9AE` and was copied beside the
  preserved rollback builds. No wire, blob, or schema meaning changed. Phase 9 settings/lifecycle
  and forced-legacy coverage remain.

## 2026-08-25 - Phase 9 failure-injection surface built in 0.3.104

- Returned the development identity from the premature 0.4.0 promotion to 0.3.104, the next patch
  after the last 0.3.x artifact. No changed binary reused 0.3.103.
- Added one-shot arena, fast-shader, depth-copy, and indirect-draw failure injections plus an
  isolated-runner parameter that records the chosen boundary in `scenario.json`.
- The injections enter existing fail-open routes rather than issuing deliberately invalid GL:
  arena/shader/draw select established rendering, while depth-copy failure leaves the complete
  indirect candidate set unculled.
- Warning-free Release build and 5,083 fast assertions pass. The verified 0.3.104 zip was copied
  into the Mods folder without removing 0.3.103 or 0.4.0. Because 0.4.0 sorts higher, an ordinary
  launch will still select it.
- All four injections passed the frozen six-viewpoint `bodanboys` route with zero settle timeouts.
  Arena refused setup with legacy unchanged and then recovered on retry; shader stayed established;
  depth-copy disabled HZB while packed multi-draw continued; draw disabled both indirect drawers
  for the session while expanded/established rendering completed the route. This is automated
  game-backed evidence on the primary AMD system, not human-watched or cross-driver evidence.

## 2026-08-25 - Two GPU evidence tasks retired by owner decision

- Retired, without completing, the corrected-mapping suppression/FPS re-measurement. The older
  ridge figures remain historical and cannot be quoted as the current 0.4.0 effect size.
- Retired, without completing, the second-GPU/driver run. Acceptance remains specific to the
  primary AMD driver and no portability claim follows from the scope decision.
- Neither retirement closes Phase 9. The paired packed route, final regional-memory policy,
  settings/lifecycle coverage, injected failure fallback, and representative forced-legacy run
  remain the plan's hardening work.

## 2026-08-25 - GPU terrain renderer accepted in play and promoted to 0.4.0

- The owner played 0.3.103, the first build drawing cached terrain through the GPU path by default
  and the first without the 0.3.99 sky guard, and reported the picture correct and performance a
  large improvement. This closes the visual and motion gate on the primary driver in ordinary play -
  the gate that had failed repeatedly since Phase 6.
- Recorded as qualitative acceptance on one machine with no figure attached, because none was
  measured. Every suppression and frame-rate number in the repository still predates the 0.3.101
  mapping fix and remains flagged as historical rather than current.
- Verified before promoting that none of the retired diagnostics is required by any open TODO item.
  All thirteen environment overrides survive, as do the live cull telemetry, the GPU stage timings,
  the section-height distribution and vertical-cull counters, the distance-band report and the
  offline `HzbField` harness. Only switches were removed. The subtree-vertical-bounds item will want
  a new toggle of its own, which is new instrumentation rather than a resurrection.
- Recorded the two things that did become slower to diagnose: isolating culling from batching now
  needs an environment variable and a restart rather than one command, and a flicker recurrence
  would mean restoring the capture from git.
- Version 0.4.0 promoted from the accepted build with no code change beyond the two version strings,
  packaged, verified and installed copy-only. `CLAUDE.md`'s patch-only versioning rule was amended
  to name owner-directed milestone promotions rather than being silently broken.
- Still open and unchanged by the acceptance: a second driver has never run the fast path, the
  paired packed route is untimed, and no current suppression or frame-rate figure exists.

## 2026-08-25 - The GPU terrain path becomes the default and the staging scaffolding is removed

- The owner read 0.3.102's new counter: 169,994 perimeter-guard refusals over 2,495,527 sampled far
  commands (6.8%), against 660,943 culled (26.5%). Every refusal was a command the depth test had
  already proved hidden and the guard drew anyway, so removing it takes far-command suppression to
  33.3% in that sample. Session 51 predicted zero; that prediction was wrong and is withdrawn. With
  the rectangle corrected the guard inspects a genuinely outside ring of texels and trips on any far
  piece with exact clear sky within one texel of its outline, which near a ridge is common. The
  guard is deleted.
- The same log confirmed 0.3.102 healthy on the primary driver: 0 degenerate verdicts, meaning the
  base-size fail-open passes rather than misfires, and 34,868 of 34,868 pyramid builds completed.
- It also corrected the display assumed by sessions 50 and 51. The owner runs 1920x1080, so the
  texel-mapping defect began at pyramid level 4 vertically and also affected width from level 8 -
  wider than the 2560x1440 write-up implied. G96 now states the rule and uses the real display.
- On the owner's decision, every stage of the path defaults on: regional arenas, batched indirect
  drawing, the depth pyramid, GPU culling, the same-frame near/far split, packed quads and clusters.
  Environment overrides flipped from opt-in to opt-out so the benchmark harness can still pin either
  side of a controlled comparison. Capability, shader, allocation and draw failures still select the
  established renderer in the same frame.
- Eight staging commands were retired with the code behind them: `.vhphase8`, `.vhindirect`,
  `.vhpacked`, `.vhclusters`, `.vhcull`, `.vhlate`, `.vhheight` and `.vhflicker`, plus the phase-8
  preset helpers and the entire armed flicker-capture machinery - shader buffer and uniform, the
  eight-slot fenced readback ring, the CPU transition tracker and its checks. `.vhgpu` remains as
  the single saved in-game off switch and `.vhhzb` remains for reading the depth report.
- Checks were inverted rather than dropped: fresh-install defaults now pin ON with the reasoning
  that changed, the retired commands are pinned absent, and the surviving switches pinned present.
- Version 0.3.103 packaged, verified and installed copy-only. Built and harness-tested only, and
  taken deliberately ahead of Phase 9's second-driver and motion gates.

## 2026-08-24 - Post-fix cleanup: rejected diagnostic removed, sky guard made accountable

- `.vhsplitbias`, the far-bucket-only depth-margin search, is removed together with
  `DepthBiasForSteps` and `MaximumDiagnosticDepthBiasSteps`. Its 4/16/64/256 comparison was
  answered by finding the real cause in 0.3.101; both buckets use the established four-step margin,
  which was already the default, so no shipped verdict changed.
- The 0.3.99 one-texel sky perimeter guard is retained but now counts its own refusals. A word
  appended after the distance bands in the live cull telemetry records every far cluster the
  perimeter refused to hide, and `.vhhzb` prints it even at zero. Deleting the guard is now gated
  on one ordinary session reading zero rather than on the argument that the mapping fix made it
  redundant.
- The box test fails open when `textureSize(hzb, 0)` is not `ivec2(screenWidth, screenHeight)`.
  Pixel anchoring reproduces the pyramid's mapping only while those agree; the check sits in the
  shader because the C# call site passes the pyramid's own fields and could only compare a value
  with itself.
- The cull program's ten uniform locations resolve once at link time instead of by name per
  dispatch.
- The `HzbField` widening figure was re-measured against the corrected mapping and the earlier
  same-day annotation calling `25.0%` an overstatement was withdrawn as unmeasured. Four runs gave
  18.4% and 25.6% at a 350-block modelled vanilla distance and 16.2% and 23.8% at the configured
  192 blocks. The camera seed alone moves the figure about seven points, so the old value sits
  inside its own noise; it is now recorded as a range with its conditions, and the choice of eight
  texels rests on the wide-4/wide-8/wide-16 ordering that held in every run. Sixteen added 3.9 to
  5.4 points over eight rather than the 2.5 previously recorded. G98 carries the trap.
- Version 0.3.102 packaged, verified and installed copy-only. Built and harness-tested only: it
  changes the culling shader, and every failure path disables culling rather than hiding terrain.

## 2026-08-24 - Phase 8 precise-angle flicker diagnosed from source and fixed

- The depth pyramid halves each level with floor and folds the leftover odd row and column into its
  last texel, so a level-L texel covers exactly `2^L` screen pixels. Both copies of the occlusion
  box test scaled the projected rectangle by the level's texel COUNT instead, which is equivalent
  only while `levelSize * 2^level == screenSize`. At 1440 rows the chain reaches 45 and then 22, so
  from level 6 up the sampled rectangle fell one texel short at its far edge and skipped precisely
  the texel holding whatever lay beyond an occluder's silhouette. Terrain peeking over a ridge was
  declared occluded, against invariant 8. Screen width was unaffected because 2560 divides exactly
  at every selectable level, making the fault vertical only and pitch-sensitive.
- Under the same-frame split, one wrongly suppressed near command leaves the exact `1.0` clear
  value where its pixels belonged, and every far cluster whose rectangle covers that hole flips
  wholesale between `occluded` and `background` as sub-texel noise decides the near verdict each
  frame. That single mechanism accounts for the null margin ladder, the failed one-texel perimeter
  guard, why clusters multiplied the affected spots, and why `.vhcull off` stopped everything.
- The rectangle is now anchored in screen pixels and shifted down by the level, with the far edge
  clamped to the last texel so the odd-dimension fold is honoured, applied identically to the GLSL
  test and its C# mirror. The change can only widen the sampled region, so it strictly reduces
  hiding and cannot cause missing terrain.
- Gated on reproduction: a new fixture reduces a real 1440-row pyramid and pins the peeking box,
  the folded remainder, two exactly-divisible control levels, the nine-texel sampling boundary and
  a box in front of the occluder. The unfixed code failed exactly that one case out of 5,089
  assertions before the fix was written.
- Version 0.3.101 packaged and installed; the owner ran it and confirmed the precise-angle flicker
  is gone. G96 records the texel-footprint rule and G97 that a reference implementation mirroring a
  shader cannot catch a shared assumption.

## 2026-08-24 - Phase 8 value proved and flicker capture narrowed to real draw-state changes

- A controlled adjacent comparison measured 386 FPS under both `late` and `cull`; the known
  precise-angle flicker occurred only under `late`. This localized the artifact to far cached
  commands testing against the same-frame near cached depth, without rejecting the split or the
  cluster units that make cached-on-cached occlusion useful.
- Version 0.3.96 exposed a live far-only 4/16/64/256-step margin ladder. Every value behaved alike
  and clusters merely made more affected spots visible, rejecting a simple near-equality bias.
- Version 0.3.97 measured the exact live command stream rather than the separate whole-section
  shadow population. In the owner's ridge/end-of-map view, split-near removed 53.3% of commands
  and 52.9% of indices; split-far removed 80.5%/77.1%, reaching 92.4%/94.8% at 4-8k. FPS rose from
  roughly 340 to 460. The earlier 9.8% shadow figure was a different population and view, not a
  broken cull path.
- Version 0.3.98 added an explicitly armed, fenced per-command capture. A stationary run recorded
  8,416 split-far samples and 27,065,856 observations with no dropped readbacks or matrix drift.
  Its leading fixed identities alternated between `occluded` and exact-clear `background`, never
  ordinary visible depth.
- Version 0.3.99 added a one-texel clear-sky refusal around only the split-far test. The owner found
  the flicker still bad and clarified that most affected meshes are inside terrain, not at a sky
  silhouette. Its report also ranked 6,592 draw-safe `visible/background` transitions above 8,666
  real cull-state transitions. The sky guard is therefore a failed hypothesis, not a correctness
  result. G95.
- Version 0.3.100 captures split-near and split-far independently, tracks command presence before
  compute separately from verdict changes, ranks only transitions that can change drawing, and
  labels each offender by projected screen region. It remains view-wide because the exact camera
  angle generally cannot also place the defect under the crosshair. Eight asynchronous slots keep
  the diagnostic nonblocking and dormant until armed.
- A proposed texture barrier was withdrawn before implementation: the pyramid reads and writes
  disjoint, explicitly clamped mip levels, so the same-texel feedback requirement was not shown.
  A partially implemented crosshair capture was also fully reverted before the 0.3.100 build.
- The complete fast tier passes 5,073 assertions. `vintagehorizons_0.3.100.zip` was verified and
  installed without launching the game. Human capture and diagnosis remain open; no flicker fix is
  claimed.

## 2026-08-24 - Cache startup, panoramic refinement, appearance, and near priority accepted

- Source tracing turned a 75.1-second first-mesh join into a zero-work bootstrap fault: persisted
  rows provided keys, but the renderer refused to run the traversal that created their first load
  and mesh obligations. Out-of-frustum rejection separately made camera direction the demand
  authority. G92-G93.
- Version 0.3.92 introduced exact-row availability and a bounded eight-lane radial demand planner
  before the empty-mesh return. Camera orientation is absent; coarse coverage and nearer refinement
  advance as an outward wave under the existing asynchronous/no-hole policies.
- Version 0.3.93 added an independent under-player foundation through L0 after the owner found that
  moving could expose coarse cache beneath vanilla terrain. The owner accepted the spatial loading
  and sharpening behavior.
- Version 0.3.94 fixed the appearance race exposed by faster loading: late tint slots bypass the
  30-second seasonal cadence, and affected meshes retain their exact obligation until every tint
  they use is published. The owner accepted the proper-colour first reveal. G94.
- Version 0.3.95 gives the closest foundation 24 of 32 unresolved slots and six of eight new
  requests per frame. The outward reservation keeps eight/two, including one outstanding slot per
  radial lane. The owner reports everything good and accepts the final pacing.
- The complete fast tier passes 5,013 assertions; 0.3.95 was packaged, verified, and installed.
  Assist protocol 1, blob format 4, and database schema 6 are unchanged. Phase 8 resumes next.

## 2026-08-24 - Phase 8 cluster experiment and controlled preset ladder built

- Workers produce a second 4x4 packed stream with exact contiguous ranges and conservative local
  bounds. A separate bounded arena publishes it, and up to sixteen independently culled commands
  retain whole-section shader addressing and complete same-frame fallback.
- The first owner scene improved from roughly 360 to 390 FPS, but precise-angle flicker affected
  more normally shaped pieces. `.vhcull off` stopped every old and new case while clusters remained
  enabled, clearing geometry/range publication and isolating the shared HZB verdict.
- The CPU reference and both compute uses gained a four-step normalized 24-bit fail-open band,
  pinned by the existing one-ULP self-occlusion fixture. The complete cluster stack still flickered
  in the first valid 0.3.89 comparison and lost to the complete legacy baseline; this rejects the
  composition, not the earlier staged wins.
- Seven live controls became the cumulative `.vhphase8 off`, `batch`, `cull`, `late`, `packed`, and
  `clusters` ladder. Every preset assigns all prerequisites, reports/logs the exact state, and
  preserves filled arenas while moving among active stages. Only crossing `off` attaches/releases
  arenas. G89-G91.
- The first `late` result retained only the single older flickering section, not the numerous
  cluster flickers. Its 362 FPS observation is invalid because the 0.3.90 command unnecessarily
  re-meshed 1,678 sections; 0.3.91 fixes that transition but remains unplayed.
- The cluster suite contributes 1,048 focused assertions; the full fast tier passed 4,980 at that
  boundary. The cache startup/refinement priority is now resolved; Phase 8 resumes at `late` versus
  `cull`.

## 2026-08-24 - Phase 7 indexed packed quads accepted on the primary driver

- Greedy opaque rectangles now have an exact 12-byte regional form rather than four expanded
  vertices plus six indices (88 bytes). Workers emit it directly and a bounded companion arena
  publishes it behind `.vhpacked`, with expanded batching and the established renderer retained as
  same-frame fallbacks.
- The first draw-arrays backend in 0.3.85 matched the picture but reduced the owner's same-scene
  frame rate from about 300 to 260 FPS because it decoded six unique shader vertices per quad.
  That topology is rejected.
- 0.3.86 reuses one `0,1,2,0,2,3` index pattern and decodes four unique corners. The owner reports
  exact visual and FPS parity with packing off and on, accepting the format and draw topology on the
  primary AMD driver. Packing is neutral rather than a standalone FPS optimization in this scene.
- 3,911 fast-tier assertions pass, including 782 packed-format checks. Cross-driver evidence,
  paired-route telemetry, and removal of the temporary expanded regional mirror remain open; the
  next implementation branch is Phase 8 cluster subdivision.

## 2026-08-24 - Phase 6 same-frame split accepted

- Opaque cached terrain is divided into near and far buckets. The depth pyramid is rebuilt after
  the near bucket and the far commands are classified against that same-frame picture.
- The previous-frame path's stale-picture policy, camera-delta re-base and turning guard are
  removed rather than tuned; their only purpose was to make an older picture usable.
- The owner played 0.3.84 and reported that the flickering is gone and it runs very well. This
  closes Phase 6 on the primary machine. Non-AMD coverage and controlled timing remain ordinary
  portability/performance debt, not reasons to reopen the stale design.

## 2026-08-24 - Phase 6 measured, built, played, and redirected

- **Phase 5's cost and correctness halves are both closed.** Culling is confirmed on hardware
  (`67008 dispatches over 1733784 commands, last frame's commands were culled on the card`), the
  depth work costs about 25us of GPU time a frame, and the picture has been human-played across
  four viewpoints - including a bird's-eye view of 27,000 sections with no legitimate occlusion
  available, where it suppressed none. A non-AMD driver and the head-to-head against delayed
  occlusion remain open and are recorded in `dev/TODO.md`.

- **The GPU arena ceiling is derived from the player's draw distance.** The fixed 256 MiB was
  refusing 322 sections and capping the batched path at 35% coverage; the owner measured 300 to
  480 FPS after the change, at 100% coverage with zero failures. G81.

- **GPU timing is available in an ordinary session**, and a pass that did not run no longer files
  a near-zero sample. G80.

- **The classify pass no longer discards the work it pays for.** It read 2 of 2,212 dispatches at
  149us a frame; it now keeps its fence until signalled, never dispatches while a result is
  outstanding, and costs about 1us. G79.

- **Phase 6's option 3 (previous-frame depth) was built, measured and then rejected.** From an
  ordinary hilltop it turned 0% hidden into 8/20/35% by band at the same cost, which is the
  strongest result the phase produced. It was rejected anyway: the owner saw mid-screen flicker
  while turning, and mid-screen means staleness is not containable by a guard. Phase 6 continues
  with the near/far split. The pyramid, cull shader, classifier, arena sizing and most guards
  carry over; the turning guard and the camera-delta re-base do not. G82.

- **The offline harness gained the modes that made the choice arguable without a playtest**:
  `--occlude-all`, `--motion b,d`, a self-occlusion meter, and a stale-versus-fresh safety
  comparison whose floor is exactly zero by construction.

## 2026-08-23 — Phase 3b closed, Phase 4 answered, Phase 5 built and culling frames

- **Phase 3b (section vertical extent) is complete and human-played.** Sections record how
  tall their drawn geometry is and the established renderer culls with that instead of a
  bedrock-to-sky box. Shipped 0.3.58; the open TODO section that tracked it is retired.
- **Phase 4's open question is answered.** Wider sampling beats cluster subdivision by roughly
  two to one and changes only the test. Measured offline over the owner's real cache at his
  own view distance, two seeds, 128 views, and then confirmed in game: 248 of 404 hides exist
  only because of the widening.
- **The measurement moved off the owner's machine.** `HzbField` reconstructs the in-game
  measurement from a cache database with no game process, in under a minute, and reproduced
  the game's own 0-1k figure to within a point at matching settings.
- **Phase 5 is built and has drawn culled frames.** Same-frame suppression - the card zeroes
  indirect draw commands between their upload and the multi-draw - confirmed active on an RX
  9070 XT. Its cost/benefit gate remains open; see `dev/TODO.md`.
- **Fixes carried along:** the GL state guard restores both indexed SSBO slots rather than one;
  the widening counter no longer sits behind a gate that made it structurally zero; `.vhcull`
  writes its status to the log; the batched path releases the occlusion-query objects it had
  been retaining unused.

## 2026-08-17 — documentation foundation

- Reviewed the supplied source with a main-thread performance lens.
- Associated the working directory with the user's GitHub fork as `origin`.
- Located and adopted the lifetime-tiered documentation workflow used by the Layout project.
- Preserved the supplied design and M4/M5 status documents in a dated superseded archive.
- Established the working agreement, architecture, gotchas, current status, TODO, compatibility ledger, plan, session records, and documentation checker.

## 2026-08-17 — fork reconciliation

- Matched the supplied source to fork commit `27e5e6a` by file hashes.
- Attached `codex/main-thread-performance` to `origin/master` release 0.2.1 at `f8d4b03`.
- Preserved pre-reconciliation files, restored the fork's newer source/tests/scripts, and retained the tiered documentation changes.
- Rebased the performance plan and current status around fixes already present in 0.2.1.
- Made the documentation checker portable across Windows PowerShell 5.1 and PowerShell 7; both hosts pass 153 checks.

## 2026-08-17 — main-thread instrumentation and asynchronous mip propagation

- Repaired the Windows SQLite stale-version fixture and completed all fast suites against the installed game.
- Added low-overhead percentile/hitch telemetry for client tick, pipeline, and render phases plus projection-reset and upload-byte counters.
- Added a Windows-native isolated rendered benchmark runner with pidfile safety and graceful client/server shutdown.
- Reproduced synchronous mip propagation at 20–22.5 ms p95, 32.5–35 ms p99, and 103.1 ms maximum on the game tick.
- Moved mip boundary sorting/merge construction to a bounded dedicated worker with world epochs, content revisions, stale/failure retry, and parent pins.
- Added regression checks for immutable worker construction and child mutation during in-flight work.
- Completed two before and two after route runs; after runs ended with no ≥25 ms game-tick hitches and no mip backlog/errors.

## 2026-08-17 — incremental discovery and retry-safe requests

- Moved integrated-singleplayer sibling-cache key enumeration to a dedicated read-only SQLite connection and below-normal reader thread.
- Added coarse background scans that publish only newly discovered keys in bounded immutable batches; unchanged scans produce no game-thread work.
- Applied each incoming server manifest chunk once and stopped re-enumerating the retained remote-key set every tick.
- Replaced implicit local-offer bookkeeping with explicit installed, retryable-miss, and unavailable outcomes.
- Restored retryable server requests to the owning pipeline with a monotonic cooldown and bounded roughly one-minute retry window.
- Cleared queued manifests between worlds and stopped the local reader during both normal world leave and mod disposal.
- Expanded the full game-backed fast tier to 728 passing assertions.

## 2026-08-17 — cached bounds and stable projection

- Replaced the per-frame scan of every opaque mesh with cached horizontal world-space
  bounds covering both opaque and water mesh keys.
- Expanded bounds in constant time on installation and deferred one exact rebuild after an
  extreme mesh removal; ordinary far-distance calculation is independent of mesh count.
- Quantized the applied camera far plane upward in 512-block steps, with immediate growth
  and a five-second stable cooldown before shrink.
- Preserved the continuous shader far edge, vanilla-view safety margin, explicit `.vhfar`
  cap, and world teardown reset.
- Added 30 focused assertions and expanded the full game-backed fast tier to 758 passing
  assertions across 19 suites.
- Built and structurally verified an ignored local Release ZIP for human playtesting.

## 2026-08-17 — smoothed periodic work and bounded client installs

- Replaced one-second sweep and transient-generation batches with 50 ms fractional
  allowances, 1 ms issue deadlines, and at most 16 new probes per tick.
- Replaced one-second server-assist serving with fair per-player/global allowances and a
  2 ms serving deadline; explicit unavailable refusals now drain gradually too.
- Added delayed-tick-safe fractional credit so configured rates remain accurate without
  releasing catch-up work after a slow tick.
- Time/byte-bounded server arrivals, integrated-singleplayer foreign blobs, and completed
  background loads at 2 ms / 512 KiB per owning-thread pass.
- Guaranteed one oldest FIFO item can progress even when it alone exceeds the byte limit.
- Added interval item/byte, pending-byte, and oldest-age telemetry for concrete install
  queues plus stable structural byte estimates for background-loaded sections.
- Added 29 focused assertions and expanded the full game-backed fast tier to 802 passing
  assertions across 21 suites.

## 2026-08-17 — off-thread foreign decode and allocation telemetry

- Moved network and integrated-singleplayer foreign blob inflation and structural parsing
  from the game tick to separately bounded queues on the storage owner.
- Kept live block resolution, classification, recolouring, skip filtering, and publication
  on the owning thread under the 2 ms / 512 KiB install policy.
- Added world epochs, failure isolation, local-win rejection, and request-slot retention
  through actual publication.
- Preserved the foreign reload fallback until storage acknowledgements can prove an
  adopted row durable.
- Added opt-in, per-owner managed-allocation totals and worst-call deltas for client tick,
  pipeline, and render phases without charging counter reads to phase elapsed time.
- Added 75 focused assertions since Session 6 and expanded the full game-backed fast tier
  to 877 passing assertions across 21 suites.
- Built a local test ZIP; a brief human playtest reported a noticeable subjective
  improvement, without a controlled before/after measurement.

## 2026-08-17 — continuous benchmark route and camera correction

- Extended the benchmark route format with elapsed-time position and camera trajectories
  while preserving fixed-waypoint compatibility.
- Added a four-leg, 1,600-block continuous movement route with one full camera turn per
  leg and regression checks for interpolation, angle mapping, and loop continuity.
- Corrected the harness from a false zero-centred pitch assumption to Vintage Story's
  PI-centred camera representation and pinned both mouse axes.
- Reclassified old sky-biased route evidence: its capture/pipeline/mip comparison remains
  useful, but its render load, screenshots, and visual claims do not.
- Completed corrected terrain-facing static and moving integration smokes with graceful
  isolated shutdown and no mod errors or tick hitches.
- Preserved the four short mip before/after CSVs under `bench/results` with their evidence
  limits and kept the large reproducible sandbox ignored.
- Made the Windows runner's PowerShell 7 requirement explicit.
- Added 23 benchmark-route assertions and expanded the full fast tier to 900 assertions
  across 22 suites.

## 2026-08-17 — continuous route evidence and bounded capture publication

- Ran and preserved the corrected 30-second-leg movement/rotation route with one warm-up
  and two measured laps, including hardware, graphics, view-distance, mod-config, and CSV
  context.
- Reproduced capture publication at 12.038 ms maximum and established it as essentially
  the whole worst measured game tick on that route.
- Time/byte-bounded capture publication at result boundaries under the shared 2 ms /
  512 KiB policy while retaining the eight-result ceiling and oldest-item progress.
- Counted queued/in-progress capture jobs plus completed/deferred results under one
  24-item backpressure cap and made ordinary scheduling respect the exact remaining capacity.
- Added result item/byte/age telemetry, worker-side raw-run byte estimates, and world-epoch
  rejection for results published after teardown.
- Repeated the full route: capture maximum fell to 5.732 ms, backlog stayed within 9
  results / 0.70 MiB / 93 ms, and zero ticks reached 25 ms.
- Preserved both full-route CSVs and their evidence limitations under `bench/results`.

## 2026-08-17 — warm-cache route classification and human review

- Established that the full Session 9 route crossed terrain already present in the VH
  cache and reclassified its evidence as warm-cache traversal rather than new exploration.
- Preserved the measured capture-publication comparison because both runs directly
  reported live capture results and owning-thread publication cost.
- Recorded the human verdict that movement and rotation looked good and smooth with no
  noticed clipping or turn-around stalls on that route.
- Closed the warm-cache visual-review task and replaced it with explicit unseen-terrain
  validation whose pre-run cache absence must be proven.

## 2026-08-17 — uncached capture-frontier evidence

- Replaced a self-overlapping cold-cache loop scenario with a one-way 1,600-block route
  that continues the capture frontier for roughly 1,344 blocks beyond its initial streaming
  footprint.
- Added an opt-in post-measurement endpoint cooldown so queue convergence can be observed
  without changing recorded frame samples or existing benchmark defaults.
- Completed two independently reset client-cache runs with zero VH game ticks at or above
  25 ms; worst ticks were 15.790 and 10.950 ms.
- Kept capture backlog within 20 results / 1.61 MiB / 234 ms and 11 results / 0.89 MiB /
  62 ms; the cooldown established zero pending capture, mip, render, save, and storage work.
- Preserved both CSVs and scenario limitations under `bench/results`, including the human
  correction that compact later legs can overlap coverage produced earlier in the same run.

## 2026-08-17 — server telemetry and instrumentation overhead

- Added p95/p99/max, hitch, managed-allocation, queue-depth, and oldest-age telemetry for
  the server capture pipeline, sweep, transient generation, and assist serving.
- Added isolated runner controls for server-mod scenarios, auto-command generation starts,
  and genuine stats-disabled A/B runs.
- Separated auto-unpause from allocation telemetry so unattended stats-off runs are real.
- Added a stationary route and preserved three alternating on/off pairs. The two warmed
  pairs measured about 0.7% lower average FPS and 1.0% lower median FPS with stats at
  roughly 445 uncapped FPS; inconsistent 1% lows support no tail claim.
- Completed a server-mod sweep smoke that emitted the new interval lines and shut down
  gracefully; assist blob/send and generation paths remain runtime-unexercised.

## 2026-08-18 — warm-cache join and completed sweep evidence

- Added non-destructive warm/cold client-cache guards, pinned server-config installation,
  required server terminal-text validation, and portable scenario-provenance JSON to the
  Windows isolated runner.
- Added fixed-view warm-join and completed-sweep routes plus a 24-chunk, 32-column/s sweep
  configuration that excludes assist serving and transient generation.
- Completed a warm join with 558 cached sections, no 25 ms Vintage Horizons tick, and a
  181-section / 51.93 MiB background backlog that drained by 30 seconds.
- Completed a 3,249-position sweep in about 68 seconds: 1,018 existing columns loaded,
  377 frontier columns skipped, nothing generated, and 256/256 sampled absent positions
  remained absent. Reported server ticks stayed below 25 ms.
- Preserved both CSVs, scenario records, context, and evidence limits under
  `bench/results/2026-08-18-join-sweep`.
- Expanded the full game-backed fast tier to 911 passing assertions.

## 2026-08-18 — completed generation and saturated assist evidence

- Added semantic runner guards for active warm/cold server cache state, completed
  transient-generation counters, and saturated live-assist receipt/installation.
- Completed a radius-8 transient run with 289 generated columns, zero failures, and
  256/256 sampled absent positions still absent from the savegame.
- Reproduced an early-join server-assist stall: 16 sections requested, zero received, and
  zero server blob/send work despite a 514-section active server cache.
- Fixed the 50 ms serve loop to retain bounded requests while a joining player is not yet
  exposed as `Playing`; the disconnect event remains responsible for real cleanup.
- Repeated the unchanged scenario with 395 requested/received/installed sections, zero
  declines, and fully drained client transfer/publication queues by 30 seconds.
- Measured server blob reads at 3.75/17.5/68.755 ms p95/p99/max and preserved before/after
  scenario proofs under `bench/results/2026-08-18-generation-assist`.
- Expanded the full game-backed fast tier to 933 passing assertions.

## 2026-08-18 — off-thread server-assist blob reads

- Replaced owning-thread server SQLite blob reads with a bounded dedicated reader that
  owns an unpooled read-only connection and prepared command.
- Preserved per-player request/send order with session-tagged ordered batches; stale
  results cannot cross disconnect/reconnect, and failures return explicit retryable state.
- Removed the shared writable-store blob command and exposed only the server cache path to
  the assist reader.
- Added FIFO, cap, exact-byte, miss, failure, and handle-lifetime regression coverage;
  the full game-backed fast tier increased to 964 passing assertions.
- Repeated the cold-client/warm-server 64/s scenario: 395 sections were requested,
  received, and installed with zero declines and all 16 request slots exercised.
- During a 17.481 ms reader call, owning-thread assist service peaked at 0.989 ms, proving
  the database wait no longer blocks that thread. A separate 32.450 ms service outlier
  occurred with sub-0.2 ms reads and remains a different attribution target.

## 2026-08-18 — mip convergence soak and restart proof

- Added a Windows-runner guard that requires a fresh semantic client sample with zero
  capture prerequisites/results, worker errors, mip obligations, unsaved state,
  asynchronous loads, and storage backlog/errors.
- Ran a 120-second, 1,600-block warm-cache route that loaded 405 sections, captured 2,401
  columns, produced no 25 ms Vintage Horizons tick, and converged completely during a
  45-second cooldown.
- Restarted with fresh server/client processes against the resulting 29,982,720-byte
  cache; 601 sections loaded and the guarded pipeline/storage state converged again.
- Preserved both CSVs, scenario records, context, and limitations under
  `bench/results/2026-08-18-mip-soak`.
- Kept active-work interruption and integrated-singleplayer recovery open rather than
  treating graceful restart as equivalent evidence.

## 2026-08-18 — active mip interruption and durable recovery

- Added an opt-in storage marker emitted only after a row with `ApplyToParent=1` is
  durable, plus a writer hold that prevents the clearing snapshot from racing the runner.
- Added PID-verified client interruption and restart guards to the Windows isolated runner.
- Interrupted one durable level-0 obligation, restarted against the same cache, loaded one
  persisted obligation, and converged all capture/mip/save/load/storage fields to zero.
- Reopened the cache in a third fresh server/client process; it reported zero persisted mip
  obligations and again passed semantic convergence.
- Preserved scenario records, CSVs, context, and limitations under
  `bench/results/2026-08-18-mip-interruption` and expanded the fast tier to 968 assertions.

## 2026-08-18 — visibility-aware traversal and independent residency

- Frustum-tested conservative quadtree node bounds before descent so invisible subtrees
  no longer select draws, request meshes, or gate refinement on invisible children.
- Separated mesh retention from visibility with distance/age residency and added focused
  traversal/residency checks plus explicit subtree-rejection telemetry.
- Ran a byte-identical 601-section cache comparison: selected nodes fell 64.2%, weighted
  average traversal time 19.8%, and weighted average draw submission 9.3%.
- Both sides retained 543 meshes with zero evictions and reported no 25 ms Vintage
  Horizons tick; aggregate FPS was effectively unchanged and is not claimed as a gain.
- Preserved the production functional run, controlled frame/scenario results, 28 matched
  telemetry intervals, method, and limitations under
  `bench/results/2026-08-18-visibility-traversal`.
- Expanded the game-backed fast tier to 975 assertions across 23 suites.

## 2026-08-18 — incremental render-dirty priority scheduling

- Replaced per-frame complete `RenderDirty` pruning and nearest selection with exact
  dirty membership plus an incremental nearest-first priority index.
- Rebuilt priorities only after a 256-block camera-cell crossing, detail-distance change,
  or world clear; ordinary frames inspect only newly added keys.
- Validated stale entries against exact membership and restored temporarily busy keys so
  in-flight meshes/reloads cannot strand or erase newer dirty obligations.
- Added 20 focused assertions for pruning, ordering, delta ingestion, busy-prefix progress,
  camera-cell reprioritization, and clear invalidation; the full tier passes 995 assertions
  across 24 suites.
- Completed a functional 601-section moving/rotation route: all four waypoints settled,
  543 meshes converged with no evictions, all guarded queues reached zero, and isolated
  client/server shutdown was graceful. No controlled performance improvement is claimed.

## 2026-08-18 — frame-budgeted mesh snapshots and GPU uploads

- Added 1 ms / 2 MiB / four-job snapshot-production ceilings and 2 ms / 4 MiB /
  four-result GPU-upload ceilings, with one-first-item progress.
- Estimated snapshot-retained shared/copied arrays and live opaque/water vertex/index
  bytes rather than treating a mesh count as a latency budget.
- Restored exact render-dirty membership when a snapshot waits for the next frame.
- Uploaded and published a complete replacement pair before disposing the previous GPU
  resources; partial upload failure retains visible terrain and restores dirty work.
- Added snapshot/upload throughput, pending bytes, oldest age, direct GL upload timing,
  and disposal timing to render telemetry.
- Made the shared frame-local budget helper allocation-free and expanded the game-backed
  fast tier to 1,002 passing assertions across 24 suites.

## 2026-08-18 — renderer scaling and revision-acknowledged persistence

- Exercised snapshot/upload budgets on 601 cached sections, then grew the isolated cache
  through a 12,800-block corridor to 3,132 rows / 157,724,672 bytes.
- Processed 94,285 snapshots/uploads with sampled queues bounded to 18/four items, direct
  GL upload below 6.9 ms, no 25 ms renderer phase, and complete semantic convergence.
- Fixed the runtime-only `LodDrainBudget` struct-constructor regression found by the first
  current-build launch, added a production-form check, and made the Windows runner reject
  assemblies older than their C# or project inputs.
- Added per-section persistence revisions, exact write success/failure acknowledgements,
  retained dirty state, bounded retry, pending same-key coalescing, durable foreign-route
  promotion, and repeated shutdown drain/ack/enqueue with exact unresolved reporting.
- Added injected failure/retry, repeated mutation, coalescing, 300-key drain, and newest-
  row restart coverage; the Release tier passes 1,050 assertions across 25 suites.
- Reopened the 3,132-section cache in game, wrote 138 revisions, converged unsaved/backlog/
  errors to zero, and shut down both isolated processes normally.
- Preserved route CSV/scenario evidence and limitations under
  `bench/results/2026-08-18-renderer-budgets-large-cache`.

## 2026-08-18 — integrated sibling retry and mip recovery

- Added a separate Windows integrated-singleplayer sandbox and guarded world launch while
  retaining exact PID/command-line interruption safety.
- Forced one transient sibling-cache miss, discovered 211 offered keys, accepted and
  installed 63 sections, and proved exact key `2,2000,2001` retried to installation with
  no wanted request or client convergence work left.
- Scoped the durable mip marker to the client storage worker so the integrated server
  cannot win the shared-environment race.
- Interrupted one durable client obligation, recovered one obligation to clean
  convergence, and required a third fresh process to load zero persisted obligations.
- Added exact zero-obligation and sibling-retry runner guards, two deterministic checks,
  and preserved accepted evidence under
  `bench/results/2026-08-18-integrated-singleplayer`; 1,056 Release assertions pass.

## 2026-08-18 — server-assist tail attribution and progress-log fix

- Repeated the guarded cold-client/warm-server 64/s assist scenario and reproduced a
  12.779 ms service maximum before instrumentation.
- Added opt-in correlated setup/publication/admission, send, allocation, and collection-
  crossing telemetry while preserving the stats-disabled fast path.
- Isolated a 3.655 ms callback whose synchronous every-200-sections progress-log boundary
  occupied 3.573 ms; two sends totalled 0.075 ms and no managed collection crossed the
  callback.
- Removed the owning-thread progress notification, retained cumulative totals in
  `/vhserver` and interval stats, and added a static no-logger guard for the admission
  method.
- Repeated the unchanged scenario after the fix: 273 sections installed, all 16 request
  slots exercised, zero declines, 2.061 ms active-transfer service maximum, 0.647 ms send
  maximum, and no collection crossing any measured callback or send.
- Built and content-verified the fixed Release playtest zip; the final tier passes 1,058
  assertions and no wire, blob, or schema number changed.

## 2026-08-18 - cached-terrain transition source fixes and hybrid design

- Moved cosmetic terrain noise from camera-relative render coordinates to a stable
  section-world coordinate.
- Removed the five-block, 110-block-wide vertex sink that made cached terrain shrink
  downward on approach.
- Replaced the old 78.5% outer cutoff with a conservative inner radial playtest handoff
  that retains at least 192 blocks of fallback overlap.
- Added 23 focused handoff/shader assertions and built two Release playtest packages; the
  newest near-handoff zip has SHA-256
  `89A20E1B48865869FC18D3689EF34C86FD9954BC4A80926B92011F353815BD0D`.
- Source-traced the installed 1.22.5 chunk-rendered signal and recorded its
  tessellation-before-upload caveat.
- Approved and documented a bounded hybrid design with exclusive 32x32x32 ownership, CPU
  whole-mesh skipping, mixed-only GPU masking, independent residency, seam gates, and
  paired performance acceptance.

## 2026-08-18 - pixel-neutral vanilla readiness shadow tracker

- Source-traced the exact installed Vintage Story 1.22.7 client chunk lifecycle: dirty
  notification precedes tessellation, `quantityDrawn` precedes tessellated-result upload,
  the post-upload callback is internal, unload removes the chunk without a public client
  event, and public shader wrappers do not expose a supported 3D texture update path.
- Added a renderer-owned 32x32x32 readiness model with power-of-two tagged-ring storage,
  fixed duplicate-coalesced candidate/deferred queues, two-frame gain stabilization,
  first-false loss proposals, stale-publication rejection, and L0-L6 ancestor counts.
- Wired `ChunkDirty`, bounded initial discovery, boundary-first loss revalidation, slower
  interior maintenance, and public `IsChunkRendered` probes under 256-item and 0.25-ms
  per-frame ceilings.
- Kept the integration pixel-neutral: readiness is absent from draw classification and the
  conservative radial handoff remains the sole pixel owner and exception fallback.
- Added readiness phase/state/queue-age/transition/error/window/event/byte telemetry to
  periodic logs and `.vhinfo`.
- Added 89 focused readiness assertions plus static wiring guards; the complete Release
  tier passes 1,176 assertions with no protocol, blob, schema, shader, package, or release
  change.

## Session 28 (2026-08-19, 0.3.5 - 0.3.15)

- Fixed a duplicate `.vhwhy` registration that threw out of `StartClientSide` and left every
  later command unregistered; `.vhdetail` had not existed in 0.3.4. Coarse-draw report is now
  `.vhcoarse`, guarded by a static uniqueness check.
- Fixed `maskSectionOrigin`, a `uniform ivec2` set through the client's `Vec2i` overload,
  which reaches `glUniform2f` and is rejected against an integer uniform. The per-fragment
  mask had addressed chunk (0,0) for every section since 0.3.0 and raised a GL error every
  frame. Split into two `uniform int`s; guarded by a static check against integer vector
  uniforms. Attributed by a controlled sandbox pair: 19,126 GL errors with the mask, zero
  without, zero after.
- Fixed `VanillaReadinessMask.ClearColumn` never being called since the feature was written,
  which let an arriving column inherit a departed column's ownership through the wrapped
  ring. `ClearSlot` now raises `ColumnEvicted`; the regression test proves the arriving
  column reuses the departed texel and does not inherit its value.
- Corrected G40 from the installed game's IL: `IsChunkRendered` is `quantityDrawn > 0`, the
  tessellator advances it and returns early for an empty chunk, and the client's `Empty`
  flag arrives from the server in the chunk packet rather than being stale. 0.3.3 failed on
  its rule, not its input.
- Established the engine's only real drawing signal, `ClientChunk.CullVisible[bufIndex]`
  (G43), after human testing showed unmodded vanilla stops drawing the ground directly
  beneath a high-altitude camera while `IsChunkRendered` still answers true.
- Added `.vhcoarse`, `.vhgeom`, `.vhskip`, `.vhholes` and `.vhpaint`; `.vhwhy` now searches
  to the full draw distance instead of 512 blocks and prints every engine signal.
- Added a once-per-second rebuild of the ownership atlas from committed tracker state, with
  a counter, bounding any mirror desync to one second.
- Excluded air chunks from the mask texel while keeping them owned in the tracker.
- Added static checks for duplicate chat command names, integer vector uniforms, and control
  characters anywhere in source; the last of these caught two escape-sequence corruptions
  introduced while editing.
- `scripts/package.sh` now resolves `python` where `python3` is absent, and
  `bench-windows.ps1` rejects a label the bench mod would rewrite, which had cost a
  five-minute timeout per run.

### Resolved - OpenGL errors every frame

Every client log on this machine carries `after final compo - OpenGL threw an error:
InvalidOperation`, tens of thousands of times per session: 30,052 in the ~2 minute 0.3.4
run, and 22k-86k in each of the five archived runs before it. It is the bulk of a 5 MB log.

Not attributed. All six sessions had VintageHorizons installed **and** ten other mods,
several of which touch rendering, so there is no control. The errors start seconds to
tens of seconds after the mod's first fill-in rather than at world join, which is
suggestive and nothing more; GL errors are sticky and are reported at the next checkpoint,
not where they were raised.

**Resolved, 0.3.6.** It was ours: a `uniform ivec2` set through the client's `Vec2i`
overload, which reaches `glUniform2f` and is rejected outright. Two isolated sandbox runs
on one stationary scene separated it - 19,126 with `-ChunkMask`, zero without - and a third
confirmed zero after the fix. See G42. The user's own logs should be clean from 0.3.6; if
they are not, what remains belongs to another mod and the same paired-run method applies.

## 2026-08-19 — the band of missing terrain, closed

Twelve builds, six explanations, two sessions. **Resolved in 0.3.16 and confirmed in game
by the owner.**

- **Root cause.** The engine range-culls every terrain mesh against the current camera every
  frame, inside `ModelDataPoolLocation.IsVisible` via `FrustumCulling.InFrustumAndRange`,
  after all four per-chunk signals have said yes. Nothing about the chunk changes when that
  happens: `quantityDrawn` only rises, the mesh stays pooled, `Hide` stays false, and
  `ChunkCuller.CullInvisibleChunks` early-returns while the camera holds one chunk, freezing
  `CullVisible` outright. Committed cells in the annulus between the view-distance circle and
  the tracked window edge therefore stayed committed forever, and the mask discarded cached
  terrain there against nothing — on the trailing side only, permanent while stationary.
- Ownership now denies any cell whose column lies beyond `viewDistance - 46` blocks, air
  included. The 46 clears the in-chunk horizontal diagonal `32*sqrt(2)`, because the engine
  measures from the mesh's geometry-midpoint bounding-sphere centre rather than the chunk
  centre; a nearest-face comparison against the plain view distance leaves a thinner copy of
  the same band. `OwnershipStopsAtTheEnginesDrawRange` asserts the no-hole direction over
  every admissible midpoint placement and fails on the weaker threshold.
- `WriteMask` now honours the air exclusion through a per-cell flag stored at publication and
  kept authoritative by the once-per-second resync. Before this, the wholesale rebuild on
  every window change reintroduced air ownership every 32 blocks of travel, undoing 0.3.15
  continuously while moving.
- `.vhwhy`, `.vhholes` and `DescribeOwnershipAt` no longer answer "engine is drawing this
  chunk" about range-culled ground; all three had been consuming the same incomplete signal,
  which is why every CPU diagnostic reported health for two sessions.
- G43 rewritten: the culler's verdict is necessary, not sufficient. G45 added for the probe
  loop's chunk lock. `scripts/bench-windows.ps1` parses the new counter.
- Retired with it: the cube-granularity theory, which was the only surviving candidate at
  the close of session 28 and had no evidence of its own; and the proposal that the
  `WriteMask` air gap was the primary cause, which the owner's report of a permanent band
  while stationary refuted in one exchange.

## 2026-08-20 — water chunk seams

- Corrected the recorded symptom: the fault was a vertical seam standing at every cached water chunk boundary, never a colour difference, and all three colour candidates previously listed were wrong.
- Found the cause in the mesh scheduler, which established whether a neighbouring section exists from RAM residency (`Sections`) rather than from stored data (`HasDataSet`), the question the shader's `openEdges` has always asked.
- Established why it reached nearly every boundary: sections load and mesh nearest-first, so a section's outward neighbour is routinely still in flight, and neither `InstallLoaded` nor `MarkChanged` ever re-meshed it afterwards.
- Measured the artefact offline on a synthetic ocean section: 1 water quad with the neighbour present against 65 without it, 64 of them a 3,200 block2 sheet of 66%-opaque water on the shared plane.
- Established why only water showed it: an opaque neighbour hides the same wall on land, which is why every land case in the visual matrix concealed the fault.
- Added a per-side assumed-covered mask, permissive for water and conservative for solids, with a targeted re-mesh driven by a new `SectionBecameResident` callback; rejected blanket neighbour dirtying and deferred meshing, both of which spend the join fill-in budget.
- Added mesher regression coverage for all four wall states and pinned the opposite-side pairing the repair depends on; 1,441 assertions pass.
- Added `seam repairs` telemetry, promoted G51, and shipped 0.3.23.
- Human-confirmed in game: ocean seams gone, shores and cliffs clean.

## 2026-08-20 — opaque GPU overdraw

- Source-traced the renderer and installed game client: Vintage Horizons had frustum,
  distance and vanilla-ownership rejection, but no own terrain occlusion. Opaque cached
  meshes were deliberately rendered two-sided despite the game exposing the needed cull
  state.
- Corrected all six solid face directions to outward counter-clockwise winding and enabled
  back-face culling for opaque cached terrain only. Water and thin/cutout geometry remain
  two-sided. Added `.vhbackface` and `VINTAGEHORIZONS_BACKFACE_CULLING` fallbacks.
- Human A/B: 218 FPS off against 260 on (+19.3%, about 0.74 ms saved), with no visible
  difference across cliffs, caves, overhangs, high views or low views. Accepted on by
  default.
- Added reusable allocation-free front-to-back ordering for opaque selected sections while
  preserving water traversal order. Added `.vhfront` and `VINTAGEHORIZONS_FRONT_TO_BACK`
  fallbacks.
- Human A/B: 149 FPS off against 173 on (+16.1%, about 0.93 ms saved), with no visual
  difference. Accepted on by default.
- Added six-direction winding and opaque-order regression coverage. Version 0.3.27 passes
  1,464 assertions and a clean Release build.
- Reprioritized the renderer backlog around conservative occlusion after the owner measured
  about 150 FPS while facing roughly 4,000 blocks of cached mountainous terrain and more
  than 300 FPS while facing away. Fast-flight coarseness remains recorded but is demoted
  because those speeds are outside normal play and extensive ordinary play found no issue.

## 2026-08-20 — post-vanilla depth rejection

- Built a default-off same-frame bounding-box query prototype with a live `.vhocclusion`
  toggle. Fixed its missing shader include, then rejected the design after it reported 83%
  hidden boxes while changing 156 FPS to 155 FPS.
- Removed the query objects, proxy shaders, direct OpenTK dependency and opaque bounds
  metadata rather than retaining a zero-gain experimental branch in production source.
- Source-traced the installed renderer ordering: Vintage Horizons ran at opaque order 0.36
  before vanilla terrain at 0.37, so the nearby current hill was absent from query depth.
- Reused `.vhocclusion` to re-register cached terrain at 0.38. Ordinary depth rejection then
  raised the owner's valley view from 148 to 179 FPS (about 1.17 ms saved) and a ground view
  from 590 to 651 FPS (about 0.16 ms saved).
- The owner found only minute distant changes detectable through immediate A/B toggling and
  judged them entirely acceptable. Post-vanilla order is default-on in 0.3.30;
  `.vhocclusion off` restores 0.36 immediately.

## 2026-08-20 — delayed exact-geometry occlusion

- Reintroduced GPU visibility as an asynchronous reuse policy around the real opaque terrain
  draw after vanilla depth, rather than the rejected same-frame proxy/conditional design.
  Available zero-sample answers skip later submissions; hidden meshes periodically draw as
  their own exact visibility probes, and stale view-epoch answers fail toward drawing.
- Added `.vhtemporal`, `.vhtemporalprofile safe|aggressive|extreme`, an environment fallback,
  and `.vhinfo` counters for skipped draws, hidden/accepted/stale results, global
  invalidations, pending queries, seam protection and turning-edge protection.
- The first stationary hill test raised about 170 FPS to nearly 500. Motion initially erased
  the gain; retaining results through rotation and probing every four frames while turning
  produced roughly 250-350 FPS in the owner's sampled areas.
- Excluded mixed vanilla/cache ownership sections after occasional seam loss. Extreme then
  exposed visible fringe distortion during very fast yaw; aggressive retained very good
  performance with the distortion nearly unnoticeable, and 0.3.37 added a narrow horizontal
  turning-edge guard. The owner accepted the final tradeoff.
- Diagnosed a location-dependent failure from the pause control: continuous chunk/readiness/
  mesh activity globally invalidated every answer, holding an enclosed running view near
  190 FPS while pause allowed about 500. Mesh replacement now invalidates its own section;
  readiness events and mask uploads preserve unrelated state and periodic probes converge.
- Made aggressive default-on in 0.3.37. Safe invalidates on small camera changes; extreme
  deliberately retains results through all camera motion without the edge guard.
- Added pure state-machine, view-threshold, exact-rotation, profile/default wiring, seam-
  invalidation and horizontal frustum-edge coverage. The Release fast tier passes 1,503
  assertions; the packaged and installed archive hash is
  `BF69FB931BCAAFFD5395FA67EDCFD8198F78ABA64903596F98E6A72826C88473`.

## 2026-08-20 — configurable cached-terrain LOD distances

- Corrected the setting semantics so the accepted default transitions begin L1-L6 at 512,
  1,024, 2,048, 4,096, 8,192 and 16,384 blocks instead of twice those distances. Retained
  nearest-edge square-section distance and coarser-parent fallback.
- The owner tested 0.3.38 and reported an enormous performance increase with very little
  visual-fidelity loss, accepting the corrected defaults.
- Replaced the single derived distance with six ordered thresholds and a monotonic policy
  revision. Existing `DetailDistance` configurations migrate to their equivalent doubling
  sequence, and `.vhdetail` remains a quick compatibility control.
- Added `.vhconfig`: six individually draggable constrained handles on one logarithmic LOD
  scale, a separate cached draw-distance slider, and Defaults/Cancel/Save behavior.
- Refined 0.3.40 after the first in-game UI review: larger and more distant handles labelled
  L1-L6, full comma-separated values, 32,768-block ceilings on both scales and 512-block
  cached draw increments. The revised layout remains pending human visual acceptance.
- Added transition, policy-refresh, constraint and static GUI regressions. The complete
  Release tier passes 1,533 assertions and the Release build has zero warnings or errors.
- Packaged and installed `vintagehorizons_0.3.40.zip`; the two copies had SHA-256
  `52B89A30B95187F335DFB6043318B7652C63844E6BD91F27750CE10DCFA2662A`.

## 2026-08-21 — periodic stutter and disk-write audit

- Source-traced ordinary persistence from owning-thread dirty membership through snapshot
  freezing, storage-worker serialization/compression and SQLite. The old path could admit
  six snapshots per 50 ms game tick and execute every row independently; clean idle wrote
  nothing, but active capture could generate a high-frequency transaction stream.
- Replaced ordinary writes with per-pipeline 30-second RAM checkpoints. Snapshot freezing is
  capped at one section per tick and 256 distinct keys per checkpoint; newer/overflow work
  remains dirty in RAM. The storage owner serializes and compresses the batch and commits it
  in one SQLite transaction. Shutdown retains its immediate exact-revision flush.
- Moved integrated-singleplayer sibling-cache blob SQL from the game tick to a bounded
  below-normal read-only worker. Key discovery was already off-thread and remains separate.
- Replaced the 240-frame seasonal burst with a 30-second staged refresh, one tint slot per
  frame and atomic publication. Replaced full GPU mesh and CPU resident-section eviction
  sweeps with rolling four-per-frame and two-per-tick queues.
- Capped unavoidable render-context visibility work at eight query issues and sixteen result
  checks per frame. Aligned server follow-up manifest scans with the 30-second checkpoint.
- Added gated-writer, async-blob, rolling-eviction and cross-file cadence guards. A warning-
  free Release build and 1,555 assertions passed. No game process was launched, so stutter
  improvement and visual/reclamation tradeoffs remain awaiting human playtest.
- Established G57 and the canonical build rule: every changed playable/package/install
  artifact advances the patch component by exactly one; ordinary compile/check runs do not.

## 2026-08-21 — GPU feasibility and legacy-only renderer boundary

- Added opt-in delayed, nonblocking opaque/water GPU timers plus draw-call, submitted-
  geometry and live-mesh-byte telemetry without changing rendered output.
- Runtime-validated the primary Radeon RX 9070 XT's required GL entry points, minimal
  compute dispatch, expected SSBO write/readback and a disposable exact-format copy of the
  active 2,560x1,440 `DEPTH_COMPONENT32` texture with all 12 mip levels.
- Centralized exact capture/restoration and verification for program, generic/indexed SSBO,
  draw/read framebuffer, active texture and texture-unit-zero state.
- Added a renderer lifecycle coordinator for publication/removal, frame preparation,
  opaque/water draw, clear and disposal. Its visible target is structurally fixed to legacy.
- Added an opt-in, validated CPU-only GPU shadow that mirrors generation-tagged identities
  and counts, owns no GL resources, receives no draw calls and fails without affecting
  legacy publication or drawing.
- Added stale-world rejection and globally non-aliasing section/opaque/water resource
  generations, including same-epoch clear coverage.
- Added frozen Bodanboys save seeding and a six-view open-horizon GPU route. Corrected one
  watched/refresh-capped run; retained the uncapped pair only as open-horizon cost evidence
  because it placed essentially no cached terrain behind vanilla terrain.
- Completed Phase 1 source/harness verification with a warning-free Release build, 1,627
  passing assertions and 1,441 documentation checks. FPS benchmarking and runtime
  equivalence remain owner-run.


## 2026-08-22 — vanilla lighting parity, and change locality for re-meshing

- Settled the long-standing "cached terrain drifts from vanilla as the day goes on" report
  offline, by decompiling the client and decoding the engine's own sunlight ramp. The
  dominant term was the light colour: vanilla's terrain multiplies by
  `Ambient.BlendedAmbientColor`, built from `ReflectColor` and floored at a blue night tint
  once the sun is down, while the mod used `SunColor`, which has no such floor. After
  sundown the two hues invert.
- Disproved the plan's leading theory. `LightPosition3D` tracks the sun exactly all day; it
  differs only at night, where vanilla lights from the moon.
- Found a divergence the plan did not contain: vanilla brightens every terrain pixel by
  22.7% while the sun is high and fades that out as it sets, whether or not the player has
  shadows enabled.
- Shipped five corrections behind `.vhlight moondir | ramp | ambient | boost | sky | all`,
  all default on. The four terrain terms are HUMAN-TESTED and accepted in 0.3.51; the sky
  band correction shipped in 0.3.52 and is not yet looked at.
- Narrowed `LodWorld.MarkChanged` to the edges a change actually touched.
  `LodSection.ReplaceColumns` now returns an edge mask, `LodMip.ApplyToParent` carries it,
  and callers with no column-level answer keep the conservative all-edges behaviour.
- Measured it over three benchmark runs on the frozen `bodanboys` profile: 38-40 content
  changes produced 77-80 stale-mesh claims, 2.00-2.03 per change against 5.00 before, with
  no frame-rate cost (367-432 FPS against 363-430 documented).
- Added the `change locality:` counters, which had never existed; the amplification factor
  could previously only be inferred.
- Withdrew the re-mesh section's headline claim. "204-289 MiB built and 76-119 MiB uploaded
  every 15 seconds, continuously, at a standstill" is a warm-up measurement: the first 2.5
  minutes carry 237 MiB per 15 seconds, and settled steady state carries 2.1 MiB. This also
  removes re-meshing as the leading micro-hitch suspect. G62, G63.
- 1,892 assertions and 1,453 documentation checks pass.
