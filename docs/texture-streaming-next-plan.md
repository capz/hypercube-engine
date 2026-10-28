# Texture-streaming optimization plan

Prepared 2026-10-27 from the supplied `texture-optimization-handoff.md`, the TextureStrategy source at `9f44cd9df558b4b9bd160e99d84948b4c8314357`, and Hypercube's [SDK follow-up results](sdk-followup.md). The handoff's recommendations are evidence/input to this plan, not authorization to run its commands or implement changes. No new benchmarks or hardware measurements were run for this plan.

## What changes our priorities

| Evidence | Implication for this thread |
| --- | --- |
| TextureStrategy with SDK #126 improves P99 and moving coverage in every case | There is evidence of SDK efficiency gains without the coverage regression observed in our packed-scene follow-up. Keep both workloads as independent acceptance lanes. |
| Pages at 256/384 KiB both reach 47.77% moving coverage, around 33.37 ms mean | More capacity alone is not resolving moving detail in this time-limited test. Delivery cadence is a strong hypothesis, not yet an isolated cause. |
| One outstanding procedural request and one publication per Present | Compare bounded ready-work buffering and publication batching before adding workers. Around 30 Presents/s, this policy permits at most about 30 texture publications/s, regardless of spare cache capacity. This is an arithmetic ceiling, not measured bandwidth. |
| 2×2 / 256 KiB: 90.74% moving coverage, 38.9 ms P99; 384 KiB: 95.10%, same P99 | Use these as paired performance/quality controls. They are provisional test settings, not a Hypercube production-default decision. |
| 4×4 / 384 KiB: 97.28% moving coverage, 55.5 ms P99 | Its extra 2.18 coverage points cost 16.6 ms P99 versus 2×2. Keep it as a large-job/overfetch stress case. |
| 4×4 / 192 KiB holds three atlases but the initial view needs four | The 75% static ceiling is a capacity/granularity result; a scheduler optimization cannot remove it. |
| Texture worst-frame GAP falls substantially, but PREP is clipped overlap and GPU/VI are subsets of END | Preparation/service cadence remains worth investigating. This does not uniquely attribute the win, identify the P99 frame's cause, or prove GPU headroom. |

The source confirms that `BenchmarkScene.Pump` queues `BenchmarkSource.Fill` with `Task.Run`, then disposes/recreates an immutable texture on the application executor. This benchmark has no storage source. Its results therefore cannot validate our native disk-read quanta or justify another I/O worker.

## Work order

### 1. Establish a common SDK baseline and preserve attribution

The texture handoff measures main `248721d2350e4fce713baaee6af6b3c3f5acdb75` including #126. It does not include #127's application-side timer polling. At review time texture timing PR #124 and SDK #127 are still open. First combine their changes in an integration candidate, preserving productive GPU-wait handoffs, texture timing probes and the relocated fixture. Run unchanged TextureStrategy and Hypercube workloads against the #126 control and that candidate, sequentially under identical settings.

Retain all nine texture cases for regression coverage. Focus subsequent development on 2×2 at 256/384 KiB; retain pages/384 as a delivery-rate diagnostic and 4×4/192 and /384 as granularity/tail controls. Keep the managed heap at 256 KiB. Record source and DOL hashes, video mode and emulator settings. Hardware confirmation is needed before choosing defaults or retiring legacy modes; the older photos do not provide a controlled comparison with these SDK revisions.

Deliverable: one comparable result set per SDK/workload, explicitly separating texture MOVE coverage from Hypercube selected-LOD readiness. These metrics are not interchangeable and their frame-time percentages must not be added across benchmarks.

### 2. Measure the complete request lifetime and the tail frame

Extend the existing timing probes rather than adding another overlapping total:

