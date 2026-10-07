#!/usr/bin/env python3
# SPDX-License-Identifier: BSD-3-Clause
# Copyright (c) 2026 Brickwright contributors
"""Stage source-compiled electrical controls without shadowing installed types.

This is a bounded qualification aid, not a replacement Infrastructure build or
consumer pin adoption. Input notices are retained. Only the namespace, extension
entry point and test type references change; model behavior is not translated.
Outputs and execution receipts belong in an untracked/private directory.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re

NAMESPACE = 'Brickwright.ElectricalQualification'
MODEL = 'src/Emulator/Peripherals/Peripherals/UART/LegoLpf2ElectricalPort.cs'
PORTS = 'src/Emulator/Peripherals/Peripherals/UART/PrimeElectricalPorts.cs'
TEST_ROOT = 'src/Emulator/Peripherals/Test/PeripheralsTests/'
TESTS = ('LegoLpf2ElectricalPortTests.cs', 'LegoLpf2ElectricalAttachmentTests.cs')


def stage(root, output):
    if output.exists():
        raise ValueError('output exists; preserve earlier qualification')
    parts, imports, inputs = [], set(), {}
    for name in (MODEL, PORTS, *(TEST_ROOT + test for test in TESTS)):
        raw = (root / name).read_bytes()
        source = raw.decode('utf8')
        inputs[name] = hashlib.sha256(raw).hexdigest()
        if name.startswith(TEST_ROOT):
            marker = '\nnamespace Antmicro.Renode.PeripheralsTests\n'
            if source.count(marker) != 1:
                raise ValueError('expected one NUnit wrapper')
            source = source.split(marker)[0]
            for model_type in ('LegoLpf2ElectricalPort', 'Lpf2ElectricalMotor'):
                source = re.sub(r'\b' + model_type + r'\b', NAMESPACE + '.' + model_type, source)
        else:
            marker = 'namespace Antmicro.Renode.Peripherals.UART'
            if source.count(marker) != 1:
                raise ValueError('expected one model namespace')
            source = source.replace(marker, 'namespace ' + NAMESPACE)
            if name == PORTS:
                source = source.replace('CreatePrimeElectricalPorts', 'CreateQualificationElectricalPorts')
        lines = []
        for line in source.splitlines():
            if line.startswith('using '):
                if line != 'using NUnit.Framework;':
                    imports.add(line)
            else:
                lines.append(line)
        parts.append('\n'.join(lines))
    imports.add('using Antmicro.Renode.Peripherals.UART;')
    generated = '\n'.join(sorted(imports)) + '\n' + '\n'.join(parts) + '\n'
    output.mkdir(parents=True)
    (output / 'models.cs').write_text(generated)
    (output / 'source-receipt.json').write_text(json.dumps({
        'scope': 'source-compiled candidate under a distinct qualification namespace; not a complete Infrastructure build',
        'transforms': ['namespace and topology entry-point rename',
                       'tests reference qualification types; NUnit wrappers omitted',
                       'using directives consolidated; source notices retained'],
        'inputs': inputs,
        'generatedSha256': hashlib.sha256(generated.encode()).hexdigest(),
    }, indent=2) + '\n')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    stage(args.root.resolve(), args.output.resolve())
