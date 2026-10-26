namespace Hypercube;

// Keep the v2/v3 pixel protocol identical while merging neighboring lit cells.
public static class TelemetryRuns
{
    public static bool Lit(int[] data, int bit)
    {
        int value=data[bit/16];
        if(value<0)value=0;if(value>65535)value=65535;
        return (value&(1<<(bit%16)))!=0;
    }
    public static int Length(int[] data, int bit)
    {
        int end=(bit/64+1)*64,at=bit+1;
        bool lit=Lit(data,bit);
        while(at<end&&Lit(data,at)==lit)at++;
        return at-bit;
    }
}
