using Dolphin.Graphics;
namespace Hypercube.GameCube;

// Owns one shared 4 KiB mask and effect. Bind only to materials whose secondary
// slot/effect are available. UV0 must map linearly to logical world XZ; the caller
// supplies metres per UV repeat and period. No cylinder or game weather policy.
public sealed class CloudShadowLayer : IDisposable
{
    private readonly Texture2D mask;private readonly GxEffect effect;
    private readonly ScrollingTextureLayer motion;
    private float cover=.5f,previous=-1;private UvTransform transform;private bool disposed;
    public float Cover {get=>cover;set=>cover=CloudMask.Cover(value);}
    public CloudShadowLayer(float worldMetresPerUvRepeat,float worldPeriod,Vector2 metresPerSecond)
    {
        if(!(worldMetresPerUvRepeat>0)||float.IsInfinity(worldMetresPerUvRepeat)||!(worldPeriod>0)||float.IsInfinity(worldPeriod))throw new ArgumentOutOfRangeException();
        float scale=worldMetresPerUvRepeat/worldPeriod,vx=metresPerSecond.X/worldPeriod,vy=metresPerSecond.Y/worldPeriod;
        if(!(scale>0)||float.IsInfinity(scale))throw new ArgumentOutOfRangeException();MaterialMotion.Offset(0,vx);MaterialMotion.Offset(0,vy);
        motion=new ScrollingTextureLayer(new Vector2(vx,vy),new Vector2(scale,scale));
        mask=Texture2D.CreateNative(64,64,NativeTextureFormat.I8,true);
        var p=new TevProgramBuilder();p.AddColor("CloudShade",Color32.White);
        p.AddStage(TevInput.Zero,TevInput.Raster,TevInput.Texture,TevInput.Zero);
        p.AddStage(TevInput.One,TevInput.Constant,TevInput.SecondaryTexture,TevInput.Zero,TevRegister.Color0,colorSlot:0);
        p.AddStage(TevInput.Zero,TevInput.Previous,TevInput.Color0,TevInput.Zero);try{mask.ReplaceNative(CloudMask.Texture());effect=p.CreateEffect();}catch{mask.Dispose();throw;}
    }
    public void Animate(double seconds)
    {
        Check();if(previous!=cover){byte s=CloudMask.Shade(cover);effect.SetColor(0,new Color32(s,s,s));previous=cover;}
        transform=motion.At(seconds);
    }
    public void Apply(Material material,Color32 baseTint,bool enabled=true)
    {
        Check();if(material==null)throw new ArgumentNullException();if(material.TextureCoordinates!=TextureCoordinateMode.Uv0||(material.SecondaryTexture!=null&&material.SecondaryTexture!=mask)||(material.Effect!=null&&material.Effect!=effect))throw new ArgumentException("Cloud layer needs UV0 and an available effect/secondary texture slot");
        bool patches=enabled&&CloudMask.Shade(cover)<255;
        if(patches&&material.Texture==null)throw new ArgumentException("Cloud layer needs a primary texture");
        material.Effect=patches?effect:null;material.SecondaryTexture=patches?mask:null;material.SecondaryTextureTransform=patches?transform:UvTransform.Identity;
        byte a=enabled?CloudMask.Ambient(cover):(byte)255;material.Color=new Color32((byte)(baseTint.R*a/255),(byte)(baseTint.G*a/255),(byte)(baseTint.B*a/255),baseTint.A);
    }
    // Call once between frames. Explicit primary addressing preserves tiling
    // when the SDK material-options block is introduced by a secondary sampler.
    public static void Configure(Material material,SamplerState primary)
    {if(material==null)throw new ArgumentNullException();material.Sampler=primary;material.SecondarySampler=new SamplerState(TextureAddressMode.Repeat,TextureAddressMode.Repeat,TextureFilter.Linear);}
    private void Check(){if(disposed)throw new ObjectDisposedException("CloudShadowLayer");}
    public void Dispose(){if(disposed)return;effect.Dispose();mask.Dispose();disposed=true;}
}
