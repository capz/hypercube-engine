using Dolphin.Graphics;
namespace Hypercube.GameCube;

// One allocation grows geometrically to a caller-supplied ceiling. Upload and
// disposal must occur between frames. Revision belongs to this buffer's content.
public sealed class DynamicMeshBuffer : IDisposable
{
    private VertexPositionColorTexture[] vertices=new VertexPositionColorTexture[0];
    private DynamicMesh? mesh;private int uploadedCapacity;private long revision;private bool committed,disposed;
    public int MaximumVertices {get;}
    public int Count {get;private set;}
    public int Capacity=>vertices.Length;
    public DynamicMesh? Mesh=>Count==0?null:mesh;
    public DynamicMeshBuffer(int maximumVertices)
    {if(maximumVertices<3||maximumVertices%3!=0)throw new ArgumentOutOfRangeException();MaximumVertices=maximumVertices;}
    public VertexPositionColorTexture[] Reserve(int required,RenderCounters? counters=null)
    {
        if(disposed)throw new ObjectDisposedException("DynamicMeshBuffer");
        int size=BufferGrowth.Next(Capacity,required,MaximumVertices);
        if(size!=Capacity){var next=new VertexPositionColorTexture[size];Array.Copy(vertices,next,Count);vertices=next;if(counters!=null){counters.BufferGrowths++;counters.BufferAllocations++;}}
        return vertices;
    }
    public bool Commit(int count,long contentRevision,RenderCounters? counters=null)
    {
        if(disposed)throw new ObjectDisposedException("DynamicMeshBuffer");
        if(count<0||count>Capacity||count%3!=0)throw new ArgumentOutOfRangeException();
        if(committed&&revision==contentRevision&&Count==count)return false;
        if(count>0){
            for(int i=count;i<Capacity;i++)vertices[i]=new VertexPositionColorTexture(Vector3.Zero,Color32.White,Vector2.Zero);
            if(uploadedCapacity!=Capacity){var next=DynamicMesh.Create(vertices);mesh?.Dispose();mesh=next;uploadedCapacity=Capacity;}else mesh!.SetVertices(vertices);
        }
        Count=count;revision=contentRevision;committed=true;if(counters!=null)counters.GeometryRebuilds++;return true;
    }
    public void Dispose(){if(disposed)return;mesh?.Dispose();mesh=null;Count=0;disposed=true;}
}
