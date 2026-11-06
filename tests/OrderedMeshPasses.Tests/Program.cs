using Dolphin.Graphics;
using Hypercube.GameCube;
static class Program
{
 static void Check(bool b){if(!b)throw new Exception("Pass contract failed");}
 static void Reject(Action a){try{a();}catch(ArgumentException){return;}catch(InvalidOperationException){return;}throw new Exception("Expected rejection");}
 static void Main(){
  var mesh=new Mesh();Reject(()=>new OrderedMeshPasses(mesh,0));Reject(()=>new OrderedMeshPasses(mesh,257));
  var p=new OrderedMeshPasses(mesh,3);var m=new Material{Texture=new Texture2D(),Color=new Color32(9),TextureTransform=new UvTransform(9)};var r=new Renderer();var t=new Transform();
  for(int i=0;i<3;i++)p.Set(i,new Color32(i),new UvTransform(i));
  GraphicsDevice.Default.IsFrameActive=true;Reject(()=>p.Set(0,new Color32(0),default));GraphicsDevice.Default.IsFrameActive=false;
  p.Draw(r,m,t);Check(r.Calls==3&&m.Color.Value==9&&m.TextureTransform.Value==9);
  r.Calls=0;r.FailAt=1;try{p.Draw(r,m,t);}catch(InvalidOperationException){}Check(r.Calls==2&&m.Color.Value==9&&m.TextureTransform.Value==9);
  r.FailAt=-1;r.Calls=0;m.SecondaryTexture=new Texture2D();Reject(()=>p.Draw(r,m,t));Check(r.Calls==0);m.SecondaryTexture=null;
  var cold=new Material{Texture=m.Texture};p.Draw(r,m,t);r.Calls=0;long before=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<1000;i++){p.Draw(r,cold,t);r.Calls=0;}Check(GC.GetAllocatedBytesForCurrentThread()==before);
  Console.WriteLine("Ordered mesh passes: ordering, color/UV restoration including exceptions, validation and zero-allocation draw passed.");
 }
}
namespace Dolphin.Graphics
{
 public sealed class Mesh{}public sealed class Texture2D{}public sealed class Transform{}
 public enum LightingMode{Unlit,Diffuse}public enum TextureCoordinateMode{Uv0,ViewNormal}
 public readonly struct Color32{public readonly int Value;public Color32(int v){Value=v;}public static Color32 White=>new(255);}
 public readonly struct UvTransform{public readonly int Value;public UvTransform(int v){Value=v;}}
 public sealed class GraphicsDevice{public static readonly GraphicsDevice Default=new();public bool IsFrameActive;}
 public readonly struct SamplerState{}
 public sealed class Material{
  public Texture2D? Texture,SecondaryTexture;public object? Effect;public LightingMode Lighting;public TextureCoordinateMode TextureCoordinates;public Color32 Color;
  public int Blend,Cull;public bool DepthTest,DepthWrite;public float AlphaCutoff;
  private float[]? options;private UvTransform uv;private SamplerState sampler;
  public UvTransform TextureTransform{get=>uv;set{uv=value;options??=new float[40];}}
  public SamplerState Sampler{get=>sampler;set{sampler=value;options??=new float[40];}}
 }
 public sealed class Renderer{public int Calls,FailAt=-1;public void DrawMesh(Mesh mesh,Material m,Transform t){int i=Calls++;if(FailAt==i)throw new InvalidOperationException();if(m.Color.Value!=i||m.TextureTransform.Value!=i)throw new Exception("Reordered passes");}}
}
