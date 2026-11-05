using Dolphin.Graphics;
namespace Hypercube.GameCube;

// Configure outside frames, then update UV state without touching mesh content.
public sealed class ScrollingTextureLayer
{
    public Vector2 RepeatsPerSecond,Scale;
    public ScrollingTextureLayer(Vector2 repeatsPerSecond,Vector2 scale){RepeatsPerSecond=repeatsPerSecond;Scale=scale;}
    public UvTransform At(double seconds)=>new UvTransform(new Vector2(MaterialMotion.Offset(seconds,RepeatsPerSecond.X),MaterialMotion.Offset(seconds,RepeatsPerSecond.Y)),Scale);
    public void Apply(Material material,double seconds,bool secondary=false)
    {var t=At(seconds);if(secondary)material.SecondaryTextureTransform=t;else material.TextureTransform=t;}
}
