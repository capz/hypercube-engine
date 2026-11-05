namespace Hypercube;

// Value snapshot in a common coordinate space. Callers resolve parenting first.
public struct DrawPose
{
    public float X,Y,Z,Qx,Qy,Qz,Qw,Sx,Sy,Sz;
    public static DrawPose Identity=>new DrawPose{Qw=1,Sx=1,Sy=1,Sz=1};
}
public struct DrawInstance
{
    public int Mesh,Material;public uint Tint;public DrawPose Pose;
}

// Only opaque, depth-tested/writing geometry may be regrouped this way.
// First appearance determines group order; order within a group is stable.
public sealed class OpaqueDrawQueue
{
    private readonly DrawInstance[] entries;
    private readonly int[] order;
    private readonly bool[] selected;
    private bool grouped;
    public int Count {get;private set;}
    public int Capacity=>entries.Length;
    public OpaqueDrawQueue(int capacity)
    {if(capacity<1)throw new ArgumentOutOfRangeException();entries=new DrawInstance[capacity];order=new int[capacity];selected=new bool[capacity];}
    public bool TryAdd(int mesh,int material,uint tint,DrawPose pose)
    {
        if(mesh<0||material<0)throw new ArgumentOutOfRangeException();
        if(!Finite(pose.X)||!Finite(pose.Y)||!Finite(pose.Z)||!Finite(pose.Qx)||!Finite(pose.Qy)||!Finite(pose.Qz)||!Finite(pose.Qw)||!Finite(pose.Sx)||!Finite(pose.Sy)||!Finite(pose.Sz))throw new ArgumentException("Non-finite pose");
        float q=pose.Qx*pose.Qx+pose.Qy*pose.Qy+pose.Qz*pose.Qz+pose.Qw*pose.Qw;
        if(!(q>0)||!Finite(q))throw new ArgumentException("Invalid pose rotation");
        if(Count==Capacity)return false;
        grouped=false;entries[Count++]=new DrawInstance{Mesh=mesh,Material=material,Tint=tint,Pose=pose};return true;
    }
    private static bool Finite(float x)=>!float.IsNaN(x)&&!float.IsInfinity(x);
    public void Group()
    {
        for(int i=0;i<Count;i++)selected[i]=false;
        grouped=true;int at=0;
        for(int i=0;i<Count;i++)if(!selected[i])for(int j=i;j<Count;j++)
            if(!selected[j]&&entries[j].Material==entries[i].Material&&entries[j].Tint==entries[i].Tint){order[at++]=j;selected[j]=true;}
    }
    public DrawInstance At(int groupedIndex)
    {if(!grouped)throw new InvalidOperationException("Group before reading");if((uint)groupedIndex>=Count)throw new ArgumentOutOfRangeException();return entries[order[groupedIndex]];}
    public void Clear(){Count=0;grouped=false;}
}
