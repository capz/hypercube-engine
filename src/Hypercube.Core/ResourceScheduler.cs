using System;

namespace Hypercube;

public enum ResourceState { Missing, Queued, Reading, Ready, Resident, Retiring, Failed }
public enum ResourcePriority { Gameplay, Coarse, Visible, Prefetch, Optional }

// A bounded, allocation-free policy core. SDK/native transfer owners remain in
// the adapter, which supplies fence completion and observes every issued read.
public sealed class ResourceScheduler
{
    private readonly int[] ids, priorities, generations, retries, references, pins, touched, fences;
    private readonly ResourceState[] states;
    private readonly int[] lookup;
    private readonly AssetCatalog catalog;
    private int nextGeneration = 1, order;
    public int ResidentBytes { get; private set; }
    public int BudgetBytes { get; }
    public int Capacity => ids.Length;
    public int Rejected, Retries, Cancelled, Stale, Published, Evicted;
    public ResourceScheduler(AssetCatalog catalog, int capacity, int budgetBytes)
    {
        if (capacity < 1 || capacity > 256 || budgetBytes < 1) throw new ArgumentOutOfRangeException("capacity");
        this.catalog = catalog; BudgetBytes = budgetBytes;
        ids = new int[capacity]; priorities = new int[capacity]; generations = new int[capacity];
        retries = new int[capacity]; references = new int[capacity]; pins = new int[capacity]; touched = new int[capacity];
        states = new ResourceState[capacity]; fences = new int[capacity]; lookup = new int[catalog.Count+1];
        for(int i=0;i<lookup.Length;i++)lookup[i]=-1;
    }
    public int Id(int slot) => ids[slot];
    public int Generation(int slot) => generations[slot];
    public ResourceState State(int slot) => states[slot];
    public ResourcePriority Priority(int slot) => (ResourcePriority)priorities[slot];
    public int Find(int id)
    { return id>0&&id<lookup.Length&&lookup[id]>=0&&states[lookup[id]]!=ResourceState.Missing?lookup[id]:-1; }
    public void BeginDemand()
    { for (int i = 0; i < ids.Length; i++) references[i] = 0; }
    public int Demand(int id, ResourcePriority priority)
    {
        catalog.Get(id);
        int found = Find(id);
        if (found >= 0)
        {
            if (references[found] == 0 || (int)priority < priorities[found]) priorities[found] = (int)priority;
            references[found]++;
            if(states[found]==ResourceState.Resident)touched[found]=++order;
            return found;
        }
        int free=-1;
        for(int i=0;i<ids.Length;i++)if(states[i]==ResourceState.Missing){free=i;break;}
        if(free<0)for(int i=0;i<ids.Length;i++)if(references[i]==0&&
            (states[i]==ResourceState.Queued||states[i]==ResourceState.Failed))
        {free=i;Cancelled++;break;}
        if(free>=0)
        {
            int i=free;
            if(ids[i]>0&&lookup[ids[i]]==i)lookup[ids[i]]=-1;
            ids[i] = id; lookup[id]=i; states[i] = ResourceState.Queued; priorities[i] = (int)priority;
            references[i] = 1; touched[i] = ++order; generations[i] = nextGeneration++;
            retries[i] = 0; return i;
        }
        Rejected++; return -1;
    }
    // Cancel an issued request without releasing its transfer-owned storage.
    // The adapter must observe completion before acknowledging retirement.
    public bool Cancel(int id)
    {
        int slot=Find(id);
        if(slot<0||states[slot]==ResourceState.Resident||states[slot]==ResourceState.Retiring)return false;
        bool issued=states[slot]==ResourceState.Reading||states[slot]==ResourceState.Ready;
        states[slot]=issued?ResourceState.Retiring:ResourceState.Missing;
        references[slot]=0;generations[slot]=nextGeneration++;Cancelled++;return true;
    }
    public void CancelUnneeded()
    {
        for (int i = 0; i < ids.Length; i++) if (references[i] == 0)
        {
            if (states[i] == ResourceState.Queued || states[i] == ResourceState.Failed)
            { states[i] = ResourceState.Missing; generations[i] = nextGeneration++; Cancelled++; }
            else if (states[i] == ResourceState.Reading || states[i] == ResourceState.Ready)
            { states[i] = ResourceState.Retiring; generations[i] = nextGeneration++; Cancelled++; }
        }
    }
    // Records must be adjacent/near-adjacent in the pack. One logical read can
    // satisfy several independently resident pages, regardless of draw size.
    public int BeginBatch(int now, int maxBytes, int maxRecords, int[] slots, out int offset, out int length)
    {
        if (maxBytes < 32 || maxRecords < 1 || maxRecords > slots.Length) throw new ArgumentOutOfRangeException("maxBytes");
        offset = length = 0; int first = -1;
        for (int i = 0; i < ids.Length; i++) if ((states[i] == ResourceState.Queued || states[i] == ResourceState.Failed) &&
            references[i] > 0 && retries[i] <= now && catalog.Get(ids[i]).Length <= maxBytes &&
            (first < 0 || priorities[i] < priorities[first] || priorities[i] == priorities[first] && touched[i] < touched[first])) first = i;
        if (first < 0) return 0;
        var initial = catalog.Get(ids[first]); offset = initial.Offset; length = initial.Length;
        slots[0] = first; states[first] = ResourceState.Reading; int count = 1;
        // Grow only forward, respecting both physical range and priority class.
        for (int pass = 1; pass < maxRecords; pass++)
        {
            int next = -1, end = offset + length;
            for (int i = 0; i < ids.Length; i++) if ((states[i] == ResourceState.Queued || states[i] == ResourceState.Failed) &&
                references[i] > 0 && retries[i] <= now && priorities[i] <= priorities[first])
            {
                var r = catalog.Get(ids[i]);
                if (r.Offset >= end && r.Offset - end <= 32 && r.Length <= maxBytes - (r.Offset - offset) &&
                    (next < 0 || r.Offset < catalog.Get(ids[next]).Offset)) next = i;
            }
            if (next < 0) break;
            var record = catalog.Get(ids[next]); slots[count++] = next;
            length = record.Offset + record.Length - offset; states[next] = ResourceState.Reading;
        }
        return count;
    }
    public int RequiredBatchBytes(int now,int configured)
    {
        int first=-1;
        for(int i=0;i<ids.Length;i++)if((states[i]==ResourceState.Queued||states[i]==ResourceState.Failed)&&
            references[i]>0&&retries[i]<=now&&(first<0||priorities[i]<priorities[first]||
            priorities[i]==priorities[first]&&touched[i]<touched[first]))first=i;
        if(first<0)return configured;
        int bytes=catalog.Get(ids[first]).Length;
        return bytes>configured?bytes:configured;
    }
    public bool Complete(int slot, int generation, bool valid, int now)
    {
        if (states[slot] == ResourceState.Retiring)
        { states[slot] = ResourceState.Missing; Stale++; return false; }
        if (generations[slot] != generation) { Stale++; return false; }
        if (references[slot] == 0) { states[slot] = ResourceState.Missing; Stale++; return false; }
        if (states[slot] != ResourceState.Reading) throw new InvalidOperationException("Resource is not reading.");
        states[slot] = valid ? ResourceState.Ready : ResourceState.Failed;
        if (!valid) { retries[slot] = now + 20; Retries++; }
        return valid;
    }
    public bool CanPublish(int slot, int physicalBytes)
        => physicalBytes > 0 && states[slot] == ResourceState.Ready && references[slot] > 0 && physicalBytes <= BudgetBytes - ResidentBytes;
    public void Publish(int slot, int physicalBytes)
    {
        if (!CanPublish(slot, physicalBytes)) throw new InvalidOperationException("Resource publication exceeds demand/budget.");
        states[slot] = ResourceState.Resident; pins[slot] = physicalBytes;
        ResidentBytes += physicalBytes; Published++;
    }
    public void Pin(int slot, int frame) { if (states[slot] == ResourceState.Resident) fences[slot] = frame; }
    public int EvictionCandidate(int completedFrame, ResourcePriority incoming = ResourcePriority.Optional)
    {
        int best = -1;
        for (int i = 0; i < ids.Length; i++) if (states[i] == ResourceState.Resident &&
            (references[i] == 0 || priorities[i] > (int)incoming) &&
            fences[i] <= completedFrame &&
            (best < 0 || priorities[i] > priorities[best] || priorities[i] == priorities[best] && touched[i] < touched[best])) best = i;
        return best;
    }
    public void Evict(int slot, int completedFrame, ResourcePriority incoming = ResourcePriority.Optional)
    {
        if (states[slot] != ResourceState.Resident || references[slot] != 0 && priorities[slot] <= (int)incoming ||
            fences[slot] > completedFrame)
            throw new InvalidOperationException("Resource is demanded or in flight.");
        ResidentBytes -= pins[slot]; pins[slot] = 0; states[slot] = ResourceState.Missing;
        generations[slot] = nextGeneration++; Evicted++;
    }
    public void ReleaseRetired(int slot)
    {
        if (states[slot] != ResourceState.Retiring) throw new InvalidOperationException("Resource is not retiring.");
        states[slot] = ResourceState.Missing;
    }
    public void Defer(int slot, int now)
    {
        if (states[slot] != ResourceState.Ready) throw new InvalidOperationException("Only ready work can be deferred.");
        states[slot] = ResourceState.Failed; retries[slot] = now + 30; Rejected++;
    }
}
