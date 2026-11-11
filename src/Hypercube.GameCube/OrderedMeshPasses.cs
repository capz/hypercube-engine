using Dolphin.Graphics;
namespace Hypercube.GameCube;

/// <summary>Ordered color/UV variants of borrowed, uniform-color geometry.
/// Configure between frames. Transparent passes keep their authored order.
/// SDK MeshPasses replays cached indices with one native call by default.
/// HypercubeNativePasses=false retains the portable comparison path.</summary>
public sealed class OrderedMeshPasses
{
#if HYPERCUBE_NATIVE_PASSES
    private readonly MeshPasses native;
    public int Count=>native.Count;
#else
    private readonly Mesh geometry;
    private readonly Color32[] colors;
    private readonly UvTransform[] uv;
    // Warm the SDK's lazy native options outside the frame, including when
    // the caller has never set a sampler or UV transform.
    private readonly Material work=new Material{Sampler=new SamplerState()};
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
        work.Texture=material.Texture;work.Blend=material.Blend;work.Cull=material.Cull;
        work.DepthTest=material.DepthTest;work.DepthWrite=material.DepthWrite;
        work.AlphaCutoff=material.AlphaCutoff;work.Sampler=material.Sampler;
        try{for(int i=0;i<Count;i++){
            work.Color=colors[i];work.TextureTransform=uv[i];renderer.DrawMesh(geometry,work,transform);
        }}finally{work.Texture=null;}
#endif
    }
}
