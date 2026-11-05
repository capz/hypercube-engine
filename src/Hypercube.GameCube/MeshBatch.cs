using Dolphin.Graphics;
namespace Hypercube.GameCube;

// Registered meshes are borrowed. Material properties are copied when registered
// (or updated with an empty queue); texture/effect resources remain borrowed.
// Effect uniforms must stay unchanged between Add and Flush. Only opaque geometry.
public sealed class MeshBatch
{
    private readonly OpaqueDrawQueue queue;
    private readonly Mesh[] registered,drawMeshes;
    private readonly Material[] materials;
    private readonly Color32[] baseColors;
    private readonly Transform[] poses;
    private int meshCount,materialCount;
    public int Count=>queue.Count;
    public int Capacity=>queue.Capacity;
    public MeshBatch(int capacity,int meshCapacity,int materialCapacity)
    {
        if(capacity<1||capacity>128||meshCapacity<1||materialCapacity<1)throw new ArgumentOutOfRangeException();
        queue=new OpaqueDrawQueue(capacity);registered=new Mesh[meshCapacity];materials=new Material[materialCapacity];baseColors=new Color32[materialCapacity];drawMeshes=new Mesh[capacity];poses=new Transform[capacity];
        for(int i=0;i<capacity;i++)poses[i]=new Transform();
        for(int i=0;i<materialCapacity;i++)materials[i]=new Material{Sampler=new SamplerState()}; // Preallocate SDK material options.
    }
    public int RegisterMesh(Mesh mesh)
    {if(mesh==null)throw new ArgumentNullException();if(meshCount==registered.Length)throw new InvalidOperationException("Mesh registry full");registered[meshCount]=mesh;return meshCount++;}
    public int RegisterMaterial(Material source)
    {if(materialCount==materials.Length)throw new InvalidOperationException("Material registry full");UpdateMaterial(materialCount,source,true);return materialCount++;}
    public void UpdateMaterial(int id,Material source)=>UpdateMaterial(id,source,false);
    private void UpdateMaterial(int id,Material source,bool registering)
    {
        if(Count!=0)throw new InvalidOperationException("Flush before changing a registered material");
        if(id<0||id>=(registering?materials.Length:materialCount))throw new ArgumentOutOfRangeException();
        if(source==null||source.Blend!=BlendMode.Opaque||!source.DepthTest||!source.DepthWrite||!(source.AlphaCutoff>=0&&source.AlphaCutoff<=1)||(int)source.TextureCoordinates<0||(int)source.TextureCoordinates>2||(int)source.Lighting<0||(int)source.Lighting>1||(int)source.Cull<0||(int)source.Cull>1)throw new ArgumentException("Batch requires opaque depth-tested/writing materials");
        var m=materials[id];m.Texture=source.Texture;m.SecondaryTexture=source.SecondaryTexture;m.Effect=source.Effect;m.Color=source.Color;baseColors[id]=source.Color;
        m.Blend=source.Blend;m.DepthTest=source.DepthTest;m.DepthWrite=source.DepthWrite;m.Cull=source.Cull;m.Lighting=source.Lighting;m.AlphaCutoff=source.AlphaCutoff;m.TextureCoordinates=source.TextureCoordinates;
        m.Sampler=source.Sampler;m.SecondarySampler=source.SecondarySampler;m.TextureTransform=source.TextureTransform;m.SecondaryTextureTransform=source.SecondaryTextureTransform;
    }
    public bool TryAdd(int mesh,int material,Vector3 position,Quaternion rotation,Vector3 scale,Color32 tint,RenderCounters? counters=null)
    {
        if((uint)mesh>=meshCount||(uint)material>=materialCount)throw new ArgumentOutOfRangeException();
        var p=new DrawPose{X=position.X,Y=position.Y,Z=position.Z,Qx=rotation.X,Qy=rotation.Y,Qz=rotation.Z,Qw=rotation.W,Sx=scale.X,Sy=scale.Y,Sz=scale.Z};
        bool added=queue.TryAdd(mesh,material,tint.Packed,p);if(!added&&counters!=null)counters.BatchOverflows++;return added;
    }
    public void Clear()=>queue.Clear();
    public void Flush(Renderer renderer,RenderCounters? counters=null)
    {
        queue.Group();
        try{for(int i=0;i<Count;){
            var first=queue.At(i);var m=materials[first.Material];var c=baseColors[first.Material];uint t=first.Tint;
            m.Color=new Color32((byte)(c.R*(t>>24)/255),(byte)(c.G*((t>>16)&255)/255),(byte)(c.B*((t>>8)&255)/255),(byte)(c.A*(t&255)/255));
            int used=0;
            while(i<Count){var e=queue.At(i);if(e.Material!=first.Material||e.Tint!=first.Tint)break;
                drawMeshes[used]=registered[e.Mesh];var p=e.Pose;poses[used].LocalPosition=new Vector3(p.X,p.Y,p.Z);poses[used].LocalRotation=new Quaternion(p.Qx,p.Qy,p.Qz,p.Qw);poses[used].LocalScale=new Vector3(p.Sx,p.Sy,p.Sz);used++;i++;
            }
            renderer.DrawMeshes(drawMeshes,poses,m,0,used);
            if(counters!=null){counters.SubmittedInstances+=used;counters.BatchCalls++;counters.MaterialGroups++;counters.MaterialTextureSlots+=(m.Texture==null?0:1)+(m.SecondaryTexture==null?0:1);}
        }}finally{queue.Clear();}
    }
}
