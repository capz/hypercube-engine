using Hypercube;

static void Check(bool value, string label) { if (!value) throw new Exception(label); }
static void Reject(Action action, string label)
{
    try { action(); } catch (IOException) { return; }
    throw new Exception("Did not reject: " + label);
}
byte[] raw = File.ReadAllBytes(args.Length == 0 ? "out/assets/world.hcp" : args[0]);
var header = raw[..32];
int count = Binary.I32(header, 8);
var index = raw[32..(32 + count * 64)];
var catalog = new AssetCatalog(header, index, raw.Length);
Check(Binary.Crc(System.Text.Encoding.ASCII.GetBytes("123456789"), 0, 9) == 0xcbf43926u, "CRC known vector");
foreach (int format in new[] { 6, 14 }) foreach (int group in new[] { 1, 2, 4 })
{
    int id = catalog.Texture(format, group, 0, 0);
    Check(id > 0, "All texture layouts/formats exist");
    int depth = 0;
    for (; id != 0; id = catalog.Get(id).Parent)
    {
        var record = catalog.Get(id);
        Check(Binary.Crc(raw, record.Offset, record.Length) == record.Checksum, "Packed payload identity");
        depth++;
    }
    Check(depth == 4, "Complete explicit fallback chain");
}
byte[] bad = (byte[])header.Clone(); bad[4] = 2;
Reject(() => new AssetCatalog(bad, index, raw.Length), "version");
bad = (byte[])index.Clone(); bad[0] ^= 1;
Reject(() => new AssetCatalog(header, bad, raw.Length), "index CRC");
Reject(() => new AssetCatalog(header, index, raw.Length - 1), "truncation");
var scheduler = new ResourceScheduler(catalog, 8, 32768);
int texture = catalog.Texture(6, 1, 3, 0), collision = 0;
for (int i = 1; i <= count; i++) if (catalog.Get(i).Kind == 3) { collision = i; break; }
scheduler.BeginDemand();
int visualSlot = scheduler.Demand(texture, ResourcePriority.Visible);
int criticalSlot = scheduler.Demand(collision, ResourcePriority.Gameplay);
int[] batch = new int[8];
Check(scheduler.BeginBatch(0, 32768, 4, batch, out _, out _) > 0 && batch[0] == criticalSlot, "Gameplay reads precede detail");
int generation = scheduler.Generation(criticalSlot);
scheduler.BeginDemand(); scheduler.CancelUnneeded();
Check(!scheduler.Complete(criticalSlot, generation, true, 1), "Obsolete read cannot publish");
scheduler.BeginDemand();
int slot = scheduler.Demand(texture, ResourcePriority.Visible);
Check(scheduler.BeginBatch(1, 32768, 4, batch, out _, out _) == 1, "Single page read");
int fresh = scheduler.Generation(slot);
Check(!scheduler.Complete(slot, generation, true, 1) && scheduler.State(slot) == ResourceState.Reading, "Old generation cannot clear current work");
Check(scheduler.Complete(slot, fresh, true, 1), "Valid completion");
scheduler.Publish(slot, 2048); scheduler.Pin(slot, 10);
scheduler.BeginDemand(); scheduler.CancelUnneeded();
Check(scheduler.EvictionCandidate(9) == -1, "GPU frame pins prevent reuse");
Check(scheduler.EvictionCandidate(10) == slot, "Completed frame releases reuse");
scheduler.Evict(slot, 10); Check(scheduler.ResidentBytes == 0, "Exact physical accounting");
// Four independently resident pages arrive through a single logical read.
scheduler = new ResourceScheduler(catalog, 8, 32768); scheduler.BeginDemand();
for (int i = 0; i < 4; i++) scheduler.Demand(texture + i, ResourcePriority.Visible);
Check(scheduler.BeginBatch(0, 8192, 8, batch, out int offset, out int length) == 4 && length == 4096, "Coalescing is independent of page allocation size");
for (int i = 0; i < 4; i++)
{
    slot = batch[i]; fresh = scheduler.Generation(slot);
    Check(scheduler.Complete(slot, fresh, i != 0, 0) == (i != 0), "Checksum failure is isolated per record");
    if (i != 0) scheduler.Publish(slot, 2048);
}
Check(scheduler.BeginBatch(19, 8192, 8, batch, out _, out _) == 0, "Failure backoff");
Check(scheduler.BeginBatch(20, 8192, 8, batch, out _, out _) == 1, "Failed read retries");
slot = batch[0]; scheduler.Complete(slot, scheduler.Generation(slot), true, 20); scheduler.Publish(slot, 2048);
Check(scheduler.ResidentBytes == 8192, "Both front/back buffers counted");
// Visible work can displace demanded prefetch, but cannot displace gameplay.
scheduler = new ResourceScheduler(catalog, 2, 2048); scheduler.BeginDemand();
slot = scheduler.Demand(texture, ResourcePriority.Prefetch);
scheduler.BeginBatch(0, 8192, 1, batch, out _, out _);
scheduler.Complete(slot, scheduler.Generation(slot), true, 0); scheduler.Publish(slot, 2048);
Check(scheduler.EvictionCandidate(0, ResourcePriority.Visible) == slot, "Prefetch does not pin visible capacity");
scheduler.Evict(slot, 0, ResourcePriority.Visible);
var cells = new CellReadiness();
Check(!cells.CanEnter(0), "Unready gameplay boundary blocks movement");
cells.CoarseReady(0); Check(!cells.CanEnter(0), "Visual readiness alone cannot permit movement");
cells.CollisionReady(0); Check(cells.CanEnter(0), "Collision and fallback permit entry");
int lod = 1;
for (int i = 0; i < 100; i++) lod = ScenePolicy.Lod(i % 2 == 0 ? 9.1f : 8.9f, lod);
Check(lod == 1, "LOD hysteresis prevents oscillation");
Check(ScenePolicy.Lod(7, 1) == 0 && ScenePolicy.Lod(19, 1) == 2, "LOD thresholds");
Console.WriteLine("Hypercube catalog, hierarchy, priority, batching, generations, fences, retries, budgets and readiness: PASS");
// Verify tree indices cover every cell once; no parent can omit a child region.
var tree = new SceneTree(); var leaves = new HashSet<int>();
for (int cell = 0; cell < 24; cell++)
{
    int node = tree.Leaf(cell);
    Check(tree.Span[node] == 1 && tree.X[node] == cell % 6 && tree.Z[node] == cell / 6, "Cell hierarchy lookup");
    Check(leaves.Add(node), "Unique leaf");
}
Check(tree.SubtreeSize(0) == 85, "Complete hierarchy size");
// Compare conservative frustum rejection with independent corner projection.
var random = new Random(1947);
for (int trial = 0; trial < 2000; trial++)
{
    float x = random.Next(-30, 50), y = random.Next(-10, 20), z = random.Next(-20, 40);
    float dx = random.Next(1, 8), dy = random.Next(1, 8), dz = random.Next(1, 8);
    bool cornerVisible = false;
    for (int k = 0; k < 8; k++)
    {
        float px = ((k & 1) == 0 ? x : x + dx) - 12;
        float py = ((k & 2) == 0 ? y : y + dy) - 12;
        float pz = ((k & 4) == 0 ? z : z + dz) - 20;
        float depth = -.6f * py - .8f * pz, up = .8f * py - .6f * pz;
        if (depth >= .1f && depth <= 80 && Math.Abs(px) <= depth * .52056705f * 4 / 3 && Math.Abs(up) <= depth * .52056705f) cornerVisible = true;
    }
    Check(!cornerVisible || Visibility.Frustum(x, y, z, x + dx, y + dy, z + dz, 12, 4), "Frustum never rejects a visible corner");
}
Check(!Visibility.Occluded(8,0,8,12,6,9,12,4,8,0,9,16,6), "Occluder cannot hide itself");
Check(!Visibility.Occluded(0,0,0,4,1,4,12,4,8,0,9,16,6), "Outside wall silhouette remains visible");
Console.WriteLine("Scene hierarchy and conservative visibility: PASS");

