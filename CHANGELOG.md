# Changelog

Player- and operator-visible changes and other significant established session outcomes
accumulate under **Unreleased** once they are established well enough to describe. Do not
wait for a version release to record significant completed work; a release reviews and
finalizes the accumulated section. See [docs/RELEASING.md](docs/RELEASING.md). Newest
first.

## [Unreleased]

## [0.3.124] - 2026-08-27

**The remaining renderer optimization ideas have been measured before implementation and rejected
where their ceilings are too small.** A fresh six-view comparison found that 16-MiB cluster-arena
pages reduced observed batches only from 76 to 74 while increasing final committed memory from 200
to 368 MiB; mean frame time changed only 2.3433 to 2.3300 ms. Eight-MiB pages remain the product
choice. The command, record, and cull-box stream peaks near 239 KiB per frame, so the proposed
region-anchored record rewrite is not funded.

GPU statistics now isolate the live ordinary, split-near, and split-far cull compute dispatches
with nonblocking timestamp pairs, reported in the client log and captured by the Windows benchmark
scenario proof. On the owner's RX 9070 XT and frozen BodanBoys route, split-far culling measured 25
us at both p95 and p99, with 108 us as the largest interval maximum and zero timer-ring skips or
target conflicts. Two-tier whole-section/cluster culling is rejected because even removing the
existing dispatch entirely would save only about 1% of the 2.3717-ms average frame.

Version 0.3.123 also corrected frame-timeline attribution: a begin-to-begin frame interval is now
paired with the callback that preceded it, not the following callback. The owner's tiny sawtooth
pattern reproduced with all mods disabled, so it is not attributed to Vintage Horizons. The owner
also retired 30-second stutter and join-time warm-up verification after extensive smooth testing.
Warning-free builds and 5,381 fast assertions pass. The verified 14-entry 0.3.124 package contains
no PDB and was copy-installed with SHA-256
`484F058D54BDD92408C52C9BA4A4EEDE2411653C1587EC80F948DDFAFF592867`. Assist protocol 1, blob
format 4, and database schema 6 are unchanged.

## [0.3.122] - 2026-08-27

**Cave culling is now a surface-visibility optimisation, is on by default, and preserves straight
mountain tunnels.** The owner does not need hidden underground networks preserved merely because
they connect cave entrances. The runtime rule therefore keeps a 64-block daylight envelope around
the actual captured terrain surface plus exact unobstructed straight sight from exterior air; it
does not use sea level, follow bends, or keep unrelated network branches.

Long straight tunnels remain open even when both mouths lie outside a mesh job's 3x3 data window:
an uninterrupted ray crossing the complete known window fails open. A fixed four-block clearance
halo extends from a proven ray through all 26 directions and stops at solid terrain, preserving
uneven floors and the visible tunnel volume without becoming a connectivity flood. On the same
40-section real-cache sample the final rule changed 1,554,740 estimated vertices to 1,136,824,
removing 26.88%. The four-block all-direction guard cost about 2,250 estimated vertices, 0.15% of
the baseline, with effectively unchanged paired harness wall time.

The owner reports that a same scene looked visually identical and rose from about 350 FPS to about
400 FPS with culling enabled, then accepted the mountain-tunnel and uneven-floor fixes. This is
qualitative single-machine evidence rather than a controlled benchmark. Reach comparisons kept
1.30% more baseline geometry at 96 and 4.76% more at 128; the owner chose 64. Cave culling now
defaults on, with `VINTAGEHORIZONS_CAVE_CULLING=0` and `.vhcavecull off` retaining immediate
rollback. The in-game command has been renamed from `.vhcaves` to `.vhcavecull`; the old name is
removed. Warning-free Release builds and 5,368 fast assertions pass. The verified, copy-installed
0.3.122 package SHA-256 is
`F971FC4643016AC2EB5A07C8702453C2E31C01257425BB826E51B805F05D9800`.

The previously proposed global span graph, persistent masks, route classifier, separate fluid
graph, and invalidation machinery were not built. Their offline prototype remains useful evidence:
it proved that preserving every entrance-to-entrance route retains deep interior, not merely
visible passages. The plan is closed as superseded by the accepted surface-only rule. Assist
protocol 1, blob format 4, and database schema 6 are unchanged.

## [0.3.116] - 2026-08-26

**Cave culling can now say why it kept a cave.** A screenshot could show a cavern that survived, but
nothing could distinguish which safety rule saved it, and the possible reasons imply completely
different fixes. `LodCaveCull` now tallies every subterranean cell of each classified section into
one of six reasons: removed and filled, kept lit by daylight, kept on a backbone between terminals
we hold terrain for, kept on a backbone whose terminal is light invented at the unknown window wall,
kept because the column has no opaque material to fill with, and water that the air pass never
classifies at all. Each reason reports cells, share, a face-area estimate, and cells per section.

`.vhcaves` with no argument writes the breakdown to the client log under `caves:`, because chat
cannot be copied out of the game; the chat reply keeps its one-line summary and points at the log.
Toggling cave culling resets the tally alongside the existing timing reset.

This changes no culling decision. The accounting reads the finished classification, walks only the
centre section of each window so terrain is not counted nine times, and the one ordering change it
required seeds the same cells to the same values. The cave suite grew from 23 to 36 assertions
pinning exact counts, and the full fast tier passes 5,278. Cave culling remains off by default. The
verified package SHA-256 is
`AE3291B9CA12A0EC5583A197A284B232630DB75AE287767F12A1046A8A15F7D1`. This build has not yet been run
in game.

## [0.3.115] - 2026-08-26

**Cave culling now runs conservatively at every rendered LOD and removes narrow dead branches from
multi-entrance cave networks, but the measured additional value is marginal.** Coarse levels scale
the light budget to approximately four represented columns instead of declining at L4-L6. Dark
cave space is reduced to a conservative passage graph; only bridge-separated branches that lead to
no light-frontier terminal are filled, while every route between terminals, wide rooms, loops, and
ambiguous junctions remain. Columns without an opaque material are left unchanged rather than
being plugged with water or thin cover.

The focused cave suite now passes 23 assertions and the complete fast tier passes 5,167. Real-cache
sampling found only 1.7%, 0.5%, and 0.3% removable at L4, L5, and L6. In the owner's settled live
comparison, the same 1,713 meshes changed from 86,884,340 to 64,791,952 opaque vertices and from
1,858.7 to 1,393.7 MiB with cave culling enabled: 25.43% and 25.02% reductions, only roughly 0.3
percentage points beyond the accepted 0.3.114 result. The complete cave-preparation pass measured
44.984 ms mean and 149.648 ms maximum worker time per section; 0.3.114 lacks the same timer, so this
is current total cost, not the refinement's isolated overhead.

Human review found substantial dry and flooded underground geometry remaining. The local
3x3-section classifier cannot prove that a large cave crossing its unknown boundary is globally
sealed; light-expiration patches are not verified surface mouths; wide natural cave branches form
cycles rather than removable graph bridges; and water is captured as occupied geometry rather than
air the cavity pass can fill. This refinement is not accepted as the final approach and cave
culling remains off by default. The verified package SHA-256 is
`69C6E8FAA2B0D3E2AFCAB3B385FC6E68A4398B507167A890FC425F194FEB9671`.

## [0.3.114] - 2026-08-26

**Cave culling now removes geometry in game instead of adding it.** The culler rebuilt the current
section with hidden air filled, but vertical face collection compared that post-cull terrain with
the original current and neighbour snapshots. Adjacent filled columns therefore saw old cave air
and grew walls through the cavern, reversing the intended saving. One prepared classification now
supplies the rebuilt current section and matching immediate-neighbour boundary coverage to every
face decision. The shipping `cavefield` path and focused checks exercise multi-column chambers and
cross-section seams so the integration disagreement cannot hide behind pre-filled test data again.

The owner accepted the correction in game. At the same settled 1,730 meshes, culling changed
88,606,884 to 66,372,948 opaque vertices and 1,895.7 to 1,427.7 MiB: about 25.1% fewer vertices and
468 MiB less live geometry. Cave culling remains opt-in because it changes terrain geometry. The
verified package SHA-256 is
`AED61A89F84528B52FFBC1595C1C4EF77255BD1F873A69C42B364E9BE26A7871`.

## [0.3.113] - 2026-08-26

**Quadtree subtrees are culled by the real height of the terrain under them.** Whole branches of
the detail tree were tested against a box running from bedrock to sky, so looking up or down
traversed subtrees a real box would refuse. Each node now carries the combined vertical extent of
the meshes resident beneath it. Measured in game this rejects 2 to 20 extra nodes per frame against
the 33-74 the walk already rejected, which is real but small: at a leaf the aggregate is a looser
version of the per-section bound shipped in 0.3.58 and removes a draw that was not happening, and
the walk it speeds up costs 44.3 us of a roughly 2000 us frame. Kept because it is free.
`VINTAGEHORIZONS_SUBTREE_HEIGHT_CULLING=0` restores the old box; `.vhsubtree` reports what it
rejected and `.vhsubtree heights` reports how tall resident terrain actually claims to be.

**Cave systems daylight never reaches can be left unbuilt, behind `.vhcaves`, off by default and
currently defective.** The mesher builds a floor, a ceiling and walls for every buried cavern in
range; measured against a real cache, 55% of all cached geometry sits below the surface and about
30% of it is removable without touching anything a player could see. The rule keeps whatever
daylight reaches within 32 blocks of spreading, and never fills a pocket with more than one way in,
so a tunnel through a mountain still goes through at any length. **Do not switch this on:** in game
it currently increases live geometry by about 2.9% rather than reducing it, for reasons that are
not yet understood. An audit that builds each section both ways is available with
`VINTAGEHORIZONS_CAVE_AUDIT=1`.


## [0.3.105] - 2026-08-25

**Phase 9 closes the packed-memory decision.** A controlled expanded/packed/packed/expanded route
on the primary AMD system measured 2.4392 ms expanded and 2.4367 ms packed. The -0.10% packed delta
is below repeat variation, so this establishes timing parity rather than a speed improvement while
retaining the 86.4% geometry-format reduction. All 24 measured viewpoints settled without timeout.

The regional GPU mirror now retains exactly the representation selected at setup: expanded,
whole-packed, or clustered-packed. The product-default cluster path no longer also allocates and
uploads the expanded and whole-packed regional copies. On the measured route this reduces live
regional duplicate geometry from about 833.7 MiB to about 90.33 MiB. It is not an equivalent total
game-memory reduction: the established per-section meshes remain resident as the complete fallback.

The Windows runner now parses the current split-near/split-far GPU timing sentence and records
upload tail latency plus live and committed bytes for each regional arena. The warning-free Release
build and 5,104 fast assertions pass. The verified 0.3.105 package has SHA-256
`13975952ED56D178B0AE61571B9164682F9F36746199954A8F9D1EC1FEFAE9AE` and was copied beside the
preserved rollback builds. No terrain cache, assist protocol, blob format, or database schema
meaning changed.

## [0.3.104] - 2026-08-25

**Phase 9 hardening begins, and the development line returns to 0.3.x.** The owner withdrew the
0.4.0 milestone promotion after confirming that the GPU renderer had become the default before the
plan's Phase 9 hardening gate was complete. This build follows the last 0.3.x artifact, 0.3.103,
with the next patch identity; it does not reuse a changed binary behind an old version.

Normal rendering is unchanged. Guarded test runs can now set
`VINTAGEHORIZONS_GPU_INJECT_FAILURE=arena|shader|depth-copy|draw` to force one named GPU boundary
to fail once. Arena and shader failures keep cached terrain on the established renderer, a depth
failure draws the complete indirect candidate set without HZB suppression, and a draw failure
permanently disables both indirect drawers so the established renderer owns the frame and later
frames. Unknown values do nothing and warn once.

The complete fast tier passes 5,083 assertions. No terrain cache, assist protocol, blob format or
database schema meaning changed.

All four guarded failures also completed the frozen six-viewpoint game route with zero settle
timeouts on the primary AMD system. Arena, shader and draw demonstrated established-renderer
fallback; depth-copy disabled HZB while packed indirect multi-draw continued unculled. These were
automated isolated runs, not a human visual or cross-driver test.

## [0.4.0] - 2026-08-25

**The GPU-driven terrain renderer is the default path, and it has been played and accepted.**

This is the milestone the 0.3.4x-0.3.103 development series was building toward. Cached terrain
beyond vanilla view distance is now stored in regional GPU buffers, submitted as a small number of
batched indirect draws instead of one call per section, culled against a hierarchical depth buffer
so terrain hidden behind hills and ridges is never drawn, stored as 12-byte packed quads, and
subdivided into clusters so a near hill can hide far terrain within the same frame.

The owner reports the result as visually correct and the performance as a large improvement on his
machine. This is qualitative acceptance on one system rather than a measured figure; the recorded
suppression and frame-rate numbers in this file all predate the 0.3.101 correctness fix and are
retained as history, not as current measurements.

**Unsupported systems are unaffected.** The established renderer remains complete and is selected
whenever the capability probe, a shader, an allocation or a draw fails - in the same frame, without
a restart and without touching the terrain cache. `.vhgpu off` returns any client to it deliberately
and is remembered between sessions.

**The command surface is much smaller.** The switches that staged this work - `.vhphase8`,
`.vhindirect`, `.vhpacked`, `.vhclusters`, `.vhcull`, `.vhlate`, `.vhheight`, `.vhflicker` and
`.vhsplitbias` - are retired along with the code behind them, because each could select a
half-finished configuration that is not the product. `.vhgpu` and `.vhhzb` remain. Scripted
benchmark comparisons are unaffected; the environment overrides still pin either side of an A/B for
a whole run.

Nothing in the terrain cache, the assist protocol, the section blob format or the database schema
changed. An existing cache is used as-is.

The complete fast tier passes 5,067 assertions.

## [0.3.103] - 2026-08-25

**The GPU terrain renderer is now the default.** Cached terrain is drawn from regional GPU buffers
in a few batched calls, culled against a depth pyramid, packed into 12-byte quads and subdivided
into clusters, without anyone turning anything on. Unsupported hardware is unaffected: a failed
capability probe, shader, allocation or draw still selects the established renderer in the same
frame, and that path remains complete.

`.vhgpu off` turns the whole thing off and is remembered between sessions, so a client that
misbehaves can be returned to the old renderer without editing files or restarting. It is now the
only switch, and the only other command kept is `.vhhzb`.

