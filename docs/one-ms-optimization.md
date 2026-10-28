# SDK and engine execution budget

Work is isolated on `codex/one-ms-engine` and `codex/one-ms-sdk`.

The tested baseline is SDK `1c77976` plus engine `326d4f1` (the previous texture work), not the repositories' current main branches. Measurements use Dolphin LLE, a single emulated CPU, the same scene, actor, audio and streaming policy, and a 384 KiB dynamic byte budget. Hardware timing remains unverified.

| Change | Foreground mean, ms |
| --- | ---: |
| Baseline | 12.290 |
| Atlas prototype | 9.109 |
| Release compilation with LTO | 7.548 |
| Cached metadata and stable demand ordering | 6.844 |
| GX indexed fetch for uniform unlit meshes | 4.403 |
| Bounded compiler leaf optimization | 3.868 |
| Checked inline runtime paths | 3.086 |
| Free-slot index | 2.816 |
| Cached console commands and rendering state | 2.390 |
| CRC table and hot-path inlining | 2.236 |
| 128 planning slots, 48 texture descriptors | 1.837 |

The final row increases planning metadata, while retaining the 384 KiB physical streaming budget. Selected detail improved from approximately 32% to 40.5%, and blocked moves decreased from 186 to 73. Its P99 foreground window is 4.160 ms; displayed frame intervals remain approximately 16.7 ms. **The 1 ms target is not achieved.**

These are foreground wall-time windows excluding measured GX/VI blocking and the explicit yield. They include managed-gate contention but exclude background work overlapping waits; they are not total CPU-cycle or full GPU timing measurements. Profiling bookkeeping after the window is also outside this metric. Compare quality, work and budget alongside timing.

Rejected experiments: merging adjacent world draws (7.641 versus 7.548 ms), argument-root deduplication, skipping gate handoffs during native worker work, and aggregate-address access inlining produced no useful additional gain. Argument deduplication and worker scheduling changes were removed. No extra threads were introduced.

The remaining preparation is approximately 0.39 ms visibility/demand, 0.12 ms residency resolution, and 0.19 ms tree traversal/sorting. HUD work and world submission are approximately 0.33 and 0.41 ms respectively. Further changes must preserve scene quality and be measured against this baseline.

## Reproduction

With the modified SDK checked out, run:

```sh
DOLPHIN_DOTNET_ROOT=/path/to/sdk EXECUTION_SUITE=1 tools/build_validation.sh
python3 tools/dolphin_validation.py out/native-execution-Release/Game.dol --suite one-ms --disc out/assets/world.iso --seconds 90 --logs out/execution-run
```

The runner requires Dolphin, Xvfb and ffmpeg; the native build uses the pinned libogc2 container. Protocol 9 adds execution and preparation measurements, preserving frozen-window, geometry, movement, budget, storage and audio validation. JSON from each experiment is under `measurements/one-ms/`; raw captures remain in the worktree's ignored `out/` directory, losslessly compressed with zstd to conserve disk space.

The SDK API addition is `OverlayGlyphMode.Atlas`; existing modes remain for comparison. Remove legacy controls only after repeated Dolphin and hardware validation, as requested. Release GameCube bundles default to LTO; symbol builds retain their previous settings. Native flag changes rebuild objects and relink through a content-sensitive dependency stamp.
