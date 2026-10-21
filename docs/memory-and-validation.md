# Memory, Streaming and Validation

## Initial MEM1 plan (MiB)

| Component | MiB |
|---|---:|
| Engine/code and stacks | 3 |
| Game/physics/animation | 4 |
| Geometry and scene buffers | 5 |
| Virtual page cache | 4 |
| Regular textures | 3 |
| External framebuffers / effects | 2 |
| Staging and DMA | 1.5 |
| Reserve | 1.5 |
| **Total** | **24** |

External framebuffer sizing must be adjusted to actual mode and output buffering. Static allocations and native runtime overhead must be audited rather than assuming all 24 MiB is available for application assets.

## ARAM plan (MiB)

| Allocation | MiB |
|---|---:|
| Audio-reserved | 2 |
| VT second-level cache | 8 |
| Geometry/cell cache | 3 |
| Animation/asset cache | 1 |
| Flexible reserve | 2 |
| **Total** | **16** |

ARAM cannot be sampled by GX or directly dereferenced as MEM1. Transfer data asynchronously to aligned MEM1 staging buffers using the platform AR/ARQ DMA API; maintain CPU/GPU data-cache coherency, lifetimes and fences. Audio/DSP traffic has priority. Treat 60–70 MB/s as an optimistic peak reference, not a sustained game transfer rate. A starting promotion budget is 128 KiB/frame at 30 FPS, subject to console measurements.

## Resource priority

P0 collision, gameplay and critical audio; P1 coarse geometry and always-available fallbacks; P2 visible detailed tiles; P3 predicted incoming cells; P4 optional detail. Resources hold stable IDs, generation counters, location, LRU/use probability, references, in-flight transfer state and eviction locks. Storage-to-MEM1 direct paths should bypass ARAM when staging adds no value.

## Delivery phases

0. Profile GX texture loads, state changes, storage and ARAM DMA, with audio running.
1. Texture strategy render experiment (DolphinDotNet sample; provisional).
2. Offline CMPR packer, mip hierarchy, proper gutters and geometric patch hierarchy.
3. Runtime MEM1 pages, fallback resolution and working-set telemetry.
4. ARAM-backed and storage-backed asynchronous page promotion, cancellation and eviction.
5. World cells, physics/collision readiness, visibility and mesh LOD.
6. TEV material presets, baked lightmaps, dynamic actor lighting and stress-test settlement.

Acceptance requires a representative **playable** 30 FPS original-console test, camera-sweep/reversal streaming stability, no missing-page holes, audio continuity, and quantitative comparisons against cell-atlas rendering. Emulator measurements are supplementary.

## Major risks

Texture page splitting can cause excessive draws; fix via hierarchical merging. Atlas overfetch can overload storage; compare equal-budget misses **and** bytes transferred. ARAM promotions can compete with audio; prioritize safely. Small MEM1 budgets constrain whole-game utility; test during animation and gameplay rather than static demos.
