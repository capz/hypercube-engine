using System;
using System.Threading.Tasks;
using Dolphin.Graphics;
using Dolphin.Memory;

namespace Hypercube.GameCube;

public sealed class AramCache : IDisposable
{
    private readonly int capacity;
    // The default streamer disables ARAM. Reserve per-record metadata only when
    // a native cold allocation succeeds, not for Has probes or rejected stores.
    private AramBuffer?[]? buffers;
    private int[]? touched;
    private int order;
    public int Bytes {get;private set;}
    public int Hits,Stores,Evictions;
    public int Budget {get;}
    public AramCache(int records,int budget)
    {if(records<0||records==int.MaxValue||budget<0)throw new ArgumentOutOfRangeException("records");capacity=records+1;Budget=budget;}
    private void ValidateId(int id)
    {if((uint)id>=(uint)capacity)throw new IndexOutOfRangeException();}
    public bool Has(int id){ValidateId(id);return buffers!=null&&buffers[id]!=null;}
    public Task Fetch(int id,NativeBuffer target,int targetOffset,int bytes)
    {
        if(!Has(id))throw new InvalidOperationException("ARAM cache miss.");
        touched![id]=++order;Hits++;
        return AramTransferQueue.CopyToMainAsync(buffers![id]!,0,target,targetOffset,(bytes+31)&~31);
    }
    public Task? Store(int id,NativeBuffer source,int sourceOffset,int bytes,int[] protectedIds,int count)
    {
        ValidateId(id);
        if(buffers!=null&&buffers[id]!=null)return null;
        if(bytes<1||bytes>int.MaxValue-31)throw new ArgumentOutOfRangeException("bytes");
        int size=(bytes+31)&~31;
        if(size>Budget)return null;
        while(size>Budget-Bytes)
        {
            int victim=-1;
            for(int i=0;i<capacity;i++)if(buffers![i]!=null)
            {
                bool protect=false;for(int j=0;j<count;j++)if(protectedIds[j]==i)protect=true;
                if(!protect&&(victim<0||touched![i]<touched[victim]))victim=i;
            }
            if(victim<0)return null;
            Bytes-=buffers![victim]!.Length;buffers[victim]!.Dispose();buffers[victim]=null;Evictions++;
        }
        if(!Aram.TryAllocate(size,out var allocation))return null;
        if(buffers==null)
        {
            try
            {
                var entries=new AramBuffer?[capacity];var ages=new int[capacity];
                buffers=entries;touched=ages;
            }
            catch{allocation.Dispose();throw;}
        }
        buffers[id]=allocation;Bytes+=size;touched![id]=++order;Stores++;
        try{return AramTransferQueue.CopyToAramAsync(source,sourceOffset,allocation,0,size);}
        catch{buffers[id]=null;Bytes-=size;allocation.Dispose();throw;}
    }
    public void Dispose()
    {
        if(buffers==null)return;
        for(int i=0;i<capacity;i++){buffers[i]?.Dispose();buffers[i]=null;}
        buffers=null;touched=null;Bytes=0;
    }
}
