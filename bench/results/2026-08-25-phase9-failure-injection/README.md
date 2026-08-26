# Phase 9 failure-injection matrix - 2026-08-25

Four owner-approved launches used `scripts/bench-windows.ps1`, the frozen `bodanboys` sandbox
profile and `bench/routes/bodanboys-gpu-baseline.txt`. Every run pinned
`-GpuRenderer auto -GpuArena on -GpuIndirect 1 -GpuPacked 1 -GpuClusters 1 -Hzb 1`, enabled GPU
stats, measured one lap with no warm-up lap, and changed only `-GpuFailure`.

All four routes completed six viewpoints with zero settle timeouts. FPS is retained in the CSVs as
run context only; these are failure-correctness runs with no paired control, so they establish no
performance effect.

| Failure | Label | Outcome | Full scenario SHA-256 |
| --- | --- | --- | --- |
| arena | `p9fail-arena-03104` | Setup refusal logged `visible legacy rendering is unchanged`; the one-shot fault was consumed and a later setup retry recovered the fast path. | `D68BADA28AE1FA3A376250A5A45AD7DE782524D22E8754E438F71048A1A0A268` |
| shader | `p9fail-shader-03104` | Both initialization stages withheld the fast shaders and logged that cached terrain stayed on the established renderer. | `D43CD83214969435E12368D25CBACDC2D7C1CBC0B7BA020FD61E14D4F6371F3C` |
| depth-copy | `p9fail-depthcopy-03104` | HZB disabled for the session; later telemetry retained 1,700-2,059 packed cluster commands in active multi-draw batches. | `1F47796D5F8AB497F7CADEB13A50379C3EE8F55EBF95A9B5B736038F97FA4CD5` |
| draw | `p9fail-draw-03104` | Both indirect drawers failed permanently; later telemetry reported zero cluster multi-draws while expanded/established rendering completed. | `DA8529C44C693D180714FEA6E94C4BB5E8757261BFFC396C8CB93784C29EE89C` |

The full scenario JSON and client logs remain in the ignored private sandbox because they include
machine/profile detail and large repetitive telemetry. Their hashes above bind this tracked summary
to those exact proofs. The tracked CSV SHA-256 values are, in table order:

- `B9883E37A43AAC3971E936ACC746E1C0BB8DB0B357E776A5B6035E8520D2D4CD`
- `426F7BCE6128505E721832C5F2B674894B6E1F560DF252ED0596A266714553F2`
- `56B3F06EC68A0D9FA23EEC1A68801C1CF1C049E7F4B0292293E081009DA130C0`
- `4288B9891E7AF456D500F384AFEDD262D63DA7353132D2102308B841680F4DF6`

These are automated game-backed results on the primary AMD RX 9070 XT / GL 4.3 driver. Nobody
visually watched the route, and no cross-driver claim follows.
