# Progressive texture LOD streaming — technical design

**Status:** Proposed architecture. This document is a design change only; the implementation is planned as separate, independently testable DolphinDotNet and Hypercube PRs.

**Targets:** retail GameCube, DolphinDotNet managed/native SDK, Hypercube's shared asset scheduler. **LOD convention:** level 0 is the highest resolution; larger indices are coarser.

## Objectives and semantics

1. First presentation uses the coarsest *usable* texture level (or an existing shared placeholder until that first level arrives).
2. A material/object opting into distance-dependent texture LOD requests the level appropriate for its projected texel density. Asynchronous promotion may skip intermediate levels.
3. A texture with only authored **low** and **high** versions uses low for first readiness and targets high once full quality is enabled. No synthetic intermediate levels are required.
4. After a successful replacement, the superseded single-level GX texture is retired and **freed after all GPU readers finish**. No entire mip pyramid is kept in MEM1 by default.
5. Failed, canceled, late and memory-rejected promotions leave the previous visible texture intact.
6. Share texture-family residency across objects; select the *finest* requested quality among visible consumers, subject to memory pressure and explicit quality limits.
7. Preserve compatibility with conventional DNTX v1/v2 textures, full resident hardware-mipmap chains, and existing HCPK v1 validation assets.

**Non-goal:** arbitrary sparse hardware mip maps in a single GXTexObj. On GX, mip data is laid out contiguously and the GPU derives subsequent LOD addresses from the base address; individual levels cannot safely be freed while the same mipmapped GX texture remains active. The default streaming mode therefore binds a **single resident image as a non-mipmapped GX texture**, normally with linear filtering. Native trilinear remains a separate optional mode that loads and retains an indivisible contiguous chain.

## Current baseline — verified in repositories

- DolphinDotNet already generates complete DNTX v2 RGBA8 mip chains using `dnd-assets texture input.png output.dndtex --mipmaps`. It supports runtime DNTX v2 loading, explicit samplers and whole-chain replacement. See `tools/DolphinDotNet.Assets/DrawingPackages.cs`, `tools/DolphinDotNet.Assets/Program.cs`, `src/DolphinDotNet.GameCube/Dolphin/Graphics/Texture2D.cs` and `docs/graphics-phase-two.md`.
- DolphinDotNet's `StreamingTexture` stages CPU-only native texels and publishes without a GPU wait, but holds **two fixed-dimension allocations**. It is not a variable-resolution texture slot.
- Hypercube's `tools/hypercube/pack.py` already creates independent GX-native CMPR/RGBA8 LOD records with parent IDs and gutters. `AssetCatalog` validates them, `ResourceScheduler` manages priority, cancellation, generations, pins and eviction, `WorldStreamer` reads and publishes them, and `AramCache` can retain cold records.
- Hypercube's `TexturePool` charges two copies per texture and reuses only matching widths/formats. Its `WorldStreamer` has a 128-KiB native staging buffer and rejects a required batch above 131,040 bytes; larger standalone texture levels require **chunked publication**.
- Hypercube's terrain `PackedWorld.DecodeMesh` bakes tile gutter/width into geometry UV coordinates for a selected LOD. Rebinding a differently sized texture without per-level UV correction would be incorrect.
- SDK integration is pinned by `.sdk-revision`. The SDK implementation must land and be validated before updating the engine's pin.

## Architecture and ownership

| Layer | Responsibility | Owns |
|---|---|---|
| DolphinDotNet.Assets | Offline mip generation, CMPR/RGBA8 encoding, low/high packaging, per-level checksums, validation | Streamable texture authoring |
| DolphinDotNet.Core | Read-only streamable descriptor parser, level offsets, width/height, UV mapping, source metadata | Format knowledge, no GPU resources |
| DolphinDotNet.GameCube | Detached single-allocation chunk uploads, publishing, texture-cache coherence, native memory accounting | GX texture allocation/upload primitives |
| Hypercube.Core | Logical texture families, per-object policies, screen-space LOD choice, hysteresis, generation and demand | Pure decision logic |
| Hypercube.GameCube | ResourceScheduler/AssetStream/ARAM coordination, active-material resolution, old-texture retirement | Physical residency and GPU-safe lifecycle |
| Application | Opt into streaming or full-quality, supply object bounds and UV density | Usage intent |

