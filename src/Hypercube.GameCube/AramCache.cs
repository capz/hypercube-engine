using System;
using System.Threading.Tasks;
using Dolphin.Graphics;
using Dolphin.Memory;

namespace Hypercube.GameCube;

public enum AramCacheState { Missing, Filling, Ready, Failed }

// Application-executor owner. Store borrows its MEM1 source until the returned
// task completes; callers must drain pending stores before clearing the cache.
public sealed class AramCache : IDisposable
{
    private readonly int capacity;
    // The default streamer disables ARAM. Reserve per-record metadata only when
    // a native cold allocation succeeds, not for Has probes or rejected stores.
    private AramBuffer?[]? buffers;
    private int[]? touched;
    private Task?[]? pending;
    private int order;
    public int Bytes {get;private set;}
    public int PendingStores {get;private set;}
    public int FailedStores {get;private set;}
    // Requested aligned upload bytes, including failed/cancelled requests.
    public long StoreBytes {get;private set;}
    public int Hits,Stores,Evictions;
    public int Budget {get;}
    public AramCache(int records,int budget)
    {if(records<0||records==int.MaxValue||budget<0)throw new ArgumentOutOfRangeException("records");capacity=records+1;Budget=budget;}
    private void ValidateId(int id)
    {if((uint)id>=(uint)capacity)throw new IndexOutOfRangeException();}
    private void Release(int id)
    {Bytes-=buffers![id]!.Length;buffers[id]!.Dispose();buffers[id]=null;}
    private void Observe(int id)
    {
        var task=pending![id];
        if(task==null||!task.IsCompleted)return;
        pending[id]=null;PendingStores--;
        try{task.GetAwaiter().GetResult();}
        catch
        {
            // A partial/cancelled upload must never become a cache hit. The
            // native transfer owner has drained before completing this task.
            Release(id);touched![id]=-1;FailedStores++;
        }
    }
    public void Pump()
    {if(PendingStores==0)return;for(int i=0;i<capacity;i++)Observe(i);}
    public AramCacheState State(int id)
    {
        ValidateId(id);if(buffers==null)return AramCacheState.Missing;
        Observe(id);
        if(pending![id]!=null)return AramCacheState.Filling;
        if(buffers[id]!=null)return AramCacheState.Ready;
        return touched![id]<0?AramCacheState.Failed:AramCacheState.Missing;
    }
    public bool Has(int id)=>State(id)==AramCacheState.Ready;
    // A filling entry keeps its allocation and source hold until it settles.
    public bool Remove(int id)
    {
        if(State(id)==AramCacheState.Filling)return false;
        if(buffers==null||buffers[id]==null)return false;
        Release(id);touched![id]=0;return true;
    }
    public Task Fetch(int id,NativeBuffer target,int targetOffset,int bytes)
    {
        if(!Has(id))throw new InvalidOperationException("ARAM cache miss.");
        touched![id]=++order;Hits++;
        return AramTransferQueue.CopyToMainAsync(buffers![id]!,0,target,targetOffset,(bytes+31)&~31);
    }
    public Task? Store(int id,NativeBuffer source,int sourceOffset,int bytes,int[] protectedIds,int count)
    {
        ValidateId(id);
        if(count<0||count>protectedIds.Length)throw new ArgumentOutOfRangeException("count");
        Pump();
        if(buffers!=null&&buffers[id]!=null)return null;
        if(bytes<1||bytes>int.MaxValue-31)throw new ArgumentOutOfRangeException("bytes");
        int size=(bytes+31)&~31;
        if(size>Budget)return null;
        while(size>Budget-Bytes)
        {
            int victim=-1;
            for(int i=0;i<capacity;i++)if(buffers![i]!=null&&pending![i]==null)
            {
                bool protect=false;for(int j=0;j<count;j++)if(protectedIds[j]==i)protect=true;
                if(!protect&&(victim<0||touched![i]<touched[victim]))victim=i;
            }
            if(victim<0)return null;
            Release(victim);touched![victim]=0;Evictions++;
        }
        if(!Aram.TryAllocate(size,out var allocation))return null;
        if(buffers==null)
        {
            try
            {
                var entries=new AramBuffer?[capacity];var ages=new int[capacity];var transfers=new Task?[capacity];
                buffers=entries;touched=ages;pending=transfers;
            }
            catch{allocation.Dispose();throw;}
        }
        buffers[id]=allocation;Bytes+=size;touched![id]=++order;Stores++;
        Task transfer;
        try{transfer=AramTransferQueue.CopyToAramAsync(source,sourceOffset,allocation,0,size);}
        catch{Release(id);touched[id]=-1;FailedStores++;throw;}
        pending![id]=transfer;PendingStores++;StoreBytes+=size;
        Observe(id);return transfer;
    }
    public void Dispose()
    {
        Pump();
        if(PendingStores!=0)throw new InvalidOperationException("Drain ARAM cache stores before disposal.");
        if(buffers==null)return;
        for(int i=0;i<capacity;i++){buffers[i]?.Dispose();buffers[i]=null;}
        buffers=null;touched=null;pending=null;Bytes=0;
    }
}
