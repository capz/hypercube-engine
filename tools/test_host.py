#!/usr/bin/env python3
"""Fast SDK-free checks, run once before selected console builds."""
import subprocess


def run(*args):
    print('+', ' '.join(args), flush=True)
    subprocess.run(args, check=True)


def main():
    run('python3', '-m', 'unittest', 'discover', '-s', 'tests', '-p', 'test_*.py')
    run('python3', 'tools/build_assets.py', '--resolution', '48')
    for config in ('Debug', 'Release'):
        run('dotnet', 'run', '--project', 'tests/Hypercube.Tests', '-c', config, '--', 'out/assets/world.hcp')
    run('dotnet', 'run', '--project', 'tests/Hypercube.GameCube.Tests', '-c', 'Release')
    for native in ('true', 'false'):
        run('dotnet', 'run', '--project', 'tests/OrderedMeshPasses.Tests', '-c', 'Release',
            '-p:HypercubeNativePasses=' + native)


if __name__ == '__main__':
    main()
