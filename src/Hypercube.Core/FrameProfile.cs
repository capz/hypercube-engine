namespace Hypercube;

// Exclusive frame phases. Decode/Create are subtracted from the enclosing pump.
// All durations are elapsed wall time, including executor stalls inside a phase.
public sealed class FrameProfile
{
    public const int Stream=0, Decode=1, Create=2, Draw=3, Present=4, Yield=5, Hud=6, Prepare=7, Other=8;
    public readonly FrameMetrics Frames=new FrameMetrics();
    public readonly int[] Current=new int[8];
    private readonly long[] totals=new long[9],quality=new long[6];
    private readonly int[] maxima=new int[9];
    public int DetailFrames {get;private set;}
    public long TotalUs {get;private set;}
    public int Mean(int phase)=>Frames.Samples==0?0:(int)(totals[phase]/Frames.Samples);
    public int Maximum(int phase)=>maxima[phase];
    // demand, selected-ready, streamed-ready, visible, submitted draws, triangles
    public int Quality100(int field)=>Frames.Samples==0?0:(int)(quality[field]*100/Frames.Samples);
    public int SelectedPermille=>quality[0]==0?0:(int)(quality[1]*1000/quality[0]);
    public void Reset()
    {
        Frames.Reset();DetailFrames=0;TotalUs=0;
        for(int i=0;i<9;i++){totals[i]=0;maxima[i]=0;if(i<8)Current[i]=0;}
        for(int i=0;i<6;i++)quality[i]=0;
    }
    public void Record(int elapsedUs,int demanded,int selected,int streamed,int visible,int draws,int triangles)
    {
        long sum=0;
        for(int i=0;i<8;i++)
        {if(Current[i]<0)throw new System.InvalidOperationException("Negative profiling interval.");sum+=Current[i];}
        if(elapsedUs<sum)throw new System.InvalidOperationException("Profiling phases overlap.");
        if(selected<0||selected>streamed||streamed>demanded||demanded>visible||draws<0||triangles<0)
            throw new System.InvalidOperationException("Invalid detail accounting.");
        Frames.Record(elapsedUs);TotalUs+=elapsedUs;
        for(int i=0;i<9;i++)
        {
            int value=i==8?elapsedUs-(int)sum:Current[i];
            totals[i]+=value;if(value>maxima[i])maxima[i]=value;
        }
        quality[0]+=demanded;quality[1]+=selected;quality[2]+=streamed;quality[3]+=visible;
        quality[4]+=draws;quality[5]+=triangles;if(streamed>0)DetailFrames++;
    }
}
