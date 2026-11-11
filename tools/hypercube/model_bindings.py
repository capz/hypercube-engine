"""Validate SDK model sidecars and generate allocation-free material wiring.
Density is measured from rest-pose geometry, after authored UV transforms.
Animated deformation requires an application-provided conservative multiplier.
"""
import hashlib
import json
import math
from pathlib import Path
import re
import struct

from .progressive import read_dnlt

def _inside(root, name):
    if not isinstance(name,str) or not name or Path(name).is_absolute():raise ValueError('Relative model asset path required')
    path=(root/name).resolve()
    if not path.is_relative_to(root):raise ValueError('Model asset escapes sidecar directory')
    return path

def _digest(path, expected):
    if not isinstance(expected,str) or not re.fullmatch('[0-9a-f]{64}',expected) or hashlib.sha256(path.read_bytes()).hexdigest()!=expected:raise ValueError('Model asset SHA256 mismatch')

def _integer(v,low,high):
    if type(v)!=int or not low<=v<=high:raise ValueError('Model integer range')
    return v

def _number(v,low,high):
    if type(v) not in (int,float) or not math.isfinite(v) or not low<=v<=high:raise ValueError('Model numeric range')
    return v

def model_density(path):
    data=path.read_bytes()
    if len(data)<112 or data[:4]!=b'DNDM' or struct.unpack_from('>I',data,4)[0] not in (1,2):raise ValueError('DNDM version')
    
    def fnv(raw):
        value=2166136261
        for b in raw:value=((value^b)*16777619)&0xffffffff
        return value
    if len(data)>16*1024*1024 or struct.unpack_from('>2I',data,8)!=(len(data),6):raise ValueError('DNDM size/sections')
    sections=[];end=160
    for i in range(6):
        kind,offset,size,checksum=struct.unpack_from('>4I',data,16+i*16)
        if kind!=i+1 or offset<end or offset%32 or size<4 or offset+size>len(data) or fnv(data[offset:offset+size])!=checksum:raise ValueError('DNDM section')
        sections.append(memoryview(data)[offset:offset+size]);end=offset+size
    if struct.unpack_from('>I',sections[3])[0]!=0:raise ValueError('Streamed model contains embedded textures')
    material_count=struct.unpack_from('>I',sections[2])[0]
    _integer(material_count,1,4096)
    nodes=sections[0];count=struct.unpack_from('>I',nodes)[0]
    if not 1<=count<=1024 or len(nodes)!=4+108*count:raise ValueError('DNDM node table')
    transforms=[None]*count
    def global_matrix(index,parents=()):
        if index in parents or len(parents)>=128 or not 0<=index<count:raise ValueError('DNDM node hierarchy')
        if transforms[index] is not None:return transforms[index]
        at=4+index*108;parent=struct.unpack_from('>i',nodes,at)[0]
        tx,ty,tz,x,y,z,w,sx,sy,sz=struct.unpack_from('>10f',nodes,at+68)
        if not all(math.isfinite(v) for v in (tx,ty,tz,x,y,z,w,sx,sy,sz)):raise ValueError('DNDM transform')
        m=((1-2*(y*y+z*z))*sx,2*(x*y-z*w)*sy,2*(x*z+y*w)*sz,tx,
           2*(x*y+z*w)*sx,(1-2*(x*x+z*z))*sy,2*(y*z-x*w)*sz,ty,
           2*(x*z-y*w)*sx,2*(y*z+x*w)*sy,(1-2*(x*x+y*y))*sz,tz,
           0,0,0,1)
        if parent!=-1:
            a=global_matrix(parent,parents+(index,));m=tuple(sum(a[r*4+k]*m[k*4+c] for k in range(4)) for r in range(4) for c in range(4))
        transforms[index]=m;return m
    for i in range(count):global_matrix(i)
    skins=[];raw_skins=sections[4];skin_at=4
    skin_count=struct.unpack_from('>I',raw_skins)[0];_integer(skin_count,0,4096)
    for _ in range(skin_count):
        joints=struct.unpack_from('>I',raw_skins,skin_at)[0];skin_at+=4;_integer(joints,1,256);palette=[]
        for _ in range(joints):
            joint=struct.unpack_from('>I',raw_skins,skin_at)[0];inverse=struct.unpack_from('>16f',raw_skins,skin_at+4);skin_at+=68
            if joint>=count or not all(math.isfinite(v) for v in inverse):raise ValueError('DNDM skin palette')
            # DNDM inverse bind matrices are column-major; use row-major math here.
            inverse=tuple(inverse[c*4+r] for r in range(4) for c in range(4));a=transforms[joint]
            palette.append(tuple(sum(a[r*4+k]*inverse[k*4+c] for k in range(4)) for r in range(4) for c in range(4)))
        skins.append(palette)
    if skin_at!=len(raw_skins):raise ValueError('DNDM skin bytes')
    result=[[0.,0.] for _ in range(material_count)];meshes=sections[1];at=4
    mesh_count=struct.unpack_from('>I',meshes)[0];_integer(mesh_count,1,4096)
    for _ in range(mesh_count):
        node,skin,material,vertices,indices=struct.unpack_from('>5i',meshes,at)
        if not 0<=node<count or not 0<=material<material_count or not 1<=vertices<=65535 or not 3<=indices<=196605 or indices%3:raise ValueError('DNDM mesh')
        start=at+44;end=start+vertices*80+indices*4
        if end>len(meshes):raise ValueError('DNDM vertex bounds')
        if not -1<=skin<len(skins):raise ValueError('DNDM skin index')
        m=transforms[node]
        def vertex(index):
            if not 0<=index<vertices:raise ValueError('DNDM index')
            base=start+index*80;x,y,z=struct.unpack_from('>3f',meshes,base);u,v=struct.unpack_from('>2f',meshes,base+24)
            values=(x,y,z,u,v)
            if not all(math.isfinite(t) for t in values):raise ValueError('DNDM vertex')
            def transformed(matrix):return tuple(sum(matrix[r*4+k]*values[k] for k in range(3))+matrix[r*4+3] for r in range(3))
            if skin>=0:
                joints=struct.unpack_from('>4i',meshes,base+48);weights=struct.unpack_from('>4f',meshes,base+64)
                if any(not math.isfinite(weight) or weight<0 for weight in weights) or abs(sum(weights)-1)>.001:raise ValueError('DNDM skin weights')
                positions=[]
                for joint,weight in zip(joints,weights):
                    if not 0<=joint<len(skins[skin]):raise ValueError('DNDM joint index')
                    positions.append(transformed(skins[skin][joint]))
                position=tuple(sum(positions[j][r]*weights[j] for j in range(4)) for r in range(3))
            else:position=transformed(m)
            return position,(u,v)
        for j in range(0,indices,3):
            va=[vertex(struct.unpack_from('>I',meshes,start+vertices*80+4*(j+k))[0]) for k in range(3)]
            e1=[va[1][0][k]-va[0][0][k] for k in range(3)];e2=[va[2][0][k]-va[0][0][k] for k in range(3)]
            du1,dv1=(va[1][1][k]-va[0][1][k] for k in range(2));du2,dv2=(va[2][1][k]-va[0][1][k] for k in range(2));det=du1*dv2-du2*dv1
            if abs(det)>1e-12:
                u=math.sqrt(sum(((e1[k]*dv2-e2[k]*dv1)/det)**2 for k in range(3)))
                v=math.sqrt(sum(((e2[k]*du1-e1[k]*du2)/det)**2 for k in range(3)))
                result[material]=[max(result[material][0],u),max(result[material][1],v)]
        at=end
    if at!=len(meshes):raise ValueError('DNDM trailing mesh bytes')
    return result

