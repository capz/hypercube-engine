"""Static OBJ/glTF/GLB ingestion, conservative repair, clustering and measured LODs.

No neural generator is used here. The adversarial fixture models common output
defects; imported assets retain their provenance in the build report.
"""
from dataclasses import dataclass, field
from pathlib import Path
import base64
import json
import math
import struct
from collections import defaultdict


@dataclass(frozen=True)
class Vertex:
    p: tuple
    uv: tuple = (0.0, 0.0)
    n: tuple = (0.0, 1.0, 0.0)


@dataclass
class Mesh:
    vertices: list
    faces: list  # (vertex0, vertex1, vertex2, material)
    materials: list = field(default_factory=lambda: ["default"])
    provenance: str = "unspecified"


def _finite(values):
    return all(math.isfinite(x) for x in values)


def _area2(a, b, c):
    u, v = [b[i] - a[i] for i in range(3)], [c[i] - a[i] for i in range(3)]
    return sum((u[(i+1) % 3] * v[(i+2) % 3] - u[(i+2) % 3] * v[(i+1) % 3]) ** 2 for i in range(3))


def load_obj(path):
    positions, uvs, normals, vertices, faces, lookup = [], [], [], [], [], {}
    materials, material = ["default"], 0
    def resolve(text, values):
        i = int(text)
        if i == 0:
            raise ValueError("OBJ indices are one-based")
        i = i - 1 if i > 0 else len(values) + i
        if not 0 <= i < len(values):
            raise ValueError("OBJ index out of range")
        return values[i]
    for line in Path(path).read_text().splitlines():
        words = line.split("#", 1)[0].split()
        if not words:
            continue
        tag, args = words[0], words[1:]
        if tag == "v":
            positions.append(tuple(map(float, args[:3])))
        elif tag == "vt":
            uvs.append(tuple(map(float, args[:2])))
        elif tag == "vn":
            normals.append(tuple(map(float, args[:3])))
        elif tag == "usemtl":
            name = " ".join(args)
            if name not in materials:
                materials.append(name)
            material = materials.index(name)
        elif tag == "f":
            if len(args) < 3:
                raise ValueError("OBJ face has fewer than three corners")
            # Fan triangulation is limited to convex polygons. Reject concave n-gons
            # rather than silently importing an invalid surface.
            polygon = []
            for corner in args:
                part = corner.split("/")
                p = resolve(part[0], positions)
                uv = resolve(part[1], uvs) if len(part) > 1 and part[1] else (0.0, 0.0)
                n = resolve(part[2], normals) if len(part) > 2 and part[2] else (0.0, 1.0, 0.0)
                value = Vertex(p, uv, n)
                if value not in lookup:
                    lookup[value] = len(vertices)
                    vertices.append(value)
                polygon.append(lookup[value])
            if len(polygon) > 3:
                points = [vertices[i].p for i in polygon]
                a, b, c = points[:3]
                normal = [(b[(i+1)%3]-a[(i+1)%3])*(c[(i+2)%3]-a[(i+2)%3])-
                          (b[(i+2)%3]-a[(i+2)%3])*(c[(i+1)%3]-a[(i+1)%3]) for i in range(3)]
                axis = max(range(3), key=lambda i: abs(normal[i]))
                plane = [i for i in range(3) if i != axis]
                turns = []
                for i in range(len(points)):
                    p, q, r = points[i-1], points[i], points[(i+1)%len(points)]
                    turns.append((q[plane[0]]-p[plane[0]])*(r[plane[1]]-q[plane[1]])-
                                 (q[plane[1]]-p[plane[1]])*(r[plane[0]]-q[plane[0]]))
                if any(t > 1e-8 for t in turns) and any(t < -1e-8 for t in turns):
                    raise ValueError("Concave OBJ polygon: triangulate before import")
            faces.extend((polygon[0], polygon[i], polygon[i+1], material) for i in range(1, len(polygon)-1))
    return Mesh(vertices, faces, materials, "imported OBJ: " + Path(path).name)


def _multiply(a, b):
    return [sum(a[k*4+r] * b[c*4+k] for k in range(4)) for c in range(4) for r in range(4)]


