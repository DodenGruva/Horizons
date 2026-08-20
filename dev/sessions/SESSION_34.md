# Session 34 — delayed exact-geometry occlusion

**Date:** `2026-08-20`
**Branch/commit:** `codex/gpu-overdraw-culling`, baseline `597c0e6`
**Mod version:** `0.3.37`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

Session 33 moved cached terrain after vanilla and recovered about 1.17 ms in the owner's
valley, but looking across roughly 4,000 blocks of cache hidden by a hill still rendered near
170 FPS while looking down exceeded 600. The owner wanted an aggressive occlusion path with
live commands, game-ready archives, and direct installation, while warning that an earlier
query design had produced no gain.

The rejected design was not retried. It rendered extra proxy boxes and consumed visibility
in the same frame through conditional rendering. This session instead asked whether the real
opaque draw, already occurring after vanilla depth, could be measured without waiting and
used to skip work on later frames.

## Work narrative

### 1. Exact draws became asynchronous probes

Each selected opaque section owns an `AnySamplesPassed` query. When its interval arrives,
the query wraps the section's ordinary terrain draw; no proxy geometry is added. Results are
polled for availability on later frames and never block the render thread. A zero-sample
answer marks the section hidden so later frames skip `RenderMesh` entirely. Every hidden
section periodically draws again, and that real draw is simultaneously its visibility probe
and the correct terrain for that frame.

The CPU policy records pending/result epochs. Projection and selected camera thresholds
advance the epoch; an older GPU answer is consumed but cannot hide the new view. Query API
failure disables the optimization and draws all terrain. Query objects follow mesh/world
lifetime, while visibility remains independent from residency and persistence.

The first owner test raised the established hill view from about 170 FPS to nearly 500 while
stationary. Turning or moving initially discarded the result too often and gave the gain
back.

### 2. Motion policy was tuned against the player's steering model

`.vhtemporal` toggles delayed occlusion immediately, and
`.vhtemporalprofile safe|aggressive|extreme` changes its invalidation/query cadence live.
Safe invalidates after small movement or rotation. Aggressive retains results through turns,
invalidates after two blocks of translation, checks visible terrain every eight frames and
hidden terrain every sixteen, shortened to four while turning. Extreme retains through all
camera motion and uses longer intervals.

After keeping results through mouse turns and shortening the turning probe, the owner
reported roughly 250-350 FPS in motion, varying by area, and asked to continue toward the
visual limit. These ranges came from sequential playable builds rather than a controlled
alternating benchmark.

### 3. Seam and disocclusion artifacts were guarded narrowly

The owner occasionally saw cached terrain disappear at the exact edge where vanilla and
cached terrain meet. That is a mixed-ownership section: most of one mesh can be rejected by
the readiness mask while a small cache-owned remainder is essential. A whole-section zero
answer is too coarse there, so mixed sections now bypass temporal suppression.

The extreme profile made rapid yaw visibly distort the edge of the screen. Aggressive kept
very good performance and made it nearly unnoticeable. Version 0.3.37 protects a narrow,
distance-normalized band at the left and right frustum planes only while rotation is
occurring. Central terrain and every stationary frame retain the full policy; extreme keeps
the guard disabled so the limit remains testable. The owner judged the final result
acceptable.

### 4. Pause isolated global invalidation churn

In a new area the feature appeared to stop: even an enclosed view stayed near 190 FPS, yet
pausing raised it to about 500. Looking at the exposed 4,000-block cache still showed the
ordinary paused rate, so pause itself was not a general optimization. The difference was
continuous scene activity. Chunk-dirty events, readiness-mask uploads, and mesh replacement
were globally advancing the query epoch, preventing any answer from surviving long enough
to skip a draw; pause stopped those updates.

