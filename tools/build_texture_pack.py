#!/usr/bin/env python3
"""Import SDK-authored DNLT levels into a versioned generic texture pack."""
import argparse
import hashlib
import json
from pathlib import Path
from hypercube import pack

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--texture',action='append',required=True,metavar='NAME=FILE.dnlod')
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--manifest',type=Path)
    args=parser.parse_args();builder=pack.Builder();sources=[];inputs=[]
    for value in args.texture:
        name,separator,path=value.partition('=')
        if not separator:parser.error('Use --texture NAME=FILE.dnlod')
        inputs.append((name,path));sources.append(dict(name=name,path=path,sha256=hashlib.sha256(Path(path).read_bytes()).hexdigest()))
    builder.import_textures(inputs)
    report=builder.write(args.output);pack.inspect(args.output)
    report['sources']=sources;report['families']=[]
    for family in builder.families:
        report['families'].append({**{k:v for k,v in family.items() if k!='levels'},'levels':[{k:v for k,v in level.items() if k!='payload'} for level in family['levels']]})
    manifest=args.manifest or args.output.with_suffix('.manifest.json');manifest.parent.mkdir(parents=True,exist_ok=True)
    manifest.write_text(json.dumps(report,indent=2,sort_keys=True)+'\n')
if __name__=='__main__':main()
