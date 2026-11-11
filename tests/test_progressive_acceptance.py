import copy
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
import unittest
import tempfile
from progressive_acceptance import CHECKS,STRESS,TIERS,validate_run,hardware_gate,compare
from unittest.mock import patch

def measured(platform='Dolphin',storage='sd2sp2',strategy='fine-pages',run_id='one'):
    # Contract data only: deliberately not saved as measured performance evidence.
    memory={k:32 for k in TIERS};memory.update(aramBytes=64,physicalBytes=192)
    return dict(schema=1,platform=platform,storage=storage,strategy=strategy,runId=run_id,console='test fixture only',
        contentSha256='a'*64,sceneSha256='b'*64,cameraTraceSha256='c'*64,shaderSha256='d'*64,samplerSha256='e'*64,dolSha256='f'*64,packSha256='1'*64,sdkRevision='2'*40,
        width=2,height=2,format='RGBA8',texelsPerWorldUnit=128,physicalCeilingBytes=256,captureFrame=120,measuredStartFrame=0,
        checks={k:True for k in CHECKS},stressCases=list(STRESS),frames=[dict(frame=i,frameUs=16000,audioActive=True,memory=memory) for i in range(300)],
        finalMemory={k:0 for k in (*TIERS,'aramBytes','physicalBytes')},coarseReadyUs=[100]*4,detailReadyUs=[200]*4,storageBytes=1024,aramReadBytes=256,draws=1000,textureBinds=1000,cancellations=1,checksumFailures=1,pressureDeferrals=1)

class ProgressiveAcceptance(unittest.TestCase):
    def test_accounting_correctness_and_hardware_are_required(self):
        self.assertEqual(validate_run(measured())['frame99Us'],16000)
        with self.assertRaises(ValueError):validate_run(measured(),hardware=True)
        for mutate in (lambda r:r['finalMemory'].update(uploadBytes=32),lambda r:r['checks'].update(cancelNeverPublished=False),lambda r:r.update(physicalCeilingBytes=100),lambda r:r['frames'][0].update(audioActive=False),lambda r:r.update(frames=[]),lambda r:r.update(checksumFailures=0)):
            run=copy.deepcopy(measured());mutate(run)
            with self.assertRaises(ValueError):validate_run(run)
    def test_repeated_sd_and_disc_both_strategies_and_thresholds(self):
        runs=[measured('GameCube',storage,strategy,storage+strategy+str(i)) for storage in ('sd2sp2','disc') for strategy in ('fine-pages','cell-atlas') for i in range(3)]
        self.assertTrue(hardware_gate(runs,33334,1000,1000)['passed'])
        with self.assertRaises(ValueError):hardware_gate(runs[:-1],33334,1000,1000)
        with self.assertRaises(ValueError):hardware_gate(runs,15000,1000,1000)
        runs[0]['platform']='Dolphin'
        with self.assertRaises(ValueError):hardware_gate(runs,33334,1000,1000)
    def test_actual_capture_decode_and_quality_report(self):
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp)/'capture.ppm';path.write_bytes(b'P6\n2 2\n255\n'+bytes([30,60,90])*4)
            report=compare(measured(),measured(strategy='cell-atlas'),path,path,path)
            self.assertFalse(report['hardwarePerformanceClaim']);self.assertEqual(report['rmse'],dict(pages=0,atlas=0))

    def test_matched_quality_identity_admission(self):
        pages=measured();atlas=measured(strategy='cell-atlas')
        # RGB decoding is separate from contract validation; no fake report is emitted.
        with patch('progressive_acceptance.rgb',return_value=bytes(12)):
            pages['physicalCeilingBytes']=512
            with self.assertRaises(ValueError):compare(pages,atlas,'ref','pages','atlas')
            pages['physicalCeilingBytes']=256;atlas['captureFrame']=121
            with self.assertRaises(ValueError):compare(pages,atlas,'ref','pages','atlas')
        with patch('progressive_acceptance.rgb',side_effect=[bytes(12),bytes([255])*12,bytes(12)]):
            atlas['captureFrame']=120
            with self.assertRaises(ValueError):compare(pages,atlas,'ref','pages','atlas')

if __name__=='__main__':unittest.main()
