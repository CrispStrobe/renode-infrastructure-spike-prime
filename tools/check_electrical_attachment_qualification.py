#!/usr/bin/env python3
# SPDX-License-Identifier: BSD-3-Clause
# Copyright (c) 2026 Brickwright contributors
"""Run bounded, paused electrical controls in an existing compatible Renode.

Uses a distinct qualification namespace; does not replace installed assemblies.
This POSIX source-compiled check supplements the canonical NUnit/full build.
Output contains host paths and logs; keep it untracked/private.
"""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import sys

from stage_electrical_attachment_qualification import stage


def stop_owned(process):
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        return
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        os.killpg(process.pid, signal.SIGKILL)
        process.wait(timeout=10)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--renode', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--timeout', type=int, default=90)
    args = parser.parse_args()
    if os.name != 'posix' or not 30 <= args.timeout <= 300:
        raise ValueError('requires POSIX and a 30..300-second timeout')
    output = args.output.resolve()
    output.mkdir(mode=0o700, parents=True, exist_ok=False)
    stage(args.root.resolve(), output / 'staged')
    temporary = output / 'tmp'
    temporary.mkdir()
    controls = output / 'controls.py'
    controls.write_text('''import json
from System import AppDomain, Array, Object
results=[]
for name, method in [('ElectricalTests','RunElectricalTests'),
                     ('ElectricalAttachmentChecks','RunElectricalAttachmentChecks')]:
    types=[a.GetType(name) for a in AppDomain.CurrentDomain.GetAssemblies() if a.GetType(name) is not None]
    try:
        if len(types)!=1: raise Exception('expected exactly one source-compiled control type')
        value=types[0].GetMethod(method).Invoke(None,Array[Object]([emulationManager.CurrentEmulation]))
        results.append(dict(control=name,passed=str(value).startswith('PASS '),result=str(value)))
    except Exception as error:
        results.append(dict(control=name,passed=False,error=str(error)))
with open(''' + repr(str(output / 'controls.json')) + ''','w') as f:
    json.dump(dict(passed=all(x['passed'] for x in results),controls=results),f,indent=2)
''')
    scenario = output / 'controls.resc'
    scenario.write_text('include @' + str(output / 'staged/models.cs') + '\ninclude @'
                        + str(controls) + '\nquit\n')
    command = [str(args.renode.resolve()), '--disable-gui', '--console', '--plain', str(scenario)]
    (output / 'invocation.json').write_text(json.dumps({
        'args': command, 'timeoutSeconds': args.timeout,
        'scope': 'paused source-compiled controls; no firmware, full Infrastructure build or consumer adoption',
    }, indent=2) + '\n')
    with (output / 'renode.log').open('wb') as log:
        process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=log,
            stderr=subprocess.STDOUT, start_new_session=True,
            env={**os.environ, 'DOTNET_PROCESSOR_COUNT': '1', 'TMPDIR': str(temporary)})
        try:
            code = process.wait(timeout=args.timeout)
        except subprocess.TimeoutExpired:
            stop_owned(process)
            code = 124
        except BaseException:
            stop_owned(process)
            raise
        finally:
            process.stdin.close()
    receipt = output / 'controls.json'
    passed = code == 0 and receipt.exists() and json.loads(receipt.read_text()).get('passed') is True
    (output / 'execution.json').write_text(json.dumps({'exit': code, 'passed': passed}, indent=2) + '\n')
    print('PASS' if passed else 'FAIL', 'source-compiled electrical controls')
    return 0 if passed else 1


if __name__ == '__main__':
    sys.exit(main())
