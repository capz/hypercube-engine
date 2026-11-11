using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dolphin.Graphics;
using Dolphin.Storage;
using Hypercube;
using Hypercube.GameCube;
using Scheduler=Dolphin.Threading.Tasks.Scheduler;

internal static class Program
{
    private static void Check(bool v){if(!v)throw new Exception("Resident mip tail assertion.");}
    private static void Drain(Task task){for(int i=0;i<10000&&!task.IsCompleted;i++)Scheduler.Pump(1);Check(task.IsCompleted);task.GetAwaiter().GetResult();}
    static int Main()
    {
        Check(Storage.Mount(StorageDevice.SdSerialPort2));
        byte[] checksum=File.ReadAllBytes("sd:/tail.crc");uint crc=Binary.U32(checksum,0);
        using var staging=NativeBuffer.Allocate(4096);var device=GraphicsDevice.Default;var renderer=new Renderer();var camera=new Camera();var transform=new Transform();
        VertexPositionNormalColorTexture V(float x,float y,float u,float v)=>new VertexPositionNormalColorTexture(new Vector3(x,y,0),new Vector3(0,0,1),Color32.White,new Vector2(u,v));
        using var mesh=Mesh.Create(new[]{V(-1,1,0,0),V(1,1,1,0),V(1,-1,1,1),V(-1,1,0,0),V(1,-1,1,1),V(-1,-1,0,1)});
        int before=device.NativeBytes;var loading=ResidentMipTail.LoadAsync("sd:/tail.dndtex",crc,staging);Drain(loading);using var tail=loading.Result;
        Check(tail.Info.Levels==6&&tail.Texture.MipLevels==6&&tail.ActiveBytes==tail.Texture.NativeBytes&&device.NativeBytes-before==tail.PhysicalBytes);
        var material=new Material();tail.Apply(material);Check(material.Sampler.MinFilter==TextureMinFilter.LinearMipmapLinear&&material.Sampler.MaxLod==5);
        var bad=ResidentMipTail.LoadAsync("sd:/tail.dndtex",crc^1,staging);
        try{Drain(bad);return 2;}catch(InvalidDataException){}
        Check(tail.ActiveBytes>0&&device.NativeBytes-before==tail.PhysicalBytes);
        using(var cancel=new CancellationTokenSource())
        {var task=ResidentMipTail.LoadAsync("sd:/tail.dndtex",crc,staging,cancellationToken:cancel.Token);cancel.Cancel();try{Drain(task);return 3;}catch(OperationCanceledException){}}
        device.BeginFrame(Color32.Black);renderer.Begin3D(camera);renderer.DrawMesh(mesh,material,transform);renderer.End3D();
        tail.RetireAfter(device.InsertFence());Check(tail.ActiveBytes==0&&tail.RetiredBytes>0&&!tail.TryCollect());device.EndFrame();
        Check(tail.TryCollect()&&tail.PhysicalBytes==0&&device.NativeBytes==before);return 0;
    }
}