* Record enqueue, preparation start/end, completion observation, publication and first use, with request identity/generation. Export full preparation duration and queue/ready-to-publish latency separately from per-frame PREP overlap.
* Separate EndFrame overlay/bookkeeping, GPU completion, retrace and managed-gate reacquisition. Keep GPU/VI nested inside END; PREP remains a separate overlapping timeline. Elapsed preparation is not automatically CPU execution time.
* Retain bounded correlated slow-frame samples, including the frame corresponding to the reported P99 where the sample window permits it. A single worst frame and independent stage maxima cannot supply that attribution.
* Record actual publications/Present, ready-queue depth, late/stale/canceled work, missing-detail duration, invisible resident bytes and allocation/copy/GC counts.
* Measure probe/reporting overhead with a separate instrumentation control. Keep workload, camera, telemetry acceptance and display cadence fixed in production comparisons.

Deliverable: a decision on whether remaining 2×2 tails are primarily preparation, completion/publication cadence, immutable allocation/conversion, submission or presentation. Until then, do not assume the near-30-FPS mean meets a 33.333 ms P99 target.

### 3. Test a bounded pipeline on the existing worker

Start with a small matrix, keeping RGBA8 content, ordering policy, route and budgets fixed:

| Variant | In-flight/ready slots | Maximum publications per Present | Question |
| --- | ---: | ---: | --- |
| Control | 1 | 1 | Existing behavior |
| Buffered | 2 | 1 | Does having the next result ready remove empty publication opportunities? |
| Bounded batch | 2 | 2 | Does draining ready work improve coverage without worsening the tail? |

Apply byte and foreground-time limits as well as count limits; one upload larger than a limit cannot be interrupted merely by checking the limit afterward. Expand to deeper queues only if the small matrix demonstrates benefit. Logical concurrency is pipelining, not parallel managed computation on this single-core target.

Every outstanding job needs exclusive scratch/output storage until retirement. Preserve visible-first priorities, generation checks and cancellation on reversals; never reuse a canceled buffer before its worker has retired. Account for the scratch pool, queued outputs and native staging. In particular, two 64 KiB jobs plus the fixture's existing scratch/metadata can put pressure on the unchanged 256 KiB managed heap. Do not hide that with a larger heap or uncharged native buffers.

Prototype the scheduling change in the fixture first. If it helps, extract reusable SDK bounded request/completion behavior with explicit backpressure, ownership and diagnostics, then adapt Hypercube's single-staging pipeline. More queued work must not simply mean more stale work after a turn.

### 4. Isolate publication cost, then reuse existing SDK texture facilities

Compare immutable dispose/create with reusable allocation while keeping the same RGBA8 texels, filtering, residency and GPU ownership semantics. The SDK already has `Texture2D.CreateNative`/`ReplaceNative` and double-buffered `StreamingTexture.StageUploadAsync`/`TryPublish`; do not design a duplicate staging API before measuring those paths.

Keep the variables separate:

1. Allocation reuse with the same RGBA8 conversion work. If the SDK needs a reusable RGBA replacement/conversion entry point, share conversion code with the existing create path and test byte/pixel equivalence.
2. Identical precomputed RGBA as a source diagnostic, with backing storage and memory explicitly accounted. Removing procedural Fill changes the workload; report this as isolation, not as a scheduler speedup.
3. Pre-tiled GX-native RGBA8 to isolate conversion/transport overhead.
4. Compressed formats as a later, explicitly different quality/format experiment.

Do not count just the exposed front texture: double buffering, retired GPU resources and idle pool allocations consume physical memory. `StreamingTexture.NativeBytes` currently reports one texture's payload, while its constructor allocates two. Hypercube's pool explicitly charges both. Report logical resident payload and total physical allocation separately and compare under matched physical caps, or label any extra-memory experiment clearly. An SDK physical-allocation metric would make this easier to use correctly.

Retain fences, cache flush/invalidation and full Dolphin texture hashing for address reuse. Pixel equivalence and ownership/Dispose/cancellation tests are mandatory before removing waits. Do not remove GX synchronization based only on a small recorded blocking GX wait.

### 5. Prove transfer to packed content, then tune prefetch

