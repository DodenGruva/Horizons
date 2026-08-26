# Phase 9 packed-memory comparison - 2026-08-25

Four owner-approved launches used `scripts/bench-windows.ps1`, the frozen `bodanboys` sandbox
profile, and `bench/routes/bodanboys-gpu-baseline.txt`. The controlled ABBA sequence was expanded,
packed, packed, expanded. Every run pinned regional arenas and indirect drawing on, clusters off,
and HZB on; only `-GpuPacked 0|1` changed. Disabling clusters was required to isolate the geometry
format because clusters depend on packed drawing. All 24 measured viewpoints settled without a
timeout.

| Run | Mean frame time | Mean FPS | Near/far GPU p95 | Upload p95 median | Expanded live | Packed live |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| expanded A | 2.4450 ms | 412.43 | 350 / 400 us | 112.5 us | 654.20 MiB | 89.21 MiB |
| packed A | 2.4400 ms | 413.28 | 325 / 400 us | 75 us | 654.67 MiB | 89.27 MiB |
| packed B | 2.4333 ms | 414.72 | 325 / 400 us | 100 us | 654.71 MiB | 89.28 MiB |
| expanded B | 2.4333 ms | 414.27 | 325 / 375 us | 100 us | 654.17 MiB | 89.20 MiB |

The two expanded runs average 2.4392 ms and the two packed runs average 2.4367 ms, a packed delta
of -0.10%. Repeat variation was larger (-0.48% expanded and -0.27% packed), so the controlled result
is timing parity rather than a speed claim. The 12-byte packed representation is 86.4% smaller than
the 88-byte expanded representation and did not create a measurable total-frame regression.

The product default selects clustered packed drawing. The same logs reported about 90.33 MiB of
live clustered geometry while all three regional copies together occupied about 833.7 MiB live.
Retaining only the selected clustered copy therefore removes about 89% of the measured *regional
duplicate geometry*. It is not an 89% reduction in game RSS: ordinary per-section legacy meshes
remain resident as the correctness fallback, and allocator commitments differ from live bytes.

These are automated game-backed measurements on the primary AMD RX 9070 XT / GL 4.3 driver. Nobody
visually watched the route, and the owner retired the cross-driver gate. The CSVs are tracked here;
private scenario JSON and client logs remain in the ignored sandbox because they contain machine
and profile details. Hashes bind this summary to the exact evidence:

| Run | CSV SHA-256 | Scenario SHA-256 | Client log SHA-256 |
| --- | --- | --- | --- |
| expanded A | `06F5D7890DC56FF2DA83E83C1A3841D4FC188636990A690D8CC8E7B94EF9AAF3` | `3C13328122B51C30F85495E7415BF0CD77B87308FC640F4764C1862A5366899C` | `873D26FBDA5633B543BAB5C399335217C9AB7B0728E3D4FE92063FEF936081DF` |
| packed A | `DD6F2356CA30660DEDE985AD7198293010C12BB3BB300E0775C7502A7EB05551` | `D16F0156C39886CC0E7C622C168D23FDD0F8519DC4E42143CBB217D47A624F24` | `FE088E4479D8099C6C48747D4605A0111493ED17A334B5FE35118E56967EA2BE` |
| packed B | `97314B8C8BCA8176D157E840537925F04DDE50F23B687B512577386B9EF62075` | `C9C0874AF55A6BC08262D47A5D99AF28470EC8DFDB446837F6748CFFE31346D1` | `21F350F4ABE60B925CBC6843B535B7892337A9A87A1B61ADC4FD4A53AFC5983F` |
| expanded B | `3D2AF700B6665115D08DAB837F67EF92D17844ED033E297FAA53890E9C41245F` | `3FA0A77FE2B80D3A3BF15568B3BD9F68C6DB091F934C72D0BA02211CB1EC7CCC` | `FF496C18EC12AA962A03D2C23E5AF103BEE094DBD3E9625F64C9EA02D4ED78AE` |
