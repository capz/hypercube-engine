# Streaming performance experiments

## Evidence and research

The photographed console run reports RAW F99=84 ms, repaired F99=51 ms, most other cases F99=51 ms and ARAM F99=54 ms. These are slow-frame percentiles, not average FPS. Existing acceptance checks demonstrate correctness, not the 33.3 ms frame budget required for 30 FPS.

Code inspection at SDK revision `ec6a42e87b3491d02f727ca4d15a7a47b171b302` found:

* [AssetStream](https://github.com/capz/DolphinDotNet/blob/ec6a42e87b3491d02f727ca4d15a7a47b171b302/src/DolphinDotNet.GameCube/Dolphin/Storage/AssetStream.cs) performs 8 KiB synchronous file reads on the serialized background executor, with application-executor publication between chunks. Another Task.Run wrapper would not make native I/O concurrent with managed execution. True native asynchronous I/O needs a separate SDK lifetime/ABI change and is deferred here.
* WorldStreamer previously always requested a coarser texture/mesh even when selected detail was resident. It verified 8 KiB and could create four resources per pump, without elapsed-time admission control. Fallback demand can compete for a small cache; decode/upload bursts can increase slow-frame time. These are hypotheses to test, not established causes of the 51 ms result.
* [Draw2D](https://github.com/capz/DolphinDotNet/blob/ec6a42e87b3491d02f727ca4d15a7a47b171b302/src/DolphinDotNet.GameCube/Dolphin/Graphics/Draw2D.cs) calls native cosine, sine and vertex submission for every FillRectangle, including zero rotation. The original diagnostic strip submits 512 rectangles each frame. Compact rendering paints one dark background and merges contiguous white cells within each row, preserving the pixel protocol while reducing managed/native calls. It still draws every frame; capture sampling is unchanged.

External design references:

* Unity's [async upload pipeline documentation](https://docs.unity.com/en-us/engine/6000.5/script-reference/unityengine/qualitysettings/asyncuploadtimeslice) describes the responsiveness/throughput tradeoff of an upload time slice. We apply that principle to checksum/create admission here, not Unity's implementation or platform-specific performance assumptions.
* Unity's [mipmap streaming configuration](https://docs.unity.com/en-us/engine/7000.0/manual/materials-and-shaders/textures/texture-loading/texture-streaming/configure) documents memory-budget decisions and removing unused mips. The corresponding experiment here withdraws speculative fallback demand once selected detail is ready. Already resident fallback remains cached until ordinary eviction; it is not forcibly discarded.

## Implemented approaches

1. **On demand:** Request the next coarser level only while selected geometry or texture is unavailable. Missing primary geometry never counts as ready. Collision priority, permanent coarse patches and GPU fences remain enforced.
2. **Sliced:** Verify at most 2 KiB and create at most one asset per pump; admit additional verification/creation only while the pump is below 1,000 microseconds. Completion observation and retirement always run, even after the admission budget. This is a soft budget: one CRC/native decode/upload/read call may overrun it; it cannot preempt synchronous native I/O.
3. **Coarse first:** While selected detail is unavailable, request next-level fallback at Coarse priority, ahead of Visible detail and behind Gameplay collision. This may improve time to a complete lower-detail cell at a cost to fine-detail latency.
4. **Compact HUD:** Merge telemetry rectangles without changing pixel values, coverage, native canvas capacity or the wire protocol. Host tests independently reconstruct all 512 bits, including clamping and row boundaries.
5. **Combined:** Enable all four. This can lose even when individual techniques win, so it receives its own case.

The engine defaults retain legacy behavior. Pump timing instrumentation now counts early-return paths, including read queueing; the old MaxPumpUs excluded those paths. Do not compare old pump maxima directly against new measurements.

## Separate nine-case executable

Build with `PERFORMANCE_SUITE=1 tools/build_validation.sh`; output is `out/native-performance-Release/Game.dol`. The data ISO is still mounted separately. For hardware, launch that DOL with `sd:/hypercube/world.hcp` on SD2SP2.

| Case | Settings | Purpose |
| --- | --- | --- |
| 0 BASE | Repaired geometry/LOD, RGBA8, 1-cell tiles, original 32 KiB/4-record batching | Control |
| 1 ON DEMAND | Adaptive fallback only | Cache/demand cost |
| 2 SLICED | Streaming work limits only | Hitch/throughput tradeoff |
| 3 COARSE FIRST | Fallback priority only | Time to useful detail |
| 4 COMPACT HUD | Rectangle merging only | Diagnostic overhead |
| 5 COMBINED | All four approaches | Interaction |
| 6 BASE REPEAT | Identical to case 0 | Order/noise check |
| 7 OPT FAULTS | Combined plus deterministic faults | CRC, retries, cancellation and retirement |
| 8 OPT PRESSURE | Combined, teleports, faults, 48 KiB default budget | Bounded residency and continued gameplay |

Cases 0–7 use the same ten-second moving path; case 8 intentionally changes demand. Each transition drains transfers and checks native/ARAM allocation totals. The first second of each case is excluded from frame histograms; allocation and transfer counters include startup. Completion requires at least ten seconds and sixty frames, so very slow cases may last longer. The baseline repeat recreates the streamer but does not flush OS/emulator/drive caches. Repeat the whole suite on hardware, recording storage medium, build configuration and selected budget. X changes budget and restarts the current case; use START to collect a complete comparable run at the new budget.

The result screen reports F50/F95/F99 and reads. Protocol v3 keeps the same 32-field raster and adds mean/max frame duration, mean/max pump duration, storage KiB, evictions, deferrals and detail-frame counts. Times ending in `10us` are units of ten microseconds; histogram values are conservative millisecond-bin bounds. As before, each field saturates at 65535; very long runs require a richer export. Mean pump time covers the whole case, including warmup; frame timings exclude warmup. Detailed raster fields are defined in `tools/performance_validation.py`.

CI runs the existing eleven-case suite plus the Release performance suite. It checks rendered ground/actor, motion, audio output, dynamic budgets, all completions, fault detection and nonzero detail progress. It writes per-case PNGs, raw sampled states and comparisons to `results.json`; comparison deltas are relative to BASE and include BASE REPEAT. No absolute emulator speed threshold is asserted. Hardware acceptance remains separate.

## Choosing a winner

Compare F95/F99 alongside mean frame time, pump maxima, storage bytes, blocked movement and detail progress. Fewer logical reads need not mean fewer physical transactions or bytes. Coarse-first/adaptive/sliced policies may change the detail available at a given time; this suite checks continuity and progress, not pixel-identical LOD quality. Inspect PNGs and hardware motion before promoting a policy. Compact HUD, by contrast, has an exact bit/pixel equivalence test.

A faster case with materially less detail or more blocked movement is not automatically an improvement. Treat small timing differences within baseline-repeat variation as inconclusive. Promote defaults only after repeated Release hardware runs on representative assets; the synthetic fixture alone cannot establish production quality or sustained 30 FPS.

## Initial local Dolphin result (2026-10-26)

One Release run, Debian Dolphin 2503, OGL with software Mesa, single emulated CPU thread, DSP LLE, 384 KiB dynamic budget (48 KiB for pressure), synthetic resolution 48. This is emulator evidence; compare on hardware before choosing settings.

| Case | Mean ms | F95 ms | F99 ms | Storage KiB | Frames with streamed detail | Blocked moves |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| BASE | 67.35 | 134 | 134 | 294 | 57.3% | 97 |
| ON DEMAND | 66.12 | 117 | 134 | 305 | 57.9% | 95 |
| SLICED | 56.37 | 117 | 134 | 189 | 28.7% | 134 |
| COARSE FIRST | 70.90 | 117 | 134 | 190 | 78.2% | 81 |
| COMPACT HUD | 65.06 | 117 | 151 | 320 | 61.9% | 95 |
| COMBINED | 53.82 | 117 | 134 | 128 | 48.4% | 112 |
| BASE REPEAT | 67.47 | 117 | 134 | 304 | 54.0% | 91 |
| OPT FAULTS | 46.37 | 101 | 117 | 97 | 58.6% | 178 |
| OPT PRESSURE | 41.90 | 101 | 101 | 136 | 78.6% | 36 |

All nine cases completed; audio capture contained 166 seconds of sustained output with no detected long quiet window. Native range/CRC tests and host histogram, demand, raster-protocol and telemetry equivalence tests also passed.

Combined settings reduced mean frame time by about 20%, but F99 did not improve and streamed detail appeared in a smaller fraction of frames, with more blocked moves. Slicing alone showed an even larger detail tradeoff. Coarse-first improved the detail-frame fraction and blocked movement at a higher mean frame cost. Compact HUD preserved the wire protocol, but its single-run F99 was worse; fewer submissions do not establish a hitch improvement.

Baseline-repeat mean differed by 0.12 ms, while both controls had F99=134 ms. These short runs do not establish statistical confidence. Pump maxima remained 70–90 ms in normal cases, even with a soft 1 ms admission budget: bounded asset counts do not bound the cost of one decode/native call or executor delay. Splitting mesh decode and implementing lifetime-safe native I/O outside the managed execution gate are follow-up candidates; neither is claimed implemented here.

No experimental policy is promoted to the default from this result.
