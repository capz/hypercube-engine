using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dolphin.Graphics;
using Dolphin.Storage;
using Scheduler=Dolphin.Threading.Tasks.Scheduler;

namespace Hypercube.GameCube;

/// <summary>Experimental opt-in native trilinear mode. Owns one complete,
/// contiguous chain; no individual level can be released. Borrow and reserve
/// a bounded staging buffer until LoadAsync completes; no source-buffer disposal
/// or promotion-time GPU wait is hidden in the loader.</summary>
public sealed class ResidentMipTail : IDisposable
{
    private Texture2D? texture;
    private GpuFence finalReader;
    private bool retiring,disposed;
    public ResidentMipTailInfo Info {get;}
    public int LogicalBaseLevel {get;}
    public int ActiveBytes=>!retiring&&texture!=null?Info.NativeBytes:0;
    public int RetiredBytes=>retiring&&texture!=null?Info.NativeBytes:0;
    public int PhysicalBytes=>texture==null?0:Info.NativeBytes;
    public Texture2D Texture=>!disposed&&!retiring&&texture!=null?texture:throw new InvalidOperationException("Tail is retired or disposed.");
    private ResidentMipTail(Texture2D texture,ResidentMipTailInfo info,int logical){this.texture=texture;Info=info;LogicalBaseLevel=logical;}
    public static async Task<ResidentMipTail> LoadAsync(string path,uint packageCrc32,NativeBuffer staging,int logicalBaseLevel=0,
        int maximumNativeBytes=65536,int maximumDimension=128,CancellationToken cancellationToken=default)
    {
        if(!Scheduler.IsApplicationThread)throw new InvalidOperationException("Create mip-tail ownership on the application executor.");
        if(staging==null||staging.Length<32||(staging.Length&31)!=0||logicalBaseLevel<0||logicalBaseLevel>10)throw new ArgumentException("Invalid staging buffer or logical base.");
        cancellationToken.ThrowIfCancellationRequested();var header=new byte[32];long length;
        using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {length=file.Length;int done=0;while(done<32){int n=file.Read(header,done,32-done);if(n==0)throw new EndOfStreamException();done+=n;}}
        var info=ResidentMipTailInfo.Read(header,length,maximumNativeBytes,maximumDimension);
        // Explicitly bounded managed storage is limited to this optional tail;
        // the normal progressive path never allocates an image-sized array.
        var package=new byte[32+info.NativeBytes];Array.Copy(header,package,32);uint crc=Binary.Crc(header,0,32);
        using(var source=new AssetStream(path))
        {
            if(source.Length!=length)throw new IOException("Mip-tail source changed during bootstrap.");
            for(int at=0;at<info.NativeBytes;)
            {
                int count=Math.Min(info.NativeBytes-at,Math.Min(32768,staging.Length));
                int actual=await source.ReadAtAsync(32L+at,staging,0,count,cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();if(actual!=count)throw new EndOfStreamException();
                crc=staging.ComputeCrc32(0,count,crc);staging.Read(package,0,32+at,count);at+=count;
            }
        }
        if(crc!=packageCrc32)throw new InvalidDataException("Mip-tail checksum mismatch.");cancellationToken.ThrowIfCancellationRequested();
        return new ResidentMipTail(Texture2D.FromPackage(package,true),info,logicalBaseLevel);
    }
    public void Apply(Material material,bool secondary=false)
    {
        if(material==null)throw new ArgumentNullException("material");
        var sampler=new SamplerState(TextureMinFilter.LinearMipmapLinear,TextureMagFilter.Linear,maxLod:Info.Levels-1);
        if(secondary){material.SecondaryTexture=Texture;material.SecondarySampler=sampler;}else{material.Texture=Texture;material.Sampler=sampler;}
    }
    public void RetireAfter(GpuFence fence)
    {if(disposed||retiring||!fence.IsValid)throw new InvalidOperationException("Supply the final reader once.");finalReader=fence;retiring=true;}
    public bool TryCollect()
    {
        if(texture==null)return true;if(!retiring||!texture.TryDisposeAfterFence(finalReader))return false;texture=null;return true;
    }
    public void Dispose()
    {
        if(disposed)return;if(retiring&&!TryCollect())throw new InvalidOperationException("Wait for the final mip-tail GPU fence.");
        texture?.Dispose();texture=null;disposed=true;
    }
}