// A full table of obsolete queued requests must not permanently starve new work.
scheduler = new ResourceScheduler(catalog, 1, 32768); scheduler.BeginDemand();
scheduler.Demand(texture, ResourcePriority.Visible); scheduler.BeginDemand();
Check(scheduler.Demand(collision, ResourcePriority.Gameplay) >= 0, "Unissued obsolete request releases its slot");
Check(scheduler.Find(texture) == -1, "Reclaimed lookup cannot alias new request");

scheduler = new ResourceScheduler(catalog, 2, 32768); scheduler.BeginDemand();
slot = scheduler.Demand(texture, ResourcePriority.Visible);
scheduler.BeginBatch(0,8192,1,batch,out _,out _);
scheduler.Complete(slot,scheduler.Generation(slot),true,0);
Check(scheduler.Cancel(texture)&&scheduler.State(slot)==ResourceState.Retiring,"Staged request retains ownership until completion");
Check(!scheduler.CanPublish(slot,2048),"Canceled staging cannot become a resident asset");
scheduler.ReleaseRetired(slot);Check(scheduler.Find(texture)==-1,"Retired staging can release its slot");

// Histogram quantization is conservative, including frames beyond the last bin.
var metrics = new FrameMetrics();
Check(metrics.Percentile(99) == 0 && metrics.MeanUs == 0, "Empty timing window");
for (int i = 0; i < 98; i++) metrics.Record(16000);
metrics.Record(33000); metrics.Record(900000);
Check(metrics.Percentile(50) == 17 && metrics.Percentile(95) == 17 && metrics.Percentile(99) == 34,
    "Percentiles use nearest rank, not the maximum or average");