def _matrix(node):
    if "matrix" in node:
        if len(node["matrix"]) != 16:
            raise ValueError("Invalid glTF transform")
        return node["matrix"]
    x, y, z, w = node.get("rotation", (0, 0, 0, 1))
    if abs(x*x+y*y+z*z+w*w-1) > 1e-4:
        raise ValueError("glTF quaternion must be normalized")
    sx, sy, sz = node.get("scale", (1, 1, 1))
    tx, ty, tz = node.get("translation", (0, 0, 0))
    return [(1-2*y*y-2*z*z)*sx, (2*x*y+2*z*w)*sx, (2*x*z-2*y*w)*sx, 0,
            (2*x*y-2*z*w)*sy, (1-2*x*x-2*z*z)*sy, (2*y*z+2*x*w)*sy, 0,
            (2*x*z+2*y*w)*sz, (2*y*z-2*x*w)*sz, (1-2*x*x-2*y*y)*sz, 0, tx, ty, tz, 1]


def load_gltf(path):
    path = Path(path)
    raw = path.read_bytes()
    binary = None
    if raw[:4] == b"glTF":
        magic, version, length = struct.unpack_from("<4sII", raw)
        if version != 2 or length != len(raw):
            raise ValueError("Invalid GLB header")
        at, doc = 12, None
        while at < length:
            if at+8 > length:
                raise ValueError("Truncated GLB chunk")
            size, kind = struct.unpack_from("<II", raw, at); at += 8
            if size % 4 or at+size > length:
                raise ValueError("Invalid GLB chunk length")
            if kind == 0x4e4f534a:
                doc = json.loads(raw[at:at+size])
            elif kind == 0x004e4942:
                binary = raw[at:at+size]
            at += size
        if doc is None:
            raise ValueError("GLB has no JSON")
    else:
        doc = json.loads(raw)
    if str(doc.get("asset", {}).get("version")) != "2.0":
        raise ValueError("Only glTF 2.0 is supported")
    if doc.get("skins") or doc.get("animations"):
        raise ValueError("Static importer: skinned/animated input needs a separate asset path")
    if doc.get("extensionsRequired"):
        raise ValueError("Required glTF extensions need preprocessing: " + str(doc["extensionsRequired"]))
    buffers = []
    for item in doc.get("buffers", []):
        uri = item.get("uri")
        if uri is None:
            data = binary
        elif uri.startswith("data:"):
            data = base64.b64decode(uri.split(",", 1)[1], validate=True)
        else:
            target = (path.parent / uri).resolve()
            if not target.is_relative_to(path.parent.resolve()):
                raise ValueError("Buffer URI escapes the asset directory")
            data = target.read_bytes()
        if data is None or len(data) < item["byteLength"]:
            raise ValueError("Truncated glTF buffer")
        buffers.append(data)
    kinds = {5120: ("b", 1), 5121: ("B", 1), 5122: ("h", 2),
             5123: ("H", 2), 5125: ("I", 4), 5126: ("f", 4)}
    widths = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}
    def accessor(index):
        a = doc["accessors"][index]
        if "sparse" in a or "bufferView" not in a or a.get("count", 0) > 10000000:
            raise ValueError("Unsupported or oversized glTF accessor")
        view = doc["bufferViews"][a["bufferView"]]
        fmt, size = kinds[a["componentType"]]
        width = widths[a["type"]]; stride = view.get("byteStride", size*width)
        begin = a.get("byteOffset", 0); view_begin = view.get("byteOffset", 0)
        end = begin + max(0, a["count"]-1)*stride + size*width
        data = buffers[view["buffer"]]
        if stride < size*width or begin < 0 or end > view["byteLength"] or view_begin+end > len(data):
            raise ValueError("glTF accessor out of bounds")
        output = [struct.unpack_from("<"+fmt*width, data, view_begin+begin+i*stride) for i in range(a["count"])]
        if a.get("normalized"):
            signed = fmt.islower()
            limit = (1 << (size*8-(1 if signed else 0))) - 1
            output = [tuple(max(-1, v/limit) for v in values) for values in output]
        return output
    vertices, faces = [], []
    materials = [m.get("name", "material"+str(i)) for i, m in enumerate(doc.get("materials", []))] or ["default"]
    identity = [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1]
    visiting = set()
    def visit(index, parent):
        if index in visiting:
            raise ValueError("Cycle in glTF nodes")
        visiting.add(index)
        node = doc["nodes"][index]
        matrix = _multiply(parent, _matrix(node))
        if "mesh" in node:
            for primitive in doc["meshes"][node["mesh"]]["primitives"]:
                if primitive.get("mode", 4) != 4 or primitive.get("targets"):
                    raise ValueError("Only static triangle primitives are supported")
                attributes = primitive["attributes"]
                p = accessor(attributes["POSITION"])
                uv = accessor(attributes["TEXCOORD_0"]) if "TEXCOORD_0" in attributes else [(0,0)]*len(p)
                normals = accessor(attributes["NORMAL"]) if "NORMAL" in attributes else [(0,1,0)]*len(p)
                if len(uv) != len(p) or len(normals) != len(p):
                    raise ValueError("Attribute counts disagree")
                # Inverse-transpose handles non-uniform scaling.
                a,b,c,d,e,f,g,h,i = matrix[0],matrix[4],matrix[8],matrix[1],matrix[5],matrix[9],matrix[2],matrix[6],matrix[10]
                det = a*(e*i-f*h)-b*(d*i-f*g)+c*(d*h-e*g)
                if abs(det) < 1e-12:
                    raise ValueError("Singular glTF node transform")
                inverse = [e*i-f*h, f*g-d*i, d*h-e*g, c*h-b*i, a*i-c*g, b*g-a*h, b*f-c*e, c*d-a*f, a*e-b*d]
                start = len(vertices)
                for position, texcoord, normal in zip(p, uv, normals):
                    point = tuple(sum(matrix[k*4+r]*position[k] for k in range(3))+matrix[12+r] for r in range(3))
                    n = [sum(inverse[r*3+k]*normal[k] for k in range(3))/det for r in range(3)]
                    length = math.sqrt(sum(v*v for v in n))
                    n = tuple(v/length for v in n) if length else (0,1,0)
                    vertices.append(Vertex(point, tuple(texcoord), n))
                indices = [v[0] for v in accessor(primitive["indices"])] if "indices" in primitive else list(range(len(p)))
                if len(indices) % 3 or any(not isinstance(v, int) or not 0 <= v < len(p) for v in indices):
                    raise ValueError("Invalid glTF triangle indices")
                material = primitive.get("material", 0)
                if not 0 <= material < len(materials):
                    raise ValueError("Invalid material index")
                for at in range(0, len(indices), 3):
                    t = indices[at:at+3]
                    if det < 0:
                        t[1], t[2] = t[2], t[1]
                    faces.append(tuple(start+v for v in t)+(material,))
        for child in node.get("children", []):
            visit(child, matrix)
        visiting.remove(index)
    scenes = doc.get("scenes", [])
    roots = scenes[doc.get("scene", 0)]["nodes"] if scenes else list(range(len(doc.get("nodes", []))))
    for node in roots:
        visit(node, identity)
    return Mesh(vertices, faces, materials, "imported static glTF/GLB: "+path.name)


