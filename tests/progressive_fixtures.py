"""Small deterministic DNLT contract fixtures, independent of the SDK encoder."""
from pathlib import Path
import struct
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from hypercube import pack,progressive

def dnlt(path,w,h,levels,flags,fmt=6,gutter=0,pair_low=None):
    count=len(levels);start=progressive.align(64+count*48);payload=bytearray();table=bytearray(count*32);uv=bytearray()
    entries=[]
    for i,logical in enumerate(levels):
        lw,lh=pair_low if flags==4 and i==1 else (max(1,w>>logical),max(1,h>>logical))
        nw,nh=lw+gutter*2,lh+gutter*2
        size=progressive.native_bytes(nw,nh,fmt)
        # Four colors occupy the same usable quadrants at every resolution.
        native=bytearray(size)
        if fmt==6:
            for y in range((nh+3)//4*4):
                for x in range((nw+3)//4*4):
                    px=max(0,min(lw-1,x-gutter));py=max(0,min(lh-1,y-gutter))
                    color=(0,255,0) if px>=lw/2 else (255,0,0)
                    if py>=lh/2:color=(color[0],color[1],255)
                    at=((y//4)*((nw+3)//4)+x//4)*64+((y%4)*4+x%4)*2
                    native[at:at+2]=bytes((255,color[0]));native[at+32:at+34]=bytes(color[1:])
        else:
            for by in range((nh+7)//8):
                for bx in range((nw+7)//8):
                    for j,(dx,dy) in enumerate(((0,0),(4,0),(0,4),(4,4))):
                        px=max(0,min(lw-1,bx*8+dx-gutter));py=max(0,min(lh-1,by*8+dy-gutter))
                        color=(0x07e0 if px>=lw/2 else 0xf800)|(31 if py>=lh/2 else 0)
                        struct.pack_into('>HHI',native,(by*((nw+7)//8)+bx)*32+j*8,color,color,0)
        entries.append((logical,nw,nh,native));uv.extend(struct.pack('<4f',lw/nw,lh/nh,gutter/nw,gutter/nh))
    for i in range(count-1,-1,-1):
        logical,nw,nh,native=entries[i];offset=start+len(payload);payload.extend(native)
        struct.pack_into('<IHH6I',table,i*32,logical,nw,nh,fmt,0,offset,len(native),progressive.crc(native),0)
    metadata=table+uv
    header=progressive.HEADER.pack(b'DNLT',1,flags,w,h,fmt,count,count-1,64,start,start+len(payload),progressive.crc(metadata),64+count*32,len(uv),0,0)
    Path(path).write_bytes(header+metadata+bytes(start-64-len(metadata))+payload)

def build(root):
    root=Path(root);root.mkdir(parents=True,exist_ok=True)
    specifications=[('terrain/rgba',512,256,[0,2,5,9],3,6,4,None),('objects/cmpr',256,256,[0,2,5],3,14,4,None),('faces/pair',128,96,[0,1],4,6,0,(12,10)),('single',32,16,[0],0,6,0,None)]
    builder=pack.Builder();inputs=[]
    for i,(name,w,h,levels,flags,fmt,gutter,low) in enumerate(specifications):
        path=root/f'{i}.dnlod';dnlt(path,w,h,levels,flags,fmt,gutter,low);inputs.append((name,path))
    builder.import_textures(inputs)
    builder.write(root/'textures.hcp');pack.inspect(root/'textures.hcp')
    return builder
if __name__=='__main__':build(sys.argv[1] if len(sys.argv)>1 else 'out/progressive')