Check(metrics.Percentile(100) == 901 && metrics.MaximumUs == 900000 && metrics.MeanUs == 25010,
    "Overflow and mean retain slow frames");
metrics.Reset(); Check(metrics.Samples == 0 && metrics.MaximumUs == 0 && metrics.Percentile(99) == 0,
    "Timing windows do not leak between cases");
Check(ScenePolicy.DemandFallback(false, true) && ScenePolicy.DemandFallback(true, false) &&
      !ScenePolicy.DemandFallback(true, true), "Adaptive demand retains fallback while detail is unavailable");
// Prioritizing fallback must never displace gameplay or publish stale work.
scheduler = new ResourceScheduler(catalog, 8, 32768); scheduler.BeginDemand();
int detailSlot = scheduler.Demand(texture, ResourcePriority.Visible);
int coarseSlot = scheduler.Demand(texture + 1, ResourcePriority.Coarse);
criticalSlot = scheduler.Demand(collision, ResourcePriority.Gameplay);
scheduler.BeginBatch(0, 8192, 1, batch, out _, out _);
Check(batch[0] == criticalSlot, "Coarse-first keeps collision first");
scheduler.Complete(criticalSlot, scheduler.Generation(criticalSlot), true, 0); scheduler.Publish(criticalSlot, 44);
scheduler.BeginBatch(0, 8192, 1, batch, out _, out _);
Check(batch[0] == coarseSlot, "Fallback is read before fine detail");
scheduler.BeginDemand(); scheduler.Demand(texture, ResourcePriority.Visible); scheduler.CancelUnneeded();
Check(!scheduler.Complete(coarseSlot, scheduler.Generation(coarseSlot), true, 0), "Withdrawn fallback cannot publish");
Check(scheduler.Find(texture) == detailSlot, "Fine request survives fallback cancellation");
Console.WriteLine("Performance timing and adaptive/coarse-first demand: PASS");

// Pixel-equivalence oracle independent of the run encoder, including clamped values.
var words=new int[32];var bits=new bool[512];var encoded=new bool[512];
for(int trial=0;trial<100;trial++)
{
    for(int i=0;i<32;i++)words[i]=trial==0?0:trial==1?65535:random.Next(-10000,80000);
    for(int i=0;i<512;i++){int v=Math.Clamp(words[i/16],0,65535);bits[i]=(v&(1<<(i%16)))!=0;}
    int rectangles=1;
    for(int at=0;at<512;)
    {
        int n=TelemetryRuns.Length(words,at);bool lit=TelemetryRuns.Lit(words,at);
        Check(n>0&&at%64+n<=64,"Telemetry run cannot cross a raster row");
        for(int i=0;i<n;i++)encoded[at+i]=lit;
        if(lit)rectangles++;at+=n;
    }
    Check(bits.SequenceEqual(encoded),"Compact telemetry preserves every bit/pixel");
    Check(rectangles<=257,"Compact telemetry bounds native rectangle submissions");
}
Console.WriteLine("Compact telemetry pixel equivalence: PASS");

