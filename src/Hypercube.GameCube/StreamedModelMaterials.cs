using System;
using Dolphin.Graphics;

namespace Hypercube.GameCube;

/// <summary>Generated per-instance material wiring. Geometry and animations remain
/// in the SDK model; all instances share family allocations in the stream.
/// Owns at most two immutable TEV effects, never the instance or textures.</summary>
public sealed class StreamedModelMaterials : IDisposable
{
    private readonly TextureLodStreamer stream;
    private readonly StreamedMaterialBinding?[] parts;
    private readonly float[] worldU,worldV;
    private GxEffect? multiply,add;
    private bool disposed;
    public int Count=>parts.Length;
    public StreamedModelMaterials(TextureLodStreamer stream,int materialCount)
    {
        if(stream==null||materialCount<1||materialCount>4096)throw new ArgumentException("Invalid model material table.");
        this.stream=stream;parts=new StreamedMaterialBinding?[materialCount];worldU=new float[materialCount];worldV=new float[materialCount];
    }
    public void SetPart(int index,Material template,StreamedMaterialTexture? primary,StreamedMaterialTexture? secondary,int combiner,float worldPerU,float worldPerV)
    {
        Check(index);if(template==null||parts[index]!=null||combiner<0||combiner>2||
            (secondary!=null)!=(combiner!=0)||combiner!=0&&primary==null||
            float.IsNaN(worldPerU)||float.IsInfinity(worldPerU)||worldPerU<0||float.IsNaN(worldPerV)||float.IsInfinity(worldPerV)||worldPerV<0)throw new ArgumentException("Invalid streamed model material.");
        if(combiner!=0)
        {
            var effect=combiner==1?multiply:add;
            if(effect==null)
            {
                var program=new TevProgramBuilder();program.AddStage(TevInput.Zero,TevInput.Raster,TevInput.Texture,TevInput.Zero);
                if(combiner==1)program.AddStage(TevInput.Zero,TevInput.Previous,TevInput.SecondaryTexture,TevInput.Zero);
                else program.AddStage(TevInput.Zero,TevInput.SecondaryTexture,TevInput.One,TevInput.Previous);
                effect=program.CreateEffect();if(combiner==1)multiply=effect;else add=effect;
            }
            template.Effect=effect;
        }
        parts[index]=new StreamedMaterialBinding(stream,template,primary,secondary);worldU[index]=worldPerU;worldV[index]=worldPerV;
    }
    private void Check(int index)
    {if(disposed)throw new ObjectDisposedException("StreamedModelMaterials");if(index<0||index>=parts.Length)throw new ArgumentOutOfRangeException("index");}
    public StreamedMaterialBinding GetPart(int index)
    {Check(index);return parts[index]??throw new InvalidOperationException("Missing generated material.");}
    /// <summary>Supply maximum application world scale times a conservative
    /// animation deformation multiplier. Rest-pose UV density already includes
    /// imported node transforms and authored UV repeats. Degenerate UVs use
    /// the explicit fallback footprint. Call only for visible material groups.</summary>
    public void DemandPart(int index,float worldScale,float centerDistance,float radius,float verticalFovDegrees,int viewportHeight,TextureFootprint fallback)
    {
        var part=GetPart(index);if(!(worldScale>0)||float.IsInfinity(worldScale))throw new ArgumentOutOfRangeException("worldScale");
        var footprint=worldU[index]>0&&worldV[index]>0?TextureFootprint.Project(worldU[index]*worldScale,worldV[index]*worldScale,centerDistance,radius,verticalFovDegrees,viewportHeight):fallback;
        if(part.Primary!=null)stream.Demand(part.Primary,footprint);if(part.Secondary!=null)stream.Demand(part.Secondary,footprint);
    }
    /// <summary>Submit opaque parts first. Sort transparent material parts in
    /// the application queue; the SDK sorts primitives within each part.</summary>
    public void DrawPart(Renderer renderer,ModelInstance instance,Transform transform,int index,int frame)
        =>renderer.DrawModelMaterialPart(instance,index,transform,GetPart(index).PrepareForDraw(frame));
    public void Dispose()
    {if(disposed)return;multiply?.Dispose();add?.Dispose();disposed=true;}
}
