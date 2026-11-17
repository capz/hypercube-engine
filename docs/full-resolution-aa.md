# Full-resolution GameCube AA

Set `DisplayOptions.AntialiasingMode` to `AntialiasingMode.FullResolution`
for 640×480 output. Implement `IFrameScene.SubmitFramePart` and pass the prepared
scene to `FrameRenderer.Draw`. It submits once with AA off and twice with full AA.

Update simulation, animation, visibility and resource residency **before** Draw.
Submission may reset and refill bounded draw batches, but must not advance time,
load/release assets, change display mode or consume input. Render world and HUD
inside the callback. Capture water/refraction within each part, then draw it.
The SDK crops perspective, orthographic and 2D projections and maps scissors.

Full AA uses hardware three-sample RGB565/Z16, without destination alpha.
It retains 640×480 logical resolution and the existing double XFB allocation;
there is no extra full-screen AA texture. Submission/geometry work repeats and
resolve copies cost bandwidth. Applications should offer a persisted off/on
option and measure their own scenes. This is independent of emulator host MSAA.
