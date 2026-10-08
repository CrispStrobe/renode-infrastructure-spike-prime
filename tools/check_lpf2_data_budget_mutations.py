#!/usr/bin/env python3
# SPDX-License-Identifier: BSD-3-Clause
# Copyright (c) 2026 Brickwright contributors
"""Compile real model mutations in an explicitly supplied owned Runtime checkout.

Run after canonical Runtime/model suites, on a hosted runner. Mutants must compile
and produce NUnit assertion failures; build/setup failures are not detections.
Original model bytes and the baseline compiled test assembly are restored.
"""
import argparse
import hashlib
import os
import signal
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET

MODEL = 'src/Emulator/Peripherals/Peripherals/UART/LegoLpf2Port.cs'
PROJECT = 'src/Emulator/Peripherals/Test/PeripheralsTests/PeripheralsTests_NET.csproj'
FILTER = 'FullyQualifiedName~LegoLpf2DataBudgetTests'


def run_compiled(command, *, cwd, timeout):
    """Bound the entire owned compiler/test process group before restoring source."""
    if os.name != 'posix':
        raise RuntimeError('Mutation compilation requires POSIX process-group supervision')
    with subprocess.Popen(command, cwd=cwd, stdout=subprocess.PIPE,
                          stderr=subprocess.PIPE, start_new_session=True) as process:
        try:
            stdout, stderr = process.communicate(timeout=timeout)
        except BaseException as error:
            # This session belongs only to this invocation, never a shared build.
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            stdout, stderr = process.communicate()
            if isinstance(error, subprocess.TimeoutExpired):
                raise subprocess.TimeoutExpired(command, timeout, output=stdout,
                                                stderr=stderr) from error
            raise
        # Retire lingering compiler workers even after a successful parent exit.
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        return subprocess.CompletedProcess(command, process.returncode, stdout, stderr)


def results(path):
    tree = ET.parse(path)
    nodes = tree.findall('.//{*}UnitTestResult')
    failed = [node for node in nodes if node.get('outcome') == 'Failed']
    return nodes, failed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime-root', type=Path, required=True)
    parser.add_argument('--results-directory', type=Path)
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
               '-p:UseSharedCompilation=false', '-nodeReuse:false',
               '--filter', FILTER, '--logger', 'trx;LogFileName=tests.trx']
    if args.results_directory is None:
        output = Path(tempfile.mkdtemp(prefix='lpf2-data-budget-'))
    else:
        output = args.results_directory.resolve()
        output.mkdir(parents=True, exist_ok=False)
    expected_names = None

    def check(name, expect_failure):
        nonlocal expected_names
        folder = output / name
        folder.mkdir()
        try:
            completed = run_compiled(command + ['--results-directory', str(folder)],
                                     cwd=root, timeout=600)
        except subprocess.TimeoutExpired as error:
            (folder / 'stdout.log').write_bytes(error.stdout or b'')
            (folder / 'stderr.log').write_bytes(error.stderr or b'')
            raise RuntimeError(name + ': compiler/test timeout; partial output retained') from error
        (folder / 'stdout.log').write_bytes(completed.stdout)
        (folder / 'stderr.log').write_bytes(completed.stderr)
        (folder / 'dotnet.log').write_bytes(completed.stdout + completed.stderr)
        receipt = folder / 'tests.trx'
        if not receipt.is_file():
            raise RuntimeError(name + ': no test receipt; build/setup failure is not a detection\n'
                                   + completed.stdout[-6000:].decode('utf-8', 'replace')
                                   + completed.stderr[-2000:].decode('utf-8', 'replace'))
        nodes, failed = results(receipt)
        if len(nodes) < 18 or any(n.get('outcome') not in ('Passed', 'Failed') for n in nodes):
            raise RuntimeError(name + ': incomplete test execution')
        names = {node.get('testName') for node in nodes}
        if None in names or len(names) != len(nodes):
            raise RuntimeError(name + ': missing or duplicate test identities')
        if expected_names is None:
            expected_names = names
        elif names != expected_names:
            raise RuntimeError(name + ': executed test identities changed')
        print(name + ': TRX sha256=' + hashlib.sha256(receipt.read_bytes()).hexdigest())
        print(name + ': build log sha256=' + hashlib.sha256((folder / 'dotnet.log').read_bytes()).hexdigest())
        if expect_failure:
            if completed.returncode == 0 or not failed:
                raise RuntimeError(name + ': mutation escaped assertions')
            for node in failed:
                message = node.findtext('.//{*}ErrorInfo/{*}Message', '')
                if not any(word in message for word in ('Expected', 'expected', 'Assert')):
                    raise RuntimeError(name + ': non-assertion test failure: ' + message)
            for node in failed:
                print(name + ': failed assertion ' + node.get('testName'))
            print(name + ': detected by %d failed assertions' % len(failed))
        else:
            if completed.returncode != 0 or failed:
                raise RuntimeError(name + ': baseline failed\n'
                                   + completed.stdout[-6000:].decode('utf-8', 'replace'))
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
