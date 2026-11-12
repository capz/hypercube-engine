using System;
using System.Threading.Tasks;
using Dolphin.Graphics;
using Dolphin.Memory;
using Hypercube.GameCube;

internal static class AramCacheChecks
{
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    internal static void Run()
    {
        long before=GC.GetAllocatedBytesForCurrentThread();var unused=new AramCache(4096,256*1024);
        Check(GC.GetAllocatedBytesForCurrentThread()-before<256,"Unused ARAM cache allocated per-record metadata.");
        before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<4097;i++)Check(!unused.Has(i),"Empty ARAM lookup hit.");
        unused.Dispose();unused.Dispose();Check(GC.GetAllocatedBytesForCurrentThread()==before,"Empty ARAM probes/disposal allocate.");
        var source=new NativeBuffer(160);for(int i=0;i<source.Data.Length;i++)source.Data[i]=(byte)i;
        var protectedIds=new int[1];
        var tiny=new AramCache(4096,32);int attempts=Aram.Attempts;
        before=GC.GetAllocatedBytesForCurrentThread();
        Check(tiny.Store(1,source,0,33,protectedIds,0)==null&&Aram.Attempts==attempts,"Over-budget store attempted native allocation.");
        Check(GC.GetAllocatedBytesForCurrentThread()==before,"Over-budget store allocated metadata.");tiny.Dispose();
        var failed=new AramCache(4096,128);Aram.Fail=true;before=GC.GetAllocatedBytesForCurrentThread();
        Check(failed.Store(1,source,0,33,protectedIds,0)==null&&failed.Bytes==0&&!failed.Has(1),"Failed native allocation became cached.");
        Check(GC.GetAllocatedBytesForCurrentThread()==before,"Failed native allocation allocated metadata.");Aram.Fail=false;failed.Dispose();
        var cache=new AramCache(8,128);
        cache.Store(1,source,32,33,protectedIds,0)!.GetAwaiter().GetResult();
        Check(cache.Bytes==64&&cache.Has(1)&&AramTransferQueue.LastBytes==64,"Initial store did not preserve aligned DMA size.");
        int stores=cache.Stores;Check(cache.Store(1,source,0,33,protectedIds,0)==null&&cache.Stores==stores,"Duplicate store copied or allocated.");
        var target=new NativeBuffer(96);cache.Fetch(1,target,32,33).GetAwaiter().GetResult();
        for(int i=0;i<64;i++)Check(target.Data[32+i]==source.Data[32+i],"Cold fetch contents or offsets changed.");
        Check(cache.Hits==1&&AramTransferQueue.LastBytes==64,"Fetch statistics/padding changed.");
        cache.Store(2,source,0,64,protectedIds,0)!.GetAwaiter().GetResult();
        protectedIds[0]=1;cache.Store(3,source,0,64,protectedIds,1)!.GetAwaiter().GetResult();
        Check(cache.Has(1)&&!cache.Has(2)&&cache.Has(3)&&cache.Evictions==1&&cache.Bytes==128,"Protected record or eviction accounting changed.");
        cache.Fetch(1,target,0,64).GetAwaiter().GetResult();cache.Store(4,source,0,64,protectedIds,0)!.GetAwaiter().GetResult();
        Check(cache.Has(1)&&cache.Has(4)&&!cache.Has(3),"Fetch did not refresh LRU order.");
        cache.Dispose();cache.Dispose();Check(cache.Bytes==0&&!cache.Has(1)&&Aram.LiveBytes==0,"Cold cache disposal leaked resources.");
        // Dispose previously cleared the cache; retain the ability to store again.
        cache.Store(5,source,0,64,protectedIds,0)!.GetAwaiter().GetResult();
        Check(cache.Has(5)&&cache.Bytes==64,"Cleared cold cache could not be reused.");cache.Dispose();
        var copyFailure=new AramCache(8,128);AramTransferQueue.Fail=true;
        try{copyFailure.Store(1,source,0,64,protectedIds,0);throw new Exception("Failed cold copy was accepted.");}catch(InvalidOperationException){}
        AramTransferQueue.Fail=false;Check(copyFailure.Bytes==0&&!copyFailure.Has(1)&&Aram.LiveBytes==0,"Synchronous copy failure leaked allocation or metadata entry.");copyFailure.Dispose();
        try{unused.Fetch(1,target,0,32);throw new Exception("Missing cold record was fetched.");}catch(InvalidOperationException){}
        try{unused.Has(4097);throw new Exception("Invalid cold ID accepted.");}catch(IndexOutOfRangeException){}
        // ID zero was accepted by the original array-based API. It must follow
        // the same eviction/disposal ownership rules as every other slot.
        var zero=new AramCache(2,64);
        zero.Store(0,source,0,32,protectedIds,0)!.GetAwaiter().GetResult();
        zero.Store(1,source,0,32,protectedIds,0)!.GetAwaiter().GetResult();
        protectedIds[0]=1;zero.Store(2,source,0,32,protectedIds,1)!.GetAwaiter().GetResult();
        Check(!zero.Has(0)&&zero.Has(1)&&zero.Has(2),"Zero ID was excluded from eviction.");
        zero.Store(0,source,0,32,protectedIds,0)!.GetAwaiter().GetResult();
        Check(zero.Has(0),"Zero ID is no longer supported.");zero.Dispose();
        Check(Aram.LiveBytes==0,"Zero ID survived cache disposal.");
        Console.WriteLine("Lazy ARAM cache: zero per-record allocation while unused/rejected, aligned transfers, LRU/protection including ID zero, failure cleanup and disposal passed.");
    }
}

namespace Dolphin.Graphics
{
    public sealed class NativeBuffer
    {
        internal readonly byte[] Data;
        public NativeBuffer(int size){Data=new byte[size];}
    }
}
namespace Dolphin.Memory
{
    public sealed class AramBuffer : IDisposable
    {
        internal readonly byte[] Data;private bool disposed;
        public int Length=>Data.Length;
        internal AramBuffer(int size){Data=new byte[size];Aram.LiveBytes+=size;}
        public void Dispose(){if(disposed)throw new Exception("ARAM allocation disposed twice.");disposed=true;Aram.LiveBytes-=Length;}
    }
    public static class Aram
    {
        public static bool Fail;
        public static int Attempts,LiveBytes;
        public static bool TryAllocate(int bytes,out AramBuffer allocation)
        {Attempts++;if(Fail){allocation=null!;return false;}allocation=new AramBuffer(bytes);return true;}
    }
    public static class AramTransferQueue
    {
        public static bool Fail;public static int LastBytes;
        public static Task CopyToAramAsync(Dolphin.Graphics.NativeBuffer source,int sourceOffset,AramBuffer target,int targetOffset,int bytes)
        {if(Fail)throw new InvalidOperationException("Injected copy failure.");LastBytes=bytes;Array.Copy(source.Data,sourceOffset,target.Data,targetOffset,bytes);return Task.CompletedTask;}
        public static Task CopyToMainAsync(AramBuffer source,int sourceOffset,Dolphin.Graphics.NativeBuffer target,int targetOffset,int bytes)
        {LastBytes=bytes;Array.Copy(source.Data,sourceOffset,target.Data,targetOffset,bytes);return Task.CompletedTask;}
    }
}
