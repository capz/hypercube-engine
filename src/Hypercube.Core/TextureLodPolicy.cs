using System;

namespace Hypercube;

public enum TextureQualityPolicy { CoarseOnly, FullQuality, DistanceMip }

/// <summary>Conservative world-space dimensions per UV axis. Distance is to
/// the nearest part of the bound, rather than its center. Projected bounds can
/// be supplied directly for oblique/nonuniform geometry.</summary>
public readonly struct TextureFootprint
{
    public readonly float PixelsU,PixelsV;
    private static bool Positive(float v)=>v>0&&!float.IsInfinity(v)&&!float.IsNaN(v);
    public TextureFootprint(float projectedPixelsU,float projectedPixelsV,float repeatsU=1,float repeatsV=1)
    {
        if(!Positive(projectedPixelsU)||!Positive(projectedPixelsV)||!Positive(repeatsU)||!Positive(repeatsV))throw new ArgumentOutOfRangeException("projectedPixelsU");
        PixelsU=projectedPixelsU/repeatsU;PixelsV=projectedPixelsV/repeatsV;
        if(!Positive(PixelsU)||!Positive(PixelsV))throw new ArgumentOutOfRangeException("repeatsU");
    }
    public static TextureFootprint Project(float width,float height,float centerDistance,float radius,float verticalFovDegrees,int viewportHeight,float repeatsU=1,float repeatsV=1,float nearDistance=.1f)
    {
        if(!Positive(width)||!Positive(height)||!Positive(nearDistance)||!Positive(verticalFovDegrees)||verticalFovDegrees>=179||viewportHeight<1||
           float.IsNaN(centerDistance)||float.IsInfinity(centerDistance)||float.IsNaN(radius)||float.IsInfinity(radius)||radius<0)throw new ArgumentOutOfRangeException("width");
        float scale=viewportHeight/(2*(float)Math.Tan(verticalFovDegrees*Math.PI/360))/Math.Max(nearDistance,centerDistance-radius);
        return new TextureFootprint(width*scale,height*scale,repeatsU,repeatsV);
    }
}

/// <summary>Per-consumer policy only; all instances referring to the same
/// family share one physical resident through TextureFamilyDemand.</summary>
public sealed class StreamedMaterialTexture
{
    public TextureFamily Family {get;}
    public TextureQualityPolicy Policy {get;set;}
    public int Bias {get;set;}
    public int MinimumLogicalLevel {get;set;}
    internal int Previous=-1,ChangedFrame;
    public StreamedMaterialTexture(TextureFamily family,TextureQualityPolicy policy=TextureQualityPolicy.DistanceMip)
    {Family=family??throw new ArgumentNullException("family");Policy=policy;}
}

public static class TextureLodSelector
{
    public static int Select(StreamedMaterialTexture binding,TextureFootprint footprint,int frame,int minimumDwellFrames=6,float hysteresis=.25f)
    {
        if(minimumDwellFrames<0||hysteresis<0||hysteresis>.5f||float.IsNaN(hysteresis)||frame<0||binding.MinimumLogicalLevel<0||binding.MinimumLogicalLevel>10||binding.Bias < -10||binding.Bias>10)throw new ArgumentOutOfRangeException("frame");
        var family=binding.Family;int selected;
        if(binding.Policy==TextureQualityPolicy.CoarseOnly)selected=family.CoarseIndex;
        else if(binding.Policy==TextureQualityPolicy.FullQuality||family.IsQualityPair)selected=0;
        else if(binding.Policy==TextureQualityPolicy.DistanceMip)
        {
            if(footprint.PixelsU<=0||footprint.PixelsV<=0)throw new ArgumentException("Missing texture footprint.");
            int coarsest=family.GetLevel(family.CoarseIndex).LogicalLevel;
            float u=footprint.PixelsU<=1?coarsest:(float)(Math.Log(family.Width/footprint.PixelsU)/Math.Log(2));
            float v=footprint.PixelsV<=1?coarsest:(float)(Math.Log(family.Height/footprint.PixelsV)/Math.Log(2));
            float raw=Math.Min(u,v)+binding.Bias;
            selected=family.IndexForLogical(Math.Max(0,(int)Math.Floor(raw+.5f)));
            int previous=binding.Previous;
            if(previous>=0&&selected!=previous)
            {
                // Sparse ladders switch when rounding reaches the coarser
                // exponent, not halfway across the missing authored levels.
                float boundary=family.GetLevel(selected<previous?previous:selected).LogicalLevel-.5f;
                float band=(float)(Math.Log(1+hysteresis)/Math.Log(2));
                if(frame-binding.ChangedFrame<minimumDwellFrames||selected<previous&&raw>=boundary-band||selected>previous&&raw<=boundary+band)selected=previous;
            }
        }
        else throw new ArgumentOutOfRangeException("Policy");
        // An explicit quality ceiling must never choose a level finer than it.
        while(selected<family.CoarseIndex&&family.GetLevel(selected).LogicalLevel<binding.MinimumLogicalLevel)selected++;
        if(selected!=binding.Previous){binding.Previous=selected;binding.ChangedFrame=frame;}
        return selected;
    }
}

