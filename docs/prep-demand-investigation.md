# Reducing preparation and demand work

## Evidence from the profiling run

The final six-case run (Release, Dolphin 2503/software Mesa, 384 KiB) completed all phase, page-consistency, rendering, motion, budget and audio checks. These are emulator wall times, not hardware CPU timings.

| Case | Frame mean ms | Prepare mean ms | Present mean ms | Reads | Exact selected LOD ready |
| --- | ---: | ---: | ---: | ---: | ---: |
| BASE | 63.95 | 33.18 | 17.62 | 62 | 8.8% |
| COARSE ONLY | 33.29 | 5.45 | 21.65 | 12 | 0% |
| TEXTURE ONLY | 44.69 | 17.48 | 17.49 | 99 | 0% |
| NO WORLD DRAW | 57.83 | 31.03 | 17.32 | 65 | 12.8% |
| NO DELAY | 61.97 | 34.31 | 17.52 | 61 | 12.7% |
| BASE REPEAT | 62.95 | 33.82 | 17.76 | 61 | 6.3% |

Turning off world draws or the explicit delay does not remove the large preparation interval. Removing fine-mesh demand reduces it, but also changes storage, readiness and the rendered content. This supports investigating demand/catalog/scheduler work and background servicing, not a claim that a particular loop consumes 33 ms of CPU.

## First target: eliminate discarded asset-record decoding

`ResourceScheduler.Demand` calls `catalog.Get(id)` and discards the returned record before its slot lookup. This call exists only to validate the ID. `Get` reconstructs 13 integer fields and one checksum from the byte index, then returns a 56-byte struct. This happens even for an already-resident asset.

Inspection of the actual generated console C confirms that the full constructor and struct copy remain present. The generated `Get` has four GC/async polling sites; the record constructor invokes the binary integer readers for every field. Source-site counts describe generated control flow, not timings or executed polls per frame.

The smallest quality-preserving change is a range-only `ValidateId` helper (or an equivalent bounds check in Demand), leaving full index/payload validation at load time. Keep invalid-ID exception behavior and test queued, resident, retry, cancellation and eviction paths unchanged. This requires no extra asset memory and preserves the requested resources and ordering. It should be the first isolated native A/B experiment; no speedup is claimed until measured.

Other scheduler code repeatedly decodes records when it needs just Offset/Length. Next, cache **only those hot scalar fields** at catalog construction, or provide narrow accessors. Caching all 56 bytes for all 649 fixture records would consume roughly 36 KiB before array/runtime overhead, which is significant inside the 256 KiB managed heap. Do not replace a CPU issue with an unmeasured memory regression.

## Second target: stop repeating tree work

* The visibility loop computes `Tree.Leaf(cell)` and `world.HasCell(cell)` computes it again. Demand later looks up the same leaf again. Cache 24 leaf indices once (96 bytes of integer payload).
* `Traverse` rescans each node's covered cells to rediscover whether it contains visible or resolved detail. Precompute one 24-bit cell mask per node (85 integers, 340 bytes), then compare against per-frame visible/detail masks. This preserves the current ragged 6-by-4 scene and fallback traversal if verified against the existing implementation.
* Demand ordering repeatedly scans 24 cells to choose each nearest cell. A fixed scratch list sorted by distance and then cell ID avoids repeated full scans. Preserve tie-breaking and request order because those affect scheduler behavior and streaming quality.

The first two caches total 436 bytes of array payload, plus runtime headers. Validate every leaf/subtree mask and compare old/new draw lists over camera sweeps, LOD transitions, pressure and occlusion; do not assume fewer loops implies a frame-time win.

## Third target: cache decisions with explicit invalidation

`BeginDemand` clears references and reissues the complete asset set each frame. A demand-set cache can avoid repeated metadata lookup while the visible footprint and selected LODs are unchanged. Maintain collision/target updates every frame and invalidate on visibility, LOD, texture layout, generation/residency and cancellation changes. Keep deterministic priority upgrades. Blindly updating demand every N frames risks worse collision waits and detail readiness, exactly the quality tradeoff seen with earlier slicing experiments.

Resolve each cell/LOD's readiness once after Pump and reuse that result for both rendering and diagnostics. Do not reuse a pre-Pump readiness result after publications or evictions.

## Background execution can be charged to preparation

The AOT compiler emits GC and async safepoints at block entries in engine methods. At a safepoint the runtime can service queued application work or release/reacquire the shared managed execution gate. `AssetStream` reads 8 KiB chunks on the background executor using synchronous Seek/Read and posts native-buffer publication back to the application executor.

Thus a preparation interval may include waiting for the worker or executing a storage completion. Removing scheduler safepoints would hide/move this work and can starve streaming; it is not a safe optimization. Native asynchronous I/O outside the managed gate is a larger SDK change requiring unmanaged buffer lifetime/cancellation protection, not another Task.Run wrapper.

To distinguish these causes, measure visibility/LOD, demand issue, readiness resolution, traversal and sorting separately, and add SDK counters for gate wait, background read wall time and completion service time. Counters must identify overlap: subtract nested intervals rather than adding them to frame time twice.

## Recommended sequence

1. Native A/B of range-only demand validation, retaining identical policy and content.
2. Add 24 cached leaves and 85 subtree masks; verify identical draw lists and collision behavior.
3. Reuse post-Pump readiness and hot scalar catalog fields; audit memory use.
4. Evaluate demand-set caching with precise invalidation and equal detail progress.
5. If preparation still includes large stalls, instrument/move native storage work in the SDK.

Accept a change using preparation and total frame means/F95/F99 **together with** exact selected-LOD readiness, blocked moves, submitted geometry, storage bytes and repeated baselines. The tests already export those signals. This investigation identifies candidates; these optimizations have not been enabled or represented as measured wins.
