using System;
using System.IO;

namespace Hypercube;

public static class Binary
{
    public static uint U32(byte[] data, int at) =>
        (uint)data[at] | (uint)data[at + 1] << 8 | (uint)data[at + 2] << 16 | (uint)data[at + 3] << 24;
    public static int I32(byte[] data, int at) => unchecked((int)U32(data, at));
    public static int U16(byte[] data, int at) => data[at] | data[at + 1] << 8;
    public static int I16(byte[] data, int at) => (short)U16(data, at);
    public static uint Crc(byte[] data, int at, int count, uint initial = 0)
    {
        uint crc = ~initial;
        for (int i = 0; i < count; i++)
        {
            crc ^= data[at + i];
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~crc;
    }
}

public readonly struct AssetRecord
{
    public readonly int Id, Kind, Offset, Length, Format, Group, Mip, Cell, Parent, Width, Useful, Material, Variant;
    public readonly uint Checksum;
    internal AssetRecord(byte[] index, int at)
    {
        Id = Binary.I32(index, at); Kind = Binary.I32(index, at + 4);
        Offset = Binary.I32(index, at + 8); Length = Binary.I32(index, at + 12);
        Checksum = Binary.U32(index, at + 16); Format = Binary.I32(index, at + 20);
        Group = Binary.I32(index, at + 24); Mip = Binary.I32(index, at + 28);
        Cell = Binary.I32(index, at + 32); Parent = Binary.I32(index, at + 36);
        Width = Binary.I32(index, at + 40); Useful = Binary.I32(index, at + 44);
        Material = Binary.I32(index, at + 48); Variant = Binary.I32(index, at + 52);
    }
}

public sealed class AssetCatalog
{
    private readonly AssetRecord[] records;
    private readonly int[] textures = new int[576], collisions = new int[24], meshHeads = new int[96], patchHeads = new int[85];
    private readonly int[] next;
    public int Count { get; }
    public int Length { get; }
    public int DataOffset { get; }
    public AssetCatalog(byte[] header, byte[] index, long actualLength)
    {
        if (header.Length != 32 || Binary.U32(header, 0) != 0x4b504348 || Binary.U32(header, 4) != 1 ||
            Binary.U32(header, 12) != 32 || Binary.U32(header, 28) != 0)
            throw new IOException("Unsupported Hypercube pack header.");
        Count = Binary.I32(header, 8); DataOffset = Binary.I32(header, 16); Length = Binary.I32(header, 20);
        if (Count <= 0 || Count > 4096 || index.Length != Count * 64 ||
            DataOffset < 32 + index.Length || (DataOffset & 31) != 0 || Length != actualLength || DataOffset > Length ||
            Binary.Crc(index, 0, index.Length) != Binary.U32(header, 24))
            throw new IOException("Invalid pack index bounds or checksum.");
        records = new AssetRecord[Count];
        for(int i=0;i<Count;i++)records[i]=new AssetRecord(index,i*64);
        next = new int[Count + 1];var meshTails=new int[96];
        int end = DataOffset, scenes = 0;
        for (int i = 1; i <= Count; i++)
        {
            var r = Get(i);
            if (r.Id != i || r.Kind < 1 || r.Kind > 5 || r.Offset < end || (r.Offset & 31) != 0 ||
                r.Length <= 0 || r.Length > Length - r.Offset || r.Parent < 0 || r.Parent > Count ||
                Binary.U32(index, (i - 1) * 64 + 56) != 0 || Binary.U32(index, (i - 1) * 64 + 60) != 0)
                throw new IOException("Invalid asset record range, order or dependency.");
            if (r.Kind == 1)
            {
                int expected = r.Format == 6 ? r.Width * r.Width * 4 : r.Format == 14 ? r.Width * r.Width / 2 : -1;
                if ((r.Group != 1 && r.Group != 2 && r.Group != 4) || r.Mip < 0 || r.Mip > 3 ||
                    r.Width < 8 || r.Width > 1024 || (r.Width & 7) != 0 || r.Useful <= 0 ||
                    r.Useful + 8 > r.Width || r.Length != expected)
                    throw new IOException("Invalid native texture descriptor.");
            }
            end = r.Offset + r.Length;
            if (r.Kind == 1)
                {
                int key=TextureIndex(r.Format,r.Group,r.Mip,r.Cell);
                int cols=(6+r.Group-1)/r.Group;
                if(r.Cell>=cols*((4+r.Group-1)/r.Group)||textures[key]!=0)throw new IOException("Duplicate or invalid texture tile.");
                textures[key] = i;
                }
            else if (r.Kind == 2)
            {
                if(r.Length<12||r.Length>16384||r.Cell<0||r.Cell>=24||r.Mip<0||r.Mip>2||r.Variant<0||r.Variant>1)throw new IOException("Invalid geometry key.");
                int key=r.Cell*4+(r.Variant==0?3:r.Mip);
                if(meshHeads[key]==0)meshHeads[key]=i;else next[meshTails[key]]=i;
                meshTails[key]=i;
            }
            else if(r.Kind==3)
            {if(r.Cell<0||r.Cell>=24||r.Length!=44||collisions[r.Cell]!=0)throw new IOException("Invalid collision record.");collisions[r.Cell]=i;}
            else if(r.Kind==4)
            {if(r.Length<12||r.Length>16384||r.Cell<0||r.Cell>=85||r.Material<0||r.Material>1)throw new IOException("Invalid patch record.");next[i]=patchHeads[r.Cell];patchHeads[r.Cell]=i;}
            else if(r.Kind==5&& (r.Length!=56||++scenes>1))throw new IOException("Invalid scene metadata.");
        }
        for (int i = 1; i <= Count; i++)
        {
            var r = Get(i);
            if (r.Parent != 0)
            {
                var p = Get(r.Parent);
                if (r.Kind != 1 || p.Kind != 1 || p.Group != r.Group || p.Cell != r.Cell ||
                    p.Format != r.Format || p.Mip != r.Mip + 1) throw new IOException("Invalid fallback hierarchy.");
            }
        }
    }
    public void ValidateId(int id)
    {
        if(id<1||id>Count)throw new ArgumentOutOfRangeException("id");
    }
    public AssetRecord Get(int id)
    {
        if (id < 1 || id > Count) throw new ArgumentOutOfRangeException("id");
        return records[id-1];
    }
    public int Texture(int format, int group, int mip, int cell)
    {
        return textures[TextureIndex(format,group,mip,cell)];
    }
    private static int TextureIndex(int format,int group,int mip,int cell)
    {
        if((format!=6&&format!=14)||(group!=1&&group!=2&&group!=4)||mip<0||mip>3||cell<0||cell>=24)
            throw new IOException("Invalid texture lookup key.");
        return (format==6?0:288)+(group==1?0:group==2?96:192)+mip*24+cell;
    }
    public int FirstMesh(int cell,int lod,bool raw=false)=>meshHeads[cell*4+(raw?3:lod)];
    public int FirstPatch(int node)=>patchHeads[node];
    public int Next(int id)=>next[id];
    public int Collision(int cell)=>collisions[cell];
}