/// <summary>One frame's finest visible demand per family. Prefetch can request
/// coarse readiness but cannot raise an already visible family's quality.</summary>
public sealed class TextureFamilyDemand
{
    private readonly AssetCatalog catalog;
    private readonly int[] targets;
    private readonly bool[] wanted,visible;
    private readonly ResourcePriority[] priorities;
    private readonly float[] errors;
    public int Frame {get;private set;}
    public TextureFamilyDemand(AssetCatalog catalog)
    {this.catalog=catalog;int n=catalog.FamilyCount+1;targets=new int[n];wanted=new bool[n];visible=new bool[n];priorities=new ResourcePriority[n];errors=new float[n];}
    public void BeginFrame(int frame)
    {if(frame<Frame)throw new ArgumentOutOfRangeException("frame");Frame=frame;for(int i=1;i<targets.Length;i++){wanted[i]=visible[i]=false;targets[i]=catalog.Family(i).CoarseIndex;priorities[i]=ResourcePriority.Optional;errors[i]=0;}}
    public void Demand(StreamedMaterialTexture binding,TextureFootprint footprint,ResourcePriority priority=ResourcePriority.Visible)
    {
        if(catalog.Family(binding.Family.Id)!=binding.Family)throw new ArgumentException("Family belongs to another catalog.");
        int index=TextureLodSelector.Select(binding,footprint,Frame);
        DemandIndex(binding.Family.Id,index,priority,Math.Max(footprint.PixelsU,footprint.PixelsV));
    }
    public void DemandKnownLod(StreamedMaterialTexture binding,int logicalLevel,ResourcePriority priority=ResourcePriority.Visible)
    {
        if(catalog.Family(binding.Family.Id)!=binding.Family||logicalLevel<0||logicalLevel>10)throw new ArgumentException("Invalid family or logical level.");
        var f=binding.Family;int index=binding.Policy==TextureQualityPolicy.CoarseOnly?f.CoarseIndex:binding.Policy==TextureQualityPolicy.FullQuality||f.IsQualityPair?0:f.IndexForLogical(logicalLevel+binding.Bias);
        while(index<f.CoarseIndex&&f.GetLevel(index).LogicalLevel<binding.MinimumLogicalLevel)index++;
        DemandIndex(f.Id,index,priority,0);
    }
    private void DemandIndex(int id,int index,ResourcePriority priority,float error)
    {
        if(priority<ResourcePriority.Gameplay||priority>ResourcePriority.Optional)throw new ArgumentOutOfRangeException("priority");
        bool isVisible=priority<=ResourcePriority.Visible;
        if(!wanted[id]||isVisible&&!visible[id])targets[id]=isVisible?index:catalog.Family(id).CoarseIndex;
        else if(isVisible&&index<targets[id])targets[id]=index;
        wanted[id]=true;visible[id]|=isVisible;if(priority<priorities[id])priorities[id]=priority;if(error>errors[id])errors[id]=error;
    }
    public bool IsWanted(int family)=>wanted[family];
    public int RequestedIndex(int family,int residentIndex)
    {
        var f=catalog.Family(family);
        if(residentIndex < -1||residentIndex>=f.Count)throw new ArgumentOutOfRangeException("residentIndex");
        return !wanted[family]?-1:residentIndex<0?f.CoarseIndex:targets[family];
    }
    public int TargetIndex(int family)=>targets[family];
    public ResourcePriority Priority(int family)=>priorities[family];
    public float ProjectedError(int family)=>errors[family];
}
