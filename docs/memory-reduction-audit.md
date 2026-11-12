# Memory reduction audit

Baseline: `b6eee61` (SDK 318). This audit covers current `main`'s v1 packed-world
loader, streaming adapter, catalog and rendering helpers. It does not assume
unmerged progressive-runtime branches are present. The target has 24 MiB MEM1;
managed objects, native assets and framebuffers all compete for that physical
memory. ARAM is a separate DMA-backed tier, not directly GX-addressable memory.

## Implemented reductions

The deterministic 48-resolution validation pack has 649 records and a largest
supported streamed record of 73,984 bytes. The new accounting tool checks pack
CRCs/ranges and reports its source SHA256; engine load-time admission still
validates descriptor semantics. The checked-in JSON is **payload accounting**,
not a physical-console trace.

| Allocation | Before | After | Reduction |
| --- | ---: | ---: | ---: |
| Native streaming source buffer | 131,072 B | 73,984 B | 57,088 B |
| Catalog ID indexes/links, retained managed payload | 5,724 B | 2,862 B | 2,862 B |
| Scheduler ID-to-slot lookup, retained managed payload | 2,600 B | 1,300 B | 1,300 B |
| ARAM metadata while unused, managed payload on 32-bit target | 5,200 B | 0 B | 5,200 B |
| Temporary catalog mesh-tail payload | 384 B | 192 B | 192 B |

The persistent reduction with an unused cold cache is **57,088 native bytes**
plus **9,362 managed payload bytes**. With ARAM caching active, the persistent
managed reduction is 4,162 bytes after the first successful cold allocation.
Object/array headers and allocator rounding are excluded from these payload
figures. The SDK's reserved managed heap is unchanged: lower live occupancy
reduces pressure but does not automatically return that reservation to the
native allocator. No heap-size default or texture quality is reduced.

### Bounded native staging

`StreamingBufferPolicy` reserves the aligned maximum of a configured coalescing
ceiling and every supported streamed record. The default ceiling is 65,536 bytes;
existing validation targets of 32/64 KiB fit. A single larger supported record
still fits even when the requested coalescing target is smaller. Unsupported
records above 131,040 bytes retain their existing demand-time rejection, so an
unused unsupported fine level does not newly reject an otherwise usable pack.

`ReadBatchBytes` remains a soft target and is capped by `MaximumReadBatchBytes`.
For the former maximum coalescing range, explicitly construct:

```csharp
new WorldStreamer(world, path, budgetBytes, maximumReadBatchBytes: 131040);
```

The original three-parameter constructor signature is retained for compiled
clients. Capacity is reserved before streaming begins; `Pump` never resizes or
disposes the source buffer. This avoids introducing a GPU wait through the SDK's
conservative native-buffer disposal. Source holds, cancellation drain, transfer
padding, CRC and texture publication are unchanged. `StagingBytes` reports this
separate allocation; `PhysicalBytes` retains its existing dynamic-residency
meaning. Scenario teardown checks now use the actual staging size.

### Compact IDs and lazy ARAM metadata

Catalog record IDs are bounded by 4,096, so private indexes/links use `ushort`.
Scheduler capacity is bounded by 256; its lookup stores slot+1 in `ushort`, with
zero as the missing sentinel. Public return types, generation checks, record
ordering and the cached `AssetRecord[]` representation are unchanged. Maximum-ID
and slot-255 tests exercise relocation, stale mappings, cancellation and drain.

Combined retained catalog/scheduler payload savings are `4*recordCount+1566`
bytes: 4,162 B at 649 records and 17,950 B at 4,096. The catalog constructor also
uses smaller temporary mesh-tail IDs without retaining or mutating its caller's
metadata buffer.

An unused `AramCache` no longer reserves per-record entry/age arrays. Metadata is
created only after the first native cold allocation succeeds; empty probes,
over-budget stores and failed native allocation do not allocate it. Once
active, the cache uses the same bounds and LRU/protected-ID behavior. On the
32-bit target this avoids `8*(recordCount+1)` payload bytes while unused, up to
32,776 B at 4,096 records. This one-time first-use allocation may occur during
streaming; normal stores already allocate transfer/ARAM ownership. Callers still drain
transfers before disposal. Zero-ID entries are now evicted and freed correctly.

### Bootstrap and failed construction

The synchronous bootstrap file is scoped to world construction. Its handle and
SDK/stdio resources are no longer retained for the world's lifetime; the
independent asynchronous `AssetStream` remains owned by the world. Failed world
construction also releases already-created coarse meshes, textures and asset
stream ownership. This prevents malformed input or allocation failure from
leaving native resources behind. Cleanup is on the bootstrap/error path, not the
steady-state promotion path. SDK-free allocation-failure/corruption tests verify
partial ownership is released.

## DolphinDotNet audit

The SDK changes are delivered separately from the engine changes. This engine
keeps its source pin and published NuGet dependency on SDK 318 until a matching
SDK release is available; source and package consumers must use the same reviewed
SDK revision. The engine reductions above work with SDK 318 independently.