The **stable logical texture handle** is shared by all material instances. Do not store a long-lived changing Texture2D token inside geometry, and do not instantiate one copy per object unless the imagery itself differs.

### Suggested material-facing API (Hypercube)

~~~csharp
var grass = textureLods.Get("terrain/grass");

// Object/material policy; not a GX sampler flag.
var binding = new StreamedMaterialTexture(
    grass,
    TextureQualityPolicy.DistanceMip);

// Frame preparation: request a target without waiting.
textureLods.Demand(binding, camera, worldBounds, uvRepeats,
                   ResourcePriority.Visible);
textureLods.Pump(uploadBudgetBytes: 32768, cpuBudgetUs: 1000);

// At actual draw submission: resolve current resident fallback, plus UV transform.
var resolved = textureLods.Resolve(binding);
material.Texture = resolved.Texture;
material.TextureTransform = Combine(resolved.UvTransform, authoredUvTransform);
renderer.DrawMesh(mesh, material, transform);
textureLods.Pin(resolved, submittedFrame);

// When the frame's GPU fence completes, free retired textures.
textureLods.Collect(completedFrame);
~~~

This is **illustrative C#**, not an exact committed signature. Provide a simpler overload for world-cell call sites that already know the selected geometry LOD.

~~~csharp
public enum TextureQualityPolicy
{
    CoarseOnly,        // No promotion; useful for bootstrap and memory pressure.
    FullQuality,       // Low/high pair -> high; mip chain -> finest.
    DistanceMip        // Per-object projected-texel choice if authored mips exist.
}
~~~

For a low/high-only pair, `DistanceMip` behaves as `FullQuality` after coarse readiness, matching the requested behavior. An explicit forced downgrade under memory pressure is permitted; the reported target remains high.

~~~csharp
public readonly struct ResolvedTextureLod
{
    public readonly Texture2D Texture;
    public readonly int LogicalLevel;
    public readonly UvTransform UvTransform;
    // Also provides a generation-checked pin/lease, not merely a raw token.
}
~~~

Prefer `StreamedMaterialTexture` over extending DolphinDotNet's `Material` with engine-specific streaming state. Resolve immediately before a draw; account for shared materials, two-texture TEV materials, atlas tiling and material sort keys.

### Distance/visibility selection

Use a *projected texel footprint* rather than hard-coded distance thresholds. From the camera FOV, viewport height, object bounds and authored UV repeat scale, estimate how many screen pixels one whole texture repeat occupies:

~~~text
pixelsPerRepeat ~= worldUnitsPerRepeat
                   * viewportHeight / (2 * tan(verticalFov / 2))
                   / max(viewDistance, nearDistance)

targetLod ~= clamp(round(log2(fullResolution / max(1, pixelsPerRepeat)))
                   + textureBias, 0, maxLod)
~~~

For sloped, oblique or irregular meshes the above is an estimate, not a replacement for hardware texture derivatives. Supply better measured UV density per mesh/material at build time; the current fixed terrain-cell world mapping is a useful first implementation. Consider both U/V dimensions and use the finer required axis for a conservative selection. Use screen-space projected bounds for nonuniformly scaled objects.

Add hysteresis at promotion/demotion thresholds (suggest initial 20–30% band), minimum dwell (suggest 4–8 frames), prefetch only for sufficiently likely visible objects, and demand no higher than the finest visible consumer. Keep hard caps for frame CPU, storage, ARAM and native bytes. All numeric thresholds are **profiling starting values**, not hardware measurements.

## Asset format

### Keep DNTX v1/v2 unchanged

DNTX v2 is still appropriate for a *fully resident* GX mip pyramid and remains readable with `Texture2D.FromPackage`. Do not change its existing on-disk format or silently treat it as independently reclaimable levels.

### Add streamable DNLT v1 alongside DNTX

Use a new format/extension such as `.dnlod` to avoid a breaking change in the native DNTX loader. The SDK asset tool emits one indexed package with independently addressable and 32-byte-aligned payloads.

Suggested binary layout:

