using System;
using System.IO;
using Dolphin.Graphics;

namespace Hypercube.GameCube;

public sealed class PackedWorld : IDisposable
{
    private readonly FileStream bootstrap;
    // Reserve one reusable decode buffer before variable-size bootstrap allocations.
    public readonly byte[] Scratch = new byte[16384];
    public AssetCatalog Catalog { get; }
    public readonly Mesh?[] Patches;
    public readonly int[] TriangleCounts,VertexCounts;
    public readonly Texture2D[] CoarseTextures = new Texture2D[2];
    public readonly SceneTree Tree = new SceneTree();
    public readonly float[] NodeMinY=new float[85],NodeMaxY=new float[85];
    public int ParentMeshCount, ParentTriangles;
    public float MinY=-1, MaxY=3;
    public bool VerifiedOccluder;
    public readonly float[] Occluder=new float[6];
    public PackedWorld(string path)
    {
        bootstrap=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        var header=new byte[32];ReadExact(bootstrap,header,32);
        int count=Binary.I32(header,8);
        if(count<1||count>4096)throw new IOException("Pack record count exceeds validation bound.");
        var table=new byte[count*64];ReadExact(bootstrap,table,table.Length);
        Catalog=new AssetCatalog(header,table,bootstrap.Length);
        Patches=new Mesh?[count+1];TriangleCounts=new int[count+1];VertexCounts=new int[count+1];
        for(int i=0;i<85;i++){NodeMinY[i]=100;NodeMaxY[i]=-100;}
        for(int tile=0;tile<2;tile++)
        {
            var r=Catalog.Get(Catalog.Texture(14,4,3,tile));
            var bytes=ReadPayload(r);
            var texture=Texture2D.CreateNative(r.Width,r.Width,NativeTextureFormat.Cmpr,true);
            texture.ReplaceNative(bytes);CoarseTextures[tile]=texture;
        }
        int native=0;
        for(int id=1;id<=count;id++)
        {
            var r=Catalog.Get(id);
            if(r.Kind==4)
            {
                var payload=ReadPayload(r);
                var texture=Catalog.Get(Catalog.Texture(14,4,3,r.Material));
                var mesh=DecodeMesh(payload,r,texture,4);
                Patches[id]=mesh;ParentMeshCount++;ParentTriangles+=Binary.U16(payload,6)/3;
                TriangleCounts[id]=Binary.U16(payload,6)/3;VertexCounts[id]=Binary.U16(payload,4);
                for(int k=0;k<Binary.U16(payload,4);k++)
                {
                    float y=Binary.I32(payload,12+k*32+4)/65536f;
                    if(y<NodeMinY[r.Cell])NodeMinY[r.Cell]=y;
                    if(y>NodeMaxY[r.Cell])NodeMaxY[r.Cell]=y;
                }
                native+=mesh.NativeBytes;
                if(native>1024*1024)throw new IOException("Coarse patch hierarchy exceeds its 1 MiB native budget.");
            }
            else if(r.Kind==5)
            {
                var payload=ReadPayload(r);
                if(r.Length!=56||Binary.U32(payload,0)!=0x44574348)throw new IOException("Invalid scene metadata.");
                MinY=Binary.I32(payload,8)/65536f;MaxY=Binary.I32(payload,20)/65536f;
                VerifiedOccluder=Binary.I32(payload,28)==1;
                for(int k=0;k<6;k++)Occluder[k]=Binary.I32(payload,32+k*4)/65536f;
            }
        }
        if(ParentMeshCount==0)throw new IOException("The pack has no permanent coarse hierarchy.");
    }
    public bool HasCell(int cell)=>Catalog.FirstPatch(Tree.Leaf(cell))!=0;
    private byte[] ReadPayload(AssetRecord r)
    {
        if(r.Length>16384)throw new IOException("Bootstrap payload exceeds its managed scratch limit.");
        var bytes=Scratch;bootstrap.Seek(r.Offset,SeekOrigin.Begin);ReadExact(bootstrap,bytes,r.Length);
        if(Binary.Crc(bytes,0,r.Length)!=r.Checksum)throw new IOException("Bootstrap asset checksum mismatch.");
        return bytes;
    }
    private static void ReadExact(FileStream stream,byte[] bytes,int count)
    {
        int done=0;
        while(done<count){int n=stream.Read(bytes,done,count-done);if(n==0)throw new EndOfStreamException();done+=n;}
    }
    public static Mesh DecodeMesh(byte[] bytes,AssetRecord record,AssetRecord texture,int group)
    {
        if(bytes.Length<12||Binary.U32(bytes,0)!=0x534d4348)throw new IOException("Invalid mesh payload.");
        int nv=Binary.U16(bytes,4),ni=Binary.U16(bytes,6);
        if(nv<3||nv>768||ni<3||ni>768||ni%3!=0||12+nv*32+ni*2!=record.Length)
            throw new IOException("Invalid bounded mesh dimensions.");
        var vertices=new VertexPositionNormalColorTexture[nv];var indices=new ushort[ni];
        int cx=record.Cell%6,cz=record.Cell/6;
        for(int i=0;i<nv;i++)
        {
            int at=12+i*32;
            float u=Binary.U16(bytes,at+18)/65535f,v=Binary.U16(bytes,at+20)/65535f;
            if(record.Kind==2){u=((cx%group)+u)/group;v=((cz%group)+v)/group;}
            u=(4+u*texture.Useful)/texture.Width;v=(4+v*texture.Useful)/texture.Width;
            vertices[i]=new VertexPositionNormalColorTexture(
                new Vector3(Binary.I32(bytes,at)/65536f,Binary.I32(bytes,at+4)/65536f,Binary.I32(bytes,at+8)/65536f),
                new Vector3(Binary.I16(bytes,at+12)/32767f,Binary.I16(bytes,at+14)/32767f,Binary.I16(bytes,at+16)/32767f),
                new Color32(bytes[at+22],bytes[at+23],bytes[at+24],bytes[at+25]),new Vector2(u,v));
        }
        for(int i=0;i<ni;i++)
        {int value=Binary.U16(bytes,12+nv*32+i*2);if(value>=nv)throw new IOException("Mesh index is out of bounds.");indices[i]=(ushort)value;}
        return Mesh.Create(vertices,indices);
    }
    public void Dispose()
    {
        bootstrap.Dispose();
        for(int i=0;i<Patches.Length;i++)Patches[i]?.Dispose();
        for(int i=0;i<CoarseTextures.Length;i++)CoarseTextures[i]?.Dispose();
    }
}
