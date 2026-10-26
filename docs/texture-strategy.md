# Virtual Textures vs Cell Atlases

**Decision pending:** compare fine-grained sparse pages with coarser texture atlases under identical content and real gameplay load.

## Sparse page candidate

- Useful virtual page: 128×128, plus 4-texel edge gutter = 136×136 CMPR (9,248 bytes).
- CPU chooses virtual LOD and resident tile; GX samples ordinary bound textures.
- Permanently resident coarse mips provide fail-safe display while pages stream.
- Offline hierarchical geometry patches merge neighboring work at coarse LOD; **never** subdivide only to finest-resolution page boundaries.
- Bilinear filtering initially, with hysteresis at LOD boundaries. Neighbor gutters resolve seams; optional dual-mip blending requires separate GPU cost study.
- Larger 256×256 page class can be profiled for coherent terrain.

## Cell atlas candidate

- Compile geometry and textures into coarse world-cell atlases.
- Fewer texture binds/draw calls, but extra bytes loaded when only part of an atlas is needed.
- Batch visibility and LOD at cell/cluster level, retaining coarse fallback representation.
- Prefer this for coherent architecture and close-spaced scenery.

## Implementation experiment

The [DolphinDotNet TextureStrategy experiment](https://github.com/capz/DolphinDotNet/tree/experiment/virtual-texture-vs-atlas/samples/TextureStrategy) provides a **first-stage render-granularity comparison**: the same 12×12 procedural textured quad field as 144 page draws or nine cell-atlas draws, plus deterministic logical LRU cache traces. This is **not yet** a virtual-texturing implementation or streaming throughput benchmark. It does not measure ARAM, storage, CMPR pages, page gutters, mip LOD or actual hardware latency.

## Selection criteria

Measure on a real GameCube: GPU/CPU milliseconds at the same visual detail, texture binds, opaque draws, resident bytes, storage/ARAM bytes per second, p95/p99 page promotion latency, visible fallback fraction and worst-case rapid camera reversals. Select a **hybrid** if large unique surfaces benefit from fine pages but architecture/terrain clusters are faster with atlases.

Gate: no design lock until an interactive camera and actual asynchronous streaming demonstration have stable performance and correct fallbacks.

## Packed scene continuation

The [engine validation executable](streaming-validation.md) adds packed GX textures, dense mesh intake, shared scheduling, collision readiness, optional ARAM caching and conservative visibility comparisons. The original SDK texture stress and nine-case comparison remain available and unchanged.