| Header (64 bytes; little-endian) | Field |
|---|---|
| 0..3 | `DNLT` magic |
| 4..7 | Version = 1 |
| 8..11 | Flags: mip chain, sparse levels, low/high-only pair |
| 12..19 | Logical full-resolution width, height |
| 20..23 | Default GX texture format |
| 24..27 | Number of indexed levels |
| 28..31 | Index of initial coarse entry |
| 32..35 | Index-table offset |
| 36..39 | First data offset |
| 40..43 | Declared file length |
| 44..47 | Index-table CRC32 |
| 48..63 | Zero-reserved |

Each 32-byte entry contains `logicalLod (u32)`, `width (u16)`, `height (u16)`, `GX format (u32)`, `flags (u32)`, `aligned byteOffset (u32)`, `padded byteLength (u32)`, `CRC32 (u32)` and `reserved (u32)`.

- Logical LOD 0 is finest, even when only sparse levels were supplied. Low/high pair mode uses its own explicit semantic flag and entries HIGH (0), LOW (1), not artificial missing intermediate mips.
- Individual data payloads are tiled and padded exactly as native GX expects. Validate native format size as well as header length/CRC; never trust an offset from an unvalidated index.
- The tool writes **coarse payload first in physical file order** for fast initial reads; table order remains logical. All offsets are relative to the containing file/asset record.
- Store independent per-level `uvScale`/`uvOffset` and optional gutter/usable-area metadata. These values are needed to keep authored UVs stable across tile sizes/atlas padding.
- For complete authored mip chains, require power-of-two logical dimensions with actual half-size progression; sparse selections preserve the original logical LOD exponent.
- A two-quality pair can have arbitrary non-zero dimensions allowed by the native standalone GX path.
- Enforce 1024-texel SDK dimension limit, 32-byte alignment, single-destination and total-memory bounds. CMPR uses 8x8 padded tiles, RGBA8 4x4 padded tiles. Small logical mips still consume at least a padded tile.
- Prefer CMPR for suitable opaque/1-bit-alpha color data, RGBA8 (or explicit supported alpha-capable native formats later) for smooth alpha. Do not silently turn semitransparent pixels into opaque CMPR content. Preserve alpha coverage where relevant and use premultiplied-aware downsampling for transparent images.
- Include a deterministic manifest/report for diagnostics (level resolutions, byte sizes, source hashes, alpha/format decisions).

Illustrative CLI:

~~~sh
dnd-assets texture grass.png grass.dnlod --streamable --mipmaps --format cmpr
dnd-assets texture grass.png grass.dnlod --streamable --mipmaps --format rgba8 --min-size 8
dnd-assets texture --low grass_low.png --high grass_high.png \
  grass.dnlod --streamable --quality-pair
~~~

Keep the existing `dnd-assets texture grass.png grass.dndtex --mipmaps` behavior unchanged. Add equivalent model-import support once the standalone texture asset pipeline is validated; avoid forcing all imported textures into a full resident chain.

### Hypercube HCP integration

The engine currently has HCPK v1 with 64-byte typed records, a fixed `AssetCatalog.Texture(format, group, mip, cell)` grid and parent/fallback IDs. Preserve that for validation workloads.

Introduce **HCPK v2** only when importing general streamable texture families:

- A logical family descriptor maps stable texture ID -> ordered/sparse independently addressed level records, quality-pair flag, fallback policy and per-level UV mapping.
- Each native texture level remains a separate CRC-checked 32-byte-aligned pack record. Do not embed one opaque multi-megabyte DNLT blob that requires reading the whole blob to validate it.
- Reuse HCPK's existing native record, generation/parent-chain and batch-read concepts; add a generic level-family lookup rather than extending the fixed 576-slot terrain grid to arbitrary objects.
- Parse v1 and v2 distinctly; reject unknown versions/flags; never reinterpret v1 reserved words in place.
- Preserve physical locality among coarse data and among likely together-requested levels, while preventing large promotion records from blocking priority gameplay/collision reads.
- Do not make mesh LOD and texture LOD obligatorily equal. Terrain geometry may be coarse while its texture resolution is independently selected; very small scene patches may need a texture-quality cap.

## SDK runtime work: variable-size upload without a whole-image staging copy

Existing `StreamingTexture` is ideal for repeated same-size pages but allocates both front and back textures permanently. Keep it unchanged for that use case. New mip-resolution swaps need one active allocation plus at most one **temporarily detached** destination. Add a safe SDK facility, named illustratively:

