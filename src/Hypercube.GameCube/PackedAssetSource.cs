using System;
using System.IO;
using Dolphin.Storage;

namespace Hypercube.GameCube;

/// <summary>Bounded header/index bootstrap only. Native payloads are always
/// read asynchronously through Assets, including levels larger than staging.</summary>
public sealed class PackedAssetSource : IDisposable
{
    public AssetCatalog Catalog {get;}
    public AssetStream Assets {get;}
    internal static AssetCatalog ReadCatalog(FileStream file)
    {
        var first=new byte[32];ReadExact(file,first,0,32);
        int version=Binary.I32(first,4);byte[] header=first;
        if(version==2){header=new byte[64];Array.Copy(first,header,32);ReadExact(file,header,32,32);}
        int size=AssetCatalog.MetadataBytes(header);var metadata=new byte[size];ReadExact(file,metadata,0,size);
        return new AssetCatalog(header,metadata,file.Length);
    }
    private static void ReadExact(FileStream file,byte[] bytes,int offset,int count)
    {while(count>0){int n=file.Read(bytes,offset,count);if(n==0)throw new EndOfStreamException();offset+=n;count-=n;}}
    public PackedAssetSource(string path)
    {
        using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))Catalog=ReadCatalog(file);
        Assets=new AssetStream(path);
    }
    public void Dispose()=>Assets.Dispose();
}