def read_sidecar(path):
    path=Path(path).resolve();root=path.parent;doc=json.loads(path.read_text())
    if doc.get('version')!=1 or doc.get('embeddedTextureCount')!=0:raise ValueError('Model sidecar version')
    model=_inside(root,doc['model']);_digest(model,doc['modelSha256']);density=model_density(model)
    textures=doc['textures'];materials=doc['materials']
    if not isinstance(textures,list) or len(textures)>4096 or not isinstance(materials,list) or len(materials)!=len(density):raise ValueError('Model sidecar counts')
    for i,t in enumerate(textures):
        if t['textureIndex']!=i:raise ValueError('Model texture index')
        source=_inside(root,t['file']);_digest(source,t['sha256'])
        if t['family']!='textures/'+t['sha256']:raise ValueError('Content-addressed family')
        d=read_dnlt(source)
        if (t['width'],t['height'])!=(d['width'],d['height']):raise ValueError('Model texture dimensions')
        _integer(t['wrapS'],0,2);_integer(t['wrapT'],0,2)
        if type(t['linear'])!=bool:raise ValueError('Model filter')
        t['path']=str(source)
    for i,m in enumerate(materials):
        if m['materialIndex']!=i:raise ValueError('Model material index')
        for key in ('primaryTexture','secondaryTexture'):_integer(m[key],-1,len(textures)-1)
        _integer(m['combiner'],0,2);_integer(m['mode'],0,7);_number(m['cutoff'],0,1)
        if m['mode']&5==5 or len(m['color'])!=4 or type(m['doubleSided'])!=bool:raise ValueError('Model material flags')
        for c in m['color']:_number(c,0,1)
        if (m['secondaryTexture']>=0)!=(m['combiner']!=0) or m['combiner'] and m['primaryTexture']<0:raise ValueError('Model combiner textures')
        m['worldPerU'],m['worldPerV']=density[i]
    return doc

