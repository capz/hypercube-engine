"""Independent DNDM fixture for the sidecar/pack/generated-binding contract."""
import hashlib
import json
from pathlib import Path
import struct
from progressive_fixtures import dnlt

def fnv(data):
    value=2166136261
    for b in data:value=((value^b)*16777619)&0xffffffff
    return value

def build(root):
    root=Path(root);root.mkdir(parents=True,exist_ok=True)
    texture=root/'shared.dnlod';dnlt(texture,16,8,list(range(5)),1);digest=hashlib.sha256(texture.read_bytes()).hexdigest()
    node=struct.pack('>i64s10f',-1,b'Root',0,0,0,0,0,0,1,2,3,1)
    vertices=b''
    for x,y,u,v in ((0,0,0,0),(1,0,1,0),(0,1,0,1)):
        vertices+=struct.pack('>12f4i4f',x,y,0,0,0,1,u,v,1,1,1,1,0,0,0,0,1,0,0,0)
    mesh=struct.pack('>5i6f',0,-1,1,3,3,0,0,0,1,1,0)+vertices+struct.pack('>3I',0,1,2)
    material=struct.pack('>4f2if3i',1,1,1,1,-1,2,.5,0,-1,0)
    sections=[struct.pack('>I',1)+node,struct.pack('>I',2)+mesh+mesh[:8]+struct.pack('>i',2)+mesh[12:],struct.pack('>I',3)+material*3,bytes(4),bytes(4),bytes(4)]
    data=bytearray(160);offset=160
    for i,s in enumerate(sections):
        struct.pack_into('>4I',data,16+i*16,i+1,offset,len(s),fnv(s));data.extend(s);data.extend(bytes((-len(data))%32));offset=len(data)
    struct.pack_into('>4I',data,0,0x444e444d,2,len(data),6)
    model=root/'parts.dndmodel';model.write_bytes(data)
    textures=[dict(textureIndex=i,family='textures/'+digest,file=texture.name,sha256=digest,width=16,height=8,wrapS=i,wrapT=2-i,linear=i==0) for i in range(2)]
    materials=[dict(materialIndex=i,primaryTexture=-1 if i==0 else 0,secondaryTexture=-1 if i==0 else 1,combiner=i,color=[1,1,1,1],mode=2,cutoff=.5,doubleSided=True) for i in range(3)]
    doc=dict(version=1,model=model.name,modelSha256=hashlib.sha256(data).hexdigest(),embeddedTextureCount=0,textures=textures,materials=materials)
    path=root/'parts.dndmodel.textures.json';path.write_text(json.dumps(doc));return path