var phaseProfile=new FrameProfile();
phaseProfile.Current[FrameProfile.Stream]=100;
phaseProfile.Current[FrameProfile.Decode]=200;
phaseProfile.Current[FrameProfile.Create]=300;
phaseProfile.Current[FrameProfile.Draw]=400;
phaseProfile.Current[FrameProfile.Present]=500;
phaseProfile.Current[FrameProfile.Yield]=600;
phaseProfile.Current[FrameProfile.Hud]=700;
phaseProfile.Current[FrameProfile.Prepare]=800;
phaseProfile.Record(4000,4,2,3,8,10,500);
Check(phaseProfile.Mean(FrameProfile.Other)==400,"Exclusive phases leave the correct unattributed interval");
phaseProfile.Record(5000,2,1,1,6,8,300);
Check(phaseProfile.Frames.MeanUs==4500&&phaseProfile.Mean(FrameProfile.Other)==900&&
      phaseProfile.Maximum(FrameProfile.Other)==1400,"Phase means/maxima retain frame alignment");
Check(phaseProfile.SelectedPermille==500&&phaseProfile.Quality100(0)==300&&phaseProfile.DetailFrames==2,
    "Detail readiness weights cells, not an average of frame percentages");
int rejectedBefore=phaseProfile.Frames.Samples;
try{phaseProfile.Record(3000,4,2,3,8,10,500);throw new Exception("Accepted overlapping phases");}
catch(InvalidOperationException){}
Check(phaseProfile.Frames.Samples==rejectedBefore,"Invalid profiling sample is atomic");
try{phaseProfile.Record(5000,2,3,2,8,10,500);throw new Exception("Accepted impossible readiness");}
catch(InvalidOperationException){}
phaseProfile.Reset();Check(phaseProfile.Frames.Samples==0&&phaseProfile.SelectedPermille==0&&
    phaseProfile.Current[FrameProfile.Draw]==0,"New profiling cases clear timings and quality");
Console.WriteLine("Exclusive frame profiling and detail readiness: PASS");

// Cached scene masks must exactly match the original ragged-region scans.
for(int c=0;c<24;c++)Check(tree.Leaves[c]==tree.Leaf(c),"Cached leaf identity");
for(int node=0;node<85;node++)
{
    int mask=0;
    for(int z=tree.Z[node];z<tree.Z[node]+tree.Span[node]&&z<4;z++)
        for(int x=tree.X[node];x<tree.X[node]+tree.Span[node]&&x<6;x++)mask|=1<<(z*6+x);
    Check(mask==tree.CellMasks[node],"Subtree mask matches old clipped scan");
    for(int trial=0;trial<100;trial++)
    {
        int visibleBits=random.Next(1<<24),detailBits=random.Next(1<<24)&visibleBits;
        bool anyVisible=false,anyDetail=false;
        for(int c=0;c<24;c++)if((mask&(1<<c))!=0)
        {anyVisible|=(visibleBits&(1<<c))!=0;anyDetail|=(detailBits&(1<<c))!=0;}
        Check(anyVisible==((tree.CellMasks[node]&visibleBits)!=0)&&anyDetail==((tree.CellMasks[node]&detailBits)!=0),
            "Cached traversal preserves visible and detail decisions");
    }
}

