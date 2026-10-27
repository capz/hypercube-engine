# Hypercube Engine

Experimental GameCube engine built on [DolphinDotNet](https://github.com/capz/DolphinDotNet). The first implementation evaluates streaming dense, inefficient static geometry before committing to a production content format.

Open **Hypercube.sln** for the engine, runtime adapter, host checks and console validation sample. The existing DolphinDotNet texture stress/comparison samples remain in the SDK repository.

## Build

Requirements: .NET 8, Python 3.10+, Git and Docker.

```sh
mkdir -p vendor
git clone https://github.com/capz/DolphinDotNet vendor/sdk
git -C vendor/sdk checkout "$(cat .sdk-revision)"
# Managed build only; suitable for opening/building the solution in an IDE.
dotnet build Hypercube.sln
# Generates assets, runs host checks, AOT-compiles and produces the actual DOL.
tools/build_validation.sh
```

The solution builds managed projects by default; the script produces the console executable. The SDK and native toolchain are pinned. Outputs are `out/native-Release/Game.dol`, `out/assets/world.hcp`, `world.iso` and `report.json`. Set `CONFIGURATION=Debug` for a Debug executable. `DOLPHIN_DOTNET_ROOT` can point to an existing SDK checkout. Restricted environments may also set `LIBOGC2_SOURCE` and `CA_CERT_FILE`.

## Run

In Dolphin, set the default inserted ISO to `out/assets/world.iso`, then boot `Game.dol`. The ISO is a data disc, not a standalone bootable game. Use DSP **LLE** with Dolphin's distributed free DSP ROMs; the old Dolphin 5.0 HLE implementation does not recognize the current AESND microcode. Set texture cache accuracy to Safe.

On hardware, launch the DOL with `world.hcp` at `sd:/hypercube/world.hcp` on an SD2SP2 adapter. Disc loading is also supported when the data disc is inserted. The code reserves 2 MiB of ARAM for audio before creating the asset cache.

```sh
docker build -t hypercube-dolphin -f tools/Dockerfile.dolphin .
docker run --rm --init --shm-size=512m --user "$(id -u):$(id -g)" \
  --mount "type=bind,src=$PWD,dst=/work" -w /work hypercube-dolphin \
  python3 tools/dolphin_validation.py out/native-Release/Game.dol \
  --disc out/assets/world.iso --logs out/dolphin-Release --seconds 240
```

The suite cycles automatically through eleven cases. A advances, X changes the dynamic cache budget (192/384/768 KiB; 24/48/96 KiB in the pressure case), Y pauses camera movement, START restarts the suite, R toggles results, B drains work and exits. The yellow actor moves only after destination collision is ready. Coarse visuals remain available while detail loads. The binary strip at the bottom supplies machine-readable measurements.

## Performance experiments

The original eleven cases remain the default. A separate executable compares adaptive fallback demand, coarse-first scheduling, bounded streaming work and compact telemetry rendering against repaired RGBA8 geometry:

```sh
PERFORMANCE_SUITE=1 tools/build_validation.sh
# Mount the same data ISO, then boot out/native-performance-Release/Game.dol.
# Or use the Dolphin container built above:
docker run --rm --init --shm-size=512m --user "$(id -u):$(id -g)" \
  --mount "type=bind,src=$PWD,dst=/work" -w /work hypercube-dolphin \
  python3 tools/dolphin_validation.py out/native-performance-Release/Game.dol \
  --suite performance --disc out/assets/world.iso --logs out/performance --seconds 240
```

The Python command requires Dolphin, Xvfb and FFmpeg; run it inside `hypercube-dolphin` as shown above. Hardware uses the same SD2SP2 asset path. The suite runs nine cases automatically and displays F50/F95/F99 frame times. START repeats the run, R toggles results. CI uploads both executables and `out/dolphin-performance-Release/results.json`. See [the experiment design and research](docs/performance-experiments.md) before choosing defaults. These are experiments; hardware results decide which settings to keep.

## Frame-phase profiling

```sh
PROFILING_SUITE=1 tools/build_validation.sh
# Mount out/assets/world.iso, then boot out/native-profiling-Release/Game.dol.
```

This separate sixteen-case suite compares baselines, work-isolation controls, and isolated preparation optimizations. It records exclusive streaming, mesh decode, native mesh creation, drawing, present, explicit delay, HUD, preparation and residual wall time. Present includes GPU completion and VSync; read latency includes queueing and frame polling.

The result screens include cache budget and selected-detail readiness. Cases 11–15 compare productive SDK waits, native asset I/O, stride-64 polling, combined SDK settings and SDK plus prep. Cases 6–10 measure fast ID validation, tree masks, readiness reuse, demand-plan caching and their combination; case 5 repeats the baseline. R cycles nine screens covering all case groups. Press **R** to cycle timing/phase/quality pages. **X** changes the budget and restarts the entire suite, clearing all old rows; **START** also clears and restarts. The isolation cases deliberately remove work and are not equivalent-quality speedups. Run the same Docker command as above with `--suite profiling`, the profiling DOL, a fresh log directory and `--seconds 360`. CI runs and exports these comparisons too. See [profiling methodology](docs/frame-profiling.md).

## Dense asset intake

```sh
python3 tools/build_assets.py --input path/to/scene.glb --output out/imported
python3 tools/build_assets.py --resolution 128 --output out/dense-fixture
```

Static OBJ, glTF 2.0 and GLB are supported. The validation envelope is X=0..24, Z=0..16 and absolute coordinates <=100; normalize imported geometry explicitly before packing. Input is repaired conservatively, clipped at cell boundaries, clustered by material and spatial location, and given measured coarse LODs. Disconnected pieces remain present. Invalid accessors, required extensions, skinning and animation are rejected with an explanation.

The default fixture is **synthetic adversarial input**, not output from an AI model. It includes duplicate corners/faces, degenerates, multiple materials, dense curved ground, an isolated component and an opaque occluder. Imported assets currently receive a planar diagnostic checker: authored PBR textures are not baked. The animated probe actor uses a rigid rotating mesh. Collision is a sampled top surface, not general mesh physics.

Read [the implementation and decision gates](docs/streaming-validation.md), [rendering design](docs/README.md) and [memory hypotheses](docs/memory-and-validation.md). Dolphin establishes correctness and comparative emulator measurements; hardware measurements decide SD/drive latency, DSP/ARAM contention, visual LOD quality and final defaults.

The next SDK iteration has a separate [wait/read follow-up suite](docs/sdk-followup.md): `SDK_FOLLOWUP_SUITE=1 tools/build_validation.sh`, then `--suite sdk-followup` in the validator. It compares completion checks, deadline hints and aligned 16/32/64 KiB native reads with four-page protocol-v7 diagnostics.
