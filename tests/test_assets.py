import base64
import json
import math
from pathlib import Path
import struct
import sys
import tempfile
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from hypercube import mesh,pack,disc

class Assets(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.root=Path(self.temp.name)
    def tearDown(self):self.temp.cleanup()
    def obj(self,text):
        p=self.root/'input.obj';p.write_text(text);return mesh.load(p)
    def test_obj_negative_indices_and_material_seams(self):
        m=self.obj('v 0 0 0\nv 1 0 0\nv 0 0 1\nusemtl A\nf -3 -2 -1\nv 1 0 1\nusemtl B\nf -3 -2 -1\n')
        self.assertEqual(len(m.faces),2);self.assertNotEqual(m.faces[0][3],m.faces[1][3])
        self.assertEqual(m.vertices[m.faces[1][2]].p,(1,0,1))
        with self.assertRaises(ValueError):self.obj('v 0 0 0\nf 0 1 1')
    def test_concave_obj_fails_closed(self):
        with self.assertRaises(ValueError):self.obj('v 0 0 0\nv 2 0 0\nv 1 0 .5\nv 2 0 2\nv 0 0 2\nf 1 2 3 4 5')
    def gltf(self,**node):
        raw=struct.pack('<18f3H',0,0,0,1,0,0,0,1,0,1,0,0,1,0,0,1,0,0,0,1,2)
        doc={'asset':{'version':'2.0'},'buffers':[{'uri':'data:application/octet-stream;base64,'+base64.b64encode(raw).decode(),'byteLength':len(raw)}],
             'bufferViews':[{'buffer':0,'byteLength':36},{'buffer':0,'byteOffset':36,'byteLength':36},{'buffer':0,'byteOffset':72,'byteLength':6}],
             'accessors':[{'bufferView':0,'componentType':5126,'type':'VEC3','count':3},{'bufferView':1,'componentType':5126,'type':'VEC3','count':3},{'bufferView':2,'componentType':5123,'type':'SCALAR','count':3}],
             'meshes':[{'primitives':[{'attributes':{'POSITION':0,'NORMAL':1},'indices':2}]}],
             'nodes':[dict(mesh=0,**node)],'scenes':[{'nodes':[0]}]}
        return doc,raw
    def test_gltf_normal_inverse_transpose_and_mirror(self):
        doc,_=self.gltf(rotation=[0,math.sqrt(.5),0,math.sqrt(.5)],scale=[2,1,3],translation=[4,0,4]);p=self.root/'scene.gltf';p.write_text(json.dumps(doc));m=mesh.load(p)
        self.assertAlmostEqual(m.vertices[0].n[0],0,places=6);self.assertAlmostEqual(m.vertices[0].n[2],-1,places=6)
        self.assertEqual(m.vertices[0].p,(4,0,4))
        doc,_=self.gltf(scale=[-1,1,1]);p.write_text(json.dumps(doc));self.assertEqual(mesh.load(p).faces[0][:3],(0,2,1))
    def test_glb_and_accessor_bounds(self):
        doc,raw=self.gltf();del doc['buffers'][0]['uri'];js=json.dumps(doc).encode();js+=b' '*((-len(js))%4);raw+=b'\0'*((-len(raw))%4)
        data=struct.pack('<4sII',b'glTF',2,12+8+len(js)+8+len(raw))+struct.pack('<II',len(js),0x4e4f534a)+js+struct.pack('<II',len(raw),0x004e4942)+raw
        p=self.root/'scene.glb';p.write_bytes(data);self.assertEqual(len(mesh.load(p).faces),1)
        p.write_bytes(data[:-1])
        with self.assertRaises(ValueError):mesh.load(p)
        doc,_=self.gltf();doc['accessors'][0]['count']=4;p=self.root/'scene.gltf';p.write_text(json.dumps(doc))
        with self.assertRaises(ValueError):mesh.load(p)
        doc,_=self.gltf();doc['animations']=[{}];p.write_text(json.dumps(doc))
        with self.assertRaises(ValueError):mesh.load(p)
    def test_repair_preserves_island_and_materials(self):
        raw=mesh.adversarial_fixture(12);clean,report=mesh.repair(raw)
        self.assertGreater(report['removed']['duplicate_faces'],0);self.assertGreater(report['removed']['degenerate'],0)
        self.assertLess(len(clean.vertices),len(raw.vertices));self.assertGreaterEqual(len(set(f[3] for f in clean.faces)),2)
        self.assertTrue(any(v.p[1]>1 for v in clean.vertices))
        chunks=list(mesh.clusters(clean,32));self.assertEqual(sum(len(c.faces) for c in chunks),len(clean.faces));self.assertTrue(all(len(c.faces)<=32 for c in chunks))
    def test_repair_keeps_opposite_winding_surface(self):
        m=mesh.Mesh([mesh.Vertex((0,0,0)),mesh.Vertex((1,0,0)),mesh.Vertex((0,0,1))],[(0,1,2,0),(1,2,0,0),(0,2,1,0)])
        clean,r=mesh.repair(m);self.assertEqual(len(clean.faces),2);self.assertEqual(r['removed']['duplicate_faces'],1)
    def test_lod_protects_clipped_cell_boundary(self):
        clean,_=mesh.repair(mesh.adversarial_fixture(32));a=mesh.clipped_cell(clean,6);b=mesh.clipped_cell(clean,7)
        edge=lambda m:{v.p for v in m.vertices if v.p[0]==4 and 4<=v.p[2]<=8}
        self.assertEqual(edge(a),edge(b))
        low,report=mesh.simplify(a,4);self.assertLessEqual(len(low.faces),len(a.faces));self.assertEqual(edge(a),edge(low));self.assertGreaterEqual(report['max_vertex_displacement'],0)
    def test_empty_adjacent_cell_does_not_fail_import(self):
        m=mesh.Mesh([mesh.Vertex((0,0,0)),mesh.Vertex((4,0,0)),mesh.Vertex((0,0,4))],[(0,1,2,0)])
        self.assertIsNotNone(mesh.clipped_cell(m,0));self.assertIsNone(mesh.clipped_cell(m,1))
    def test_native_texture_layout_and_gutters(self):
        pixels=bytes([10,20,30,255])*64;encoded=pack.rgba8(pixels,8)
        self.assertEqual(encoded[:2],bytes([255,10]));self.assertEqual(encoded[32:34],bytes([20,30]));self.assertEqual(len(pack.cmpr(pixels,8)),32)
        decoded=pack.decode_cmpr(pack.cmpr(pixels,8),8)
        self.assertLessEqual(max(abs(a-b) for a,b in zip(decoded,pixels)),8)
        w,_,a=pack.tile(1,0,0,0);_,_,b=pack.tile(1,0,1,0)
        for y in range(w):self.assertEqual(a[(y*w+36)*4:(y*w+36)*4+4],b[(y*w+4)*4:(y*w+4)*4+4])
        with self.assertRaises(ValueError):pack.cmpr(pixels,7)
    def test_pack_identity_corruption_and_data_disc(self):
        path=self.root/'world.hcp';builder=pack.Builder();builder.add(pack.COLLISION,b'payload');builder.write(path);original=path.read_bytes();self.assertEqual(pack.inspect(path),1)
        builder.write(path);self.assertEqual(original,path.read_bytes())
        for offset in (4,35,len(original)-32):
            bad=bytearray(original);bad[offset]^=1;path.write_bytes(bad)
            with self.assertRaises(ValueError):pack.inspect(path)
        path.write_bytes(original);iso=self.root/'world.iso';disc.build(iso,path);data=iso.read_bytes()
        self.assertEqual(struct.unpack_from('>I',data,0x1c)[0],0xc2339f3d);self.assertEqual(data[0x8000:0x8000+len(original)],original);self.assertEqual(len(data)%2048,0)
        fst,size,_=struct.unpack_from('>III',data,0x424);self.assertEqual(struct.unpack_from('>III',data,fst+12),(1,0x8000,len(original)))

if __name__=='__main__':unittest.main()
