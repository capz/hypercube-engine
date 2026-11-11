# Progressive texture families

Author `.dnlod` files with the DolphinDotNet streamable texture CLI. Import each
level as a separate native HCP record; the runtime reads metadata first and
validates each independently streamed payload, never an entire DNLT blob.

```sh
python3 tools/build_texture_pack.py --texture terrain/grass=grass.dnlod \
  --texture characters/face=face-low-high.dnlod --output out/materials.hcp
```

This command emits HCPK v2 and a deterministic diagnostic manifest. The existing
validation asset builder still emits byte-compatible HCPK v1. Unknown versions,
flags, reserved fields, duplicate identities, invalid native sizes, malformed
UVs and invalid fallback chains fail validation.

## HCPK v2 contract

The 64-byte little-endian header starts with `HCPK`, version 2, record count,
index offset (64), aligned first payload offset, file length and CRC32 of the
entire metadata region. Word 7 is zero. Words 8–10 contain family count,
absolute family table offset and family table length; words 11–15 are zero.
Limits are 4096 records, 128 families and 64 KiB of family metadata.

The record table retains 64-byte typed records and zero reserved words. Kind 6
is an independent native family level: format is GX 6/14, group is family ID,
mip is the authored logical exponent, width/useful are native width/height,
parent is the next coarser level record ID. Cell/material/variant are zero.
Other record kinds retain their v1 semantics. Physical records are monotonically
ordered, individually CRC checked, and 32-byte aligned; levels within a family
are written coarse first. `Builder.import_textures` and the CLI batch all initial
coarse levels together before finer levels across families. No family payload
must be read to validate its index.

The family table follows the record table immediately. It begins with one
128-byte descriptor per family: ID, DNLT flags (0, 1, 3 or 4), full logical
width/height, level count, level offset relative to this table, zero fallback
policy, ASCII name length, four reserved zero words, and an 80-byte zero-padded
name. Names use letters, digits, `/`, `_`, `-`, `.`. IDs begin at 1. Descriptors
are followed without gaps by each family's logical-order level entries.

Each 32-byte level entry is record ID, logical exponent, four float32 values
(scaleU, scaleV, offsetU, offsetV), and two zero words. Mip dimensions and usable
UV rectangles must agree; sparse exponents remain intact. Low/high flag 4 uses
HIGH 0 and LOW 1 with arbitrary standalone dimensions. Every kind-6 record is
indexed exactly once. Parent references match the ordered family ladder.

## Selection and shared demand

`StreamedMaterialTexture` holds a shared `TextureFamily` and a consumer's policy:
`CoarseOnly`, `FullQuality` or `DistanceMip`. `TextureFootprint.Project` estimates
both UV axes from world bounds, nearest-bound distance, FOV, viewport and repeat
counts. Supply direct projected bounds for irregular or oblique meshes.

The selector uses the finer required axis, configurable bias and a quality cap.
It starts with a 25% hysteresis band and six-frame dwell. These are profiling
defaults, not hardware measurements. Sparse targets select an available level
at least as detailed as requested. Both full-quality and distance policies
target HIGH directly for an authored low/high pair.

`TextureFamilyDemand.BeginFrame`/`Demand` aggregates the finest visible consumer
without duplicating images. Prefetch requests coarse readiness and cannot raise
visible quality. `DemandKnownLod` supports existing cell/geometry decisions while
keeping texture selection independent. `RequestedIndex(family, -1)` always
returns the coarsest authored level before any promotion.

Run `python3 tests/progressive_fixtures.py`,
`python3 -m unittest discover -s tests -p test_progressive_textures.py` and
`dotnet run --project tests/TextureFamilies.Tests -c Release` for format,
corruption, alpha-preserving native import, selection and request-churn checks.

## Streaming and draw ownership

```csharp
using var source = new PackedAssetSource("dvd:/materials.hcp");
using var lods = new TextureLodStreamer(source.Catalog, source.Assets,
                                      budgetBytes: 2 * 1024 * 1024);
var grass = lods.Get("terrain/grass", TextureQualityPolicy.DistanceMip);
var draw = new StreamedMaterialBinding(lods, authoredMaterial, grass);

// Each frame, between GPU submissions:
lods.BeginFrame(frame, completedFrame);
lods.Demand(grass, TextureFootprint.Project(width, height, distance, radius,
                                          fovDegrees, viewportHeight, repeatsU, repeatsV));
lods.Pump(uploadBudgetBytes: 32768, cpuBudgetUs: 1000);
device.BeginFrame(clear);
renderer.Begin3D(camera);
draw.Draw(renderer, meshWithNormalizedUvs, transform, frame);
renderer.End3D();
lods.EndFrame(device.InsertFence());
device.EndFrame();
```

