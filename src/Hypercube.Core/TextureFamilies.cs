using System;
using System.IO;

namespace Hypercube;

public readonly struct TextureUv
{
    public readonly float ScaleU, ScaleV, OffsetU, OffsetV;
    public TextureUv(float scaleU, float scaleV, float offsetU = 0, float offsetV = 0)
    { ScaleU=scaleU;ScaleV=scaleV;OffsetU=offsetU;OffsetV=offsetV; }
    public static TextureUv Identity => new TextureUv(1,1);
}

public readonly struct TextureFamilyLevel
{
    public readonly int RecordId, LogicalLevel;
    public readonly TextureUv Uv;
    internal TextureFamilyLevel(int record, int logical, TextureUv uv){RecordId=record;LogicalLevel=logical;Uv=uv;}
}

/// <summary>Immutable shared identity. Levels are ordered finest to coarsest;
/// sparse logical exponents and authored two-quality ladders remain distinct.</summary>
public sealed class TextureFamily
{
    private readonly TextureFamilyLevel[] levels;
    public int Id {get;}
    public string Name {get;}
    public int Width {get;}
    public int Height {get;}
    public int Flags {get;}
    public bool IsQualityPair=>Flags==4;
    public int Count=>levels.Length;
    public int CoarseIndex=>Count-1;
    internal TextureFamily(int id,string name,int width,int height,int flags,TextureFamilyLevel[] levels)
    {Id=id;Name=name;Width=width;Height=height;Flags=flags;this.levels=levels;}
    public TextureFamilyLevel GetLevel(int index)
    {if(index<0||index>=Count)throw new ArgumentOutOfRangeException("index");return levels[index];}
    // Choose the nearest available level at least as detailed as the request.
    public int IndexForLogical(int logical)
    {int result=0;for(int i=1;i<Count;i++){if(levels[i].LogicalLevel>logical)break;result=i;}return result;}
}

internal static class TextureFamilyReader
{
    private static float Float(byte[] data,int at)=>Binary.F32(data,at);
    private static bool Finite(float x)=>!float.IsNaN(x)&&!float.IsInfinity(x);
    internal static int NativeBytes(int w,int h,int format)
    {if(w<1||h<1||w>1024||h>1024)return -1;return format==6?((w+3)/4)*((h+3)/4)*64:format==14?((w+7)/8)*((h+7)/8)*32:-1;}
    internal static TextureFamily[] Read(AssetCatalog catalog,byte[] data,int start,int count)
    {
        if(count<1||count>128||data.Length-start<count*128)throw new IOException("Invalid family table bounds.");
        var families=new TextureFamily[count];var assigned=new bool[catalog.Count+1];int end=start+count*128;
        for(int i=0;i<count;i++)
        {
            int at=start+i*128,id=Binary.I32(data,at),flags=Binary.I32(data,at+4),w=Binary.I32(data,at+8),h=Binary.I32(data,at+12);
            int n=Binary.I32(data,at+16),offset=Binary.I32(data,at+20),nameLength=Binary.I32(data,at+28);
            if(id!=i+1||(flags!=0&&flags!=1&&flags!=3&&flags!=4)||w<1||h<1||w>1024||h>1024||
               n<1||n>11||(flags==0&&n!=1)||(flags==4&&n!=2)||offset!=end-start||n*32>data.Length-end||
               Binary.U32(data,at+24)!=0||nameLength<1||nameLength>80)
                throw new IOException("Invalid texture family descriptor.");
            for(int k=32;k<48;k++)if(data[at+k]!=0)throw new IOException("Unknown family flags.");
            var chars=new char[nameLength];
            for(int k=0;k<80;k++)
            {
                byte ch=data[at+48+k];
                if(k>=nameLength){if(ch!=0)throw new IOException("Invalid family name padding.");}
                else{if(!((ch>=65&&ch<=90)||(ch>=97&&ch<=122)||(ch>=48&&ch<=57)||ch==47||ch==95||ch==45||ch==46))throw new IOException("Invalid family name.");chars[k]=(char)ch;}
            }
            string name=new string(chars);for(int k=0;k<i;k++)if(families[k].Name==name)throw new IOException("Duplicate family name.");
            bool mip=flags==1||flags==3;
            if(mip&&((w&(w-1))!=0||(h&(h-1))!=0))throw new IOException("Mip family dimensions must be powers of two.");
            var levels=new TextureFamilyLevel[n];int previous=-1;
            for(int k=0;k<n;k++)
            {
                int entry=end+k*32,record=Binary.I32(data,entry),logical=Binary.I32(data,entry+4);
                if(record<1||record>catalog.Count)throw new IOException("Invalid family record reference.");
                var r=catalog.Get(record);var uv=new TextureUv(Float(data,entry+8),Float(data,entry+12),Float(data,entry+16),Float(data,entry+20));
                if(logical<0||logical>10||logical<=previous||(k==0&&logical!=0)||(flags==4&&logical!=k)||
                   (flags==1&&logical!=k)||assigned[record]||r.Kind!=6||r.Group!=id||r.Mip!=logical||
                   !Finite(uv.ScaleU)||!Finite(uv.ScaleV)||!Finite(uv.OffsetU)||!Finite(uv.OffsetV)||
                   uv.ScaleU<=0||uv.ScaleV<=0||uv.OffsetU<0||uv.OffsetV<0||uv.ScaleU+uv.OffsetU>1.000001f||uv.ScaleV+uv.OffsetV>1.000001f||
                   Binary.U32(data,entry+24)!=0||Binary.U32(data,entry+28)!=0)
                    throw new IOException("Invalid texture family level or UV mapping.");
                if((mip||k==0)&&(Math.Abs(r.Width*uv.ScaleU-Math.Max(1,w>>logical))>.001f||Math.Abs(r.Height*uv.ScaleV-Math.Max(1,h>>logical))>.001f))
                    throw new IOException("Family usable dimensions disagree with logical LOD.");
                levels[k]=new TextureFamilyLevel(record,logical,uv);assigned[record]=true;previous=logical;
            }
            if(flags==1&&(w>>previous>1||h>>previous>1))throw new IOException("Incomplete full mip family.");
            for(int k=0;k<n;k++)if(catalog.Get(levels[k].RecordId).Parent!=(k+1<n?levels[k+1].RecordId:0))throw new IOException("Invalid family fallback chain.");
            families[i]=new TextureFamily(id,name,w,h,flags,levels);end+=n*32;
        }
        if(end!=data.Length)throw new IOException("Trailing family metadata.");
        for(int i=1;i<=catalog.Count;i++)if(catalog.Get(i).Kind==6&&!assigned[i])throw new IOException("Unindexed native family level.");
        return families;
    }
}