**The sky guard is removed.** The 0.3.102 counter measured what it was costing: 169,994 refusals
over 2,495,527 sampled far commands, each one a piece of terrain the depth test had already proved
hidden and the guard drew anyway. Removing it raises far-command suppression from 26.5% to 33.3%
in that sample. It was written for a sky-silhouette theory that the 0.3.101 mapping fix superseded.

**The staging commands are retired**, along with the code behind them:
`.vhphase8`, `.vhindirect`, `.vhpacked`, `.vhclusters`, `.vhcull`, `.vhlate`,
`.vhheight` and `.vhflicker`. They existed to compare stages of an experiment that is now the
product, and each could select a half-built configuration. The armed per-command flicker capture
they fed is deleted with them; the artifact it was built for is fixed. Scripted A/B comparisons are
unaffected - the environment variables still pin either side for a whole benchmark run, and now
read as opt-out rather than opt-in.

Unrelated commands are untouched. The complete fast tier passes 5,067 assertions.


## [0.3.102] - 2026-08-24

**Cleanup and one safety net, after the flicker fix.** No visual change is intended.

`.vhsplitbias` is removed. It searched a larger safety margin for the far cached bucket, and the
4/16/64/256 comparison it existed for was answered by finding the real cause; both buckets now use
the one established four-step margin. The 0.3.99 one-texel sky guard is **retained but now counted**:
`.vhhzb` reports how many far clusters it refused to hide, so the guard can be deleted on evidence
that it never fires rather than on the argument that it should not. A run reporting zero there is
what retires it.

The depth test now fails open if its screen size is ever not the depth pyramid's own base size.
The pixel-anchored sampling introduced in 0.3.101 reproduces the pyramid's mapping only while those
two agree, which is true by construction today and checked nowhere; a future reduced-resolution
pyramid would otherwise reintroduce the flicker silently.

The culling shader's uniform locations are resolved once when the program links instead of by name
on every dispatch.

The offline `HzbField` widening measurement was re-run against the corrected mapping. Of the
sections the old narrow width could not hide, eight texels hides 18.4% and 25.6% on two camera
seeds at a 350-block modelled vanilla distance, and 16.2%/23.8% at the 192 blocks currently
configured. Changing only the camera seed moves that figure about seven points, which is more than
the mapping correction moved it, so the previously recorded `25.0%` is inside the noise of its own
measurement and is now quoted as a range. The complete fast tier passes 5,091 assertions.


## [0.3.101] - 2026-08-24

**The precise-angle terrain flicker is fixed.** Cached terrain no longer alternates between drawn
and missing at certain camera angles under `.vhphase8 clusters`. The owner has confirmed the
artifact is gone.

The depth pyramid halves each level with floor and folds the leftover odd row and column into its
last texel, so a texel at level L stands for exactly `2^L` screen pixels. Both copies of the
occlusion box test instead scaled the projected rectangle by the level's texel *count*, which is
only the same thing while `levelSize * 2^level == screenSize`. At 1440 rows the chain reaches 45
and then 22, so from level 6 upward the sampled rectangle fell one texel short at its top edge -
skipping exactly the texel that holds whatever lies beyond an occluder's silhouette. Terrain
peeking over a ridge was therefore judged hidden, which the renderer's own conservatism rule
forbids. Screen width was unaffected, since 2560 divides exactly at every level the test selects;
the error was vertical only, which is why the artifact tracked camera pitch.

Under the same-frame near/far split that one wrong verdict became a visible flicker: a wrongly
suppressed near command leaves the exact depth-clear value where its pixels should have been, and
every far cluster whose test rectangle covers that hole flips wholesale between hidden and drawn as
sub-texel noise decides the near verdict each frame. This also accounts for the two failed
remedies - the 4/16/64/256 depth-margin ladder could not close a gap between real depth and the
clear value, and the 0.3.99 one-texel sky perimeter inspects outside the rectangle while the hole
lies inside it.

The rectangle is now anchored in screen pixels and divided down by the level's own halving, with
the far edge clamped to the last texel so the odd-dimension fold is honoured. The change is applied
identically to the culling shader and to its C# reference. It can only ever widen the sampled
region, so it strictly reduces what culling hides and cannot cause missing terrain. The 0.3.99 sky
guard and the `.vhsplitbias` diagnostic are retained but are now expected to be redundant.

A new deterministic check reduces a real 1440-row pyramid and pins the peeking box, the folded
remainder, two exactly-divisible control levels, the nine-texel sampling boundary, and a box in
front of the occluder. The unfixed code failed exactly that fixture before the change was written.
The complete fast tier passes 5,089 assertions.

## [0.3.100] - 2026-08-24

**The flicker capture now follows the actual draw-state chain across both halves of the same-frame
split.** The owner found the one-texel sky guard did not cure the artifact and clarified that most
flickering meshes sit inside visible terrain rather than at a sky silhouette. The 0.3.99 capture
also contained 6,592 high-ranked transitions between two verdicts that both draw, obscuring the
8,666 transitions that actually switched culling on and off.

While explicitly armed, `.vhflicker` now captures split-near as well as split-far commands, tracks
commands disappearing before compute separately from GPU verdict changes, ranks only transitions
that can alter drawing, and labels each offender's screen region. Harmless verdict changes remain
counted in the summary but cannot displace a real offender. Eight fenced slots accommodate both
dispatches without waiting; ordinary frames remain unchanged. The complete fast tier passes 5,073
assertions.

## [0.3.99] - 2026-08-24

**The same-frame far cull now fails open at a cached-terrain silhouette instead of alternating
against sky.** A completely stationary 0.3.98 capture collected 8,416 split-far samples and
27,065,856 command observations with no dropped readbacks and exactly zero view-projection change.
Every leading offender alternated only between `occluded` and `background`; none crossed the
visible-depth threshold. The leading cluster flipped 4,021 times, with its HZB sample alternating
between terrain depth and the exact `1.0` clear value. This closes depth bias as a cause.

Only the split-far cached-on-cached verdict now checks a one-HZB-texel perimeter around an
otherwise hidden projected box. Exact clear sky in that perimeter returns `background` and draws
the cluster; non-sky perimeter depths never participate in its occlusion comparison. Ordinary
vanilla-only culling and the projected box's core depth rule remain unchanged. `.vhflicker` now
keeps its full offender list in the log but shows a concise summary in chat, and replaces the
angle-bracket transition label that Vintage Story misread as markup. The complete fast tier passes
5,071 assertions. The subsequent owner test rejected this as the visible fix: flicker remained
bad, most affected meshes were inside terrain, and 0.3.100 replaced the report's raw-verdict
ranking with actual draw-state attribution. The guard remains narrow historical code and must not
be widened without new evidence.

## [0.3.98] - 2026-08-24

**The precise-angle split flicker now has an explicitly armed per-cluster capture.** The owner's
ridge run on 0.3.97 proves the Phase 8 mechanism is working: the actual split-far command stream
zeroed 80.5% of commands and removed 77.1% of triangle indices, including 92.4%/94.8% at 4-8k,
while FPS rose from about 340 to 460 in that terrain-heavy view. The earlier 9.8% result described
the separate whole-section shadow classifier in a different view, not broken live culling.

The remaining problem is therefore narrow correctness, not whether to retain the split or
clusters. `.vhflicker on` records the actual split-far verdict, nearest box depth, farthest HZB
depth, mip, and texel rectangle for every enabled cluster command, aligned with stable section and
4x4-cell identities. Fenced four-slot readback never waits. `.vhflicker off` ranks repeated
verdict transitions and separates `occluded<->background` sky-edge changes from
`occluded<->visible` depth-threshold changes while reporting camera-matrix drift. The large result
stream is completely dormant until armed; any capture failure disables only the diagnostic.
The complete fast tier passes 5,066 assertions.

## [0.3.97] - 2026-08-24

**`.vhhzb` now reports what the live GPU cull actually removes from the real draw-command
stream.** The earlier percentages came from a separate whole-section shadow classifier, so they
could not establish whether cluster verdicts reached the multi-draw. The real compute pass now
samples its exact enabled command slots asynchronously, once per 30 dispatches per bucket. It
reports commands zeroed, triangle indices removed, background/sky refusals, undecided verdicts,
and all four figures by distance for the ordinary, split-near, and split-far paths. Each
denominator is the enabled command or index workload in sampled frames, never all terrain resident
in memory.

The counter buffers are fenced and polled without waiting; an unfinished sample is kept for a
later frame, and any instrumentation failure disables only the counters while culling and drawing
continue unchanged. Unsampled frames execute no counter atomics. The active packed drawer now also
supplies `.vhcull`'s last-frame status instead of that status always consulting the expanded
drawer. The complete fast tier passes 5,038 assertions.

## [0.3.96] - 2026-08-24

**The same-frame split now has a live safety-margin diagnostic for its cached-on-cached depth
verdict.** The controlled Phase 8 comparison measured the same 386 FPS under `late` and `cull`,
but the known precise-angle flicker appeared only under `late`. This localizes the artifact to the
far cached bucket testing against the freshly drawn near cached bucket; it does not reject the
split or clusters, because clusters provide the smaller units that let near cached terrain hide
farther cached terrain despite whole-section sky overlap.

`.vhsplitbias 4|16|64|256` changes that far-bucket margin live without rebuilding meshes or
weakening ordinary vanilla-only culling. Four preserves the established 0.3.95 behavior, and the
setting is deliberately not saved. The complete fast tier passes 5,018 assertions.

## [0.3.95] - 2026-08-24

**The closest terrain band now receives most of the bounded sharpening allowance.** The accepted
inner foundation previously split loading pressure evenly with the outward wave, even though the
player judges the nearby L0 result first. The total ceiling remains unchanged at 32 unresolved rows
and eight new requests per frame, but the foundation now receives 24/six while the outward wave
retains eight/two.

The outward reservation is deliberately one outstanding row per radial lane, so accelerating the
near band cannot freeze a direction or return to camera-dependent loading. Four additional policy
assertions pin the dominant near share, unchanged total pressure, and continued outward progress;
the complete fast tier passes 5,013 assertions. The owner reports everything good and accepts the
final pacing; cache startup/refinement is complete and Phase 8 resumes.

## [0.3.94] - 2026-08-24

**Newly loaded cached terrain now waits for its real climate and seasonal colours before its
first reveal.** Faster radial loading exposed a second startup race: the initial tint refresh could
finish before any cached palette registered its grass, foliage, or water maps. Those later slots
started as identity white, then remained that way until the ordinary 30-second refresh, making the
horizon visibly snap into colour long after its geometry appeared.

New tint slots now bypass the ordinary cadence and enter the existing one-slot-per-frame sampler
immediately. A section whose palette uses an unpublished slot retains its exact mesh obligation;
coarser parent coverage remains live, while untinted terrain proceeds normally. The completed tint
table is still published atomically, and `.vhinfo` reports ready versus registered slots. Eight
focused assertions pin late-slot wakeup and the appearance gate; the complete fast tier passes
5,009 assertions. The owner accepted the result in game: terrain now reveals with its proper
colours while the radial loading behavior remains intact.

## [0.3.93] - 2026-08-24

**The terrain beneath the player now has its own fast refinement foundation.** The first radial
candidate began outside vanilla's draw range, so cached terrain hidden under vanilla could remain
coarse until movement exposed it. The owner found that reveal jarring and also found the nearest
visible ring took longer than the eye expected to reach final detail.

The first configured LOD band now plans from the player outward through L0 independently of the
larger radial wave. It and the outward planner each retain eight-direction fairness while splitting
the previous allowance: 16 outstanding rows and four new requests per frame apiece, preserving the
same combined 32-row/eight-request ceiling. The inner foundation can therefore finish its required
parent gates and finest rows without waiting behind every outward band, while distant coverage
continues progressing. The complete fast tier passes 5,001 assertions. The owner accepted the
under-player detail and outward sharpening behavior in game; the faster reveal then exposed the
separate delayed-tint race fixed in 0.3.94.

## [0.3.92] - 2026-08-24

**Cached terrain now starts and refines as an orientation-independent radial wave.** Exact stored
rows are separated from synthetic quadtree ancestors, then admitted through eight equally serviced
radial lanes. Nearby coarse coverage starts first; each next wave combines one nearer refinement
step with the next outward band's coarse arrival. Camera direction is absent from demand ordering,
so turning can change what is drawn but cannot create a previously untouched load wave.

The planner runs before the renderer's empty-mesh return, breaking the zero-work join cycle. It
filters work to exact rows in the active radius and levels no finer than distance currently needs,
retains the existing parent-until-children-ready rule, and admits at most 32 unresolved rows rather
than dirtying the complete cache. The Release build is warning-free and the complete fast tier
passes 4,996 assertions. Runtime timing, visual wave quality, memory, and turn-around behavior await
the owner's 5,317-section playtest.

**Cache startup and refinement are now the top development priority; Phase 8 testing is paused.**
The owner's 5,317-section cache reproduced the loading failure decisively: at ten seconds the mod
knew every cache key but had 0 render-dirty sections, 0 loads, 0 mesh jobs, and 5,101 frames skipped
for want of any mesh; at thirty seconds it still had no mesh or queued work. The first mesh arrived
at 75.1 seconds. Source tracing confirms a circular bootstrap—the selection walk creates mesh/load
demand but refuses to run until a mesh exists—and a separate orientation rule that rejects
behind-camera subtrees before they can request data. A dedicated plan now targets bounded immediate
bootstrap, 360-degree coarse coverage, then stable near-to-far refinement without requiring the
player to turn around. Version 0.3.92 is the first source/harness-complete implementation and now
awaits in-game evaluation.

## [0.3.91] - 2026-08-24

**Moving between Phase 8 presets no longer rebuilds every regional mesh.** The first 0.3.90
`late` test correctly selected that stage, but the command also re-requested arenas that were
already on and queued all 1,678 live sections for re-meshing. That made the 362 FPS observation a
warm-up-contaminated number and would have repeated at every ladder step. Active presets now share
their filled buffers; only a transition to or from `off` changes arena ownership. The no-argument
report also describes the effective path for the selected stage instead of always describing the
disabled cluster path.

The visual observation remains valid: the `late` preset reproduced the single older flickering
section but not the more numerous flickers introduced by `clusters`. The next clean adjacent test
is `cull`, which removes the same-frame near/far split while retaining whole-section HZB culling.

