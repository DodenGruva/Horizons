# Changelog

Player- and operator-visible changes and other significant established session outcomes
accumulate under **Unreleased** once they are established well enough to describe. Do not
wait for a version release to record significant completed work; a release reviews and
finalizes the accumulated section. See [docs/RELEASING.md](docs/RELEASING.md). Newest
first.

## [Unreleased]

**Render-dirty scheduling is incremental between coarse camera-cell crossings.** Exact
dirty membership now publishes new-key deltas into a nearest-first priority index instead
of pruning and searching the complete set every rendered frame. The index rebuilds after
a 256-block camera-cell crossing, detail-policy change, or world clear; stale entries are
validated and temporarily busy mesh/load keys retain their obligations. Twenty focused
assertions cover ordering, pruning, bounded progress, reprioritization, and teardown. A
601-section functional route settled all four moving/full-turn waypoints and converged
543 meshes plus every guarded queue to zero before graceful shutdown. This is functional
evidence, not a controlled performance comparison; thousands-section scaling remains
open, as do time/byte budgets for mesh snapshots and GPU uploads.

**Visibility-aware quadtree traversal has controlled runtime evidence.** The renderer now
rejects a node's conservative world-height frustum box before descending, so an invisible
subtree performs no draw selection or mesh demand. Refinement waits only for visible child
slots. GPU residency uses a separate distance-and-age policy aligned with CPU section
eviction, so camera direction cannot evict the mesh hierarchy behind the player. In a
same-cache 601-section dedicated-process comparison, selected nodes fell 64.2%, weighted
average traversal time fell 19.8%, and weighted average draw-submission time fell 9.3%.
Both sides retained 543 meshes with zero evictions and zero reported 25 ms game ticks.
Aggregate FPS was effectively unchanged and is not claimed as an improvement. A
thousands-section scale run and human in-motion clipping/turn-around review remain open.

**Interrupted mip work now has durable restart evidence.** The isolated Windows runner can
wait until a real `ApplyToParent` row is written, revalidate and terminate only its sandbox
client, then require the recovery process to load persisted mip work and converge all
capture, mip, save, load, and storage guards. The interrupted run retained one obligation;
the recovery process loaded it and drained cleanly, and a third fresh process reopened the
same 601-section cache with zero persisted obligations. The hook is inert outside an
explicit benchmark environment. This is dedicated client/server evidence; equivalent
integrated-singleplayer recovery remains open.

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