~~~csharp
using var upload = DetachedTextureUpload.Create(
    width, height, format, expectedNativeBytes);

// The engine reads the asset in bounded (e.g. 16/32 KiB) native chunks.
// Source must be held until each copy returns.
await upload.StageChunkAsync(nativeStaging, sourceOffset,
                             destinationOffset, chunkBytes, cancellationToken);

// After every chunk and the entire record checksum are verified:
upload.Seal();
if (upload.TryPublish(out Texture2D newTexture))
    textureLods.ReplaceResident(family, logicalLod, newTexture);
~~~

Required implementation pieces in DolphinDotNet:

1. A native `TextureStageBufferRange` operation that copies from a held native buffer to a **detached** Texture2D at a checked 32-byte-aligned destination range. It performs CPU copy/cache flush only; no draw/GX command and no global GPU completion wait.
2. A managed `DetachedTextureUpload` owner that creates the single target on the application executor, tracks completed ranges, forbids overlap/missing ranges at publication, retains source lifetime through cancellation and has an explicit safe abort/dispose path.
3. Reuse existing `TexturePublishStaged` for application-thread cache invalidation and publication after checksum validation. The texture is never visible before completion.
4. Expose physical byte counts for staged/current/retired allocations; do not confuse one logical resident level with total live native bytes.
5. Transfer ownership of the published immutable `Texture2D` to Hypercube, which already owns GPU frame pins, generation checks and eviction. The engine alone decides when older resources are safe to release.

This avoids a permanent 2x cost and allows larger texture levels without allocating a 256-KiB/1-MiB managed staging array. The destination allocation naturally still costs its full native texture bytes.

All API calls touching GX publication/draw state execute on the application executor. Background worker code only reads/copies into provably detached memory. A cancelled native copy must finish or be safely joined before its destination and source storage can be reused.

## Hypercube residency state machine

~~~text
Unrequested -> CoarseQueued -> CoarseResident
                  |                  |
                  v                  v
               Read/CRC        TargetUpgradeQueued
                                  |
                     ReadChunks -> StageChunks -> ValidateCRC
                                  |
                                  v
                         ReadyForPublication
                            /           \
                     stale/fail       valid
                         |              |
                  discard target    publish new level
                                       |
                                  retire old level
                                       |
                                GPU fence complete
                                       |
                                dispose old texture
~~~

Use existing `ResourceScheduler` IDs/priorities/generations and `WorldStreamer` progress. On camera reversal, supersede the queued generation; draining an already started transfer is mandatory before recycling its storage. The active fallback is never evicted *just to start* a promotion; if temporary replacement headroom does not exist, defer promotion. A shared placeholder can remain permanent for cold starts, but each demanded family should become usable after its coarsest level arrives.

Scheduling order:

1. Gameplay/collision/audio-critical work.
2. Missing visible coarse textures and permanent world fallback.
3. Visible object upgrades, ordered by projected pixel error and importance.
4. Likely next-visible prefetch.
5. Optional/cold quality.

A resident family normally has exactly **one** MEM1 texture level, plus an optional transient destination and GPU-retiring old allocation. Multiple levels are not retained automatically. The optional ARAM cold cache is a **separate** counted residency tier: if the requirement is strict release of all copies, evict the unused level from ARAM as well; otherwise it can remain in ARAM to accelerate camera reversal. Keep packed bytes on disc/SD as the source of truth.

Handle low/high-only families as a two-state quality ladder. If mip selection is enabled but no authored intermediate levels exist, target HIGH directly. Under transient pressure LOW remains a valid fallback.

### UV and model handling

Do not reuse geometry UVs computed for a specific texture-record gutter/width. Author source-normalized mesh UVs, and apply the level's own gutter/usable-area transform through DolphinDotNet's existing `Material.TextureTransform` / `SecondaryTextureTransform` fields. Compose that static transform with scrolling/animation UV transforms at draw time. This applies to both ordinary objects and existing tile/atlas draw paths. Do not rebuild geometry solely to promote texture resolution.

### Memory accounting and example

GX textures live in **MEM1**; TMEM is the graphics texture cache, not a freely managed external VRAM allocation. ARAM requires DMA to MEM1 before GX use.

