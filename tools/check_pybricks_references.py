#!/usr/bin/env python3
# SPDX-License-Identifier: BSD-3-Clause
# Copyright (c) 2026 Brickwright contributors
"""Validate retained attribution and the inventoried LPF2 discovery fixture."""
import hashlib
import json
from pathlib import Path
import re

def require(condition, message):
    if not condition:
        raise SystemExit(message)

root = Path(__file__).resolve().parents[1]
manifest = json.loads((root/'pybricks-references.json').read_text())
listed = {item['path'] for item in manifest['inputs']}
for path in (root/'src/Emulator/Peripherals/Peripherals').rglob('*.cs'):
    if 'pybricks' in path.read_text().lower():
        require(path.relative_to(root).as_posix() in listed, f'{path}: uninventoried Pybricks reference')
for item in manifest['inputs']:
    path = root/item['path']
    require(hashlib.sha256(path.read_bytes()).hexdigest() == item['sha256'], f'{path}: inventory drift')
    text = path.read_text()
    require('Copyright (c) 2019-2023 The Pybricks Authors' in text, f'{path}: attribution missing')
    require(manifest['commit'] in text, f'{path}: source pin missing')
text = (root/manifest['inputs'][0]['path']).read_text()
body = re.search(r'discoveryBytes\s*=\s*\{(.*?)\};', text, re.S).group(1)
data = bytes(int(x, 16) for x in re.findall(r'0x([0-9a-fA-F]+)', body))
require(hashlib.sha256(data).hexdigest() == manifest['fixture_sha256'], 'discovery fixture changed')
require(hashlib.sha256((root/'licenses/Pybricks-MIT.txt').read_bytes()).hexdigest() == manifest['notice_sha256'], 'MIT grant changed or truncated')
print('Pybricks source/reference attribution and discovery fixture verified')
