#!/usr/bin/env python3
"""Author a bounded indivisible DNTX tail from validated independent DNLT levels."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
from hypercube.progressive import read_dnlt,crc

def build(source,output,start_level,maximum_native_bytes=65536,maximum_dimension=128):
    if not 32<=maximum_native_bytes<=262144 or maximum_dimension not in (2,4,8,16,32,64,128,256):raise ValueError('Tail budget/dimension limit')
    family=read_dnlt(source)
    if family['flags'] not in (1,3):raise ValueError('A hardware tail requires authored mip levels')
    levels=[x for x in family['levels'] if x['logical']>=start_level]
    if len(levels)<2 or levels[0]['logical']!=start_level or max(levels[0]['width'],levels[0]['height'])>maximum_dimension:raise ValueError('Tail start or dimension limit')
    first=levels[0];w,h=first['width'],first['height']
    if w&(w-1) or h&(h-1):raise ValueError('Hardware tail dimensions must be powers of two')
    for i,level in enumerate(levels):
        if level['logical']!=start_level+i or level['width']!=max(1,w>>i) or level['height']!=max(1,h>>i) or tuple(level['uv'])!=(1.,1.,0.,0.) or level['fmt']!=first['fmt']:raise ValueError('A hardware tail cannot contain sparse, padded-atlas or mixed-format levels')
    if levels[-1]['width']!=1 or levels[-1]['height']!=1:raise ValueError('Tail must end at 1x1')
    payload=b''.join(x['payload'] for x in levels)
    if len(payload)>maximum_native_bytes:raise ValueError('Entire mip tail exceeds native memory ceiling')
    package=struct.pack('<4s7I',b'DNTX',2,first['fmt'],w,h,len(payload),len(levels),0)+payload
    output=Path(output);output.parent.mkdir(parents=True,exist_ok=True);output.write_bytes(package)
    report=dict(mode='resident-mip-tail',logicalBaseLevel=start_level,width=w,height=h,format=first['fmt'],levels=len(levels),nativeBytes=len(payload),packageCrc32=crc(package),sha256=hashlib.sha256(package).hexdigest(),sourceSha256=hashlib.sha256(Path(source).read_bytes()).hexdigest())
    output.with_suffix(output.suffix+'.manifest.json').write_text(json.dumps(report,indent=2,sort_keys=True)+'\n')
    return report
if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('source',type=Path);p.add_argument('--output',type=Path,required=True);p.add_argument('--start-level',type=int,required=True);p.add_argument('--maximum-native-bytes',type=int,default=65536);p.add_argument('--maximum-dimension',type=int,default=128);a=p.parse_args()
    print(json.dumps(build(a.source,a.output,a.start_level,a.maximum_native_bytes,a.maximum_dimension)))
