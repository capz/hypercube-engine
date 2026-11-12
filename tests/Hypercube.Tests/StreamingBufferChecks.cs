using System;
using Hypercube;

internal static class StreamingBufferChecks
{
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Reject(Action action)
    {try{action();}catch(ArgumentException){return;}throw new Exception("Invalid staging policy accepted.");}
    public static void Run(AssetCatalog catalog)
    {
        int capacity=StreamingBufferPolicy.Capacity(catalog);
        Check(capacity==73984,"Validation fixture staging reserves its largest RGBA8 image, not 128 KiB.");
        Check(131072-capacity==57088,"Fixture staging saving is 57,088 native payload bytes.");
        Check(StreamingBufferPolicy.Capacity(catalog,131040)==131040,"Explicit legacy coalescing cap.");
        Check(StreamingBufferPolicy.CoalescingLimit(32768,65536)==32768,"32 KiB coalescing retained.");
        Check(StreamingBufferPolicy.CoalescingLimit(65536,65536)==65536,"64 KiB coalescing retained.");
        Check(StreamingBufferPolicy.CoalescingLimit(131040,65536)==65536,"Runtime target is bounded by reserved coalescing storage.");
        foreach(int requested in new[]{32768,65536,131040})
        {
            var scheduler=new ResourceScheduler(catalog,8,1024*1024);scheduler.BeginDemand();
            scheduler.Demand(catalog.Texture(6,4,0,0),ResourcePriority.Visible);
            int limit=scheduler.RequiredBatchBytes(0,StreamingBufferPolicy.CoalescingLimit(requested,65536));
            Check(limit==73984,"A large single image overrides the coalescing target.");
            var slots=new int[8];Check(scheduler.BeginBatch(0,limit,8,slots,out _,out int actual)==1,"Large image remains schedulable.");
            Check(((actual+31)&~31)<=capacity,"Aligned large-image read fits its reserved buffer.");
        }
        // Adjacent 44-byte collision records are separated by 20 alignment bytes.
        // Coalescing them reads 108 bytes and needs 128 bytes after SDK padding.
        var collisions=Catalog(3,44,0,2);
        Check(StreamingBufferPolicy.Capacity(collisions,32)==64,"A small single record includes native read padding.");
        Check(StreamingBufferPolicy.Capacity(collisions,108)==128,"An unaligned coalescing cap reserves padding.");
        var batch=new ResourceScheduler(collisions,2,1024);batch.BeginDemand();
        batch.Demand(1,ResourcePriority.Gameplay);batch.Demand(2,ResourcePriority.Gameplay);
        var ids=new int[2];Check(batch.BeginBatch(0,108,2,ids,out _,out int bytes)==2&&bytes==108,"Near-adjacent record coalescing remains intact.");
        Check(((bytes+31)&~31)==StreamingBufferPolicy.Capacity(collisions,108),"Rounded batch ends exactly at reserved capacity.");
        var supported=Catalog(1,176*176*4,176,1);
        Check(StreamingBufferPolicy.Capacity(supported)==123904,"A supported image larger than the default cap reserves its full source.");
        var unsupported=Catalog(1,1024*1024*4,1024,1);
        Check(StreamingBufferPolicy.Capacity(unsupported)==65536,"Unused unsupported fine levels do not force a large allocation.");
        var unsupportedDemand=new ResourceScheduler(unsupported,1,8*1024*1024);unsupportedDemand.BeginDemand();unsupportedDemand.Demand(1,ResourcePriority.Visible);
        Check(unsupportedDemand.RequiredBatchBytes(0,65536)>StreamingBufferPolicy.MaximumRecordBytes,"Demanded unsupported images retain the existing Queue rejection.");
        foreach(int maximum in new[]{-1,0,31,131041,int.MaxValue})Reject(()=>StreamingBufferPolicy.Capacity(catalog,maximum));
        Reject(()=>StreamingBufferPolicy.Capacity(null!));Reject(()=>StreamingBufferPolicy.CoalescingLimit(31,65536));Reject(()=>StreamingBufferPolicy.CoalescingLimit(32,0));
        Console.WriteLine("Streaming staging: catalog sizing, explicit caps, dynamic targets, oversized records and read padding passed.");
    }
    private static AssetCatalog Catalog(int kind,int length,int width,int count)
    {
        var header=new byte[32];var table=new byte[count*64];int offset=32+table.Length;
        void Word(byte[] data,int at,int value)=>BitConverter.GetBytes(value).CopyTo(data,at);
        for(int i=0;i<count;i++)
        {
            int at=i*64;Word(table,at,i+1);Word(table,at+4,kind);Word(table,at+8,offset);Word(table,at+12,length);
            if(kind==1){Word(table,at+20,6);Word(table,at+24,1);Word(table,at+40,width);Word(table,at+44,width-8);}
            Word(table,at+32,i);offset=(offset+length+31)&~31;
        }
        Word(header,0,0x4b504348);Word(header,4,1);Word(header,8,count);Word(header,12,32);Word(header,16,32+table.Length);Word(header,20,offset);
        BitConverter.GetBytes(Binary.Crc(table,0,table.Length)).CopyTo(header,24);
        return new AssetCatalog(header,table,offset);
    }
}
