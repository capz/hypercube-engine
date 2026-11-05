# Shared rendering services

These APIs extract reusable mechanisms from AnimalTown without depending on its
terrain IDs, saves, weather state, tree shapes, cylindrical projection or HUD.
They are single-render-thread services built on the pinned DolphinDotNet SDK.
AnimalTown's submodule and application code are not changed by this engine PR.

| Area | Portable core | GameCube adapter |
| --- | --- | --- |
| Opaque submission | `OpaqueDrawQueue`, value `DrawPose` snapshots | `MeshBatch` |
| Dynamic geometry | `BufferGrowth` | `DynamicMeshBuffer` |
| Material animation | `MaterialMotion`, periodic `CloudMask` | `ScrollingTextureLayer`, `CloudShadowLayer` |
| Visibility | configurable, reusable `CameraFrustum` | caller supplies camera-compatible parameters |
| Shared resources | `ResourceLeases` | `SharedGraphicsResources` |
| Profiling | `RenderCounters`, existing `FrameProfile.Rendering` | `RenderTelemetry.Capture` |

## Opaque batches

Construct `MeshBatch(capacity, meshCapacity, materialCapacity)` outside frames.
Capacity is 1..128, matching the SDK. Construct the submitting renderer as
`new Renderer(device, batch.Capacity)` so its bulk staging capacity is sufficient.
Register borrowed meshes and materials. Material properties are copied into
preallocated material objects; registrations/updates require an empty queue.
`TryAdd` takes value position/quaternion/scale/tint snapshots. It returns false
without changing the queue on overflow; flush and retry or use a smaller window.
All transforms must already be in a common coordinate space. Arbitrary sheared
world matrices are not represented by TRS snapshots.

`Flush` groups compatible material IDs and tints by first appearance, preserving
order within each group. It consumes the queue even if submission throws, so
partially submitted groups are not replayed accidentally. Borrowed meshes,
textures and effect uniforms must remain valid/unchanged through flush. Effect
uniforms are shared GPU resources, not cloned by a material snapshot.

Only opaque, depth-tested and depth-writing materials are accepted. Keep alpha
blending, order-dependent coplanar overlays and custom painter order outside this
queue. The optimization reduces managed/native API crossings and repeated state
setup. It does **not** promise one GX primitive per material group: distinct
transforms and the SDK's native vertex staging can produce multiple submissions.
Logical `BatchCalls`/`MaterialGroups` are separate from measured SDK draw counts.

## Dynamic geometry

`DynamicMeshBuffer(maximumVertices)` owns one reusable CPU vertex array and one
retained dynamic mesh. The ceiling and vertex counts are triangle-aligned.
`Reserve(required)` grows geometrically up to the ceiling and preserves committed
content. Always use the returned array after growth. `Commit(count, revision)`
uploads only when the content revision or used count changes. The caller must
advance the revision for changed vertices; edits alone are not detected.

Unused capacity is padded with degenerate triangles, so it does not expose old
geometry when the used prefix shrinks. A zero count produces no mesh to submit.
Capacity is retained for later reuse. SDK uploads cover capacity, not only the
used prefix; native allocation/vertex telemetry can therefore exceed the logical
used count. Growth briefly retains old and replacement native meshes, allowing
creation failure to leave the old mesh usable. Budget that peak as well as the
maximum CPU buffer. Upload, growth and disposal belong between frames with prior
GPU use completed. `BufferAllocations` counts CPU buffer allocations; it is not a
measurement of the SDK heap.

## Materials and cloud shadows

`ScrollingTextureLayer` controls either texture slot using independent wrapped
UV translations. Speeds are UV repeats/second; period wrapping stays in [0,1),
including tiny negative offsets and long sessions. Configure samplers before
frames so the SDK's material-options arrays are allocated before animation.

`CloudShadowLayer(metresPerUvRepeat, worldPeriod, metresPerSecond)` owns one shared
64x64 I8 mask (4 KiB) and one four-stage TEV effect. Reuse one layer across all
receiving materials. UV0 must map linearly to logical world XZ; world/cylinder
projection remains application policy. For AnimalTown's UV0 = world XZ * .5,
pass metresPerUvRepeat=2. Call `Configure` once with explicit primary addressing
and filtering: introducing secondary sampling must not silently clamp a repeating
base texture. Then animate once and apply to each material with its original
base tint. Applying tint always starts from that supplied baseline, avoiding drift.

The partly cloudy preset modulates the existing draw with no added geometry,
draw pass or per-frame texture uploads. Cover 0 and 1 bypass the cloud shader and
secondary texture, retaining only the applicable ambient tint. `enabled=false`
restores original tint and clears the cloud effect/secondary transform for indoor
materials. It is an exclusive material layer: unrelated effects, occupied second
texture slots and generated reflection UVs are rejected before mutation. Existing
two-layer water should remain unchanged until the SDK supports another slot or a
separately reviewed alternative is chosen. Cloud coverage, wind, receivers,
time-of-day and weather persistence belong to the application.

