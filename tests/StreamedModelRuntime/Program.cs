using System;
using Dolphin.Graphics;
using Dolphin.Storage;
using Hypercube;
using Hypercube.GameCube;
using Scheduler=Dolphin.Threading.Tasks.Scheduler;

internal static class Program
{
    private static void Check(bool v){if(!v)throw new Exception("Streamed model binding assertion.");}
    static int Main()
    {
        Check(Storage.Mount(StorageDevice.SdSerialPort2));
        using var source=new PackedAssetSource("sd:/models.hcp");using var stream=new TextureLodStreamer(source.Catalog,source.Assets);
        using var materials=ModelMaterials.Create0(stream,TextureQualityPolicy.FullQuality);
        Check(materials.Count==3&&source.Catalog.FamilyCount==1);
        Check(materials.GetPart(1).Primary!.Family==materials.GetPart(2).Secondary!.Family);
        Check(materials.GetPart(1).Template.Effect!=materials.GetPart(2).Template.Effect);
        var loading=Model.LoadAsync("sd:/parts.dndmodel");for(int i=0;i<10000&&!loading.IsCompleted;i++)Scheduler.Pump(1);
        using var model=loading.GetAwaiter().GetResult();using var instance=model.CreateInstance();model.Dispose();
        var renderer=new Renderer();var camera=new Camera();camera.LookAt(new Vector3(0,0,5),Vector3.Zero,new Vector3(0,1,0));var transform=new Transform();var device=GraphicsDevice.Default;
        bool sawCoarse=false,detail=false;
        var controller=new TextureBudgetController();
        for(int frame=0;frame<3000;frame++)
        {
            Scheduler.Pump(16);stream.BeginFrame(frame,Math.Max(0,frame-1));
            for(int part=1;part<3;part++)materials.DemandPart(part,1,5,1,60,480,new TextureFootprint(256,256));
            var budget=controller.Next(1000,stream.LastPumpUs,0);
            stream.Prefetch(materials.GetPart(1).Primary!,new TexturePrefetchHint(.9f,.3f,.8f,100),budget);
            stream.Pump(budget);var resolved=stream.Resolve(materials.GetPart(1).Primary!);
            if(resolved.LogicalLevel==4)sawCoarse=true;
            detail=stream.DetailReady(1);
            device.BeginFrame(Color32.Black);renderer.Begin3D(camera);
            materials.DrawPart(renderer,instance,transform,1,frame);materials.DrawPart(renderer,instance,transform,2,frame);
            renderer.End3D();stream.EndFrame(device.InsertFence());device.EndFrame();
            if(detail)break;
        }
        Check(sawCoarse&&detail&&stream.Publications==2);
        stream.SetColdRetention(TextureColdRetention.Disabled);
        for(int n=0;n<10000&&!stream.Stop();n++){Scheduler.Pump(16);device.BeginFrame(Color32.Black);device.EndFrame();}
        Check(!stream.HasTransfer);stream.Dispose();Check(stream.PhysicalBytes==0);return 0;
    }
}
