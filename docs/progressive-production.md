# Progressive texture production controls and acceptance

This completes the implementation lanes in the PR11 design. **Production tuning
and the page/atlas strategy decision remain conditional on real-console results.**
The checked-in phase-four measurements are Dolphin correctness evidence. No
physical GameCube measurements are claimed, and adaptive/prefetch/tail behavior
is opt-in. DNTX, fixed-size StreamingTexture and HCPK v1 retain their existing
paths. See [progressive-textures.md](progressive-textures.md) for the base mode.

## Budget feedback, prefetch and cold copies

`TextureBudgetController.Next(frameCpuUs, lastPumpUs, lastReadUs, audioStarved,
criticalQueued)` returns a bounded `TextureWorkBudget`. Measure CPU work **before
present**, excluding GX/VI waits; use measured read completion latency, not read
submission time. The starting 32 KiB maximum / 4 KiB minimum chunks, 1 ms pump
allowance and 8 ms CPU/storage soft limits are configurable experiment inputs.
Slow work halves issuance, sixteen stable frames grow it, and audio/gameplay or
insufficient CPU headroom pauses new issuance. `TextureLodStreamer.Pump(budget)`
still drains already-started ownership when issuance is paused. `LastPumpUs` and
the controller's pause/reduction/growth counters support profiling. A deadline
cannot interrupt a native operation already in progress.

`Prefetch(binding, hint, budget)` admits coarse readiness only if probability,
time-to-visible, camera-direction agreement, projected size, CPU and physical
headroom are sufficient. Seen-recently lowers the probability/time threshold for
reversal. Visible demand always wins; prefetch never raises visible quality.
Supply visibility/motion evidence from the application's scene, rather than
prefetching every catalog entry. Both features are off unless called explicitly.

`SetColdRetention(Disabled|ReversalCache|StrictRelease)` distinguishes counted
ARAM reuse from strict removal of unused levels. Initialize ARAM before enabling
it. Disable drains an in-flight protected copy before dropping it; strict mode
drops old copies when their residency is released. For a shared `WorldStreamer`,
call **WorldStreamer.SetColdRetention**, which applies the change between legacy
batches, so queued ARAM fetches cannot lose their source. Standalone streams use
their own setter. Explicit stop, final GPU fence and trim are still required for
zero-allocation shutdown. ARAM bytes are reported separately from MEM1.

## Model sidecars and generated bindings

With the SDK pinned by this branch:

```sh
dotnet run --project "$DOLPHIN_DOTNET_ROOT/tools/DolphinDotNet.Assets" -- \
  model actor.glb actor.dndmodel --streamable-textures --compile-gx-materials
python3 tools/build_texture_pack.py \
  --model-sidecar actor.dndmodel.textures.json \
  --model-sidecar props.dndmodel.textures.json \
  --output out/models.hcp --bindings Generated/ModelMaterials.g.cs
```

The packer validates the model SHA256, DNDM section hashes, sidecar material IDs,
paths, image hashes, DNLT CRCs, samplers and combiners. Relative paths must remain
under the sidecar directory. Identical image bytes across materials/models share
one family. All coarse records precede all upgrades. Geometry, skinning and
animations stay in the model package; no resident model texture chain is hidden
in the pack. The generated `Create0`, `Create1`, etc. factories map to input order
and are recorded in the pack manifest. Generated code replaces runtime JSON
parsing and returns `StreamedModelMaterials` ownership; dispose it after its
final draw frame to release its shared multiply/add TEV programs.

Load the model through the SDK, create an instance, then call `DemandPart` only
for visible material groups before pumping. `DrawPart` resolves and pins both
texture slots at actual submission through `DrawModelMaterialPart`. Queue the
binding plus material index, not a resolved Texture2D. Separate instances may
have independent policies while sharing family allocations. Imported authored
UV transforms are already baked into the geometry once; selected-level gutter
transforms compose at draw time for both slots.

Offline UV density uses triangle derivatives in the model's **rest pose**,
including imported node transforms and authored UV repeats. Supply maximum
application scale times a conservative animation/deformation multiplier to
`DemandPart`; use direct projected footprints for deforming or oblique cases.
Degenerate UVs use its explicit fallback footprint. Submit opaque parts before
transparent parts and sort transparent parts in the application queue. The SDK
sorts primitives within each material group, not across separate calls.

## Optional resident hardware mip tail

```sh
python3 tools/build_mip_tail.py texture.dnlod --start-level 2 \
  --output tail.dndtex --maximum-native-bytes 65536 --maximum-dimension 128
```

This mode is separate from the single-image family: it creates **one contiguous,
indivisible full chain** through 1×1, so GX can use trilinear filtering. Sparse,
NPOT pair, incomplete, mixed-format and padded atlas/gutter tails are rejected.
The default upper limit is 128 pixels / 64 KiB native; optional hard maxima are
256 pixels / 256 KiB. The manifest includes the whole-package CRC32.

`ResidentMipTail.LoadAsync(path, manifestCrc, borrowedNativeBuffer, logicalBase)`
checks only the 32-byte header synchronously, then reads bounded native chunks,
validates the entire package and publishes on the application executor. Reserve
exclusive use/lifetime of the supplied staging buffer until the task completes;
it is not disposed by the loader. This optional, capped mode uses a bounded
managed package array for the existing SDK DNTX factory. The ordinary family
upload path still has no whole-image managed staging.

