"""Versioned, aligned asset packs, GX texture encoders and bounded mesh payloads."""
from pathlib import Path
import json
import math
import struct
import zlib
from . import mesh as meshes

HEADER = struct.Struct("<4s7I")
RECORD = struct.Struct("<16I")
TEXTURE, GEOMETRY, COLLISION, PATCH, SCENE = 1, 2, 3, 4, 5
RGBA8, CMPR = 6, 14


def align(value, alignment=32):
    return (value + alignment-1) // alignment * alignment


def crc(data):
    return zlib.crc32(data) & 0xffffffff


def color(x, y):
    x = max(0, min(191, x)); y = max(0, min(127, y))
    cellx, celly = x//32, y//32
    light = ((x//8) ^ (y//8) ^ cellx ^ celly) & 1
    return (45+cellx*24 if light else 18+cellx*16,
            65+celly*30 if light else 26+celly*20, 184 if light else 84, 255)


def tile(group, mip, tx, ty, gutter=4):
    useful = (32*group) >> mip
    width = align(useful+2*gutter, 8)
    step = 1 << mip
    pixels = bytearray()
    for y in range(width):
        for x in range(width):
            samples = [color(tx*group*32+(x-gutter)*step+dx,
                             ty*group*32+(y-gutter)*step+dy)
                       for dy in range(step) for dx in range(step)]
            pixels.extend(sum(s[c] for s in samples)//len(samples) for c in range(4))
    return width, useful, bytes(pixels)


def rgba8(pixels, width):
    if len(pixels) != width*width*4 or width % 4:
        raise ValueError("RGBA8 dimensions/alignment")
    out = bytearray(len(pixels))
    for y in range(width):
        for x in range(width):
            block = ((y//4)*(width//4)+x//4)*64
            at = ((y%4)*4+x%4)*2
            p = pixels[(y*width+x)*4:(y*width+x)*4+4]
            out[block+at] = p[3]; out[block+at+1] = p[0]
            out[block+32+at] = p[1]; out[block+33+at] = p[2]
    return bytes(out)


def _565(pixel):
    r,g,b = pixel[:3]
    return (r>>3)<<11 | (g>>2)<<5 | (b>>3)


def _rgb(word):
    return (((word>>11)&31)*255//31, ((word>>5)&63)*255//63, (word&31)*255//31)


def cmpr(pixels, width):
    if width % 8 or len(pixels) != width*width*4:
        raise ValueError("CMPR requires complete 8x8 blocks")
    out = bytearray()
    for by in range(0, width, 8):
        for bx in range(0, width, 8):
            for dy,dx in ((0,0),(0,4),(4,0),(4,4)):
                block = [tuple(pixels[((by+dy+y)*width+bx+dx+x)*4:((by+dy+y)*width+bx+dx+x)*4+3])
                         for y in range(4) for x in range(4)]
                c0 = _565(max(block, key=lambda p: sum(p)))
                c1 = _565(min(block, key=lambda p: sum(p)))
                c0,c1 = max(c0,c1),min(c0,c1)
                if c0 == c1:
                    if c0 < 65535: c0 += 1
                    else: c1 -= 1
                p0,p1 = _rgb(c0),_rgb(c1)
                palette = [p0,p1,tuple((2*a+b)//3 for a,b in zip(p0,p1)),tuple((a+2*b)//3 for a,b in zip(p0,p1))]
                indices = 0
                for pixel in block:
                    choice = min(range(4), key=lambda i: sum((a-b)**2 for a,b in zip(pixel,palette[i])))
                    indices = (indices<<2)|choice
                out.extend(struct.pack(">HHI", c0,c1,indices))
    return bytes(out)


def decode_cmpr(data, width):
    output = bytearray(width*width*4); at = 0
    for by in range(0,width,8):
        for bx in range(0,width,8):
            for dy,dx in ((0,0),(0,4),(4,0),(4,4)):
                a,b,bits = struct.unpack_from(">HHI",data,at); at += 8
                p,q = _rgb(a),_rgb(b)
                palette = [p,q,tuple((2*x+y)//3 for x,y in zip(p,q)),tuple((x+2*y)//3 for x,y in zip(p,q))]
                for i in range(16):
                    pixel = palette[(bits>>(30-i*2))&3]+(255,)
                    destination = ((by+dy+i//4)*width+bx+dx+i%4)*4
                    output[destination:destination+4] = bytes(pixel)
    return bytes(output)


class Builder:
    def __init__(self):
        self.items = []

    def add(self, kind, payload, *, fmt=0, group=0, mip=0, cell=0, parent=0, width=0, useful=0, material=0, variant=0):
        identity = len(self.items)+1
        self.items.append(dict(id=identity, kind=kind, payload=payload, fmt=fmt, group=group, mip=mip,
                               cell=cell, parent=parent, width=width, useful=useful, material=material, variant=variant))
        return identity

    def write(self, path):
        start = align(HEADER.size+len(self.items)*RECORD.size)
        data, records = bytearray(start), bytearray()
        for item in self.items:
            offset = align(len(data))
            data.extend(b"\0"*(offset-len(data))); data.extend(item["payload"])
            values = (item["id"],item["kind"],offset,len(item["payload"]),crc(item["payload"]),
                      item["fmt"],item["group"],item["mip"],item["cell"],item["parent"],
                      item["width"],item["useful"],item["material"],item["variant"],0,0)
            records.extend(RECORD.pack(*values))
        data.extend(b"\0"*(align(len(data))-len(data)))
        data[HEADER.size:HEADER.size+len(records)] = records
        data[:HEADER.size] = HEADER.pack(b"HCPK",1,len(self.items),HEADER.size,start,len(data),crc(records),0)
        Path(path).parent.mkdir(parents=True,exist_ok=True)
        Path(path).write_bytes(data)
        return dict(records=len(self.items), bytes=len(data), index_crc=crc(records))


def inspect(path):
    raw = Path(path).read_bytes()
    if len(raw) < HEADER.size:
        raise ValueError("Truncated header")
    magic,version,count,index,data,length,checksum,reserved = HEADER.unpack_from(raw)
    if magic != b"HCPK" or version != 1 or reserved or count > 4096 or index != HEADER.size or length != len(raw):
        raise ValueError("Invalid pack header")
    if data < index+count*RECORD.size or data % 32 or data > length:
        raise ValueError("Invalid index bounds")
    table = raw[index:index+count*RECORD.size]
    if crc(table) != checksum:
        raise ValueError("Index checksum")
    end = data
    for i in range(count):
        record = RECORD.unpack_from(table,i*RECORD.size)
        identity,kind,offset,size,digest = record[:5]
        if identity != i+1 or kind not in (TEXTURE,GEOMETRY,COLLISION,PATCH,SCENE) or offset % 32 or offset < end or size > length-offset:
            raise ValueError("Record range/order")
        if crc(raw[offset:offset+size]) != digest:
            raise ValueError("Payload checksum")
        end = offset+size
    return count


def mesh_payload(mesh, cell, uv_group=1):
    # 32-byte vertices: fixed XYZ, fixed normal, UV16, RGBA, reserved.
    out = bytearray(struct.pack("<4sHHI", b"HCMS",len(mesh.vertices),len(mesh.faces)*3,0))
    cx,cy = cell%6,cell//6
    for v in mesh.vertices:
        # The validation surface uses world-space planar material coordinates.
        # Imported authored UVs remain in the optimization report/export, and
        # conventional material textures can use the same payload UV channel.
        u = (v.p[0]/4-cx)/uv_group; t = (v.p[2]/4-cy)/uv_group
        nx,ny,nz = (max(-32767,min(32767,round(n*32767))) for n in v.n)
        out.extend(struct.pack("<iiihhhHH4BI2x",*(round(p*65536) for p in v.p),
                               nx,ny,nz,max(0,min(65535,round(u*65535))),max(0,min(65535,round(t*65535))),
                               255,255,255,255,0))
    for f in mesh.faces:
        out.extend(struct.pack("<HHH",*f[:3]))
    return bytes(out)


def build(path, report_path, source=None, resolution=48):
    raw = meshes.load(source) if source else meshes.adversarial_fixture(resolution)
    clean, report = meshes.repair(raw)
    minimum = [min(v.p[i] for v in clean.vertices) for i in range(3)]
    maximum = [max(v.p[i] for v in clean.vertices) for i in range(3)]
    if minimum[0] < 0 or minimum[2] < 0 or maximum[0] > 24.001 or maximum[2] > 16.001 or max(abs(x) for x in minimum+maximum) > 100:
        raise ValueError("Validation scene envelope is X=0..24, Z=0..16, |XYZ|<=100; normalize the imported static asset explicitly")
    report["bounds"] = [minimum,maximum]
    report["texture_binding"] = "validation planar checker material; authored PBR textures are not baked by this probe"
    builder = Builder()
    texture_ids = {}
    for fmt in (RGBA8,CMPR):
        for group in (1,2,4):
            for mip in (3,2,1,0):
                for ty in range(math.ceil(4/group)):
                    for tx in range(math.ceil(6/group)):
                        w,useful,pixels = tile(group,mip,tx,ty)
                        payload = rgba8(pixels,w) if fmt == RGBA8 else cmpr(pixels,w)
                        identity = builder.add(TEXTURE,payload,fmt=fmt,group=group,mip=mip,
                                               cell=ty*math.ceil(6/group)+tx,width=w,useful=useful)
                        texture_ids[fmt,group,mip,tx,ty] = identity
    # Stable parent references are assigned after all texture levels exist.
    for item in builder.items:
        if item["mip"] < 3:
            cols = math.ceil(6/item["group"])
            tx,ty = item["cell"]%cols,item["cell"]//cols
            item["parent"] = texture_ids[item["fmt"],item["group"],item["mip"]+1,tx,ty]
    report["lods"] = [dict(resolution=r,triangles=0,max_vertex_displacement=0,max_uv_displacement=0) for r in (0,8,4)]
    cells = [meshes.clipped_cell(clean,i) for i in range(24)]
    geometry_counts = []
    coarse = {}
    for mip,grid in enumerate((0,8,4)):
        count = 0
        for cell,part in enumerate(cells):
            if part is None:
                continue
            lod,metrics = (part,dict(triangles=len(part.faces),max_vertex_displacement=0,max_uv_displacement=0)) if grid==0 else meshes.simplify(part,grid)
            report["lods"][mip]["triangles"] += len(lod.faces)
            for key in ("max_vertex_displacement","max_uv_displacement"):
                report["lods"][mip][key] = max(report["lods"][mip][key],metrics[key])
            if mip==2:
                coarse[cell] = lod
            for cluster in meshes.clusters(lod,128):
                builder.add(GEOMETRY,mesh_payload(cluster,cell),mip=mip,cell=cell,
                            material=cluster.faces[0][3],variant=1)
                count += 1
        geometry_counts.append(count)
    # Raw triangle soup is deliberately retained as a separately selectable
    # baseline, split into equally bounded chunks; it is not the default render path.
    raw_finite = meshes.Mesh(raw.vertices,[f for f in raw.faces if len(set(f[:3])) == 3 and
                                         all(_finite_vertex(raw.vertices[i]) for i in f[:3])],
                            raw.materials,raw.provenance)
    raw_count = 0
    for cell in range(24):
        # Keep original triangle soup inside each cell for the raw draw baseline.
        part = meshes.clipped_cell(raw_finite,cell,False)
        if part is None: continue
        # Expand corners again: the raw path intentionally carries duplicate vertices.
        expanded = meshes.Mesh([part.vertices[i] for f in part.faces for i in f[:3]],
                               [(i*3,i*3+1,i*3+2,f[3]) for i,f in enumerate(part.faces)],part.materials,part.provenance)
        for cluster in meshes.clusters(expanded,128):
            builder.add(GEOMETRY,mesh_payload(cluster,cell),mip=0,cell=cell,material=cluster.faces[0][3],variant=0)
            raw_count += 1
    # Parent patches merge complete coarse cells, preserving exact clipped seams.
    node_count = 0
    def parent(x,z,span):
        nonlocal node_count
        node = node_count;node_count += 1
        for tex in range(2):
            selected = [part for cell,part in coarse.items() if x<=cell%6<x+span and z<=cell//6<z+span and (cell%6)//4==tex]
            if selected:
                merged = meshes.merge(selected)
                for cluster in meshes.clusters(merged,128,False):
                    builder.add(PATCH,mesh_payload(cluster,tex*4,4),group=span,mip=3,cell=node,material=tex,variant=cluster.faces[0][3])
        if span>1:
            for dz,dx in ((0,0),(0,span//2),(span//2,0),(span//2,span//2)):
                parent(x+dx,z+dz,span//2)
    parent(0,0,8)
    for cell in range(24):
        x,z = cell%6*4,cell//6*4
        heights = [meshes.surface_height(clean,x+dx*2,z+dz*2) for dz in range(3) for dx in range(3)]
        mask = sum(1<<i for i,h in enumerate(heights) if h is not None)
        values = [round((h or 0)*65536) for h in heights]
        builder.add(COLLISION,struct.pack("<4sI9i",b"HCCL",mask,*values),cell=cell)
    box = (8,0,8,16,6,9) if source is None else (0,0,0,0,0,0)
    builder.add(SCENE,struct.pack("<4s6iI6i",b"HCWD",*(round(v*65536) for v in minimum+maximum),
                                1 if source is None else 0,*(round(v*65536) for v in box)))
    parents=[item for item in builder.items if item["kind"]==PATCH]
    parent_bytes=sum(struct.unpack_from("<H",item["payload"],4)[0]*36+
                     struct.unpack_from("<H",item["payload"],6)[0]*2 for item in parents)
    if len(builder.items)>4096 or len(parents)>250 or parent_bytes>1024*1024:
        raise ValueError("Validation runtime bounds exceeded: reduce mesh/material complexity before packing (4096 records, 250 parent meshes, 1 MiB coarse geometry)")
    report["coarse_native_bytes"]=parent_bytes
    report["coarse_meshes"]=len(parents)
    report["pack"] = builder.write(path)
    report["geometry_clusters"] = geometry_counts
    report["raw_clusters"] = raw_count
    report["limits"] = ["Static geometry only", "Planar diagnostic material replaces authored PBR shading",
                        "Collision is a top-surface heightfield; walls/overhangs need a separate collision representation",
                        "Vertex-clustering displacement is measured; it is not a certified silhouette bound"]
    report["fixture_kind"] = "external" if source else "synthetic_adversarial"
    Path(report_path).write_text(json.dumps(report,indent=2)+"\n")
    inspect(path)
    return report


def _finite_vertex(v):
    return all(math.isfinite(x) for x in v.p+v.uv+v.n)
