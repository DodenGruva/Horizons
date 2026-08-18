# Integrated-singleplayer sibling retry and mip recovery

These artifacts establish the remaining integrated-singleplayer correctness scenarios
from the main-thread performance plan. They use the Windows runner's separate
`.testdata/integrated` sandbox and the world `vhbench-integrated`; no ordinary Vintage
Story data or process was used.

## Sibling-cache retry and adoption

`integrated-sibling-retry` began with no client or server LOD database. The integrated
server completed a radius-12 `/vhgen`: 625 positions were examined, 414 columns were
generated, 211 frontier positions were skipped, and no height-map failure or timeout was
reported. The client discovered 211 server-side section keys.

The runner enabled a guarded one-miss hook for this isolated process. Key `2,2000,2001`
returned a retryable miss, remained wanted through the normal cooldown, and was later the
exact key recorded as installed. By the final sample, 63 sibling sections had been
accepted and installed, no request remained wanted, and every capture/mip/save/load/
storage convergence field was zero. Eighty offered sections remained remote-only because
the fixed view never requested them; this is expected visibility-driven behavior.

## Integrated interruption and recovery

`integrated-mip-interrupt-accepted` reopened the same client and sibling caches, moved
along the 1,600-block capture frontier, and terminated only the pidfile-verified integrated
process after the client storage worker durably wrote `ApplyToParent=1` for level-0 section
`7999,8002`. The server-side storage worker was explicitly excluded from the marker hook,
because both pipelines share the same environment in one process.

`integrated-mip-recovery-accepted` reopened the same world and reported one persisted mip
obligation. Its final sample had zero pending columns, capture jobs/results, mip jobs,
in-flight or dirty mip work, unsaved sections, asynchronous loads, storage backlog, and
worker/storage errors. `integrated-mip-postcheck` then opened a third fresh integrated
process, required exactly zero persisted obligations at startup, and again converged all
guards to zero.

The first recovery attempt is preserved as
`diagnostic-rejected-server-cache-scenario.json`. Its mip recovery and convergence fields
were clean, but the runner correctly rejected the run because `-ServerCache Warm` asks for
an active server-cache startup report while this recovery intentionally left the
integrated server cache idle. The accepted repeat omitted that unrelated guard.

## Evidence limits

- The sibling scenario uses a deliberate one-miss test hook because a real scan/write
  race is nondeterministic. The exact production retry state machine, cooldown, decoder,
  owning-thread publication, and persistence paths handle the retry.
- The generated area was near the player, so the absence verifier excluded all 256
  samples; this run establishes local adoption, not transient-generation savegame
  absence, which is covered by the dedicated-server generation scenario.
- The integrated server used command generation with sweeping disabled. Default-radius
  integrated savegame-sweep cadence remains separate verification debt.
- Frame-rate numbers in the CSVs are incidental; correctness and convergence are the
  acceptance criteria. No human visual review was performed.
