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
    public int PhysicalBytes {get;private set;}
    public int Reuses,Allocations;
    public int ExtraBytes(AssetRecord r)
    {
        for(int i=0;i<textures.Length;i++)if(!used[i]&&textures[i]!=null&&widths[i]==r.Width&&formats[i]==r.Format)return 0;
        return r.Length*2;
    }
    public int TrimFree()
    {
        for(int i=0;i<textures.Length;i++)if(!used[i]&&textures[i]!=null)
        {textures[i]!.Dispose();textures[i]=null;PhysicalBytes-=bytes[i];return bytes[i];}
        return 0;
    }
    public int Acquire(AssetRecord r)
    {
        for(int i=0;i<textures.Length;i++)if(!used[i]&&textures[i]!=null&&widths[i]==r.Width&&formats[i]==r.Format)
        {used[i]=true;Reuses++;return i;}
        int slot=-1;
        for(int i=0;i<textures.Length;i++)if(textures[i]==null){slot=i;break;}
        if(slot<0){if(TrimFree()==0)return -1;return Acquire(r);}
        textures[slot]=new StreamingTexture(r.Width,r.Width,(NativeTextureFormat)r.Format,true);
        widths[slot]=r.Width;formats[slot]=r.Format;bytes[slot]=2*r.Length;
        PhysicalBytes+=bytes[slot];used[slot]=true;Allocations++;return slot;
    }
    public StreamingTexture Get(int slot)=>textures[slot]!;
    public void Unbind(int slot)=>used[slot]=false;
    public void Dispose()
    {
        for(int i=0;i<textures.Length;i++){textures[i]?.Dispose();textures[i]=null;used[i]=false;}
        PhysicalBytes=0;
    }
}
