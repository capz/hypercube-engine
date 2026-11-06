using Hypercube;
using Hypercube.GameCube;
using Dolphin.Graphics;
var cache=new IndexedMeshCache();var counters=new RenderCounters();
var vertices=new VertexPositionNormalColorTexture[12];var indices=new ushort[12];
if(!cache.Commit(vertices,4,indices,6,1,counters)||Mesh.Creates!=1||cache.VertexCount!=4||cache.IndexCount!=6)throw new Exception("Exact prefix upload");
var original=cache.Mesh;
long before=GC.GetAllocatedBytesForCurrentThread();
for(int i=0;i<1000;i++)if(cache.Commit(vertices,4,indices,6,1,counters))throw new Exception("Unchanged content uploaded");
if(GC.GetAllocatedBytesForCurrentThread()!=before||Mesh.Creates!=1)throw new Exception("Cache hits allocate");
Mesh.Fail=true;try{cache.Commit(vertices,4,indices,6,2,counters);throw new Exception("Failure swallowed");}catch(OutOfMemoryException){}
if(cache.Mesh!=original||original!.Disposed||counters.GeometryRebuilds!=1)throw new Exception("Failed upload mutated cache");
Mesh.Fail=false;if(!cache.Commit(vertices,4,indices,6,2,counters)||!original!.Disposed)throw new Exception("Replacement did not retire old mesh");
if(!cache.Commit(vertices,3,indices,3,2,counters)||Mesh.LastVertices!=3||Mesh.LastIndices!=3)throw new Exception("Count change reused stale topology");
try{cache.Commit(vertices,3,indices,4,3);throw new Exception("Invalid triangle accepted");}catch(ArgumentOutOfRangeException){}
var last=cache.Mesh;cache.Dispose();cache.Dispose();if(!last!.Disposed||cache.Mesh!=null||cache.VertexCount!=0)throw new Exception("Dispose");
try{cache.Commit(vertices,3,indices,3,4);throw new Exception("Disposed cache reused");}catch(ObjectDisposedException){}
Console.WriteLine("Indexed cache: exact prefixes, revision/count invalidation, allocation-free hits, failed replacement and disposal passed.");
namespace Dolphin.Graphics {
 public struct VertexPositionNormalColorTexture{}
 public sealed class Mesh:IDisposable {
  public static int Creates,LastVertices,LastIndices;public static bool Fail;public bool Disposed;
  public static Mesh Create(VertexPositionNormalColorTexture[] vertices,int vertexCount,ushort[] indices,int indexCount){if(Fail)throw new OutOfMemoryException();Creates++;LastVertices=vertexCount;LastIndices=indexCount;return new Mesh();}
  public void Dispose(){if(Disposed)throw new Exception("Double disposal");Disposed=true;}
 }
}
