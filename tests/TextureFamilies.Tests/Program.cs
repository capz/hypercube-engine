using Hypercube;

static void Check(bool value,string label){if(!value)throw new Exception(label);}
byte[] raw=File.ReadAllBytes(args.Length==0?"out/progressive/textures.hcp":args[0]);
var header=raw[..64];int length=AssetCatalog.MetadataBytes(header);var table=raw[64..(64+length)];
var catalog=new AssetCatalog(header,table,raw.Length);
Check(catalog.Version==2&&catalog.FamilyCount==4,"Versioned family index");
var rgba=catalog.Family("terrain/rgba");var pair=catalog.Family("faces/pair");
Check(rgba.GetLevel(1).LogicalLevel==2&&rgba.IndexForLogical(3)==1,"Sparse exponents remain authored");
Check(pair.IsQualityPair&&pair.Count==2&&catalog.Get(pair.GetLevel(1).RecordId).Height==10,"Arbitrary low/high dimensions");
Check(rgba.GetLevel(0).Uv.OffsetU>0,"Per-level gutters preserved");
for(int id=1;id<=catalog.FamilyCount;id++)
{
    var f=catalog.Family(id);
    for(int k=0;k<f.Count;k++)
    {
        var r=catalog.Get(f.GetLevel(k).RecordId);
        Check(Binary.Crc(raw,r.Offset,r.Length)==r.Checksum,"Independent payload CRC");
        Check(r.Parent==(k+1<f.Count?f.GetLevel(k+1).RecordId:0),"Parent fallback chain");
    }
}
var a=new StreamedMaterialTexture(rgba);var b=new StreamedMaterialTexture(rgba);var demand=new TextureFamilyDemand(catalog);
demand.BeginFrame(0);demand.Demand(a,new TextureFootprint(2,2));demand.Demand(b,new TextureFootprint(1000,1000));
Check(demand.TargetIndex(rgba.Id)==0,"Finest shared visible demand");
Check(demand.RequestedIndex(rgba.Id,-1)==rgba.CoarseIndex,"Initial readiness is coarse before costly promotion");
Check(demand.RequestedIndex(rgba.Id,rgba.CoarseIndex)==0,"Promotion can skip intermediate authored levels");
demand.BeginFrame(10);demand.Demand(a,new TextureFootprint(2,2));
Check(demand.TargetIndex(rgba.Id)>0,"Removed consumer releases demand");
demand.Demand(b,new TextureFootprint(1000,1000),ResourcePriority.Prefetch);
Check(demand.TargetIndex(rgba.Id)>0,"Prefetch cannot raise visible quality");
var p=new StreamedMaterialTexture(pair);
Check(TextureLodSelector.Select(p,new TextureFootprint(1,1),0)==0,"Distance pair targets high directly");
p.Policy=TextureQualityPolicy.CoarseOnly;Check(TextureLodSelector.Select(p,default,1)==1,"Coarse only pair");
p.Policy=TextureQualityPolicy.FullQuality;Check(TextureLodSelector.Select(p,default,2)==0,"Explicit full quality pair");
var projected=TextureFootprint.Project(2,4,20,2,60,480,2,4);
Check(Math.Abs(projected.PixelsU-projected.PixelsV)<.001,"Both axes and repeats");
Check(TextureFootprint.Project(2,2,20,10,60,480).PixelsU>TextureFootprint.Project(2,2,20,0,60,480).PixelsU,"Nearest bound is conservative");
var stable=new StreamedMaterialTexture(rgba);Check(TextureLodSelector.Select(stable,new TextureFootprint(128,64),0)==1,"Projected sparse level");
for(int frame=1;frame<100;frame++)Check(TextureLodSelector.Select(stable,new TextureFootprint(frame%2==0?180:182,90),frame)==1,"Hysteresis suppresses oscillation");
Check(TextureLodSelector.Select(stable,new TextureFootprint(240,120),100)==0,"Sparse hysteresis uses the actual density transition plus 25 percent");
Check(TextureLodSelector.Select(stable,new TextureFootprint(1000,1000),100)==0,"Promotion can skip levels");
Check(TextureLodSelector.Select(stable,new TextureFootprint(1,1),101)==0,"Minimum dwell");
Check(TextureLodSelector.Select(stable,new TextureFootprint(1,1),110)==rgba.CoarseIndex,"Settled demotion");
stable.MinimumLogicalLevel=2;stable.Policy=TextureQualityPolicy.FullQuality;
Check(TextureLodSelector.Select(stable,default,120)==1,"Explicit quality cap");
var random=new Random(831);
foreach(uint bits in new uint[]{0,0x80000000,1,0x007fffff,0x00800000,0x3f800000,0x7f7fffff,0x7f800000,0xff800000,0x7fc00000})CheckFloat(bits);
for(int i=0;i<10000;i++)CheckFloat((uint)random.Next()|(random.Next(2)==0?0:0x80000000u));
void CheckFloat(uint bits)
{
    float decoded=Binary.F32(BitConverter.GetBytes(bits),0),expected=BitConverter.Int32BitsToSingle(unchecked((int)bits));
    Check(float.IsNaN(expected)?float.IsNaN(decoded):BitConverter.SingleToInt32Bits(decoded)==unchecked((int)bits),"Portable float metadata decoding preserves IEEE finite bits, signed zero and infinity");
}
for(int trial=0;trial<2000;trial++)
{
    var bad=(byte[])table.Clone();bad[random.Next(bad.Length)]^=(byte)random.Next(1,256);
    try{new AssetCatalog(header,bad,raw.Length);throw new Exception("Corrupt metadata accepted");}catch(IOException){}
}
// Recompute CRC so semantic checks cannot hide behind the checksum gate.
void RejectWord(int at,uint value)
{
    var bad=(byte[])table.Clone();var h=(byte[])header.Clone();BitConverter.GetBytes(value).CopyTo(bad,at);BitConverter.GetBytes(Binary.Crc(bad,0,bad.Length)).CopyTo(h,24);
    try{new AssetCatalog(h,bad,raw.Length);throw new Exception("Invalid family accepted");}catch(IOException){}
}
int familyStart=catalog.Count*64;
RejectWord(familyStart+4,2);RejectWord(familyStart+20,1);RejectWord(familyStart+24,1);
RejectWord(familyStart+4*128+8,0x7fc00000);RejectWord(36,999);
var scheduler=new ResourceScheduler(catalog,8,2*1024*1024);scheduler.BeginDemand();
int coarse=rgba.GetLevel(rgba.CoarseIndex).RecordId,fine=rgba.GetLevel(0).RecordId;
int slot=scheduler.Demand(coarse,ResourcePriority.Coarse);var batch=new int[1];
Check(scheduler.BeginBatch(0,1024*1024,1,batch,out _,out _)==1,"Coarse read issued");
Check(scheduler.Complete(slot,scheduler.Generation(slot),true,0),"Coarse CRC valid");scheduler.Publish(slot,catalog.Get(coarse).Length);
for(int i=0;i<100;i++)
{
    scheduler.BeginDemand();scheduler.Demand(coarse,ResourcePriority.Coarse);int upgrade=scheduler.Demand(fine,ResourcePriority.Visible);
    Check(scheduler.BeginBatch(i*30,1024*1024,1,batch,out _,out _)==1,"Upgrade issued");int generation=scheduler.Generation(upgrade);
    Check(scheduler.Cancel(fine)&&scheduler.State(upgrade)==ResourceState.Retiring,"Canceled read must drain");
    Check(!scheduler.Complete(upgrade,generation,true,i*30),"Stale generation never publishes");
    Check(scheduler.State(slot)==ResourceState.Resident&&scheduler.ResidentBytes==catalog.Get(coarse).Length,"Churn preserves fallback ownership");
}
var controller=new TextureBudgetController();
Check(controller.Next(1000,0,0).UploadBytes==32768,"Budget starts bounded");
Check(controller.Next(1000,1200,0).UploadBytes==16384,"Slow pump reduces chunk work");
Check(!controller.Next(1000,100,12000).AllowPrefetch&&controller.ChunkBytes==8192,"Slow storage reduces speculative work");
Check(controller.Next(1000,100,100,audioStarved:true).UploadBytes==0,"Audio pressure pauses issuance");
Check(controller.Next(1000,100,100,criticalQueued:1).UploadBytes==0,"Gameplay pressure pauses issuance");
Check(controller.Next(7999,100,100).UploadBytes==0,"Insufficient CPU headroom pauses issuance");
TextureWorkBudget budget=default;
for(int i=0;i<32;i++)budget=controller.Next(1000,100,100);
Check(budget.UploadBytes==32768&&budget.AllowPrefetch&&controller.Growths==2,"Stable headroom recovers gradually within cap");
var hint=new TexturePrefetchHint(.9f,.5f,.8f,128);
Check(TexturePrefetchPolicy.Likely(hint,budget,65536,1024),"Likely near-future visible content may prefetch");
Check(!TexturePrefetchPolicy.Likely(new TexturePrefetchHint(.1f,.5f,.8f,128),budget,65536,1024),"Unlikely content does not prefetch");
Check(!TexturePrefetchPolicy.Likely(hint,budget,1024,1024),"Prefetch preserves memory headroom");
Check(!TexturePrefetchPolicy.Likely(hint,new TextureWorkBudget(0,1),65536,1024),"Paused work cannot speculate");
var tailHeader=new byte[32];
void TailWord(int at,int value)=>BitConverter.GetBytes(value).CopyTo(tailHeader,at);
TailWord(0,0x58544e44);TailWord(4,2);TailWord(8,6);TailWord(12,16);TailWord(16,8);TailWord(20,832);TailWord(24,5);
var tailInfo=ResidentMipTailInfo.Read(tailHeader,864);Check(tailInfo.NativeBytes==832&&tailInfo.Levels==5,"Bounded rectangular contiguous tail");
foreach(int at in new[]{0,4,8,12,16,20,24,28})
{
    var bad=(byte[])tailHeader.Clone();bad[at]^=128;
    try{ResidentMipTailInfo.Read(bad,864);throw new Exception("Invalid mip tail accepted");}catch(IOException){}
}
try{ResidentMipTailInfo.Read(tailHeader,864,maximumNativeBytes:512);throw new Exception("Tail byte ceiling ignored");}catch(IOException){}
Console.WriteLine("Texture families: v2/CRC/sparse/pair/gutters/shared demand/density/hysteresis/dwell/caps/fuzz/budgets/prefetch PASS");