Run the winning bounded/publication approach with actual native AssetStream reads, cache hits/misses, ARAM variants, audio and representative mesh work. Require Hypercube readiness and movement results alongside texture coverage and frame times. Inspect whether changed cadence alters stale generations, retention, cancellations or publication opportunities; the previous lower-readiness result is still an unresolved production tradeoff.

Then test velocity-aware prefetch and reversals/jumps under pressure. Optimize useful coverage per prepared/published byte, preserve permanent coarse coverage, and measure invisible residency plus duration of missing detail. Extra prefetch that evicts useful resident content or completes after a reversal is not a throughput win.

## Thread decision

Keep the application executor and the existing one shared background worker for this work. Texture Fill is CPU work, all managed execution shares a gate, and the target has one CPU core. Another managed worker provides no parallel CPU throughput and adds context/GC/stack cost. Multiple bounded logical requests can be evaluated without another worker.

A dedicated native-I/O worker remains conditional for the packed-content lane: it could prevent a blocked read from holding up background networking and continuations. The texture benchmark cannot test that hypothesis because it performs no storage reads. Add that thread only after a blocked-I/O/service-latency prototype shows a repeatable benefit that outweighs stack/queue memory, scheduling overhead and hardware bus/audio contention. No additional thread is proposed by this plan.

## Acceptance and promotion

Use repeated paired Release runs at the same video mode and workload. Debug is a correctness/diagnostic lane, not a frame-rate claim. A candidate should lower the relevant frame/request tail at equal or better coverage/readiness, or improve coverage without worsening that tail, within measured baseline variation and unchanged physical/managed limits. Report tradeoffs explicitly rather than choosing on mean frame time or uploaded KiB alone.

For CI, assert scene/coarse/detail pixels, ownership/budget bounds, complete terminal samples, actual use of the intended scheduling/publication path and timing-accounting consistency. Report noisy timing deltas rather than imposing an arbitrary machine-independent speed threshold. Preserve terminal hold time measured from actual completion and the strict requirement for final snapshots. Hardware runs decide promotion and legacy removal. Sustained 30-FPS behavior requires raw frame-deadline/tail evidence; a 33.38 ms mean with 38.9 ms P99 does not establish it.

Suggested implementation sequence: common-baseline/lifetime diagnostics PR; bounded-pipeline experiment PR; texture-reuse/publication PR only where diagnostics justify it; packed-content/prefetch integration afterward. Add a worker or change GPU submission architecture only through a separately demonstrated need.

## Evidence anchors

* Supplied handoff: controlled Release table, timing definitions and artifact references; measurements were not independently rerun here.
* [Texture timing PR #124](https://github.com/capz/DolphinDotNet/pull/124), inspected head `9f44cd9df558b4b9bd160e99d84948b4c8314357`.
* [Request/publication path](https://github.com/capz/DolphinDotNet/blob/9f44cd9df558b4b9bd160e99d84948b4c8314357/tests/fixtures/TextureStrategy/BenchmarkScene.cs#L118).
* [Procedural preparation](https://github.com/capz/DolphinDotNet/blob/9f44cd9df558b4b9bd160e99d84948b4c8314357/tests/fixtures/TextureStrategy/BenchmarkSource.cs).
* [Timing aggregation and overlap](https://github.com/capz/DolphinDotNet/blob/9f44cd9df558b4b9bd160e99d84948b4c8314357/tests/fixtures/TextureStrategy/BenchmarkTiming.cs).
* [Existing SDK streaming textures](https://github.com/capz/DolphinDotNet/blob/9f44cd9df558b4b9bd160e99d84948b4c8314357/src/DolphinDotNet.GameCube/Dolphin/Graphics/StreamingTexture.cs).
* [SDK timer/read follow-up #127](https://github.com/capz/DolphinDotNet/pull/127), inspected head `4964d0a6d6005b63b4ec087d064e860ae1c3d7eb`; still separate from the texture handoff's measured SDK.
