#!/usr/bin/env python3
"""Restore actual engine nupkgs outside the repository with an isolated cache."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape, quoteattr
from zipfile import ZipFile
ROOT=Path(__file__).resolve().parents[1]

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--feed',type=Path,default=ROOT/'out/packages');p.add_argument('--workspace',type=Path);p.add_argument('--core-only',action='store_true');p.add_argument('--native',action='store_true');args=p.parse_args()
    work=(args.workspace or Path(tempfile.mkdtemp(prefix='hypercube nuget '))).resolve()
    if work.exists() and any(work.iterdir()):p.error('Consumer workspace must be empty')
    work.mkdir(parents=True,exist_ok=True);feed=work/'relocated feed';feed.mkdir()
    ids=['Capz.Hypercube.Core']+([] if args.core_only else ['Capz.Hypercube.GameCube']);versions={}
    for identity in ids:
        packages=list(args.feed.glob(identity+'.*.nupkg'))
        if len(packages)!=1:p.error('Feed must contain exactly one version of '+identity)
        package=packages[0];shutil.copy2(package,feed)
        with ZipFile(package) as z:
            root=ET.fromstring(z.read(identity+'.nuspec'));versions[identity]=next(e.text for e in root.iter() if e.tag.endswith('}version'))
    if len(set(versions.values()))!=1:p.error('Engine packages must share a version')
    env=os.environ.copy();env.update(NUGET_PACKAGES=str(work/'packages'),DOTNET_CLI_HOME=str(work/'cli'),DOTNET_CLI_TELEMETRY_OPTOUT='1')
    def run(*cmd,cwd=work):subprocess.run(cmd,cwd=cwd,env=env,check=True)
    (work/'NuGet.Config').write_text('<configuration><packageSources><clear/><add key="engine" value='+quoteattr(str(feed))+'/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>')
    core=work/'Portable';core.mkdir()
    (core/'Portable.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Capz.Hypercube.Core" Version="'+versions[ids[0]]+'"/></ItemGroup></Project>')
    (core/'Program.cs').write_text('''using System;using Hypercube;
class Program {static void Main(){var q=new OpaqueDrawQueue(2);q.TryAdd(1,2,0xffffffff,DrawPose.Identity);q.Group();if(q.At(0).Mesh!=1)throw new Exception();var c=new CameraFrustum(0,0,0,0,0,-1,0,1,0,60,1,1,20);if(!c.Sphere(0,0,-5,1))throw new Exception();var r=new ResourceLeases(1,1,4);r.Register(1,4);var l=r.Acquire(1);r.Release(l);if(r.RemoveUnused(0)!=0)throw new Exception();Console.WriteLine("External Core NuGet consumer passed");}}
''')
    run('dotnet','run','--project',str(core),'-c','Release')
    sdk=ET.parse(ROOT/'Directory.Build.props').find('.//DolphinDotNetPackageVersion').text
    if not args.core_only:
        app=work/'GameCube';app.mkdir()
        (app/'GameCube.csproj').write_text('<Project Sdk="DolphinDotNet.Sdk/'+sdk+'"><ItemGroup><PackageReference Include="Capz.Hypercube.GameCube" Version="'+versions[ids[1]]+'"/></ItemGroup></Project>')
        shutil.copy2(ROOT/'samples/Rendering/Program.cs',app/'Program.cs')
        run('dotnet','build',str(app),'-c','Release','-p:DndBuildNative=false')
        assets=json.loads((app/'obj/project.assets.json').read_text())
        for identity in ['Capz.Hypercube.Core','Capz.Hypercube.GameCube','DolphinDotNet.GameCube','DolphinDotNet.Core']:
            if not any(k.startswith(identity+'/') for k in assets['libraries']):raise AssertionError('Missing transitive dependency '+identity)
        # Source validation and distributed native/compiler assemblies must use
        # the same reviewed SDK revision, not just compatible managed API names.
        sdk_revision=(ROOT/'.sdk-revision').read_text().strip()
        for identity in ['DolphinDotNet.Core','DolphinDotNet.GameCube','DolphinDotNet.Sdk']:
            package=work/'packages'/identity.lower()/sdk
            nuspec=ET.parse(package/(identity.lower()+'.nuspec'))
            repository=next(e for e in nuspec.iter() if e.tag.split('}')[-1]=='repository')
            if repository.get('commit')!=sdk_revision:
                raise AssertionError('SDK source revision mismatch for '+identity+': '+str(repository.get('commit')))
            if identity!='DolphinDotNet.Sdk' and identity+'/'+sdk not in assets['libraries']:
                raise AssertionError('SDK dependency version mismatch for '+identity)
        compiler=work/'packages/dolphindotnet.sdk'/sdk/'tools/compiler/DolphinDotNet.Compiler.dll'
        run('dotnet',str(compiler),'--bundle',str(app/'bin/Release/net8.0/GameCube.dll'),str(work/'native-consumer'))
        if not (work/'native-consumer/source/generated_program.c').is_file():raise AssertionError('No consumer AOT bundle')
        # Exercise the distributed default backend, rather than only compiling
        # the stubbed contract test or the source-checkout adapter.
        ordered=work/'Ordered';ordered.mkdir()
        (ordered/'Ordered.csproj').write_text((app/'GameCube.csproj').read_text())
        (ordered/'Program.cs').write_text('''using Dolphin.Graphics;using Hypercube.GameCube;
var vertices=new[]{new VertexPositionColorTexture(new Vector3(0,0,0),Color32.White,Vector2.Zero),new VertexPositionColorTexture(new Vector3(1,0,0),Color32.White,Vector2.Zero),new VertexPositionColorTexture(new Vector3(0,1,0),Color32.White,Vector2.Zero)};
using var mesh=Mesh.Create(vertices);using var texture=Texture2D.CreateNative(8,8,NativeTextureFormat.I8);
var passes=new OrderedMeshPasses(mesh,2);passes.Set(0,Color32.White,new UvTransform(Vector2.Zero,new Vector2(1,1)));
var renderer=new Renderer();var device=GraphicsDevice.Default;device.BeginFrame(Color32.Black);renderer.Begin3D(new Camera(60,4f/3,.1f,100));passes.Draw(renderer,new Material{Texture=texture},new Transform());renderer.End3D();device.EndFrame();
''')
        run('dotnet','build',str(ordered),'-c','Release','-p:DndBuildNative=false')
        run('dotnet',str(compiler),'--bundle',str(ordered/'bin/Release/net8.0/Ordered.dll'),str(work/'native-ordered'))
        if 'dnd_mesh_draw_passes(' not in (work/'native-ordered/source/generated_program.c').read_text():
            raise AssertionError('Published engine did not select SDK native ordered passes')
        # Companion to AnimalTown #47: the engine's exact transitive SDK
        # dependency must expose independent alpha and two-reference cutouts.
        water=work/'Water';water.mkdir()
        (water/'Water.csproj').write_text((app/'GameCube.csproj').read_text())
        shutil.copy2(ROOT/'tests/fixtures/sdk_water_consumer.cs',water/'Program.cs')
        run('dotnet','build',str(water),'-c','Release','-p:DndBuildNative=false','-warnaserror:NU1608')
        run('dotnet',str(compiler),'--bundle',str(water/'bin/Release/net8.0/Water.dll'),str(work/'native-water'))
        if not (work/'native-water/source/generated_program.c').is_file():raise AssertionError('No water alpha AOT bundle')
        if args.native:
            for project in (app,water):
                run('dotnet','build',str(project),'-c','Release','-p:DndBuildBackend=Docker')
                if not list(project.rglob('*.dol')) or not list(project.rglob('*.elf')):raise AssertionError('No external native executable: '+project.name)
    print('External NuGet restore, dependency closure and consumer checks passed')
if __name__=='__main__':main()
