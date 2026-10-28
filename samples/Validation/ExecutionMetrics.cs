using System;
// Foreground execution window, excluding measured GPU/retrace blocking and explicit yielding.
// It may still include managed-gate contention; this is not a hardware cycle counter.
internal sealed class ExecutionMetrics
{
    private readonly int[] bins=new int[4096];
    private long total,gpu,vi;
    internal int Samples,Maximum;
    internal readonly int[] CurrentPrep=new int[7];
    private readonly long[] prep=new long[7];
    internal int PrepMean(int part)=>Samples==0?0:(int)(prep[part]/Samples);
    internal void Reset(){Array.Clear(bins,0,bins.Length);Array.Clear(prep,0,prep.Length);total=gpu=vi=0;Samples=Maximum=0;}
    internal void Record(int elapsed,int gpuWait,int retrace)
    {
        int work=elapsed-gpuWait-retrace;if(work<0)throw new InvalidOperationException("Overlapping execution timing.");
        int bin=work/16;if(bin>=bins.Length)bin=bins.Length-1;bins[bin]++;
        Samples++;total+=work;gpu+=gpuWait;vi+=retrace;if(work>Maximum)Maximum=work;
        for(int i=0;i<prep.Length;i++)prep[i]+=CurrentPrep[i];
    }
    internal int Mean=>Samples==0?0:(int)(total/Samples);
    internal int GpuMean=>Samples==0?0:(int)(gpu/Samples);
    internal int ViMean=>Samples==0?0:(int)(vi/Samples);
    internal int P99 {get{if(Samples==0)return 0;int target=(Samples*99+99)/100,n=0;for(int i=0;i<bins.Length;i++){n+=bins[i];if(n>=target)return i==bins.Length-1?Maximum:(i+1)*16;}return 0;}}
}