The SDK renderer previously reserved two 4,096-entry model submission tables,
although it admits at most 4,096 submissions in total. A shared table preserves
opaque insertion order, transparent depth sorting and stable ties while removing
65,536 B of native static storage. This saving applies when the executable links
the model submission path. A raw-mesh-only Hypercube executable may discard that
section, so this is not added to the world staging savings above.

SDK transforms now reserve a local matrix only when first parented, and a child
list only when a child is first attached. A never-parented leaf keeps its existing
world-matrix cache and computes TRS directly into it. `MeshBatch`'s 128 root poses
therefore avoid 8,192 B of local-matrix payload plus child-list objects, empty
backing arrays and headers. Hierarchy caches are reserved during attachment and
retained for reuse; matrix updates and draws remain allocation-free after setup.
These are managed occupancy savings, not a change to the reserved heap size.
The SDK's host CLR probe measures 47,128 B before versus 28,696 B after for 128
root transforms, including the array that holds them: an 18,432 B reduction.

Native skinning workspace now holds the largest validated skin in a model
instead of always reserving room for 256 joints. A two-joint instance uses
288 B rather than 32,800 B including the native allocation header and alignment.
A cached stride avoids scanning skins per update; its extra 4 B per PPC instance
leaves a net 32,508 B saving in that example. A 256-joint model keeps its former
workspace size and pays the 4 B stride field. Budget checks, inverse-transpose
normal matrices, unload/recreation and cleanup remain covered by native tests.

## Other opportunities and why they are deferred

| Opportunity | Potential | Required follow-up |
| --- | --- | --- |
| Dense permanent-patch metadata | The 649-record fixture reserves 7,800 B of 32-bit array payload for 89 permanent patches; dense storage would use about 1,068 B plus a mapping | `Patches`, `TriangleCounts` and `VertexCounts` are public arrays indexed by record ID. Introduce/version an accessor API before changing that contract. |
| Compact or streamed catalog metadata | Parsed records alone occupy 56 B per record; input metadata adds another 64 B per record during construction | Reconstructing records in `Get` adds work to a hot path; a streaming parser needs bounded-read/CRC and v2-family tests. Keep cached records for this pass. The 4,096-record format limit is not a promise that a full world fits the default 256 KiB managed heap. |
| Further flatten `MeshBatch` transforms | After SDK transform compaction, 128 root poses still retain 13,312 B of float-array payload plus headers | A flat-matrix/native batching path needs transform, ordering, allocation and throughput parity. Keep reusable preallocation. |
| Drop decoded collision source copies | Up to 24 extra 44-byte native payloads, plus allocator overhead, duplicate decoded heights/masks | Residency and cell-readiness currently use their ownership during eviction. Change the accounting and lifecycle together, with movement/hole tests. |
| Shrink decode scratch | A valid maximum mesh needs 13,068 B versus the 16,384 B scratch reservation | Small gain relative to source staging; preserve public scratch/decode behavior until a separate bounded workspace API is agreed. |
| Reduce texture-pool copies or idle retention | Double-buffered textures and reusable unbound allocations dominate some working sets | Active GPU readers and async staging require their storage. Use measured residency/quality policy or the independent progressive-level runtime; do not free a front/back allocation or reduce quality implicitly. |
| Lower heap/framebuffer/ARAM reservations | Potentially larger physical MEM1 savings than live-object compaction | Requires representative console high-water, video mode, stacks, audio and pressure measurements. No physical-console performance or reserve-sizing claim is made here. |

## Reproduction and acceptance

```sh
python3 tools/build_assets.py --resolution 48
python3 tools/memory_audit.py out/assets/world.hcp --output out/memory-audit/world.json
python3 tools/measure_core_allocations.py --revision b6eee61
python3 tools/measure_core_allocations.py
python3 tools/test_host.py
python3 tests/progressive_fixtures.py
dotnet run --project tests/TextureFamilies.Tests -c Release
```

The CLR probe exports baseline or working-tree Core sources to an isolated
project, warms constructors, excludes input-buffer creation and counts allocated
bytes for catalog and a capacity-256 scheduler (the streaming adapter uses 128).
It is a host measurement, not a
GameCube heap measurement. On this executor, totals were:

| Record count | Baseline allocated | Updated allocated | Reduction |
| --- | ---: | ---: | ---: |
| 649 | 55,008 B | 50,664 B | 4,344 B |
| 4,096 | 275,624 B | 257,488 B | 18,136 B |

Validation includes maximum record/slot boundaries, coalescing/alignment and
unsupported levels, unchanged catalog ownership, ARAM LRU/protection/copy failure,
empty-cache allocation bounds, constructor cleanup, both ordered-rendering paths
and full console-scene CI. Console checks must continue to cover stale/canceled
work, pixel consistency, audio, movement readiness and zero-resource teardown.
