# Frame-phase profiling tests

The hardware runs at a consistent budget showed 34/51/51 ms F50/F95/F99 for the baseline and every streaming variant. Slicing reduced reads without changing those percentiles. The next test measures where elapsed frame time goes and deliberately removes particular workloads, rather than inferring a bottleneck from read counts.

## Build and run

`PROFILING_SUITE=1 tools/build_validation.sh` generates `out/native-profiling-Release/Game.dol`. `PERFORMANCE_SUITE` and `PROFILING_SUITE` are mutually exclusive. Mount `out/assets/world.iso` before booting the DOL in Dolphin. On GameCube use the same SD2SP2 path, `sd:/hypercube/world.hcp`.

For automatic validation, run the existing Dolphin container command with:

```sh
python3 tools/dolphin_validation.py out/native-profiling-Release/Game.dol \
  --suite profiling --disc out/assets/world.iso \
  --logs out/dolphin-profiling-Release --seconds 180
```

All six cases use repaired RGBA8 geometry, 1-cell texture tiles, 32 KiB/4-record batching, compact telemetry, the same moving-camera path and selected cache budget. The first guest second is warmup, followed by approximately nine measured seconds. Completion requires ten guest seconds and at least sixty rendered frames. Each completed case holds its frozen measurements until fourteen seconds to allow all telemetry pages to be sampled. Native allocations/transfers drain at transitions and the existing leak, collision and audio assertions remain active.

| Case | Deliberate difference | Interpretation |
| --- | --- | --- |
| BASE | Full streaming, drawing and explicit 1 ms delay | Instrumented control |
| COARSE ONLY | Collision requests only; permanent coarse world remains drawn | Removes texture/fine-mesh streaming work; lower visual detail |
| TEXTURE ONLY | Collision and textures requested, no dynamic mesh requests | Removes dynamic mesh decode/create while retaining texture traffic; lower visual detail |
| NO WORLD DRAW | Normal demand/pump/residency/pins, suppress world DrawMesh calls; actor remains drawn | Removes world submission/GPU work; intentionally omits the world image |
| NO DELAY | Full streaming/rendering, skip only the explicit Task.Delay(1) | Tests the explicit delay; runtime safepoints/background executor and present waits still exist |
| BASE REPEAT | Same settings as BASE | Detects variation/order effects; does not flush storage caches |

The isolation cases change resource readiness and therefore do not submit equal geometry or perform equal storage work. They are causal probes, not production optimizations. NO DELAY must still demonstrate mesh publications, detail progress, collision-gated actor movement and audible output; skipping the explicit delay is not permission to starve the background executor.

## What the timings mean

The measured frame begins before controller processing and ends after the explicit delay returns (or its disabled branch). Eight separately measured intervals and one residual partition that frame:

* **STREAM:** Pump wall time minus the nested decode and native mesh creation intervals. Includes verification, texture/collision work, publication and any executor stalls occurring inside those calls.
* **DECODE:** The managed vertex/index conversion loop in `PackedWorld.DecodeMesh`, after payload-header validation and before `Mesh.Create`.
* **CREATE:** The synchronous native `Mesh.Create` call. Decode and create are recorded separately, then subtracted from STREAM so neither is counted twice.
* **DRAW:** BeginFrame, Begin3D, material/world submissions, actor submission and End3D. Scene preparation and EndFrame are excluded.
* **PRESENT:** EndFrame, including SDK GPU completion, framebuffer presentation/VSync and retirement collection. It is **not** a standalone GPU timing query.
* **WAIT:** Elapsed time around the explicit frame delay. Timer service, executor progress and gate reacquisition may also occur there; this does not measure every scheduler stall in the program.
* **HUD:** Report/telemetry preparation, fence insertion, results bookkeeping and telemetry rendering.
* **PREPARE:** Movement, demand/visibility, resource checks, draw-list resolution/sorting and camera preparation.
* **OTHER:** Frame elapsed time minus all eight intervals; includes input, profiling overhead, detail counting and bookkeeping outside the timed spans.