def load(path):
    return load_obj(path) if Path(path).suffix.lower() == ".obj" else load_gltf(path)


def repair(mesh):
    vertices, faces, keys, seen = [], [], {}, set()
    rejected = {"nonfinite": 0, "degenerate": 0, "duplicate_faces": 0}
    for face in mesh.faces:
        *indices, material = face
        if len(indices) != 3 or any(not 0 <= i < len(mesh.vertices) for i in indices):
            raise ValueError("Face index out of range")
        corners = [mesh.vertices[i] for i in indices]
        if any(not _finite(v.p+v.uv+v.n) for v in corners):
            rejected["nonfinite"] += 1; continue
        if _area2(*(v.p for v in corners)) < 1e-16:
            rejected["degenerate"] += 1; continue
        # Exact attribute welding preserves UV seams, normals and material boundaries.
        remapped = []
        for v in corners:
            key = (v, material)
            if key not in keys:
                keys[key] = len(vertices); vertices.append(v)
            remapped.append(keys[key])
        key = (min(tuple(remapped[i:]+remapped[:i]) for i in range(3)), material)
        if key in seen:
            rejected["duplicate_faces"] += 1; continue
        seen.add(key); faces.append(tuple(remapped)+(material,))
    if not faces:
        raise ValueError("No usable triangles remain")
    referenced = sorted({i for face in faces for i in face[:3]})
    mapping = {old: new for new, old in enumerate(referenced)}
    clean = Mesh([vertices[i] for i in referenced], [tuple(mapping[i] for i in f[:3])+(f[3],) for f in faces], mesh.materials, mesh.provenance)
    edges = defaultdict(int)
    for face in clean.faces:
        for a, b in ((face[0], face[1]), (face[1], face[2]), (face[2], face[0])):
            edges[tuple(sorted((clean.vertices[a].p, clean.vertices[b].p)))] += 1
    report = dict(source_vertices=len(mesh.vertices), source_triangles=len(mesh.faces),
                  vertices=len(clean.vertices), triangles=len(clean.faces), removed=rejected,
                  nonmanifold_edges=sum(v > 2 for v in edges.values()), boundary_edges=sum(v == 1 for v in edges.values()),
                  provenance=mesh.provenance)
    return clean, report


