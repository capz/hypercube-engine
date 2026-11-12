using System;
using Hypercube;

internal static class CompactIndexChecks
{
    private static void Check(bool condition,string message)
    {if(!condition)throw new Exception(message);}
    private static void Word(byte[] bytes,int at,int value)=>BitConverter.GetBytes(value).CopyTo(bytes,at);

    internal static void Run()
    {
        // The highest IDs exercise every compact lookup/link category, and the
        // 4092 mesh records retain their original forward insertion order.
        const int count=4096,geometryCount=4092;
        var index=new byte[count*64];var header=new byte[32];int offset=32+index.Length;
        for(int id=1;id<=count;id++)
        {
            int at=(id-1)*64,kind=id<=geometryCount?2:id<=4094?4:id==4095?3:1;
            int length=kind==3?44:kind==1?128:12;
            Word(index,at,id);Word(index,at+4,kind);Word(index,at+8,offset);Word(index,at+12,length);
            if(kind==4)Word(index,at+32,84);
            if(kind==3)Word(index,at+32,23);
            if(kind==1)
            {Word(index,at+20,14);Word(index,at+24,4);Word(index,at+28,3);Word(index,at+32,1);Word(index,at+40,16);Word(index,at+44,8);}
            offset+=(length+31)&~31;
        }
        Word(header,0,0x4b504348);Word(header,4,1);Word(header,8,count);Word(header,12,32);
        Word(header,16,32+index.Length);Word(header,20,offset);
        Word(header,24,unchecked((int)Binary.Crc(index,0,index.Length)));
        var catalog=new AssetCatalog(header,index,offset);
        Check(catalog.Count==4096&&catalog.Get(4096).Id==4096,"Maximum record ID survives compact indexes");
        Check(catalog.FirstMesh(0,0,true)==1,"Compact mesh head preserves first record");
        for(int id=1;id<=geometryCount;id++)Check(catalog.Next(id)==(id==geometryCount?0:id+1),"Compact forward mesh links preserve ordering and terminal zero");
        Check(catalog.FirstPatch(84)==4094&&catalog.Next(4094)==4093&&catalog.Next(4093)==0,"Compact reverse patch links preserve highest node/record IDs");
        Check(catalog.Collision(23)==4095&&catalog.Texture(14,4,3,1)==4096,"Compact collision/texture indexes preserve maximum record IDs");
        var last=catalog.Get(4096);Array.Clear(index);Array.Clear(header);
        Check(catalog.Get(4096).Equals(last)&&catalog.Texture(14,4,3,1)==4096&&catalog.Next(4094)==4093,"Catalog owns immutable validated metadata after caller buffers change");

        var scheduler=new ResourceScheduler(catalog,256,1024*1024);
        for(int slot=0;slot<256;slot++)
        {
            int id=3841+slot;
            Check(scheduler.Demand(id,ResourcePriority.Visible)==slot&&scheduler.Find(id)==slot,"Compact lookup reaches every scheduler slot");
        }
        Check(scheduler.Find(4096)==255&&scheduler.Find(0)==-1&&scheduler.Find(4097)==-1,"Slot 255 uses a nonzero 16-bit lookup sentinel");
        int oldGeneration=scheduler.Generation(255);
        scheduler.Cancel(4096);scheduler.Cancel(3841);
        Check(scheduler.Find(4096)==-1&&scheduler.Find(3841)==-1,"Cancelled mappings remain missing");
        // Relocate an ID before its previous slot is reused. Reclaiming that
        // stale slot must not erase the ID's newer mapping.
        Check(scheduler.Demand(4096,ResourcePriority.Visible)==0,"Relocated maximum ID uses first free slot");
        Check(scheduler.Demand(1,ResourcePriority.Visible)==255&&scheduler.Find(4096)==0&&scheduler.Find(1)==255,"Stale slot reuse cannot clear a relocated lookup");
        Check(scheduler.Generation(255)!=oldGeneration,"Slot reuse renews generation");
        scheduler.Cancel(1);Check(scheduler.Demand(2,ResourcePriority.Gameplay)==255&&scheduler.Find(1)==-1,"Reclaimed slot clears its own obsolete mapping");
        var batch=new int[1];
        Check(scheduler.BeginBatch(0,32,1,batch,out _,out _)==1&&batch[0]==255,"Highest compact slot can issue a prioritized read");
        int generation=scheduler.Generation(255);
        scheduler.Cancel(2);Check(scheduler.State(255)==ResourceState.Retiring,"Issued highest-slot cancellation waits for drain");
        Check(!scheduler.Complete(255,generation,true,0)&&scheduler.Find(2)==-1,"Drained highest-slot generation cannot publish");
        Check(scheduler.Demand(3,ResourcePriority.Gameplay)==255&&scheduler.Find(3)==255,"Highest slot remains reusable after drain");
        Console.WriteLine("Compact indexes: maximum 4096 records, slot 255, immutable metadata, stale relocation and cancellation/drain passed.");
    }
}
