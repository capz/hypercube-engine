# Hypercube Engine — Technical Design

Status: rendering design **proposed**; the [streaming validation foundation](streaming-validation.md) is implemented for Dolphin and hardware evaluation. Production choices await representative-content GameCube measurements. Target: retail Nintendo GameCube, 24 MiB MEM1, 16 MiB ARAM; baseline 30 FPS. Inspired by Fox Engine's coherent material authoring, world streaming, and baked lighting, **not** its deferred rendering implementation.

## Architecture

- GX/TEV hybrid forward renderer, CPU visibility, material-sorted geometry submission.
- Sparse virtual textures for unique static scenery; conventional textures for characters, props, foliage and UI.
- Offline tiling and hierarchical render patches rather than shader-based page-table lookup.
- Region/cell streaming (nominal 32 m urban, 64–128 m outdoor), separate gameplay/collision and visual readiness.
- Fixed set of GX-compatible material permutations approximating authored albedo, roughness, specular, AO, normal detail, emissive and baked lightmaps.
- Baked indirect/static lighting with limited vertex/dynamic lights and selectively projected contact shadows.
- Single asynchronous resource scheduler across storage, ARAM and MEM1; no render-thread waiting on missing detail pages.

See [texture experiments](texture-strategy.md) and [memory and development plan](memory-and-validation.md).

## References

[Lambert N64 megatexture proof](https://github.com/lambertjamesd/n64brew2023), [libogc](https://github.com/devkitPro/libogc), [GX graphics documentation](https://www.davidgf.net/downloads/gcwii/gx.pdf), [GameCube architecture](https://www.copetti.org/writings/consoles/gamecube/).

All performance estimates and memory allocations are starting hypotheses until profiled on console.
