using System;
using Hypercube.GameCube;

internal static class TexturePoolChecks
{
    private static void Check(bool value,string message)
    {if(!value)throw new Exception(message);}
    internal static void Run()
    {
        using var world=new PackedWorld("out/assets/world.hcp");
        var record=world.Catalog.Get(world.Catalog.Texture(14,1,0,0));
        int pair=record.Length*2;
        using(var fenced=new TexturePool())
        {
            int old=fenced.Acquire(record,0);fenced.Pin(old,10);fenced.Unbind(old,10);
            Check(fenced.ExtraBytes(record,9)==pair,"A GPU reader was treated as reusable storage.");
            Check(fenced.TrimFree(9)==0,"Pressure trimming freed a GPU reader.");
            int next=fenced.Acquire(record,9);
            Check(next!=old&&fenced.PhysicalBytes==2*pair,"Acquisition reused an unfinished GPU allocation.");
            Check(fenced.Acquire(record,10)==old&&fenced.Reuses==1,"Completed storage was not reused.");
            fenced.Unbind(old,11);fenced.Unbind(next,11);
            Check(fenced.TrimCold(12,10,10,0,0,2)==0,"Cold trimming skipped its idle dwell.");
            Check(fenced.TrimCold(21,9,10,0,0,2)==1&&fenced.PhysicalBytes==pair,
                "Cold trimming must retain incomplete readers and reclaim completed idle storage.");
            Check(fenced.TrimCold(21,10,10,0,0,2)==1&&fenced.PhysicalBytes==0,"Completed cold storage was not physically freed.");
        }
        using(var warm=new TexturePool())
        {
            int first=warm.Acquire(record,0),second=warm.Acquire(record,0),third=warm.Acquire(record,0);
            warm.Unbind(first,10);warm.Unbind(second,10);warm.Unbind(third,10);
            int allocations=warm.Allocations;
            Check(warm.Acquire(record,0)==first&&warm.Allocations==allocations&&warm.Reuses==1,
                "Demand reversal inside the warm window allocated instead of reusing.");
            warm.Unbind(first,12);
            Check(warm.TrimCold(14,0,5,2*pair,pair,1)==0,"Warm dwell was not preserved.");
            Check(warm.TrimCold(15,0,5,2*pair,pair,1)==1&&warm.IdleBytes==2*pair,
                "Cold trim did not obey its per-pump release limit.");
            Check(warm.TrimCold(16,0,5,2*pair,pair,1)==1&&warm.IdleBytes==pair,
                "Byte hysteresis stopped at its high watermark instead of continuing to low.");
            Check(warm.TrimCold(100,0,5,2*pair,pair,1)==0&&warm.PhysicalBytes==pair,
                "A bounded warm reuse reserve was discarded below its low watermark.");
        }
        Console.WriteLine("Texture pool: GPU fences, warm reversal reuse, dwell, byte hysteresis, bounded trim and physical reclamation passed.");
    }
}
