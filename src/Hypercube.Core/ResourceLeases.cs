namespace Hypercube;

// Each acquisition has its own generation-checked ticket. Copies of a released
// ticket cannot consume another acquisition's reference count.
public readonly struct ResourceLease
{
    internal readonly int Owner,Ticket,Generation;
    internal ResourceLease(int owner,int ticket,int generation){Owner=owner;Ticket=ticket;Generation=generation;}
}
public sealed class ResourceLeases
{
    private static int owners;
    private readonly int owner=++owners;
    private readonly int[] keys,sizes,references,lastUse,tickets,generations;
    private readonly bool[] occupied,active;
    public int Bytes {get;private set;}
    public int ActiveLeases {get;private set;}
    public int Budget {get;}
    public int Capacity=>keys.Length;
    public ResourceLeases(int capacity,int leaseCapacity,int byteBudget)
    {
        if(capacity<1||leaseCapacity<1||byteBudget<0)throw new ArgumentOutOfRangeException();Budget=byteBudget;
        keys=new int[capacity];sizes=new int[capacity];references=new int[capacity];lastUse=new int[capacity];occupied=new bool[capacity];tickets=new int[leaseCapacity];generations=new int[leaseCapacity];active=new bool[leaseCapacity];
    }
    public int Find(int key){for(int i=0;i<Capacity;i++)if(occupied[i]&&keys[i]==key)return i;return -1;}
    public int Register(int key,int bytes)
    {
        if(key<0||bytes<0)throw new ArgumentOutOfRangeException();if(Find(key)>=0)throw new InvalidOperationException("Duplicate resource key");
        if(bytes>Budget-Bytes)throw new InvalidOperationException("Resource byte budget exhausted");
        for(int i=0;i<Capacity;i++)if(!occupied[i]){occupied[i]=true;keys[i]=key;sizes[i]=bytes;lastUse[i]=-1;Bytes+=bytes;return i;}
        throw new InvalidOperationException("Resource slots exhausted");
    }
    public ResourceLease Acquire(int key)
    {
        int slot=Find(key);if(slot<0)throw new InvalidOperationException("Unknown resource");
        for(int i=0;i<active.Length;i++)if(!active[i]){if(generations[i]==int.MaxValue)continue;active[i]=true;tickets[i]=slot;generations[i]++;references[slot]++;ActiveLeases++;return new ResourceLease(owner,i,generations[i]);}
        throw new InvalidOperationException("Lease tickets exhausted");
    }
    public int Slot(ResourceLease lease)
    {
        int t=lease.Ticket;if(lease.Owner!=owner||(uint)t>=active.Length||!active[t]||generations[t]!=lease.Generation)throw new InvalidOperationException("Stale or foreign lease");return tickets[t];
    }
    public void Release(ResourceLease lease){int slot=Slot(lease);active[lease.Ticket]=false;references[slot]--;ActiveLeases--;}
    public void Pin(ResourceLease lease,int submittedFrame)
    {if(submittedFrame<0)throw new ArgumentOutOfRangeException();int slot=Slot(lease);lastUse[slot]=Math.Max(lastUse[slot],submittedFrame);}
    public int UnusedCandidate(int completedFrame)
    {
        if(completedFrame< -1)throw new ArgumentOutOfRangeException();
        for(int i=0;i<Capacity;i++)if(occupied[i]&&references[i]==0&&lastUse[i]<=completedFrame)return i;return -1;
    }
    public int RemoveUnused(int completedFrame)
    {int i=UnusedCandidate(completedFrame);if(i>=0){occupied[i]=false;Bytes-=sizes[i];}return i;}
    public bool CanDispose(int completedFrame)
    {if(ActiveLeases!=0)return false;for(int i=0;i<Capacity;i++)if(occupied[i]&&lastUse[i]>completedFrame)return false;return true;}
}
