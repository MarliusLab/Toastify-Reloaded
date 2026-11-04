#!/usr/bin/env python3
"""Validate current-channel packages, prune historical feed entries and write SHA-256."""
import argparse
import json
from pathlib import Path

from velopack_release import finalize_stage

parser = argparse.ArgumentParser()
parser.add_argument("--directory", type=Path, default=Path("dist/windows-release"))
parser.add_argument("--version", required=True)
args = parser.parse_args()
print(json.dumps(finalize_stage(args.directory.resolve(), args.version)))