Every interval is elapsed wall time. Compiler safepoints can yield the managed execution gate within a measured method; a long DECODE interval therefore does not alone prove arithmetic is expensive. A long PRESENT interval does not distinguish GPU completion from VSync. Correlate phase means/maxima with the isolation cases and repeat on hardware.

Read latency is measured from issuing an AssetStream request until Pump observes completion. It includes queueing, SDK chunk handoffs, frame cadence and polling; it is not a physical drive/SD transaction timer. Read counters, decode-call maxima and read-observation latency include warmup; exclusive frame means/maxima and detail-readiness averages exclude it.

## Detail and result validity

The suite reports demanded cells, cells with the exact selected LOD ready, cells with any streamed LOD ready, visible cells, submitted world draws/triangles, blocked moves and transferred bytes. Selected readiness is weighted by demanded cell observations. A frame with one streamed cell no longer stands in for an entirely ready scene. Permanent coarse fallback does not count as streamed detail. The actor is excluded from world draw/triangle counters and remains visible in NO WORLD DRAW.

R cycles three result screens: frame percentiles with budget/readiness, mean phase microseconds, and quality/read/block counters. X restarts **all** cases at the new budget and clears previous results; START also clears all rows and restores automatic movement. Pending rows say PENDING. This prevents the mixed old/new measurements encountered with individual reruns of the earlier comparison suite. Changing budgets during a run invalidates a captured CI comparison.

Protocol v4 retains the 32-word raster but rotates three pages every 250 ms. Completed pages are frozen after the last present **and** delay have been measured. A 2 Hz capture samples all three pages during the completion hold. Common fields include case, flags identifying the isolation, budget, sample count, completion mask, actor position, selected readiness, failures and audio state. Page 0 contains frame percentiles and mean phase times; page 1 phase maxima, read latency and mesh/draw counts; page 2 transfer and quality statistics. Definitions are in `tools/profiling_validation.py`. Times stored in 10 microsecond units saturate at 655350 microseconds; individual 16-bit counters also saturate. Do not use saturated records to diagnose larger hitches or very long runs.

CI verifies complete three-page snapshots at one budget, closes phase accounting within quantization error, checks readiness relationships and isolation invariants, and retains scene/actor/motion/audio/memory checks. Ground coverage is deliberately waived only in NO WORLD DRAW; actor and streaming progress are still required there. Host tests reject overlapping intervals, impossible readiness, mixed budgets, stale snapshots, missing pages and wrong protocol versions. No absolute timing win is required on a shared emulator runner.

## Initial emulator result

The first Release run completed all six cases at 384 KiB with scene/progress/audio/budget checks and complete protocol snapshots. Mean phase wall times in milliseconds:

| Case | Frame | Stream other | Decode | Mesh create | Draw | Present | Explicit wait | Prepare | Selected ready |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| BASE | 63.18 | 5.42 | 0.47 | 0.02 | 3.32 | 19.69 | 1.20 | 31.54 | 10.3% |
| COARSE ONLY | 33.29 | 0.73 | 0.00 | 0.00 | 3.02 | 21.71 | 1.16 | 5.45 | 0.0% |
| TEXTURE ONLY | 44.11 | 2.83 | 0.00 | 0.00 | 3.02 | 19.40 | 1.19 | 16.26 | 0.0% |
| NO WORLD DRAW | 61.21 | 5.77 | 0.46 | 0.01 | 0.34 | 20.41 | 1.17 | 31.55 | 9.6% |
| NO DELAY | 63.72 | 4.92 | 0.53 | 0.02 | 3.33 | 20.24 | 0.00 | 33.13 | 10.0% |
| BASE REPEAT | 65.33 | 6.00 | 0.46 | 0.02 | 3.23 | 21.72 | 1.19 | 31.17 | 7.8% |

These software-rendered Dolphin measurements do not establish hardware costs. Preparation/demand and present dominate these intervals; managed mesh conversion and creation are relatively small. Removing world draws or the explicit delay did not remove the baseline F99 hitch. Background execution can be serviced inside preparation calls, so further attribution within preparation would require SDK/runtime instrumentation. No production setting is changed by this test suite.
