using System;
using System.IO;
using System.Threading;
using Dolphin.Graphics;
using Dolphin.Memory;
using Hypercube;
using Hypercube.GameCube;

internal static class StreamingChecks
{
    private static readonly int[] none=Array.Empty<int>();
    private static void Check(bool value,string message)
    {if(!value)throw new Exception(message);}
    private static int[] Triple(AssetCatalog catalog)
    {
        for(int id=1;id<catalog.Count-1;id++)
        {
            var a=catalog.Get(id);var b=catalog.Get(id+1);var c=catalog.Get(id+2);
            if(a.Kind==2&&b.Kind==2&&c.Kind==2&&a.Variant==1&&b.Variant==1&&c.Variant==1&&
               a.Length<=8192&&b.Length<=8192&&c.Length<=8192&&
               b.Offset-a.Offset-a.Length<=32&&c.Offset-b.Offset-b.Length<=32)
                return new[]{id,id+1,id+2};
        }
        throw new Exception("Fixture needs adjacent bounded detail meshes.");
    }
    private static void Tick(WorldStreamer streamer,int[] demand,int completed=-1)
    {
        streamer.Frame++;
        streamer.CompletedFrame=completed<0?streamer.Frame-1:completed;
        streamer.BeginDemand();
        for(int i=0;i<demand.Length;i++)streamer.Policy.Demand(demand[i],ResourcePriority.Visible);
        streamer.Pump();Thread.Yield();
    }
    private static void Ready(WorldStreamer streamer,int[] demand)
    {
        for(int attempt=0;attempt<160;attempt++)
        {
            Tick(streamer,demand);bool ready=true;
            for(int i=0;i<demand.Length;i++)ready&=streamer.Ready(demand[i]);
            if(ready)return;
        }
        throw new Exception("Demand did not become resident.");
    }
    private static void Seed(WorldStreamer streamer,AssetCatalog catalog,int id,byte[] file,bool corrupt=false)
    {
        var record=catalog.Get(id);
        using var bytes=NativeBuffer.Allocate((record.Length+31)&~31);
        bytes.Write(file,record.Offset,0,record.Length);
        if(corrupt){var first=new[]{(byte)(file[record.Offset]^128)};bytes.Write(first,0,0,1);}
        streamer.Cold.Store(id,bytes,0,record.Length,none,0)!.GetAwaiter().GetResult();
        Check(streamer.Cold.Has(id),"Seeded cache entry did not become ready.");
    }
    internal static void Run()
    {
        using var world=new PackedWorld("out/assets/world.hcp");
        int[] triple=Triple(world.Catalog),pair={triple[0],triple[1]};
        byte[] file=File.ReadAllBytes("out/assets/world.hcp");
        OptionalWrite(world,pair);
        PartialHits(world,pair,file);
        MissRuns(world,triple,file,0);
        MissRuns(world,triple,file,1);
        ShortMissRun(world,triple,file);
        CorruptCold(world,pair[0],file);
        FailedFetch(world,pair[0],file);
        IdleResidency(world,pair[0]);
        CancelledTexture(world,pair[0]);
        Check(!AramTransferQueue.HasPending&&StreamingTexture.PendingCount==0,"Streaming test left transfers pending.");
        Console.WriteLine("World streaming: detached optional writes, source reuse, partial hits, cold failure/corruption recovery, idle fences/reversal and cancelled upload ownership passed.");
    }
    private static void OptionalWrite(PackedWorld world,int[] pair)
    {
        int native=NativeBuffer.LiveBytes;
        using(var streamer=new WorldStreamer(world,"unused"))
        {
            streamer.UseAram=true;streamer.RetireIdleResources=false;
            AramTransferQueue.Delay=true;
            Ready(streamer,new[]{pair[0]});
            Check(streamer.Active==0&&streamer.Cold.PendingStores==1&&streamer.Cold.State(pair[0])==AramCacheState.Filling,
                "An optional cold write blocked the completed visible batch.");
            Check(streamer.ColdStagingBytes==16384&&NativeBuffer.LiveBytes==native+streamer.StagingBytes+16384,
                "The optional native source was not separately bounded/accounted.");
            int reads=world.Assets.Reads;
            Ready(streamer,new[]{pair[1]});
            Check(world.Assets.Reads>reads&&streamer.ColdWriteSkips>0,
                "A pending cold write prevented the next storage read/source reuse.");
            AramTransferQueue.Complete();AramTransferQueue.Delay=false;
            Tick(streamer,new[]{pair[1]});
            var record=world.Catalog.Get(pair[0]);
            using(var restored=NativeBuffer.Allocate((record.Length+31)&~31))
            {
                streamer.Cold.Fetch(pair[0],restored,0,record.Length).GetAwaiter().GetResult();
                Check(restored.ComputeCrc32(0,record.Length)==record.Checksum,
                    "Reusing the foreground source overwrote the pending cold snapshot.");
            }
            int collision=world.Catalog.Collision(0);AramTransferQueue.Delay=true;
            Ready(streamer,new[]{collision});
            Check(streamer.Cold.PendingStores==1&&!streamer.Stop(),"Stop did not retain the optional DMA source.");
            try{streamer.Dispose();throw new Exception("Undrained optional source disposed.");}
            catch(InvalidOperationException){}
            AramTransferQueue.Complete(new IOException("Injected optional DMA failure."));AramTransferQueue.Delay=false;
            while(!streamer.Stop()){}
            Check(streamer.ColdWriteFailures==1&&streamer.Cold.State(collision)==AramCacheState.Failed,
                "An asynchronous optional failure escaped or retained its partial entry.");
        }
        Check(NativeBuffer.LiveBytes==native,"Streaming shutdown leaked the detached cold source.");
    }
    private static void PartialHits(PackedWorld world,int[] pair,byte[] file)
    {
        using var streamer=new WorldStreamer(world,"unused");streamer.UseAram=true;
        Seed(streamer,world.Catalog,pair[0],file);
        int reads=world.Assets.Reads,requested=world.Assets.RequestedBytes;
        Ready(streamer,pair);
        Check(streamer.PartialCacheBatches==1&&world.Assets.Reads==reads+1,
            "One cold miss discarded a ready neighboring cache entry.");
        int expected=(world.Catalog.Get(pair[1]).Length+31)&~31;
        Check(world.Assets.RequestedBytes-requested==expected&&streamer.Cold.Hits==1,
            "Mixed-source streaming reread cached storage bytes.");
        while(!streamer.Stop()){}
    }
    private static void CorruptCold(PackedWorld world,int id,byte[] file)
    {
        using var streamer=new WorldStreamer(world,"unused");streamer.UseAram=true;
        Seed(streamer,world.Catalog,id,file,true);int reads=world.Assets.Reads;
        Tick(streamer,new[]{id});Tick(streamer,new[]{id});
        Check(streamer.ChecksumFailures==1&&!streamer.Ready(id)&&!streamer.Cold.Has(id),
            "Corrupt cold data was published or kept as a retry source.");
        Ready(streamer,new[]{id});
        Check(world.Assets.Reads>reads,"Corrupt cache retry did not return to verified storage.");
        while(!streamer.Stop()){}
    }
    private static void MissRuns(PackedWorld world,int[] ids,byte[] file,int cached)
    {
        using var streamer=new WorldStreamer(world,"unused");streamer.UseAram=true;
        Seed(streamer,world.Catalog,ids[cached],file);
        int reads=world.Assets.Reads,requested=world.Assets.RequestedBytes;
        Ready(streamer,ids);
        var a=world.Catalog.Get(ids[0]);var b=world.Catalog.Get(ids[1]);var c=world.Catalog.Get(ids[2]);
        int expected=cached==0?(c.Offset+c.Length-b.Offset+31)&~31:
            ((a.Length+31)&~31)+((c.Length+31)&~31);
        Check(world.Assets.Reads-reads==(cached==0?1:2)&&world.Assets.RequestedBytes-requested==expected,
            "Mixed cache hits did not coalesce consecutive storage misses or split at a cached separator.");
        Check(streamer.Cold.Hits==1&&streamer.PartialCacheBatches==1,"Mixed coalescing lost its ARAM source.");
        while(!streamer.Stop()){}
    }
    private static void ShortMissRun(PackedWorld world,int[] ids,byte[] file)
    {
        using var streamer=new WorldStreamer(world,"unused");streamer.UseAram=true;
        Seed(streamer,world.Catalog,ids[2],file);
        world.Assets.ShortReadNext=world.Catalog.Get(ids[0]).Length-1;
        Tick(streamer,ids);Tick(streamer,ids);
        Check(!streamer.Ready(ids[0])&&!streamer.Ready(ids[1])&&!streamer.Ready(ids[2])&&streamer.Cold.Hits==0,
            "A short storage prefix incorrectly validated a later cached record or stale source bytes.");
        Ready(streamer,ids);
        while(!streamer.Stop()){}
    }
    private static void FailedFetch(PackedWorld world,int id,byte[] file)
    {
        using var streamer=new WorldStreamer(world,"unused");streamer.UseAram=true;
        Seed(streamer,world.Catalog,id,file);int reads=world.Assets.Reads;
        AramTransferQueue.Delay=true;Tick(streamer,new[]{id});
        Check(AramTransferQueue.HasPending,"Cache fetch did not retain its source.");
        AramTransferQueue.Complete(new IOException("Injected cache read failure."));AramTransferQueue.Delay=false;
        Ready(streamer,new[]{id});
        Check(streamer.ColdReadFailures==1&&world.Assets.Reads>reads,"Cache DMA failure did not fall back to storage.");
        while(!streamer.Stop()){}
    }
    private static void IdleResidency(PackedWorld world,int mesh)
    {
        using var streamer=new WorldStreamer(world,"unused");
        streamer.IdleResourceFrames=3;streamer.IdleTextureFrames=2;streamer.MaximumIdleReleases=1;
        streamer.WarmTextureHighBytes=streamer.WarmTextureLowBytes=0;
        int texture=world.Catalog.Texture(14,1,0,0),collision=world.Catalog.Collision(0);
        int[] demand={mesh,texture,collision};Ready(streamer,demand);
        var originalMesh=streamer.Geometry(mesh);var originalTexture=streamer.Texture(texture);
        int patches=world.ParentMeshCount;
        Tick(streamer,none);Tick(streamer,none);Ready(streamer,demand);
        Check(streamer.Geometry(mesh)==originalMesh&&streamer.Texture(texture)==originalTexture,
            "Demand reversal inside the dwell recreated native detail.");
        streamer.Pin(mesh);streamer.Pin(texture);int pinned=streamer.Frame;
        for(int i=0;i<6;i++)Tick(streamer,none,pinned-1);
        Check(streamer.Ready(mesh)&&streamer.Ready(texture),"Idle reclamation freed an unfinished GPU reader.");
        for(int i=0;i<8;i++)Tick(streamer,none,pinned);
        Check(!streamer.Ready(mesh)&&!streamer.Ready(texture)&&streamer.PhysicalBytes==44,
            "Cold detail did not release physical mesh/texture allocations.");
        Check(streamer.Ready(collision)&&streamer.Cells.HasCollision(0)&&world.ParentMeshCount==patches,
            "Idle detail retirement removed movement data or permanent fallback.");
        while(!streamer.Stop()){}
    }
    private static void CancelledTexture(PackedWorld world,int mesh)
    {
        using var streamer=new WorldStreamer(world,"unused");streamer.RetireIdleResources=false;
        int texture=world.Catalog.Texture(14,1,0,0);int[] demand={texture};
        StreamingTexture.Delay=true;
        for(int i=0;i<12&&StreamingTexture.PendingCount==0;i++)Tick(streamer,demand);
        Check(StreamingTexture.PendingCount==1&&streamer.Active!=0,"Texture transfer did not retain its source.");
        int reads=world.Assets.Reads;
        Check(streamer.Policy.Cancel(texture),"Pending texture could not be cancelled.");
        Tick(streamer,new[]{mesh});
        Check(world.Assets.Reads==reads&&streamer.Active!=0,"Cancelled upload released its source before completion.");
        StreamingTexture.CompletePending();StreamingTexture.Delay=false;
        Ready(streamer,new[]{mesh});
        Check(!streamer.Ready(texture)&&streamer.CancelledUploads==1,
            "Cancelled completed texture became visible or its pair stayed transfer-owned.");
        while(!streamer.Stop()){}
    }
}
