using Dolphin.Graphics;
namespace Hypercube.GameCube;

/// <summary>Ordered color/UV variants of borrowed, uniform-color geometry.
/// Configure between frames. Transparent passes keep their authored order.
/// Set HypercubeNativePasses=true when building against the source SDK that
/// provides MeshPasses to replay cached indices with one native call.</summary>
public sealed class OrderedMeshPasses
{
#if HYPERCUBE_NATIVE_PASSES
    private readonly MeshPasses native;
    public int Count=>native.Count;
#else
    private readonly Mesh geometry;
    private readonly Color32[] colors;
    private readonly UvTransform[] uv;
    public int Count=>colors.Length;
#endif
    public OrderedMeshPasses(Mesh geometry,int count)
    {
        if(geometry==null)throw new ArgumentNullException(nameof(geometry));
        if(count<1||count>256)throw new ArgumentOutOfRangeException(nameof(count));
#if HYPERCUBE_NATIVE_PASSES
        native=new MeshPasses(geometry,count);
#else
        if(GraphicsDevice.Default.IsFrameActive)throw new InvalidOperationException("Prepare passes between frames.");
        this.geometry=geometry;colors=new Color32[count];uv=new UvTransform[count];
        for(int i=0;i<count;i++)colors[i]=Color32.White;
#endif
    }
    public void Set(int index,Color32 color,UvTransform transform)
    {
        if(GraphicsDevice.Default.IsFrameActive)throw new InvalidOperationException("Configure passes between frames.");
        if(index<0||index>=Count)throw new ArgumentOutOfRangeException(nameof(index));
#if HYPERCUBE_NATIVE_PASSES
        native.Set(index,color,transform);
#else
        colors[index]=color;uv[index]=transform;
#endif
    }
    /// <summary>Pass color and UV replace the material's primary color/UV.
    /// Requires one texture, authored UVs, unlit rendering and no custom effect.</summary>
    public void Draw(Renderer renderer,Material material,Transform transform)
    {
        if(renderer==null||material==null||transform==null)throw new ArgumentNullException();
        if(material.Texture==null||material.SecondaryTexture!=null||material.Effect!=null||material.Lighting!=LightingMode.Unlit||material.TextureCoordinates!=TextureCoordinateMode.Uv0)
            throw new ArgumentException("Ordered passes require a single-texture, unlit material with authored UVs and no custom effect.");
#if HYPERCUBE_NATIVE_PASSES
        renderer.DrawMeshPasses(native,material,transform);
#else
        var savedColor=material.Color;var savedUv=material.TextureTransform;
        try{for(int i=0;i<Count;i++){
            material.Color=colors[i];material.TextureTransform=uv[i];renderer.DrawMesh(geometry,material,transform);
        }}finally{material.Color=savedColor;material.TextureTransform=savedUv;}
#endif
    }
}
