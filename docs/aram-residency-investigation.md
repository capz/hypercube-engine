# Dynamic ARAM residency across DolphinDotNet, Hypercube and AnimalTown

Investigation of the current main branches on 2026-11-12. This is a design and
source/asset audit; it enables no new runtime paging policy. Moving a copy to
ARAM saves MEM1 only after its original allocation is actually released.
Asynchronous DMA alone does not establish unchanged frame time.

| Repository | Audited revision | Consumer versions |
| --- | --- | --- |
| DolphinDotNet | `bac008e509c44cd454eeeec0befad50adf096324` | SDK main includes the memory reductions from PR 171 |
| Hypercube | `a5d2eea566c0521aa5a91a8b177ebdd11df49518` | Source pin and published SDK dependency are SDK 318 |
| AnimalTown | `31d531feebd3a3dc6567627c60ad8abaa34dac3d` | SDK/Assets 315; Hypercube `0.1.0-preview.28.1` |

AnimalTown's dependency versions and local native patches matter. SDK/engine
main improvements must not be assumed present in its shipped packages. Older
AnimalTown streaming documentation also predates its current polled model
startup. The game uses its own terrain archive and resident texture cache,
rather than Hypercube's V1 `WorldStreamer`.

## Best opportunities

| Priority | Resource and trigger | Potential MEM1 release | Conditions and cost |
| --- | --- | ---: | --- |
| 1 | AnimalTown outdoor terrain working cache while indoors | **1,048,576 B** fixed buffer payload | Terrain is already backed by ARAM. Unbind all mesh views after GPU completion, retain archive/metadata/view identities, and release only the working cache. Reserve and prefetch it before returning outdoors. No extra snapshot upload is needed. |
| 2 | AnimalTown species models whose instances are all in other spaces for a predictable interval | Historical fox-player example: **5,527,232 B** of selected package/texture payload for the other five species, plus eligible instance skin caches | This is a sizing example from older converted assets, not a current-build saving. Store 3,233,472 B of packages in ARAM, retain the active player's model, and restore before any instance needs drawing/animation. Fix shared DMA arbitration and bounded restoration first. |
| 3 | Hypercube idle texture pairs and detailed meshes after demand/fences expire | A physically trimmed texture pair releases `2 * record.Length`; detailed meshes release their native geometry | Existing ARAM source copies can avoid later disc reads, but CRC/decode/create/upload costs remain. Use idle dwell and high/low watermarks; retain rapid-turn working sets. |
| 4 | AnimalTown cold UI atlases | Historical heading font + item icons: **786,432 B** pixel payload | Current fonts/batches retain concrete texture aliases, and menus can open immediately. Keep these hot initially; page only after an explicit readiness/prefetch contract exists. |
| 5 | Larger future building/interior catalogs | Historical interior geometry under the current retention policy: only **33,504 B** | Current retained building models reject SDK copy/unload. Cottage parts share a 20,480 B texture that exterior users keep alive. A compact static-geometry pager would be needed; this is low value for today's shared kit. |

These opportunities are conditional and must not be summed as a demonstrated
concurrent saving. Object fields, allocator overhead, shared ownership, retained
metadata, staging, destination/retirement overlap and fragmentation affect the
net result. No texture-resolution reduction or visible-object omission is
needed for the proposed policy.

## Global ARAM budget and ownership

The [SDK allocator][sdk-aram] manages internal 16 MiB ARAM, excluding the lower
16 KiB system area and the application's initialization reservation. AnimalTown
reserves 2 MiB for audio/platform ownership, then reserves 8 MiB for terrain:

| Item | Bytes |
| --- | ---: |
| Physical ARAM | 16,777,216 |
| System base | −16,384 |
| Audio/application reservation | −2,097,152 |
| Capacity available to SDK allocations | 14,663,680 |
| Existing terrain allocation | −8,388,608 |
| Remaining before other SDK owners | **6,275,072** (5.984375 MiB) |

The allocator has 256 allocation slots, 32-byte-rounded first-fit blocks and no
compaction. A large model can fail admission despite sufficient aggregate free
bytes; check the largest free block and slot count. Disposed but held allocations
still occupy space until DMA drains. Use a shared budget and bounded arenas for
numerous small records; separate per-cache budgets are insufficient.

The [historical terrain measurement][town-terrain-evidence] used 3,840,416 B of
its 8 MiB reservation. That leaves 4,548,192 B reserved but unused in that town.
Shrinking a sealed allocation or allocating bounded terrain segments could
recover ARAM headroom, but saves no MEM1 by itself. The SDK has no in-place
shrink API today. Do not free/reallocate the archive underneath its live offset
descriptors, or reduce the audio reservation based on guessed usage.

## AnimalTown: use space transitions before camera-only eviction

### Suspend the outdoor working set

