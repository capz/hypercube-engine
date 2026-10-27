namespace Hypercube;

// Cache stable IDs/priorities, never slots, generations, residency or retry state.
// Replay still visits the scheduler each frame and preserves duplicate requests/order.
public sealed class DemandPlan
{
    public readonly int[] Ids;
    public readonly ResourcePriority[] Priorities;
    private readonly int[] cells=new int[24],levels=new int[24];
    private int cellCount,configuration;
    private bool raw,valid,overflow;
    public int Count {get;private set;}
    public bool Overflow=>overflow;
    public DemandPlan(int capacity=256)
    {
        if(capacity<1)throw new System.ArgumentOutOfRangeException("capacity");
        Ids=new int[capacity];Priorities=new ResourcePriority[capacity];
    }
    public bool Matches(int[] order,int count,int[] lods,int config,bool isRaw)
    {
        if(!valid||count!=cellCount||configuration!=config||raw!=isRaw)return false;
        for(int i=0;i<count;i++)if(cells[i]!=order[i]||levels[i]!=lods[order[i]])return false;
        return true;
    }
    public void Begin(int[] order,int count,int[] lods,int config,bool isRaw)
    {
        if(count<0||count>24)throw new System.ArgumentOutOfRangeException("count");
        valid=overflow=false;Count=0;cellCount=count;configuration=config;raw=isRaw;
        for(int i=0;i<count;i++){cells[i]=order[i];levels[i]=lods[order[i]];}
    }
    public void Add(int id,ResourcePriority priority)
    {
        if(Count==Ids.Length){overflow=true;return;}
        Ids[Count]=id;Priorities[Count++]=priority;
    }
    public void Seal(){valid=!overflow;}
}