// Both demand validation paths preserve state, invalid-ID behavior and generations.
var slowPolicy=new ResourceScheduler(catalog,8,32768);
var fastPolicy=new ResourceScheduler(catalog,8,32768){ValidateIdsOnly=true};
for(int f=0;f<80;f++)
{
    slowPolicy.BeginDemand();fastPolicy.BeginDemand();
    for(int i=0;i<6;i++)
    {
        int id=i==0?collision:texture+(f/4+i)%4;
        var priority=i==0?ResourcePriority.Gameplay:(i%2==0?ResourcePriority.Prefetch:ResourcePriority.Visible);
        Check(slowPolicy.Demand(id,priority)==fastPolicy.Demand(id,priority),"Fast ID demand slot parity");
    }
    slowPolicy.CancelUnneeded();fastPolicy.CancelUnneeded();
    int[] slowBatch=new int[8],fastBatch=new int[8];
    int n=slowPolicy.BeginBatch(f,8192,4,slowBatch,out int so,out int sl);
    int nf=fastPolicy.BeginBatch(f,8192,4,fastBatch,out int fo,out int fl);
    Check(n==nf&&so==fo&&sl==fl,"Fast ID scheduling parity");
    for(int i=0;i<n;i++)
    {
        int a=slowBatch[i],b=fastBatch[i];bool valid=f%3!=0;
        Check(slowPolicy.Complete(a,slowPolicy.Generation(a),valid,f)==fastPolicy.Complete(b,fastPolicy.Generation(b),valid,f),"Fast ID completion parity");
        if(valid){slowPolicy.Publish(a,44);fastPolicy.Publish(b,44);}
    }
    for(int i=0;i<8;i++)Check(slowPolicy.Id(i)==fastPolicy.Id(i)&&slowPolicy.State(i)==fastPolicy.State(i)&&
        slowPolicy.Generation(i)==fastPolicy.Generation(i),"Fast ID full policy parity");
}
foreach(int id in new[]{-1,0,catalog.Count+1})
{
    foreach(var policy in new[]{slowPolicy,fastPolicy})
    {try{policy.Demand(id,ResourcePriority.Visible);throw new Exception("Invalid demand ID accepted");}catch(ArgumentOutOfRangeException){}}
}
var plan=new DemandPlan(4);int[] order={1,2};int[] levels=new int[24];levels[1]=1;levels[2]=2;
plan.Begin(order,2,levels,77,false);plan.Add(texture,ResourcePriority.Visible);plan.Add(texture,ResourcePriority.Prefetch);plan.Seal();
Check(plan.Matches(order,2,levels,77,false)&&plan.Count==2&&plan.Ids[0]==plan.Ids[1]&&
    plan.Priorities[0]!=plan.Priorities[1],"Demand plans preserve duplicate requests and priorities");
levels[1]=0;Check(!plan.Matches(order,2,levels,77,false),"LOD change invalidates plan");levels[1]=1;
Check(!plan.Matches(new[]{2,1},2,levels,77,false)&&!plan.Matches(order,1,levels,77,false)&&
    !plan.Matches(order,2,levels,78,false)&&!plan.Matches(order,2,levels,77,true),"Plan key invalidation");
plan.Begin(order,2,levels,77,false);for(int i=0;i<5;i++)plan.Add(texture+i%4,ResourcePriority.Visible);plan.Seal();
Check(plan.Overflow&&!plan.Matches(order,2,levels,77,false),"Overflowed plan cannot replay truncated demand");
Console.WriteLine("Prep optimization masks, scheduler parity and demand-plan invalidation: PASS");

// Residency/generation changes do not stale a plan: only IDs are replayed through Demand.
plan.Begin(order,1,levels,77,false);plan.Add(texture,ResourcePriority.Visible);plan.Seal();
var replayPolicy=new ResourceScheduler(catalog,1,32768);
int oldSlot=replayPolicy.Demand(texture,ResourcePriority.Visible);
replayPolicy.BeginBatch(0,8192,1,batch,out _,out _);int oldTag=replayPolicy.Generation(oldSlot);
replayPolicy.Complete(oldSlot,oldTag,true,0);replayPolicy.Publish(oldSlot,2048);
replayPolicy.BeginDemand();replayPolicy.Evict(oldSlot,0);
Check(plan.Matches(order,1,levels,77,false),"Stable ID plan survives residency changes");
for(int i=0;i<plan.Count;i++)replayPolicy.Demand(plan.Ids[i],plan.Priorities[i]);
Check(replayPolicy.State(replayPolicy.Find(texture))==ResourceState.Queued&&replayPolicy.Generation(oldSlot)!=oldTag,
    "Plan replay restores live demand with a fresh generation");