## [0.3.90] - 2026-08-24

**The single Phase 8 command now preserves the experiment's stages.** `.vhphase8` accepts
`off`, `batch`, `cull`, `late`, `packed`, and `clusters` as a chronological preset ladder. Each
preset assigns every dependent switch, reports the exact stage and component states, and writes
that evidence to the log. `.vhphase8 on` remains an alias for the complete `clusters` preset.

This follows the first valid 0.3.89 comparison: the log confirms the complete cluster stack was
active, then the complete legacy baseline was active. The owner saw cluster flicker return and the
legacy baseline run faster. That does not erase the substantial wins measured in earlier renderer
phases; it proves only that one or more additions between those winning stages and the complete
stack regressed the result. The preset ladder can locate that boundary without asking the player
to reconstruct seven commands or treating all Phase 8 work as one variable.

## [0.3.89] - 2026-08-24

**Phase 8 now has one master switch.** `.vhphase8 on` enables the entire experiment in dependency
order: regional arenas, batched drawing, packed quads, HZB, GPU depth culling, the same-frame
near/far picture, and 4x4 clusters. `.vhphase8 off` disables the complete stack for the legacy
baseline. With no argument it reports `ON`, `OFF`, or `MIXED`, names every component, says whether
the cluster path is actually active, and writes the same evidence to the log.

This replaces an error-prone seven-command setup. The first 0.3.88 performance follow-up was not a
valid cluster comparison: its log shows late depth initially off, then the hidden share rising from
0.5% to 4.6% after it was enabled, while clusters remained `on, but idle` because packed drawing was
off. The artifact fix remains in place; its retained performance now needs a correctly controlled
master-switch comparison.

## [0.3.88] - 2026-08-24

**GPU depth culling now fails open on borderline depth.** The owner found that normally shaped
terrain pieces could flicker at precise camera angles, and that `.vhcull off` stopped every case
with clusters both on and off. This clears the cluster geometry and identifies the shared depth
verdict as the fault: it treated any difference greater than zero as proof that a draw was hidden,
even when projection, rasterization, and depth-buffer rounding differed by only a few representable
steps.

The CPU reference and both compute paths now reserve a four-step 24-bit depth safety band. A box
inside that band draws; terrain with a real depth separation can still be cancelled. An existing
offline fixture already reproduced the exact one-last-bit self-occlusion and now pins its repair,
along with the shader/reference agreement. The cluster path remains opt-in while visual stability
and the retained performance gain are tested.

## [0.3.87] - 2026-08-24

**Phase 8 clustered terrain is ready for an opt-in playtest.** The packed renderer can now
split each opaque section into a 4x4 grid of exact geometry ranges. Each populated cell has
its own conservative bounds and indirect command, so a ridge that overlaps open sky in one
part of a section no longer forces every hidden part of that section to draw.

Turn it on with `.vhclusters on` after `.vhgpu on`, `.vhindirect on`, `.vhpacked on`,
`.vhhzb on`, and `.vhcull on`; `.vhclusters off` returns immediately to the accepted
whole-section packed path. The benchmark equivalent is `-GpuClusters 0|1`. The old packed
stream, expanded batching, and the established renderer remain complete same-frame fallbacks.
The clustered geometry and command path pass 1,048 focused assertions, and the full fast tier
passes 4,959 assertions. Visual parity and real GPU cost/saving remain human-test gates.

## [0.3.86] - 2026-08-24

**Packed terrain now reuses four decoded corners per quad.** The first 0.3.85 experiment looked
identical but reduced the owner's same-scene frame rate from about 300 to 260 FPS. It saved memory
while making the vertex shader reconstruct six corners for every quad, where the expanded indexed
path processed only four unique corners.

This revision keeps the exact 12-byte records but draws them through one small reusable index
pattern. Each packed quad is decoded four times and the graphics card reuses those results for its
six triangle indices. The 0.3.85 draw-arrays implementation is rejected. In the owner's same-scene
comparison, 0.3.86 looked exactly the same and held exactly the same FPS with packing on and off.
That accepts the indexed format on the primary driver: it is not a standalone FPS win in this
scene, but no longer erases the compact representation's benefit. `.vhpacked` remains opt-in while
paired-route telemetry, a second driver, and retirement of the temporary expanded regional mirror
remain open.

## [0.3.85] - 2026-08-24

**Packed opaque terrain is ready for an opt-in playtest.** Greedy quads now have an exact
12-byte regional representation instead of four vertices plus six indices (88 bytes), an 86.4%
reduction. The worker writes packed records directly, a bounded companion arena publishes them,
and the shader reconstructs the same six triangle vertices. Turn it on with `.vhpacked on` after
`.vhgpu on` and `.vhindirect on`; `.vhpacked off` returns to expanded batching for an immediate
same-view comparison.

This is deliberately not a default yet. Expanded batching and the established renderer remain
complete same-frame fallbacks if packed geometry, a GPU page, the shader, or a draw is unavailable.
The packed path is source- and harness-tested; this package exists to establish visual parity and
real-driver timing before the temporary dual regional representation is retired.

## [0.3.84]

Human-played and accepted on the primary machine.

**Distant terrain now hides other distant terrain from a same-frame depth picture.** The opaque
pass is split into near and far buckets; after the near hills draw, the mod refreshes its depth
snapshot and uses it to cancel hidden far draws. This replaces the one-frame-old experiment and
removes its stale-picture policy, camera-delta re-base and turning guard. The owner reports the
flicker is gone and the result runs very well.

## [0.3.83]

Human-played. Twelve versions (0.3.72-0.3.83) covering one feature, one memory fix and four
diagnostics that were reporting on themselves rather than on the game.

**Distant terrain can hide other distant terrain.** `.vhlate on` takes the depth snapshot at the
end of the frame instead of before distant terrain is drawn, so the mod's own hills become
occluders. From a hilltop looking out, the old arrangement hid nothing at all; this hides 8% of
pieces within a kilometre, 20% at one to two, and 35% at two to four, for the same cost. Off by
default and remembered per install.

**The pool holding distant terrain sizes itself from your draw distance.** It was a fixed 256 MB,
which was refusing a third of the terrain and quietly sending it down the slower path, so the
faster drawing could only help part of the screen. 300 to 480 FPS at the same spot after the
change. It is a limit rather than a reservation, so a distance you never reach costs nothing.

**Depth measurements are readable in an ordinary session.** They previously recorded themselves
only under the benchmark script, so a normal run reported zero and looked free rather than
unmeasured. Switching the depth feature on now starts the clock, and the report says whether its
own figures are real.

**Fixed:** the end-of-frame snapshot was never being taken at all (the engine refuses a second
shader mid-pass); the pass that measures hidden terrain was discarding 99.9% of its own results at
six times the cost of the thing it measured; the culling shader was created too late to report its
own state; and the timer counted frames where nothing was drawn, reporting a fifth of the real
cost.

**Known:** with the end-of-frame snapshot on, a few pieces of distant terrain can flicker while the
camera turns. The next change takes the snapshot during the same frame instead, which removes the
cause rather than guarding against it.

## [0.3.71]

Packaged and installed; not yet run.

**The GPU render path is remembered between sessions.** `.vhgpu`, `.vhindirect` and `.vhcull`
are saved per install rather than retyped every session. They stay off by default for a new
install, and restore in dependency order so a switch is never left on with nothing under it.
`.vhcull on` also switches on the depth pyramid it needs - without that it sat idle, which is
what cost the 0.3.69 test its meaning.

## [0.3.70]

Human-tested. Depth culling confirmed active for the first time.

**Fixed a counter that could only ever read zero.** The figure reporting what the wider depth
sampling bought was gated so that its two conditions were mutually exclusive; it reported "no
benefit" for a full playtest. Corrected, it shows 248 of 404 hidden pieces exist only because
of that change. **`.vhcull` now writes its status to the log**, since game chat cannot be
copied out and a test run that cannot be shown to have tested anything is a wasted run.

## [0.3.69]

Human-tested; looked correct, but could not be attributed - see 0.3.70.

**Depth verdicts can stop terrain being drawn** (off by default). The card cancels the drawing
of hidden pieces in the same frame the decision is made, rather than a frame later, so the
world cannot move on from the answer in between. Every failure path draws everything.

**The hidden-terrain test samples eight texels per axis instead of two**, chosen by measuring
both candidate fixes offline against a real terrain cache.

Also: restoring graphics state after the mod's compute work now covers both buffer slots it
binds rather than one, and the batched path releases the occlusion-query objects it had been
retaining unused for whole sessions.

## [0.3.68]

In development, not yet run. Measurement only; nothing is hidden.

**The mod now weighs the two possible answers to the same problem, side by side.** Distant
terrain often cannot be marked hidden because the piece being tested overlaps open sky. There
are two ways to fix that: draw terrain in smaller pieces, or look at the depth picture in
finer detail. The first changes how terrain is built and drawn; the second changes only the
test. `.vhhzb` now reports what each would win, over exactly the same set of pieces, so the
choice can be made from one number rather than an argument.

Offline, on a synthetic hillside, looking in finer detail found about a quarter more hidden
pieces than the current setting. Whether that holds on real terrain is what the new report is
for. Nothing has changed yet - both figures are measurements.

**The mod also now says WHY it could not judge a piece:** too close to the camera, off the
side of the screen, or a shape it could not work with. A run where half the pieces came back
unjudged could not say which, and that turned out to matter - it was the ring of terrain
right around the player, which can never be judged and which was quietly distorting the
figures.

## [0.3.67]

In development.

**Fixed a 160-millisecond stutter** the first time `.vhhzb on` was used. That was the mod
compiling a small program for the graphics card, which happens once - but it was being timed
as though it were the per-frame cost, and reported as such.

**`.vhhzb why` now answers about something useful.** It was landing on the piece of ground
right beside you, which can never be judged because part of it sits behind the camera. It now
walks past those to the first piece it can actually say something about.

## [0.3.66]

In development, not yet run. Measurement only; nothing is hidden and the picture is unchanged.

**The mod now measures how much it would gain from testing smaller pieces.** Every distant
piece it could not hide gets its footprint split into a 4x4 grid, and each of the sixteen
cells is tested on exactly the same terms as the whole. The share that comes back hidden is
what the mod would save if it drew terrain in smaller units than it does today - reported by
`.vhhzb` as a headroom figure, over precisely the pieces that currently save nothing.

This exists because of what 0.3.65 measured: every piece that was not hidden failed for the
same reason, that its rectangle overlaps open sky. A piece is 64 to 1,024 blocks across, so
part of it reaches past whatever ridge is hiding the rest. The question of whether to build
smaller draw units is a real one, and it should be answered with a number rather than an
argument.

The height is deliberately not split - only the footprint. A piece is refused for being too
wide, not too tall.

## [0.3.65]

In development. Nothing is hidden yet; the picture is unchanged.

**The mod can now find terrain hidden behind hills, and it says how much there is.** After
the game draws its own nearby world, the card holds a picture of how far away everything on
screen is. The mod copies that, shrinks it into a stack where each step remembers the
farthest thing it covers, and then asks - for every piece of distant terrain at once -
whether the whole piece sits behind what is already drawn. Turn it on with `.vhhzb on`, play
for a few seconds, and run `.vhhzb` to see what it found.

**It hides nothing.** This whole stage exists to measure, because the idea only pays if
finding the hidden terrain is cheaper than drawing it. On the test machine the stack costs
about 27 microseconds of card time per frame, against roughly 219 microseconds spent
submitting the distant terrain it could remove - about an eighth.

**What it found, standing on the ground in front of terrain:** a bit under half of distant
pieces hidden within 1 km, rising to essentially all of them past 2 km, across sixteen
million checks at two locations. Standing above the landscape looking at open horizons it
found nothing at all, which is the honest answer for that view.

**One limitation is now measured rather than suspected.** A piece of terrain is only
counted as hidden if every pixel it covers is already drawn over - and sky counts as
"nothing drawn". Since a piece is 64 to 1,024 blocks across, it nearly always pokes into
open sky somewhere, and that makes it unhideable however buried its ground is. Every single
piece that was not hidden failed for exactly this reason. Testing smaller pieces is what
unlocks the rest.

**`.vhhzb why`** points at whatever you are looking at and explains its verdict in words.
Both commands write to the log as well as the screen.

**Also:** `.vhheight` was printing nothing, because the game reads chat as markup and the
`<=4` labels looked like an unclosed tag. And the mod stopped claiming its shaders had
failed on every startup - the first attempt happens before the game has read the mod's
shader files and could never work, which made a real failure indistinguishable from noise.

## [0.3.60]

In development, not yet human-tested. Nothing looks different; nothing is hidden.

**The mod can now build the thing that finds terrain hidden behind hills.** After the game
finishes drawing its own nearby world, the graphics card is holding a picture of how far
away everything on screen is. The mod now takes a private copy of that picture and shrinks
it repeatedly into a stack of smaller and smaller versions, where each pixel of a smaller
one remembers the FARTHEST thing in the patch it stands for. That stack is what lets a
later step ask, about a whole piece of distant terrain at once, "is all of this behind the
hill in front of it?" - cheaply, and without drawing it to find out.

**It decides nothing yet, on purpose.** This step exists to measure what the stack costs,
because the whole idea only pays if building it is cheaper than the drawing it saves, and
no amount of reading the code answers that. It is off by default: `.vhhzb on` turns it on
for a session, `.vhhzb` alone reports its state, and the log grows an `hzb:` line with the
graphics-card time it takes.

Two choices in it are worth knowing about, because both are the difference between working
and quietly deleting scenery. Each smaller pixel takes the farthest of what it covers, never
the average the graphics card would give for free - an average reads as nearer than some of
what is under it, which would hide ground you can actually see through a gap. And the whole
thing refuses to run at all if the game is ever found measuring depth backwards, rather than
guessing and inverting every comparison.

## [0.3.59]

In development, not yet human-tested. Fixes two things the 0.3.58 playtest exposed.

**`.vhheight` printed nothing in chat, and that was a bug in the reply, not the feature.**
The height buckets were labelled `<=4`, `<=16` and so on. The game reads chat text as
markup, so a leading `<` opened a tag it could never close and it refused the whole reply.
The labels are ranges now - `0-4`, `4-16`, `64-256` - and the same line always worked in
the log, which is where the 0.3.58 numbers came from.

