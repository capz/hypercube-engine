import contextlib
import io
import itertools
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import ci_changes as ci


class Routing(unittest.TestCase):
    def test_documentation_only_and_executable_files_under_docs(self):
        self.assertEqual(ci.select(['README.md', 'docs/streaming-validation.md',
                                    'docs/measurements/scene.json']), set())
        self.assertEqual(ci.select(['docs/check.py']), ci.ALL)

    def test_rendering_does_not_schedule_world_comparisons(self):
        for name in ci.RENDERING_FILES:
            self.assertEqual(ci.select(['src/Hypercube.GameCube/' + name]),
                             {'host', 'rendering', 'packages'})
        self.assertEqual(ci.select(['samples/Rendering/Program.cs']),
                         {'host', 'rendering', 'packages'})

    def test_shared_state_sdk_and_new_code_are_conservative(self):
        for path in ('.sdk-revision', 'Directory.Build.props', 'eng/Package.props',
                     'src/Hypercube.Core/ResourceScheduler.cs',
                     'src/Hypercube.GameCube/NewHelper.cs', 'tests/NewTests/Program.cs',
                     '.github/workflows/validation.yml', '.github/actions/new/action.yml',
                     'tools/ci_changes.py', 'tests/test_ci_changes.py', 'unrecognized.file'):
            with self.subTest(path=path):
                self.assertEqual(ci.select([path]), ci.ALL)

    def test_world_and_family_dependencies(self):
        self.assertEqual(ci.select(['src/Hypercube.GameCube/WorldStreamer.cs']),
                         ci.WORLD | {'packages'})
        self.assertEqual(ci.select(['src/Hypercube.Core/TextureLodPolicy.cs']),
                         {'host', 'families', 'packages'})
        self.assertEqual(ci.select(['tools/hypercube/pack.py']), ci.WORLD | {'families'})
        self.assertEqual(ci.select(['tools/build_texture_pack.py']), {'host', 'families'})

    def test_decoder_import_dependencies_and_specific_suites(self):
        self.assertEqual(ci.select(['tools/dolphin_validation.py']), ci.WORLD | {'rendering'})
        self.assertEqual(ci.select(['tools/profiling_validation.py']), ci.PROFILING)
        self.assertEqual(ci.select(['tools/performance_validation.py']), {'host', 'performance'})
        self.assertEqual(ci.select(['tools/rendering_validation.py']), {'host', 'rendering'})
        self.assertEqual(ci.select(['tests/test_profiling_validation.py']), {'host'})
        self.assertEqual(ci.select(['eng/pack.py', 'LICENSE']), {'packages'})

    def test_union_and_native_lanes_always_have_host_prerequisite(self):
        self.assertEqual(ci.select(['docs/a.md', 'tools/rendering_validation.py',
                                    'tools/performance_validation.py']),
                         {'host', 'rendering', 'performance'})
        paths = list(ci.RULES) + ['src/Hypercube.GameCube/' + n for n in ci.RENDERING_FILES | ci.WORLD_FILES]
        for path in paths:
            selected = ci.select([path])
            if selected & ({'scene', 'rendering'} | ci.SUITES):
                self.assertIn('host', selected, path)

    def test_all_tracked_runtime_paths_are_intentionally_classified(self):
        # Unknown paths still select everything; this catches accidental exclusions
        # when adding suffix/prefix shortcuts to the classifier.
        for directory in ('src', 'samples', 'tools', 'eng'):
            for path in Path(directory).rglob('*'):
                if path.is_file() and not any(p in path.parts for p in ('bin', 'obj', '__pycache__')):
                    self.assertTrue(ci.select([path.as_posix()]), path)