// First-free selection remains observable and deterministic through every
// missing-state transition, including capacities crossing a 32-slot word.
var freeSlotRandom=new Random(711);
foreach(int capacity in new[]{1,31,32,33,64,255,256})
{
    var policy=new ResourceScheduler(catalog,capacity,1024*1024);
    var wanted=new HashSet<int>();var issued=new int[8];
    for(int round=0;round<30;round++)
    {
        policy.BeginDemand();wanted.Clear();
        for(int step=0;step<capacity*2;step++)
        {
            int id=1+freeSlotRandom.Next(catalog.Count),expected=policy.Find(id);
            if(expected<0)for(int i=0;i<capacity;i++)if(policy.State(i)==ResourceState.Missing){expected=i;break;}
            if(expected<0)for(int i=0;i<capacity;i++)if(!wanted.Contains(policy.Id(i))&&
                (policy.State(i)==ResourceState.Queued||policy.State(i)==ResourceState.Failed)){expected=i;break;}
            int actual=policy.Demand(id,ResourcePriority.Visible);
            Check(actual==expected,"First-free bitmap agrees with independent slot scan");if(actual>=0)wanted.Add(id);
            if(step%5==0){int victim=policy.Id(freeSlotRandom.Next(capacity));if(policy.Cancel(victim))wanted.Remove(victim);}
        }
        policy.CancelUnneeded();
        int issuedCount=policy.BeginBatch(round*100,1024*1024,8,issued,out _,out _);
        for(int i=0;i<issuedCount;i++)
        {
            int at=issued[i],tag=policy.Generation(at),id=policy.Id(at);
            Check(!policy.Complete(at,tag+1,true,round*100),"Wrong generation leaves issued slot intact");
            if(i%4==0)
            {
                policy.Cancel(id);wanted.Remove(id);
                if(round%2==0)policy.ReleaseRetired(at);
                else Check(!policy.Complete(at,tag,true,round*100),"Retired completion frees slot");
            }
            else if(policy.Complete(at,tag,true,round*100))
            {if(i%3==0)policy.Defer(at,round*100);else policy.Publish(at,64);}
        }
        policy.BeginDemand();wanted.Clear();policy.CancelUnneeded();
        for(int i=0;i<capacity;i++)if(policy.State(i)==ResourceState.Resident)policy.Evict(i,int.MaxValue);
    }
}
// Stable nearest-first ordering must preserve the old repeated-minimum policy.
for(int trial=0;trial<1000;trial++)
{
    var onScreen=new bool[24];var distance=new float[24];var fast=new int[25];var slow=new int[24];
    fast[24]=-917;
    for(int i=0;i<24;i++){onScreen[i]=freeSlotRandom.Next(4)!=0;distance[i]=freeSlotRandom.Next(65)*.25f;}
    int countFast=DemandOrder.Build(fast,onScreen,distance),countSlow=0,chosen=0;
    for(int pass=0;pass<24;pass++)
    {
        int next=-1;
        for(int i=0;i<24;i++)if(onScreen[i]&&(chosen&(1<<i))==0&&(next<0||distance[i]<distance[next]))next=i;
        if(next<0||distance[next]>=12)break;slow[countSlow++]=next;chosen|=1<<next;
    }
    Check(countFast==countSlow&&fast[24]==-917,"Demand ordering preserves bounds and threshold");
    for(int i=0;i<countFast;i++)Check(fast[i]==slow[i],"Demand ordering preserves priorities and equal-distance ties");
}
var execution=new ExecutionMetrics();
for(int i=0;i<100;i++)execution.Record(10000,1000,6000);
Check(execution.Samples==100&&execution.Mean==3000&&execution.Maximum==3000&&execution.P99==3008,
    "Execution timing excludes only measured GPU and VI blocking");
Check(execution.GpuMean==1000&&execution.ViMean==6000,"Blocking timing remains separately visible");
try{execution.Record(1,1,1);throw new Exception("Accepted overlapping waits");}catch(InvalidOperationException){}
Check(execution.Samples==100,"Invalid timing does not corrupt the sample window");
execution.Reset();Check(execution.Samples==0&&execution.Mean==0&&execution.Maximum==0,"Restart clears execution history");
execution.Record(100000,0,0);Check(execution.P99==100000,"Overflow histogram retains maximum");
var stableIndex=(byte[])index.Clone();var stableCatalog=new AssetCatalog(header,stableIndex,raw.Length);
var beforeMutation=stableCatalog.Get(1);Array.Clear(stableIndex);
Check(stableCatalog.Get(1).Equals(beforeMutation),"Validated metadata remains immutable after input mutation");
Console.WriteLine("Free-slot transitions, stable demand ordering, cached metadata and execution accounting passed.");