**The mod cried wolf about its own shaders on every single start.** It asks the game for
its shaders once when it loads, which is before the game has read the mod's own shader
files - so that first attempt could never work, and the mod logged "shader failed to
compile; LOD rendering disabled" and a matching warning about the batched-drawing variant.
The game then reloads shaders with mod files present and the second attempt succeeds, which
is why terrain drew perfectly well regardless. The errors were noise, and the kind that
matters: reading them in a log is what made batched drawing look broken on this machine
when it is not. The first attempt is quiet now and says what it is waiting for; anything
after it is reported at full volume. The mod also says once, out loud, when the batched
variant compiled - so the log can answer "did it work" rather than only "did it complain".

**And the height report now says where a piece of terrain starts and ends, not just how
tall it is.** 146 blocks tall means something quite different if it runs from y=5 to y=151
than if it runs from y=60 to y=206, and only the first of those is worth acting on.

## [0.3.58]

In development, not yet human-tested.

**Distant terrain is now bounded by how tall it actually is.** Until now the mod judged
every piece of cached terrain against a box running from bedrock to sky, because a piece
never recorded the height of the ground inside it. Sideways that costs nothing - the left
and right edges of your view do the work. Looking up at the sky or down at your feet, it
meant the mod kept working on pieces of ground nowhere near the screen. It now measures
the real top and bottom of what it draws, which it gets for free from work it already does,
and uses that instead. Nothing changes in your cache files; an existing world derives the
new measurement as its terrain loads, like any other.

Solid ground and water are measured separately, since a lake surface sits nowhere near the
lakebed under it and one box round both would give most of the saving straight back.

**And the mod now says out loud how tall your terrain is.** The periodic log line reports
the distribution - the average height of a piece of drawn terrain as a share of the whole
world, and how many pieces the new box rejected that the old one would have kept. That
number decides whether the next stage of the render plan is worth building at all: the
stage after this one hides terrain behind mountains, and it cannot hide a piece that
reaches from bedrock to sky. Nobody knew which of those an ordinary piece of terrain was.

`.vhheight off` puts the old bedrock-to-sky box back for an immediate side-by-side, and
`.vhheight` on its own prints the distribution. If terrain ever vanishes while you look up
or down, that switch is the one to reach for, and the answer is worth reporting.

## [0.3.57]

In development, not yet human-tested. Nothing changes unless you turn it on.

**The batched-drawing comparison can be run as a script rather than typed.** The benchmark
runner takes `-GpuIndirect 0|1`, so the two sides of the comparison are two runs of the same
route with exactly one thing different between them, instead of a chat command flipped by
hand halfway through a session. `.vhindirect` still exists for looking at the two live.

**Distant terrain can now be drawn in a handful of big batches instead of one draw per
piece.** Turn it on with `.vhgpu on` and then `.vhindirect on`; it is off by default and
not saved between sessions. Measurement on this machine said the same view that takes 87
to 182 separate submissions would take 8 to 17 batches, so this is where that saving gets
spent for real. The picture should be identical either way - if anything looks different
with it on, that is a bug worth reporting, and turning it off restores the old path
immediately.

Two things to know while testing it. The delayed occlusion that skips hidden terrain is
suspended while batching is on, because it needs one draw per piece to ask its question;
so compare the two with `.vhtemporal off` on both sides if you want the batching alone.
And pieces the GPU buffers do not hold are still drawn the old way in the same frame, so
partial coverage costs speed rather than terrain.

**Under the hood: one shader, two builds of it.** The shader that draws cached terrain now
lives in a single body file that both paths include, differing only in where each piece's
position and size come from - uniforms for the established path, a buffer for the batched
one. That is what lets "the batched path must look identical" be a real test instead of a
promise.

**Two fixes found by reviewing the above before anyone ran it.** Switching batching on or
off now throws away the hidden-terrain answers collected under the other path, which
would otherwise have shown as missing terrain right after switching back - in exactly the
comparison this switch exists for. And running `.vhgpu` a second time no longer strands
the previous drawing buffers on the graphics card.

**The log can now see the micro-hitches you have been describing.** Nothing in the mod
could measure them: its hitch counters only trip at 25 milliseconds and up, and a whole
frame at 400 FPS is 2.5 milliseconds, so a spike worth a sixth of a frame was counted by
nothing at all. The periodic stats line now carries a `frame timeline:` entry that measures
the gap between one frame and the next, how much of each frame was this mod, and how far
each frame ran over its own recent baseline.

The number to look for is how many frames stood out and how many of those had our own work
elevated too. If the two are far apart, the hitches are not this mod's doing and the next
session should look elsewhere - which is worth knowing before more time goes into our own
code. It measures itself against the frames around it rather than a fixed threshold, so it
means the same thing at 400 FPS as at 60, and a world load does not register as a hitch.

Just play normally with it on; the numbers land in the client log every fifteen seconds.

**Why one world takes a minute to show its horizon, measured rather than guessed.** Your
logs hold six joins. Five of them show the first hundred pieces of distant terrain within
two to nine seconds. One - the big 457 MB world - took sixty seconds, and thirty seconds in
had built nothing at all, with every queue in the mod empty. That is not a slow version of
the others; the mod was not working slowly, it was not asking for anything.

Enough of the mechanism is now established to know where to look: the renderer skips the
walk that asks for terrain until it has at least one piece built, so the very first piece
has to come from somewhere else. In that world it never did. Why is still open, and one log
cannot tell "nothing was ever queued" from "everything queued was still in flight" - which
is exactly the difference that decides the fix.

So this build measures it instead of guessing. If nothing has been built ten seconds after
joining, the log now carries one `Join:` line naming which stage is idle, and there is a
`first mesh after Xs` milestone before the old hundred-mesh one. Join the big world once,
normally, and that line settles it.

## [0.3.52]

In development, not yet human-tested.

**The far edge of the cache now dissolves into the sky the game actually draws.** Every
engine shader asks for sky colour with a daylight value that is not the plain daylight
strength this mod was passing, so the band where cached terrain fades out did not match the
sky behind it - about 20% too dim at dusk and about 60% too bright on a moonlit night,
identical in full daylight. Behind `.vhlight sky`. Not yet human-tested.

**Cached terrain is rebuilt less often when it changes.** When one stored section changed,
the mod rebuilt that section's mesh and all four of its neighbours' meshes, whether or not
the change was anywhere near them - and repeated that at every zoom level, so a single
change could cost up to 35 rebuilds. It now checks which edges of the section actually
moved and only rebuilds the neighbours across those edges. A change in the middle of a
section costs one rebuild instead of five.

**Measured on the frozen benchmark profile, three runs:** 38-40 real content changes
produced 77-80 mesh rebuilds - 2.00 per change against 5.00 before, a 60% cut - with no
frame-rate cost (367-432 FPS against 363-430 for the same route before the change).

**A correction to an earlier claim recorded here.** This changelog previously said that
standing still cost 5,057 mesh rebuilds and about 90 MB of GPU upload every fifteen
seconds, continuously. Measuring it properly shows that was the loading period, not
standing still: for the first two and a half minutes after joining the mod builds the 781
meshes for 3,291 cached sections, at about 237 MB per fifteen seconds, and then it stops.
Once settled, a standing camera costs about **2 MB and three mesh rebuilds per fifteen
seconds**, with many fifteen-second stretches doing none at all. The original figure was a
warm-up measurement labelled as a steady-state one.

So the saving above is real but small in absolute terms, and re-meshing is no longer a
plausible explanation for the frame-time hitches - that hunt moves elsewhere.

## [0.3.51]

**Distant terrain is lit the way the game lights terrain, and sunset and night no longer
drift away from it.** Cached ground matched vanilla in good daylight and diverged further
and further as the day went on, ending up visibly wrong at dusk and at night. The cause was
the light colour: the game tints terrain with an ambient colour built from its reflect
colour, which is deliberately floored at a blue night tint once the sun goes down, while
this mod tinted with the sun colour, which has no such floor and stays orange. After
sundown the two ended up close to opposite hues. Human-tested and reported substantially
better.

Three smaller matching errors were found and corrected in the same pass: the game brightens
all terrain by 22.7% while the sun is high and fades that out as it sets, which this mod did
not do; slopes facing away from the light were 22% too bright at every hour; and at night the
game lights from the moon while this mod still lit from a sun below the horizon.

Established by decompiling the client and decoding the game's own sunlight ramp offline,
with no game run. The earlier leading theory - that the light direction drifted apart
through the day - was disproven by the same reading.

`.vhlight` toggles each correction separately (`moondir`, `ramp`, `ambient`, `boost`,
`sky`, or `all`), so the old lighting remains available for comparison.

## [0.3.50]

In development. The measurement below is game-observed on the primary machine; nothing
about how terrain is drawn has changed.

**Measured: a regional GPU renderer would cut cached-terrain submissions by about 91%.**
On the BodanBoys world at a fixed six-view route, 87-182 opaque submissions per frame
collapse to 8-19 multi-draw batches - roughly eleven times fewer. That clears the 90% bar
the renderer plan set for this stage, so the visible fast path is worth building. Frame
rates were identical to a run with the measurement off, so the shadow itself costs nothing
observable.

**Buffer size is the lever, and it is now a setting.** A batch is one pair of buffers, so
how many on-screen sections can share a batch follows from how many fit in a buffer. At the
original 8 MiB the reduction was only about four times; at 32 MiB it is eleven.
`VINTAGEHORIZONS_GPU_ARENA_PAGE_MB` sets it, so tuning costs a run rather than a build.
Larger buffers pack less efficiently, so a larger ceiling is needed with them.

**Fixes to the measurement itself.** Sections that were drawn but had not made it into the
buffers were silently ignored rather than counted, so a partial mirror reported a
flatteringly small batch count; coverage is now reported as a percentage beside every
result. The memory budget was split between geometry and index buffers by their byte ratio,
but buffers are allocated in pairs, so the index side capped the whole mirror while still
40% empty; the split now follows buffer size. Searching a full page set for room is no
longer counted as an allocation failure.

## [0.3.49]

In development. Source- and harness-tested; nothing here has been observed in game yet.

**Cached terrain is now copied into the large shared GPU buffers a future renderer would
draw from, purely to measure it.** Today every cached piece of terrain is handed to the
graphics card as its own small parcel, so the cost of handing them over grows with how many
there are. The mod can now also pack that same geometry into a few large shared buffers,
grouped by area of the world, and report how few submissions would be needed to draw it.
Nothing is drawn from those buffers and no pixel changes; the established renderer keeps
full authority.

`.vhgpu on` switches the measurement on, `.vhgpu` alone reports what it found, and
`.vhgpu off` releases every buffer. `verify` additionally reads each stored piece back off
the card and compares it byte for byte. It is off by default, is not saved, and is refused
outright on a driver that has not passed the capability checks. Replaced geometry is held
until the graphics card confirms it has finished with it, reclamation is capped per frame
so it can never stall one, and the whole thing lives under a memory ceiling
(`VINTAGEHORIZONS_GPU_ARENA_MB`, 256 MiB by default) because the geometry necessarily
exists twice while it is on.

## [0.3.47]

In development. Telemetry, runtime compute/SSBO validation, and private depth-copy/mip
allocation are game-probed on the primary machine; controlled performance baselines remain
open.

**The renderer now has a legacy-only dual-path boundary for the next GPU phases.** Mesh
publication/removal, frame preparation, opaque/water drawing, world clear and disposal pass
through one coordinator whose visible target is fixed to the established renderer. An
opt-in, CPU-only shadow can mirror section identities and geometry counts after capability
validation, but owns no GL resources and is never asked to draw. Shadow faults disable only
the mirror. World, section-render and resource generations make stale ownership explicit,
and the disposable GPU probes now share exact, verified GL-state restoration. Source and
1,627 fast assertions pass; runtime equivalence remains owner-tested.

**Phase 0 GPU feasibility measurement is now available without changing rendered pixels.**
An opt-in benchmark mode records delayed, nonblocking GPU timings for cached opaque and
water passes plus draw counts, submitted geometry, live mesh bytes, advertised GPU
capabilities and the active depth attachment. The first isolated run on the owner's Radeon
RX 9070 XT confirmed that the instrumentation works and that the chosen generic landscape
camera angle is suitable for repeatable probes.

The entry-point follow-up validated the required advanced OpenGL entry points, compiled and
dispatched a minimal compute shader, read the expected SSBO value and restored the prior
program and generic/indexed SSBO bindings. It cannot activate the proposed fast renderer;
the complete legacy path remains selected.

The private-depth follow-up allocates a same-format, viewport-sized depth texture with a
complete mip chain, blits the active vanilla depth into level zero, verifies framebuffer
completeness and restores the incoming framebuffer and texture bindings. The disposable
probe does not retain the copy or construct a conservative HZB. The isolated primary-machine
run validated a 2,560x1,440 `DEPTH_COMPONENT32` copy with 12 mip levels and no GL errors.
The later uncapped Bodanboys pair is retained only as open-horizon GPU-cost evidence: its
aerial camera placed essentially no cached terrain behind vanilla terrain, so it is not a
temporal-occlusion comparison. The owner now owns FPS baselines and noise-floor evidence.

**LOD cache writes are now coarse checkpoints instead of a stream of tiny transactions.**
Dirty sections remain authoritative in RAM. Each active client or server pipeline starts a
checkpoint no more than once every 30 seconds, freezes at most one section per tick, and
hands at most 256 coalesced snapshots to the storage worker for one SQLite transaction.
Compression, serialization and disk I/O remain off the game thread; leaving a world still
forces an immediate final flush. Exact revision acknowledgements keep mutations made during
a checkpoint dirty for the next one.

**Periodic collection-sized maintenance has been replaced with rolling work.** Seasonal
colour sampling now starts every 30 seconds, updates one tint slot per frame, and publishes
the finished table atomically. GPU mesh eviction checks four resident keys per frame rather
than scanning every mesh every 300 frames, while CPU section eviction checks two keys per
game tick instead of scanning the resident dictionary every five seconds. Server manifest
follow-up scans now match the 30-second persistence cadence.

**The remaining safe SQLite work has left the game thread.** Integrated-singleplayer
sibling-cache blob reads now use a bounded, below-normal read-only worker. OpenGL visibility
queries cannot leave their render context, so they are capped at eight new queries and
sixteen result checks per frame instead. The complete fast tier passes 1,555 assertions and
the Release build is warning-free; no game process was launched for this change.

