using System;
using Dolphin.Graphics;

namespace Hypercube.GameCube;

/// <summary>Generation-sensitive texture sort identity, refreshed each frame.
/// Material state (effect/blend/etc.) remains part of the application's key.</summary>
public readonly struct TextureDrawKey
{
    public readonly int PrimaryFamily,PrimaryGeneration,SecondaryFamily,SecondaryGeneration;
    internal TextureDrawKey(ResolvedTextureLod primary,ResolvedTextureLod secondary)
    {PrimaryFamily=primary.FamilyId;PrimaryGeneration=primary.Generation;SecondaryFamily=secondary.FamilyId;SecondaryGeneration=secondary.Generation;}
}

/// <summary>Resolve and pin at actual submission. Owns no texture allocations;
/// separate bindings can share the template and logical families safely.</summary>
public sealed class StreamedMaterialBinding
{
    private readonly TextureLodStreamer textures;
    private readonly Material resolved=new Material{Sampler=new SamplerState()};
    public Material Template {get;}
    public StreamedMaterialTexture? Primary {get;}
    public StreamedMaterialTexture? Secondary {get;}
    public StreamedMaterialBinding(TextureLodStreamer textures,Material template,StreamedMaterialTexture? primary,StreamedMaterialTexture? secondary=null)
    {this.textures=textures;Template=template??throw new ArgumentNullException("template");Primary=primary;Secondary=secondary;}
    public TextureDrawKey SortKey()=>new TextureDrawKey(Primary==null?default:textures.Resolve(Primary),Secondary==null?default:textures.Resolve(Secondary));
    public static UvTransform Compose(TextureUv level,UvTransform authored)
        =>authored.Then(new UvTransform(new Vector2(level.OffsetU,level.OffsetV),new Vector2(level.ScaleU,level.ScaleV)));
    public static UvTransform TerrainUv(AssetRecord level)
    {if(level.Kind!=1)throw new ArgumentException("Expected a v1 terrain texture.");return new UvTransform(new Vector2(4f/level.Width,4f/level.Width),new Vector2((float)level.Useful/level.Width,(float)level.Useful/level.Width));}
    /// <summary>Use the returned material immediately; queued commands should
    /// retain this binding and resolve during execution, not cache this material.</summary>
    public Material PrepareForDraw(int frame)
    {
        var source=Template;var m=resolved;
        m.Texture=source.Texture;m.SecondaryTexture=source.SecondaryTexture;m.Effect=source.Effect;m.Color=source.Color;
        m.Blend=source.Blend;m.Lighting=source.Lighting;m.Cull=source.Cull;m.DepthTest=source.DepthTest;m.DepthWrite=source.DepthWrite;
        m.AlphaCutoff=source.AlphaCutoff;m.TextureCoordinates=source.TextureCoordinates;m.Sampler=source.Sampler;m.SecondarySampler=source.SecondarySampler;
        m.TextureTransform=source.TextureTransform;m.SecondaryTextureTransform=source.SecondaryTextureTransform;
        if(Primary!=null)
        {
            var p=textures.Resolve(Primary);if(!textures.Pin(p,frame))throw new InvalidOperationException("Stale primary texture lease.");
            m.Texture=p.Texture;m.TextureTransform=Compose(p.Uv,source.TextureTransform);
        }
        if(Secondary!=null)
        {
            var p=textures.Resolve(Secondary);if(!textures.Pin(p,frame))throw new InvalidOperationException("Stale secondary texture lease.");
            m.SecondaryTexture=p.Texture;m.SecondaryTextureTransform=Compose(p.Uv,source.SecondaryTextureTransform);
        }
        return m;
    }
    public void Draw(Renderer renderer,Mesh mesh,Transform transform,int frame)=>renderer.DrawMesh(mesh,PrepareForDraw(frame),transform);
}
