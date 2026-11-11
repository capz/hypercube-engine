using System;
using System.IO;

namespace Hypercube;

/// <summary>Opt-in bounded, indivisible DNTX v2 hardware mip tail. This is a
/// different residency mode from independently reclaimable DNLT family levels.</summary>
public readonly struct ResidentMipTailInfo
{
    public readonly int Width,Height,Format,Levels,NativeBytes;
    private ResidentMipTailInfo(int width,int height,int format,int levels,int bytes)
    {Width=width;Height=height;Format=format;Levels=levels;NativeBytes=bytes;}
    public static ResidentMipTailInfo Read(byte[] header,long fileLength,int maximumNativeBytes=65536,int maximumDimension=128)
    {
        if(maximumNativeBytes<32||maximumNativeBytes>262144||maximumDimension<2||maximumDimension>256||(maximumDimension&(maximumDimension-1))!=0)throw new ArgumentOutOfRangeException("maximumNativeBytes");
        if(header==null||header.Length!=32||Binary.U32(header,0)!=0x58544e44||Binary.U32(header,4)!=2||Binary.U32(header,28)!=0)throw new IOException("Expected a DNTX v2 mip tail.");
        int format=Binary.I32(header,8),w=Binary.I32(header,12),h=Binary.I32(header,16),declared=Binary.I32(header,20),levels=Binary.I32(header,24);
        if(w<1||h<1||w>maximumDimension||h>maximumDimension||(w&(w-1))!=0||(h&(h-1))!=0||(format!=6&&format!=14)||levels<2||levels>9)throw new IOException("Mip tail dimensions, format or chain limit.");
        int bytes=0,count=0,mw=w,mh=h;
        while(true){bytes+=TextureFamilyReader.NativeBytes(mw,mh,format);count++;if(mw==1&&mh==1)break;mw=Math.Max(1,mw/2);mh=Math.Max(1,mh/2);}
        if(count!=levels||declared!=bytes||bytes>maximumNativeBytes||fileLength!=32L+bytes)throw new IOException("Mip tail must be complete and fit its entire native byte budget.");
        return new ResidentMipTailInfo(w,h,format,levels,bytes);
    }
}
