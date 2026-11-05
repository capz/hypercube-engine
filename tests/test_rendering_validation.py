import importlib.util
from pathlib import Path
import sys
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
import rendering_validation as rendering

class RenderingValidationTests(unittest.TestCase):
    def rows(self):
        return [dict(frame=f,phase=f//180%3,uploads=1 if f<120 else 2,batchCalls=2,apiCalls=3,instances=16,culled=0,triangles=576,gxDraws=17,rebuilds=int(f==120),growths=0) for f in range(30,571,30)]
    def test_complete_proof(self):
        proof=rendering.validate_rows(self.rows())
        self.assertEqual(proof['dirtyUploads'],2)
        self.assertEqual(proof['phases'],[0,1,2])
        self.assertEqual(proof['maxApiCalls'],3)
        self.assertEqual(proof['maxGxDraws'],17)
    def test_rejects_missing_cases(self):
        with self.assertRaises(AssertionError):rendering.validate_rows(self.rows()[:10])
    def test_rejects_repeated_uploads_and_growth(self):
        for key,value in [('uploads',3),('growths',1),('apiCalls',5),('instances',12)]:
            rows=self.rows();rows[5][key]=value
            with self.assertRaises(AssertionError):rendering.validate_rows(rows)
    def test_frozen_telemetry_is_not_success(self):
        rows=self.rows();rows+= [dict(rows[-1]) for _ in range(10)]
        with self.assertRaises(AssertionError):rendering.validate_rows(rows)
    def test_no_guest_execution_is_not_success(self):
        with self.assertRaises(AssertionError):rendering.validate_rows([])