For a 256x256 texture, ignoring allocator overhead and small-level tile padding:

| Texture | CMPR | RGBA8 |
|---|---:|---:|
| 256x256 | 32 KiB | 256 KiB |
| 128x128 | 8 KiB | 64 KiB |
| 64x64 | 2 KiB | 16 KiB |
| 32x32 | 0.5 KiB | 4 KiB |

A full pyramid adds roughly one third above mip 0, whereas keeping only 64x64 instead of 256x256 saves 15/16 of that level's memory. Promotion temporarily needs both old and new plus bounded native staging; count it in the physical peak. Actual smaller levels round up to GX tile sizes. Log `activeBytes` , `stagingBytes` , `retiredBytes` , `poolBytes` and `ARAMBytes` separately.

## Work plan / PR boundaries

**SDK PR 1 — offline assets and header-only parser**
- Add streamable DNLT v1 authoring, deterministic mip filtering/CMPR selection and low/high pairs.
- Add descriptor parser, per-level native size/CRC/offset bounds and tools documentation.
- Add host tests: 1x1/rectangular mip sizes, non-power-of-two quality pair, sparse levels, transparent alpha, tiles/gutters, fuzz/corrupt headers and backward-compatible DNTX.

**SDK PR 2 — detached chunked uploads**
- New checked native range-stage operation and managed owner; CPU staging must not issue GX commands.
- Native tests for source holds, cancellation/draining, partial/duplicate/out-of-range chunks, 32-byte alignment, CRC failure, GPU-safe disposal, publish timing and native bytes.
- Real-console/Dolphin fixture verifying pixel consistency and no rendering stalls / memory leaks.

**Hypercube PR 1 — generic texture families**
- Extend the asset packer/catalog with a version-gated generic family index; keep v1 and its validation tests.
- Pure screen-density LOD policy with hysteresis, bounds/repeats data and low/high semantics.
- Tests for shared consumers, sparse levels, initial coarse, request churn and parent fallback.

**Hypercube PR 2 — streaming integration**
- Reuse ResourceScheduler, WorldStreamer, AssetStream and ARAM; introduce a dynamic-size single-resident texture slot next to the existing fixed-size TexturePool.
- Attach texture handles and composed UV transforms to material draws. Publish only fully validated generations; retire after GPU fence.
- Expose budget, coarse/fine readiness, bytes/cancel/stale/retire telemetry and a stress/inspection fixture.
- Move `.sdk-revision` only after both SDK PRs are merged/available and the pinned GameCube AOT build passes.

**Hypercube PR 3 — production polish (conditional on console results)**
- Prefetch heuristics, dynamic byte budgets and ARAM policy; optional limited resident GX mip tail for trilinear.
- Model importer sidecars and generated material bindings.
- Compare fine pages vs cell atlas under matched quality and physical-memory ceilings.

## Acceptance gates

- Low/coarsest is shown before any costly upgrade; no holes or dangling/stale texture references on rapid camera reversal.
- No visible flash, UV jump or corrupt pixel after LOD replacement; compare screenshot pixel patches across RGBA8/CMPR, UV gutters and multi-material meshes.
- No frame-time blocking GPU wait in steady-state promotion path, and no synchronous whole-texture file read.
- Every allocated stage, old level, source buffer and optional ARAM copy is accounted; after the last GPU fence and explicit trim, unwanted level allocations drop to zero.
- A corrupted payload or canceled read never publishes; a low/high-only family becomes high after available/budget permits.
- Validate on real GameCube with SD2SP2 and disc assets, audio active, moving/turning cameras, multiple simultaneous objects, pressure budgets, p95/p99 frame times, low-ready latency and detail-ready latency. Emulator-only timing is not a hardware-performance claim.
- Keep all existing DNTX, StreamingTexture and Hypercube v1 validation tests green.

## Engineering decision

**Recommended first delivery:** single-level-per-family streaming with one temporary detached upload and retired-by-fence disposal. Reuse the engine's mature priority/generation/fallback mechanisms. The extra SDK capability is **safe chunked uploads to a new, nonresident texture allocation**. Do not attempt to make GX interpret a fragmented mip pyramid, and do not replace the current fixed-size StreamingTexture API or material sampler APIs.