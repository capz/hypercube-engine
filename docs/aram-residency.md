# Cold resource residency

The V1 world streamer now reclaims genuinely unused detailed native resources
and can retain their verified packed source bytes in ARAM. Coarse world patches,
coarse textures and decoded movement data remain available while detail restores.
ARAM cannot be rendered directly; cache hits still need native creation or
texture staging before publication.

## Defaults and controls

`WorldStreamer` retains both existing constructor signatures. Normal pumping
uses these public controls:

```csharp
streamer.RetireIdleResources = true;
streamer.IdleResourceFrames = 120;
streamer.IdleTextureFrames = 120;
streamer.MaximumIdleReleases = 2;
streamer.WarmTextureHighBytes = 32768;
streamer.WarmTextureLowBytes = 16384;
```

At 60 frames per second, the default demand dwell is two seconds. Detailed meshes
and texture mappings must remain undemanded throughout that window, and their
last submitted frame must have completed before retirement. Re-demand resets
the dwell. Collision residents are excluded from automatic idle retirement;
normal budget-pressure eviction and movement-readiness checks remain in force.

Retiring a mesh frees its native geometry. Retiring a texture mapping leaves its
physical pair available for fast reuse. When idle texture bytes exceed the high
watermark, physical trimming starts after the separate texture dwell and continues
to the low watermark. Each pump retires at most the configured number of logical
residents and trims at most that many texture pairs. Pending texture copies and
GPU readers prevent reuse and trimming. These controls allow bounded warm reuse
for camera reversals rather than allocating on every visibility change.

Set `RetireIdleResources=false` for a fixed-policy comparison. This disables
automatic dwell-based reclamation, while preserving the existing hard residency
budget and pressure eviction. A frame count is a policy input, not a hardware
timing guarantee; callers supply monotonic `Frame` and actual `CompletedFrame`
values and pump between graphics frames.

## Optional cold writes

`UseAram` remains opt-in. The validation's ARAM and fault scenarios enable it;
other scenarios remain useful storage controls. A verified, admitted record may
be copied into one lazy 16 KiB native cold-write buffer using the world's existing
decode scratch. One optional upload can be outstanding at a time. Further writes
are skipped while that buffer is busy, or when a record exceeds its capacity.
Large records remain recoverable from the pack on storage. No second full-size
read buffer is allocated.

Cold writes no longer retain the main read/upload source or keep its batch active.
A subsequent visible batch can progress while the separate cold buffer is owned
by DMA. The cold buffer is never overwritten or freed before its task completes.
The SDK transfer queue still owns the native holds; completion releases them.
Copy, allocation and asynchronous cache failures only disable that optional
cache entry. A synchronous admission failure backs off for 120 frames rather
than repeatedly allocating on every record.

The cache now exposes `Missing`, `Filling`, `Ready` and `Failed`. `Has` only
returns true for `Ready`. Polling observes completion, releases failed or
cancelled allocations exactly once, and permits a later retry. Filling entries
cannot be fetched, replaced, evicted or removed. Cache disposal requires uploads
to drain; existing clear-and-reuse behavior is retained. Native holds allow an
already-issued fetch to finish after the ready entry is removed.

`Stop()` drains both foreground work and optional cold writes without starting
new work. `Dispose()` rejects undrained owners. Optional writes do not gate
ordinary foreground batches, but shutdown must still preserve their source
lifetime.

## Mixed cache hits and publication

An all-storage batch retains its coalesced read. A batch containing any ready
cache entry fetches hits from ARAM and coalesces consecutive misses into storage
runs. Cached entries split those runs. One miss no longer discards the other
records' cache benefit, and several adjacent misses need only one media read.
Cache-transfer
failure invalidates that entry and falls back to storage. Cached data that fails
the ordinary CRC check is removed so subsequent retries use the source of truth.

Generations, stale cancellation, source/upload drain, checksums and explicit
texture publication remain authoritative. An ARAM hit is never exposed directly
to a material, and a cancelled or failed upload cannot publish visible content.

## Accounting and performance limits

`PhysicalBytes` retains its dynamic-native-residency meaning, including reusable
idle texture pairs. `Policy.ResidentBytes` only counts logical residents.
`StagingBytes` remains the original read-source allocation.
`ColdStagingBytes` separately reports the lazy 16 KiB cold buffer. The validation
includes both staging allocations in native baselines and teardown checks.

Cold ARAM allocations and metadata are separate. Active cache metadata adds one
`Task?[recordCount+1]` array; it is absent while the cache has never admitted an
allocation. The idle-residency policy adds bounded bookkeeping for 128 resources
and 48 texture pairs. These managed fields/arrays do not reduce the SDK's reserved
managed heap. Compare actual reclaimed native bytes against the extra cold buffer
and metadata rather than treating cache bytes as MEM1 savings.

`ColdWriteBytes` and `Cold.StoreBytes` report aligned requested upload bytes;
they include submitted requests that later fail. `AramBytes` counts requested
aligned fetch bytes. `PartialCacheBatches`, `ColdWriteSkips`, `ColdWriteFailures`,
`ColdReadFailures`, `ColdReleases` and `ColdTextureTrims` expose policy activity.
The DMA service can split requests internally; these are not hardware bus-byte
measurements or queue-latency statistics.

The frame budget still cannot preempt an individual native allocation, copy or
free. The extra source snapshot copies at most 16 KiB twice on the application
executor. The pinned SDK routes the compatible copy calls through its shared
native DMA broker: cache restores use demand priority and optional writes use
background priority. It reconsiders priority at each DMA boundary of at most
16 KiB, with round-robin service for independent equal-priority requests.
Conflicting ARAM or MEM1 ranges preserve submission order, and an active DMA
must drain before another can begin. Chunk bounds therefore do not guarantee
restoration deadlines; queue wait, dependent snapshots and bus contention still
need measurement. The engine retains the original public copy signatures.

Acceptance requires matching scene quality, request replay and cold/warm state,
then comparing native high water, selected-detail readiness, allocations and
p95/p99 frame times with audio active. Camera reversals, rapid movement, ARAM
exhaustion and storage fallback are required cases. Host/AOT/Dolphin ownership
checks do not establish original-console throughput or zero contention.

## Recorded fixture acceptance

The [Release Dolphin evidence](measurements/aram/engine-dolphin.json) records the
SDK revision, tested working-tree source hashes and immutable DOL/data-disc
hashes. All eleven scenarios completed with mask 2047. The occlusion comparison
matched all 199,680 tested pixels, and the DSP capture contains 188.656 seconds
of continuous audio with no quiet windows. Native accounting returned to its
544 KiB baseline after leaving the two scenarios with a 16 KiB cold source.

This software-rendered Dolphin run verifies functional behavior and ownership.
It is not a stock-console performance benchmark, and its quantized guest frame
counters do not establish zero overhead or matched-quality throughput.
