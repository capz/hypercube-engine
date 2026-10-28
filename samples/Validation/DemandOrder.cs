internal static class DemandOrder
{
    // Stable nearest-first order. Equal distances retain ascending cell IDs,
    // exactly matching the previous repeated-minimum scan. Caller owns scratch.
    internal static int Build(int[] output,bool[] visible,float[] distances)
    {
        int count=0;
        for(int cell=0;cell<visible.Length;cell++)
        {
            if(!visible[cell]||distances[cell]>=12)continue;
            int at=count;
            while(at>0&&distances[output[at-1]]>distances[cell])
            {output[at]=output[at-1];at--;}
            output[at]=cell;count++;
        }
        return count;
    }
}
