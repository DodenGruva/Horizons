# Changelog

Player- and operator-visible changes and other significant established session outcomes
accumulate under **Unreleased** once they are established well enough to describe. Do not
wait for a version release to record significant completed work; a release reviews and
finalizes the accumulated section. See [docs/RELEASING.md](docs/RELEASING.md). Newest
first.

## [Unreleased]

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