## Ownership and retirement

`SharedGraphicsResources` owns registered textures and effects. Registration
transfers ownership only on success; duplicate keys/objects, full slots and
exhausted byte budgets fail explicitly. Texture charges use `NativeBytes`; effect
charges are supplied explicitly because the SDK does not expose their individual
allocation size. `TrackedBytes` is this ledger, not total SDK/native heap usage.
Do not mutate or dispose registered resources through other references.

Every `Acquire` returns a separate generation-checked `ResourceLease` ticket.
Release consumes that ticket; default, foreign, stale and repeated releases are
rejected without decrementing another acquisition. Hold leases while constructing
and flushing draws, and `Pin(lease, submittedFrame)` for each submitted use.
Released resources remain owned until `TrimUnused(completedFrame)` can prove both
zero leases and completed GPU use. Supply a monotonically progressing completed
frame from actual SDK fences, not a guessed CPU counter. Call trimming/disposal
between frames. `Dispose(completedFrame)` refuses outstanding leases or GPU work.
The existing streaming `TexturePool` still owns its double-buffered streaming
allocations; this cache does not register or take over those buffers.

## Culling and profiling

`CameraFrustum.Set` updates a preallocated look-at frustum for arbitrary position,
orientation, vertical FOV, aspect and near/far planes. It caches plane coefficients
and sphere radii; `Box`/`Sphere` tests allocate nothing. Invalid updates preserve
the previous view. The existing `Visibility.Frustum` now delegates to a configured
instance matching its historical validation camera. Existing rectangular
occlusion proofs are retained. Cylinder horizon tests, world elevation and LOD
selection remain game policy.

Each helper can contribute logical events to a caller-owned `RenderCounters`.
Reset it at the desired frame boundary. `FrameProfile` exposes the same counters
alongside its existing exclusive timing and percentile metrics without changing
old telemetry protocols. `RenderTelemetry.Capture` copies actual SDK submissions,
texture loads, pipeline changes, triangles and API entries. Capture after the
passes of interest and before the next BeginFrame; the sample captures 3D counts
before its telemetry HUD. `CaptureWaits` reads GPU/VI blocking durations only
after EndFrame; `CaptureCompletedFrame` captures full-frame submissions and waits
together. Native heap bytes are read separately from lease byte charges. GPU wait
duration is not GPU execution time.

## Integration and verification

The Rendering sample uses every area above, including leased texture/effect
ownership, frame-fence retirement, arbitrary-camera culling, two tint batches,
revision-cached ground geometry, cloud cover states and native/logical counters:

```sh
dotnet build Hypercube.sln -c Release
python3 tools/build_assets.py --resolution 48
dotnet run --project tests/Hypercube.Tests -c Release -- out/assets/world.hcp
python3 -m unittest discover -s tests -p 'test_*.py'
tools/build_rendering.sh
docker run --rm --init --shm-size=512m \
  --mount "type=bind,src=$PWD,dst=/work" -w /work hypercube-dolphin \
  python3 tools/rendering_validation.py out/native-rendering-Release/Game.dol \
  --logs out/dolphin-rendering-Release --seconds 35
```

Use a fresh log directory. No data ISO or audio setup is needed for this sample.
Protocol-v9 telemetry checks live progress through clear/partial/overcast cases,
exactly two total geometry uploads, no steady-state buffer growth, bounded API
crossings and complete visibility accounting. It rejects frozen/missing/corrupt
telemetry rather than treating a boot screen as success. Dolphin EFB captures
are normalized to logical 640x480 coordinates, matching existing validators.

Host checks cover stable grouping, pose copies, overflow/reuse, exhaustive growth
bounds, translated/rotated camera queries, failure atomicity, lease tickets and
GPU retirement, mask packing/seams and allocation-free steady-state core calls.
CI builds/runs the sample in Debug and Release and uploads DOL/ELF, results and
captures with the existing validation artifacts. Both local and CI SDK pins now
use b815694, the already validated AnimalTown SDK providing bulk mesh submission
and complete draw telemetry. No SDK source modification or third texture support
is included. Emulator correctness is not a physical GameCube speedup measurement.

Local verification for this change passed Debug/Release solution builds,
1,050,052 rendering assertions plus the existing host suites, 36 Python tests,
both native executables, and all eleven existing Dolphin validation cases
(including sustained audio and the occlusion image comparison). The final
rendering capture decoded 148 live samples across all three cover states, exactly
two dirty geometry uploads, at most two bulk batch calls / three drawing API
calls, 18 actual GX submissions and 576 triangles. These figures demonstrate
bounded work and the distinction between crossings and GX primitives; they are
not before/after hardware frame-time measurements.
