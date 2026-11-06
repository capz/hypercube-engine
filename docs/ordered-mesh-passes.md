# Ordered mesh passes

`OrderedMeshPasses` keeps a fixed sequence of color/UV variants for one borrowed
uniform-color mesh. It is intended for effects such as a live backdrop blur,
where changing draw order changes the result. It does not merge transparent
geometry or reduce the number of GPU samples.

Create the plan and call `Set` between frames. `Draw` requires one texture,
authored UVs, unlit shading and no custom effect. Pass colors replace the material
color; pass UV transforms replace its primary transform. The caller's material
is never mutated, including if a portable draw throws. The portable path warms
a private working material during construction, so even a caller material with
untouched lazy sampler/UV state cannot allocate in the first draw. The mesh and texture remain owned
by the caller and must remain alive through the draw.

The published-SDK build uses the ordered portable loop. With the source SDK PR
that adds `MeshPasses`, build `Hypercube.GameCube` with
`-p:HypercubeNativePasses=true` and `-p:DolphinDotNetRoot=/path/to/sdk`.
That path stores variants once, prepares an aligned POS/UV index command list
between frames, validates once per block and replays it natively. It saves CPU
submission; it does not promise less GPU work. Neither path allocates during draw.

```csharp
var blur = new OrderedMeshPasses(panelMesh, 81);
// Configure the same 81 weights and UV offsets between frames.
blur.Set(tap, weight, offset);
// During the renderer's 3D pass:
blur.Draw(renderer, backdropMaterial, identity);
```

Run `dotnet run --project tests/OrderedMeshPasses.Tests -c Release` for portable
ordering, validation, exceptional state restoration and allocation checks.
The source-SDK backend is also exercised by the real VideoMode integration DOL:

```
python3 ci/build_videomode_source_sdk.py --sdk /path/to/DolphinDotNet \
  --hypercube /path/to/hypercube-engine --output /tmp/videomode \
  --icons --glass --blur
```

Run that command from the Samples checkout. Native captures and guest-clock
measurements are recorded in its `VideoMode/Design/submission-round-2` report.
Cloud Dolphin results are comparative emulator evidence, not physical GameCube
timings. The default package dependency and existing engine validation scene
remain compatible with the published SDK.