[Indoor rendering skips outdoor terrain preparation][town-scene], but the
[archive's mesh views and cache][town-archive] retain their last outdoor
bindings. The native adapter owns an 8 MiB ARAM archive and a separate 1 MiB
MEM1 cache. Its existing `Reset` releases both; calling it on entry would destroy
the backing archive and force a rebuild.

A dedicated suspend/resume lifecycle should:

1. After the final outdoor GPU work completes, unbind every view and release its
   parent holds. Preserve managed/native view identities and archive metadata.
2. Release the MEM1 cache, invalidate selection/bound-state caches, and leave
   immutable ARAM contents intact. Optionally retain it during short doorway
   reversals before demoting it.
3. Before exit commits, reserve a contiguous destination, refill the required
   outdoor sections and rebind views. Retain the interior until this succeeds.

Admission must include the simultaneous interior and returning outdoor working
sets. Avoid repeated 1 MiB allocation churn and fragmentation. Use existing door
transition signals for lookahead; debug teleports, room changes and immediate
reversals need an explicit readiness gate. Current terrain does synchronous DMA
on cache misses, so suspension alone does not make restoration frame-bounded.

### Cold species groups

The game [loads six shared species models and seven instances][town-models].
The player can share a model with an NPC. Native unloading operates on the
**shared model**, releasing its package, last-owned cached textures, and all
matching instances' skinned outputs/workspaces. Wrappers, transforms, metadata,
pose/global matrices, gameplay and animation playback state remain in MEM1.
During the transition, SDK instance drawing/update calls reject that model.
An invisible instance alone is therefore insufficient evidence for eviction.

The [asset inventory](measurements/aram/animaltown-assets.json) contains six
SHA-verified packages from historical game `fca068009bed`, converted with SDK
266. Their combined package length is 3,812,128 B, with 2,621,440 B of unique
runtime texture-copy payload. These textures have distinct hashes across the
species. A fox-player-only room leaves five candidate packages totaling
3,233,472 B, plus 2,293,760 B of separate texture copies: 5,527,232 B of selected
MEM1 payload, before allocator/instance-cache effects. The package already
contains texture bytes; ARAM stores the package once, not both representations.

Current actor art is unchanged, but the pinned converter is now 315. Regenerate
and measure current packages before assigning runtime budgets. Native
`Model.NativeBytes` is a per-model budget charge: shared texture bytes can be
charged to multiple models while physically allocated once. Reclaim estimates
must count unique last owners and instance bytes separately.

Pin the player, visible actors, actors approaching doors/camera bounds and every
model with imminent users. Keep small simulation/collision state hot. Start with
other-space idle windows; camera-only cold decisions need conservative lookahead
for sprinting, cylinder wrapping, NPC movement and sudden camera/teleport changes.

### Assets with poor economics today

- [Building retention][town-retained] has already discarded redundant package
  tables and points meshes at compact geometry. AnimalTown explicitly rejects
  package copy/unload on these models. Original file lengths overstate their
  current MEM1 footprint; do not call `UnloadToAramAsync` on retained buildings.
- Shared floor textures, generated tree meshes, active fonts/icons, player
  resources and logical terrain/collision are small, immediately needed or
  heavily shared. Keep them resident for the initial policy.
- Title/splash textures are already trimmed at the mode boundary. Disposing
  one-shot assets avoids retaining an unnecessary ARAM backup.
- Active audio sources feed native decoder callbacks directly. Future long
  inactive clips could have explicit residency, but continuous audio requires
  a prefetched playback ring; DMA must never run inside an audio callback.
- C# objects cannot become ARAM pointers. Offloading whole objects requires
  versioned serialization, stable IDs and reconstruction; GC roots and native
  handles cannot be copied as a valid object snapshot.

## SDK work needed to preserve frame time

### One DMA broker must include the game-native terrain path

[Generic and native SDK transfers][sdk-native-queue] have separate FIFO queues
but share one native transfer slot. Both retry `-EBUSY`; neither provides
promotion deadlines or promotion-over-demotion priority. Low ARQ priority does
not create priority between these SDK callers.

AnimalTown's [custom terrain DMA][town-dma] attempts `dnd_aram_begin_native`
once, treats every nonzero result as failure, then busy-polls completion.
`TerrainArchive.Prepare` throws on the resulting bind error. Adding model or
texture background transfers can therefore break movement when that channel is
busy, including a completed request whose owning caller has not polled it yet.
Do not enable concurrent paging before replacing/coordinating this path.

Use one native-aware broker with reserved foreground/terrain service,
deadline-aware promotions, speculative prefetch and last-priority demotions.
Arbitrate between bounded chunks, limit queue depth/bytes in flight, and drain
submitted work before releasing its owners. A foreground path must not poll a
different owner's transfer or convert contention into corruption/failure.

### Bulk copies and bounded model publication

[Model residency][sdk-model] copies through a managed 4 KiB buffer. Generic ARAM
uploads always perform read-modify-write, including aligned writes. For an
aligned 1 MiB package, first offload currently entails 256 logical requests,
512 DMA operations moving 2 MiB, approximately 3 MiB of CPU memcpy, and at least
768 transient managed transfer/TCS/task objects, plus repost delegates.

The existing aligned native path can transfer 64 KiB chunks without that bounce
copy/RMW. Model packages need a safe native source lease and rounded tail
ownership before it can be used. Copying into another full-size native buffer
would increase peak MEM1; prefer a direct retained lease or an explicitly
budgeted reusable small workspace.

[Restore finalization][sdk-restore] synchronously hashes and validates the whole
package, recreates texture allocations, flushes data and evaluates every matching
instance. Unloading also synchronously retires instance buffers and textures.
Other native resource releases, such as `NativeBuffer` disposal, can wait for GX.
`Task.Run` does not bound these operations or
provide another GameCube core; managed executors share an execution gate.

Add separately bounded copy, validation, texture creation/publication,
instance-reconstruction and retirement steps. Keep required live resources
until atomic publication succeeds. Reserve restore peaks first. Existing cold
model snapshots stay pinned in ARAM after restoration until `Model.Dispose`;
a global cold-budget policy also needs explicit evictable backing ownership.
`PrepareAnimationAsync(name)` restores the whole model, not just one clip.

## Hypercube: turn a storage cache into an explicit residency policy

`UseAram=true` alone releases **zero MEM1 bytes**: [the streamer][engine-stream]
caches verified source records while keeping native residents until ordinary
eviction. Mesh eviction disposes storage; texture eviction only unbinds a reusable
double-buffered pair. [Physical trimming][engine-pool] is required to reclaim it.
Keep the existing reference, priority, generation and GPU-fence protections.

The deterministic 48-resolution fixture has 649 records and SHA256
`0a782f8137b426528a185cb8bbb5a67ae50d8fc5448ef6a88d56016daec49024`:

| Candidate set | Records | Packed bytes | ARAM bytes after per-record alignment | Native payload |
| --- | ---: | ---: | ---: | ---: |
| Group-2 CMPR fine textures, three LODs | 18 | 22,080 | 22,080 | 44,160 in pairs |
| Clustered fine meshes, three LODs | 136 | 296,598 | 298,624 | 324,354 |
| Collision | 24 | 1,056 | 1,536 | 1,056 duplicate bytes; decoded hot arrays remain |
| Permanent coarse patches | 89 | 304,260 | 305,632 | 334,620 |

The first three sets require 178 entries and 322,240 ARAM bytes, exceeding the
current 256 KiB cold budget by 60,096 B. The MEM1 scheduler independently has
128 slots. Increasing budgets cannot bypass the SDK's global 256 ARAM-allocation
limit; arenas would be needed for large per-record catalogs.

Current cold hits require every record in a batch to be cached; one miss sends
the whole batch to storage. Fetches are serial. Optional stores start before
MEM1 admission, and the next batch waits for every store because they borrow the
same staging buffer. Decouple only with explicit source holds and bounded extra
storage. A second full source buffer costs another 73,984 MEM1 bytes here.
Skipping speculative demotion when demand work is pending may be cheaper.

Before decoupling, add `Filling/Ready/Failed` cache states and asynchronous failure
rollback: [current Store][engine-cold] exposes an entry before DMA completes.
The existing one-batch drain prevents its normal reader from racing that write;
removing the drain without a replacement would break that protection.

Prefer revisit-aware admission, partial-hit batches, idle-pair trimming and
prefetch before detail demand. Keep collision and coarse fallback resident:
44-byte collision records offer little gain, while a coarse fallback protects
movement/rendering deadlines. Main contains texture-family/LOD demand metadata,
but no generic family GPU pager; unmerged progressive branches are not assumed.

## Acceptance and implementation order

1. **Measure and coordinate first.** Add the shared broker, global memory and
   ownership accounting, and bounded prepare/retire steps. Do not change default
   residency until baseline traces and these contracts exist.
2. **Suspend outdoor cache indoors.** Exercise entry/exit/reversal, retained
   aliases, allocation failure, save/load and debug teleports. Keep interior
   ownership until outdoor prefetch is ready; never silently omit geometry.
3. **Page other-space model groups.** Prove all-user pinning, animation/state
   preservation, cancellation, shared-texture accounting and retry/failure paths.
4. **Extend engine policies/catalogs as demand grows.** Add partial-hit source
   caching and GPU retirement, then mip-aware family/asset adapters. Defer tiny
   collision/UI/building candidates until measured pressure justifies them.

Measure separate MEM1 resident/idle/staging/destination/retired bytes, ARAM
allocated/ready/pending bytes, largest block/slot counts, actual DMA read/write
bytes, queue wait, copy/CRC/decode/create/GX-wait time, managed allocations/GC,
detail-ready deadlines, churn, missed presentations, input latency and audio.
`AramBytes` currently counts cold-read batch spans only; `MaxUploadUs` starts
after cold-store admission and omits that overhead.

Compare matched replays at equal asset quality and identical initial saves,
camera, movement and audio. Include cold/warm travel, fast reversals, doors,
indoor/outdoor transitions, teleports and low-memory/fragmented conditions.
Restore lead time must exceed measured queue wait + DMA + CPU reconstruction +
publication time, with margin. If the deadline/headroom is unavailable, retain
the asset or postpone demotion; never wait for a cold read in Draw/Step.

The existing fixture's comparable-layout no-ARAM/ARAM cases showed fewer storage
reads (129 to 87), but rounded p99 frame time was 17 to 18 ms and publication
counts differed. This is illustrative, not a matched performance proof. Keep
the same presentation cadence and visible readiness, and require no regression
in repeated hardware p95/p99/missed-deadline/input/audio measurements. Dolphin
can check correctness and compare traces; it cannot establish stock-console DMA
bandwidth, audio coexistence or a zero-impact guarantee.

## Evidence and focused verification

[Cross-repository budget data](measurements/aram/cross-repo-budget.json) and the
[historical asset inventory](measurements/aram/animaltown-assets.json) distinguish
source observations, derived payload figures and older release measurements.
The latter records release/manifest/package hashes; current SDK 315 asset tools
were not cached, so this audit did not regenerate today's converted game assets.

The existing SDK host ARAM test passed at the audited source revision:

```sh
cc -std=c11 -O2 -Wall -Wextra -Werror -Iinclude -DDND_ARAM_TEST \
  tests/aram_test.c source/aram.c -o /tmp/dnd-aram-offload-audit-test
/tmp/dnd-aram-offload-audit-test
```

It exercises alignment, capacity, fragmentation/coalescing, 256 handles, busy
transfers, adjacent-byte preservation, pending disposal and injected faults.
This verifies simulated ownership/allocator behavior, not hardware throughput.
No console build or new runtime benchmark is needed to validate this docs-only
investigation; performance acceptance remains future implementation work.

[sdk-aram]: https://github.com/capz/DolphinDotNet/blob/bac008e509c44cd454eeeec0befad50adf096324/source/aram.c#L39
[sdk-native-queue]: https://github.com/capz/DolphinDotNet/blob/bac008e509c44cd454eeeec0befad50adf096324/src/DolphinDotNet.GameCube/Dolphin/Graphics/AramTransferQueue.cs#L15
[sdk-model]: https://github.com/capz/DolphinDotNet/blob/bac008e509c44cd454eeeec0befad50adf096324/src/DolphinDotNet.GameCube/Dolphin/Graphics/Model.cs#L217
[sdk-restore]: https://github.com/capz/DolphinDotNet/blob/bac008e509c44cd454eeeec0befad50adf096324/source/model.c#L611
[town-scene]: https://github.com/capz/AnimalTown/blob/31d531feebd3a3dc6567627c60ad8abaa34dac3d/game/AnimalTown.GameCube/Program.cs#L638
[town-archive]: https://github.com/capz/AnimalTown/blob/31d531feebd3a3dc6567627c60ad8abaa34dac3d/game/AnimalTown.GameCube/TerrainArchive.cs#L72
[town-dma]: https://github.com/capz/AnimalTown/blob/31d531feebd3a3dc6567627c60ad8abaa34dac3d/tools/native-terrain-archive.inc#L29
[town-models]: https://github.com/capz/AnimalTown/blob/31d531feebd3a3dc6567627c60ad8abaa34dac3d/game/AnimalTown.GameCube/Program.cs#L112
[town-retained]: https://github.com/capz/AnimalTown/blob/31d531feebd3a3dc6567627c60ad8abaa34dac3d/tools/patch-managed-allocator.py#L23
[town-terrain-evidence]: https://github.com/capz/AnimalTown/blob/31d531feebd3a3dc6567627c60ad8abaa34dac3d/docs/performance/2026-11-09-immutable-terrain.json
[engine-stream]: https://github.com/capz/hypercube-engine/blob/a5d2eea566c0521aa5a91a8b177ebdd11df49518/src/Hypercube.GameCube/WorldStreamer.cs#L185
[engine-pool]: https://github.com/capz/hypercube-engine/blob/a5d2eea566c0521aa5a91a8b177ebdd11df49518/src/Hypercube.GameCube/TexturePool.cs#L21
[engine-cold]: https://github.com/capz/hypercube-engine/blob/a5d2eea566c0521aa5a91a8b177ebdd11df49518/src/Hypercube.GameCube/AramCache.cs#L58
