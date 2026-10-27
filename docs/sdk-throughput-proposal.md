# DolphinDotNet throughput proposal for Hypercube

## Scope and source revisions

Reviewed Hypercube main `4fcea730527b96ae560023fa0b683ed66fd04424`, its profiling/preparation branch at `77b75efca7f7599dcc20beedc96fad50e0bf1d7d`, DolphinDotNet pinned by `.sdk-revision` at `ec6a42e87b3491d02f727ca4d15a7a47b171b302`, and SDK main `7bbca65149311fef2d7f305d97f4d2bbc37ce15e` through the GitHub connector. Main is five commits ahead of the pin; those changes concern launcher ownership/artifacts, not the performance paths below. A pin update alone will not implement these improvements.

This records the original design proposal. The first implementation is tracked in DolphinDotNet PR #126; see [implementation results and remaining work](sdk-throughput-results.md) for its narrower scope. API names below are illustrative. Keep current APIs and synchronization defaults working while introducing measured opt-in paths. The GameCube has one CPU: the intended gains are less CPU overhead and overlap of CPU work with I/O/GPU waits, not parallel managed computation.

## Measured motivation and limits

The recent software-rendered Dolphin baseline measured 62.63 ms/frame: prepare 31.61, stream-other 7.27, decode 0.62, mesh creation 0.03, draw 3.38, present 16.79, and remaining phases 3.51 ms. Engine-only combined prep changes reached 59.41 ms mean with unchanged 134 ms F99 and lower selected-detail readiness. These are wall intervals, not CPU-time or isolated GPU measurements, and cannot be substituted for hardware measurements.

Preparation includes runtime work serviced at cooperative safepoints. It is therefore premature to call all 31.61 ms scene-calculation cost. Conversely, a 16.79 ms present interval is not a guaranteed 16.79 ms recoverable saving: part is display synchronization. Establish overlap and gate/service counters before making a numerical speedup claim.

## P0 — attribute SDK work before changing scheduling

Add allocation-free native counters in `async_runtime.c`, `storage.c`, `graphics_device.c`, `graphics_gx.c` and compiler poll hooks:

* Managed execution-gate wait and hold time per executor; service-callback wall time and count.
* Queue residence, backend read duration, actual backend bytes/read calls, staging-copy bytes and app/background handoffs.
* Async poll fast/slow counts and time; GC coordination, collection and root-frame counters separately.
* Begin-frame copy/wait, end-frame copy/GPU wait and VSync wait separately; present mode, video mode and in-flight frame count.

Intervals that overlap are separate timelines. Do not add worker I/O time to main-frame wall time. Expose snapshots through a small SDK diagnostics API and include them in Hypercube JSON/CSV without per-frame strings or file writes. Measure instrumentation overhead in an idle/fixed-resident-scene control.

## P1 — native AssetStream requests outside the managed execution gate

### Current bottleneck

`AssetStream.ReadAtAsync` posts `ReadWork.Step` to the serialized background executor. Each Step seeks and synchronously reads at most 8 KiB into a managed byte array, then posts Publish to the application executor. Publish copies into the destination NativeBuffer and posts the next Step. A full 128 KiB read entails 16 Step and 16 Publish dispatches, plus a complete intermediate copy of the payload. Not every handoff necessarily costs a frame, but all consume queue/service work.

The native file table, error/exception handling and mounted filesystem state are shared. `dnd_fs_read_native` is not currently an unlocked-worker-safe API. The model importer already releases the execution gate for parsing, but `model_source.c::import_io` explicitly reacquires it for I/O. Simply wrapping existing reads in `dnd_async_native_begin/end` is unsafe and does not solve ownership.

### Proposed SDK implementation

Preserve the public `Task<int> ReadAtAsync(offset, NativeBuffer, destinationOffset, count, CancellationToken)` shape, backed by a bounded native request queue and native worker:

