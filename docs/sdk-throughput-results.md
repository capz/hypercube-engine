# SDK throughput implementation and next steps

DolphinDotNet [PR #126](https://github.com/capz/DolphinDotNet/pull/126), benchmarked at `593ec9e54a6ec498d16319889ab05aadbc36f72b`, implements the first throughput tranche. Hypercube [PR #2](https://github.com/capz/hypercube-engine/pull/2) consumes that commit and extends the profiling suite to sixteen protocol-v6 cases at 384 KiB. The original [proposal](sdk-throughput-proposal.md) remains a roadmap, not a claim that every proposed subsystem is implemented.

## Implemented SDK changes

* Native AssetStream reads use a read-only native handle and exclusive NativeBuffer lease, release the managed gate during backend I/O, and publish once per logical request. The legacy 8 KiB managed staging/publication loop remains selectable for comparisons. Native reads are the SDK default; cancellation/disposal retire after backend completion.
* Scheduler callback queues are FIFO rings. PollStride defaults to one clock check per 64 block polls; setting 1 restores the original clock-check cadence. GC polling placement is unchanged. PollCalls and SlowPollCalls expose counters.
* ProductiveWaits is opt-in and releases the managed gate during synchronous end-frame GX/VSync waits, preserving context, render ownership, EndFrame and fence semantics.
* Zero-rotation Draw2D avoids sin/cos; Renderer exposes a caller-scratch mesh batch overload. Every profiling case includes the ring queue/compiler fixes, so BASE is the new SDK with legacy reads, stride 1 and productive waits off, not an exact old-SDK executable.

## Measurements

Both complete sixteen-case runs passed scene/progress/phase/quality/budget/audio checks. All times are milliseconds; readiness is selected-LOD cell observations.

| Case | Run 1 mean | Quiet repeat mean | Quiet F99 | Quiet prep | Quiet selected ready | Quiet storage KiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| BASE | 64.18 | 64.18 | 134 | 34.19 | 10.1% | 316 |
| BASE REPEAT | 62.16 | 62.16 | 134 | 31.96 | 13.3% | 336 |
| PREP COMBINED | 60.83 | 60.83 | 134 | 33.71 | 12.5% | 321 |
| SDK WAITS | 63.29 | 63.29 | 134 | 35.49 | 9.3% | 314 |
| SDK NATIVE IO | 54.29 | 54.29 | 101 | 17.39 | 15.2% | 371 |
| SDK POLL64 | 51.35 | 51.35 | 117 | 23.03 | 18.0% | 403 |
| SDK COMBINED | 41.41 | 41.41 | 85 | 4.45 | 23.8% | 491 |
| SDK AND PREP | 41.14 | 41.48 | 84 | 3.26 | 27.1% | 507 |

The quiet repeat lowered mean time by 35.5% with SDK COMBINED; F99 fell from 134 to 85 ms and selected readiness increased from 10.1% to 23.8%. Native reads and stride-64 polling each help separately. Productive waits alone remain within baseline variation. Adding preparation changes to combined SDK settings gives little additional mean-time benefit in this repeat.

Full data: [run 1 JSON](measurements/sdk-throughput-run1.json), [run 1 CSV](measurements/sdk-throughput-run1.csv), [quiet repeat JSON](measurements/sdk-throughput-run2.json), [quiet repeat CSV](measurements/sdk-throughput-run2.csv). Each JSON includes exclusive phase means/maxima, read observations, detail, geometry, movement, failure and audio evidence.

The first capture overlapped host compiler/graphics validation for part of its duration. The second capture ran after those builds completed and is the primary comparison. Both use the identical Release DOL, software-rendered Dolphin, the same moving-camera path and scene, and 384 KiB dynamic residency. These are elapsed guest frame intervals; they establish comparative emulator behavior, not original-console frame-time guarantees. Different throughput changes asset availability and frame count, even with the same demand policy. Compare detail, triangles, bytes, actor movement and blocked attempts as well as frame time. Raw blocked counts include warmup and are not normalized per frame.

## Validation and reproduction

`PROFILING_SUITE=1 tools/build_validation.sh` builds the DOL and host tests using the pinned SDK. Boot the DOL with `out/assets/world.iso` mounted; on hardware place `world.hcp` at `sd:/hypercube/world.hcp`. Run `tools/dolphin_validation.py` with `--suite profiling --seconds 360` and a fresh log directory. It writes full phase/quality JSON, CSV, Markdown and sixteen screenshots. R cycles nine result screens; X restarts all cases with the next budget; START restarts at the current budget.

SDK validation includes all 23 native Makefile test binaries, native read byte/range/EOF and disposal-lifetime checks, exclusive buffer access, productive-wait background progress/context restoration, and 64-versus-1 poll stride counters. AOT compiler integration exercises reference/scalar loads and System.Action arrays with callback queue growth, wrapped indices and FIFO order in Debug and Release, both normally and with GC stress. Compiler and graphics acceptance suites pass on Linux and SDK CI passes on macOS, with separate Windows/macOS build/launch checks. Hypercube Debug/Release host tests and 24 Python tests pass. A clean SDK git archive also passes native tests and CI selection/coverage tests; no untracked checkout fixtures are required.

All 49 bundled native SDK source files match the pin. The DOL was built before the final fixture-only commits; those commits change tests/CI, not production source or code generation.

## Legacy mode retirement

The legacy managed asset-read path and `UseNativeReads` switch are temporary validation support, scheduled for removal after repeated Dolphin and hardware comparisons establish native reads as the performant way forward. Preserve representative frame-time/throughput wins, detail readiness, streaming/actor progress, audio and memory bounds, and short-read/error/cancellation/disposal correctness before closing validation. Current emulator results alone do not authorize removing the fallback.

At that point remove the SDK selector, legacy FileStream handle, 8 KiB staging buffer and chunked Step/Publish path together. Retire/rebaseline Hypercube's legacy A/B cases and protocol/report expectations, retain these historical measurements, and keep native-path correctness and performance regression coverage. The tracking TODOs are beside the SDK selector and Hypercube case configuration; runtime behavior is unchanged.

## Remaining SDK opportunities

1. **Task progress and timer wake latency.** The optimized native-read cases expose a much larger explicit wait interval for `Task.Delay(1)` than BASE. `async_runtime.c::dnd_async_progress` always parks after service, even if service completed the awaited task and its wake signal already occurred. Add a completion/progress epoch or split service-and-recheck-before-park API; use timer deadlines for waits rather than fixed polling. Instrument park duration, gate reacquisition and timer due-to-dispatch latency to distinguish these costs. Test completion during service, completion immediately before parking, cancellation, unrelated wakeups and real console timer behavior. In the quiet repeat, SDK COMBINED records 10.99 ms in this interval versus 1.22 ms for BASE. The observed interval is not a promised recoverable saving.
2. **Aligned direct backend reads.** `storage.c::aligned_read` still caps each backend call at 4096 bytes and copies from a stack buffer. Add a direct path for suitably aligned NativeBuffer ranges with bounded 16/32/64 KiB quanta, retaining the bounce path for unaligned ranges and tails. Measure physical calls, copy bytes, cancellation latency and SD/disc/audio contention. This implementation removes managed handoffs, not every native copy.
3. **AOT polling and root overhead.** This generated profiling executable contains 2,918 GC poll sites and 1,557 scheduler poll sites (static sites, not dynamic execution counts). Stride 64 avoids repeated clock reads but still calls the poll function at every emitted scheduler site. Benchmark inlined fast guards and CFG-selected entry/backedge/allocating-call polls with a bounded service deadline and GC-stress/root-lifetime tests. Do not remove backedge or allocating-call safety points without proof.
4. **Presentation attribution and frame-start work.** `graphics_gx.c` still copies/clears the display and waits at BeginFrame, then copies/waits at EndFrame. Add separate begin-copy, GPU-complete, VSync and gate-wait counters, then test avoiding the redundant copy while preserving clear color/depth and overlay behavior. Productive waits alone did not show a strong mean-time benefit in the first capture.
5. **Bounded read/consume and GPU pipelines.** WorldStreamer still has one staging buffer and one outstanding read, so native backend time and consumption remain partly serialized. An SDK two-buffer request API could overlap them within the memory budget. Asynchronous GPU submission additionally requires real fence-based completion, texture/mesh pin retirement, and display-buffer ownership before Hypercube can stop treating EndFrame as completion. These larger ownership changes remain deferred.

The single GameCube CPU limits parallel managed compute, but the measured native-read/polling improvements show that the earlier frame times were not an immutable hardware/architecture floor. The next iteration should target measured task-wait and presentation costs before more scene-side micro-optimizations.
