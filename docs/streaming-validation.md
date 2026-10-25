# Streaming validation implementation

This implementation turns the next architecture questions into executable comparisons. It is a validation foundation, not a complete production renderer or general asset optimizer.

## What is implemented

| Layer | Implementation | Evaluation |
| --- | --- | --- |
| Content | Static OBJ/glTF/GLB, exact attribute/material welding, invalid/duplicate face removal, bounded clusters | Raw triangle soup versus repaired geometry; provenance and removals in report |
| LOD | Clipped cell boundaries, conservative vertex clustering, merged quadtree parent patches | Triangle counts and maximum vertex/UV displacement; exact boundary tests |
| Texture | Offline GX-native RGBA8/CMPR, four explicit mip levels, neighboring-source gutters | Format and 1/2/4-cell tile comparisons in the same executable |
| Storage | Versioned pack, index and per-record CRC, 32-byte alignment, native staging | Adjacent records share one logical AssetStream read |
| Residency | 64 asset slots, reusable pool of 24 double-buffered textures, fixed staging | Enforced dynamic native resource budget and transition leak assertions |
| Scheduling | Gameplay > coarse > visible > prefetch > optional, generations, cancellation, retry backoff | Stale completions cannot publish; pressure/failure cases |
| GPU ownership | Publication between frames, per-frame pins and completion fences | No reuse before a submitted frame completes |
| Gameplay | Independent coarse and collision readiness | Moving actor waits at an unready destination |
| Visibility | Conservative AABB frustum, quadtree fallback traversal, material sorting | Actual submitted geometry counters |
| Occlusion | Verified opaque rectangular wall, conservative projected test | Fixed-pose reference/cull image comparison plus draw reduction |
| ARAM | Optional 256 KiB bounded cold cache, aligned asynchronous native transfers | Direct-storage and ARAM cases, transfer and cache-hit counters |
| Audio | Looping resident PCM voice while streaming | Voice activity checked every frame; captured DSP samples checked for sustained output |

## Reading the comparisons

Cases 0–8 run the same moving-camera path except the pressure case, which repeatedly jumps across the scene. The measured period lasts at least ten guest seconds and sixty rendered frames. Each transition drains outstanding reads/uploads/ARAM transfers and checks that native resource totals return to the fixed baseline.

1. RAW: unrepaired triangle soup, small RGBA8 pages.
2. REPAIRED: welded/repaired clustered geometry with LOD selection.
3. CMPR: native compressed pages.
4. BATCHED: larger logical reads and up to eight adjacent records.
5. ATLAS2: two-by-two-cell texture groups.
6. ATLAS4: four-by-four-cell groups.
7. ARAM: optional cold cache.
8. FAULTS: delayed completion, short reads, actual staging corruption and withdrawal of staged texture requests.
9. PRESSURE: rapid demand changes, failures and a tighter 48 KiB cache budget.
10. OCC REF: fixed camera and leaf coarse geometry.
11. OCC CULL: identical pose/geometry/materials with conservative occlusion.

Read/publication counts describe logical operations. AssetStream internally reads in bounded chunks; these counters are not physical SD or optical-drive transactions. Double-buffered textures are charged at twice their payload size. SDK resource bytes include native staging, geometry, textures and the diagnostic canvas; they exclude allocator overhead, the managed heap, audio allocations and other runtime/driver allocations. They are not total MEM1 usage.

One shared 8 KiB stream-read buffer and reusable 384-vertex/index decode arrays are reserved before coarse loading. Large per-asset managed arrays are avoided; native meshes copy only the used range. Small task/work objects remain managed.

The fixture deliberately remains within the SDK's unchanged 256 KiB managed heap and the coarse hierarchy has a separate 1 MiB native geometry limit. The dynamic budget excludes permanent coarse resources and the 128 KiB native staging buffer. ARAM caching has its own bound and the audio reservation is separate.

## Pack contract

All integer metadata is little-endian. The 32-byte header contains `HCPK`, version 1, record count, index offset, payload start, exact pack length, index CRC32 and a zero reserved word. Each 64-byte index record describes ID, kind, offset, length, CRC32, native format, tile group, mip, cell/node, parent, width, useful width, material, variant and two zero reserved words. Payload starts are 32-byte aligned. IDs are one-based; zero means no dependency. CRC uses incremental IEEE CRC32.

Kinds are native texture (1), fine geometry (2), collision (3), parent patch (4) and scene metadata (5). Mesh payloads have a 12-byte `HCMS` header, 32-byte fixed-point vertices and unsigned 16-bit triangle indices. Textures use hardware RGBA8 tile planes or CMPR blocks directly. The runtime validates bounds, descriptors and checksums before allocating/publishing resources. Data ISOs are additionally padded to complete 2048-byte sectors.

## Decisions and remaining gates

Commit now to offline processing, bounded native ownership, separate collision readiness, explicit coarse fallback, cancellation generations and fenced reuse. These are useful across tile sizes and storage choices.

Keep texture group size, compression policy, read batch size, LOD thresholds and ARAM caching configurable. Choose defaults using representative content and hardware measurements. Favor fewer submitted triangles/draws and bounded latency, rather than vertex count alone. Large atlas tiles can waste cache capacity on invisible material; small pages may increase reads and submissions.

The simplifier measures vertex displacement, not a certified Hausdorff or silhouette bound. It preserves exact clipped boundaries, UV/normal/material domains and disconnected pieces, but is not a mesh reconstruction system. It cannot infer whether a floating component was intended. Imported PBR textures, transparent materials, skinned animation, arbitrary collision surfaces, general occluder extraction and audio underrun instrumentation remain production work. An AI-produced representative asset is still required for judging appearance and realistic compression/LOD quality.

The scene telemetry protocol is version 2 (`0x4843`). Its 32 fields include guest frame, case, native usage, 99th-percentile frame interval in milliseconds, logical reads, publications, cache/failure/culling counters, actor position, collision blocks and completion mask. Percentiles use millisecond bins; overflow uses the observed maximum as a conservative bound. The checker samples rendered frames and saves completed measurements in JSON.

The first accepted Release run reached every case and preserved the checked scene pixels across occlusion. The dense fixture also exercised roughly 98,000 source vertices. These are functional acceptance results, not a 30 FPS claim: moving cases still exceed the frame-time target. The SDK serializes managed execution, so synchronous native reads on its background executor can also delay application progress. Lifetime-safe native I/O outside that execution gate and CPU submission costs remain performance gates.
