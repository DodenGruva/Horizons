# Off-thread server-assist reader evidence

The retained `assist-offthread-batched` run repeats the Session 14 saturated-assist
scenario against the same 514-section warm server cache and an independently empty client
cache. Configuration remains deliberately stressful at 64 sections/s per player and
global, above the ordinary 8/s per-player default.

The semantic proof records 395 requested, received, and installed sections, all 16 normal
request slots occupied, and zero declines. The client reached 446.8 average FPS / 370.3
FPS 1% low during the measured stationary interval, with no settle timeout. Client and
server processes shut down gracefully.

The pre-change run recorded 68.755 ms maximum synchronous blob read and 68.879 ms maximum
assist service. In this run, the first active transfer interval contained a 17.481 ms
reader call while owning-thread assist service stayed at 0.989 ms maximum. That is direct
runtime evidence that the read no longer blocks the server thread. All 395 sections still
arrived.

A later interval recorded a 32.450 ms assist-service outlier while reader calls stayed at
0.179 ms maximum and individual sends at 0.249 ms maximum. The run therefore does not
claim that every server assist tail is solved; packet serialization/publication, managed
GC, or process scheduling remains a separate attribution target. No person watched the
route in motion, and this is one warm-cache, one-player, elevated-rate sample.
