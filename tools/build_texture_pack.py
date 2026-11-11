#!/usr/bin/env python3
"""Import SDK-authored DNLT levels into a versioned generic texture pack."""
import argparse
import hashlib
import json
from pathlib import Path
from hypercube import pack
from hypercube.model_bindings import read_sidecar,generate

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--texture',action='append',default=[],metavar='NAME=FILE.dnlod')
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--manifest',type=Path)
    parser.add_argument('--model-sidecar',action='append',type=Path,default=[])
    parser.add_argument('--bindings',type=Path)
    parser.add_argument('--class-name',default='ModelMaterials')
    args=parser.parse_args();builder=pack.Builder();sources=[];inputs=[]
    if not args.texture and not args.model_sidecar:parser.error('Supply textures or model sidecars')
    if args.model_sidecar and not args.bindings:parser.error('Model sidecars require --bindings')
    models=[read_sidecar(path) for path in args.model_sidecar]
    for value in args.texture:
        name,separator,path=value.partition('=')
        if not separator:parser.error('Use --texture NAME=FILE.dnlod')
        inputs.append((name,path));sources.append(dict(name=name,path=path,sha256=hashlib.sha256(Path(path).read_bytes()).hexdigest()))
    seen={name:hashlib.sha256(Path(path).read_bytes()).hexdigest() for name,path in inputs}
    for doc in models:
        for texture in doc['textures']:
            name=texture['family'];digest=texture['sha256']
            if name in seen:
                if seen[name]!=digest:raise ValueError('Conflicting shared model family')
                continue
            seen[name]=digest;inputs.append((name,texture['path']));sources.append(dict(name=name,path=texture['path'],sha256=digest))
    bindings=generate(models,args.class_name) if models else None
    builder.import_textures(inputs)
    report=builder.write(args.output);pack.inspect(args.output)
    if bindings is not None:
        args.bindings.parent.mkdir(parents=True,exist_ok=True);args.bindings.write_text(bindings)
    report['models']=[dict(model=d['model'],sha256=d['modelSha256'],factory='Create'+str(i)) for i,d in enumerate(models)]
    report['sources']=sources;report['families']=[]
    for family in builder.families:
        report['families'].append({**{k:v for k,v in family.items() if k!='levels'},'levels':[{k:v for k,v in level.items() if k!='payload'} for level in family['levels']]})
    manifest=args.manifest or args.output.with_suffix('.manifest.json');manifest.parent.mkdir(parents=True,exist_ok=True)
    manifest.write_text(json.dumps(report,indent=2,sort_keys=True)+'\n')
if __name__=='__main__':main()
