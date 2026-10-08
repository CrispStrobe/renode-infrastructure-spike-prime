#!/usr/bin/env python3
# SPDX-License-Identifier: BSD-3-Clause
# Copyright (c) 2026 Brickwright contributors
"""Mock mutation controls and small owned Python process tests; no C#/engine runs."""
from contextlib import redirect_stdout
import importlib.util
import io
import os
import signal
import time
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import MagicMock, patch
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('budget_mutations', ROOT / 'tools/check_lpf2_data_budget_mutations.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class Runner(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix='budget-harness-control-')
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        infrastructure = self.root / 'src/Infrastructure'
        self.model = infrastructure / module.MODEL
        self.model.parent.mkdir(parents=True)
        self.original = (ROOT / module.MODEL).read_bytes()
        self.model.write_bytes(self.original)
        project = infrastructure / module.PROJECT
        project.parent.mkdir(parents=True)
        project.write_text('<Project/>')
        self.output = self.root / 'receipts'
        self.phases = []
        self.defect = None

    def fake_dotnet(self, command, **kwargs):
        self.assertEqual(command[:2], ['dotnet', 'test'])
        folder = Path(command[-1])
        name = folder.name
        self.phases.append(name)
        baseline = name in ('baseline', 'restored-baseline')
        self.assertEqual(self.model.read_bytes() == self.original, baseline)
        defect = self.defect if name == 'ignore-zero-budget' else None
        if defect == 'timeout':
            raise subprocess.TimeoutExpired(command, 600, output=b'partial-out', stderr=b'partial-err')
        if defect == 'compile':
            return subprocess.CompletedProcess(command, 1, b'compile failed', b'compiler error')
        tree = ET.Element('TestRun')
        results = ET.SubElement(tree, 'Results')
        count = 17 if defect == 'incomplete' else 18
        for index in range(count):
            failed = not baseline and index == 0 and defect != 'escape'
            node = ET.SubElement(results, 'UnitTestResult', {
                'testName': ('changed-case' if defect == 'identity' and index == 0 else 'case-' + str(index)),
                'outcome': 'Failed' if failed else 'Passed',
            })
            if failed:
                info = ET.SubElement(ET.SubElement(node, 'Output'), 'ErrorInfo')
                ET.SubElement(info, 'Message').text = ('Unhandled NullReferenceException'
                    if defect == 'exception' else 'Expected: 1 but was: 2')
        ET.ElementTree(tree).write(folder / 'tests.trx')
        return subprocess.CompletedProcess(command, 0 if baseline or defect == 'escape' else 1,
                                           b'mocked compiler/test output\n', b'')

    def run_control(self):
        with patch.object(sys, 'argv', ['check', '--runtime-root', str(self.root),
                                      '--results-directory', str(self.output)]), \
                patch.object(module, 'run_compiled', self.fake_dotnet), redirect_stdout(io.StringIO()):
            module.main()

    def test_mutations_restore_source_and_rebuild_baseline(self):
        self.run_control()
        self.assertEqual(self.phases, ['baseline', 'ignore-zero-budget', 'never-consume-budget',
                                      'reserve-after-delivery', 'restored-baseline'])
        self.assertEqual(self.model.read_bytes(), self.original)
        self.assertEqual(len(list(self.output.glob('*/tests.trx'))), 5)
        self.assertEqual((self.output / 'baseline/stdout.log').read_bytes(),
                         b'mocked compiler/test output\n')

    def test_setup_failure_is_not_a_detection(self):
        self.defect = 'compile'
        with self.assertRaisesRegex(RuntimeError, 'no test receipt'):
            self.run_control()
        self.assertEqual(self.phases[-1], 'restored-baseline')
        self.assertEqual(self.model.read_bytes(), self.original)
        self.assertEqual((self.output / 'ignore-zero-budget/stderr.log').read_bytes(), b'compiler error')

    def test_incomplete_tests_are_rejected(self):
        self.defect = 'incomplete'
        with self.assertRaisesRegex(RuntimeError, 'incomplete test execution'):
            self.run_control()
        self.assertEqual(self.model.read_bytes(), self.original)

    def test_changed_test_identities_are_rejected(self):
        self.defect = 'identity'
        with self.assertRaisesRegex(RuntimeError, 'test identities changed'):
            self.run_control()
        self.assertEqual(self.model.read_bytes(), self.original)

    def test_runtime_exception_is_not_an_assertion_detection(self):
        self.defect = 'exception'
        with self.assertRaisesRegex(RuntimeError, 'non-assertion test failure'):
            self.run_control()
        self.assertEqual(self.model.read_bytes(), self.original)

    def test_escaped_mutation_is_rejected(self):
        self.defect = 'escape'
        with self.assertRaisesRegex(RuntimeError, 'mutation escaped'):
            self.run_control()
        self.assertEqual(self.model.read_bytes(), self.original)

    def test_timeout_preserves_partial_bytes_and_restores_source(self):
        self.defect = 'timeout'
        with self.assertRaisesRegex(RuntimeError, 'timeout; partial output retained'):
            self.run_control()
        self.assertEqual(self.model.read_bytes(), self.original)
        self.assertEqual(self.phases[-1], 'restored-baseline')
        folder = self.output / 'ignore-zero-budget'
        self.assertEqual((folder / 'stdout.log').read_bytes(), b'partial-out')
        self.assertEqual((folder / 'stderr.log').read_bytes(), b'partial-err')

    def test_existing_results_are_not_overwritten(self):
        self.output.mkdir()
        marker = self.output / 'previous.log'
        marker.write_bytes(b'previous immutable evidence')
        with self.assertRaises(FileExistsError):
            self.run_control()
        self.assertEqual(marker.read_bytes(), b'previous immutable evidence')
        self.assertEqual(self.phases, [])
        self.assertEqual(self.model.read_bytes(), self.original)


@unittest.skipUnless(sys.platform.startswith('linux'), 'Linux hosted process controls')
class ProcessGroup(unittest.TestCase):
    def test_success_preserves_exit_status_and_both_streams(self):
        result = module.run_compiled(
            [sys.executable, '-c', 'import sys; print("out"); print("err", file=sys.stderr); sys.exit(7)'],
            cwd=ROOT, timeout=5)
        self.assertEqual(result.returncode, 7)
        self.assertEqual(result.stdout, b'out\n')
        self.assertEqual(result.stderr, b'err\n')

    def test_success_stops_redirected_descendant_before_returning(self):
        parent = ('import os,subprocess,sys; '
                  'p=subprocess.Popen([sys.executable,"-c","import time; time.sleep(60)"],'
                  'stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL); '
                  'print(os.getpid(),p.pid,flush=True)')
        result = module.run_compiled([sys.executable, '-c', parent], cwd=ROOT, timeout=5)
        self.assertEqual(result.returncode, 0)
        self.assert_stopped_owned_descendant(result.stdout)

    def test_timeout_stops_owned_descendant_before_returning_partial_output(self):
        child = 'import signal,time; signal.signal(signal.SIGTERM,signal.SIG_IGN); time.sleep(60)'
        parent = ('import os,subprocess,sys,time; '
                  'p=subprocess.Popen([sys.executable,"-c",sys.argv[1]],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL); '
                  'print(os.getpid(),p.pid,flush=True); print("partial",file=sys.stderr,flush=True); time.sleep(60)')
        with self.assertRaises(subprocess.TimeoutExpired) as caught:
            module.run_compiled([sys.executable, '-c', parent, child], cwd=ROOT, timeout=2)
        error = caught.exception
        self.assertEqual(error.stderr, b'partial\n')
        self.assert_stopped_owned_descendant(error.stdout)

    def assert_stopped_owned_descendant(self, output):
        parent_pid, child_pid = map(int, output.split())
        def cleanup_owned_group():
            try:
                if os.getpgid(child_pid) == parent_pid:
                    os.killpg(parent_pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
        self.addCleanup(cleanup_owned_group)
        # A killed grandchild can briefly be an init-owned zombie; it must not
        # execute. The supervisor reaps its direct child before returning.
        deadline = time.monotonic() + 2
        while True:
            stat = Path('/proc/%d/stat' % child_pid)
            try:
                state = stat.read_text().rsplit(')', 1)[1].split()[0]
            except FileNotFoundError:
                break
            if state == 'Z':
                break
            if time.monotonic() >= deadline:
                self.fail('Owned descendant remains live after invocation: ' + state)
            time.sleep(0.01)

    def test_interruption_stops_group_and_reaps_before_propagating(self):
        process = MagicMock()
        process.pid = 987654321  # No real signal: killpg is mocked below.
        process.__enter__.return_value = process
        process.communicate.side_effect = [KeyboardInterrupt(), (b'out', b'err')]
        with patch.object(module.subprocess, 'Popen', return_value=process) as spawn, \
                patch.object(module.os, 'killpg') as kill:
            with self.assertRaises(KeyboardInterrupt):
                module.run_compiled(['synthetic-command'], cwd=ROOT, timeout=5)
        self.assertTrue(spawn.call_args.kwargs['start_new_session'])
        kill.assert_called_once_with(process.pid, signal.SIGKILL)
        self.assertEqual(process.communicate.call_args_list[-1].args, ())
        self.assertEqual(process.communicate.call_count, 2)


if __name__ == '__main__':
    unittest.main()
