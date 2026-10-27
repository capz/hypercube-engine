from pathlib import Path
import copy
import sys
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from profiling_validation import Collector, COMMON, FIELDS, FLAGS, NAMES, comparison_rows, check_packet, check_result
from dolphin_validation import SIZE, telemetry


class ProfilingProof(unittest.TestCase):
    def packets(self,case=0):
        common=dict(zip(COMMON,[0x4843,6,300,case,200,384,100,65535,0,FLAGS[case],1000,1000,1,0,500,4]))
        # Mean 40 ms = nine exclusive phases; all units are 10 us.
        a=[34,51,51,4000,100,200,100,600,1600,300,100,700,300,84,1000,500]
        b=[1000]*9+[5000,10000,20,20,2000,2000,0]
        b[4]=3000
        c=[50,250,80,30,0,0,100,200,400,200,300,1000,10000,200,9000,0]
        if case in (1,2):
            common['selectedPermille']=0;b[12]=0;c[6]=0;c[9]=c[10]=0
        if case==3:b[13]=b[14]=0
        if case in (9,10,15):b[15]=10;c[15]=100
        return [dict(common,page=p,**{f'word{i}':v for i,v in enumerate(values)}) for p,values in enumerate((a,b,c))]

    def result(self,case=0):
        collector=Collector()
        for p in self.packets(case):collector.observe(p)
        return collector.result(case)

    def test_completed_pages_can_arrive_out_of_order(self):
        c=Collector();pages=self.packets()
        for p in (pages[2],pages[0],pages[2],pages[1]):c.observe(p)
        r=c.result(0)
        self.assertEqual(r['phaseMeanUs']['present'],16000)
        self.assertEqual(r['frameMeanUs'],40000)
        self.assertEqual(r['decodeCalls'],20)
        self.assertFalse(r['isolation'])

    def test_incomplete_or_mixed_snapshots_are_rejected(self):
        c=Collector();pages=self.packets();c.observe(pages[0])
        with self.assertRaises(AssertionError):c.result(0)
        for field,value in [('budgetKiB',768),('samples',201),('frame',301)]:
            with self.subTest(field=field):
                changed=dict(pages[1]);changed[field]=value
                with self.assertRaises(AssertionError):c.observe(changed)

    def test_live_pages_do_not_complete_a_case(self):
        c=Collector()
        for p in self.packets():p['completedMask']=0;c.observe(p)
        with self.assertRaises(AssertionError):c.result(0)

    def test_exclusive_accounting_catches_double_counting_and_missing_time(self):
        for adjustment in (-1000,1000):
            r=self.result();r['phaseMeanUs']['decode']+=adjustment
            with self.assertRaises(AssertionError):check_result(r)

    def test_quality_and_isolation_invariants(self):
        for case in range(len(NAMES)):check_result(self.result(case))
        invalid=[('selectedMean',5),('detailFrames',201),('readObservations',0)]
        for field,value in invalid:
            r=self.result();r[field]=value
            with self.assertRaises(AssertionError):check_result(r)
        r=self.result(2);r['decodeCalls']=1
        with self.assertRaises(AssertionError):check_result(r)
        r=self.result(3);r['drawsMean']=1
        with self.assertRaises(AssertionError):check_result(r)

    def test_wrong_case_flags_audio_or_failures_are_rejected(self):
        for field,value in [('flags',8),('audioActive',0),('failures',1),('page',3),('selectedPermille',1001)]:
            p=self.packets()[0];p[field]=value
            with self.assertRaises(AssertionError):check_packet(p)

    def test_protocol_v6_raster_decode(self):
        expected=self.packets()[1];rgb=bytearray(SIZE)
        for field,name in enumerate(FIELDS):
            for bit in range(16):
                index=field*16+bit;at=((450+index//64*4)*640+index%64*10+5)*3
                rgb[at:at+3]=bytes([240 if expected[name]&(1<<bit) else 16])*3
        self.assertEqual(telemetry(rgb,fields=FIELDS,version=6),expected)
        with self.assertRaises(ValueError):telemetry(rgb)

    def test_optimization_rows_preserve_work_flags_and_report_tradeoffs(self):
        rows=[self.result(c) for c in range(len(NAMES))]
        for c in range(6,len(NAMES)):self.assertFalse(rows[c]['isolation'])
        rows[6]['phaseMeanUs']['prepare']-=1000
        rows[6]['selectedPermille']-=100
        comparison=comparison_rows(rows)[0]
        self.assertEqual(comparison['prepareChangeUs'],-1000)
        self.assertEqual(comparison['selectedChangePermille'],-100)

    def test_plan_optimization_must_actually_hit_and_build(self):
        for field in ('demandPlanHits','demandPlanBuilds'):
            r=self.result(9);r[field]=0
            with self.assertRaises(AssertionError):check_result(r)

    def test_sdk_cases_are_independent_and_preserve_content(self):
        self.assertEqual(FLAGS[11:16],(1024,256,512,1792,2032))
        rows=[self.result(c) for c in range(len(NAMES))]
        for case in range(11,16):
            self.assertFalse(rows[case]['isolation'])
            self.assertEqual(FLAGS[case]&15,0)
        self.assertEqual([r['name'] for r in comparison_rows(rows)][-5:],list(NAMES[11:16]))
        collector=Collector()
        for page in self.packets(15):collector.observe(page)
        self.assertEqual(collector.result(15)['completedMask'],65535)