def simplify(mesh, resolution):
    """Vertex clustering with explicit position/UV error and protected boundaries.

    This is a measured starting LOD strategy, not a general topology repair or
    guaranteed screen-space/silhouette-error simplifier.
    """
    minimum = [min(v.p[i] for v in mesh.vertices) for i in range(3)]
    extent = [max(v.p[i] for v in mesh.vertices)-minimum[i] for i in range(3)]
    position_edges = defaultdict(int)
    for f in mesh.faces:
        for a,b in ((f[0],f[1]),(f[1],f[2]),(f[2],f[0])):
            position_edges[tuple(sorted((mesh.vertices[a].p,mesh.vertices[b].p)))] += 1
    protected = {p for edge,count in position_edges.items() if count != 2 for p in edge}
    keys, remap, vertices, max_error, max_uv_error = {}, [], [], 0.0, 0.0
    # Keep distinct material islands and quantized normal/UV domains.
    uses = defaultdict(set)
    for f in mesh.faces:
        for i in f[:3]:
            uses[i].add(f[3])
    for i,v in enumerate(mesh.vertices):
        step = max(max(extent), 1e-8)/resolution
        spatial = tuple(round((v.p[k]-minimum[k])/step) for k in range(3))
        uv = tuple(round(t*resolution) for t in v.uv)
        normal = tuple(round(t*16) for t in v.n)
        key = (v.p if v.p in protected else spatial, uv, normal, tuple(sorted(uses[i])))
        if key not in keys:
            keys[key] = len(vertices); vertices.append(v)
        target = keys[key]; remap.append(target)
        max_error = max(max_error, math.dist(v.p, vertices[target].p))
        max_uv_error = max(max_uv_error, math.dist(v.uv, vertices[target].uv))
    faces = [tuple(remap[i] for i in f[:3])+(f[3],) for f in mesh.faces]
    result, _ = repair(Mesh(vertices, faces, mesh.materials, mesh.provenance))
    return result, dict(resolution=resolution, triangles=len(result.faces),
                        max_vertex_displacement=max_error, max_uv_displacement=max_uv_error)


def clusters(mesh, max_triangles=128, spatial=True):
    """Stable spatial/material ordering, bounded chunks and no cross-seam welds."""
    if not 1 <= max_triangles <= 256:
        raise ValueError("Cluster triangle limit must be 1..256")
    def key(face):
        center = tuple(sum(mesh.vertices[i].p[k] for i in face[:3])/3 for k in range(3))
        return (face[3], math.floor(center[0]/4) if spatial else 0, math.floor(center[2]/4) if spatial else 0, center)
    ordered = sorted(mesh.faces, key=key)
    current, previous = [], None
    for face in ordered:
        group = key(face)[:3]
        if current and (len(current) >= max_triangles or group != previous):
            yield _submesh(mesh, current); current = []
        current.append(face); previous = group
    if current:
        yield _submesh(mesh, current)


def _submesh(mesh, faces):
    ids = sorted({i for f in faces for i in f[:3]})
    remap = {old:new for new,old in enumerate(ids)}
    return Mesh([mesh.vertices[i] for i in ids],
                [tuple(remap[i] for i in f[:3])+(f[3],) for f in faces],
                mesh.materials, mesh.provenance)