Resolve/pin at actual submission; keep bindings in queued commands and resolve
when executing them. `SortKey()` includes both family identities and resident
generations; refresh it each frame and combine it with effect/blend/material
state. A stale `ResolvedTextureLod` fails `Pin`. The binding copies its shared
template's state into a private material and composes authored animation before
the resident level's usable UV rectangle for both texture slots. The complete
affine matrix preserves rotated animation with unequal U/V gutters.

Geometry uses normalized source UVs. Repeated standalone textures should have
no atlas gutter; a padded atlas rectangle expects coordinates within that
rectangle (split tiled geometry at repeat boundaries). GX repeat wraps the
whole physical image. `TerrainUv` exposes the old tile mapping for normalized
terrain meshes without baking a selected texture level into geometry.

Every family becomes usable at coarse first. An upgrade is queued only after
the coarse image was pinned into a submitted frame. One private destination is
read in bounded native chunks, staged by the SDK service executor, checked by
incremental CRC, and published on the application executor. Cancellation joins
reads, copies and ARAM users before reusing the staging buffer or freeing the
destination. A superseded scheduler generation cannot publish. Budget rejection,
CRC failure and short reads preserve the old image and use scheduler retry
backoff. GPU fences, rather than promotion-time GPU waits, retire old images.

`ActiveBytes`, `UploadBytes`, `ReadBufferBytes`, `RetiredBytes`, `PoolBytes`,
`PhysicalBytes` and `AramBytes` are separate counters. Standalone streams own a
32-KiB native read buffer; a world-integrated stream shares the existing 128-KiB
buffer. The physical cap includes transient destination, retired images and
placeholder. ARAM has its own cap and is not GX-visible memory. `PeakBytes`
records physical texture peaks; counts include the minimum native tile padding.
The byte budget bounds each chunk; the CPU budget bounds issuing more work.
A single native allocation, CRC slice or copy cannot be preempted.

The optional ARAM tier retains complete records that fit a single read chunk;
larger images remain on storage. In-flight stores/fetches cannot be evicted.
`StrictReleaseColdCopies` removes superseded cold copies as well as MEM1 images.
`TrimUnwanted` frees undemanded images after their final fence. Shutdown calls
`Stop()` while continuing application pumping and completed frame fences, then
`Dispose()`; disposal refuses outstanding transfer/GPU ownership. Explicit trim
and final fences reduce unwanted image, read-buffer, placeholder and ARAM bytes
to zero.

## Shared world scheduling

An HCPK v2 `PackedWorld` exposes `WorldStreamer.TextureLods`. Use the world's
`BeginDemand`, `Pump`, `Frame` and `CompletedFrame`, and forward each submitted
frame fence through `WorldStreamer.EndFrame`. Family demand shares the existing
`ResourceScheduler`, `AssetStream`, staging buffer and `AramCache`. Gameplay and
collision can preempt between large texture chunks. Missing visible coarse
images precede upgrades; upgrade ties use projected error before FIFO age.

`WorldStreamer(..., normalizedTextureUvs: true)` opts dynamic terrain geometry
into normalized tile/atlas coordinates; use streamed material mappings at draw
time. Permanent v1 fallback meshes retain their original mapping. The v1-only
fixed-size `TexturePool`, validation path and its historical accounting remain
unchanged. A v2 world's `PhysicalBytes`/`PeakPhysicalBytes` also count permanent
world meshes and textures plus shared staging. Promotions never evict a demanded
family's fallback simply to obtain replacement headroom.

## Validation and SDK availability

The SDK source pin includes the reviewed streamable assets and detached-upload
PRs. Build source checkouts using `.sdk-revision`. Before that SDK revision has
a public NuGet release, `python3 eng/pack.py --sdk-source vendor/sdk --version
0.1.0-preview.local.1` creates an exact-revision SDK dependency feed for isolated
consumer validation. Its manifest marks these as source-preview artifacts;
publication refuses them. Public engine distribution requires publishing the
pinned SDK and updating `DolphinDotNetPackageVersion` first.

`python3 tools/test_progressive_runtime.py` executes standalone and mixed-world
scenarios in Debug and Release through actual SDK AOT, native storage/graphics,
ARAM and GC stress. It covers coarse presentation, CRC/short-read failures,
generation churn, both material slots, pressure rejection, physical accounting,
collision preemption during >128-KiB upgrades, and complete shutdown. The
standalone path permits only five resource-cleanup waits and the AOT device
shutdown wait, with none during texture replacement.

`tools/build_progressive.sh` builds the PowerPC fixture and data ISO.
`python3 tools/progressive_validation.py Game.dol --disc textures.iso --logs
out/fresh-capture --seconds 60` checks independent coarse/fine pixel patches,
rotated gutter UVs in both texture slots, request reversal, live DSP audio,
failure/retirement counters, and zero bytes after final trim. It records p95/p99
frame times and readiness latency as emulator observations, with an explicit
`hardwarePerformanceClaim: false` marker. Real GameCube SD2SP2/disc/audio pressure
and latency gates still require physical-console captures.