def generate(models, class_name='ModelMaterials'):
    if not re.fullmatch('[A-Za-z_][A-Za-z0-9_]*',class_name) or class_name in ('abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while record required file'.split()):raise ValueError('C# class identifier')
    lines=['// Generated from verified SDK model sidecars. Do not edit.', 'using Dolphin.Graphics;', 'using Hypercube;', 'using Hypercube.GameCube;', 'public static class '+class_name, '{']
    def num(v):return format(v,'.9g')+'f'
    for index,doc in enumerate(models):
        lines += ['    // '+json.dumps(doc['model']),f'    public static StreamedModelMaterials Create{index}(TextureLodStreamer stream, TextureQualityPolicy policy=TextureQualityPolicy.DistanceMip)', '    {',f'        var result=new StreamedModelMaterials(stream,{len(doc["materials"])});', '        try', '        {']
        for m in doc['materials']:
            refs=[]
            for key in ('primaryTexture','secondaryTexture'):
                t=doc['textures'][m[key]] if m[key]>=0 else None
                refs.append('stream.Get('+json.dumps(t['family'])+',policy)' if t else 'null')
            color=','.join(str(int(c*255+.5)) for c in m['color'])
            lines += [f'            var m{m["materialIndex"]}=new Material{{Color=new Color32({color}),Lighting=LightingMode.{"Unlit" if m["mode"]&2 else "Diffuse"},Blend=BlendMode.{"Alpha" if m["mode"]&4 else "Opaque"},DepthWrite={str(not bool(m["mode"]&4)).lower()},Cull=CullMode.{"None" if m["doubleSided"] else "Back"},AlphaCutoff={num(m["cutoff"] if m["mode"]&1 else 0)}}};']
            for key,slot in (('primaryTexture','Sampler'),('secondaryTexture','SecondarySampler')):
                if m[key]>=0:
                    t=doc['textures'][m[key]];wrap=['Clamp','Repeat','Mirror'];filt='Linear' if t['linear'] else 'Nearest'
                    lines += [f'            m{m["materialIndex"]}.{slot}=new SamplerState(TextureAddressMode.{wrap[t["wrapS"]]},TextureAddressMode.{wrap[t["wrapT"]]},TextureFilter.{filt});']
            lines += [f'            result.SetPart({m["materialIndex"]},m{m["materialIndex"]},{refs[0]},{refs[1]},{m["combiner"]},{num(m["worldPerU"])},{num(m["worldPerV"])});']
        lines += ['            return result;', '        }', '        catch {result.Dispose();throw;}', '    }']
    return '\n'.join(lines+['}',''])
