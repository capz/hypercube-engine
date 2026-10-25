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
