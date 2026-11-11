import copy
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest
from progressive_fixtures import dnlt
from model_sidecar_fixtures import build
from hypercube.model_bindings import read_sidecar,generate
from hypercube import pack
from build_mip_tail import build as build_tail

class ProgressivePolish(unittest.TestCase):
    def test_model_density_and_shared_content_generated_materials(self):
        with tempfile.TemporaryDirectory() as tmp:
            path=build(tmp);doc=read_sidecar(path)
            self.assertEqual([(m['worldPerU'],m['worldPerV']) for m in doc['materials']],[(0,0),(2,3),(2,3)])
            code=generate([doc]);self.assertIn('TextureAddressMode.Mirror',code);self.assertIn('TextureFilter.Nearest',code)
            self.assertIn('stream.Get(',code);self.assertIn(',1,2f,3f)',code);self.assertIn(',2,2f,3f)',code)
            root=Path(tmp);script=Path(__file__).resolve().parents[1]/'tools/build_texture_pack.py'
            subprocess.run([sys.executable,str(script),'--model-sidecar',str(path),'--model-sidecar',str(path),'--bindings',str(root/'a.cs'),'--output',str(root/'a.hcp')],check=True)
            self.assertEqual(pack.inspect(root/'a.hcp'),5)
            self.assertEqual(json.loads((root/'a.manifest.json').read_text())['families'][0]['name'],doc['textures'][0]['family'])
    def test_model_rejects_wrong_hash_paths_mappings_and_nonfinite(self):
        with tempfile.TemporaryDirectory() as tmp:
            path=build(tmp);original=json.loads(path.read_text())
            for mutate in (lambda d:d.update(model='../escape'),lambda d:d.update(modelSha256='0'*64),lambda d:d['textures'][0].update(file='/tmp/no'),lambda d:d['textures'][0].update(sha256='0'*64),lambda d:d['materials'][1].update(primaryTexture=99),lambda d:d['materials'][1].update(color=[float('nan')]*4),lambda d:d['materials'][1].update(combiner=0)):
                doc=copy.deepcopy(original);mutate(doc);path.write_text(json.dumps(doc))
                with self.assertRaises(ValueError):read_sidecar(path)
            for name in ('a.b','a;','class','a\nB'):
                with self.assertRaises(ValueError):generate([],name)
    def test_tail_complete_rectangular_crc_and_byte_ceiling(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);source=root/'a.dnlod';output=root/'tail.dndtex';dnlt(source,32,16,list(range(6)),1)
            report=build_tail(source,output,1)
            self.assertEqual((report['width'],report['height'],report['levels']),(16,8,5))
            self.assertEqual(len(output.read_bytes()),32+report['nativeBytes'])
            self.assertEqual(struct.unpack_from('<I',output.read_bytes(),24)[0],5)
            with self.assertRaises(ValueError):build_tail(source,output,0,32)
            with self.assertRaises(ValueError):build_tail(source,output,5)
    def test_tail_rejects_sparse_gutters_incomplete_pairs_corruption(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);source=root/'a.dnlod';out=root/'a.dndtex'
            for levels,flags,gutter in (([0,2,4],3,0),([0,1,2,3,4],1,1),([0,1],3,0),([0,1],4,0)):
                dnlt(source,16,8,levels,flags,gutter=gutter,pair_low=(3,2) if flags==4 else None)
                with self.assertRaises(ValueError):build_tail(source,out,0)
            dnlt(source,16,8,list(range(5)),1);bad=bytearray(source.read_bytes());bad[-1]^=1;source.write_bytes(bad)
            with self.assertRaises(ValueError):build_tail(source,out,0)


class MatchedTextureAssets(unittest.TestCase):
    def test_pages_and_atlas_share_all_useful_texels_at_each_authored_level(self):
        from build_texture_comparison import build as comparison
        from hypercube.progressive import read_dnlt
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);report=comparison(root)
            def pixel(level,x,y):
                w=level['width'];at=((y//4)*((w+3)//4)+x//4)*64+((y%4)*4+x%4)*2;p=level['payload']
                return p[at+1],p[at+32],p[at+33],p[at]
            for y in range(4):
                for x in range(6):
                    page=read_dnlt(root/f'fine-pages-{x}-{y}.dnlod');atlas=read_dnlt(root/f'cell-atlas-{x//2}-{y//2}.dnlod')
                    for i in range(3):
                        useful=32>>i
                        for py in range(useful):
                            for px in range(useful):
                                self.assertEqual(pixel(page['levels'][i],4+px,4+py),pixel(atlas['levels'][i],4+(x%2)*useful+px,4+(y%2)*useful+py))
            self.assertEqual(report['strategies'][0]['coarseBytes']>0,True)
            with self.assertRaises(ValueError):comparison(root,physical_ceiling=32)

if __name__=='__main__':unittest.main()
