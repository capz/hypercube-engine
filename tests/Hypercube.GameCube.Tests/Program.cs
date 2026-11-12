using Hypercube;
using Hypercube.GameCube;
using Dolphin.Graphics;
AramCacheChecks.Run();
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
PackedWorldChecks.Run();
TexturePoolChecks.Run();
StreamingChecks.Run();
namespace Dolphin.Graphics {
 public readonly struct Vector2 {public readonly float X,Y;public Vector2(float x,float y){X=x;Y=y;}}
 public readonly struct Vector3 {public readonly float X,Y,Z;public Vector3(float x,float y,float z){X=x;Y=y;Z=z;}}
 public readonly struct Color32 {public readonly byte R,G,B,A;public Color32(byte r,byte g,byte b,byte a){R=r;G=g;B=b;A=a;}}
 public struct VertexPositionNormalColorTexture {public VertexPositionNormalColorTexture(Vector3 p,Vector3 n,Color32 c,Vector2 uv){}}
 public enum NativeTextureFormat {Cmpr=14}
 public sealed class Texture2D:IDisposable {
  public static int Live,Uploads,FailOnUpload=-1;private bool disposed;
  internal readonly byte[] Data;
  public int NativeBytes=>Data.Length;
  private Texture2D(int bytes){Data=new byte[bytes];Live++;}
  public static Texture2D CreateNative(int width,int height,NativeTextureFormat format,bool linear)
   =>new Texture2D(format==NativeTextureFormat.Cmpr?((width+3)/4)*((height+3)/4)*8:width*height*4);
  public void ReplaceNative(byte[] data){Uploads++;if(Uploads==FailOnUpload)throw new InvalidOperationException("Injected texture upload failure.");Array.Copy(data,Data,Data.Length);}
  public void Dispose(){if(disposed)throw new Exception("Texture disposed twice");disposed=true;Live--;}
 }
 public sealed class Mesh:IDisposable {
  public static int Creates,LastVertices,LastIndices,Live,FailOnCreate=-1;public static bool Fail;public bool Disposed;
  public int NativeBytes{get;private set;}
  public static Mesh Create(VertexPositionNormalColorTexture[] vertices,int vertexCount,ushort[] indices,int indexCount){if(Fail||Creates+1==FailOnCreate)throw new OutOfMemoryException();Creates++;Live++;LastVertices=vertexCount;LastIndices=indexCount;return new Mesh{NativeBytes=vertexCount*36+indexCount*2};}
  public void Dispose(){if(Disposed)throw new Exception("Double disposal");Disposed=true;Live--;}
 }
}

namespace Dolphin.Storage {
 public sealed class AssetStream:IDisposable {
  public static int Open;private FileStream? stream;
  public int Reads,RequestedBytes;public readonly List<int> ReadSizes=new List<int>();
  public bool FailNextRead;public int ShortReadNext=-1;
  public AssetStream(string path){stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);Open++;}
  public Task<int> ReadAtAsync(int offset,Dolphin.Graphics.NativeBuffer target,int targetOffset,int count){
   if(stream==null)throw new ObjectDisposedException("AssetStream");
   target.EnsureWritable();Reads++;RequestedBytes+=count;ReadSizes.Add(count);
   if(FailNextRead){FailNextRead=false;return Task.FromException<int>(new IOException("Injected storage failure."));}
   if(ShortReadNext>=0){count=Math.Min(count,ShortReadNext);ShortReadNext=-1;}
   stream.Seek(offset,SeekOrigin.Begin);int read=stream.Read(target.Data,targetOffset,count);return Task.FromResult(read);
  }
  public void Dispose(){if(stream==null)return;stream.Dispose();stream=null;Open--;}
 }
}
namespace Dolphin.Timing {
 public static class Clock {public static long Timestamp=>(long)(System.Diagnostics.Stopwatch.GetTimestamp()*(1000000.0/System.Diagnostics.Stopwatch.Frequency));}
}
