using System;
using System.IO;
using System.Threading.Tasks;
using Dolphin.Audio;
using Dolphin.Diagnostics;
using Dolphin.Graphics;
using Dolphin.Input;
using Dolphin.Memory;
using Dolphin.Storage;
using Dolphin.Timing;
using Hypercube;
using Hypercube.GameCube;

internal static class Program
{
    private static int Main()
    {
        DebugOverlay.PerformanceVisible=false;
        DebugOverlay.WriteLine("HYPERCUBE - PACKED ASSETS / DENSE MESH VALIDATION");
        if(!Aram.Initialize(2*1024*1024))throw new InvalidOperationException("ARAM could not reserve the audio region.");
        string path="";
        if(Storage.MountDisc()&&File.Exists("dvd:/world.hcp"))path="dvd:/world.hcp";
        else if(Storage.Mount(StorageDevice.SdSerialPort2)&&File.Exists("sd:/hypercube/world.hcp"))path="sd:/hypercube/world.hcp";
        if(path=="")throw new IOException("Insert the validation data ISO or copy world.hcp to sd:/hypercube/.");
        // Validate the new native staging checksum API on the actual target.
        using(var check=NativeBuffer.Allocate(9))
        {
            check.Write(new byte[]{49,50,51,52,53,54,55,56,57});
            uint seed=check.ComputeCrc32(0,4);
            if(check.ComputeCrc32(4,5,seed)!=0xcbf43926u)throw new InvalidOperationException("Native checksum ABI mismatch.");
        }
        using var scene=new ProbeScene(path);
        bool exiting=false;
        while(true)
        {
            scene.BeginProfileFrame();
            Controller.Update();var pad=Controller.GetState(0);
            if(pad.B.WasPressed)exiting=true;
            if(exiting){if(scene.Stop())return 0;}
            else scene.Input(pad);
            try{scene.Present();}
            catch(Exception error){DebugOverlay.WriteLine("SCENE ERROR: "+error.Message);throw;}
            // Yield the serialized managed execution gate, independently of I/O completion.
            long yieldAt=scene.BeginProfileYield();
            if(!scene.SkipProfileDelay)Task.Delay(1).GetAwaiter().GetResult();
            scene.EndProfileFrame(yieldAt);
        }
    }
}