Reserve the **entire** new chain, staging and any old retiring chain before
loading. `Apply(material)` enables trilinear filtering. It does not update family
residency or automatically replace an old texture. `RetireAfter(finalFence)` and
`TryCollect()` release the chain without promotion-time GPU waiting. Account its
ActiveBytes/RetiredBytes alongside the application's other native allocations;
individual tail levels cannot be reclaimed. No tail is enabled by default.

## Matched page/atlas experiment

`python3 tools/build_texture_comparison.py --output out/texture-comparison`
authors paired fine-page and 2×2 cell-atlas HCPK v2 packs from the same 192×128
validation image, with identical useful RGBA8 texels at logical levels 0/1/2,
four-texel gutters and normalized geometry mappings. `--format cmpr` is a
separate equal-format lane. The manifest includes source/camera/pack hashes,
quad mappings, coarse/native headroom and a common physical ceiling. Python
checks compare **every useful texel at every authored level** in the RGBA8 pair.
The common camera trace includes movement and turns. This authors experiment
inputs; it does not simulate throughput or choose a winner.

Render each manifest's quads with the same shader, sampler, viewport, camera
trace, audio and object/animation load. Use the same physical ceiling, charging
active images, a detached target, source buffer, retired images, idle pool and
external scene resources. Choose a ceiling large enough for the complete
coarse set plus replacement/retirement/staging headroom. The builder checks that
minimum; the runtime must enforce the ceiling including scene resources.

Capture a common deterministic frame from both strategies and a full-detail
reference. Admit comparisons with:

```sh
python3 tools/progressive_acceptance.py --output comparison.json compare \
  pages-run.json atlas-run.json --reference reference.png \
  --page-image pages.png --atlas-image atlas.png --maximum-rmse 2
```

Admission rejects differing content, camera, shader, sampler, viewport, source
texel density, native format, SDK or physical ceiling. Both captures must be the
same camera frame and meet the reference RMSE threshold (RGB 0..255 units).
Report P95/P99 frame times, P95 coarse/detail readiness, physical/ARAM peaks,
storage/ARAM traffic, draws and texture binds together. A lower-detail or
higher-memory result cannot establish a faster strategy. Repeat runs and
compare variation before choosing defaults. Report hashes bind input runs and
captures; retain those raw artifacts with the report.

## Version 1 measured-run contract and hardware gate

A run JSON contains `schema:1`, a unique `runId`, `platform` (`Dolphin` or
`GameCube`), `storage` (`sd2sp2` or `disc`), `strategy` (`fine-pages` or
`cell-atlas`), and physical-console/video identification in `console` for
hardware. Include 64-character lowercase SHA256 fields `contentSha256`,
`sceneSha256`, `cameraTraceSha256`, `shaderSha256`, `samplerSha256`, `dolSha256`,
`packSha256`; a 40-character `sdkRevision`; `width`, `height`, `format`
(`RGBA8`/`CMPR`), `texelsPerWorldUnit`, `physicalCeilingBytes`, and `captureFrame`.

Record at least 300 measured `frames`, each with `frameUs`, `audioActive:true`,
and a `memory` object. Its fields are `activeBytes`, `uploadBytes` (the entire
private target allocation, not just bytes already copied), `readBufferBytes`,
`retiredBytes`, `poolBytes`, `externalBytes`, `physicalBytes` (sum of those six),
and separate `aramBytes`. Record `finalMemory` with the same fields all zero
**after** final fence/trim/shutdown. Include at least four observations each in
`coarseReadyUs` and `detailReadyUs`, and measured totals `storageBytes`,
`aramReadBytes`, `draws`, `textureBinds`, `cancellations`, `checksumFailures`,
`pressureDeferrals`. The last three must be exercised rather than left zero.

`stressCases` must include `camera-turn`, `multiple-objects`, `budget-pressure`,
`camera-reversal`, `corrupt-read`, `cancel-read`. Supply observed evidence in
`checks`, all true: `coarseBeforeDetail`, `noHoles`, `noStaleDraws`, `noUvJumps`,
`noCorruptPixels`, `corruptNeverPublished`, `cancelNeverPublished`,
`pairReachedHigh`, `noPromotionGpuWait`, `noSynchronousWholeTextureRead`.
These are operator/application observations: the validator cannot establish
physical hardware provenance or visible correctness from a self-reported JSON.
Retain video, pixel patches, instrumentation and source/DOL hashes for review.
Never substitute the unit-test contract data for a measured run.

Run three independent Release repeats per strategy on **both** real GameCube
SD2SP2 and disc (12 runs), with audio active, multiple objects/animations,
camera reversal and budget pressure. Produce six accepted screenshot
comparisons, one for each matched repeat pair. Then invoke:

```sh
python3 tools/progressive_acceptance.py --output hardware.json hardware \
  measured/*.run.json --comparison sd-pair-1.json --comparison sd-pair-2.json \
  --comparison sd-pair-3.json --comparison disc-pair-1.json \
  --comparison disc-pair-2.json --comparison disc-pair-3.json \
  --maximum-frame99-us 33334 --maximum-coarse95-us 500000 \
  --maximum-detail95-us 2000000
```

These example thresholds are experiment targets, not achieved measurements.
Specify application readiness thresholds before collecting results. The gate
rejects emulator timing, changed workloads, incomplete repeats, missing matched
quality evidence, accounting leaks and failed correctness/performance checks.
Physical-console access is required to complete this gate; it remains pending
in this delivery.
