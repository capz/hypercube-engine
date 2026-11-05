namespace Hypercube;

// Per-frame logical counters, separate from frame-time and SDK GPU snapshots.
// A caller decides where a frame begins; helpers never reset a shared instance.
public sealed class RenderCounters
{
    public int SubmittedInstances,BatchCalls,MaterialGroups,MaterialTextureSlots,CulledObjects;
    public int DrawSubmissions,MaterialChanges,TextureBindings,Triangles,DrawingApiCalls;
    public int GpuWaitMicroseconds,RetraceWaitMicroseconds,NativeBytes;
    public int GeometryRebuilds,BufferGrowths,BufferAllocations,BatchOverflows;
    public void Reset()
    {
        SubmittedInstances=BatchCalls=MaterialGroups=MaterialTextureSlots=CulledObjects=0;
        DrawSubmissions=MaterialChanges=TextureBindings=Triangles=DrawingApiCalls=0;
        GpuWaitMicroseconds=RetraceWaitMicroseconds=NativeBytes=0;
        GeometryRebuilds=BufferGrowths=BufferAllocations=BatchOverflows=0;
    }
}

public static class BufferGrowth
{
    public static int Next(int current,int required,int maximum,int alignment=3)
    {
        if(alignment<1||maximum<alignment||maximum%alignment!=0||current<0||current>maximum||current%alignment!=0||required<0||required>maximum)throw new ArgumentOutOfRangeException();
        if(required<=current)return current;
        int next=current==0?alignment:current;
        while(next<required)next=next>maximum/2?maximum:next*2;
        return next;
    }
}