## [0.3.40]

In development. Automated and source verification are complete; revised in-game layout
acceptance is pending.

**The `.vhconfig` scales are now tighter and more precise after the first in-game review.**
The LOD markers are larger, sit farther from the shared track, and carry their L1-L6 names
inside the handles. Both scales stop at 32,768 blocks, every displayed value uses the full
block number rather than abbreviated `k` notation, and cached draw distance moves in
512-block increments. `Defaults` now restores a 32,768-block draw distance.

## [0.3.39]

Human-tested. The window opened and its first layout received concrete revision requests.

**`.vhconfig` now opens a player-facing LOD settings window.** L1-L6 appear as independently
draggable markers on one logarithmic distance scale. Each marker stops before its immediate
neighbours, keeping the terrain policy ordered, while a separate slider sets the maximum
cached-terrain draw distance. `Defaults` restores the accepted 512 / 1,024 / 2,048 / 4,096 /
8,192 / 16,384 thresholds; `Cancel` discards edits and `Save` applies them live and persists
them.

Older one-distance configuration files migrate to the equivalent doubling sequence.
`.vhdetail` remains as a quick command for resetting that sequence, and no cache, database,
blob, or assist-protocol migration is involved.

## [0.3.38]

Human-tested and accepted. The owner reported an enormous performance increase with very
little visual-fidelity loss.

**The configured detail distance is now the first real LOD transition.** With the default
`.vhdetail 512`, full one-block horizontal detail ends and L1 begins at 512 blocks; L2-L6
then begin at 1,024, 2,048, 4,096, 8,192, and 16,384 blocks. Previously every transition
occurred twice as far away as the command and configuration described. Distance remains
measured to each square section's nearest edge, so a section crossing the nominal circular
boundary stays finer instead of bringing coarse terrain prematurely toward the player.

## [0.3.37]

In development. The delayed occlusion default and its aggressive visual tradeoff were
accepted in game on the owner's machine.

**Cached solid terrain proven hidden by hills is no longer submitted every frame.** The
renderer now wraps selected ordinary opaque terrain draws in asynchronous GPU visibility
queries after vanilla terrain has populated depth. Results are consumed only after the GPU
reports them ready; a zero-sample result skips later submissions, and a periodically drawn
exact mesh doubles as both the visibility probe and the correct terrain for that frame. No
proxy boxes, same-frame wait, or conditional draw dependency is involved.

This is deliberately different from the rejected 0.3.30 prototype. That experiment queried
extra boxes and tried to consume their answers in the same frame, so its overhead replaced
its saving. The accepted path measures real terrain now and reuses the answer later. In the
owner's roughly 4,000-block hill view, the first stationary version raised about 170 FPS to
nearly 500. The accepted motion policy produced roughly 250-350 FPS while moving and turning,
with the exact result varying by area. These are owner-observed playtest ranges, not a
controlled benchmark.

The default `aggressive` profile keeps hidden results through camera turns, rechecks hidden
terrain every four frames while turning, and invalidates after two blocks of translation.
Cached sections at the vanilla/cache ownership seam never inherit a whole-section hidden
answer. A narrow left/right screen-edge band also draws normally while turning, addressing
the fringe distortion exposed by fast yaw without disabling central or stationary culling.
The owner judged the resulting 0.3.37 tradeoff acceptable. `.vhtemporal off` disables the
feature immediately; `.vhtemporalprofile safe|aggressive|extreme` exposes the safety,
accepted, and deliberately unguarded limits for live comparison.

Continuous terrain streaming initially invalidated every visibility result globally. In a
new area this held the game near 190 FPS even when the player was enclosed, while pausing
stopped the churn and allowed about 500 FPS. Mesh replacement now invalidates only that
mesh; readiness events and mask uploads do not erase unrelated results. Hidden exact-mesh
probes provide bounded recovery when the scene changes. `.vhinfo` reports hidden draws,
seam/turning-edge protection, stale results, global invalidations, and pending queries.

## [0.3.30]

In development. The rendering change below was accepted in game on the owner's machine.

**Nearby real terrain now hides distant cached terrain before the cached shader runs.**
Vintage Horizons used to draw immediately before the game's terrain pass. The final image
was correct because real chunks overwrote cached ground, but the GPU had already paid to
shade everything behind the foreground. Cached terrain now draws immediately after real
terrain, allowing the ordinary depth test to reject those hidden pixels first.

In the owner's valley comparison, with roughly 4,000 blocks of cache behind a current hill,
`.vhocclusion on` raised 148 FPS to 179 FPS (about 1.17 ms saved per frame). Looking down at
ground raised 590 FPS to 651 FPS (about 0.16 ms saved). The owner saw only minute distant
changes detectable through immediate toggling and judged them entirely acceptable. The
order is on by default; `.vhocclusion off` restores the earlier order for the current
session.

An earlier same-frame bounding-box query prototype was rejected. It eventually reported
83% of boxes hidden but changed 156 FPS to 155 FPS: its proxy/query/dependency cost replaced
the work it suppressed, and it ran before the nearby real hill existed in depth. None of
that query machinery remains.

## [0.3.27]

In development. Both rendering changes below were accepted in game on the owner's machine.

**Cached solid terrain no longer draws the backs of faces that cannot be seen.** The terrain
mesher now gives every solid face a consistent outward winding, so the GPU can reject the
back-facing half before shading it. Water and thin/cutout surfaces remain two-sided. In the
owner's same-view comparison, `.vhbackface on` raised 218 FPS to 260 FPS with no visible
difference across cliffs, caves, overhangs, high views or low views. The feature is on by
default; `.vhbackface off` is the immediate session-only fallback.

**Cached solid terrain is now submitted nearest first.** Near opaque terrain can therefore
populate the depth buffer before mountains and ground hidden behind it reach fragment
shading. The order is allocation-free after its reusable list grows, applies only to the
opaque pass, and leaves water in its original traversal order. In the owner's same-view
comparison, `.vhfront on` raised 149 FPS to 173 FPS with no visual change. It is on by
default; `.vhfront off` restores the old order for the current session.

These are overdraw reductions, not true occlusion culling. Looking across roughly 4,000
blocks of cached mountainous terrain still measured about 150 FPS against more than 300 FPS
when facing away; 0.3.30 addresses the foreground-overdraw part of that remainder.

## [0.3.23]

In development. The water fix below is confirmed in game.

**Distant water no longer shows a seam at every chunk boundary.** On a large ocean the
cached surface was crossed by a grid of dark vertical lines, one at each cached chunk edge,
and they never went away while you stayed put.

Each cached chunk builds its own shape, and where it has no neighbour to compare against it
closes that edge off with a wall - which is right at the edge of explored world, and wrong
everywhere else. It was deciding "no neighbour" by asking whether the neighbouring chunk was
in memory rather than whether the cache had data for it. Cached chunks are read from disk
nearest-first, so the outward-facing edge of nearly every one of them was built before its
neighbour had arrived, and nothing went back to correct it afterwards. On land the resulting
wall is hidden behind the neighbouring ground; water is see-through, so the wall showed
through the surface as a line.

Cached water now leaves such an edge open, and the chunk is rebuilt once the missing
neighbour actually arrives. Solid ground keeps its wall in the meantime, so a cliff on a
chunk boundary cannot open into a gap. The periodic log reports these repairs as
`seam repairs`.

## [0.3.22]

In development.

**Distant flat ground no longer darkens at dawn and dusk when the ground at your feet does
not.** With the colour itself finally exact, what was left showed up as a match that held at
some times of day and broke at others.

The game deliberately never darkens a flat, upward-facing surface as the sun gets low - there
is a note in its own code saying that block tops coming out darker than block sides looks
wrong. The mod had no such rule and shaded everything by sun angle, so cached ground dropped
to about 55% brightness at sunrise and sunset while the real ground beside it stayed at 95%.
At midday the two agreed, which is why it looked right some of the time.

Cached ground now follows the same rule. Cliffs, slopes and anything not facing upwards are
untouched - the rule can only brighten, never darken.

`.vhtoplight off` goes back to the old shading if you want to compare the two. Worth doing at
dawn or dusk; at midday there is nothing to see. The setting lasts until you quit, so a
restart always returns to the corrected lighting.


## [0.3.21]

In development.

**Distant grass now matches vanilla's exactly, rather than nearly.** 0.3.20 got the shape of
it right - bare dirt showing through a see-through grass layer, with only the grass coloured
for the season - but it asked the game how see-through that grass layer is, and the game
answers that question by looking at **four pixels** of the texture. Four pixels said the
layer covers 57% of the block; the real answer is 69%. So the mod was showing about a fifth
too much bare dirt, and distant ground came out slightly too brown.

The mod now reads the whole texture instead of asking for the four-pixel summary. Measured
against vanilla's own blend for ordinary grassy soil at midsummer, the result is no longer
close - it is the same number.

Sparse and very sparse grass, peat, clay, cob and forest floor all go through the same path,
so they are exact now too. Existing caches repair themselves as they load, as before.


## [0.3.20]

In development.

**Distant grass now has the brown in it that real grass has.** After 0.3.18 and 0.3.19 the
colour was consistent and followed the season, but it was still visibly too green next to
the ground under your feet. Two separate faults were behind it, both found from a screenshot.

The first: the game stores the average colour of a texture and a random pixel of a texture
in **opposite channel orders** - red and blue the other way round. Grass-covered ground is
one of the few things the game answers with a random pixel, and the mod read it as though it
were an average, so grass, and only grass, came through with red and blue exchanged. That is
why it looked as though the grass and the tree colours had been swapped: everything else in
the world was reading correctly.

The second, and the bigger one: grass-covered ground is not one colour in vanilla either. The
game draws bare dirt and then paints a grass layer over it that is only about two-thirds
opaque, and it colours the grass for the season while leaving the dirt exactly as it is. That
untinted brown third is what makes real ground look olive rather than green, and it carries
almost all of the blue - the seasonal grass colour has hardly any blue in it, so anything
tinted by it comes out with none.

The mod now builds the same mixture the game does, and tints only the part the game tints.
Measured against the game's own shader for ordinary grassy soil at midsummer, the green-to-red
balance goes from 28% too green to within 6%, and the blue from nearly absent to slightly
generous. Sparse and very sparse grass get their own correct amount of dirt as well.

Existing caches repair themselves as they load, as before.


## [0.3.19]

In development.

**Distant grass is the right green for the season now.** 0.3.18 fixed neighbouring tiles
being different colours; this fixes the colour they all agreed on being slightly wrong.

The game does not have one grass colour per season. It has a strip of sixteen slightly
different shades for each point in the year, and every individual block picks one of the
sixteen based on where it stands - which is why a real meadow up close is subtly mottled
rather than a flat sheet of green. The mod was taking a single one of those sixteen and
using it for every field in view. In midsummer they range from a dark olive to a bright
yellow-green, so the mod's distant green could be off by about a quarter, in either
direction, and it silently changed to a different one of the sixteen as you travelled.

The mod now mixes all sixteen the way a real field does, sampled across the ground around
you, so distant grass matches the meadow at your feet and stays put as you move. The same
correction applies to every seasonal tint, so autumn leaves benefit too.


## [0.3.18]

In development.

**Neighbouring patches of distant ground no longer come out as completely different
colours.** One tile of cached terrain would be green and the tile beside it brown, with a
hard edge between them, on ground that is the same grass in the real world.

The cause: when the mod files away the colour of a block, it asks the game "what colour is
this block". For grass-covered ground the game answers with **a randomly chosen pixel out of
the grass texture** - a different one every time it is asked. The mod asks once per cached
tile, so each tile picked its own random pixel and painted its entire surface with it. In
the cache from the last playtest, one single block type - ordinary grassy soil - was stored
under 38 different colours across 1,041 tiles.

The mod now works out one colour per block type, once, by averaging many of those draws, and
every tile uses it. Blocks that gave a straight answer before are unaffected, and chiselled
blocks still take their colour from the materials actually in them. Existing caches repair
themselves as they load, so nothing needs re-exploring and no cached terrain is thrown away.


## [0.3.17]

In development.

**Each vanilla chunk now gets its own ground by default.** Until now the mod handed terrain
over to the game at a single measured distance, one radius for the whole world, because the
per-chunk version had a band of missing terrain in it. That band was fixed in 0.3.16 and
confirmed gone in play, and the overlap the fix accepts at the seam went unnoticed, so the
per-chunk version is now what you get.

In practice: cached terrain gives way to real terrain chunk by chunk instead of at one
circle, and a cached piece the game has completely covered is not drawn at all, which is
where the frame-rate gain comes from. `.vhmask off` goes back to the single distance, and
the choice is saved between sessions now rather than lasting only until you quit.

Worth saying plainly: this is a default change made on one player's verdict on one machine,
one world and one view distance, and the mod has not been benchmarked since 0.3.9. If it
looks or performs worse for you, `.vhmask off` is the whole fix.

## [0.3.16]

In development. Both changes are live only with `.vhmask on`.

**The band behind you was terrain the game had quietly stopped drawing.** The game only draws
a chunk of world while it is within your view distance, and it checks that every single frame,
right before drawing. But none of the other signs that a chunk is being drawn change when it
falls out of range: the chunk stays loaded, keeps its shape in memory, and keeps its "drawn"
marker, which only ever gets set and never cleared. Stand still and the game even stops
recalculating what is visible at all.

The mod was reading those signs and concluding the game had that ground covered, so it hid its
own cached terrain there. Nothing drew it. That produced a strip of empty world a couple of
chunks wide, always on the side you had just travelled away from - the side that had loaded
chunks out there in the first place - and standing still never fixed it, because standing
still is exactly when nothing gets re-evaluated.

The mod now applies the same distance rule the game does before handing ground over, and stops
about a chunk and a half short of the true edge. It has to: the game measures the distance to
wherever the terrain in a chunk actually sits, not to the chunk itself, so stopping exactly at
the edge would leave a thinner version of the same empty strip. The trade is that cached
terrain may now be drawn over the outermost sliver of real terrain, which shows as a seam
rather than a gap. The `.vhwhy` and `.vhholes` reports used to answer "the game is drawing this
chunk" for exactly these cells; they now say the chunk is beyond the draw range.

