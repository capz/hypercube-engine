using System;
using Dolphin.Graphics;
using Hypercube.GameCube;

// Consumed outside this repo via one engine PackageReference. Graphics calls
// execute on the console; host validation compiles and AOT-bundles this fixture.
var definition=TevMaterialPresets.DualTextureGlints(
    new Color32(102,150,176),new Color32(215,248,244),33,235);
var program=definition.GetPackage();
if(program[4]!=4||program[8]!=3||program[21]!=1||
   program[24]!=(byte)AlphaComparison.LessOrEqual||program[25]!=33||
   program[26]!=(byte)AlphaCompareOperation.Or||
   program[27]!=(byte)AlphaComparison.GreaterOrEqual||program[28]!=235)
    throw new InvalidOperationException("Expected the SDK's independent alpha and OR cutout");
using var texture=Texture2D.Create(4,4,new byte[64]);
using var water=definition.Create(texture,texture);
water.Material.Blend=BlendMode.Alpha;water.Material.DepthWrite=false;
water.Material.Color=Color32.White; // Keep the >=235 alpha branch reachable.
var primary=new ScrollingTextureLayer(new Vector2(.017f,.044f),new Vector2(1,1));
var secondary=new ScrollingTextureLayer(new Vector2(0,-.067f),new Vector2(1,1));
primary.Apply(water.Material,12);secondary.Apply(water.Material,12,true);
var vertices=new[]{
    new VertexPositionColorTexture(new Vector3(0,0,0),Color32.White,Vector2.Zero),
    new VertexPositionColorTexture(new Vector3(1,0,0),Color32.White,Vector2.Zero),
    new VertexPositionColorTexture(new Vector3(0,1,0),Color32.White,Vector2.Zero)};
using var mesh=Mesh.Create(vertices);
var renderer=new Renderer();var camera=new Camera(60,4f/3,.1f,100);var transform=new Transform();
var device=GraphicsDevice.Default;
device.BeginFrame(Color32.Black);renderer.Begin3D(camera);
renderer.DrawMesh(mesh,water.Material,transform);
renderer.End3D();device.EndFrame();
