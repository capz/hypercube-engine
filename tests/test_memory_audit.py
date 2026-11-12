from pathlib import Path
import sys
import struct
import tempfile
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from hypercube import pack
from memory_audit import audit


class MemoryAudit(unittest.TestCase):
    def test_fixture_counts_and_reported_payload_savings(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);pack.build(root/'world.hcp',root/'pack-report.json',resolution=16)
            result=audit(root/'world.hcp')
            count=result['records']
            self.assertFalse(result['hardwareMeasurement'])
            self.assertEqual(result['largestSupportedStreamRecord'],73984)
            self.assertEqual(result['nativeStagingBytes'],dict(before=131072,after=73984,saved=57088))
            self.assertEqual(result['retainedManagedPayload']['saved'],4*count+1566)
            self.assertEqual(result['transientCatalogTailPayload']['saved'],192)
            self.assertEqual(result['idleAramMetadata']['disabled']['saved'],8*(count+1))
            self.assertEqual(result['idleAramMetadata']['enabledAfterFirstStore']['saved'],-4*(count+1))
            self.assertEqual(result['idleAramMetadata']['enabledAfterFirstStore']['after'],12*(count+1))
            self.assertEqual(result['optionalColdWriteStaging']['bufferBytes'],16384)
            self.assertTrue(result['optionalColdWriteStaging']['allocatedLazily'])
            self.assertEqual(result['optionalColdWriteStaging']['sourceAndColdBuffers'],
                             dict(before=131072,after=90368,saved=40704))
            self.assertEqual(result['idleLifecycleArrayPayload']['change'],dict(before=32,after=904,saved=-872))
            self.assertEqual(result['selectedManagedArraySavings']['coldCacheUnused'],12*count+702)
            self.assertEqual(result['selectedManagedArraySavings']['coldCacheActive'],690)
            self.assertEqual(audit(root/'world.hcp',131040)['nativeStagingBytes']['after'],131040)
            self.assertEqual(audit(root/'world.hcp',32)['nativeStagingBytes']['after'],73984)
            self.assertEqual(result,audit(root/'world.hcp'))
            bad=bytearray((root/'world.hcp').read_bytes());payload=struct.unpack_from('<I',bad,32+8)[0];bad[payload]^=1;(root/'bad.hcp').write_bytes(bad)
            with self.assertRaises(ValueError):audit(root/'bad.hcp')
            for cap in (0,31,131041):
                with self.assertRaises(ValueError):audit(root/'world.hcp',cap)

if __name__=='__main__':unittest.main()
