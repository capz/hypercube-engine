import copy
from pathlib import Path
import sys
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from progressive_validation import FIELDS,validate_rows

class ProgressiveAcceptance(unittest.TestCase):
    def rows(self):
        row=dict.fromkeys(FIELDS,0)
        row.update(magic=0x4854,phase=10,coarseMask=15,crcFailures=1,failures=2,stale=1,deferred=1,cancelled=1,aramHits=1,audioActive=1)
        return [dict(row,frame=600+i) for i in range(20)]
    def test_complete_zero_copy_trim_and_live_audio(self):
        proof=validate_rows(self.rows());self.assertFalse(proof['hardwarePerformanceClaim']);self.assertEqual(proof['platform'],'Dolphin')
    def test_each_residency_tier_must_drain(self):
        for key in ('active32','upload32','retired32','aram32','readBufferKiB','poolBytes'):
            rows=self.rows();rows[-1][key]=1
            with self.subTest(tier=key),self.assertRaises(AssertionError):validate_rows(rows)
    def test_fault_paths_and_progress_cannot_be_skipped(self):
        for key in ('crcFailures','failures','stale','deferred','cancelled','aramHits','coarseMask'):
            rows=self.rows();rows[-1][key]=0
            with self.subTest(gate=key),self.assertRaises(AssertionError):validate_rows(rows)
        rows=self.rows()
        for row in rows:row['frame']=1
        with self.assertRaises(AssertionError):validate_rows(rows)
    def test_audio_cannot_stop_mid_stream(self):
        rows=self.rows();rows[2]['audioActive']=0
        with self.assertRaises(AssertionError):validate_rows(rows)
if __name__=='__main__':unittest.main()
