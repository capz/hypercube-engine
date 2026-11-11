using System;

namespace Hypercube;

public readonly struct TextureWorkBudget
{
    public readonly int UploadBytes,CpuMicroseconds;
    public readonly bool AllowPrefetch;
    public TextureWorkBudget(int uploadBytes,int cpuMicroseconds,bool allowPrefetch=false)
    {
        if(uploadBytes<0||(uploadBytes&31)!=0||cpuMicroseconds<1)throw new ArgumentOutOfRangeException("uploadBytes");
        UploadBytes=uploadBytes;CpuMicroseconds=cpuMicroseconds;AllowPrefetch=allowPrefetch&&uploadBytes>0;
    }
}

/// <summary>Opt-in feedback control. Supply measured CPU work before present,
/// the previous pump cost and read latency. Defaults are profiling starting
/// values; pending copies still drain when issuance is paused.</summary>
public sealed class TextureBudgetController
{
    private int stable;
    public int MinimumChunkBytes {get;}
    public int MaximumChunkBytes {get;}
    public int ChunkBytes {get;private set;}
    public int CpuBudgetUs {get;}
    public int FrameCpuBudgetUs {get;}
    public int ReadLatencySoftLimitUs {get;}
    public int PausedFrames,ReducedFrames,Growths;
    public TextureBudgetController(int maximumChunkBytes=32768,int minimumChunkBytes=4096,int cpuBudgetUs=1000,int frameCpuBudgetUs=8000,int readLatencySoftLimitUs=8000)
    {
        if(minimumChunkBytes<32||(minimumChunkBytes&31)!=0||maximumChunkBytes<minimumChunkBytes||(maximumChunkBytes&31)!=0||maximumChunkBytes>131072||cpuBudgetUs<1||frameCpuBudgetUs<cpuBudgetUs||readLatencySoftLimitUs<1)throw new ArgumentOutOfRangeException("maximumChunkBytes");
        MinimumChunkBytes=minimumChunkBytes;MaximumChunkBytes=ChunkBytes=maximumChunkBytes;CpuBudgetUs=cpuBudgetUs;FrameCpuBudgetUs=frameCpuBudgetUs;ReadLatencySoftLimitUs=readLatencySoftLimitUs;
    }
    public TextureWorkBudget Next(int frameCpuUs,int lastPumpUs,int lastReadUs,bool audioStarved=false,int criticalQueued=0)
    {
        if(frameCpuUs<0||lastPumpUs<0||lastReadUs<0||criticalQueued<0)throw new ArgumentOutOfRangeException("frameCpuUs");
        int headroom=FrameCpuBudgetUs-frameCpuUs;
        if(audioStarved||criticalQueued>0||headroom<Math.Min(128,CpuBudgetUs)){stable=0;PausedFrames++;return new TextureWorkBudget(0,1);}
        int cpu=Math.Min(CpuBudgetUs,headroom);
        if(lastPumpUs>cpu||lastReadUs>ReadLatencySoftLimitUs)
        {ChunkBytes=Math.Max(MinimumChunkBytes,(ChunkBytes/2)&~31);stable=0;ReducedFrames++;}
        else if(lastPumpUs<cpu/2&&headroom-cpu>=cpu)
        {
            if(++stable>=16){int next=Math.Min(MaximumChunkBytes,ChunkBytes*2);if(next!=ChunkBytes)Growths++;ChunkBytes=next;stable=0;}
        }
        else stable=0;
        return new TextureWorkBudget(ChunkBytes,cpu,headroom-cpu>=cpu&&lastReadUs<=ReadLatencySoftLimitUs);
    }
}

public readonly struct TexturePrefetchHint
{
    public readonly float VisibilityProbability,SecondsToVisible,DirectionDot,ProjectedPixels;
    public readonly bool SeenRecently;
    public TexturePrefetchHint(float probability,float secondsToVisible,float directionDot,float projectedPixels,bool seenRecently=false)
    {
        if(float.IsNaN(probability)||probability<0||probability>1||float.IsNaN(secondsToVisible)||float.IsInfinity(secondsToVisible)||secondsToVisible<0||
           float.IsNaN(directionDot)||directionDot< -1||directionDot>1||float.IsNaN(projectedPixels)||float.IsInfinity(projectedPixels)||projectedPixels<0)throw new ArgumentOutOfRangeException("probability");
        VisibilityProbability=probability;SecondsToVisible=secondsToVisible;DirectionDot=directionDot;ProjectedPixels=projectedPixels;SeenRecently=seenRecently;
    }
}

public static class TexturePrefetchPolicy
{
    public static bool Likely(TexturePrefetchHint hint,TextureWorkBudget budget,int freeNativeBytes,int coarseBytes,int reserveBytes=32768)
    {
        if(freeNativeBytes<0||coarseBytes<1||reserveBytes<0)throw new ArgumentOutOfRangeException("freeNativeBytes");
        return budget.AllowPrefetch&&coarseBytes<=freeNativeBytes&&reserveBytes<=freeNativeBytes-coarseBytes&&hint.ProjectedPixels>=8&&
            hint.VisibilityProbability>=(hint.SeenRecently?.6f:.75f)&&hint.SecondsToVisible<=(hint.SeenRecently?1:.75f)&&hint.DirectionDot>=.25f;
    }
}
