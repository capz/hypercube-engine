namespace Hypercube;

public static class MaterialMotion
{
    // Speed is texture repeats per second, not metres per second.
    public static float Offset(double seconds,float speed)
    {
        if(double.IsNaN(seconds)||double.IsInfinity(seconds)||float.IsNaN(speed)||float.IsInfinity(speed))throw new ArgumentOutOfRangeException();
        double v=seconds*speed;if(double.IsInfinity(v))throw new ArgumentOutOfRangeException();
        float wrapped=(float)(v-Math.Floor(v));return wrapped>=1?0:wrapped;
    }
}
public static class CloudMask
{
    public const int Size=64;
    public static float Cover(float value)=>float.IsNaN(value)?0:Math.Max(0,Math.Min(1,value));
    public static byte Ambient(float cover)=>(byte)(255-24*Cover(cover));
    public static byte Shade(float cover){float c=Cover(cover);return (byte)(255-44*4*c*(1-c));}
    public static byte Sample(int x,int y)
    {
        double u=(x%Size+Size)%Size*Math.PI*2/Size,v=(y%Size+Size)%Size*Math.PI*2/Size;
        double n=.5+.24*Math.Sin(u+Math.Sin(v))+.17*Math.Cos(v*2-u)+.09*Math.Sin(u*3+v*2);
        n=Math.Max(0,Math.Min(1,(n-.28)/.52));return (byte)(n*n*(3-2*n)*255);
    }
    public static byte[] Texture()
    {var data=new byte[4096];int at=0;for(int y=0;y<64;y+=4)for(int x=0;x<64;x+=8)for(int dy=0;dy<4;dy++)for(int dx=0;dx<8;dx++)data[at++]=Sample(x+dx,y+dy);return data;}
}
