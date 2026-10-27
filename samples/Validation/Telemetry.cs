using Dolphin.Graphics;

internal sealed class Telemetry : System.IDisposable
{
    private readonly Draw2D draw=new Draw2D(GraphicsDevice.Default,new Draw2DOptions{Width=640,Height=480,VertexCapacity=4096});
    internal readonly int[] Data=new int[32];
    internal bool Compact;
    internal void Draw()
    {
        draw.Begin();
        if(Compact)
        {
            draw.FillRectangle(new RectF(0,448,640,32),new Color32(16,16,16));
            for(int at=0;at<512;)
            {
                int length=Hypercube.TelemetryRuns.Length(Data,at);
                if(Hypercube.TelemetryRuns.Lit(Data,at))
                    draw.FillRectangle(new RectF(at%64*10,448+at/64*4,length*10,4),new Color32(240,240,240));
                at+=length;
            }
            draw.End();return;
        }
        for(int i=0;i<32;i++)
        {
            int value=Data[i];if(value<0)value=0;if(value>65535)value=65535;
            for(int bit=0;bit<16;bit++)
            {
                int at=i*16+bit;byte shade=(byte)((value&(1<<bit))!=0?240:16);
                draw.FillRectangle(new RectF(at%64*10,448+at/64*4,10,4),new Color32(shade,shade,shade));
            }
        }
        draw.End();
    }
    public void Dispose()=>draw.Dispose();
}