1. Under the managed gate, validate the request; acquire a generation-checked file lease and an exclusive destination write lease; record offset/range in a native request slot.
2. Submit directly to the native worker queue. No managed Step callback must execute before the worker can start. The worker uses only captured native state, with no managed objects, shared exception machinery or unprotected resource-table access.
3. Perform positional reads outside the managed gate. Use dedicated read handles or serialized seek+read per handle if the platform lacks a positional backend. Protect shared FAT/disc backend state with native storage locking. Release storage locks before reacquiring the managed gate; preserve a documented lock order across synchronous and asynchronous entry points.
4. Write directly into the leased aligned NativeBuffer. Remove the managed 8 KiB bounce/copy/handoff loop. Retain bounded native I/O quanta for fairness; benchmark 8/16/32/64 KiB rather than selecting a universal size from emulator results.
5. Mark a native completion ticket and signal progress. Resolve the managed Task once per logical request on the application side, outside active rendering. Preserve actual short-read counts, cancellation and error semantics.

Reuse `dnd_buffer_hold/unhold/held_address` for lifetime protection, but add exclusive range/busy ownership: holds alone do not prevent concurrent CPU reads/writes or overlapping transfers. Closing a file, disposing a buffer, unmounting storage and runtime shutdown must defer/drain while leases exist. Cancellation does not permit early buffer reuse. Native errors are stored as plain results and translated into managed exceptions after returning to the managed context. Apply backend-specific alignment/cache maintenance; do not invalidate dirty CPU-written data blindly.

Keep one active read per AssetStream initially, a small bounded queue across streams, and explicit native-memory accounting. The direct-to-existing-buffer path does not require another large data allocation. This change can let I/O progress during current frame waits without first changing GPU fencing, because the native worker no longer needs the managed gate.

### Hypercube integration

Keep pack format, logical batch selection, generation checks and publication policy unchanged. Switch the SDK pin and rerun the current streaming/profiling cases. Add old/new AssetStream modes while validating. Compare equal byte ranges and priorities; record physical backend work separately from logical READ counts.

## P2 — reduce AOT poll and scalar-helper overhead

### Current bottleneck

`ValueCBackend` emits `dnd_gc_safepoint()` at every basic-block entry, and async safepoints in engine assembly blocks. The async hook reads the clock at each invocation before checking its 1 ms deadline. Once due, it can service callbacks and yield/reacquire the gate. Small helpers, getters, binary reads and struct copies therefore incur runtime work and can hide I/O/service stalls inside preparation.

The current GC safepoint is a hook dispatch, not a collection on every block. GC root frames also protect live references and exception paths. Treat these as distinct costs; disabling GC/polls globally is not an acceptable optimization.

### Proposed SDK/compiler implementation

* Emit an inline cheap pending/budget test before entering async/GC slow paths. Use a per-executor block/instruction budget so most polls avoid a clock read, dispatcher call and gate operation. Tune fairness against measured maximum service latency on the console.
* Place expensive coordination at allocation-capable call boundaries and loop backedges using verified CFG/liveness analysis. Every unbounded loop must remain cooperative; preserve an all-block stress/debug mode and debugger stop behavior.
* Add scalar replacement/inlining for proven leaf getters, integer/binary operations and small value types, with bounds checks and exception behavior preserved. Avoid unnecessary struct copies and root frames only when liveness/allocation analysis proves it safe.
* Replace List.RemoveAt(0) scheduler queues with bounded/growing ring queues and reuse frame-wait registrations. These are secondary improvements; measure queue depth and timer-allocation rates before assigning them high priority.

Keep existing general Task behavior as the compatibility path. Introduce an opt-in frame-loop service boundary with a measured callback budget, plus constrained internal completion polling. Generic engine safepoints should not unexpectedly execute arbitrary application continuations in a throughput-oriented frame loop. Explicit Wait/await must retain progress and captured-context semantics; a new mode must not deadlock timers, I/O or cancellation.