**The ownership map on the graphics card stopped re-poisoning itself with air every 32 blocks.**
0.3.15 stopped empty air from hiding cached terrain, which fixed the sheared horizon. But that
rule was only applied to single updates. The full rebuild - which runs every time you cross a
chunk boundary, so constantly while moving - marked air as the game's again, and only the
once-a-second repair pass cleaned it up. Air ownership is now recorded once and honoured by
both paths, so travelling no longer reintroduces the fault 0.3.15 fixed.

## [0.3.15]

In development.

**The band was the mod cutting the top off its own horizon.** Painting the hidden pixels red
showed the whole band lighting up, which meant the mod really was hiding it - while every
internal record said the ground there belonged to the game and the game was drawing it.
Both were true at once, and that is the answer.

The mod's cached terrain is an approximation, so in places it stands taller than the real
world. The extra height sits in the empty air above the real ground. The mod was treating
air as the game's to draw - correct in one sense, since there is nothing there - and hiding
its own terrain in it. But the game draws nothing in empty air, so hiding there removes the
only thing that was drawing and shears the top off the distant landscape. Hence a band, at
the horizon, that never fills in.

Air no longer hides anything. It still counts as the game's territory everywhere else, which
matters: a column of world only counts as the game's when all of it does, and every column
has sky above it - refusing that outright broke an earlier build completely.

The trade is deliberate: where the approximation overshoots, cached terrain may now show
slightly above the real ground instead of being cut off. A hole is worse than an overlap.

**Also, the map on the graphics card now rebuilds itself from the mod's own records once a
second.** It was only ever updated incrementally, and an incremental copy is only as correct
as the completeness of its update paths - one missing path caused a separate fault fixed in
0.3.13. Any future gap between the two now closes within a second, and the periodic log
counts the corrections so a missing path cannot hide behind the repair.

## [0.3.14]

In development. Diagnostic.

**`.vhpaint on` colours the terrain the mask is hiding bright red instead of hiding it.**
Every argument about this has gone through the mod's own bookkeeping, and that bookkeeping
has been reporting itself healthy while the gap stayed on screen. This asks the picture
instead: if the gap turns red, the mask is hiding that terrain and the question is why; if
the gap stays empty, the mask never touched it and every explanation offered so far - this
one included - has been aimed at the wrong thing.

Whole-piece dropping is suspended while painting, so nothing can escape the paint by never
reaching the graphics card at all.

## [0.3.13]

In development.

**Found and fixed the band of missing terrain.** The mod keeps a small map on the graphics
card saying which patches of world the game is drawing, and that map is a fixed-size ring
that scrolls as you move: a patch leaving the far side hands its slot to a patch arriving on
the near side. When a patch left, the mod forgot it internally but never cleared its slot on
the graphics card - so the arriving patch inherited the departed one's answer. Where that
answer was "the game is drawing here", the mod hid its own terrain over ground nobody was
drawing.

That is why it appeared as a band rather than scattered holes, why it sat behind you as you
flew, why it never filled in on its own, and why every check came back clean: the mod's own
records were correct the whole time and only the copy on the graphics card was stale. It also
explains why none of the ownership rules helped, and why turning off whole-piece dropping
changed nothing.

The routine to clear a departed patch already existed and was documented as necessary.
Nothing had ever called it, from the first version of this feature through 0.3.12. A test now
fails the build if that stops happening again.

## [0.3.12]

In development. Diagnostics.

**`.vhskip` splits the two things the mask does.** Testing established that with the mask on,
every patch of world the mod hands to the game is genuinely being drawn by the game - and the
holes are still there. So the mod is choosing the right patches and doing the wrong thing
with them. There are exactly two places that happens: hiding individual pixels, and dropping
a whole cached piece before drawing it at all. Both only run with the mask on, which is why
they have been indistinguishable.

`.vhskip off` leaves the pixel masking working and stops the whole-piece dropping. If the
holes go, it is the piece dropping; if they stay, it is the pixel masking. Nothing else in
this build changes behaviour.

Note that `.vhholes` cannot find anything while the mask is on, and that is expected rather
than reassuring: the mod already refuses to claim a patch the game is not drawing, so the
command has nothing left to report. It is meaningful with the mask off, where that rule does
not run.

## [0.3.11]

In development. Diagnostics.

**`.vhwhy` was searching 512 blocks and answering about the rest.** The cached band runs out
to the mod's full draw distance, so a hole in its outer half sat past the end of the search
and the command reported "nothing wrong" about ground it had never looked at. It now searches
as far as the mod draws.

**`.vhholes` finds them without aiming.** A band behind you is not something a view ray can
be pointed at, and whatever is visible through a hole answers for itself. This sweeps every
patch of world the mod has handed to the game, reports how many of them the game is not
actually drawing, and lists the nearest few.

Both now print every signal the game offers about a patch side by side - whether it is
empty, whether the game holds a mesh for it, the ray culler's verdict, whether the mesh is
flagged not to draw, and whether it was last inside the view. Each of those has been mistaken
for "the game is drawing here" at some point in this investigation, and reading them together
is what stops the guessing.

The mod also now treats a mesh flagged not to draw as not drawn, which is a separate switch
from the culler's verdict and was being missed.

## [0.3.10]

In development.

**The mod was asking the game the wrong question, and a player's experiment proved it.**
Flying high enough makes the game stop drawing the ground directly below you - in plain
vanilla, with no mods at all. Everything the mod was using to decide "the game is drawing
here" still said yes throughout, so the mod kept hiding its own terrain over ground nobody
was drawing. That is the hole.

The reason: the mod asked whether a patch of world had *ever* been prepared. That marker is
set once and never cleared. Meanwhile the game decides what to actually draw every frame, by
tracing outward from the camera and marking what it reaches, and it has a separate path for
when the camera is above a height limit - which is exactly the case that was tested.

The mod now reads the game's own per-frame verdict instead. It is public information, so no
guesswork is involved, and a check fails the build if a game update moves it. Unknown still
counts as "the game is drawing", so a wrong answer can only cost the correction and never
uncover live terrain.

`.vhwhy` reports the same thing now, so it will no longer say a hole looks fine. `.vhgeom`
still switches the rule off for comparison.

## [0.3.9]

In development.

**`.vhgeom` lets you switch 0.3.8's change off in game.** 0.3.8 stopped treating ground the
game claims but holds no terrain for as the game's to draw. Testing found more holes after
it, not fewer, which does not follow: that change only ever makes the mod draw *more* of its
cached terrain, never less. So if it is responsible, it is because the extra terrain needs
meshes that cannot be built fast enough, and the gap you see is terrain that has not been
built yet rather than terrain being hidden.

`.vhgeom off` reverts to 0.3.8's predecessor behaviour without a new build, so the same hole
can be looked at both ways from one spot.

## [0.3.8]

In development.

**The confirmed cause of the holes is now fixed, behind `.vhmask on`.** A player standing at
a hole ran `.vhwhy` and it reported the exact combination this build was built to find: the
game claimed that patch of world, held no terrain in it, and the mod suppressed its own
cached terrain there and skipped drawing the whole cached piece as well. Nothing drew that
ground, and nothing ever would.

The reason the game can claim ground it is not drawing: its "drawn" marker is set once and
never cleared, so it means "prepared at some point", not "there is terrain here now". A
patch can lose its terrain afterwards and still report drawn forever. The mod no longer
treats such a patch as the game's to draw. Empty sky still counts as the game's, which
matters - every column of the world has sky above it, and refusing that is what broke an
earlier build.

The rule applies only while `.vhmask on`, so the default path is byte-for-byte what was
measured before. It has not been visually confirmed yet.

**Two of my own faults, fixed.** The height breakdown in the periodic log always printed
zeros. And the diagnostic added in 0.3.5 asked the game about every patch it probed, which
takes the same internal lock the game's own loading threads want - worst exactly while a
world is coming up. It now asks only where the answer can change a decision. That is a
suspect for cached terrain being slow to appear after joining, not a proven cause: a log
from a fresh join shows the first hundred cached pieces taking 36 seconds against 6 in an
earlier build.

## [0.3.7]

In development. Diagnostics only; nothing about drawing changed.

**`.vhwhy` can now tell you why a hole is a hole.** Stand looking at one and run it. For
each patch of ground along your line of sight it now also reports what the game itself is
holding there, which is a different question from whether the game says it drew it: the
game's "drawn" counter advances for a chunk of pure air and is never reset afterwards, so
it means "this was prepared at some point", not "there is terrain here now". When the mod
is hiding its cached terrain, the game claims the chunk, the chunk is not air, and the game
holds no terrain for it, the report says so in as many words - that combination is ground
that nothing at all is drawing.

It also no longer walks past that case. `.vhwhy` used to stop only at ground the game says
it is not drawing, so a chunk the game claims while holding nothing looked fine to it and
the answer came back "nothing wrong". You get one verdict, not a list: it reports the first
patch along your line of sight that qualifies, so the terrain behind the hole does not
enter into it.

The periodic log also records where those chunks are, by distance and by height. In a
standing test most of them are underground, where holding no terrain is normal and
invisible, which is why the raw count on its own is not evidence.

## [0.3.6]

In development.

**The per-chunk ownership mask was never actually working, and now it is.** The mod tells
the graphics card which patch of the world each piece of cached terrain belongs to by
handing the shader that piece's position. That one value was being sent in a format the
graphics driver rejects: it refused the value silently, kept the previous one - zero - and
recorded a complaint in the log once per frame. So every piece of cached terrain asked
about ownership as if it sat at the world origin, the answer was almost always "outside the
tracked area", and the per-pixel half of the mask discarded nearly nothing.

That is why four separate attempts to explain the holes came up empty: they were all
looking at bookkeeping that was working correctly. What was broken sat one step later, in
the handoff to the graphics card. Two isolated test runs on the same scene proved it -
19,126 graphics errors per run with the mask on, zero with it off, and zero again after the
fix - and a check now fails the test run if any value is ever sent that way again.

**What this means for playing:** `.vhmask on` now does what it was described as doing, and
it has never been visually judged in that state. The holes may be gone, changed, or moved.
The whole-piece skipping that produced the earlier measured frame-rate gain was always
working and is unchanged; frame rate in a standing test was unaffected by the fix.

## [0.3.5]

In development.

**Fixed a startup fault that switched off part of the mod without saying so.** Two different
commands had been given the same name, `.vhwhy`. The game refuses the second one and stops
loading the mod at that point, so `.vhdetail` did not exist at all and the log recorded
VintageHorizons as a failed system while it carried on drawing. The hole-finding `.vhwhy`
keeps its name; the report about terrain drawing coarser than it should is now `.vhcoarse`.
An automatic check now fails the build's test run if two commands are ever given one name
again.

**The leading explanation for the terrain holes turned out to be wrong, and the mod now
measures the right thing instead.** The game keeps a counter that says a chunk of the world
has been drawn, and it advances that counter for chunks that are pure air without drawing
anything - so the mod could not tell "there is real ground here" from "there is sky here".
The previous conclusion was that the game's own empty-or-not flag could not be trusted to
separate them. Reading the game's compiled code shows the opposite: that flag is sent by the
server along with the chunk, and the game itself uses exactly it to decide whether to draw.
What went wrong in an earlier test build was the rule built on top of it, which took
ownership away from every patch of sky and therefore from every column of the world at once.

Sky being empty is normal and explains the large count reported by the previous build. The
mod now looks for the one case that could actually leave a hole: a chunk that is not empty,
that the game says it drew, and for which the game holds no terrain mesh at all. That count
appears in the periodic log as `drawn-without-geometry chunks`. Nothing acts on it yet; it
decides whether the theory survives at all.

## [0.3.4]

In development. Test builds now carry an incrementing patch number so a reported symptom can
be tied to the exact build that produced it, which the `-dev` suffix could not do. The work below is established and playable behind its own switch, but
it has not been packaged for release, and the release drops the suffix: see
`docs/RELEASING.md`.

**Each vanilla chunk can now own its own ground, instead of one distance deciding for
everything.** Turn it on with `.vhmask on`; it is off by default while it is being
evaluated. The mod keeps a marker for every 32x32x32 chunk the game has finished drawing
and hides cached terrain exactly there, so an unloaded chunk beside you no longer pulls
cached coverage back in every direction the way a single radius had to. Cached pieces that
are wholly replaced are no longer sent to the graphics card at all, which measured a 7.3%
higher frame rate in a controlled standing comparison, with about 42 of every 149 draws
skipped and no change to how much terrain stays in memory. The marker map costs 32 KiB for
a 256-block view distance and three microseconds to update.

Cached terrain also stays out of the near field: it remains suppressed within 48 blocks of
the camera while the chunk you are standing in is confirmed drawn, so per-chunk ownership
cannot put coarse cached geometry at arm's length. If anything goes wrong - a failed
texture upload, a different dimension, an unavailable tracker - the previous measured
radius takes over immediately, and terrain that has not been confirmed always falls back to
the cache rather than disappearing.

Testing at far above normal flight speed found that ownership could not keep up: cached
terrain drew over real terrain, and flying backwards left a band of missing world where the
game had unloaded chunks the mod still believed were drawn. Discovery now follows the
direction of travel rather than restarting from the middle of the view, the probe budget
opens up when it falls behind, and loss detection sweeps the whole boundary each time the
camera crosses a chunk. Formats, protocols and the database schema are unchanged.

**The near handoff now measures where vanilla terrain actually is.** Cached terrain was
hidden inside a fixed radius of half the vanilla view distance, which left a wide band
where both terrains drew the same ground. The radius is now the distance to the nearest
vanilla chunk column the client has not finished rendering, less one chunk of margin. At a
256-block view distance a stationary measurement moved the handoff from 64 to 192 blocks,
shrinking the overlap band from about 192 blocks to between 22 and 96. Coverage is restored
in the same frame when measured ownership shrinks, while growth waits half a second and
applies the smallest radius seen while waiting, so an unloaded chunk cannot leave a hole and
the boundary does not flicker. If the tracker is unavailable or the player leaves the
default dimension, the previous constant returns immediately. This is a visual change only:
cached terrain inside the radius is still drawn and discarded, so no draw-call or GPU saving
is claimed. A person evaluated the packaged build in game and found it acceptable.

