#!/usr/bin/env python3
# SPDX-License-Identifier: BSD-3-Clause
# Copyright (c) 2026 Brickwright contributors
"""Compile real model mutations in an explicitly supplied owned Runtime checkout.

Run after canonical Runtime/model suites, on a hosted runner. Mutants must compile
and produce NUnit assertion failures; build/setup failures are not detections.
Original model bytes and the baseline compiled test assembly are restored.
"""
import argparse
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET

MODEL = 'src/Emulator/Peripherals/Peripherals/UART/LegoLpf2Port.cs'
PROJECT = 'src/Emulator/Peripherals/Test/PeripheralsTests/PeripheralsTests_NET.csproj'
FILTER = 'FullyQualifiedName~LegoLpf2DataBudgetTests'


def results(path):
    tree = ET.parse(path)
    nodes = tree.findall('.//{*}UnitTestResult')
    failed = [node for node in nodes if node.get('outcome') == 'Failed']
    return nodes, failed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime-root', type=Path, required=True)
    args = parser.parse_args()
    root = args.runtime_root.resolve()
    infrastructure = root / 'src/Infrastructure'
    model = infrastructure / MODEL
    project = infrastructure / PROJECT
    if not model.is_file() or not project.is_file():
        parser.error('A complete owned Runtime/Infrastructure checkout is required')
    original = model.read_bytes()
    source = original.decode('utf-8')
    guard = 'Device == null || (DataReportsLimited && DataReportsRemaining == 0)'
    reserve = '''            if(DataReportsLimited)
            {
                DataReportsRemaining--;
            }
'''
    send = '            SendMessage((byte)(0xc0 | (sizeCode << 3) | (SelectedMode & 0x7)), padded);\n'
    mutations = [
        ('ignore-zero-budget', guard, 'Device == null'),
        ('never-consume-budget', 'DataReportsRemaining--;', 'DataReportsRemaining += 0;'),
        ('reserve-after-delivery', reserve + send, send + reserve),
    ]
    for name, old, unused in mutations:
        if source.count(old) != 1:
            parser.error('Mutation boundary drift: ' + name)
    command = ['dotnet', 'test', str(project), '--configuration', 'Release', '--no-restore',
               '-p:GUI_DISABLED=true', '-p:CurrentPlatform=Linux', '-p:NET=true',
               '--filter', FILTER, '--logger', 'trx;LogFileName=tests.trx']
    with tempfile.TemporaryDirectory(prefix='lpf2-data-budget-') as directory:
        output = Path(directory)

        def check(name, expect_failure):
            folder = output / name
            folder.mkdir()
            completed = subprocess.run(command + ['--results-directory', str(folder)],
                                       cwd=root, capture_output=True, text=True, timeout=600)
            (folder / 'dotnet.log').write_text(completed.stdout + completed.stderr)
            receipt = folder / 'tests.trx'
            if not receipt.is_file():
                raise RuntimeError(name + ': no test receipt; build/setup failure is not a detection\n'
                                   + completed.stdout[-6000:] + completed.stderr[-2000:])
            nodes, failed = results(receipt)
            if len(nodes) < 13 or any(n.get('outcome') not in ('Passed', 'Failed') for n in nodes):
                raise RuntimeError(name + ': incomplete test execution')
            if expect_failure:
                if completed.returncode == 0 or not failed:
                    raise RuntimeError(name + ': mutation escaped assertions')
                for node in failed:
                    message = node.findtext('.//{*}ErrorInfo/{*}Message', '')
                    if not any(word in message for word in ('Expected', 'expected', 'Assert')):
                        raise RuntimeError(name + ': non-assertion test failure: ' + message)
                print(name + ': detected by %d failed assertions' % len(failed))
            else:
                if completed.returncode != 0 or failed:
                    raise RuntimeError(name + ': baseline failed\n' + completed.stdout[-6000:])
                print(name + ': %d tests passed' % len(nodes))

        check('baseline', False)
        try:
            for name, old, new in mutations:
                model.write_text(source.replace(old, new))
                check(name, True)
        finally:
            model.write_bytes(original)
            # Test rebuild also restores the real compiled model, not just text.
            check('restored-baseline', False)
        if model.read_bytes() != original:
            raise RuntimeError('Original model bytes were not restored')


if __name__ == '__main__':
    main()
