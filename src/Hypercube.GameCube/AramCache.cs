using System;
using System.Threading.Tasks;
using Dolphin.Graphics;
using Dolphin.Memory;

namespace Hypercube.GameCube;

public sealed class AramCache : IDisposable
{
    private readonly AramBuffer?[] buffers;
    private readonly int[] touched;
    private readonly Task?[] transfers;
    private int order;
    public int Bytes {get;private set;}
    public int Hits,Stores,Evictions;
    public int Budget {get;}
    public AramCache(int records,int budget){if(records<1||budget<0)throw new ArgumentOutOfRangeException("records");buffers=new AramBuffer?[records+1];transfers=new Task?[records+1];touched=new int[records+1];Budget=budget;}
    private bool Ready(int id)
    {
        var task=transfers[id];if(task==null)return true;if(!task.IsCompleted)return false;
        transfers[id]=null;
        try{task.GetAwaiter().GetResult();}catch(Exception){if(buffers[id]!=null){Bytes-=buffers[id]!.Length;buffers[id]!.Dispose();buffers[id]=null;}}
        return true;
    }
    public bool Has(int id)=>Ready(id)&&buffers[id]!=null;
    public bool Drop(int id)
    {
        if(!Ready(id))return false;if(buffers[id]==null)return true;
        Bytes-=buffers[id]!.Length;buffers[id]!.Dispose();buffers[id]=null;Evictions++;return true;
    }
    public int Trim()
    {int before=Bytes;for(int i=1;i<buffers.Length;i++)Drop(i);return before-Bytes;}
    public int TrimExcept(int protectedRecord)
    {int before=Bytes;if(protectedRecord>0&&Bytes==(buffers[protectedRecord]?.Length??0))return 0;for(int i=1;i<buffers.Length;i++)if(i!=protectedRecord)Drop(i);return before-Bytes;}
    public Task Fetch(int id,NativeBuffer target,int targetOffset,int bytes)
        =>FetchRange(id,0,target,targetOffset,bytes);
    public Task FetchRange(int id,int sourceOffset,NativeBuffer target,int targetOffset,int bytes)
    {
        if(!Has(id))throw new InvalidOperationException("ARAM cache miss or busy transfer.");
        touched[id]=++order;Hits++;
        return transfers[id]=AramTransferQueue.CopyToMainAsync(buffers[id]!,sourceOffset,target,targetOffset,(bytes+31)&~31);
    }
    public Task? Store(int id,NativeBuffer source,int sourceOffset,int bytes,int[] protectedIds,int count)
    {
        if(!Ready(id)||buffers[id]!=null)return null;
        int size=(bytes+31)&~31;
        if(size>Budget)return null;
        while(Bytes+size>Budget)
        {
            int victim=-1;
            for(int i=1;i<buffers.Length;i++)if(Ready(i)&&buffers[i]!=null)
            {
                bool protect=false;for(int j=0;j<count;j++)if(protectedIds[j]==i)protect=true;
                if(!protect&&(victim<0||touched[i]<touched[victim]))victim=i;
            }
            if(victim<0)return null;
            Bytes-=buffers[victim]!.Length;buffers[victim]!.Dispose();buffers[victim]=null;Evictions++;
        }
        if(!Aram.TryAllocate(size,out var allocation))return null;
        buffers[id]=allocation;Bytes+=size;touched[id]=++order;Stores++;
        try{return transfers[id]=AramTransferQueue.CopyToAramAsync(source,sourceOffset,allocation,0,size);}
        catch{buffers[id]=null;Bytes-=size;allocation.Dispose();throw;}
    }
    public void Dispose()
    {
        for(int i=1;i<buffers.Length;i++)if(!Ready(i))throw new InvalidOperationException("Drain ARAM transfers before disposing the cache.");
        for(int i=1;i<buffers.Length;i++){buffers[i]?.Dispose();buffers[i]=null;}
        Bytes=0;
    }
}
