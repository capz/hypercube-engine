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
