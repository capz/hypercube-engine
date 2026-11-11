#!/usr/bin/env python3
"""Actual SDK AOT/native runtime, storage, graphics and ARAM under GC stress."""
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import argparse
from contextlib import nullcontext
ROOT=Path(__file__).resolve().parents[1]
SDK=Path(os.environ.get('DOLPHIN_DOTNET_ROOT',ROOT/'vendor/sdk')).resolve()
def run(*args,env=None):
    print('+',' '.join(map(str,args)),flush=True)
    subprocess.run(list(map(str,args)),cwd=ROOT,env=env,check=True)
def main(projects,configurations):
    run('python3','tests/progressive_fixtures.py')
    run('python3','tests/world_progressive_fixtures.py')
    run('dotnet','build',SDK/'tools/DolphinDotNet.Compiler','-c','Release')
    kept=os.environ.get('HYPERCUBE_TEST_WORK')
    with nullcontext(kept) if kept else tempfile.TemporaryDirectory(prefix='hypercube-lods-') as folder:
        work=Path(folder);(work/'sd').mkdir(parents=True,exist_ok=True);shutil.copyfile(ROOT/'out/progressive/textures.hcp',work/'sd/world.hcp');shutil.copyfile(ROOT/'out/progressive/world-mixed.hcp',work/'sd/world-mixed.hcp')
        sys.path.insert(0,str(ROOT/'tests'));from progressive_fixtures import dnlt
        from build_mip_tail import build as build_tail
        import struct
        dnlt(work/'tail.dnlod',32,16,list(range(6)),1)
        tail=build_tail(work/'tail.dnlod',work/'sd/tail.dndtex',0);(work/'sd/tail.crc').write_bytes(struct.pack('<I',tail['packageCrc32']))
        from model_sidecar_fixtures import build as build_model
        model_sidecar=build_model(work/'sd')
        run('python3','tools/build_texture_pack.py','--model-sidecar',model_sidecar,'--output',work/'sd/models.hcp','--bindings','out/progressive/ModelMaterials.g.cs')
        backend=(SDK/'tests/graphics_backend.c').read_text()
        backend=backend.replace('assert(t->data&&t->width==4&&t->height==4);const unsigned char *p=t->data;assert(p[0]==0&&p[1]==0);','assert(t->data&&t->width>0&&t->height>0);')
        backend=backend.replace('graphics_draws++;','texture_host_draws++;graphics_draws++;').replace('void dnd_draw_backend_wait(void){}','void dnd_draw_backend_wait(void){texture_host_waits++;}').replace('void dnd_draw_backend_publish_texture(void){}','void dnd_draw_backend_publish_texture(void){texture_host_publishes++;}')
        backend='extern int texture_host_waits,texture_host_draws,texture_host_publishes;\n'+backend
        (work/'backend.c').write_text(backend)
        sources=['managed_runtime','async_runtime','numeric','text','draw_resources','graphics_device','performance','model_transform','model_graphics','model','model_animation','storage','contiguous_write','file_optimize','file_extents','verified_copy','timing','stw','gc_stw','bcl','aram']
        env={**os.environ,'DND_ASYNC_MANUAL':'1','DND_STORAGE_ROOT':str(work)}
        for project,config in [(p,c) for p in projects for c in configurations]:
            run('dotnet','build',f'tests/{project}','-c',config,f'-p:DolphinDotNetRoot={SDK}')
            generated=work/(project+config+'.c');exe=work/(project+config)
            run('dotnet',SDK/'tools/DolphinDotNet.Compiler/bin/Release/net8.0/DolphinDotNet.Compiler.dll','--aot',f'tests/{project}/bin/{config}/net8.0/{project}.dll',generated)
            dead='-Wl,-dead_strip' if sys.platform=='darwin' else '-Wl,--gc-sections'
            defines=['-DHYPERCUBE_WORLD_RUNTIME'] if project=='WorldTextureLodRuntime' else []
            if project=='MipTailRuntime':defines=['-DHYPERCUBE_TAIL_RUNTIME']
            if project=='StreamedModelRuntime':defines=['-DHYPERCUBE_MODEL_RUNTIME']
            project_sources=sources+(['model_source','model_ufbx','model_obj'] if project=='StreamedModelRuntime' else [])
            run('cc','-std=c11','-O1','-D_DEFAULT_SOURCE','-D_DARWIN_C_SOURCE','-DDND_GRAPHICS_TEST',*defines,'-ffunction-sections','-fdata-sections','-pthread',f'-I{SDK}/include',generated,'tests/texture_lod_host.c',work/'backend.c',*[SDK/f'source/{s}.c' for s in project_sources],dead,'-lm','-o',exe)
            run(exe,env=env)
if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--project',choices=['TextureLodRuntime','WorldTextureLodRuntime','MipTailRuntime','StreamedModelRuntime']);p.add_argument('--configuration',choices=['Debug','Release']);a=p.parse_args()
    main([a.project] if a.project else ['TextureLodRuntime','WorldTextureLodRuntime','MipTailRuntime','StreamedModelRuntime'],[a.configuration] if a.configuration else ['Debug','Release'])
