#!/usr/bin/env python3
"""Regenerate all supported Python locks. Run inside the pinned tools environment."""
from pathlib import Path
import subprocess
import sys
import tempfile

root = Path(__file__).resolve().parents[1]
engine = Path('src/AlgoTrading.PythonEngine')

def compile_lock(source, destination, *options):
    subprocess.run([sys.executable, '-m', 'uv', 'pip', 'compile', str(source), '--upgrade',
        '--python-version', '3.12', '--generate-hashes', '--no-header', '--output-file', str(destination), *options],
        cwd=root, check=True)

for profile in ['bootstrap', 'tools']:
    compile_lock(engine / f'requirements-{profile}.in', engine / f'requirements-{profile}.txt', '--universal')
compile_lock(engine / 'requirements-runtime.in', engine / 'requirements.txt',
    '--universal', '--find-links', 'vendor/python', '--emit-find-links')
with tempfile.TemporaryDirectory() as folder:
    constraints = Path(folder) / 'runtime.txt'
    constraints.write_text((root / engine / 'requirements.txt').read_text().replace('--find-links vendor/python\n', ''))
    target = engine / 'requirements-research-linux.txt'
    compile_lock(engine / 'requirements-research-linux.in', target, '--constraint', str(constraints),
        '--find-links', 'vendor/python', '--emit-find-links', '--python-platform', 'x86_64-unknown-linux-gnu')
    path = root / target
    path.write_text(path.read_text().replace(str(constraints), str(engine / 'requirements.txt')))
