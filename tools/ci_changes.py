#!/usr/bin/env python3
"""Conservative CI selection from the whole PR diff; unknown changes run everything."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess

FLAGS = ('host', 'families', 'rendering', 'scene', 'packages')
COMPARISONS = (
    dict(suite='performance', build_flag='PERFORMANCE_SUITE', seconds=240),
    dict(suite='profiling', build_flag='PROFILING_SUITE', seconds=360),
    dict(suite='sdk-followup', build_flag='SDK_FOLLOWUP_SUITE', seconds=360),
    dict(suite='texture-followup', build_flag='TEXTURE_FOLLOWUP_SUITE', seconds=480),
)
SUITES = {c['suite'] for c in COMPARISONS}
ALL = set(FLAGS) | SUITES
WORLD = {'host', 'scene'} | SUITES
PROFILING = {'host', 'profiling', 'sdk-followup', 'texture-followup'}
# Explicit names make new runtime files conservative until their consumers are reviewed.
WORLD_FILES = {'AramCache.cs', 'PackedWorld.cs', 'TexturePool.cs', 'WorldStreamer.cs'}
RENDERING_FILES = {'CloudShadowLayer.cs', 'DynamicMeshBuffer.cs', 'IndexedMeshCache.cs',
                   'MeshBatch.cs', 'OrderedMeshPasses.cs', 'RenderTelemetry.cs',
                   'ScrollingTextureLayer.cs', 'SharedGraphicsResources.cs'}
FAMILY_FILES = {'TextureFamilies.cs', 'TextureLodPolicy.cs'}
HOST_PROJECTS = ('Hypercube.Tests', 'Hypercube.GameCube.Tests', 'OrderedMeshPasses.Tests')
PYTHON_TESTS = {'test_assets.py', 'test_performance_validation.py', 'test_profiling_validation.py',
                'test_progressive_textures.py', 'test_rendering_validation.py', 'test_ci_changes.py'}
RULES = {
    'tools/build_assets.py': WORLD | {'families'},
    'tools/hypercube/pack.py': WORLD | {'families'},
    'tools/hypercube/mesh.py': WORLD,
    'tools/hypercube/disc.py': WORLD,
    'tools/hypercube/progressive.py': {'host', 'families'},
    'tools/hypercube/__init__.py': WORLD | {'families'},
    'tools/build_texture_pack.py': {'host', 'families'},
    'tools/build_validation.sh': WORLD,
    'tools/build_rendering.sh': {'host', 'rendering'},
    # Rendering and performance/profiling tools import the common decoder/helpers.
    'tools/dolphin_validation.py': WORLD | {'rendering'},
    'tools/rendering_validation.py': {'host', 'rendering'},
    'tools/performance_validation.py': {'host', 'performance'},
    'tools/profiling_validation.py': PROFILING,
    'tests/progressive_fixtures.py': {'host', 'families'},
    'tests/nuget_consumer.py': {'packages'},
    'eng/pack.py': {'packages'},
    'eng/package-readme.md': {'packages'},
    'LICENSE': {'packages'},
}


def select(paths):
    selected = set()
    for path in paths:
        # Config/code extensions always take priority over a documentation extension.
        if path in RULES:
            selected.update(RULES[path])
        elif path.startswith('.github/') or path in {
            '.sdk-revision', 'Directory.Build.props', 'Directory.Build.targets',
            'global.json', 'NuGet.Config', 'nuget.config', 'Hypercube.sln',
            'tools/ci_changes.py', 'tools/test_host.py', 'tests/test_ci_changes.py', 'eng/Package.props',
        } or path.endswith(('.csproj', '.props', '.targets', '.sln', '.slnx')):
            selected.update(ALL)
        elif path.startswith('src/Hypercube.Core/'):
            name = path.removeprefix('src/Hypercube.Core/')
            selected.update({'host', 'families', 'packages'} if name in FAMILY_FILES else ALL)
        elif path.startswith('src/Hypercube.GameCube/'):
            name = path.removeprefix('src/Hypercube.GameCube/')
            if name in WORLD_FILES:
                selected.update(WORLD | {'packages'})
            elif name in RENDERING_FILES:
                selected.update({'host', 'rendering', 'packages'})
            else:
                selected.update(ALL)
        elif path.startswith('samples/Validation/'):
            selected.update(WORLD)
        elif path.startswith('samples/Rendering/'):
            # The relocated NuGet consumer compiles this sample too.
            selected.update({'host', 'rendering', 'packages'})
        elif path.startswith('tests/TextureFamilies.Tests/'):
            selected.update({'host', 'families'})
        elif any(path.startswith('tests/' + name + '/') for name in HOST_PROJECTS):
            selected.add('host')
        elif path.startswith('tests/') and path.removeprefix('tests/') in PYTHON_TESTS:
            selected.add('host')
        elif path.startswith('docs/') or path in {'README.md', 'AGENTS.md', 'SECURITY.md'}:
            # Executable/configuration files under docs are not silently exempt.
            if path.endswith(('.md', '.txt', '.png', '.jpg', '.jpeg', '.svg')) or (
                path.startswith('docs/measurements/') and path.endswith(('.json', '.csv'))
            ):
                continue
            selected.update(ALL)
        else:
            selected.update(ALL)
    return selected


def plan(selected):
    return {**{key: key in selected for key in FLAGS},
            'comparisons': [dict(c) for c in COMPARISONS if c['suite'] in selected]}


def changed_paths(event, cwd=None):
    pr = event['pull_request']
    base, head = pr['base']['sha'], pr['head']['sha']
    if not all(isinstance(sha, str) and re.fullmatch('[0-9a-fA-F]{40,64}', sha) for sha in (base, head)):
        raise ValueError('Invalid comparison revision')
    # --no-renames includes BOTH sides of a rename; NUL preserves whitespace/newlines.
    raw = subprocess.check_output(['git', 'diff', '--name-only', '--no-renames', '-z',
                                   base + '...' + head, '--'], cwd=cwd, stderr=subprocess.PIPE)
    return [path.decode('utf-8') for path in raw.split(b'\0') if path]


def selection(event_name, event, cwd=None):
    if event_name != 'pull_request':
        return plan(ALL), 'Full validation for main, tags and manual runs'
    try:
        paths = changed_paths(event, cwd)
        return plan(select(paths)), f'Selected from {len(paths)} changed PR paths'
    except (KeyError, TypeError, ValueError, OSError, subprocess.SubprocessError) as error:
        return plan(ALL), f'Diff unavailable; full validation ({type(error).__name__})'


def check_results(needs):
    """Skipped is valid only for an unselected lane, never for a selected one."""
    if needs.get('changes', {}).get('result') != 'success':
        raise ValueError('CI selection did not succeed')
    selected = json.loads(needs['changes']['outputs']['plan'])
    expected = {'host': selected['host'], 'baseline-validation': selected['scene'],
                'rendering': selected['rendering'], 'release-comparisons': bool(selected['comparisons'])}
    for job, required in expected.items():
        actual = needs.get(job, {}).get('result')
        if actual != ('success' if required else 'skipped'):
            raise ValueError(f'{job}: expected {"success" if required else "skipped"}, got {actual}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check-results', action='store_true')
    args = parser.parse_args()
    if args.check_results:
        check_results(json.loads(os.environ['CI_NEEDS']))
        print('Every selected validation lane passed; unselected lanes were skipped.')
        return
    try:
        event = json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text())
        result, reason = selection(os.environ.get('GITHUB_EVENT_NAME', ''), event)
    except (KeyError, ValueError, OSError):
        result, reason = plan(ALL), 'Event unavailable; full validation'
    outputs = {key: str(result[key]).lower() for key in FLAGS}
    outputs['compare'] = str(bool(result['comparisons'])).lower()
    # A sentinel keeps the matrix valid even when a skipped job is expanded by tooling.
    matrix = result['comparisons'] or [dict(suite='not-selected', build_flag='CI_NOT_SELECTED', seconds=0)]
    outputs['comparisons'] = json.dumps({'include': matrix}, separators=(',', ':'))
    outputs['plan'] = json.dumps(result, separators=(',', ':'))
    with Path(os.environ['GITHUB_OUTPUT']).open('a') as output:
        for name, value in outputs.items():
            output.write(f'{name}={value}\n')
    summary = reason + '\n\n' + '\n'.join(f'- {key}: {value}' for key, value in outputs.items()
                                          if key not in {'plan', 'comparisons'})
    summary += '\n- comparisons: ' + ', '.join(c['suite'] for c in result['comparisons']) + '\n'
    if os.environ.get('GITHUB_STEP_SUMMARY'):
        with Path(os.environ['GITHUB_STEP_SUMMARY']).open('a') as output:
            output.write(summary)
    print(summary)


if __name__ == '__main__':
    main()