**The readiness tracker was validated in a real client, and a defect it exposed is fixed.**
Its interior maintenance sweep only revisited cells it had already committed as vanilla
owned, so it could lose ownership but never gain it; because the client announces a dirty
chunk before that chunk finishes tessellating, a cell's first probe usually failed and
nothing ever looked at it again unless the camera crossed a chunk boundary. One measured
fifteen-second interval spent 432,744 probes re-confirming cells it already owned and none
on the 1,801 cells awaiting an answer. Maintenance no longer filters by state. Across five
isolated runs the tracker held 232 of 441 columns fully owned with no probe errors, no
dropped events, no renderer phase reaching 25 ms, and 18-20 microseconds of average frame
cost. Engine-announced chunk events are now counted separately from the tracker's own
sweeps, and the benchmark runner gained readiness convergence, budget, and
wasted-budget assertions. Formats and protocols are unchanged.

**Chunk-aware vanilla readiness now runs as a pixel-neutral shadow tracker.** An exact
source trace of the installed 1.22.7 client found that `ChunkDirty` precedes tessellation,
`IsChunkRendered` can become true before tessellated output is uploaded, the post-upload
callback is internal, and no public chunk-unload event covers the observed removal path.
The renderer therefore combines dirty-event candidates with bounded polling, requires two
render-frame-separated true observations before readiness gain, and revalidates the
streaming boundary first for prompt loss detection. Fixed tagged-ring storage, coalesced
queues, stale-publication rejection, L0-L6 aggregates, and diagnostics are implemented and
covered by the 1,176-assertion Release tier. The state is intentionally excluded from draw
classification: the existing radial handoff remains the sole pixel owner until runtime
convergence and cost are measured. Formats and protocols are unchanged.

**Cached-terrain transition artifacts have source fixes and an exact handoff plan.**
Terrain color variation now uses a stable section world origin instead of camera-relative
render coordinates, and the five-block approach sink has been removed. The old 78.5%
distance cutoff could discard fallback before vanilla chunks streamed; the current
playtest uses a conservative inner radial handoff with at least 192 blocks of fallback
overlap. That radius remains a stopgap because it cannot identify individual rendered
chunks. The approved follow-up is a bounded hybrid: fully cache-owned meshes use the
unchanged draw path, fully vanilla-owned meshes are skipped on the CPU, and only mixed
frontier meshes sample a compact 32x32x32 readiness mask. Twenty-three focused assertions
were added and now pass as part of the later 1,176-assertion Release tier; the latest
Release playtest package was built for human evaluation. Formats and protocols are
unchanged.

**Server-assist progress logging no longer blocks the server tick.** The elevated-rate
transfer benchmark reproduced multi-millisecond assist tails at the synchronous
every-200-sections notification. Correlated setup/publication/admission, send, allocation,
and GC-crossing telemetry isolated a 3.655 ms callback whose two packet sends totalled
0.075 ms while the progress-log boundary occupied 3.573 ms; no managed collection crossed
the call. The hot-path notification is gone, while `/vhserver` and opt-in interval stats
retain cumulative sections and bytes. The unchanged guarded fix run installed 273
sections, exercised all 16 request slots, declined nothing, and kept active-transfer
assist service at or below 2.061 ms. Formats and protocols are unchanged, and the Release
tier now passes 1,058 assertions.

**Integrated-singleplayer sibling retry and mip recovery are now guarded end to end.**
The Windows runner can launch a named world in a separate integrated sandbox, distinguish
client and `-server` cache files, parse both in-process logs, force one transient local
offer miss, and require that exact section to install later. A clean-cache `/vhgen` run
discovered 211 sibling keys and installed 63 sections, including the forced-miss key, then
converged with no wanted request or client pipeline/storage work left. The client-only mip
interruption hook can no longer be won by the integrated server's storage worker. A hard
integrated-process interruption retained one durable client obligation; recovery loaded
and cleared it, and a third fresh process required zero persisted obligations. Formats
and protocols are unchanged, and the Release tier now passes 1,056 assertions.

**Cache writes now clear dirty state only after the exact revision is durable.** Frozen
section snapshots carry runtime-only revisions and the storage worker returns an explicit
success or failure for every executed write. A stale success cannot erase a newer change,
failures retain dirty state under bounded exponential retry, and repeated pending saves
for one section coalesce to the newest snapshot. Foreign terrain keeps its remote fallback
until a local write succeeds. World close repeatedly drains accepted writes and then
queues the dirty revisions exposed by those acknowledgements until clean or a fixed
timeout; any remainder is logged as exact section coordinates and revisions. The disk,
blob, and network formats are unchanged. The 1,050-assertion Release tier covers injected
failure/retry, repeated mutation, coalescing, a 300-key drain, and newest-row restart. A
3,132-section game cache then reopened, wrote 138 revisions, converged to zero unsaved/
backlog/errors, and shut down normally.

**Mesh preparation and GPU upload are boundary-budgeted.** Render-thread snapshot
production now stops after 1 ms, 2 MiB of estimated retained section arrays, or four
jobs; completed GPU results stop after 2 ms, 4 MiB of live vertex/index data, or four
results. One first item always progresses even if oversized. The complete new
opaque/water pair uploads before the previous mesh is disposed, so a partial failure
retains visible terrain and restores its dirty obligation. New telemetry reports
snapshot/upload throughput, queued bytes, oldest age, direct GL upload time, and disposal
time. A 601-section moving route and a 12,800-block growth route to 3,132 persisted
sections exercised both budgets. The larger run processed 94,285 snapshots/uploads,
bounded sampled queues to 18 snapshots and four uploads, measured direct GL upload below
6.9 ms, recorded no 25 ms renderer phase, and converged every guarded queue. Human visual
review and cross-driver evidence remain open.

**Render-dirty scheduling is incremental between coarse camera-cell crossings.** Exact
dirty membership now publishes new-key deltas into a nearest-first priority index instead
of pruning and searching the complete set every rendered frame. The index rebuilds after
a 256-block camera-cell crossing, detail-policy change, or world clear; stale entries are
validated and temporarily busy mesh/load keys retain their obligations. Twenty focused
assertions cover ordering, pruning, bounded progress, reprioritization, and teardown. A
601-section functional route settled all four moving/full-turn waypoints and converged
543 meshes plus every guarded queue to zero before graceful shutdown. This is functional
evidence, not a controlled performance comparison. A later 3,132-section route also
converged with bounded renderer queues; causal scheduler timing and human review remain
open.

**Visibility-aware quadtree traversal has controlled runtime evidence.** The renderer now
rejects a node's conservative world-height frustum box before descending, so an invisible
subtree performs no draw selection or mesh demand. Refinement waits only for visible child
slots. GPU residency uses a separate distance-and-age policy aligned with CPU section
eviction, so camera direction cannot evict the mesh hierarchy behind the player. In a
same-cache 601-section dedicated-process comparison, selected nodes fell 64.2%, weighted
average traversal time fell 19.8%, and weighted average draw-submission time fell 9.3%.
Both sides retained 543 meshes with zero evictions and zero reported 25 ms game ticks.
Aggregate FPS was effectively unchanged and is not claimed as an improvement. A
later 3,132-section route established automated scaling and convergence, while human
in-motion clipping/turn-around review remains open.

**Interrupted mip work now has durable restart evidence.** The isolated Windows runner can
wait until a real `ApplyToParent` row is written, revalidate and terminate only its sandbox
client, then require the recovery process to load persisted mip work and converge all
capture, mip, save, load, and storage guards. The interrupted run retained one obligation;
the recovery process loaded it and drained cleanly, and a third fresh process reopened the
same 601-section cache with zero persisted obligations. The hook is inert outside an
explicit benchmark environment. This paragraph records the dedicated client/server proof;
the integrated equivalent is recorded in the newer entry above.

**Semantic mip convergence and restart evidence.** The Windows runner can now require a
post-route client state with no pending capture input/results, worker errors, mip
queue/in-flight/dirty work, unsaved sections, asynchronous loads, or storage backlog/errors;
the parsed proof is preserved beside the frame CSV. A 120-second warm-cache movement run
captured 2,401 columns with no 25 ms Vintage Horizons tick and converged completely during
cooldown. A fresh client/server process then loaded 601 sections from the resulting cache
and converged again. These runs establish graceful sustained-work and persisted-restart
behavior, not active-work interruption or integrated-singleplayer recovery.

**Server-assist database reads no longer block the server thread.** Assist blob requests
now use a bounded dedicated reader with its own unpooled read-only SQLite connection.
Player-session tags and ordered in-flight batches preserve response order and prevent a
completed read crossing reconnect; database failures produce explicit retryable replies.
The repeated 64/s cold-client/warm-server route again requested, received, and installed
395 sections with zero declines. A 17.481 ms reader call coincided with only 0.989 ms
maximum owning-thread assist work, directly proving separation. A later 32.450 ms assist
outlier occurred while reads stayed below 0.2 ms and remains a separate send/GC/scheduling
tail rather than evidence that SQLite is still on-thread.

**Completed generation/assist evidence and fixed early-join transfer stalls.** The Windows
runner now proves active server cache state, completed transient generation, and saturated
assist transfer rather than trusting scenario labels. A radius-8 run generated all 289
columns transiently with zero timeouts/height-map failures and kept 256/256 sampled
positions absent from the savegame. The first cold-client/warm-server assist run exposed a
race: all 16 request slots filled before the joining player appeared as `Playing`, and the
50 ms server loop silently removed the queue. Retaining bounded requests until the
disconnect event reports a real departure let the unchanged rerun request, receive, and
install 395 sections with no declines and no client 25 ms tick. The run also measured
synchronous server blob reads at 3.75/17.5/68.755 ms p95/p99/max, establishing the
before-change baseline for the dedicated-reader result above.

**Reproducible warm-join and completed-sweep evidence.** The Windows isolated runner can
now require a warm or cold client cache, pin a server configuration on a fresh process,
require an exact server completion line, and preserve cache/completion provenance beside
the frame CSV. A warm join adopted 558 cached sections; its first interval reached 11.180
ms maximum game-tick time and drained 181 background results by 30 seconds, with no tick at
or above 25 ms. A pinned 24-chunk sweep examined 3,249 dependency-aware positions and
finished in about 68 seconds: 1,018 existing columns loaded, 377 frontier columns skipped,
nothing generated, and 256/256 sampled absent positions remained absent. Server pipeline
ticks peaked at 17.874 ms with no 25 ms hitch. These are single warm-cache isolated
server/client runs, not integrated-singleplayer, cold-cache, default-radius, or human
visual evidence.

**Server observability and telemetry cost evidence.** Explicit stats sessions now report
server capture-pipeline, sweep, transient-generation, and assist phases with p95/p99/max,
hitch, queue, and managed-allocation context. The isolated runners can install the server
mod and perform a genuine stats-disabled comparison; auto-unpause no longer implicitly
turns allocation sampling on. Two warmed stationary on/off pairs measured about a 0.7%
average-FPS and 1.0% median-FPS cost at roughly 445 uncapped FPS, while inconsistent 1%
lows support no tail-latency claim. A later pinned sweep reached completion with bounded
reported server ticks. Live assist blob/send work and completed transient generation
remain open.

**Clean-cache exploration evidence.** A new one-way benchmark follows the active capture
frontier for 1,600 blocks without looping back through earlier legs. Two independently
reset client-cache runs had no Vintage Horizons game ticks at or above 25 ms; their worst
ticks were 15.790 and 10.950 ms. Capture backlog stayed within 20 results / 1.61 MiB /
234 ms and 11 results / 0.89 MiB / 62 ms, then converged during an opt-in endpoint cooldown.
The cooldown holds the final view only after frame measurement and leaves existing route
behavior unchanged by default. This is client-only evidence on one machine; human review
of cold motion, live assist transfer, and integrated-singleplayer scenarios remain open.

**Smoother capture publication during warm-cache traversal.** A full corrected
movement/rotation route reproduced capture-result publication at 12.038 ms on the game
tick. Publication now stops at result boundaries after 2 ms or 512 KiB, retains the
existing item ceiling, and applies backpressure across queued/in-progress jobs and
completed/deferred results. One oldest result always progresses, and cross-world results
are rejected by world epoch. The same full route reduced the measured capture maximum to 5.732 ms while
holding backlog to 9 results / 0.70 MiB / 93 ms old, with average FPS within 0.2%, improved
1% lows at all four waypoints, and no ticks at or above 25 ms. One admitted result remains
non-preemptible. The route crossed terrain already present in the VH cache; a human watched
it and reported smooth motion with no noticed clipping or turn-around stalls. A separate
clean-cache capture-frontier route is now measured; integrated scenarios remain untested.

**Reproducible moving-camera performance route.** The isolated benchmark can now follow
deterministic harness-owned trajectories as well as hold fixed viewpoints. A bundled
1,600-block loop moves continuously while rotating the camera through four full turns,
targeting streaming/capture, traversal, projection stability, and turn-around behavior
without measurement-time teleport commands. Legacy routes retain their original behavior,
and focused checks cover parsing, interpolation, angle preservation, engine pitch mapping,
and loop continuity. The harness also now translates its conventional zero-degree horizon
to Vintage Story's PI-centred camera pitch and pins both mouse axes. Earlier route
screenshots were sky-biased; their capture/mip tick comparison remains useful, but they
are not renderer-load or visual evidence. A corrected terrain-facing warm-cache route
completed with five projection resets and no tick hitches. Human review reported smooth
motion with no noticed clipping or turn-around stalls. Clean-cache endpoint screenshots
are now inspected; human review of the cold route in motion remains open.

**Smoother adoption of server-assisted and singleplayer-cache terrain.** Compressed
foreign sections are now inflated and structurally parsed by the storage worker instead
of on the game tick. The owning thread still performs the live block lookup, terrain
classification, recolouring, and final publication under its existing time/byte budget.
World identity and local-win checks prevent a delayed foreign result from overwriting
terrain the client captured while decode was pending. Network request slots now remain in
flight until actual publication rather than packet arrival. Corrupt or future data fails
one section without stopping later decode work. Focused checks cover queue bounds,
thread-safe deferred palette state, failure isolation, and request transitions. A brief
human playtest reported a noticeable subjective improvement; a controlled assist or
sibling-cache benchmark is still pending.

**Better evidence for allocation-driven stutter.** Explicit stats and benchmark sessions
now record managed allocation totals and worst single-call allocation for client tick,
pipeline, and renderer phases. The counters are opt-in, scoped to the measured client
owners, and read outside the elapsed-time boundary. This makes later movement tests able
to distinguish a phase's own work from memory pressure and garbage-collection effects.

