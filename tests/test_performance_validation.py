from pathlib import Path
import sys
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from dolphin_validation import SIZE, telemetry
from performance_validation import FIELDS, check_state, comparisons


class PerformanceProof(unittest.TestCase):
    def state(self):
        s=dict.fromkeys(FIELDS,0)
        s.update(magic=0x4843,version=3,scenario=0,budgetKiB=384,dynamicKiB=200,
                 frame50ms=17,frame95ms=34,frame99ms=51,frameMaxMs=84,
                 pumpMean10us=25,pumpMax10us=100,frameMean10us=2000,audioActive=1,
                 caseFrames=300,detailFrames=200,storageKiB=100,blocked=5)
        return s

    def test_protocol_v3_decodes_and_rejects_wrong_suite(self):
        s=self.state(); rgb=bytearray(SIZE)
        for field,name in enumerate(FIELDS):
            for bit in range(16):
                index=field*16+bit
                at=((450+index//64*4)*640+index%64*10+5)*3
                shade=240 if s[name] & (1<<bit) else 16
                rgb[at:at+3]=bytes([shade])*3
        self.assertEqual(telemetry(rgb,fields=FIELDS,version=3),s)
        with self.assertRaises(ValueError):telemetry(rgb)

    def test_checks_reject_false_performance_wins(self):
        check_state(self.state())
        for field,value in [('dynamicKiB',385),('audioActive',0),('frame95ms',60),
                            ('pumpMean10us',101),('failures',1),('scenario',9)]:
            with self.subTest(field=field):
                s=self.state();s[field]=value
                with self.assertRaises(AssertionError):check_state(s)

    def test_faults_are_allowed_only_in_stress_cases(self):
        for case in (7,8):
            s=self.state();s.update(scenario=case,failures=5);check_state(s)

    def test_comparison_reports_costs_and_quality_without_declaring_winner(self):
        completed={i:self.state() for i in range(7)}
        completed[1]=dict(self.state(),frame99ms=34,storageKiB=150,pumpMax10us=50)
        result=comparisons(completed)[0]
        self.assertEqual(result['frame99ChangeMs'],-17)
        self.assertEqual(result['storageChangeKiB'],50)
        self.assertEqual(result['pumpMaxChangeUs'],-500)
        self.assertAlmostEqual(result['detailFrameFraction'],2/3)
