using System;
using System.IO;
using System.Threading.Tasks;
using Dolphin.Graphics;
using Dolphin.Storage;
using Dolphin.Timing;

namespace Hypercube.GameCube;

public sealed class WorldStreamer : IDisposable
{
    private const int ResourceCapacity=128;
    public readonly ResourceScheduler Policy;
    public readonly TexturePool Textures=new TexturePool();
    public readonly AramCache Cold;
    public readonly CellReadiness Cells=new CellReadiness();
    private readonly AssetCatalog catalog;
    private readonly PackedWorld world;
    private readonly AssetStream stream;
    private readonly NativeBuffer staging=NativeBuffer.Allocate(131072);
    private readonly Mesh?[] meshes=new Mesh?[ResourceCapacity];
    private readonly NativeBuffer?[] collisionBuffers=new NativeBuffer?[ResourceCapacity];
    private readonly int[] textureSlots=new int[ResourceCapacity],owned=new int[ResourceCapacity];
    private readonly int[] triangleCounts=new int[ResourceCapacity];
    private readonly float[] heights=new float[216];
    private readonly int[] heightMasks=new int[24];
    private readonly int[] batch=new int[8],tags=new int[8],batchIds=new int[8];
    private readonly Task?[] uploads=new Task?[8],stores=new Task?[8];
    private readonly bool[] observed=new bool[8];
    private readonly byte[] scratch,one=new byte[1];
    private Task<int>? reading;
    private int count,batchOffset,batchLength,actualRead,verifyRecord,verifyAt,holdUntil,sequence,textureStages,otherBytes;
    private uint checksum;
    private bool stopping;
    public int Frame,CompletedFrame,Group=1,Format=14,ReadBatchBytes=32768,MaxBatchRecords=4;
    public bool UseAram,InjectFaults,AdaptiveFallback,CoarseFirst,TexturesOnly,ProfileReads;
    private long readAt;
    private DemandPlan? demandPlan;
    private bool recordDemandPlan;
    public int DemandPlanHits,DemandPlanBuilds,DemandPlanOverflows;
    public long ReadLatencyUs;
    public int ReadLatencyCalls,MaxReadLatencyUs;
    // Zero time budget retains legacy behavior. A single native call cannot be preempted.
    public int ChecksumBytesPerPump=8192,CreatesPerPump=4,PumpBudgetUs;
    public long PumpUs;
    public int PumpCalls;
    public int ReadOperations,StorageBytes,AramBytes,Failures,ChecksumFailures,ShortReads,Corruptions,Delays,Batches,MaxBatch,
        MultiplePublications,CancelledUploads,UploadedBytes,ChecksumUs,MaxUploadUs,MaxPumpUs,MeshTriangles,Deferred,Publications;
    public int PhysicalBytes=>Textures.PhysicalBytes+otherBytes;
    public int Active=>count;
    public WorldStreamer(PackedWorld world,string path,int budgetBytes=384*1024)
    {
        this.world=world;scratch=world.Scratch;catalog=world.Catalog;stream=world.Assets;
        Policy=new ResourceScheduler(catalog,ResourceCapacity,budgetBytes);
        Cold=new AramCache(catalog.Count,256*1024);
        for(int i=0;i<textureSlots.Length;i++)textureSlots[i]=-1;
        for(int i=0;i<24;i++)if(world.HasCell(i))Cells.CoarseReady(i);
    }
    public void BeginDemand()=>Policy.BeginDemand();
    private void Demand(int id,ResourcePriority priority)
    {
        if(recordDemandPlan)demandPlan!.Add(id,priority);
        if(Policy.Demand(id,priority)>=0)return;
        // Slot capacity is independent of byte capacity. Reclaim an unneeded
        // resident even when the byte budget itself still has room.
        int victim=Policy.EvictionCandidate(CompletedFrame,priority);
        if(victim>=0){Release(victim,priority);Policy.Demand(id,priority);}
    }
    public void DemandCell(int cell,int lod,bool raw=false)
    {
        int cols=(6+Group-1)/Group;
        int tile=(cell/6/Group)*cols+(cell%6/Group);
        Demand(catalog.Texture(Format,Group,lod,tile),ResourcePriority.Visible);
        for(int id=TexturesOnly?0:catalog.FirstMesh(cell,lod,raw);id!=0;id=catalog.Next(id))Demand(id,ResourcePriority.Visible);
        bool ready=false;
        if(AdaptiveFallback||CoarseFirst)
        {
            int first=catalog.FirstMesh(cell,lod,raw);
            ready=first!=0&&Ready(catalog.Texture(Format,Group,lod,tile));
            for(int id=first;id!=0;id=catalog.Next(id))if(!Ready(id))ready=false;
        }
        if(lod<2&&ScenePolicy.DemandFallback(AdaptiveFallback,ready))
        {
            var priority=CoarseFirst&&!ready?ResourcePriority.Coarse:ResourcePriority.Prefetch;
            Demand(catalog.Texture(Format,Group,lod+1,tile),priority);
            for(int id=TexturesOnly?0:catalog.FirstMesh(cell,lod+1);id!=0;id=catalog.Next(id))Demand(id,priority);
        }
    }
    public void DemandCells(int[] order,int count,int[] lods,bool raw=false)
    {
        // Adaptive/coarse-first policies depend on readiness; expand afresh in that mode.
        if(AdaptiveFallback||CoarseFirst)
        {for(int i=0;i<count;i++)DemandCell(order[i],lods[order[i]],raw);return;}
        if(demandPlan==null)demandPlan=new DemandPlan();
        int config=(Format<<8)|(Group<<1)|(TexturesOnly?1:0);
        if(demandPlan.Matches(order,count,lods,config,raw))
        {
            DemandPlanHits++;
            for(int i=0;i<demandPlan.Count;i++)Demand(demandPlan.Ids[i],demandPlan.Priorities[i]);
            return;
        }
        DemandPlanBuilds++;demandPlan.Begin(order,count,lods,config,raw);recordDemandPlan=true;
        try{for(int i=0;i<count;i++)DemandCell(order[i],lods[order[i]],raw);}
        finally{recordDemandPlan=false;}
        demandPlan.Seal();if(demandPlan.Overflow)DemandPlanOverflows++;
    }
    public void DemandCollision(int cell)
    {if(cell>=0&&cell<24)Demand(catalog.Collision(cell),ResourcePriority.Gameplay);}
    public bool Ready(int id){int slot=Policy.Find(id);return slot>=0&&Policy.State(slot)==ResourceState.Resident;}
    public Mesh? Geometry(int id){int slot=Policy.Find(id);return slot>=0?meshes[slot]:null;}
    public int Triangles(int id){int slot=Policy.Find(id);return slot>=0?triangleCounts[slot]:0;}
    public Texture2D? Texture(int id)
    {
        int slot=Policy.Find(id);
        return slot>=0&&Policy.State(slot)==ResourceState.Resident&&textureSlots[slot]>=0?Textures.Get(textureSlots[slot]).Texture:null;
    }
    public void Pin(int id){int slot=Policy.Find(id);if(slot>=0)Policy.Pin(slot,Frame);}
    public float Ground(int cell,float x,float z)
    {
        float u=(x-(cell%6)*4)/2,v=(z-(cell/6)*4)/2;
        int ix=(int)u,iz=(int)v;if(ix<0)ix=0;if(ix>1)ix=1;if(iz<0)iz=0;if(iz>1)iz=1;
        float fx=u-ix,fz=v-iz;
        int at=cell*9+iz*3+ix;
        int mask=(1<<(iz*3+ix))|(1<<(iz*3+ix+1))|(1<<((iz+1)*3+ix))|(1<<((iz+1)*3+ix+1));
        if((heightMasks[cell]&mask)!=mask)throw new InvalidOperationException("Movement requested over a hole in the collision heightfield.");
        float a=heights[at]*(1-fx)+heights[at+1]*fx,b=heights[at+3]*(1-fx)+heights[at+4]*fx;
        return a*(1-fz)+b*fz;
    }
    private void Release(int slot,ResourcePriority incoming)
    {
        var r=catalog.Get(Policy.Id(slot));
        if(textureSlots[slot]>=0){Textures.Unbind(textureSlots[slot]);textureSlots[slot]=-1;}
        if(meshes[slot]!=null){meshes[slot]!.Dispose();meshes[slot]=null;otherBytes-=owned[slot];}
        if(collisionBuffers[slot]!=null)
        {collisionBuffers[slot]!.Dispose();collisionBuffers[slot]=null;otherBytes-=owned[slot];Cells.CollisionMissing(r.Cell);}
        owned[slot]=0;Policy.Evict(slot,CompletedFrame,incoming);
    }
    private bool Room(int extra,ResourcePriority priority)
    {
        while(PhysicalBytes+extra>Policy.BudgetBytes)
        {
            if(Textures.TrimFree()>0)continue;
            int victim=Policy.EvictionCandidate(CompletedFrame,priority);
            if(victim<0)return false;
            Release(victim,priority);
        }
        return true;
    }
    private bool TextureRoom(AssetRecord record,ResourcePriority priority)
    {
        while(PhysicalBytes+Textures.ExtraBytes(record)>Policy.BudgetBytes)
        {
            int victim=Policy.EvictionCandidate(CompletedFrame,priority);
            if(victim>=0){Release(victim,priority);continue;}
            if(Textures.TrimFree()>0)continue;
            return false;
        }
        return true;
    }
    private void Queue()
    {
        int limit=Policy.RequiredBatchBytes(Frame,ReadBatchBytes);
        if(limit>131040)throw new IOException("Asset exceeds the bounded native staging size.");
        count=Policy.BeginBatch(Frame,limit,MaxBatchRecords,batch,out batchOffset,out batchLength);
        if(count==0)return;
        // A large record may exceed the requested coalescing size; SDK reads it
        // in bounded chunks. Adjacent small records still share one logical read.
        batchLength=(batchLength+31)&~31;
        for(int i=0;i<count;i++)
        {tags[i]=Policy.Generation(batch[i]);batchIds[i]=Policy.Id(batch[i]);uploads[i]=stores[i]=null;observed[i]=false;}
        bool cached=UseAram;
        for(int i=0;i<count;i++)if(!Cold.Has(batchIds[i]))cached=false;
        if(ProfileReads)readAt=Clock.Timestamp;
        reading=cached?ReadCold():stream.ReadAtAsync(batchOffset,staging,0,batchLength);
        if(cached)AramBytes+=batchLength;else{StorageBytes+=batchLength;ReadOperations++;}
        sequence++;Batches++;if(count>MaxBatch)MaxBatch=count;
        verifyRecord=verifyAt=0;checksum=0;actualRead=-1;
        holdUntil=InjectFaults&&sequence%5==0?Frame+12:Frame;
        if(holdUntil>Frame)Delays++;
    }
    private async Task<int> ReadCold()
    {
        for(int i=0;i<count;i++)
        {
            var r=catalog.Get(batchIds[i]);
            await Cold.Fetch(r.Id,staging,r.Offset-batchOffset,r.Length);
        }
        return batchLength;
    }
    private void StartRecord(int i)
    {
        int slot=batch[i];var r=catalog.Get(batchIds[i]);int relative=r.Offset-batchOffset;
        if(UseAram)stores[i]=Cold.Store(r.Id,staging,relative,r.Length,batchIds,count);
        long at=Clock.Timestamp;
        if(r.Kind==1)
        {
            if(!TextureRoom(r,Policy.Priority(slot))||!Policy.CanPublish(slot,r.Length*2))
            {Policy.Defer(slot,Frame);Deferred++;return;}
            int pool=Textures.Acquire(r);
            if(pool<0){Policy.Defer(slot,Frame);Deferred++;return;}
            textureSlots[slot]=pool;owned[slot]=r.Length*2;
            uploads[i]=Textures.Get(pool).StageUploadAsync(staging,relative);
            // Deterministic ownership-loss fault, including the first texture:
            // its transfer must finish before the physical pair is reusable.
            if(InjectFaults&&textureStages++%5==0)Policy.Cancel(r.Id);
        }
        else
        {
            if(r.Length>scratch.Length)throw new IOException("Mesh/collision payload exceeds scratch.");
            staging.Read(scratch,relative,0,r.Length);
            int bytes=r.Kind==2?Binary.U16(scratch,4)*36+Binary.U16(scratch,6)*2:r.Length;
            if(!Room(bytes,Policy.Priority(slot))||!Policy.CanPublish(slot,bytes))
            {Policy.Defer(slot,Frame);Deferred++;return;}
            if(r.Kind==2)
            {
                int tile=(r.Cell/6/Group)*((6+Group-1)/Group)+(r.Cell%6/Group);
                var texture=catalog.Get(catalog.Texture(Format,Group,r.Mip,tile));
                meshes[slot]=world.DecodeMesh(scratch,r,texture,Group);
                bytes=meshes[slot]!.NativeBytes;triangleCounts[slot]=Binary.U16(scratch,6)/3;MeshTriangles+=triangleCounts[slot];
            }
            else
            {
                if(r.Length!=44||Binary.U32(scratch,0)!=0x4c434348)throw new IOException("Invalid collision payload.");
                heightMasks[r.Cell]=Binary.I32(scratch,4);
                for(int k=0;k<9;k++)heights[r.Cell*9+k]=Binary.I32(scratch,8+k*4)/65536f;
                collisionBuffers[slot]=NativeBuffer.Allocate(r.Length);collisionBuffers[slot]!.Write(scratch,0,0,r.Length);
                if(heightMasks[r.Cell]==511)Cells.CollisionReady(r.Cell);
            }
            owned[slot]=bytes;otherBytes+=bytes;Policy.Publish(slot,bytes);Publications++;UploadedBytes+=r.Length;
        }
        int cost=(int)(Clock.Timestamp-at);if(cost>MaxUploadUs)MaxUploadUs=cost;
    }
    public void Pump()
    {
        long pumpAt=Clock.Timestamp;
        try{PumpWork(pumpAt);}
        finally
        {
            int elapsed=(int)(Clock.Timestamp-pumpAt);PumpUs+=elapsed;PumpCalls++;
            if(elapsed>MaxPumpUs)MaxPumpUs=elapsed;
        }
    }
    private void PumpWork(long pumpAt)
    {
        int publishedBefore=Publications;
        Policy.CancelUnneeded();
        if(count==0){if(!stopping)Queue();return;}
        if(actualRead<0)
        {
            if(reading==null||!reading.IsCompleted||Frame<holdUntil)return;
            try{actualRead=reading.GetAwaiter().GetResult();}
            catch(IOException){actualRead=0;Failures++;}
            if(ProfileReads)
            {
                int latency=(int)(Clock.Timestamp-readAt);ReadLatencyUs+=latency;ReadLatencyCalls++;
                if(latency>MaxReadLatencyUs)MaxReadLatencyUs=latency;
            }
            reading=null;
            if(InjectFaults&&sequence%7==3)
            {var r=catalog.Get(batchIds[count-1]);actualRead=r.Offset-batchOffset+r.Length-1;ShortReads++;}
            if(InjectFaults&&sequence%7==5)
            {var r=catalog.Get(batchIds[count-1]);staging.Read(one,r.Offset-batchOffset,0,1);one[0]^=128;staging.Write(one,0,r.Offset-batchOffset,1);Corruptions++;}
        }
        int work=ChecksumBytesPerPump,created=0;
        while(verifyRecord<count&&work>0&&created<CreatesPerPump&&
              (PumpBudgetUs==0||Clock.Timestamp-pumpAt<PumpBudgetUs))
        {
            int i=verifyRecord,slot=batch[i];var r=catalog.Get(batchIds[i]);
            bool valid=r.Offset-batchOffset+r.Length<=actualRead;
            if(Policy.State(slot)==ResourceState.Retiring||tags[i]!=Policy.Generation(slot)||stopping)
            {Policy.Complete(slot,tags[i],false,Frame);observed[i]=true;verifyRecord++;verifyAt=0;checksum=0;continue;}
            if(valid)
            {
                int bytes=r.Length-verifyAt;if(bytes>work)bytes=work;
                long at=Clock.Timestamp;checksum=staging.ComputeCrc32(r.Offset-batchOffset+verifyAt,bytes,checksum);
                ChecksumUs+=(int)(Clock.Timestamp-at);verifyAt+=bytes;work-=bytes;
                if(verifyAt<r.Length)break;
                valid=checksum==r.Checksum;if(!valid)ChecksumFailures++;
            }
            if(!valid)Failures++;
            if(Policy.Complete(slot,tags[i],valid,Frame)&&!stopping){StartRecord(i);created++;}
            if(uploads[i]==null)observed[i]=true;
            verifyRecord++;verifyAt=0;checksum=0;
        }
        bool done=verifyRecord==count;
        for(int i=0;i<count;i++)
        {
            if(uploads[i]!=null&&!observed[i])
            {
                if(!uploads[i]!.IsCompleted){done=false;continue;}
                uploads[i]!.GetAwaiter().GetResult();
                int slot=batch[i];
                if(!stopping&&Policy.State(slot)==ResourceState.Ready&&tags[i]==Policy.Generation(slot))
                {
                    if(!Textures.Get(textureSlots[slot]).TryPublish())throw new InvalidOperationException("Completed staging did not publish.");
                    Policy.Publish(slot,owned[slot]);Publications++;UploadedBytes+=catalog.Get(batchIds[i]).Length;
                }
                else
                {
                    // Consume the completed staging state while the allocation
                    // is unbound. The obsolete asset is never registered resident
                    // or exposed to a material; the physical pair can be reused.
                    if(!Textures.Get(textureSlots[slot]).TryPublish())throw new InvalidOperationException("Obsolete texture staging did not finish.");
                    CancelledUploads++;
                    Textures.Unbind(textureSlots[slot]);textureSlots[slot]=-1;owned[slot]=0;
                    if(Policy.State(slot)==ResourceState.Retiring)Policy.ReleaseRetired(slot);
                }
                observed[i]=true;
            }
            if(stores[i]!=null)
            {if(!stores[i]!.IsCompleted)done=false;else{stores[i]!.GetAwaiter().GetResult();stores[i]=null;}}
        }
        if(Publications-publishedBefore>1)MultiplePublications++;
        if(done)count=0;
    }
    public bool Stop()
    {
        stopping=true;Policy.BeginDemand();Policy.CancelUnneeded();
        if(count!=0){Pump();return false;}return true;
    }
    public void Dispose()
    {
        if(count!=0)throw new InvalidOperationException("Drain streaming work before disposal.");
        for(int i=0;i<meshes.Length;i++){meshes[i]?.Dispose();collisionBuffers[i]?.Dispose();}
        Textures.Dispose();Cold.Dispose();staging.Dispose();otherBytes=0;
    }
}