internal sealed partial class ProbeScene : IDisposable
{
#if HYPERCUBE_PERFORMANCE || HYPERCUBE_PROFILING
    internal const bool PerformanceSuite=true;
#else
    internal const bool PerformanceSuite=false;
#endif
    private const int PerformanceCases=9;
    private int CaseCount=>ProfilingSuite?ProfileCases:PerformanceSuite?PerformanceCases:11;
    private int Kind=>ProfilingSuite?1:PerformanceSuite?(scenario==7?7:scenario==8?8:1):scenario;
    private readonly string[] performanceNames={"BASE","ON DEMAND","SLICED","COARSE FIRST","COMPACT HUD","COMBINED","BASE REPEAT","OPT FAULTS","OPT PRESSURE"};
    private string CaseName=>ProfilingSuite?(FollowupSuite?followupNames[scenario]:profileNames[scenario]):PerformanceSuite?performanceNames[scenario]:names[scenario];
    private readonly FrameMetrics frameMetrics=new FrameMetrics();
    private readonly int[,] performanceResults=new int[9,32];
    private int detailFrames;
    private readonly GraphicsDevice device=GraphicsDevice.Default;
    private readonly Renderer renderer=new Renderer();
    private readonly Camera camera=new Camera(55,4f/3,.1f,80);
    private readonly Material material=new Material{Cull=CullMode.None,Lighting=LightingMode.Unlit};
    private readonly Material actorMaterial=new Material{Cull=CullMode.None,Lighting=LightingMode.Unlit,Color=new Color32(255,230,0)};
    private readonly Transform identity=new Transform(),actorTransform=new Transform();
    private readonly Telemetry telemetry=new Telemetry();
    private readonly Mesh actor;
    private readonly Sound sound;
    private readonly SoundChannel voice;
    private readonly PackedWorld world;
    private readonly string path;
    private WorldStreamer streamer;
    private readonly int[] selectedLod=new int[24],resolved=new int[24],drawIds=new int[256],drawTextures=new int[256];
    private readonly float[] distances=new float[24];
    private readonly bool[] visible=new bool[24];
    private readonly int[,] snapshots=new int[11,32];
    private readonly int[] resultFrame99=new int[11];
    private readonly string[] names={"RAW","REPAIRED","CMPR","BATCHED","ATLAS2","ATLAS4","ARAM","FAULTS","PRESSURE","OCC REF","OCC CULL"};
    private int scenario,requested=-1,budgetIndex=1,completedMask,frame,caseFrames,drawCount,triangles,fallbacks,
        visibleCount,frustumCulled,occluded,bindings,lastTexture,baseline,reportSecond=-1;
    private long caseAt,lastTick;
    private bool stopped,complete,summary,automatic=true;
    private float actorX=2,actorY,actorZ=4,focusX=2,focusZ=4;
    private int actorCell=6;
    private int Budget=>Kind==8?(budgetIndex==0?24:budgetIndex==1?48:96)*1024:
        budgetIndex==0?192*1024:budgetIndex==1?384*1024:768*1024;
    internal ProbeScene(string path)
    {
        this.path=path;world=new PackedWorld(path);
        DebugOverlay.WriteLine("BOOT: COARSE WORLD LOADED");
        actor=ActorMesh();
        var tone=new byte[4096];
        for(int i=0;i<tone.Length;i++)tone[i]=(byte)(128+((i/36)%2==0?48:-48));
        sound=Sound.FromPcm8(tone,16000);sound.Volume=.2f;voice=sound.Play(true);
        DebugOverlay.WriteLine("BOOT: AUDIO LOADED");
        streamer=new WorldStreamer(world,path,Budget);Configure();
        DebugOverlay.WriteLine("BOOT: STREAMER READY");
        baseline=device.NativeBytes-streamer.PhysicalBytes;
        caseAt=lastTick=Clock.Timestamp;
        device.BeginFrame(new Color32(8,10,19));device.EndFrame();
    }
    private static Mesh ActorMesh()
    {
        var p=new[]{new Vector3(-.35f,0,-.3f),new Vector3(.35f,0,-.3f),new Vector3(0,0,.4f),new Vector3(0,1.2f,0)};
        var ids=new[]{0,1,3,1,2,3,2,0,3,0,2,1};
        var v=new VertexPositionColor[12];
        for(int i=0;i<12;i++)v[i]=new VertexPositionColor(p[ids[i]],Color32.White);
        return Mesh.Create(v);
    }
    private void Configure()
    {
        streamer.Group=Kind==4||Kind==6||Kind==7?2:Kind==5?4:1;
        streamer.Format=Kind<2?6:14;
        streamer.ReadBatchBytes=Kind<3?32768:65536;
        streamer.MaxBatchRecords=Kind<3?4:8;
        streamer.UseAram=Kind==6||Kind==7;
        streamer.InjectFaults=Kind==7||Kind==8;
        for(int i=0;i<24;i++)selectedLod[i]=0;
        caseFrames=0;complete=false;summary=false;reportSecond=-1;detailFrames=0;
        frameMetrics.Reset();
        if(PerformanceSuite&&!ProfilingSuite)
        {
            // Each experiment starts from the same repaired RGBA8 layout/batch policy.
            streamer.Group=1;streamer.Format=6;streamer.ReadBatchBytes=32768;streamer.MaxBatchRecords=4;
            streamer.UseAram=false;
            telemetry.Compact=scenario==4||scenario==5||scenario>=7;
            streamer.AdaptiveFallback=scenario==1||scenario==5||scenario>=7;
            streamer.CoarseFirst=scenario==3||scenario==5||scenario>=7;
            if(scenario==2||scenario==5||scenario>=7)
            {streamer.ChecksumBytesPerPump=2048;streamer.CreatesPerPump=1;streamer.PumpBudgetUs=1000;}
        }
        if(ProfilingSuite)ConfigureProfile();
        actorX=focusX=2;actorZ=focusZ=4.5f;actorY=0;actorCell=6;
        actorTransform.LocalRotation=new Quaternion(0,0,0,1);
    }
    internal void Input(ControllerState pad)
    {
        if(pad.A.WasPressed)requested=(scenario+1)%CaseCount;
        if(ProfilingSuite){ProfileInput(pad);return;}
        if(pad.X.WasPressed){budgetIndex=(budgetIndex+1)%3;requested=scenario;}
        if(pad.Y.WasPressed)automatic=!automatic;
        if(pad.Start.WasPressed){completedMask=0;requested=0;}
        if(pad.R.WasPressed)summary=!summary;
    }
    private void Transition()
    {
        streamer.Frame=frame;
        if(!streamer.Stop())return;
        streamer.Dispose();
        if(device.NativeBytes!=baseline-131072)throw new InvalidOperationException("Native resources leaked across a scenario transition.");
        if(Aram.Statistics.AllocatedBytes!=0)throw new InvalidOperationException("ARAM transfers or allocations leaked across a scenario transition.");
        scenario=requested;requested=-1;completedMask&=~(1<<scenario);
        streamer=new WorldStreamer(world,path,Budget);Configure();
        baseline=device.NativeBytes;caseAt=lastTick=Clock.Timestamp;
    }
    private int TextureId(int cell,int lod)
    {
        int group=streamer.Group,cols=(6+group-1)/group;
        return world.Catalog.Texture(streamer.Format,group,lod,(cell/6/group)*cols+cell%6/group);
    }
    private bool ReadyLevel(int cell,int lod,bool raw)
    {
        int first=world.Catalog.FirstMesh(cell,lod,raw);
        if(first==0||!streamer.Ready(TextureId(cell,lod)))return false;
        for(int id=first;id!=0;id=world.Catalog.Next(id))if(!streamer.Ready(id))return false;
        return true;
    }
    private bool Occluded(int node)
    {
        if(Kind!=10||!world.VerifiedOccluder)return false;
        var tree=world.Tree;float x0=tree.X[node]*4,z0=tree.Z[node]*4;
        float x1=x0+tree.Span[node]*4,z1=z0+tree.Span[node]*4;
        if(x1>24)x1=24;if(z1>16)z1=16;
        var o=world.Occluder;
        return Visibility.Occluded(x0,world.NodeMinY[node],z0,x1,world.NodeMaxY[node],z1,focusX,focusZ,
                                   o[0],o[1],o[5],o[3],o[4]);
    }
    private void VisibilityAndDemand(float targetX,float targetZ)
    {
        visibleCount=frustumCulled=0;visibleMask=0;streamer.BeginDemand();
        int targetCell=(int)(targetZ/4)*6+(int)(targetX/4);
        streamer.DemandCollision(actorCell);streamer.DemandCollision(targetCell);
        for(int cell=0;cell<24;cell++)
        {
            int node=OptimizeTree?world.Tree.Leaves[cell]:world.Tree.Leaf(cell);
            bool onScreen=(OptimizeTree?world.Catalog.FirstPatch(node)!=0:world.HasCell(cell))&&Visibility.Frustum(cell%6*4,world.NodeMinY[node],cell/6*4,
                cell%6*4+4,world.NodeMaxY[node],cell/6*4+4,focusX,focusZ);
            visible[cell]=onScreen;if(!onScreen){frustumCulled++;continue;}visibleCount++;visibleMask|=1<<cell;
            float dx=cell%6*4+2-focusX,dz=cell/6*4+2-focusZ;
            float distance=NativeDistance(dx,dz);
            selectedLod[cell]=ScenePolicy.Lod(distance,selectedLod[cell]);
            distances[cell]=distance;
        }
        int selected=0,planCells=0;profileDemanded=0;
        for(int pass=0;pass<24&&Kind<9;pass++)
        {
            int next=-1;
            for(int c=0;c<24;c++)if(visible[c]&&(selected&(1<<c))==0&&
                (next<0||distances[c]<distances[next]))next=c;
            if(next<0||distances[next]>=12)break;
            selected|=1<<next;
            profileDemanded++;
            if(!(ProfilingSuite&&(ProfileFlags&1)!=0)&&!Occluded(OptimizeTree?world.Tree.Leaves[next]:world.Tree.Leaf(next)))
            {
                if(OptimizeDemand)demandOrder[planCells++]=next;
                else streamer.DemandCell(next,Kind==0?0:selectedLod[next],Kind==0);
            }
        }
        if(OptimizeDemand)streamer.DemandCells(demandOrder,planCells,selectedLod,Kind==0);
    }
    private static float NativeDistance(float x,float z)
    {
        // L1 is conservative for the square streaming footprint; hysteresis
        // thresholds are policy parameters, not a claimed screen-error bound.
        return (x<0?-x:x)+(z<0?-z:z);
    }
    private void Add(int mesh,int texture)
    {
        if(drawCount>=drawIds.Length)throw new InvalidOperationException("Visible draw list exceeds the fixed capacity.");
        drawIds[drawCount]=mesh;drawTextures[drawCount++]=texture;
    }
    private void Traverse(int node)
    {
        var tree=world.Tree;
        if(!tree.HasCells(node))return;
        bool anyVisible=false,anyDetail=false;
        if(OptimizeTree)
        {anyVisible=(tree.CellMasks[node]&visibleMask)!=0;anyDetail=(tree.CellMasks[node]&detailMask)!=0;}
        else
        {
            for(int z=tree.Z[node];z<tree.Z[node]+tree.Span[node]&&z<4;z++)
                for(int x=tree.X[node];x<tree.X[node]+tree.Span[node]&&x<6;x++)
                {int cell=z*6+x;if(visible[cell])anyVisible=true;if(resolved[cell]>=0)anyDetail=true;}
        }
        if(!anyVisible)return;
        if(Occluded(node)){occluded++;return;}
        if(!anyDetail&&(Kind<9||tree.Span[node]==1))
        {
            for(int id=world.Catalog.FirstPatch(node);id!=0;id=world.Catalog.Next(id))
            {var r=world.Catalog.Get(id);Add(-id,world.Catalog.Texture(14,4,3,r.Material));fallbacks++;}
            return;
        }
        if(tree.Span[node]>1)
        {for(int q=0;q<4;q++)Traverse(tree.ChildAt(node,q));return;}
        int c=tree.Z[node]*6+tree.X[node],lod=resolved[c];
        if(lod<0)return;
        int texture=TextureId(c,lod);
        for(int id=world.Catalog.FirstMesh(c,lod,Kind==0&&lod==0);id!=0;id=world.Catalog.Next(id))Add(id,texture);
    }
    internal void Present()
    {
        if(requested>=0&&!stopped)Transition();
        long now=Clock.Timestamp;int elapsed=(int)((now-caseAt)/1000),frameUs=(int)(now-lastTick);lastTick=now;
        if(!complete&&requested<0&&!stopped&&caseFrames>0&&(!PerformanceSuite||elapsed>=1000))
        {
            frameMetrics.Record(frameUs);
        }
        if(ProfilingSuite)profileMeasured=!complete&&requested<0&&!stopped&&elapsed>=1000;
        long prepareAt=ProfilingSuite?Clock.Timestamp:0;
        float targetX=actorX,targetZ=actorZ;
        if(!complete&&automatic&&Kind<9)
        {
            int step=(elapsed/25)%160;
            targetX=step<80?2+step*.25f:22-(step-80)*.25f;
            targetZ=(elapsed/4000)%2==0?4.5f:12.5f;
            if(Kind==8){targetX=(elapsed/1000)%2==0?3:21;targetZ=(elapsed/1500)%2==0?3:13;}
        }
        if(Kind>=9){targetX=4;targetZ=4.5f;focusX=12;focusZ=4;}
        else{focusX=actorX;focusZ=actorZ;}
        streamer.Frame=frame;
        int cell=(int)(targetZ/4)*6+(int)(targetX/4);
        if(streamer.Cells.CanEnter(cell))
        {actorX=targetX;actorZ=targetZ;actorCell=cell;actorY=streamer.Ground(cell,actorX,actorZ)+.35f;}
        if(Kind<9){focusX=actorX;focusZ=actorZ;actorTransform.Rotate(0,2,0);}
        // Cull and demand using the same camera pose that will render this frame.
        // Movement uses already-published collision; new collision publishes for
        // the following frame, avoiding a culling mismatch on teleports.
        VisibilityAndDemand(targetX,targetZ);
        if(ProfilingSuite)profile!.Current[FrameProfile.Prepare]+=(int)(Clock.Timestamp-prepareAt);
        long pumpAt=ProfilingSuite?Clock.Timestamp:0,decodeBefore=world.DecodeUs,createBefore=world.CreateUs;
        if(requested<0&&!stopped)streamer.Pump();
        if(ProfilingSuite)RecordProfilePump(pumpAt,decodeBefore,createBefore);
        prepareAt=ProfilingSuite?Clock.Timestamp:0;
        actorTransform.LocalPosition=new Vector3(actorX,actorY,actorZ);
        if(device.NativeBytes!=baseline+streamer.PhysicalBytes||streamer.PhysicalBytes>streamer.Policy.BudgetBytes)
            throw new InvalidOperationException("Native resource accounting or dynamic budget failed.");
        if(!voice.IsPlaying)throw new InvalidOperationException("Audio voice stopped during streaming.");
        detailMask=selectedReadyMask=0;
        for(int c=0;c<24;c++)
        {
            resolved[c]=-1;
            if(visible[c]&&Kind<9)
                for(int l=Kind==0?0:selectedLod[c];l<3;l++)if(ReadyLevel(c,l,Kind==0&&l==0))
                {
                    resolved[c]=l;detailMask|=1<<c;
                    if(l==selectedLod[c])selectedReadyMask|=1<<c;
                    break;
                }
        }
        drawCount=triangles=fallbacks=occluded=bindings=0;lastTexture=-1;Traverse(0);
        for(int i=1;i<drawCount;i++)
        {
            int id=drawIds[i],texture=drawTextures[i],j=i;
            while(j>0&&drawTextures[j-1]>texture){drawIds[j]=drawIds[j-1];drawTextures[j]=drawTextures[j-1];j--;}
            drawIds[j]=id;drawTextures[j]=texture;
        }
        camera.LookAt(new Vector3(focusX,12,focusZ+16),new Vector3(focusX,0,focusZ),new Vector3(0,1,0));
        if(ProfilingSuite)
        {
            profile!.Current[FrameProfile.Prepare]+=(int)(Clock.Timestamp-prepareAt);
            CountProfileDetail();
        }
        long drawAt=ProfilingSuite?Clock.Timestamp:0;
        device.BeginFrame(new Color32(8,10,19));renderer.Begin3D(camera);
        for(int i=0;i<drawCount;i++)
        {
            int id=drawIds[i],texture=drawTextures[i];AssetRecord r=world.Catalog.Get(id<0?-id:id);
            if(texture!=lastTexture){bindings++;lastTexture=texture;}
            material.Texture=id<0?world.CoarseTextures[r.Material]:streamer.Texture(texture);
            if(material.Texture==null)throw new InvalidOperationException("Drawing an unpublished texture.");
            int tint=r.Kind==4?r.Variant:r.Material;
            material.Color=tint%3==0?Color32.White:tint%3==1?new Color32(230,255,255):new Color32(255,230,255);
            if(!(ProfilingSuite&&(ProfileFlags&4)!=0))renderer.DrawMesh(id<0?world.Patches[-id]!:streamer.Geometry(id)!,material,identity);
            triangles+=id<0?world.TriangleCounts[-id]:streamer.Triangles(id);
            if(id>0){streamer.Pin(id);streamer.Pin(texture);}
            // Payload index count is stable; metrics use native geometry bytes
            // only for accounting, never as a triangle-count approximation.
        }
        renderer.DrawMesh(actor,actorMaterial,actorTransform);renderer.End3D();
        if(ProfilingSuite)profile!.Current[FrameProfile.Draw]=(int)(Clock.Timestamp-drawAt);
        long hudAt=ProfilingSuite?Clock.Timestamp:0;
        if(!complete&&elapsed>=1000&&fallbacks<drawCount)detailFrames++;
        var fence=device.InsertFence();
        if(fence.IsCompleted)throw new InvalidOperationException("A frame fence completed before submission finished.");
        if(elapsed/1000!=reportSecond){Report(elapsed);reportSecond=elapsed/1000;}
        if(ProfilingSuite)FillProfileTelemetry(elapsed);else if(PerformanceSuite)FillPerformanceTelemetry();else FillTelemetry();
        if(!complete&&elapsed>=10000&&caseFrames>=60)
        {
            if(streamer.InjectFaults&&(streamer.ShortReads==0||streamer.Corruptions==0||streamer.Delays==0||streamer.ChecksumFailures==0))
                throw new InvalidOperationException("Fault scenario did not exercise short reads, corrupt payload rejection and delayed completions.");
            complete=true;completedMask|=1<<scenario;
            if(!ProfilingSuite)resultFrame99[scenario]=Frame99();
            if(scenario==CaseCount-1)summary=true;
            if(ProfilingSuite)profileFinishPending=true;else telemetry.Data[31]=completedMask;
            if(!ProfilingSuite)for(int i=0;i<32;i++)
            {snapshots[scenario,i]=telemetry.Data[i];if(PerformanceSuite)performanceResults[scenario,i]=telemetry.Data[i];}
        }
        telemetry.Draw();
        if(ProfilingSuite)profile!.Current[FrameProfile.Hud]=(int)(Clock.Timestamp-hudAt);
        long presentAt=ProfilingSuite?Clock.Timestamp:0;
        device.EndFrame();
        if(ProfilingSuite)profile!.Current[FrameProfile.Present]=(int)(Clock.Timestamp-presentAt);
        if(!fence.IsCompleted)throw new InvalidOperationException("A completed frame retained its fence.");
        streamer.CompletedFrame=frame;
        frame++;caseFrames++;
#if HYPERCUBE_CI
        if(complete&&scenario<CaseCount-1&&elapsed>=(ProfilingSuite?14000:11500))requested=scenario+1;
#else
        if(complete&&scenario<CaseCount-1&&automatic&&elapsed>=(ProfilingSuite?14000:11500))requested=scenario+1;
#endif
    }
    private int Frame99()=>frameMetrics.Percentile(99);
    private void FillPerformanceTelemetry()
    {
        var t=telemetry.Data;
        if(complete){for(int i=0;i<32;i++)t[i]=performanceResults[scenario,i];return;}
        t[0]=0x4843;t[1]=3;t[2]=frame;t[3]=scenario;t[4]=caseFrames;
        t[5]=Budget/1024;t[6]=streamer.PhysicalBytes/1024;t[7]=device.NativeBytes/1024;
        t[8]=frameMetrics.Percentile(50);t[9]=frameMetrics.Percentile(95);t[10]=Frame99();
        t[11]=frameMetrics.MaximumUs/1000+1;t[12]=frameMetrics.MeanUs/10;
        t[13]=streamer.MaxPumpUs/10;t[14]=streamer.PumpCalls==0?0:(int)(streamer.PumpUs/streamer.PumpCalls/10);
        t[15]=streamer.ReadOperations;t[16]=streamer.StorageBytes/1024;t[17]=streamer.Publications;
        t[18]=streamer.Policy.Evicted;t[19]=streamer.Deferred;t[20]=streamer.Failures;
        t[21]=streamer.Policy.Cancelled+streamer.Policy.Stale;t[22]=drawCount;t[23]=triangles;t[24]=fallbacks;
        t[25]=(int)(actorX*256);t[26]=(int)(actorZ*256);t[27]=streamer.Cells.BlockedMoves;
        t[28]=voice.IsPlaying?1:0;t[29]=streamer.CancelledUploads;t[30]=detailFrames;t[31]=completedMask;
    }
    private void FillTelemetry()
    {
        var t=telemetry.Data;
        t[0]=0x4843;t[1]=2;t[2]=frame;t[3]=scenario;t[4]=caseFrames;t[5]=streamer.PhysicalBytes/1024;
        t[6]=device.NativeBytes/1024;t[7]=streamer.Policy.ResidentBytes/1024;t[8]=Frame99();
        t[9]=streamer.Publications;t[10]=streamer.ReadOperations;t[11]=streamer.StorageBytes/1024;
        t[12]=streamer.AramBytes/1024;t[13]=streamer.Cold.Hits;t[14]=streamer.Textures.Reuses;t[15]=streamer.Policy.Evicted;
        t[16]=streamer.Failures;t[17]=streamer.Policy.Cancelled+streamer.Policy.Stale;t[18]=streamer.MaxBatch;
        t[19]=streamer.MultiplePublications;t[20]=drawCount;t[21]=triangles;t[22]=fallbacks;
        t[23]=visibleCount;t[24]=(int)(actorX*256);t[25]=(int)(actorY*256);t[26]=(int)(actorZ*256);
        t[27]=streamer.Cells.BlockedMoves;t[28]=voice.IsPlaying?1:0;t[29]=streamer.CancelledUploads;t[30]=occluded;t[31]=completedMask;
    }
    private void Report(int elapsed)
    {
        if(ProfilingSuite){ReportProfile();return;}
        if(PerformanceSuite&&summary)
        {
            DebugOverlay.WriteLine("PERFORMANCE RESULTS - F50/F95/F99 MS");
            for(int s=0;s<PerformanceCases;s++)DebugOverlay.WriteLine(performanceNames[s]+" "+performanceResults[s,8]+"/"+performanceResults[s,9]+"/"+performanceResults[s,10]+" READ "+performanceResults[s,15]);
            return;
        }
        if(summary)
        {
            DebugOverlay.WriteLine("SCENARIO RESULTS - A:NEXT X:BUDGET START:RERUN");
            // Ten rows fit the built-in overlay: split the last pair into one line.
            for(int s=0;s<8;s++)DebugOverlay.WriteLine(names[s]+" READ "+snapshots[s,10]+" PUB "+snapshots[s,9]+" F99 "+resultFrame99[s]+" MS");
            DebugOverlay.WriteLine("PRESSURE F99 "+resultFrame99[8]+" OCC DRAWS "+snapshots[9,20]+" TO "+snapshots[10,20]);
            return;
        }
        DebugOverlay.WriteLine("HYPERCUBE "+(scenario+1)+"/"+CaseCount+" "+CaseName+" "+elapsed+" MS");
        DebugOverlay.WriteLine("DYNAMIC KIB "+streamer.PhysicalBytes/1024+" / "+Budget/1024+" TOTAL GX "+device.NativeBytes/1024);
        DebugOverlay.WriteLine("READS "+streamer.ReadOperations+" STORAGE KIB "+streamer.StorageBytes/1024+" ARAM HITS "+streamer.Cold.Hits);
        DebugOverlay.WriteLine("PUBLISH "+streamer.Publications+" BATCH MAX "+streamer.MaxBatch+" MULTI "+streamer.MultiplePublications);
        DebugOverlay.WriteLine("FAIL "+streamer.Failures+" STALE "+streamer.Policy.Stale+" REUSE "+streamer.Textures.Reuses);
        DebugOverlay.WriteLine("DRAW "+drawCount+" FALLBACK "+fallbacks+" BIND "+bindings+" CULL "+frustumCulled+" OCC "+occluded);
        DebugOverlay.WriteLine("ACTOR CELL "+actorCell+" BLOCKED "+streamer.Cells.BlockedMoves+" AUDIO ACTIVE");
        DebugOverlay.WriteLine("F50/95/99 "+frameMetrics.Percentile(50)+"/"+frameMetrics.Percentile(95)+"/"+Frame99()+" PUMP US "+streamer.MaxPumpUs);
        DebugOverlay.WriteLine("A:NEXT X:BUDGET Y:CAMERA START:RERUN R:RESULTS B:EXIT");
        DebugOverlay.WriteLine("SYNTHETIC ADVERSARIAL FIXTURE - IMPORT REPORT RECORDS PROVENANCE");
    }
    internal bool Stop(){stopped=true;streamer.Frame=frame;return streamer.Stop();}
    public void Dispose()
    {
        if(!streamer.Stop())throw new InvalidOperationException("Drain before disposal.");
        streamer.Dispose();voice.Dispose();sound.Dispose();actor.Dispose();telemetry.Dispose();world.Dispose();
        if(GraphicsDevice.Default.NativeBytes!=0||Aram.Statistics.AllocatedBytes!=0)
            throw new InvalidOperationException("Shutdown leaked native assets.");
    }
}
