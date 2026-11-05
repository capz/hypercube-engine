using Dolphin.Diagnostics;
using Dolphin.Graphics;
using Dolphin.Input;
using Dolphin.Timing;
using Hypercube;
using Hypercube.GameCube;

internal static class Program
{
    private static VertexPositionColorTexture V(float x,float z)=>new VertexPositionColorTexture(new Vector3(x,0,z),Color32.White,new Vector2(x*.5f,z*.5f));
    private static Mesh Marker()
    {
        var p=new[]{new Vector3(-.4f,0,-.4f),new Vector3(.4f,0,-.4f),new Vector3(.4f,0,.4f),new Vector3(-.4f,0,.4f),new Vector3(0,1.5f,0)};
        var v=new VertexPositionColor[12];for(int i=0;i<4;i++){v[i*3]=new VertexPositionColor(p[i],Color32.White);v[i*3+1]=new VertexPositionColor(p[4],Color32.White);v[i*3+2]=new VertexPositionColor(p[(i+1)%4],Color32.White);}return Mesh.Create(v);
    }
    private static int Main()
    {
        DebugOverlay.PerformanceVisible=false;
        var device=GraphicsDevice.Default;var profile=new FrameProfile();var counters=profile.Rendering;
        var owner=new SharedGraphicsResources(4,8,8192);
        var texture=Texture2D.CreateNative(8,8,NativeTextureFormat.I8,true);var pixels=new byte[64];int pixel=0;
        for(int y=0;y<8;y+=4)for(int dy=0;dy<4;dy++)for(int x=0;x<8;x++)pixels[pixel++]=(byte)(((x+(y+dy))&1)==0?255:225);
        texture.ReplaceNative(pixels);owner.RegisterTexture(1,texture);var textureLease=owner.Acquire(1);
        var effect=GxEffect.ColorRamp(new Color32(50,60,80),Color32.White);owner.RegisterEffect(2,effect,192);var effectLease=owner.Acquire(2);
        var groundMaterial=new Material{Texture=owner.Texture(textureLease),Cull=CullMode.None};
        CloudShadowLayer.Configure(groundMaterial,new SamplerState(TextureAddressMode.Repeat,TextureAddressMode.Repeat,TextureFilter.Linear));
        using var clouds=new CloudShadowLayer(2,32,new Vector2(.4f,.2f));
        using var ground=new DynamicMeshBuffer(3072);var vertices=ground.Reserve(1536,counters);int at=0;
        for(int z=-8;z<8;z++)for(int x=-8;x<8;x++){
            vertices[at++]=V(x,z);vertices[at++]=V(x+1,z+1);vertices[at++]=V(x+1,z);vertices[at++]=V(x,z);vertices[at++]=V(x,z+1);vertices[at++]=V(x+1,z+1);
        }
        int trackedBytes=owner.TrackedBytes;
        int uploads=ground.Commit(at,1,counters)?1:0;
        using var marker=Marker();var batch=new MeshBatch(32,1,1);var renderer=new Renderer(device,batch.Capacity);var identity=new Transform();var ui=new Draw2D(device);var data=new int[32];
        int mesh=batch.RegisterMesh(marker),material=batch.RegisterMaterial(new Material{Effect=owner.Effect(effectLease),Cull=CullMode.None});
        var frustum=new CameraFrustum(4,10,16,0,0,0,0,1,0,55,4f/3,.1f,80);var camera=new Camera(55,4f/3,.1f,80);camera.LookAt(new Vector3(4,10,16),Vector3.Zero,new Vector3(0,1,0));
        long start=Clock.Timestamp;int frame=0;
        while(true){
            Controller.Update();if(Controller.GetState(0).B.WasPressed)break;
            long frameAt=Clock.Timestamp;frame++;counters.Reset();for(int i=0;i<8;i++)profile.Current[i]=0;
            if(frame==120)vertices[0]=new VertexPositionColorTexture(vertices[0].Position,new Color32(225,255,225),vertices[0].TextureCoordinate);
            if(ground.Commit(at,frame<120?1:2,counters))uploads++;
            if(uploads!=(frame<120?1:2))throw new InvalidOperationException("Dirty geometry uploaded more than once");
            int phase=(frame/180)%3;clouds.Cover=phase==0?.5f:phase==1?0:1;clouds.Animate((Clock.Timestamp-start)/1000000.0);clouds.Apply(groundMaterial,new Color32(140,190,110));
            for(int z=-6;z<=6;z+=4)for(int x=-6;x<=6;x+=4){
                if(!frustum.Sphere(x,.75f,z,1.5f)){counters.CulledObjects++;continue;}
                var tint=(x+z)%8==0?new Color32(240,170,80):new Color32(80,180,230);
                if(!batch.TryAdd(mesh,material,new Vector3(x,0,z),Quaternion.Identity,new Vector3(1,1,1),tint,counters))throw new InvalidOperationException("Unexpected batch overflow");
            }
            if(frame==360){
                var temporary=Texture2D.CreateNative(8,8,NativeTextureFormat.I8,true);temporary.ReplaceNative(pixels);owner.RegisterTexture(3,temporary);
                var retiring=owner.Acquire(3);owner.Pin(retiring,frame);owner.Release(retiring);
                if(owner.TrimUnused(frame-1)!=0)throw new InvalidOperationException("GPU resource retired early");
            }
            owner.Pin(textureLease,frame);owner.Pin(effectLease,frame);
            profile.Current[FrameProfile.Prepare]=(int)(Clock.Timestamp-frameAt);long drawAt=Clock.Timestamp;
            device.BeginFrame(new Color32(120,170,195));renderer.Begin3D(camera);renderer.DrawMesh(ground.Mesh!,groundMaterial,identity);batch.Flush(renderer,counters);renderer.End3D();
            RenderTelemetry.Capture(counters);
            data[0]=0x4843;data[1]=9;data[2]=frame&65535;data[3]=phase;data[4]=uploads;data[5]=counters.BatchCalls;data[6]=counters.DrawingApiCalls;data[7]=counters.DrawSubmissions;data[8]=counters.Triangles;data[9]=counters.SubmittedInstances;data[10]=counters.CulledObjects;data[11]=counters.GeometryRebuilds;data[12]=counters.BufferGrowths;data[13]=counters.NativeBytes/1024;
            data[31]=0;for(int i=0;i<31;i++)data[31]^=data[i]&65535;
            ui.Begin();ui.FillRectangle(new RectF(0,448,640,32),new Color32(16,16,16));
            for(int bit=0;bit<512;){int length=TelemetryRuns.Length(data,bit);if(TelemetryRuns.Lit(data,bit))ui.FillRectangle(new RectF(bit%64*10,448+bit/64*4,length*10,4),Color32.White);bit+=length;}ui.End();
            profile.Current[FrameProfile.Draw]=(int)(Clock.Timestamp-drawAt);long presentAt=Clock.Timestamp;
            var fence=device.InsertFence();device.EndFrame();if(!fence.IsCompleted)throw new InvalidOperationException("Frame not completed");
            if(frame==360&&(owner.TrimUnused(frame)!=1||owner.TrackedBytes!=trackedBytes))throw new InvalidOperationException("Completed resource did not retire");
            RenderTelemetry.CaptureWaits(counters);
            profile.Current[FrameProfile.Present]=(int)(Clock.Timestamp-presentAt);
            profile.Record((int)(Clock.Timestamp-frameAt),0,0,0,0,counters.DrawSubmissions,counters.Triangles);
            if(counters.BatchCalls>2||counters.SubmittedInstances<1||counters.Triangles<1)throw new InvalidOperationException("Invalid batch telemetry");
            if(frame%180==0)DebugOverlay.WriteLine("HYPERCUBE RENDER PASS phase="+phase+" uploads="+uploads+" batchCalls="+counters.BatchCalls+" api="+counters.DrawingApiCalls+" gx="+counters.DrawSubmissions+" triangles="+counters.Triangles);
        }
        device.WaitIdle();owner.Release(textureLease);owner.Release(effectLease);owner.Dispose(frame);return 0;
    }
}
