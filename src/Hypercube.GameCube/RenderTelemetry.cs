using Dolphin.Diagnostics;
using Dolphin.Graphics;
namespace Hypercube.GameCube;
public static class RenderTelemetry
{
    // Capture after the passes of interest, before the next BeginFrame resets SDK counters.
    // Logical batching calls are deliberately not reported as GX draw counts.
    public static void Capture(RenderCounters counters)
    {
        var s=GpuProfiler.Snapshot();counters.DrawSubmissions=s.DrawSubmissions;counters.TextureBindings=s.TextureBindings;counters.Triangles=s.Triangles;
        counters.NativeBytes=GraphicsDevice.Default.NativeBytes;
        counters.DrawingApiCalls=GpuProfiler.DrawingApiCalls;counters.MaterialChanges=GpuProfiler.MaterialChanges;
    }
    public static void CaptureWaits(RenderCounters counters)
    {
        if(GraphicsDevice.Default.IsFrameActive)throw new InvalidOperationException("Read completed waits after EndFrame");
        counters.GpuWaitMicroseconds=GpuProfiler.GpuWaitMicroseconds;counters.RetraceWaitMicroseconds=GpuProfiler.RetraceWaitMicroseconds;
    }
    public static void CaptureCompletedFrame(RenderCounters counters){CaptureWaits(counters);Capture(counters);}

}
