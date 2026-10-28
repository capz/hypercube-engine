# Texture pipeline and SDK 2D throughput

SDK implementation: [DolphinDotNet #128](https://github.com/capz/DolphinDotNet/pull/128), integrating the texture timing fixture in #124 and the task/timer/read work in #127. The implementation follows the [texture streaming plan](texture-streaming-next-plan.md) and the user's subsequent instruction to implement and validate it. The attached handoff supplied evidence and proposals; it was not treated as independent authorization.

## SDK and consumer changes

The SDK now constructs filled rectangles directly in the existing native canvas allocation and covers console-font bitmaps with non-overlapping rectangles. Both preserve pixel/vertex content and ordering; they reduce SDK foreground submission work. Checked RGBA scratch ranges and replacement reuse allow bounded texture preparation without increasing the original 256 KiB managed heap or 84 KiB scratch pool. Texture-only trials compare queue depths, publications, foreground time limits, identical bulk-row preparation and clear-copy reuse on the existing shared worker.

Hypercube pins that SDK revision in `.sdk-revision` and CI. The original non-profiling client uses the SDK's faster 2D defaults. The new profiling executable explicitly selects legacy 2D behavior in its controls and the native rectangle/font path in candidates; it does not change demand policy or weaken scene, readiness, audio or memory checks. Earlier protocol-v6/v7 runs explicitly retain their old 2D settings so historical controls remain meaningful.

No additional worker thread was added. The GameCube's single CPU cannot parallelize managed preparation across more workers; the measured texture candidate already reaches the tested refresh interval. A separate native-I/O worker would require blocked-I/O/service-latency evidence and a memory/scheduling/audio comparison, not this procedural source's results.

## Protocol v8

Build with `TEXTURE_FOLLOWUP_SUITE=1 tools/build_validation.sh`; validate the resulting DOL with `--suite texture-followup --disc out/assets/world.iso`. Use a fresh log directory and a 480-second local capture, or longer on a slower host. The original four profiling pages, exact flags, scene movement, ground/detail pixels, audio, memory budget, native read traffic, timer/park counters and exclusive phase accounting are all required.

Case 0 and 12 preserve the previous SDK behavior as controls. Case 1 changes only SDK 2D geometry. Other candidates combine SDK geometry with the old follow-up's completion/timer/read configurations; the clear-reuse bit is also set. Case 10 additionally retains the prior engine preparation combination. Case 11 deliberately removes the explicit delay and is an isolation case.

For protocol v8 only, flag bit 32 selects both native 2D rectangles and rectangle-covered console glyphs; bit 64 selects completed same-color EFB clear reuse. The remaining wait/read flags and page layouts retain their protocol-v7 meanings. Protocol/flags are validated together; v7 and v8 cannot be silently combined.

## Measurement boundaries

Texture coverage and Hypercube selected-LOD readiness are different metrics. The procedural source has no storage reads and cannot establish storage/ARAM bandwidth. SDK-only and source/pipeline gains are reported separately. Timing is elapsed guest time in the same Dolphin environment, not exclusive CPU or a measured hardware speedup. The visible HUD and machine-readable strips remain present.

The legacy read and graphics validation controls remain marked for retirement only after repeated performance/hardware validation establishes the preferred path. Clear reuse remains opt-in because the isolated texture experiment demonstrated copy elision without an independent mean-frame gain.

## Packed-scene results

The complete protocol-v8 run passed all sixteen cases, world/ground/detail rendering, actor progress, four frozen counter pages, ownership/budget checks, native read/park accounting and continuous audio. The audio dump contains 463.8 seconds with no quiet window. The same SDK source and unchanged 384 KiB budget are used for controls and candidates.

| Case | Mean ms | F99 ms | Selected ready | Storage KiB | Backend calls |
| --- | ---: | ---: | ---: | ---: | ---: |
| Previous SDK | 42.47 | 89 | 25.3% | 497 | 183 |
| SDK 2D only | 37.04 | 86 | 30.0% | 555 | 203 |
| All32 + SDK 2D | 18.90 | 34 | 29.4% | 546 | 100 |
| All16 + SDK 2D | 18.94 | 34 | 28.5% | 553 | 104 |
| All64 + SDK 2D | 18.86 | 34 | 29.8% | 559 | 101 |
| All32 + engine prep + SDK 2D | 17.07 | 34 | 29.6% | 582 | 105 |
| Previous repeat | 41.84 | 87 | 25.3% | 516 | 189 |
| All32 + SDK 2D repeat | 19.02 | 34 | 29.8% | 560 | 101 |
| Poll1 + SDK 2D | 37.93 | 50 | 11.8% | 366 | 70 |

All32 + SDK 2D lowers mean by 55.5% and F99 by 61.8% against the control while increasing selected readiness and transferred data. SDK geometry alone lowers mean by 12.8% and raises selected readiness to 30.0%; the remaining larger gain includes the task/timer work already isolated in the earlier follow-up. Do not attribute that entire combined gain to font/rectangle construction.

The raw blocked-attempt count rises from 81 to 160 in All32, while the frame count/rate also rises. That counter includes warmup and is not a blocked-duration measurement; it cannot establish a worse or better amount of blocked wall time. Actor progress and readiness checks pass, but hardware acceptance should include blocked duration and gameplay quality.

The 16/32/64 KiB combined means differ by less than 0.1 ms, with no repeatable quantum winner established here. Keep 32 KiB rather than extrapolating a benefit from still larger reads. Polling every basic block is substantially worse than the bounded stride-64 path. Another worker was not justified.

[Full JSON and build/source hashes](measurements/texture-sdk-final.json), [comparison CSV](measurements/texture-sdk-final.csv), [SDK counters](measurements/texture-sdk-counters.csv). Earlier results are retained separately.

## Final texture-only regression and stop condition

The final SDK/source/policy candidate passes all nine texture cases, the full pixel/coverage/ownership oracle and the return to stress mode. Means span 16.68–16.78 ms; P99 spans 16.7–17.2 ms. At 384 KiB, moving detail is 98.25% for pages, 99.39% for 2×2 and 99.29% for 4×4. The 2×2/384 KiB control was 33.36 ms / 34.3 ms / 95.1% on the unchanged integrated baseline, and 95.7% with the instrumented fixed-pool control; the final compared integrated control is explicitly labeled in the report.

The SDK-only mode and the additional queue/source/indexed-policy changes are separate rows. Indexing removes repeated linear residency scans in the texture fixture and uses 3 KiB of managed payload plus array headers. It retains the original 256 KiB heap limit and 84 KiB staging pool. Exhaustive route/reversal tests compare priorities, eviction choices, identities, coverage and useful bytes against the scanned policy.

Paired page/384 KiB runs measured 16.71 and 16.78 ms with four jobs. Five jobs measured 16.82 ms with slightly lower coverage; a 750 µs upload budget measured 16.82 ms and lower coverage. These changes have reached the tested refresh interval. Further tested queue/foreground/read-quantum changes offer no repeatable gain on this path; this is a measured local plateau, not a proof that future SDK work cannot help other workloads.

[All texture comparisons](measurements/texture-pipeline.md), [CSV](measurements/texture-pipeline.csv), [JSON/DOL hashes](measurements/texture-pipeline.json). The downloadable profiling package includes standalone texture DOLs and the Hypercube DOL/data disc, evidence, logs and a file-hash manifest. Emulator results remain subject to hardware confirmation before retiring validation controls.
