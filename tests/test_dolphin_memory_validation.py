from pathlib import Path
import sys
import unittest

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from dolphin_validation import native_residency_baseline


class NativeResidencyValidation(unittest.TestCase):
    @staticmethod
    def state(case,extra=0):
        return dict(scenario=case,nativeKiB=544+200+extra,dynamicKiB=200)

    def test_lazy_bounded_cold_source_and_transition_release(self):
        baseline=native_residency_baseline(self.state(0),None)
        self.assertEqual(baseline,544)
        for case in (6,7):
            for extra in (0,16):
                self.assertEqual(native_residency_baseline(self.state(case,extra),baseline),baseline)
        self.assertEqual(native_residency_baseline(self.state(8),baseline),baseline)

    def test_native_leaks_cannot_hide_in_cold_source_allowance(self):
        for case in (6,7):
            for extra in (8,24,32,-16):
                with self.subTest(case=case,extra=extra),self.assertRaises(AssertionError):
                    native_residency_baseline(self.state(case,extra),544)
        for case in (0,1,2,3,4,5,8,9,10):
            with self.subTest(case=case),self.assertRaises(AssertionError):
                native_residency_baseline(self.state(case,16),544)

    def test_independent_kib_rounding_does_not_reject_valid_states(self):
        for extra in (-1,0,1):
            self.assertEqual(native_residency_baseline(self.state(0,extra),544),544)
            self.assertEqual(native_residency_baseline(self.state(6,16+extra),544),544)


if __name__=='__main__':unittest.main()
