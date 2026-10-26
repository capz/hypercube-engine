using System;
using System.Threading.Tasks;
using Dolphin.Graphics;
using Dolphin.Memory;

namespace Hypercube.GameCube;

public sealed class AramCache : IDisposable
{
    private readonly AramBuffer?[] buffers;
    private readonly int[] touched;
    private int order;
    public int Bytes {get;private set;}
    public int Hits,Stores,Evictions;
    public int Budget {get;}
    public AramCache(int records,int budget){buffers=new AramBuffer?[records+1];touched=new int[records+1];Budget=budget;}
    public bool Has(int id)=>buffers[id]!=null;
    public Task Fetch(int id,NativeBuffer target,int targetOffset,int bytes)
    {
        if(buffers[id]==null)throw new InvalidOperationException("ARAM cache miss.");
        touched[id]=++order;Hits++;
        return AramTransferQueue.CopyToMainAsync(buffers[id]!,0,target,targetOffset,(bytes+31)&~31);
    }
    public Task? Store(int id,NativeBuffer source,int sourceOffset,int bytes,int[] protectedIds,int count)
    {
        if(buffers[id]!=null)return null;
        int size=(bytes+31)&~31;
        if(size>Budget)return null;
        while(Bytes+size>Budget)
        {
            int victim=-1;
            for(int i=1;i<buffers.Length;i++)if(buffers[i]!=null)
            {
                bool protect=false;for(int j=0;j<count;j++)if(protectedIds[j]==i)protect=true;
                if(!protect&&(victim<0||touched[i]<touched[victim]))victim=i;
            }
            if(victim<0)return null;
            Bytes-=buffers[victim]!.Length;buffers[victim]!.Dispose();buffers[victim]=null;Evictions++;
        }
        if(!Aram.TryAllocate(size,out var allocation))return null;
        buffers[id]=allocation;Bytes+=size;touched[id]=++order;Stores++;
        try{return AramTransferQueue.CopyToAramAsync(source,sourceOffset,allocation,0,size);}
        catch{buffers[id]=null;Bytes-=size;allocation.Dispose();throw;}
    }
    public void Dispose()
    {
        for(int i=1;i<buffers.Length;i++){buffers[i]?.Dispose();buffers[i]=null;}
        Bytes=0;
    }
}
