using System;
using Dolphin.Graphics;

namespace Hypercube.GameCube;

// Logical asset mappings and physical double-buffer allocations are accounted
// separately: unbound reusable buffers still consume the physical budget.
public sealed class TexturePool : IDisposable
{
    private const int Capacity=48;
    private readonly StreamingTexture?[] textures=new StreamingTexture?[Capacity];
    private readonly bool[] used=new bool[Capacity];
    private readonly int[] widths=new int[Capacity],formats=new int[Capacity],bytes=new int[Capacity];
    private readonly int[] idleAt=new int[Capacity],lastFrame=new int[Capacity];
    private bool trimming;
    public int PhysicalBytes {get;private set;}
    public int Reuses,Allocations;
    public int IdleBytes
    {get{int total=0;for(int i=0;i<Capacity;i++)if(!used[i]&&textures[i]!=null)total+=bytes[i];return total;}}
    public int ExtraBytes(AssetRecord r)=>ExtraBytes(r,int.MaxValue);
    public int ExtraBytes(AssetRecord r,int completedFrame)
    {
        for(int i=0;i<textures.Length;i++)if(CanTrim(i,completedFrame)&&widths[i]==r.Width&&formats[i]==r.Format)return 0;
        return r.Length*2;
    }
    public int TrimFree()=>TrimFree(int.MaxValue);
    public int TrimFree(int completedFrame)
    {
        for(int i=0;i<textures.Length;i++)if(CanTrim(i,completedFrame))return Trim(i);
        return 0;
    }
    private bool CanTrim(int slot,int completedFrame)
        =>!used[slot]&&textures[slot]!=null&&!textures[slot]!.HasPendingUpload&&lastFrame[slot]<=completedFrame;
    private int Trim(int slot)
    {textures[slot]!.Dispose();textures[slot]=null;PhysicalBytes-=bytes[slot];return bytes[slot];}
    /// <summary>Keep a bounded warm pool, with byte hysteresis and an idle dwell.
    /// Pending copies and GPU readers always take precedence over reclamation.</summary>
    public int TrimCold(int frame,int completedFrame,int idleFrames,int highBytes,int lowBytes,int maximumReleases)
    {
        if(frame<0||idleFrames<0||lowBytes<0||highBytes<lowBytes||maximumReleases<0)throw new ArgumentOutOfRangeException();
        int idle=IdleBytes,released=0;
        if(idle>highBytes)trimming=true;
        if(idle<=lowBytes)trimming=false;
        for(int i=0;i<Capacity&&trimming&&released<maximumReleases;i++)
            if(CanTrim(i,completedFrame)&&frame-idleAt[i]>=idleFrames)
            {idle-=Trim(i);released++;if(idle<=lowBytes)trimming=false;}
        return released;
    }
    public int Acquire(AssetRecord r)=>Acquire(r,int.MaxValue);
    public int Acquire(AssetRecord r,int completedFrame)
    {
        for(int i=0;i<textures.Length;i++)if(CanTrim(i,completedFrame)&&widths[i]==r.Width&&formats[i]==r.Format)
        {used[i]=true;Reuses++;return i;}
        int slot=-1;
        for(int i=0;i<textures.Length;i++)if(textures[i]==null){slot=i;break;}
        if(slot<0){if(TrimFree(completedFrame)==0)return -1;return Acquire(r,completedFrame);}
        textures[slot]=new StreamingTexture(r.Width,r.Width,(NativeTextureFormat)r.Format,true);
        widths[slot]=r.Width;formats[slot]=r.Format;bytes[slot]=2*r.Length;
        lastFrame[slot]=0;idleAt[slot]=0;
        PhysicalBytes+=bytes[slot];used[slot]=true;Allocations++;return slot;
    }
    public StreamingTexture Get(int slot)=>textures[slot]!;
    public void Unbind(int slot)=>Unbind(slot,0);
    public void Unbind(int slot,int frame){used[slot]=false;idleAt[slot]=frame;}
    public void Pin(int slot,int frame){if(frame>lastFrame[slot])lastFrame[slot]=frame;}
    public void Dispose()
    {
        for(int i=0;i<textures.Length;i++){textures[i]?.Dispose();textures[i]=null;used[i]=false;}
        PhysicalBytes=0;trimming=false;
    }
}
