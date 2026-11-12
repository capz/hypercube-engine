// Host CLR construction-allocation probe. Never a GameCube heap measurement.
using System;
using Hypercube;

internal static class Program
{
    private static void Word(byte[] data,int at,int value)=>BitConverter.GetBytes(value).CopyTo(data,at);
    private static void Main()
    {
        foreach(int count in new[]{649,4096})
        {
            // Synthetic, valid metadata isolates the record-count allocation cost.
            // Input buffers and JIT warmup are outside both measured windows.
            var index=new byte[count*64];var header=new byte[32];int offset=32+index.Length;
            for(int i=0;i<count;i++)
            {int at=i*64;Word(index,at,i+1);Word(index,at+4,2);Word(index,at+8,offset+i*32);Word(index,at+12,12);}
            Word(header,0,0x4b504348);Word(header,4,1);Word(header,8,count);Word(header,12,32);
            Word(header,16,offset);Word(header,20,offset+count*32);Word(header,24,unchecked((int)Binary.Crc(index,0,index.Length)));
            var warm=new AssetCatalog(header,index,offset+count*32);
            var warmScheduler=new ResourceScheduler(warm,256,1024*1024);
            long start=GC.GetAllocatedBytesForCurrentThread();
            var catalog=new AssetCatalog(header,index,offset+count*32);
            long catalogBytes=GC.GetAllocatedBytesForCurrentThread()-start;
            start=GC.GetAllocatedBytesForCurrentThread();
            var scheduler=new ResourceScheduler(catalog,256,1024*1024);
            long schedulerBytes=GC.GetAllocatedBytesForCurrentThread()-start;
            Console.WriteLine($"count={count} catalogAllocated={catalogBytes} schedulerAllocated={schedulerBytes}");
            GC.KeepAlive(warmScheduler);GC.KeepAlive(scheduler);
        }
    }
}