class DiffSelection(unittest.TestCase):
    def git(self, *args):
        return subprocess.check_output(['git', *args], cwd=self.root, text=True).strip()

    def commit(self, path, text):
        file = self.root / path
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text(text)
        self.git('add', '-A')
        self.git('commit', '-qm', 'fixture')
        return self.git('rev-parse', 'HEAD')

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.git('init', '-q')
        self.git('config', 'user.name', 'CI Test')
        self.git('config', 'user.email', 'ci@example.invalid')
        self.base = self.commit('README.md', 'base')

    def event(self, head, base=None):
        return dict(pull_request=dict(base=dict(sha=base or self.base), head=dict(sha=head)))

    def test_entire_pr_not_only_last_commit(self):
        self.commit('src/Hypercube.GameCube/WorldStreamer.cs', 'code')
        head = self.commit('docs/a.md', 'last commit is docs')
        selected, _ = ci.selection('pull_request', self.event(head), self.root)
        self.assertEqual(selected, ci.plan(ci.WORLD | {'packages'}))

    def test_merge_base_excludes_unrelated_target_branch_changes(self):
        self.git('checkout', '-qb', 'feature')
        head = self.commit('docs/a.md', 'documentation')
        self.git('checkout', '-qb', 'target', self.base)
        target = self.commit('src/Hypercube.Core/ResourceScheduler.cs', 'unrelated target change')
        selected, _ = ci.selection('pull_request', self.event(head, target), self.root)
        self.assertEqual(selected, ci.plan(set()))

    def test_deletions_renames_and_whitespace_are_preserved(self):
        self.base = self.commit('src/Hypercube.GameCube/WorldStreamer.cs', 'code')
        (self.root / 'docs').mkdir()
        self.git('mv', 'src/Hypercube.GameCube/WorldStreamer.cs', 'docs/moved.md')
        self.git('commit', '-qm', 'rename')
        head = self.commit('docs/space and\nnewline.md', 'documentation')
        paths = ci.changed_paths(self.event(head), self.root)
        self.assertIn('src/Hypercube.GameCube/WorldStreamer.cs', paths)
        self.assertIn('docs/moved.md', paths)
        self.assertIn('docs/space and\nnewline.md', paths)
        self.assertEqual(ci.selection('pull_request', self.event(head), self.root)[0],
                         ci.plan(ci.WORLD | {'packages'}))

    def test_invalid_missing_and_unavailable_history_runs_full(self):
        for event in ({}, [], self.event('bad'), self.event('f' * 40)):
            with self.subTest(event=event):
                self.assertEqual(ci.selection('pull_request', event, self.root)[0], ci.plan(ci.ALL))
        for event_name in ('push', 'workflow_dispatch', 'schedule', 'merge_group', ''):
            self.assertEqual(ci.selection(event_name, {}, self.root)[0], ci.plan(ci.ALL))


class RequiredGate(unittest.TestCase):
    def needs(self, selected):
        p = ci.plan(selected)
        return dict(changes=dict(result='success', outputs=dict(plan=json.dumps(p))),
                    host=dict(result='success' if p['host'] else 'skipped'),
                    **{'baseline-validation': dict(result='success' if p['scene'] else 'skipped'),
                       'rendering': dict(result='success' if p['rendering'] else 'skipped'),
                       'release-comparisons': dict(result='success' if p['comparisons'] else 'skipped')})

    def test_legitimate_skips_and_success(self):
        for selected in (set(), ci.ALL, {'host'}, {'host', 'rendering'}, {'packages'}):
            ci.check_results(self.needs(selected))

    def test_selected_failures_cancellation_and_skips_cannot_pass(self):
        for job, status in itertools.product(('host', 'baseline-validation', 'rendering', 'release-comparisons'),
                                             ('failure', 'cancelled', 'skipped', None)):
            needs = self.needs(ci.ALL)
            needs[job]['result'] = status
            with self.subTest(job=job, status=status), self.assertRaises(ValueError):
                ci.check_results(needs)
        for status in ('failure', 'cancelled', 'skipped', None):
            needs = self.needs(set())
            needs['changes']['result'] = status
            with self.assertRaises(ValueError):
                ci.check_results(needs)

    def test_unexpected_execution_and_missing_result_fail(self):
        needs = self.needs(set());needs['rendering']['result'] = 'success'
        with self.assertRaises(ValueError):ci.check_results(needs)
        needs = self.needs(ci.ALL);del needs['host']
        with self.assertRaises(ValueError):ci.check_results(needs)

    def test_cli_emits_valid_empty_matrix_and_single_line_outputs(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder);event = root / 'event.json';output = root / 'output';summary = root / 'summary'
            event.write_text('{}')
            env = dict(os.environ, GITHUB_EVENT_NAME='pull_request', GITHUB_EVENT_PATH=str(event),
                       GITHUB_OUTPUT=str(output), GITHUB_STEP_SUMMARY=str(summary))
            with contextlib.redirect_stdout(io.StringIO()), patch.dict(os.environ, env), patch.object(sys, 'argv', ['ci_changes.py']), patch.object(ci, 'selection', return_value=(ci.plan(set()), 'Docs only')):
                ci.main()
            values = dict(line.split('=', 1) for line in output.read_text().splitlines())
            self.assertEqual(values['compare'], 'false')
            self.assertEqual(json.loads(values['comparisons'])['include'][0]['suite'], 'not-selected')
            self.assertEqual(json.loads(values['plan'])['comparisons'], [])
            self.assertEqual(set(values), set(ci.FLAGS) | {'compare', 'comparisons', 'plan'})


if __name__ == '__main__':
    unittest.main()
