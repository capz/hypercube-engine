using System;
using Dolphin.Graphics;
using Dolphin.Memory;
using Dolphin.Storage;
using Hypercube;
using Hypercube.GameCube;
using Scheduler=Dolphin.Threading.Tasks.Scheduler;

internal static class Program
{
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static int Main()
    {
        Check(Aram.Initialize(2*1024*1024)&&Storage.Mount(StorageDevice.SdSerialPort2),"Storage/ARAM unavailable.");
        using var world=new PackedWorld("sd:/world-mixed.hcp");
        using var stream=new WorldStreamer(world,"sd:/world-mixed.hcp",2*1024*1024,true){UseAram=true};
        var lods=stream.TextureLods!;var binding=lods.Get("terrain/rgba",TextureQualityPolicy.FullQuality);
        var late=lods.Get("objects/cmpr",TextureQualityPolicy.CoarseOnly);
        var device=GraphicsDevice.Default;using var draw=new Draw2D(device);
        bool preempted=false,lateRequested=false,lateReady=false,done=false,stopping=false;int frame=0;
        while(!done)
        {
            Scheduler.Pump(16);
            if(!stopping)
            {
                stream.Frame=frame;stream.CompletedFrame=Math.Max(0,frame-1);stream.BeginDemand();stream.DemandCollision(0);
                lods.Demand(binding,new TextureFootprint(2048,2048));
                if(lods.UploadBytes>131072){stream.DemandCollision(1);lateRequested=true;}
                if(lateRequested)lods.Demand(late,new TextureFootprint(128,128));
                stream.Pump();
                if(frame==0)
                {stream.SetColdRetention(TextureColdRetention.StrictRelease);Check(stream.Policy.State(stream.Policy.Find(world.Catalog.Collision(0)))==ResourceState.Reading&&!lods.HasTransfer,"Gameplay did not precede missing coarse texture.");Check(lods.ColdRetention==TextureColdRetention.ReversalCache,"Cold policy changed before the outstanding batch drained.");}
                if(lods.UploadBytes>131072&&stream.Ready(world.Catalog.Collision(1)))preempted=true;
                if(lods.CoarseReady(late.Family.Id)&&!lods.DetailReady(binding.Family.Id))lateReady=true;
                if(lods.DetailReady(binding.Family.Id))
                {
                    Check(preempted,"Chunked promotion blocked gameplay/collision preemption.");
                    Check(lateReady,"A late visible coarse family was blocked behind a detached upgrade.");
                    Check(stream.PhysicalBytes<=stream.Policy.BudgetBytes&&world.NativeBytes>0,"World/staging/permanent residency not counted.");
                    Check(lods.ColdRetention==TextureColdRetention.StrictRelease,"Deferred shared cold policy was never applied.");Check(stream.NormalizedTextureUvs,"Normalized UV opt-in lost.");stopping=true;
                }
            }
            else done=stream.Stop();
            device.BeginFrame(Color32.Black);draw.Begin();
            if(!stopping)
            {
                var p=lods.Resolve(binding);Check(lods.Pin(p,frame),"Stale world texture lease.");draw.DrawTexture(p.Texture,new RectF(32,32,256,256),Color32.White);
                if(lateRequested){var q=lods.Resolve(late);Check(lods.Pin(q,frame),"Stale late family lease.");draw.DrawTexture(q.Texture,new RectF(320,32,256,256),Color32.White);}
            }
            draw.End();stream.EndFrame(device.InsertFence());device.EndFrame();
            Check(++frame<3000,"World texture integration timed out.");
        }
        return 0;
    }
}