### Hypercube integration

Use one SDK frame-service boundary before update/Pump rather than a timer allocation and Task.Delay(1) as a manual progress mechanism. Keep this small adapter change separate from AOT placement changes for A/B attribution. The existing NO DELAY experiment shows that merely deleting the explicit delay is insufficient.

## P3 — make native waits productive, then offer optional pipelined presentation

### Current bottleneck

`dnd_device_begin/end` holds the managed execution critical section across rendering. `dnd_graphics_begin_frame` issues GX_CopyDisp(..., GX_TRUE) and GX_DrawDone. `dnd_graphics_end_frame` performs another copy, GX_DrawDone and VIDEO_WaitVSync. Current fences are signaled by synchronous EndFrame completion.

### Stage A: preserve the existing synchronous API

Instrument and audit the beginning-of-frame copy/clear/wait separately. Retain correct per-frame clear colors, EFB/XFB ownership and GX ordering; replace a blocking wait only when a hardware-correct ordering barrier suffices. Do not simply delete the clear/copy or both fences.

Separate the rule "application callbacks/publication may not reenter rendering" from "every executor must remain blocked while the app waits in native presentation." A dedicated app native-wait scope can suspend/restore its managed/exception/root context and let permitted background work progress while GX/VSync waits. Do not call the existing `dnd_async_native_begin` from the app thread: that API deliberately rejects application callers. Keep renderer-owned state frozen until the wait ends and reacquire the gate before resource collection or frame-state changes.

P1 may already recover much of the I/O overlap, so measure its marginal gain before introducing a more general app-wait scope.

### Stage B: opt-in asynchronous submission

Add a new presentation/submission mode with an actual native GPU completion fence, a bounded frame queue and back-pressure. Distinguish submitted, GPU-complete and displayed frames. Reuse current XFBs where possible; do not add a third framebuffer or unbounded queue without a MEM1/latency budget. Keep synchronous EndFrame as the default contract. An unthrottled diagnostic mode must still respect GPU ownership and is not a production speedup claim.

Hypercube currently asserts that a fence is complete immediately after EndFrame and assigns `streamer.CompletedFrame=frame`. Those assumptions must be changed before asynchronous mode is enabled. Retire resources using actual completion fences and the highest contiguous completed submission, not CPU frame number. Texture pairs, mesh pins and staging buffers must remain owned until their individual transfers/GPU uses finish. Removing GX_DrawDone without this work creates reuse/corruption bugs.

## P4 — bounded SDK streaming pipeline and batch fast paths

Hypercube currently has one 128 KiB staging batch and waits for verification, uploads and ARAM stores before reusing it. Even a faster SDK read cannot overlap the next batch with consumers of that same memory. Provide a generic optional read-lease/staging-pool helper in the SDK, with two bounded buffers and queue back-pressure; Hypercube keeps its own HCP record interpretation and resource priorities. Account for the extra buffer before fixed memory baselines are established. Make this a separate case from P1 so an extra 128 KiB allocation does not masquerade as a free SDK improvement.

Expose a safe public wrapper around the existing internal Renderer.DrawMeshBatch/native batch path for same-material/same-transform meshes. Use caller-owned scratch and preserve full material equivalence, actor separation, order where required and native validation. Add zero-rotation rectangle fast paths instead of invoking native sine/cosine for every FillRectangle. These are useful SDK-wide changes, but current draw/HUD measurements bound their likely standalone benefit; they do not explain the entire preparation interval.

## Validation and rollout

Suggested separately reviewable SDK PRs:

1. SDK attribution counters and deterministic delayed-I/O/frame-wait fixtures.
2. Native AssetStream leases/queue and lifecycle tests, preserving the public read API.
3. AOT fast polling/leaf operations and queue/wait improvements behind feature switches.
4. Productive synchronous native waits and redundant begin-frame work audit.
5. Optional GPU frame pipeline, followed by Hypercube's actual-fence adapter and optional two-buffer read pipeline.

