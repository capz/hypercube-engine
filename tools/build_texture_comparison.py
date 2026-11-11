#!/usr/bin/env python3
"""Matched-content page/atlas assets for the physical-console comparison lane.
Uses the existing validation source image and native encoders. No timing is
simulated. Both packs contain independent levels and identical useful texels.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import struct
from hypercube import pack,progressive

def family(path,group,x,y,fmt):
    entries=[]
    for lod in (0,1,2):
        width,useful,pixels=pack.tile(group,lod,x,y)
        payload=pack.rgba8(pixels,width) if fmt==6 else pack.cmpr(pixels,width)
        entries.append(dict(lod=lod,width=width,useful=useful,payload=payload))
    count=len(entries);start=progressive.align(64+count*48);table=bytearray(32*count);uv=bytearray();payloads=bytearray()
    for i,level in enumerate(entries):
        width,useful=level['width'],level['useful'];uv.extend(struct.pack('<4f',useful/width,useful/width,4/width,4/width))
    for i in reversed(range(count)):
        e=entries[i];p=e['payload'];struct.pack_into('<IHH6I',table,i*32,e['lod'],e['width'],e['width'],fmt,0,start+len(payloads),len(p),progressive.crc(p),0);payloads.extend(p)
    metadata=table+uv;header=progressive.HEADER.pack(b'DNLT',1,3,32*group,32*group,fmt,count,count-1,64,start,start+len(payloads),progressive.crc(metadata),64+count*32,len(uv),0,0)
    path.write_bytes(header+metadata+bytes(start-64-len(metadata))+payloads)

def build(root,physical_ceiling=524288,fmt=6):
    if physical_ceiling<131072 or fmt not in (6,14):raise ValueError('Comparison configuration')
    root=Path(root);root.mkdir(parents=True,exist_ok=True);reports=[]
    content=bytes(c for y in range(128) for x in range(192) for c in pack.color(x,y))
    trace=[dict(frame=i,eye=[12+8*math.sin(i*.035),10,16+8*math.cos(i*.035)],target=[12,0,8]) for i in range(600)]
    (root/'camera.json').write_text(json.dumps(trace,separators=(',',':'))+'\n')
    for strategy,group in (('fine-pages',1),('cell-atlas',2)):
        builder=pack.Builder();inputs=[];quads=[]
        for y in range(4//group):
            for x in range(6//group):
                name=f'{strategy}/{x}/{y}';path=root/f'{strategy}-{x}-{y}.dnlod';family(path,group,x,y,fmt);inputs.append((name,path))
                quads.append(dict(family=name,x=x*group*4,z=y*group*4,width=group*4,height=group*4,normalizedUvs=True))
        builder.import_textures(inputs);file=root/(strategy+'.hcp');builder.write(file);pack.inspect(file)
        coarse=sum(len(f['levels'][-1]['payload']) for f in builder.families)
        largest=max(len(l['payload']) for f in builder.families for l in f['levels'])
        # The physical cap includes all coarse images, one replacement and one
        # retiring image, one 32 KiB source buffer, and the permanent placeholder.
        minimum=coarse+largest*2+32768+64
        if minimum>physical_ceiling:raise ValueError('Ceiling cannot hold coarse plus replacement/retirement/staging headroom')
        reports.append(dict(strategy=strategy,pack=file.name,packSha256=hashlib.sha256(file.read_bytes()).hexdigest(),quads=quads,coarseBytes=coarse,minimumPhysicalBytes=minimum))
    report=dict(schema=1,contentSha256=hashlib.sha256(content).hexdigest(),cameraTraceSha256=hashlib.sha256((root/'camera.json').read_bytes()).hexdigest(),format='RGBA8' if fmt==6 else 'CMPR',texelsPerWorldUnit=8,physicalCeilingBytes=physical_ceiling,sourceDimensions=[192,128],levels=[0,1,2],gutter=4,strategies=reports,hardwarePerformanceClaim=False)
    (root/'comparison.json').write_text(json.dumps(report,indent=2,sort_keys=True)+'\n');return report
if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--output',type=Path,default=Path('out/texture-comparison'));p.add_argument('--physical-ceiling',type=int,default=524288);p.add_argument('--format',choices=['rgba8','cmpr'],default='rgba8');a=p.parse_args();build(a.output,a.physical_ceiling,6 if a.format=='rgba8' else 14)
