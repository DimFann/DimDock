import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import mysql.connector

import fast_start


class FastStartTests(unittest.TestCase):
    def setUp(self):
        self.environment = patch.dict(os.environ, {
            'DB_HOST': 'test', 'DB_USER': 'test', 'DB_PASSWORD': 'test',
            'DB_NAME': 'test', 'DB_PORT': '3307', 'DIMDOCK_DATA_ROOT': '/test-data',
        })
        self.environment.start()
        self.addCleanup(self.environment.stop)
        self.store_patch = patch.object(fast_start, 'LocalFileStore')
        self.store = self.store_patch.start().return_value
        self.addCleanup(self.store_patch.stop)
        self.store.exists.return_value = True
        self.connect_patch = patch.object(fast_start.mysql.connector, 'connect')
        self.connect = self.connect_patch.start()
        self.addCleanup(self.connect_patch.stop)
        self.connection = self.connect.return_value
        self.cursor = self.connection.cursor.return_value.__enter__.return_value
        self.cursor.fetchone.return_value = (1,)

    def test_initialized_start_checks_database_without_writes(self):
        fast_start.check_initialized()
        self.assertEqual(self.store.exists.call_count, 3)
        self.assertEqual(self.connect.call_args.kwargs['port'], 3307)
        self.assertEqual(self.cursor.execute.call_count, len(fast_start.REQUIRED_TABLES) + 1)
        self.assertTrue(all(call.args[0].startswith('SELECT ') for call in self.cursor.execute.call_args_list))
        self.connection.close.assert_called_once()
        self.connection.commit.assert_not_called()
        self.store.write.assert_not_called()

    def test_each_missing_marker_refuses_fast_start(self):
        for marker in fast_start.REQUIRED_MARKERS:
            with self.subTest(marker=marker):
                self.store.exists.side_effect = lambda area, name: name != marker
                with self.assertRaisesRegex(RuntimeError, 'PATH_LOADER_FAST=false'):
                    fast_start.check_initialized()
        self.connect.assert_not_called()

    def test_empty_database_is_rejected(self):
        self.cursor.fetchone.return_value = None
        with self.assertRaisesRegex(RuntimeError, 'initialized database'):
            fast_start.check_initialized()
        self.connection.close.assert_called_once()

    def test_missing_schema_is_rejected(self):
        self.cursor.execute.side_effect = mysql.connector.Error('missing table', errno=1146)
        with self.assertRaisesRegex(RuntimeError, 'schema update'):
            fast_start.check_initialized()
        self.connection.close.assert_called_once()

    @patch.object(fast_start.time, 'sleep')
    def test_transient_connection_failure_retries(self, sleep):
        self.connect.side_effect = [mysql.connector.Error('not ready', errno=2003), self.connection]
        fast_start.check_initialized()
        self.assertEqual(self.connect.call_count, 2)
        sleep.assert_called_once_with(2)

    @patch.object(fast_start.time, 'sleep')
    def test_connection_retry_is_bounded(self, sleep):
        self.connect.side_effect = mysql.connector.Error('not ready', errno=2003)
        with self.assertRaisesRegex(RuntimeError, 'available, initialized'):
            fast_start.check_initialized()
        self.assertEqual(self.connect.call_count, 30)
        self.assertEqual(sleep.call_count, 29)

    def test_auth_failure_is_not_retried(self):
        self.connect.side_effect = mysql.connector.Error('denied', errno=1045)
        with self.assertRaisesRegex(RuntimeError, 'available, initialized'):
            fast_start.check_initialized()
        self.assertEqual(self.connect.call_count, 1)


@unittest.skipUnless(shutil.which('bash') and os.name != 'nt', 'Requires Linux Bash (run in Docker)')
class StartupScriptTests(unittest.TestCase):
    def test_fast_branch_skips_all_processing_and_propagates_check_failure(self):
        for check_exit in ('0', '1'):
            with self.subTest(check_exit=check_exit), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                (root / 'nc').write_text('#!/bin/sh\nexit 0\n')
                (root / 'python').write_text(
                    '#!/bin/sh\nprintf "%s\\n" "$*" >> "$CALL_LOG"\n'
                    'case "$1" in\n'
                    '  /app/fast_start.py) exit "$CHECK_EXIT" ;;\n'
                    '  *) echo "Unexpected processing in fast mode" >&2; exit 99 ;;\nesac\n'
                )
                for name in ('nc', 'python'):
                    (root / name).chmod(0o755)
                env = dict(os.environ, PATH=f'{root}:{os.environ["PATH"]}',
                           PATH_LOADER_FAST='true', DB_HOST='test', CHECK_EXIT=check_exit,
                           CALL_LOG=str(root / 'calls'))
                result = subprocess.run(['bash', 'db-load-activity.sh'], env=env, capture_output=True, text=True)
                self.assertEqual(result.returncode, int(check_exit), result.stderr)
                self.assertEqual((root / 'calls').read_text().splitlines(), ['/app/fast_start.py'])

    def test_normal_start_preserves_initialization_and_reconciliation(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'nc').write_text('#!/bin/sh\nexit 0\n')
            (root / 'python').write_text(
                '#!/bin/sh\n'
                'if [ "$1" = "-c" ]; then exec "' + sys.executable + '" "$@"; fi\n'
                'printf "%s\\n" "$*" >> "$CALL_LOG"\n'
            )
            for name in ('nc', 'python'):
                (root / name).chmod(0o755)
            env = dict(os.environ, PATH=f'{root}:{os.environ["PATH"]}',
                       PATH_LOADER_FAST='false', DB_HOST='test',
                       DIMDOCK_DATA_ROOT=str(root / 'data'), CALL_LOG=str(root / 'calls'))
            expected = ['load_paths.py', 'build_shortcuts.py --directories-only', 'build_shortcuts.py']
            for _ in range(2):
                result = subprocess.run(['bash', 'db-load-activity.sh'], env=env, capture_output=True, text=True)
                self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual((root / 'calls').read_text().splitlines(),
                             expected + expected[:2])
            for marker in fast_start.REQUIRED_MARKERS:
                self.assertTrue((root / 'data' / 'app_state' / marker).is_file())

    def test_invalid_flag_is_rejected(self):
        result = subprocess.run(['bash', 'db-load-activity.sh'],
                                env=dict(os.environ, PATH_LOADER_FAST='maybe'),
                                capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('must be true or false', result.stderr)


if __name__ == '__main__':
    unittest.main()