**Smoother server sweeps, generation, and terrain transfer.** Savegame sweeping,
transient generation, and server-assist serving no longer release a full second's work in
one callback. Their configured rates are spread across normal ticks with small elapsed-time
ceilings and bounded probe publication. On the client, arrived server sections, local
singleplayer-cache blobs, and completed background loads now stop after 2 ms or 512 KiB in
a tick instead of draining solely because results are ready. FIFO work always advances by
at least one section, and telemetry reports queued bytes and oldest age. These policies are
source- and harness-tested. Dedicated-server sweep, completed generation, and saturated
assist now have runtime evidence; integrated-singleplayer and human playtesting remain
pending.

**Less per-frame renderer work.** The renderer used to scan every resident distant-terrain
mesh on every frame to find the camera's far edge. Because that distance was an exact
camera-relative number, ordinary movement could also rebuild the game's projection for
tiny changes. The renderer now maintains the outer world-space bounds as meshes arrive and
leave, so the steady calculation takes constant work however large the explored cache is.
The camera projection grows immediately in safe 512-block steps and waits five seconds
before shrinking to a stable lower step. The `.vhfar` cap behaves as before. Isolated
checks and a full automated moving-camera route cover the bounds and projection policy.
The warm-cache route also passed human clipping and turn-around review; behavior while
new distant coverage first arrives remains open.

**Fixed: a temporary section miss no longer leaves that distant terrain stuck for the
session.** A local singleplayer-cache read that missed could stop being wanted while still
occupying an in-flight slot. A server's retryable "not written yet" answer could likewise
finish the network attempt without restoring the pipeline request. Both paths now release
the completed attempt and return the section to an explicit retryable state. Server retries
wait 7.5 seconds and span a bounded window of roughly one minute; permanent refusal and
bad data remain terminal instead of retrying forever.

**Faster large singleplayer and server-assisted caches.** The integrated-singleplayer
client no longer enumerates the sibling server cache's complete SQLite key index on the
game thread. A dedicated read-only worker scans at a coarse cadence and publishes only new
keys in bounded batches. Incoming server manifests are also applied once, one bounded
chunk at a time, instead of passing the complete retained offer set through the pipeline
on every tick. Idle game-thread work no longer grows with every section ever explored or
offered.

**Faster active exploration.** Building coarser horizon levels used to collect, sort, and
merge vertical boundaries on the game tick. In the reproduced short exploration route,
that phase reached 20–22.5 ms p95, 32.5–35 ms p99, and 103.1 ms maximum. The merge now runs
on a bounded worker and carries world/revision identity so an old result cannot overwrite
newer terrain. Two repeats of the same route ended with no mip errors or backlog and no
game ticks at or above 25 ms. These are controlled short-route measurements; long
continuous play and interrupted-restart testing remain open.

## [0.2.1] - 2026-08-15

**Fixed: chiselled blocks drew as one flat wrong colour in the distance.** Reported as
purple. A chiselled block's colour lives in its block entity, and only a probe at the
block's exact position finds it - the world map colours chisel work the same way. The
LOD colour probe asked at a stand-in position, the centre of the chunk column, found no
block entity there, and fell through to the placeholder texture. So every chisel in a
section took the placeholder's colour. The probe now uses the block's own position, and
distant chisel work takes the materials it is made of. Chiselled terrain that came from
a server or from transient generation has no block entity to read. That now draws a
neutral grey instead of the placeholder colour. Ground already cached with the wrong
colour corrects itself when you visit it again.

**Fixed: reloading a singleplayer world crashed with "cache file not writable".** When
you left a world, the mod parked an open handle to the server-side cache in a connection
pool. The handle lived as long as the game process. On the next load of the same world,
the integrated server failed to open its own cache file. On platforms whose file sharing
blocks a writer while any handle is open, this failed every time. 0.1.0 had no
server-side cache, so this fault did not exist there. The mod now closes the handle,
and a check watches the process's handle table so that this stays true.

**Fixed: Vistas Beyond no longer switches this mod off.** Vistas Beyond was on the list
of LOD mods that this one defers to, a guess made from its name. It does not belong
there: it is a server-side worldgen mod that adjusts terrain generation and draws
nothing, so there is no conflict to avoid. The two together now give exactly what that
pairing promises: more dramatic terrain, visible from further away. Reported from the
field.

**Fixed: patches of distant terrain were solid black, and stayed black.** A server has
no texture atlas, so it stores no colour, and the client adds the colour on arrival. The
client also saves what it receives. So anything that stopped the colour step went to the
cache without colour and stayed there. That ground drew as pure black for as long as
that world existed.

What stopped it was a block code that failed to resolve. The mod kept that answer for
the rest of the session, and the lookup runs while a world still starts. So when one
common block lost that race, every section saved after it had no colour at all. The
measurement on a real world found 7 sections with no colour anywhere and 59 more in
patches, on ground as ordinary as soil, slate and tall grass.

The mod no longer keeps a failed lookup, and tries the code again instead. Sections that
were saved without colour get their colour from the texture atlas as they load, and the
mod writes them back. So a cache repairs itself as you play, and nothing is discarded. A
block that this game does not have now draws as plain grey, and the log names both the
count and the block codes. A black patch with no explanation cannot occur again.

**Fixed: joining a server before its cache existed switched the assist off for the whole
session.** The server reported the assist as "off" whenever its cache was empty at the
instant you joined. An empty cache is the ordinary state of a fresh server, and of any
server just before an admin runs `/vhgen`. The client took that answer as final and
ignored everything that the server sent afterwards. No amount of generation helped until
you relogged. The answer now says whether the server *will* serve, not whether it holds
anything at that second, and a server with nothing yet says so plainly.

**Fixed: a server cache that grew while you were online never reached you.** The server
sent its list of sections once, when you joined, and never again. A client only requests
sections that the server offered. So an admin who ran `/vhgen` while people played built
terrain that none of them had a way to request. `.vhwhy` reported `no-data` for ground
that the server held for hours, and a relog was the only cure. A sweep that finished
late did the same, and so did other players who explored. The server now offers what it
gained every few seconds, and `/vhserver` reports how many of those follow-up offers it
sent.

**Fixed: a section requested too early was lost for the rest of the session.** The
server answers every request, but it refused with the same empty packet both when it
will never have that section and when it simply did not write it yet. The client read
both answers as "never" and stopped asking. A server that sweeps or runs `/vhgen` is in
the "not yet" state all the time, so a player who joined in the middle of a run gave up
on sections whose data arrived seconds later. The two answers are now distinct, and the
client retries "not yet" a bounded number of times. An older client ignores the new
field and keeps the behaviour it had.

**Fixed: short freezes while exploring with a cold cache.** A capture that landed on a
section no longer in memory read and decompressed that section inline, on the game
tick. The worst measured case took 113 ms, which is more than two whole game ticks.
Capture now waits for the section to load in the background, and results apply in
order when it arrives.

**Faster.** Flat water broke into one rectangle per depth step of the seabed under it,
because the merge grouped surface quads by a depth they never draw. A flat sea now
collapses the way the mesher always intended: water quads over a sloping seabed went
from 602 to 35, with identical geometry on screen. Opening a world with a large cache
now reads only the keys it needs - 931 ms down to 13 ms on a 691 MB cache. The
singleplayer client now asks the server-side cache what it holds once a second, not
twice a frame, which cost up to 105 ms of main-thread time per second on a large
world. The renderer's per-frame walk drops a square root and a logarithm per node. The
sky colour is computed only for the horizon ring that uses it. And meshing a section
stops allocating 241 KB of scratch buffers - simple terrain now allocates less than
1 KB.

**Fixed: another LOD mod that is switched off no longer switches this one off too.**
0.2.0 went idle whenever Farseer, ChunkLOD, Vistas Beyond or TopoHorizon was loaded. On a server that
runs one of those, the game makes every client load it, and downloads it for you. So
"loaded" never meant "drawing". A player who switched Farseer off in its own dialog and
used this mod instead got no distant terrain from either mod.

This mod now reads Farseer's own switch, in `farseer-client.json`, and draws when
Farseer is off. There is nothing to configure: the setting is already on disk. For the
mods whose switch this mod cannot read, it still defers. `IgnoreOtherLodMods` in
`vintagehorizons.json` overrides that, and `.vhdefer on|off` sets it from chat. A change
applies at the next start, never in the middle of a session.

`.vhinfo` and the log line no longer tell you to remove the other mod. That is advice
that a player on such a server cannot obey.

**Fixed: an idle client made the game log a channel warning.** The deferral path
returned before it registered the network channel. The game then reported "Server sends
me channel name vintagehorizons, but no client side mod registered it" at every join.

**Fixed: a crashing client cleaned up almost nothing.** When the game crashed during its
own shutdown, our teardown ran off the main thread, where the engine refuses these
calls, and each refusal skipped every step behind it. That cost the storage writer's
shutdown, and then, once that was guarded, the release of every GPU mesh. Each step now
stands alone, and our own work runs before the engine calls that can refuse.

## [0.2.0] - 2026-08-03

**Chunk generation on request**, with `/vhgen start [radius] [x z]`. It builds the LOD
picture around a player, or around coordinates you give, for terrain nobody has visited.
It writes nothing to the savegame. Real worldgen runs transiently from the seed, through
the engine's `PeekChunkColumn`. A column that already exists loads normally instead, so
player builds stay correct.

The command needs the controlserver privilege, which every singleplayer host has. Config
ceilings and rate caps bound it. Generated terrain has no trees until a real visit
replaces it. Give both coordinates or neither: the command refuses one on its own, rather
than centring somewhere you did not ask for.

**The non-destructive promise is now measured, twice.** Every sweep and every generation
run re-probes sampled positions that did not exist before it. Each run then prints the
result, as "Verified 256/256 sampled absent positions still absent". So a worldgen mod
that breaks the promise is detected on the server where it happens, and not only in this
repo's test matrix. The check regimen also asserts, byte for byte, that an all-peek run
leaves the savegame's terrain tables identical.

The sample keeps clear of online players, because the engine generates terrain around a
player as ordinary play. A run centred on a player therefore still measures something,
instead of reporting "Verified 0/0".

**Fixed: a client can stop receiving terrain for the rest of a session.** A server
dropped queued section requests without answering them, in two places. The first was
when its cache was not open yet. The second was when a client asked for more than the
queue holds.

A client marks a key in flight when it asks, and forgets it only when a reply arrives. So
a dropped key was stranded. Sixteen of them filled the in-flight cap and blocked every
later request. The server now refuses out loud in both cases, and `/vhserver` counts the
refusals. This fits an intermittent stall seen in testing. It was never caught with
logging in place, so treat it as a defect fixed and not as a diagnosis confirmed.

**Fixed: a bad config file destroyed your settings.** A file that failed to parse was
overwritten with defaults, which deleted every hand-edited setting over one stray comma.
The file is now left untouched, the error names the problem, and defaults apply for that
session only.

**Also fixed.** The client now notices a server-side cache that appears mid-session, from
a sweep after a slow start or from a `/vhgen` run. Before, it looked once at join and
never again. The cache-format purge also reports how many sections it discards, instead
of deleting them in silence.

**Stays out of the way of other LOD mods.** With Farseer, ChunkLOD, Vistas Beyond or
TopoHorizon loaded, this mod now goes idle. Two LOD mods would fight for the camera far
plane and draw over each other. A server that runs one of those forces it onto every
client, while this mod stays optional, so this is the one that yields. `.vhinfo`
reports the idle state and names the mod it defers to.

**Optional server-side assist.** The mod is now Universal, with both
`requiredOnClient` and `requiredOnServer` false. Install it only on your client and it
works exactly as before, on any server, vanilla included.

Install it on the server too and it builds its own LOD cache from everyone's travels. It
then shares that cache with connecting clients on request. A fresh join, or a fresh area,
can therefore already be far. Before, it showed only what that one player had explored.

Server admins get `ModConfig/vintagehorizons-server.json` and a `/vhserver` status
command. The settings cover capture on and off, serving on and off, a serve radius per
player, and rate caps. Serving defaults on, at an 8192-block radius per player.

**Savegame sweeping**, on by default. A server now loads terrain the world generated in
past sessions, so the cache can be built from it at once. Before, the cache grew only
from terrain a player walked past again. Sweeping never generates new terrain. This
covers a dedicated server and singleplayer's own integrated server.

**Pre-generation**, separate and off by default. `PregenRadiusChunks` builds the cache
around spawn at startup, over terrain nobody has visited yet. A server can therefore offer
a horizon on the first join, instead of one that appears over weeks of play.

It now runs the same transient generation `/vhgen` uses. It costs worldgen time but **no
disk**, because the terrain is captured and thrown away. Earlier in this release it loaded
columns instead, which cost a few hundred MB at radius 64. It stays opt-in because it
still reveals map nobody has explored.

**Faster terrain fill-in.** Meshing runs on a thread pool. Before, one thread did capture
and meshing in lockstep, and exploring new terrain starved the mesher. Measured 2-3.5x
faster fill-in at the same load.

**Fixed:**
- LOD regions got permanently stuck coarse, with a hard, unmoving edge between detailed
  and blocky terrain. A wrong "does the server have this" check misfired on ancestor keys.
- LOD colour sometimes resolved to the wrong texture, on a block whose first texture has
  no baked colour. Confirmed on vanilla `fruitingbush-wild-blackberry`, and reported
  against a modded world.
- A false "discarding your cache" message appeared on every new install.
- Singleplayer ran two redundant copies of the whole pipeline in one process.
- Remote terrain now arrives nearest-to-you first, instead of in an arbitrary order.

**Internally**, a repeatable check suite (`scripts/check.sh`) now backs the correctness
claims. Before, each one rested on a hand-run sandbox session.

## [0.1.1] - 2026-07-25

0.1.0 shipped without the LICENSE in the zip. A review afterwards found three defects.
Ground-cover mats floated on mip-merged runs. Thin plants hid water, so shorelines
showed through. And an unbounded scheduler loop turned one frame into a six-figure scan.

## [0.1.0] - 2026-07-25

Initial release. Unlimited render distance, decoupled from the vanilla view-distance
slider. Real 3D terrain, not a heightmap: mountains, overhangs, cave mouths, forests, and
player builds all appear at distance. Translucent water over lake and sea floors. Live
seasonal colour and a derived snow line. Persistent per-world cache that keeps growing as
you play. Fully client-side, works on any server.
