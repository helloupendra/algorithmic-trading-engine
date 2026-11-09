#!/usr/bin/env python3
"""Audit a selected environment, including the reviewed upstream FYERS source version.

Run with the isolated tools environment; --python is the environment to inspect.
The local wheel's metadata/resource-only patch is reviewed in vendor/python/README.md.
"""
import argparse
import json
from pathlib import Path
import subprocess
import sys
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('--python', required=True)
parser.add_argument('--output', required=True)
parser.add_argument('--format', default='json', choices=['json', 'cyclonedx-json'])
args = parser.parse_args()
packages = json.loads(subprocess.check_output([args.python, '-m', 'pip', 'list', '--format=json'], text=True))
with tempfile.TemporaryDirectory() as folder:
    inventory = Path(folder) / 'inventory.txt'
    requirements = []
    for package in packages:
        name, version = package['name'], package['version']
        if name.lower().replace('_', '-') == 'fyers-apiv3' and version == '3.2.1+openfno.1':
            version = '3.2.1'
            print('Auditing local FYERS wheel against upstream 3.2.1; see vendor/python/README.md.', flush=True)
        requirements.append(f'{name}=={version}')
    inventory.write_text('\n'.join(sorted(requirements)) + '\n')
    # Inventory is already fully resolved. Do not install or resolve dependencies while auditing it.
    sys.exit(subprocess.call([sys.executable, '-m', 'pip_audit', '--disable-pip', '--no-deps',
        '-r', str(inventory), '--format', args.format, '--output', args.output]))
