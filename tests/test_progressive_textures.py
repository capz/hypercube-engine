import tempfile
from pathlib import Path
import struct
import unittest
from progressive_fixtures import build,dnlt
from hypercube import pack,progressive

class ProgressiveTextures(unittest.TestCase):
    def test_roundtrip_deterministic_coarse_first_sparse_pair_and_gutters(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);b=build(root);data=(root/'textures.hcp').read_bytes();b.write(root/'again.hcp')
            self.assertEqual(data,(root/'again.hcp').read_bytes());self.assertEqual(pack.inspect(root/'textures.hcp'),10)
            self.assertEqual([f['levels'][-1]['record'] for f in b.families],[1,2,3,4])
            for f in b.families:
                levels=f['levels'];self.assertEqual([x['record'] for x in levels],sorted([x['record'] for x in levels],reverse=True))
            self.assertEqual(b.families[0]['levels'][1]['logical'],2)
            self.assertEqual(b.families[2]['flags'],4)
            self.assertEqual((b.families[2]['levels'][1]['width'],b.families[2]['levels'][1]['height']),(12,10))
    def test_corruption_rejected_metadata_and_every_payload(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);build(root);original=(root/'textures.hcp').read_bytes();count=struct.unpack_from('<I',original,8)[0]
            for at in (0,4,24,32,36,40,44,64):
                data=bytearray(original);data[at]^=128
                with self.assertRaises((ValueError,UnicodeError)):progressive.inspect(data)
            for i in range(count):
                data=bytearray(original);at=struct.unpack_from('<I',data,64+i*64+8)[0];data[at]^=1
                with self.assertRaises(ValueError):progressive.inspect(data)
            data=bytearray((root/'0.dnlod').read_bytes());data[-1]^=1;(root/'bad.dnlod').write_bytes(data)
            with self.assertRaises(ValueError):pack.Builder().import_texture('bad',root/'bad.dnlod')
    def test_unknown_flags_bad_parents_and_unindexed_levels(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);build(root);original=(root/'textures.hcp').read_bytes();family_offset=struct.unpack_from('<I',original,36)[0];size=struct.unpack_from('<I',original,40)[0]
            for at,value in ((family_offset+4,2),(family_offset+24,1),(family_offset+20,1),(64+36,999),(64+24,999)):
                data=bytearray(original);struct.pack_into('<I',data,at,value);struct.pack_into('<I',data,24,progressive.crc(data[64:family_offset+size]))
                with self.assertRaises(ValueError):progressive.inspect(data)
    def test_dnlt_full_chain_and_reject_missing_sparse_flag(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'test.dnlod';dnlt(path,16,8,[0,1,2,3,4],1);self.assertEqual(len(progressive.read_dnlt(path)['levels']),5)
            dnlt(path,16,8,[0,2,4],1)
            with self.assertRaises(ValueError):progressive.read_dnlt(path)

if __name__=='__main__':unittest.main()