def clipped_cell(mesh, cell, clean_result=True):
    """Clip before LOD construction, making cell boundaries explicit/protected."""
    x0,z0 = cell%6*4,cell//6*4
    vertices,faces = [],[]
    for face in mesh.faces:
        polygon = [mesh.vertices[i] for i in face[:3]]
        for axis,limit,positive in ((0,x0,True),(0,x0+4,False),(2,z0,True),(2,z0+4,False)):
            result = []
            if not polygon:
                break
            previous = polygon[-1]
            for current in polygon:
                a = previous.p[axis]-limit; b = current.p[axis]-limit
                ina,inb = (a>=-1e-9,b>=-1e-9) if positive else (a<=1e-9,b<=1e-9)
                if ina != inb:
                    t = a/(a-b)
                    def mix(p,q): return tuple(round(v+(w-v)*t,9) for v,w in zip(p,q))
                    result.append(Vertex(mix(previous.p,current.p),mix(previous.uv,current.uv),mix(previous.n,current.n)))
                if inb:
                    result.append(current)
                previous = current
            polygon = result
        if len(polygon) >= 3:
            polygon=[Vertex(tuple(round(v,8) for v in p.p),tuple(round(v,8) for v in p.uv),tuple(round(v,8) for v in p.n)) for p in polygon]
            start = len(vertices);vertices.extend(polygon)
            faces.extend((start,start+i,start+i+1,face[3]) for i in range(1,len(polygon)-1))
    if not faces:
        return None
    result=Mesh(vertices,faces,mesh.materials,mesh.provenance)
    if clean_result and not any(_area2(*(vertices[i].p for i in f[:3]))>=1e-16 for f in faces):
        return None
    return repair(result)[0] if clean_result else result


def merge(parts):
    vertices,faces = [],[]
    for part in parts:
        start = len(vertices);vertices.extend(part.vertices)
        faces.extend(tuple(start+i for i in f[:3])+(f[3],) for f in part.faces)
    return Mesh(vertices,faces,parts[0].materials if parts else ["default"],"merged cell hierarchy")


def surface_height(mesh,x,z):
    result = None
    for f in mesh.faces:
        a,b,c = [mesh.vertices[i].p for i in f[:3]]
        determinant = (b[2]-c[2])*(a[0]-c[0])+(c[0]-b[0])*(a[2]-c[2])
        if abs(determinant)<1e-12:
            continue
        u = ((b[2]-c[2])*(x-c[0])+(c[0]-b[0])*(z-c[2]))/determinant
        v = ((c[2]-a[2])*(x-c[0])+(a[0]-c[0])*(z-c[2]))/determinant
        w = 1-u-v
        if min(u,v,w)>=-1e-6:
            y = u*a[1]+v*b[1]+w*c[1]
            result = y if result is None else max(result,y)
    return result


def height(x, z):
    return .22*math.sin(x*.9)*math.cos(z*.7)


def adversarial_fixture(resolution=48):
    vertices, faces = [], []
    # Deliberately emit independent corners and repeated faces, like triangle soup.
    for z in range(resolution):
        for x in range(resolution):
            p = [(x/resolution*24, z/resolution*16), ((x+1)/resolution*24, z/resolution*16),
                 (x/resolution*24, (z+1)/resolution*16), ((x+1)/resolution*24, (z+1)/resolution*16)]
            for corners in ((0,2,1),(1,2,3)):
                at = len(vertices)
                for i in corners:
                    px,pz = p[i]
                    vertices.append(Vertex((px,height(px,pz),pz), (px/24,pz/16)))
                material = (x // max(1,resolution//3)) % 3
                faces.append((at,at+1,at+2,material))
                if (x+z) % 17 == 0:
                    faces.append((at,at+1,at+2,material))
                if (x+z) % 29 == 0:
                    faces.append((at,at,at+1,material))
    # Disconnected floating tetrahedron: cleanup must not silently discard it.
    at = len(vertices)
    vertices += [Vertex((12,2,8)),Vertex((12.2,2,8)),Vertex((12,2.2,8)),Vertex((12,2,8.2))]
    faces += [(at,at+2,at+1,0),(at,at+1,at+3,0),(at,at+3,at+2,0),(at+1,at+2,at+3,0)]
    # Authored opaque occluder with verified bounds, retained in every fixture LOD.
    at=len(vertices)
    vertices += [Vertex((x,y,z)) for z in (8,9) for y in (0,6) for x in (8,16)]
    for quad in ((0,1,3,2),(4,6,7,5),(0,4,5,1),(2,3,7,6),(0,2,6,4),(1,5,7,3)):
        faces += [(at+quad[0],at+quad[1],at+quad[2],1),(at+quad[0],at+quad[2],at+quad[3],1)]
    return Mesh(vertices,faces,["stone","metal","paint"],
                "synthetic adversarial triangle soup; not an actual AI-generated asset")
