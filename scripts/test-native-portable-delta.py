#!/usr/bin/env python3
"""Exercise only the native patch command against isolated, unpublished packages."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def checked_path(value, exists=True):
    path = Path(value).resolve()
    if not path.is_relative_to(ROOT) or path == ROOT or (exists and not path.is_file()):
        raise ValueError("Delta probe paths must be files inside the checkout")
    return path


def manifest(path):
    with zipfile.ZipFile(path) as archive:
        names = [name for name in archive.namelist() if name.endswith('.nuspec') and '/' not in name]
        if len(names) != 1: raise ValueError('Ambiguous package manifest')
        return {node.tag.rsplit('}', 1)[-1]: node.text for node in ET.fromstring(archive.read(names[0])).iter()}


def entry_hashes(path):
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        names = [entry.filename for entry in entries if not entry.is_dir()]
        if len(names) != len(set(names)) or any(name.startswith('/') or '\\' in name or '..' in name.split('/') for name in names):
            raise ValueError('Unsafe or duplicate package entry')
        return {name: hashlib.sha256(archive.read(name)).hexdigest() for name in names}


def probe(helper, old, delta, target, output):
    helper, old, delta, target = map(checked_path, (helper, old, delta, target))
    output = checked_path(output, exists=False)
    if output.exists() and any(output.iterdir()): raise ValueError('Delta probe output must be empty')
    if any(path.is_relative_to(output) for path in (helper, old, delta, target)):
        raise ValueError('Probe output must be separate from its immutable inputs')
    metadata = [manifest(path) for path in (old, delta, target)]
    for key in ('id', 'channel', 'rid', 'os'):
        if not metadata[0].get(key) or len({item.get(key) for item in metadata}) != 1:
            raise ValueError('Delta probe package identity differs')
    if metadata[1].get('version') != metadata[2].get('version') or metadata[0].get('version') == metadata[2].get('version'):
        raise ValueError('Delta probe needs a distinct verified baseline and matching target')
    before = {path: hashlib.sha256(path.read_bytes()).hexdigest() for path in (helper, old, delta, target)}
    output.mkdir(parents=True, exist_ok=True)
    native = output / 'native-patch-helper'
    shutil.copy2(helper, native)
    native.chmod(native.stat().st_mode | 0o100)
    context = output / 'isolated-context'; context.mkdir()

    def run(patch, destination, log):
        # No apply/start/install command or application invocation is permitted.
        arguments = [str(native), 'patch', '--old', str(old), '--delta', str(patch),
                     '--output', str(destination), '--rootDir', str(context), '--silent', '--log', str(log)]
        result = subprocess.run(arguments, cwd=output, capture_output=True, text=True, timeout=300)
        (output / (log.stem + '-console.txt')).write_text(result.stdout + result.stderr, encoding='utf-8')
        return result

    rebuilt = output / 'reconstructed.nupkg'
    result = run(delta, rebuilt, output / 'patch.log')
    if result.returncode != 0: raise RuntimeError('Native patch failed; see isolated patch log')
    expected, actual = entry_hashes(target), entry_hashes(rebuilt)
    if actual != expected: raise ValueError('Native reconstruction differs from target package contents')

    corrupt = output / 'corrupt-delta.nupkg'
    with zipfile.ZipFile(delta) as source, zipfile.ZipFile(corrupt, 'w') as damaged:
        selected = next((entry.filename for entry in source.infolist()
                         if entry.filename.startswith('lib/') and entry.filename.endswith('.zsdiff') and entry.file_size > 0), None)
        if selected is None: raise ValueError('Real delta has no nonempty native patch to corrupt')
        for entry in source.infolist():
            damaged.writestr(entry, b'not-a-valid-zstd-patch' if entry.filename == selected else source.read(entry))
    rejected = run(corrupt, output / 'corrupt-output.nupkg', output / 'corrupt-patch.log')
    if rejected.returncode == 0: raise ValueError('Native patch accepted corrupt delta content')
    if any(hashlib.sha256(path.read_bytes()).hexdigest() != value for path, value in before.items()):
        raise ValueError('Delta probe changed an immutable input')
    return {'result': 'PASS', 'package_id': metadata[2]['id'], 'runtime': metadata[2]['rid'],
            'previous_version': metadata[0]['version'], 'target_version': metadata[2]['version'],
            'compared_files': len(expected), 'corrupt_delta_rejected': True, 'application_started': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ('helper', 'old', 'delta', 'target', 'output-directory'):
        parser.add_argument('--' + option, required=True, type=Path)
    args = parser.parse_args()
    print(json.dumps(probe(args.helper, args.old, args.delta, args.target, args.output_directory)))


if __name__ == '__main__': main()
