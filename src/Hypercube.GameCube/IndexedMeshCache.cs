using Dolphin.Graphics;
namespace Hypercube.GameCube;

/// <summary>Revision-keyed native storage for stable indexed geometry. Commit and
/// dispose between frames. Input arrays are borrowed only during Commit.</summary>
public sealed class IndexedMeshCache : IDisposable
{
    private Mesh? mesh;
    private long revision;
    private bool committed,disposed;
    public Mesh? Mesh=>mesh;
    public int VertexCount {get;private set;}
    public int IndexCount {get;private set;}
    public bool Commit(VertexPositionNormalColorTexture[] vertices,int vertexCount,ushort[] indices,int indexCount,long contentRevision,RenderCounters? counters=null)
    {
        if(disposed)throw new ObjectDisposedException(nameof(IndexedMeshCache));
        if(vertices==null||indices==null)throw new ArgumentNullException();
        if(vertexCount<1||vertexCount>65535||vertexCount>vertices.Length||indexCount<3||indexCount>3*65535||indexCount>indices.Length||indexCount%3!=0)throw new ArgumentOutOfRangeException();
        // A revision identifies all positions, colors, UVs and topology. Reusing
        // it avoids even scanning/copying the decode arrays in steady state.
        if(committed&&revision==contentRevision&&VertexCount==vertexCount&&IndexCount==indexCount)return false;
        var next=Mesh.Create(vertices,vertexCount,indices,indexCount);
        // Keep the old mesh and revision intact if validation/allocation fails.
        var previous=mesh;mesh=next;VertexCount=vertexCount;IndexCount=indexCount;
        revision=contentRevision;committed=true;previous?.Dispose();
        if(counters!=null)counters.GeometryRebuilds++;
        return true;
    }
    public void Dispose()
    {if(disposed)return;mesh?.Dispose();mesh=null;VertexCount=IndexCount=0;committed=false;disposed=true;}
}
