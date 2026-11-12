#!/usr/bin/env python3
"""Host CLR allocation comparison, never a GameCube heap measurement."""
import argparse
from pathlib import Path
import subprocess
import tempfile
import shutil
ROOT=Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser(description=__doc__);p.add_argument('--revision',help='Git source revision, or omit for the working tree');a=p.parse_args()
with tempfile.TemporaryDirectory(prefix='hypercube-core-memory-') as tmp:
    temp=Path(tmp);(temp/'Core').mkdir()
    if a.revision:
        paths=subprocess.check_output(['git','ls-tree','-r','--name-only',a.revision,'--','src/Hypercube.Core'],cwd=ROOT,text=True).splitlines()
        for path in paths:
            if path.endswith('.cs'):
                (temp/'Core'/Path(path).name).write_bytes(subprocess.check_output(['git','show',a.revision+':'+path],cwd=ROOT))
    else:
        for path in (ROOT/'src/Hypercube.Core').glob('*.cs'):shutil.copyfile(path,temp/'Core'/path.name)
    shutil.copyfile(ROOT/'tools/memory_probe.cs',temp/'Program.cs')
    (temp/'Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>')
    print('Source:',a.revision or 'working tree',flush=True)
    subprocess.run(['dotnet','run','--project',str(temp/'Probe.csproj'),'-c','Release'],check=True)
