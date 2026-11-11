using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dolphin.Graphics;
using Dolphin.Storage;
using Dolphin.Timing;

namespace Hypercube.GameCube;

public readonly struct ResolvedTextureLod
{
    public readonly Texture2D Texture;
    public readonly int FamilyId,LogicalLevel,Generation;
    public readonly TextureUv Uv;
    internal ResolvedTextureLod(Texture2D texture,int family,int logical,int generation,TextureUv uv)
    {Texture=texture;FamilyId=family;LogicalLevel=logical;Generation=generation;Uv=uv;}
}

/// <summary>Single resident image per shared family, one detached destination
/// across this stream, and fence-retired old images. The optional shared policy,
/// source/cache/buffer let WorldStreamer arbitrate gameplay before each chunk.</summary>
public sealed class TextureLodStreamer : IDisposable
{
    private readonly AssetCatalog catalog;
    private readonly AssetStream assets;
    private readonly NativeBuffer staging;
    private readonly bool ownPolicy,ownBuffer,ownCache;
    private readonly Func<int>? externalBytes;
    private readonly Func<ResourcePriority,bool>? reclaim;
    private readonly Texture2D?[] active,retired;
    private readonly int[] resident,activeSlots,generations,requested,usedFrame;
    private readonly GpuFence[] lastUse,retireFences;
    private readonly long[] firstDemand;
    private readonly int[] protectedIds;
    private readonly Texture2D placeholder;
    private GpuFence submittedFence;
    private DetachedTextureUpload? upload;
    private CancellationTokenSource? cancellation;
    private Task<int>? read;
    private Task? copy,store;
    private int slot=-1,tag,familyId,levelIndex,offset,chunkBytes,chunkOffset;
    private bool cached,cacheAttempted,stopping,disposed;
    private long began;
    public readonly ResourceScheduler Policy;
    public readonly TextureFamilyDemand Demands;
    public readonly AramCache Cold;
    public bool UseAram,StrictReleaseColdCopies;
    public bool CorruptNextChunk,ShortNextRead;
    public int Frame {get;private set;}
    public int CompletedFrame {get;private set;}
    public int ActiveBytes {get;private set;}
    public int RetiredBytes {get;private set;}
    public int UploadBytes=>upload?.StagedBytes??0;
    public int ReadBufferBytes=>!disposed&&ownBuffer?staging.Length:0;
    public int StagingBytes=>ReadBufferBytes+UploadBytes;
    public int PoolBytes=>disposed?0:placeholder.NativeBytes;
    public int PhysicalBytes=>ActiveBytes+RetiredBytes+StagingBytes+PoolBytes;
    public int AramBytes=>Cold.Bytes;
    public int PeakBytes,Publications,Cancellations,StaleUploads,Failures,ChecksumFailures,Deferred,Retirements,StorageBytes,ColdReadBytes,UploadedBytes,ReadOperations,Preemptions;
    public int CoarseReadyCount,DetailReadyCount,MaxPumpUs;
    public long CoarseLatencyUs,DetailLatencyUs;
    public bool OwnsStaging=>read!=null||copy!=null||store!=null;
    public bool HasTransfer=>upload!=null||OwnsStaging;
    public TextureLodStreamer(AssetCatalog catalog,AssetStream assets,int budgetBytes=2*1024*1024,
        ResourceScheduler? policy=null,AramCache? cold=null,NativeBuffer? stagingBuffer=null,
        Func<int>? externalPhysicalBytes=null,Func<ResourcePriority,bool>? reclaimUnneeded=null)
    {
        if(catalog.FamilyCount==0)throw new ArgumentException("Pack has no texture families.");
        this.catalog=catalog;this.assets=assets;externalBytes=externalPhysicalBytes;reclaim=reclaimUnneeded;
        ownPolicy=policy==null;Policy=policy??new ResourceScheduler(catalog,256,budgetBytes);
        ownCache=cold==null;Cold=cold??new AramCache(catalog.Count,256*1024);
        ownBuffer=stagingBuffer==null;staging=stagingBuffer??NativeBuffer.Allocate(32768);
        int n=catalog.FamilyCount+1;active=new Texture2D?[n];resident=new int[n];activeSlots=new int[n];generations=new int[n];requested=new int[n];
        usedFrame=new int[n];lastUse=new GpuFence[n];firstDemand=new long[n];retired=new Texture2D?[Policy.Capacity];retireFences=new GpuFence[Policy.Capacity];protectedIds=new int[n];
        for(int i=0;i<n;i++){resident[i]=activeSlots[i]=-1;usedFrame[i]=-1;}
        placeholder=Texture2D.Create(2,2,new byte[]{64,72,88,255,96,104,120,255,96,104,120,255,64,72,88,255});
        Demands=new TextureFamilyDemand(catalog);PeakBytes=PhysicalBytes;
    }
    public StreamedMaterialTexture Get(string name,TextureQualityPolicy quality=TextureQualityPolicy.DistanceMip)=>new StreamedMaterialTexture(catalog.Family(name),quality);
    public void BeginFrame(int frame,int completedFrame)
    {
        if(disposed||stopping)throw new InvalidOperationException("Texture stream is stopping.");
        if(frame<Frame||completedFrame>frame||completedFrame<CompletedFrame)throw new ArgumentOutOfRangeException("frame");
        Frame=frame;CompletedFrame=completedFrame;if(ownPolicy)Policy.BeginDemand();Demands.BeginFrame(frame);
    }
    public void Demand(StreamedMaterialTexture binding,TextureFootprint footprint,ResourcePriority priority=ResourcePriority.Visible)=>Demands.Demand(binding,footprint,priority);
    public void DemandKnownLod(StreamedMaterialTexture binding,int logicalLevel,ResourcePriority priority=ResourcePriority.Visible)=>Demands.DemandKnownLod(binding,logicalLevel,priority);
    public bool CoarseReady(int family)=>active[family]!=null;
    public bool DetailReady(int family)=>active[family]!=null&&resident[family]==Demands.TargetIndex(family);
    public int ResidentIndex(int family)=>resident[family];
    public void PrepareDemand()
    {
        for(int id=1;id<active.Length;id++)
        {
            bool wanted=!stopping&&Demands.IsWanted(id);int index=wanted?Demands.RequestedIndex(id,resident[id]):-1;
            int record=index<0?0:catalog.Family(id).GetLevel(index).RecordId;
            if(record!=requested[id]&&requested[id]!=0&&requested[id]!=(activeSlots[id]<0?0:Policy.Id(activeSlots[id])))
            {if(Policy.Cancel(requested[id]))Cancellations++;}
            requested[id]=record;
            if(!wanted)continue;
            if(firstDemand[id]==0)firstDemand[id]=Clock.Timestamp;
            var priority=Demands.Priority(id);
            if(activeSlots[id]>=0)Policy.Demand(Policy.Id(activeSlots[id]),priority<=ResourcePriority.Visible?ResourcePriority.Coarse:priority);
            // A newly ready coarse image must reach an actual submitted frame
            // before a full-quality consumer can enqueue a costly replacement.
            if(index>=0&&index!=resident[id]&&(active[id]==null||lastUse[id].IsValid))
            {
                if(active[id]==null&&priority<=ResourcePriority.Visible)priority=ResourcePriority.Coarse;
                else if(active[id]!=null&&priority<ResourcePriority.Visible)priority=ResourcePriority.Visible;
                int error=(int)Math.Min(1000000,Demands.ProjectedError(id));
                Policy.Demand(record,priority,error);
            }
        }
    }
    public ResolvedTextureLod Resolve(StreamedMaterialTexture binding)
    {
        int id=binding.Family.Id;if(catalog.Family(id)!=binding.Family)throw new ArgumentException("Foreign texture family.");
        if(active[id]==null)return new ResolvedTextureLod(placeholder,id,-1,generations[id],TextureUv.Identity);
        var level=binding.Family.GetLevel(resident[id]);
        return new ResolvedTextureLod(active[id]!,id,level.LogicalLevel,generations[id],level.Uv);
    }
    /// <summary>Validate the lease immediately BEFORE submitting its draw. EndFrame
    /// supplies the actual GPU fence for every family used in that frame.</summary>
    public bool Pin(ResolvedTextureLod resolved,int submittedFrame)
    {
        int id=resolved.FamilyId;
        if(id<1||id>=active.Length||submittedFrame!=Frame||resolved.Generation!=generations[id]||resolved.Texture!=(active[id]??placeholder))return false;
        usedFrame[id]=Frame;if(activeSlots[id]>=0)Policy.Pin(activeSlots[id],Frame);return true;
    }
    public void EndFrame(GpuFence fence)
    {
        if(!fence.IsValid)throw new ArgumentException("Missing submitted frame fence.");submittedFence=fence;
        for(int i=1;i<active.Length;i++)if(usedFrame[i]==Frame)lastUse[i]=fence;
    }
    public void Collect()
    {
        for(int i=0;i<retired.Length;i++)if(retired[i]!=null&&retired[i]!.TryDisposeAfterFence(retireFences[i]))
        {RetiredBytes-=catalog.Get(Policy.Id(i)).Length;retired[i]=null;Policy.ReleaseRetired(i);Retirements++;}
    }
    private void Retire(int id)
    {
        int old=activeSlots[id];if(old<0)return;
        retired[old]=active[id];retireFences[old]=lastUse[id].IsValid?lastUse[id]:submittedFence;
        int bytes=active[id]!.NativeBytes;ActiveBytes-=bytes;RetiredBytes+=bytes;
        Policy.RetireResident(old);active[id]=null;activeSlots[id]=resident[id]=-1;generations[id]++;
        if(StrictReleaseColdCopies)Cold.Drop(Policy.Id(old));
    }
    public int TrimUnwanted()
    {
        int before=PhysicalBytes;
        for(int id=1;id<active.Length;id++)if((stopping||!Demands.IsWanted(id))&&active[id]!=null&&submittedFence.IsValid)Retire(id);
        if(StrictReleaseColdCopies)for(int id=1;id<=catalog.FamilyCount;id++)
        {
            var family=catalog.Family(id);
            for(int k=0;k<family.Count;k++)if(k!=resident[id]&&!(slot>=0&&familyId==id&&levelIndex==k))Cold.Drop(family.GetLevel(k).RecordId);
        }
        Collect();return before-PhysicalBytes;
    }
    // WorldStreamer's completed-frame eviction path can reclaim this image
    // immediately only when its actual final GPU fence has also completed.
    internal bool Evict(int candidate,ResourcePriority incoming)
    {
        for(int id=1;id<active.Length;id++)if(activeSlots[id]==candidate)
        {
            var fence=lastUse[id].IsValid?lastUse[id]:submittedFence;
            if(!fence.IsCompleted)return false;
            int bytes=active[id]!.NativeBytes;if(!active[id]!.TryDisposeAfterFence(fence))return false;
            active[id]=null;ActiveBytes-=bytes;resident[id]=activeSlots[id]=-1;generations[id]++;
            if(StrictReleaseColdCopies)Cold.Drop(Policy.Id(candidate));Policy.Evict(candidate,CompletedFrame,incoming);Retirements++;return true;
        }
        return false;
    }
    private bool Current()=>slot>=0&&!stopping&&Policy.Generation(slot)==tag&&Policy.State(slot)==ResourceState.Reading&&requested[familyId]==Policy.Id(slot);
    private bool Room(int bytes,ResourcePriority priority)
    {
        while(PhysicalBytes+(externalBytes?.Invoke()??0)+bytes>Policy.BudgetBytes)
        {
            int before=PhysicalBytes;TrimUnwanted();if(PhysicalBytes<before)continue;
            if(reclaim!=null&&reclaim(priority))continue;
            if(ownPolicy)
            {
                int victim=Policy.EvictionCandidate(CompletedFrame,priority);
                if(victim>=0&&Evict(victim,priority))continue;
            }
            return false;
        }
        return true;
    }
    private bool Start()
    {
        int next=Policy.NextQueued(Frame);if(next<0||catalog.Get(Policy.Id(next)).Kind!=6)return false;
        var record=catalog.Get(Policy.Id(next));int id=record.Group;var family=catalog.Family(id);int index=-1;
        for(int k=0;k<family.Count;k++)if(family.GetLevel(k).RecordId==record.Id)index=k;
        if(index<0)throw new IOException("Unindexed family read.");
        if(!Room(record.Length,Policy.Priority(next)))
        {Policy.BeginRecord(next,Frame);Policy.Complete(next,Policy.Generation(next),true,Frame);Policy.Defer(next,Frame);Deferred++;return false;}
        Policy.BeginRecord(next,Frame);slot=next;tag=Policy.Generation(next);familyId=id;levelIndex=index;offset=0;cacheAttempted=false;began=Clock.Timestamp;
        cancellation=new CancellationTokenSource();cached=UseAram&&Cold.Has(record.Id);
        try{upload=DetachedTextureUpload.Create(record.Width,record.Height,(NativeTextureFormat)record.Format,record.Length,record.Checksum);}
        catch(ArgumentException){return AllocationDeferred();}
        catch(InvalidOperationException){return AllocationDeferred();}
        PeakBytes=Math.Max(PeakBytes,PhysicalBytes+(externalBytes?.Invoke()??0));return true;
    }
    private bool AllocationDeferred()
    {Policy.Complete(slot,tag,true,Frame);Policy.Defer(slot,Frame);cancellation!.Dispose();cancellation=null;slot=-1;Deferred++;return false;}
    private void Discard(bool failed)
    {
        if(OwnsStaging)throw new InvalidOperationException("Drain all source users before discarding an upload.");
        int record=Policy.Id(slot);
        if(Policy.State(slot)==ResourceState.Retiring){Policy.Complete(slot,tag,false,Frame);StaleUploads++;}
        else if(Policy.State(slot)==ResourceState.Reading){Policy.Complete(slot,tag,false,Frame);if(failed)Failures++;}
        upload?.Dispose();upload=null;cancellation?.Dispose();cancellation=null;slot=-1;
        if(StrictReleaseColdCopies||failed)Cold.Drop(record);
    }
    private async Task<int> ReadCold(int record,int sourceOffset,int bytes)
    {await Cold.FetchRange(record,sourceOffset,staging,0,bytes);return bytes;}
    public void Pump(int uploadBudgetBytes=32768,int cpuBudgetUs=1000,bool allowStart=true)
    {
        if(disposed)throw new ObjectDisposedException("TextureLodStreamer");
        if(uploadBudgetBytes<0||cpuBudgetUs<0)throw new ArgumentOutOfRangeException("uploadBudgetBytes");
        long start=Clock.Timestamp;
        try{PumpWork(uploadBudgetBytes,cpuBudgetUs,allowStart,start);}
        finally{int cost=(int)(Clock.Timestamp-start);if(cost>MaxPumpUs)MaxPumpUs=cost;PeakBytes=Math.Max(PeakBytes,PhysicalBytes+(externalBytes?.Invoke()??0));}
    }
    private void PumpWork(int budget,int cpuBudget,bool allowStart,long start)
    {
        Collect();if(ownPolicy){PrepareDemand();Policy.CancelUnneeded();}
        if(slot<0){if(stopping||!allowStart||budget<32||!Start())return;}
        bool current=Current();if(!current)cancellation!.Cancel();
        if(store!=null)
        {if(!store.IsCompleted)return;try{store.GetAwaiter().GetResult();}catch(Exception){Cold.Drop(Policy.Id(slot));}store=null;}
        if(copy!=null)
        {
            if(!copy.IsCompleted)return;
            try{copy.GetAwaiter().GetResult();offset=upload!.CompletedBytes;UploadedBytes+=chunkBytes;}
            catch(Exception){copy=null;if(read==null){Discard(current);return;}throw;}
            copy=null;
        }
        if(read!=null)
        {
            if(!read.IsCompleted)return;
            int actual=0;try{actual=read.GetAwaiter().GetResult();}catch(Exception){read=null;Discard(current);return;}read=null;
            if(cached)ColdReadBytes+=actual;else StorageBytes+=actual;
            if(!current){Discard(false);return;}
            if(ShortNextRead){ShortNextRead=false;actual--;}
            if(actual!=chunkBytes){Discard(true);return;}
            if(CorruptNextChunk){CorruptNextChunk=false;var one=new byte[1];staging.Read(one);one[0]^=128;staging.Write(one);}
            try{copy=upload!.StageChunkAsync(staging,0,offset,chunkBytes,cancellation!.Token);}catch(Exception){Discard(true);}return;
        }
        if(!current){Discard(false);return;}
        var record=catalog.Get(Policy.Id(slot));
        if(offset==record.Length)
        {
            try{upload!.Seal();}catch(InvalidDataException){ChecksumFailures++;Discard(true);return;}
            if(!cacheAttempted)
            {
                cacheAttempted=true;
                if(UseAram&&chunkOffset==0&&chunkBytes==record.Length)
                {
                    protectedIds[0]=record.Id;
                    store=Cold.Store(record.Id,staging,0,record.Length,protectedIds,1);if(store!=null)return;
                }
            }
            if(!Policy.Complete(slot,tag,true,Frame)){Discard(false);return;}
            if(!Policy.CanPublish(slot,record.Length)){Policy.Defer(slot,Frame);upload!.Dispose();upload=null;cancellation!.Dispose();cancellation=null;slot=-1;Deferred++;return;}
            if(!upload!.TryPublish(out var texture))throw new InvalidOperationException("Validated texture did not publish.");
            if(active[familyId]!=null)Retire(familyId);
            active[familyId]=texture;resident[familyId]=levelIndex;activeSlots[familyId]=slot;generations[familyId]++;lastUse[familyId]=default;
            ActiveBytes+=record.Length;Policy.Publish(slot,record.Length);Publications++;
            long latency=Clock.Timestamp-(firstDemand[familyId]==0?began:firstDemand[familyId]);
            if(levelIndex==catalog.Family(familyId).CoarseIndex){CoarseReadyCount++;CoarseLatencyUs+=latency;}
            if(levelIndex==Demands.TargetIndex(familyId)){DetailReadyCount++;DetailLatencyUs+=Clock.Timestamp-began;}
            upload.Dispose();upload=null;cancellation!.Dispose();cancellation=null;slot=-1;return;
        }
        if(!allowStart||budget<32||cpuBudget>0&&Clock.Timestamp-start>=cpuBudget)return;
        int queued=Policy.NextQueued(Frame);
        if(queued>=0&&Policy.Priority(queued)<Policy.Priority(slot))
        {
            Preemptions++;
            if(catalog.Get(Policy.Id(queued)).Kind==6)
            {
                // One detached destination is allowed. Yielding forever while
                // retaining it would deadlock a newly visible coarse family.
                // All source users have drained here, so discard/requeue this
                // upgrade and let the coarse request obtain that allocation.
                if(Policy.Cancel(Policy.Id(slot)))Cancellations++;
                Discard(false);
            }
            return;
        }
        chunkOffset=offset;chunkBytes=Math.Min(record.Length-offset,Math.Min(staging.Length,budget))&~31;
        if(chunkBytes==0)return;
        if(cached)read=ReadCold(record.Id,offset,chunkBytes);
        else{read=assets.ReadAtAsync(record.Offset+offset,staging,0,chunkBytes,cancellation!.Token);ReadOperations++;}
    }
    public bool Stop()
    {
        stopping=true;PrepareDemand();cancellation?.Cancel();
        if(slot>=0&&Policy.State(slot)!=ResourceState.Retiring)Policy.Cancel(Policy.Id(slot));
        Pump(0,0,false);TrimUnwanted();return !HasTransfer&&ActiveBytes==0&&RetiredBytes==0;
    }
    public void Dispose()
    {
        if(disposed)return;if(!Stop())throw new InvalidOperationException("Pump Stop and complete frame fences before disposal.");
        if(submittedFence.IsValid){if(!placeholder.TryDisposeAfterFence(submittedFence))throw new InvalidOperationException("Placeholder GPU readers remain.");}
        else placeholder.Dispose();
        if(ownCache)Cold.Dispose();if(ownBuffer)staging.Dispose();disposed=true;
    }
}
