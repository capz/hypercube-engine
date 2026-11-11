using System;
using Dolphin.Graphics;
using Dolphin.Timing;
using Hypercube;
using Hypercube.GameCube;
using Scheduler=Dolphin.Threading.Tasks.Scheduler;

// Shared by the console inspection scene and the Debug/Release host AOT test.
internal sealed class TextureRuntimeScenario : IDisposable
{
    private readonly PackedAssetSource source;
    private TextureLodStreamer textures;
    private readonly GraphicsDevice device=GraphicsDevice.Default;
    private readonly Draw2D overlay;
    private readonly Renderer renderer=new Renderer();
    private readonly Camera camera=new Camera();
    private readonly Mesh quad;
    private readonly GxEffect multiply;
    private readonly Transform left=new Transform{LocalPosition=new Vector3(-2,0,0),LocalScale=new Vector3(3,3,1)};
    private readonly Transform right=new Transform{LocalPosition=new Vector3(2,0,0),LocalScale=new Vector3(3,3,1)};
    private readonly StreamedMaterialTexture[] bindings;
    private readonly StreamedMaterialBinding primary,dual;
    private readonly int[] fields=new int[32];
    private readonly Func<bool>? audioPlaying;
    private ResolvedTextureLod firstLease;
    private int frame,phase,phaseFrame,coarseMask,crcFailures,failures,stale,publications,deferred,peak,retirements,aramHits;
    private bool stopped,disposed,shortInjected;
    private long previousAt;
    private int coarseReadyCount,detailReadyCount,maxPumpUs,storageBytes,coldBytes,uploadedBytes,cancelled;
    private long coarseUs,detailUs;
    public readonly FrameMetrics Frames=new FrameMetrics();
    public bool Done=>phase==10;
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static VertexPositionNormalColorTexture V(float x,float y,float u,float v)=>new VertexPositionNormalColorTexture(new Vector3(x,y,0),new Vector3(0,0,1),Color32.White,new Vector2(u,v));
    internal TextureRuntimeScenario(string path,Func<bool>? audioPlaying=null)
    {
        this.audioPlaying=audioPlaying;
        source=new PackedAssetSource(path);textures=new TextureLodStreamer(source.Catalog,source.Assets){UseAram=true};
        bindings=new[]{textures.Get("terrain/rgba",TextureQualityPolicy.CoarseOnly),textures.Get("objects/cmpr",TextureQualityPolicy.CoarseOnly),textures.Get("faces/pair",TextureQualityPolicy.CoarseOnly),textures.Get("single",TextureQualityPolicy.CoarseOnly)};
        var rotation=new UvTransform(new Vector2(1,0),new Vector2(1,1),90);
        var material=new Material{Cull=CullMode.None,TextureTransform=rotation,SecondaryTextureTransform=rotation,DepthTest=false,DepthWrite=false};
        var program=new TevProgramBuilder();program.AddStage(TevInput.Zero,TevInput.Raster,TevInput.Texture,TevInput.Zero);program.AddStage(TevInput.Zero,TevInput.Previous,TevInput.SecondaryTexture,TevInput.Zero);multiply=program.CreateEffect();
        var two=new Material{Cull=CullMode.None,TextureTransform=rotation,SecondaryTextureTransform=rotation,DepthTest=false,DepthWrite=false,Effect=multiply};
        primary=new StreamedMaterialBinding(textures,material,bindings[0]);dual=new StreamedMaterialBinding(textures,two,bindings[1],bindings[0]);
        quad=Mesh.Create(new[]{V(-.5f,.5f,0,0),V(.5f,.5f,1,0),V(.5f,-.5f,1,1),V(-.5f,.5f,0,0),V(.5f,-.5f,1,1),V(-.5f,-.5f,0,1)});
        overlay=new Draw2D(device);camera.LookAt(new Vector3(0,0,5),Vector3.Zero,new Vector3(0,1,0));camera.SetOrthographic(8,6,.1f,100);
    }
    private bool AllReady(bool detail)
    {for(int i=0;i<bindings.Length;i++)if(detail?!textures.DetailReady(i+1):!textures.CoarseReady(i+1))return false;return true;}
    private void Next(int next){phase=next;phaseFrame=frame;}
    private void Snapshot()
    {
        crcFailures+=textures.ChecksumFailures;failures+=textures.Failures;stale+=textures.StaleUploads;publications+=textures.Publications;deferred+=textures.Deferred;peak=Math.Max(peak,textures.PeakBytes);retirements+=textures.Retirements;aramHits+=textures.Cold.Hits;
        coarseReadyCount+=textures.CoarseReadyCount;detailReadyCount+=textures.DetailReadyCount;coarseUs+=textures.CoarseLatencyUs;detailUs+=textures.DetailLatencyUs;
        maxPumpUs=Math.Max(maxPumpUs,textures.MaxPumpUs);storageBytes+=textures.StorageBytes;coldBytes+=textures.ColdReadBytes;uploadedBytes+=textures.UploadedBytes;cancelled+=textures.Cancellations;
    }
    public void Step()
    {
        Check(!disposed,"Disposed scenario.");long at=Clock.Timestamp;if(previousAt!=0)Frames.Record((int)(at-previousAt));previousAt=at;
        Scheduler.Pump(16);
        Check(audioPlaying==null||audioPlaying(),"Audio voice stopped under texture pressure.");
        if(phase<7||phase==8)
        {
            textures.BeginFrame(frame,Math.Max(0,frame-1));
            if(phase==8)textures.Demand(bindings[0],new TextureFootprint(2048,2048));
            else for(int i=0;i<bindings.Length;i++)textures.Demand(bindings[i],new TextureFootprint(2048,2048));
            textures.Pump(32768,1000);
            for(int i=0;i<bindings.Length;i++)if(textures.CoarseReady(i+1))coarseMask|=1<<i;
            if(phase==0&&AllReady(false))
            {
                for(int i=0;i<bindings.Length;i++)Check(textures.ResidentIndex(i+1)==bindings[i].Family.CoarseIndex,"Coarse must precede fine readiness.");
                firstLease=textures.Resolve(bindings[0]);Next(1);
            }
            else if(phase==1&&frame-phaseFrame>=30)
            {bindings[0].Policy=TextureQualityPolicy.FullQuality;textures.CorruptNextChunk=true;Next(2);}
            else if(phase==2&&textures.ChecksumFailures>0)
            {
                Check(textures.Resolve(bindings[0]).Texture==firstLease.Texture,"CRC failure evicted fallback.");
                textures.ShortNextRead=true;shortInjected=true;Next(3);
            }
            else if(phase==3&&textures.DetailReady(1))
            {
                Check(shortInjected&&textures.Failures>=2,"Short read or CRC failure was not isolated.");
                Check(!textures.Pin(firstLease,frame),"Stale raw texture lease remained valid.");
                for(int i=0;i<bindings.Length;i++)bindings[i].Policy=TextureQualityPolicy.FullQuality;
                if(AllReady(true))Next(4);
            }
            else if(phase==4){if(!AllReady(true))phaseFrame=frame;else if(frame-phaseFrame>=60)Next(5);}
            else if(phase==5)
            {
                camera.LookAt(new Vector3((float)Math.Sin(frame*.1)*2,(float)Math.Cos(frame*.07),5),Vector3.Zero,new Vector3(0,1,0));
                bindings[0].Policy=(frame-phaseFrame)%4<2?TextureQualityPolicy.CoarseOnly:TextureQualityPolicy.FullQuality;
                Check(textures.CoarseReady(1),"Camera reversal lost visible fallback.");
                if(frame-phaseFrame>=100){bindings[0].Policy=TextureQualityPolicy.FullQuality;camera.LookAt(new Vector3(0,0,5),Vector3.Zero,new Vector3(0,1,0));Next(6);}
            }
            else if(phase==6&&AllReady(true)&&frame-phaseFrame>=60)
            {
                Check(textures.StaleUploads>0&&textures.Cancellations>0,"Churn did not drain stale work.");
                int active=0;for(int i=0;i<bindings.Length;i++)active+=source.Catalog.Get(bindings[i].Family.GetLevel(textures.ResidentIndex(i+1)).RecordId).Length;
                Check(textures.ActiveBytes==active,"Active residency is not exactly one image per family.");
                Check(textures.Cold.Bytes>0,"ARAM retention did not retain cold copies.");
                textures.StrictReleaseColdCopies=true;Next(7);
            }
            else if(phase==8&&textures.Deferred>0&&frame-phaseFrame>=60)
            {Check(textures.ResidentIndex(1)==bindings[0].Family.CoarseIndex,"Pressure evicted coarse to start a promotion.");Next(9);}
        }
        if(phase==7||phase==9)
        {
            if(textures.Stop())
            {
                textures.Cold.Trim();Check(textures.Cold.Bytes==0,"Cold copies survived strict trim.");Snapshot();textures.Dispose();Check(textures.PhysicalBytes==0,"Native bytes survived final fence/trim.");stopped=true;
                if(phase==7)
                {
                    textures=new TextureLodStreamer(source.Catalog,source.Assets,128*1024){StrictReleaseColdCopies=true};stopped=false;
                    textures.BeginFrame(frame,Math.Max(0,frame-1));
                    bindings[0]=textures.Get("terrain/rgba",TextureQualityPolicy.FullQuality);Next(8);
                }
                else Next(10);
            }
        }
        device.BeginFrame(new Color32(24,28,36));
        if(phase<7)
        {renderer.Begin3D(camera);primary.Draw(renderer,quad,left,frame);dual.Draw(renderer,quad,right,frame);renderer.End3D();}
        overlay.Begin();
        if(phase<7)
        {
            for(int i=2;i<bindings.Length;i++)
            {
                var p=textures.Resolve(bindings[i]);Check(textures.Pin(p,frame),"Stale auxiliary lease.");
                overlay.DrawTexture(p.Texture,new RectF(p.Uv.OffsetU*p.Texture.Width,p.Uv.OffsetV*p.Texture.Height,p.Uv.ScaleU*p.Texture.Width,p.Uv.ScaleV*p.Texture.Height),new RectF(40+(i-2)*320,380,240,32),Color32.White);
            }
        }
        if(phase==8)
        {
            var p=textures.Resolve(bindings[0]);Check(textures.Pin(p,frame),"Stale pressure lease.");
            overlay.DrawTexture(p.Texture,new RectF(40,120,240,240),Color32.White);
        }
        if(Done)overlay.FillRectangle(new RectF(40,120,560,240),new Color32(32,180,100));
        fields[0]=0x4854;fields[1]=phase;fields[2]=frame;fields[3]=publications+(stopped?0:textures.Publications);
        fields[4]=crcFailures+(stopped?0:textures.ChecksumFailures);fields[5]=failures+(stopped?0:textures.Failures);
        fields[6]=stale+(stopped?0:textures.StaleUploads);fields[7]=stopped?0:textures.ActiveBytes/32;fields[8]=stopped?0:textures.UploadBytes/32;
        fields[9]=stopped?0:textures.RetiredBytes/32;fields[10]=stopped?0:textures.AramBytes/32;fields[11]=coarseMask;fields[12]=deferred+(stopped?0:textures.Deferred);
        fields[16]=0;if(phase<7)for(int i=0;i<bindings.Length;i++)if(textures.DetailReady(i+1))fields[16]|=1<<i;fields[17]=Math.Max(peak,stopped?0:textures.PeakBytes)/1024;
        fields[13]=retirements+(stopped?0:textures.Retirements);fields[14]=Frames.Percentile(99);fields[15]=aramHits+(stopped?0:textures.Cold.Hits);
        fields[18]=audioPlaying==null||audioPlaying()?1:0;fields[19]=coarseReadyCount+(stopped?0:textures.CoarseReadyCount);fields[20]=detailReadyCount+(stopped?0:textures.DetailReadyCount);
        fields[21]=(int)((coarseUs+(stopped?0:textures.CoarseLatencyUs))/Math.Max(1,fields[19])/1000);fields[22]=(int)((detailUs+(stopped?0:textures.DetailLatencyUs))/Math.Max(1,fields[20])/1000);
        fields[23]=Math.Min(65535,Math.Max(maxPumpUs,stopped?0:textures.MaxPumpUs));fields[24]=(storageBytes+(stopped?0:textures.StorageBytes))/1024;
        fields[25]=(coldBytes+(stopped?0:textures.ColdReadBytes))/1024;fields[26]=(uploadedBytes+(stopped?0:textures.UploadedBytes))/1024;fields[27]=cancelled+(stopped?0:textures.Cancellations);fields[28]=Frames.Percentile(95);
        fields[29]=stopped?0:textures.ReadBufferBytes/1024;fields[30]=stopped?0:textures.PoolBytes;
        fields[31]=0;for(int k=0;k<31;k++)fields[31]^=fields[k];
        for(int k=0;k<fields.Length;k++)for(int bit=0;bit<16;bit++)overlay.FillRectangle(new RectF(32+((k%16)*16+bit)*2,432+k/16*10,2,8),(fields[k]&(1<<bit))!=0?Color32.White:Color32.Black);
        overlay.End();var fence=device.InsertFence();if(!stopped)textures.EndFrame(fence);device.EndFrame();frame++;
        Check(frame<5000||Done,"Progressive stress scenario timed out.");
    }
    public void Dispose()
    {
        if(disposed)return;if(!stopped)throw new InvalidOperationException("Complete stress scenario before disposal.");
        overlay.Dispose();quad.Dispose();multiply.Dispose();source.Dispose();disposed=true;
    }
}