Run each independently and then combine proven changes. Publish old/new SDK hashes, feature flags, video mode, storage backend, memory budgets and raw counters in every package.

Required SDK tests:

* Block a fake native read while app updates proceed; complete native I/O while a fake present wait is blocked; ensure app continuations/publication do not reenter an active frame.
* Dispose/cancel/close/unmount before enqueue, during read and after native completion but before Task resolution; verify leases, stale generations, short reads, errors, shutdown draining and zero resource leaks.
* GC stress/debug stop, long allocating/nonallocating loops, exceptions across native wait scopes, timer/cancellation fairness and bounded service latency under optimized polling.
* Preserve current synchronous fence assertions; async mode must prevent texture/mesh/staging reuse and release before real completion, with bounded in-flight memory.
* Fixed-resident-scene pixels, changing clear colors, raw-pass/3D/overlay transitions and occlusion image equivalence under presentation changes.
* Resident PCM and streamed-audio continuity under simultaneous SD/disc traffic, not just the current resident tone.

Required Hypercube comparisons: existing correctness suite, existing streaming variants and current preparation suite on baseline/new SDK; add native-read-only, poll-only, wait-only, pipeline-only and combined SDK cases. Preserve content, camera path, logical priorities, selected-LOD policy and native budgets. Gate measurements on selected/detail readiness, world draws/triangles, collision progress, audio, bytes and leak checks. Extra buffering must be explicitly charged. A faster run with reduced detail is not sufficient.

A useful initial target is a repeated original-console 30 FPS test (33.3 ms frame budget) with preserved scene progress and sharply fewer >50 ms hitches. Treat that as an acceptance target, not a forecast. The latest 62.63 ms emulator mean cannot be extrapolated into a promised hardware multiplier.

## Source links

SDK main's relevant paths match the pinned performance implementation:

* [AssetStream read/copy/handoff loop](https://github.com/capz/DolphinDotNet/blob/7bbca65149311fef2d7f305d97f4d2bbc37ce15e/src/DolphinDotNet.GameCube/Dolphin/Storage/AssetStream.cs)
* [Execution gate and native work scopes](https://github.com/capz/DolphinDotNet/blob/7bbca65149311fef2d7f305d97f4d2bbc37ce15e/source/async_runtime.c)
* [Filesystem tables and synchronous native reads](https://github.com/capz/DolphinDotNet/blob/7bbca65149311fef2d7f305d97f4d2bbc37ce15e/source/storage.c)
* [Native resource holds and buffer access](https://github.com/capz/DolphinDotNet/blob/7bbca65149311fef2d7f305d97f4d2bbc37ce15e/source/draw_resources.c)
* [AOT block polling](https://github.com/capz/DolphinDotNet/blob/7bbca65149311fef2d7f305d97f4d2bbc37ce15e/tools/DolphinDotNet.Compiler/ValueCBackend.cs)
* [Frame ownership](https://github.com/capz/DolphinDotNet/blob/7bbca65149311fef2d7f305d97f4d2bbc37ce15e/source/graphics_device.c) and [GX presentation](https://github.com/capz/DolphinDotNet/blob/7bbca65149311fef2d7f305d97f4d2bbc37ce15e/source/graphics_gx.c)
* [Hypercube single-batch streaming](https://github.com/capz/hypercube-engine/blob/77b75efca7f7599dcc20beedc96fad50e0bf1d7d/src/Hypercube.GameCube/WorldStreamer.cs)
* [Hypercube frame/fence/delay assumptions](https://github.com/capz/hypercube-engine/blob/77b75efca7f7599dcc20beedc96fad50e0bf1d7d/samples/Validation/Program.cs)
* [Measured profiling methodology and results](https://github.com/capz/hypercube-engine/blob/77b75efca7f7599dcc20beedc96fad50e0bf1d7d/docs/frame-profiling.md)
