#!/usr/bin/env python3
"""Build real engine packages against a published SDK dependency; never uploads."""
import argparse
import json
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET
from zipfile import ZipFile
ROOT=Path(__file__).resolve().parents[1]
IDS=('Capz.Hypercube.Core','Capz.Hypercube.GameCube')

def run(*args):subprocess.run(args,cwd=ROOT,check=True)
def inspect(package,identity,version,sdk_version=None):
    with ZipFile(package) as z:
        root=ET.fromstring(z.read(identity+'.nuspec'));metadata=next(e for e in root if e.tag.endswith('metadata'))
        def value(name):return next(e.text for e in metadata if e.tag.endswith('}'+name) or e.tag==name)
        assert value('id')==identity and value('version')==version
        assert value('license')=='GPL-2.0-only'
        for name in ['LICENSE','README.md','lib/net8.0/'+identity.removeprefix('Capz.')+'.dll','lib/net8.0/'+identity.removeprefix('Capz.')+'.pdb']:assert name in z.namelist(),name
        dependencies={e.attrib['id']:e.attrib['version'] for e in metadata.iter() if e.tag.endswith('}dependency') or e.tag=='dependency'}
        assert not any(e.startswith('DolphinDotNet') for e in dependencies) if identity==IDS[0] else True
        if identity==IDS[1]:
            assert dependencies[IDS[0]]==version,dependencies
            sdk=sdk_version or ET.parse(ROOT/'Directory.Build.props').find('.//DolphinDotNetPackageVersion').text
            assert dependencies['DolphinDotNet.GameCube']=='['+sdk+']',dependencies
            assert not any(n.startswith('lib/') and '/DolphinDotNet.' in n for n in z.namelist())
        return dependencies

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--version',required=True);p.add_argument('--output',type=Path,default=ROOT/'out/packages');p.add_argument('--sdk-source',type=Path,help='Build a test-only dependency feed from the exact pinned SDK before it is published');args=p.parse_args()
    if not re.fullmatch(r'0\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?',args.version):p.error('Use a 0.x.y stable or preview NuGet version')
    output=args.output.resolve();output.mkdir(parents=True,exist_ok=True)
    commit=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip()
    sdk_revision=(ROOT/'.sdk-revision').read_text().strip();sdk_version=ET.parse(ROOT/'Directory.Build.props').find('.//DolphinDotNetPackageVersion').text;extra=[]
    if args.sdk_source:
        sdk_root=args.sdk_source.resolve();actual=subprocess.check_output(['git','rev-parse','HEAD'],cwd=sdk_root,text=True).strip()
        if actual!=sdk_revision:p.error('SDK checkout must match .sdk-revision exactly')
        if subprocess.check_output(['git','status','--porcelain','--untracked-files=no'],cwd=sdk_root,text=True).strip():p.error('Pinned SDK must have no tracked changes')
        sdk_version='0.1.0-preview.source.'+sdk_revision[:12];sdk_feed=output/'sdk-feed'
        subprocess.run(['python3',str(sdk_root/'eng/pack.py'),'--version',sdk_version,'--output',str(sdk_feed)],cwd=sdk_root,check=True)
        extra=['-p:DolphinDotNetPackageVersion='+sdk_version,'-p:RestoreAdditionalProjectSources='+str(sdk_feed)]
    for identity in IDS:
        project=ROOT/'src'/identity.removeprefix('Capz.')/(identity.removeprefix('Capz.')+'.csproj')
        run('dotnet','pack',str(project),'-c','Release','-o',str(output),'-p:Version='+args.version,'-p:PackageVersion='+args.version,'-p:RepositoryCommit='+commit,'-p:ContinuousIntegrationBuild=true','-p:HypercubeUsePublishedSdk=true',*extra)
        inspect(output/(identity+'.'+args.version+'.nupkg'),identity,args.version,sdk_version)
    (output/'packages.json').write_text(json.dumps({'version':args.version,'commit':commit,'packages':list(IDS),'sdkVersion':sdk_version,'sdkRevision':sdk_revision,'sdkSourcePreview':bool(args.sdk_source)},indent=2)+'\n')
    print('Engine packages and NuGet dependency metadata verified')
if __name__=='__main__':main()
