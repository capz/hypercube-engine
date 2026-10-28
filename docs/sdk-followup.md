# SDK wait/read follow-up

The SDK change is [DolphinDotNet PR #127](https://github.com/capz/DolphinDotNet/pull/127), stacked on #126. The Hypercube companion is a separate branch/PR stacked on #2. The first tranche's [recorded measurements](sdk-throughput-results.md) remain historical evidence; this suite measures the next tranche against the prior combined SDK configuration.

## Changes and contracts

Task.Wait/GetResult services callbacks, rechecks completion under the managed gate, and parks only when still pending. This handles successful completion, cancellation and faults delivered during service. Timer polling publishes the next actionable monotonic deadline: timed waits can shorten their existing 1 ms cap and due timers yield once per hint to service before falling back to the timed backstop, avoiding a spin when native I/O blocks the service executor. Queued/executing callbacks are excluded from hints; removal clears stale hints. The cap remains for networking, cancellation and stale deadline hints. The application can also poll due timers while the service executor is in native I/O. Callbacks retain their captured executor; application callbacks cannot reenter an active render frame.

Native asset reads use aligned direct calls capped at 16/32/64 KiB, with the previous aligned bounce helper for unaligned prefixes/tails. Short reads re-evaluate alignment, EINTR retries, and EOF/error/disposal retire the exclusive native buffer lease. The quantum caps backend calls, not total logical-request latency; cancellation still retires after the logical read. Backend storage remains serialized. The default quantum is 32 KiB.

Application-side timer polling, completion-aware waits and deadline hints default on. False/zero settings retain before/after controls pending performance validation, under the existing [legacy retirement gate](sdk-throughput-results.md#legacy-mode-retirement). The original protocol-v6 suite explicitly selects the old follow-up settings to preserve its experiments. GPU submission/fences, framebuffers, staging ownership and GC-poll placement are unchanged.

## Reproduce

```
SDK_FOLLOWUP_SUITE=1 tools/build_validation.sh
python3 tools/dolphin_validation.py out/native-sdk-followup-Release/Game.dol \
  --suite sdk-followup --disc out/assets/world.iso \
  --logs out/dolphin-sdk-followup-Release --seconds 360
```

Run the validator inside the existing Dolphin container. Mount the data ISO before booting the DOL; hardware can instead read `sd:/hypercube/world.hcp`. The default dynamic residency budget is 384 KiB. R cycles result screens, X changes the budget and clears/restarts all cases, START repeats. Use fresh log directories.

All sixteen cases use native reads, the original scene/demand policy, stride-64 polling and productive synchronous presentation waits except the stated differences:

| Case | Change from PREVIOUS SDK |
| --- | --- |
| PREVIOUS SDK | Bounce-only reads, previous unconditional parking, deadline hints off |
| COMPLETE CHECK | Recheck task completion after service |
| DIRECT 16K / 32K / 64K | Aligned direct backend calls at each quantum |
| DEADLINE WAIT | Timer deadline hints only |
| CHECK DIRECT32 | Completion checks and direct 32 KiB reads |
| ALL 32K / 16K / 64K | Application timer polling, completion checks, deadlines and direct reads |
| ALL32 AND PREP | ALL 32K plus preparation experiments |
| ALL32 NO DELAY | ALL 32K with explicit Task.Delay removed; an isolation control |
| PREVIOUS REPEAT | Repeat old settings to reveal variation/order effects |
| ALL32 REPEAT | Repeat combined new settings |
| ALL32 POLL1 | ALL 32K with original per-block clock polling |
| APP TIMERS ONLY | Application timer polling with previous waits/bounce backend |

Cases do not flush storage caches. NativeBuffer residency remains bounded, but faster completion can change asset availability. Compare selected readiness, geometry, bytes, actor progress and blocked attempts alongside timings. No absolute speedup is asserted on a shared emulator runner.

The follow-up uses flag bit 16 for application timer polling; engine preparation is enabled explicitly only in ALL32 AND PREP. This differs from v6's flag-bit-16 ID experiment.

## Protocol and counters

Protocol v7 retains the 32-word raster and 16-bit completion mask, rotating four pages at 300 ms to avoid aliasing with 2 Hz capture. Pages 0–2 retain the frame/phase/quality meanings of v6. Page 3 words 0–15 are: application service calls, application park count, mean park microseconds/10, max park microseconds/10, avoided parks, shortened deadline parks, completed logical reads, backend calls, returned KiB, bounce-copy KiB, direct KiB, backend mean microseconds/10, backend max microseconds/10, configured quantum KiB, mode bits (`(flags >> 8) | ((flags & 16) << 4)`), timer maximum lateness microseconds/10.

Counters reset at each case and include warmup; frame/phase means exclude warmup. Native snapshot calls copy each counter group under the gate, preventing AOT safepoints from mixing different completions within one group. Park duration includes gate reacquisition and is not a pure sleep timer; productive presentation waits are separate. Backend duration ends before gate reacquisition and includes native-read/scheduling overhead. The validator checks four frozen pages, mode/flag agreement, actual direct or bounce traffic, byte accounting, and inactive-control counters, plus scene/ground/actor motion, detail, audio, budget and phase accounting. Sixteen-bit telemetry saturates; long diagnostic captures need wider export.

Wait-stat definitions and snapshot APIs are documented in the SDK's `docs/throughput.md`. CSV output adds `sdk-counters.csv` alongside the normal comparisons and full JSON.

## Results

The final sixteen-case run passed all scene, ground, actor movement, detail/progress, audio, memory-budget, phase-accounting and four-page counter checks. It sampled 709 frames at 2 Hz, at one 384 KiB budget. Means below are elapsed guest frame milliseconds in software-rendered Dolphin, not hardware guarantees.

| Case | Mean ms | F99 ms | Explicit wait ms | Selected ready | Storage KiB | Backend calls | Avoided parks |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| PREVIOUS SDK | 42.47 | 89 | 11.99 | 25.3% | 497 | 183 | 0 |
| COMPLETE CHECK | 41.94 | 87 | 11.67 | 23.5% | 495 | 182 | 0 |
| DIRECT 16K | 41.86 | 86 | 11.48 | 23.9% | 502 | 96 | 0 |
| DIRECT 32K | 41.40 | 87 | 10.91 | 25.7% | 515 | 91 | 0 |
| DIRECT 64K | 42.02 | 87 | 11.60 | 25.9% | 497 | 91 | 0 |
| DEADLINE WAIT | 42.13 | 87 | 11.66 | 26.2% | 510 | 188 | 0 |
| CHECK DIRECT32 | 42.07 | 85 | 11.35 | 24.4% | 512 | 91 | 0 |
| ALL 32K | 33.15 | 34 | 1.15 | 18.1% | 449 | 79 | 271 |
| ALL 16K | 33.09 | 34 | 1.14 | 17.1% | 418 | 84 | 261 |
| ALL 64K | 32.97 | 34 | 1.16 | 17.1% | 418 | 78 | 276 |
| ALL32 AND PREP | 31.64 | 34 | 1.16 | 19.2% | 441 | 80 | 290 |
| ALL32 NO DELAY | 30.67 | 34 | 0.00 | 16.3% | 442 | 80 | 0 |
| PREVIOUS REPEAT | 41.78 | 85 | 11.26 | 23.9% | 518 | 188 | 0 |
| ALL32 REPEAT | 33.21 | 34 | 1.16 | 20.8% | 441 | 77 | 272 |
| ALL32 POLL1 | 43.35 | 67 | 1.16 | 13.6% | 359 | 66 | 205 |
| APP TIMERS ONLY | 33.34 | 34 | 2.16 | 17.1% | 424 | 157 | 0 |

ALL 32K reduced mean time by 21.9% (42.47 to 33.15 ms); F99 fell from 89 to 34 ms. PREVIOUS REPEAT and ALL32 REPEAT measured 41.78 and 33.21 ms, respectively. Application timer polling alone measured 33.34 ms; completion checks alone and direct reads alone were close to baseline variation. The largest demonstrated benefit is avoiding the single worker's timer/I/O scheduling dependency.

**Quality tradeoff:** selected readiness fell from 25.3% to 18.1%; storage transfer fell from 497 to 449 KiB, and blocked attempts rose from 81 to 127. The repeat's selected readiness was 20.8%. The scene/demand/camera policy is unchanged, but execution cadence and asset availability differ. Raw blocked counts include warmup and are not normalized per frame. This is a frame-pacing improvement with a streaming-quality tradeoff, not proof of equal-quality throughput improvement. Validate that tradeoff on hardware before promoting or retiring validation controls.

Direct reads removed the bounce payload copy: ALL 32K reports 0 bounce KiB and 446 direct KiB. Backend call counts also drop, although request volume differs; compare the isolated DIRECT rows rather than interpreting all reduced calls as equal-work gains. ALL32 NO DELAY deliberately removes the explicit delay and is not a production optimization result.

A full preliminary sixteen-case pilot also passed before application-side timer polling was added. It measured approximately 41 ms with completion/deadline/direct-read combinations and zero avoided parks, exposing the service-worker dependency. Its case-15 definition and diagnostics differ from the final suite; it is retained as design evidence, not an interchangeable repeat of the final configuration.

[Final JSON](measurements/sdk-followup-final.json), [final CSV](measurements/sdk-followup-final.csv), [SDK counter CSV](measurements/sdk-followup-counters.csv), [pilot JSON](measurements/sdk-followup-pilot.json), [pilot CSV](measurements/sdk-followup-pilot.csv). JSON build metadata records the exact DOL hash and source variant. The final executable uses SDK production code at `e2004c3cf669b26207e583d4a9dbbb9f50d8152f`; the pin advances to `4964d0a6d6005b63b4ec087d064e860ae1c3d7eb`, whose only subsequent change attaches XML documentation to the correct properties. All 49 bundled native source files match the pin.

Validation: 29 Python checks; all 23 SDK native binaries; compiler integration now executes RefLoadSmoke and TimingSmoke in Debug/Release normally and with GC stress; graphics/model acceptance; clean-checkout CI invariants; native GameCube build; boot smoke; and both complete pilot/final Dolphin comparisons. The final run uses atomic counter snapshots. The no-delay control is exempt from requiring avoided Task parks because it does not wait on a Task in that phase.

## Shared background worker and a possible second thread

All background Task.Run/async service work currently shares one SDK worker. A native read releases the managed execution gate, but its synchronous backend call still occupies that same worker. Before this PR, timer polling was also confined to that worker. Deadline-aware waits and smaller copies alone did not fix that scheduling dependency: the pilot still measured about 11 ms in the explicit Task.Delay(1) interval and zero completion-during-service avoided parks. Application-side timer polling addresses that particular dependency while retaining each callback's captured executor.

This PR does not free the background service thread during I/O. Networking polls, background timer callbacks and background continuations can still wait behind a native read. A useful next architecture is **two bounded shared background threads with distinct roles**: the existing serialized managed service executor, and a native-only I/O worker fed by a bounded request/completion queue. The native worker would use leased file/buffer state and no managed heap or exception machinery; the service/application executors would consume completions under the existing gate. It could improve responsiveness and overlap blocked I/O with service work.

**Decision for this PR: do not add a thread.** The measured application-timer fix removes the demonstrated timer dependency using the existing application thread and single shared background worker. Hypercube has one staging buffer and one outstanding logical read, and SDK storage access remains serialized, so an additional I/O thread cannot increase backend concurrency for this workload. It might improve background networking/continuation responsiveness, but that is not a demonstrated frame-throughput benefit here. The faster case also has lower selected-detail readiness and fewer transferred bytes; the timing win must not be presented as equal-quality throughput or proof that more threads help.

On this single-core target, require a repeatable measured benefit before paying for another thread. A targeted native-I/O/service split prototype would need better background service latency and/or equal-quality frame throughput, measured against extra stack/queue memory, context-switch/GC coordination cost and hardware bus/audio contention. Retain the current architecture until those measurements justify the cost.

Two arbitrary managed workers would still serialize through the gate on the single GameCube CPU. That arrangement cannot double CPU throughput and adds scheduling/root/context complexity. A dedicated native-I/O split instead requires a measured extra stack budget (the existing worker uses 64 KiB), bounded queue/request metadata, backpressure, storage-lock ordering, cancellation/close/shutdown retirement and buffer lifetime tests. Actual throughput gains depend on backend blocking and bus contention; they cannot be inferred solely from shorter task-wait intervals.

Validate that next split with a deliberately blocked native read while network service and background continuations continue, plus bounded memory/stack/queue usage and audio/SD/disc contention on hardware. Keep the current single-worker baseline so the benefit can be measured. It is a separate change from the application-timer fix implemented here.

## Deferred work

The counters support follow-up attribution rather than claiming every wait is recoverable. Begin-frame copy/GPU/VSync attribution, compiler CFG poll relocation, bounded dual staging and asynchronous GPU submission remain separate changes with different ownership/GC/pixel-equivalence requirements. Hardware storage/audio contention and timer behavior still require measurements on the console.