Mesh replacement now invalidates only that section. Chunk/readiness events and mask uploads
do not erase unrelated results; mixed ownership remains protected directly, and periodic
exact probes discover changed occlusion. `.vhinfo` exposes accepted hidden results, stale
results, global invalidations, pending queries, skipped draws, and seam/turning-edge draws so
future recurrence can be distinguished from a genuinely visible scene. The owner reported
the corrected build was much better.

### 5. Default, verification, and package

Aggressive delayed occlusion is default-on; `VINTAGEHORIZONS_TEMPORAL_OCCLUSION=0` and
`.vhtemporal off` are fail-open fallbacks. The state policy, view thresholds, exact rotation
detection, profile/default wiring, seam-local invalidation, driver-query ownership check and
horizontal edge classifier have automated coverage. The Release fast tier passes 1,503
assertions, `git diff --check` is clean apart from line-ending notices, and the 0.3.37 archive
was content/version/hash verified before installation. The assistant did not launch Vintage
Story; every FPS and visual observation is owner-run.

---

## Delivered

**Source:** default-on delayed exact-geometry occlusion; safe/aggressive/extreme policies;
stale-result rejection; localized streaming invalidation; mixed ownership and rapid-turn
edge protection; fail-open GL handling and lifetime cleanup.

**Commands and diagnostics:** `.vhtemporal`, `.vhtemporalprofile`, and expanded `.vhinfo`.

**Tests:** pure temporal state and view-change coverage; frustum edge fixtures; static
default/profile/command/query-order/invalidation guards. Full Release fast tier: 1,503
assertions, zero failures.

**Playable build:** `dist/vintagehorizons_0.3.37.zip`, SHA-256
`BF69FB931BCAAFFD5395FA67EDCFD8198F78ABA64903596F98E6A72826C88473`, installed in the
owner's mod folder with 0.3.35 preserved under `dist/installed-backup`.

## Decisions

- Accept delayed exact-geometry occlusion as default-on with the aggressive profile. The
  owner found its motion performance substantial and the final guarded tradeoff acceptable.
- Preserve rotation rather than globally invalidating it. Mouse steering is ordinary play;
  a policy that switches off while turning does not address the product case.
- Protect known disocclusion boundaries narrowly: mixed handoff sections always draw, and
  only the horizontal screen-edge band draws while turning. Global conservatism would erase
  the measured gain.
- Retain safe and extreme as live, unsaved diagnostic profiles. Safe identifies invalidation
  artifacts; extreme keeps the visible failure boundary reproducible.
- Treat the same-frame proxy experiment and delayed exact-draw policy as different designs.
  The former remains rejected evidence and must not be resurrected from its hidden count.
- Do not change assist protocol, blob format, or schema; rendering state is session-local.

## Traps

- Global invalidation on local streaming changes prevents convergence and can masquerade as
  location-dependent or pause-dependent GPU behavior. Scope invalidation to changed identity.
- A whole-section visibility answer cannot safely decide the mixed vanilla/cache seam.
- Turning invalidation is not conservative in product terms when mouse steering is normal:
  it silently removes the optimization exactly while the player looks around.
- Reusing hidden answers through rotation needs a disocclusion policy; otherwise the newly
  exposed screen fringe shows stale absence for the probe interval.
- A GPU result being available does not make it current. Epoch identity must be checked
  before a zero-sample answer is allowed to hide terrain.

## Flagged and unverified

- The final 0.3.37 edge guard was accepted qualitatively but its FPS cost was not isolated.
- The near-500 stationary result and 250-350 motion range are sequential owner observations,
  not repeated alternating comparisons. Area, streaming state, driver work, and the evolving
  implementation were not controlled.
- Evidence covers one owner, machine, driver, world, and ordinary sampled views. Other
  drivers, multiplayer, long sessions, teleports, caves/structures, vertical screen-edge
  disocclusion, and sustained streaming remain ordinary coverage debt.
- Delayed occlusion applies only to opaque cached meshes. Water submission and residual CPU
  traversal/setup for sections not yet proven hidden remain.
