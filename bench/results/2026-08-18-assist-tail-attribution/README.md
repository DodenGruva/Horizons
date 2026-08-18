# Server-assist tail attribution and progress-log fix

These four guarded runs repeat the one-player saturated-assist scenario against a warm
server cache and an empty client cache. The configuration deliberately serves up to 64
sections/s, above the ordinary 8/s per-player default, so it is stress evidence rather
than a default-rate expectation.

The baseline transferred 277 sections and recorded 12.779 ms maximum assist service,
including a 12.075 ms individual send. The next two runs enabled correlated setup,
publication, admission, send, allocation, and managed-collection-crossing diagnostics.
Both installed 273 sections, filled all 16 request slots, and declined nothing. No
managed collection crossed a measured callback or send. The clearest attributed tail
was 3.655 ms total: 3.573 ms was admission, two sends totalled 0.075 ms, and the same
callback emitted the synchronous `Assist served 201 sections` notification.

The final run removes that periodic notification from the owning-thread serve callback.
It again installed 273 sections with all 16 slots exercised and zero declines. During
active transfer, assist service stayed at or below 2.061 ms, individual sends stayed
below 0.647 ms, and no managed collection crossed a measured callback or send. The
former 200-section notification was absent; cumulative totals remain available through
`/vhserver` and opt-in interval statistics.

Each run retains its CSV and semantic scenario proof; large generated screenshots remain
ignored. Aggregate FPS is supporting context, not a causal comparison, because the
processes and caches evolved between runs. The older 32.450 ms outlier from Session 15
cannot be conclusively relabelled because its raw correlated server log was not retained.
