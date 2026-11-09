import ast
from contextlib import redirect_stderr, redirect_stdout
import io
import json
import logging
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
from urllib.error import HTTPError, URLError
from urllib.parse import parse_qs, urlparse

from drive_shortcuts.export_drive_shortcuts import (
    DriveClient, DriveError, FOLDER_MIME, SHORTCUT_MIME, export_shortcuts, main, write_output,
)


def file(file_id, name, mime='image/png'):
    return {'id': file_id, 'name': name, 'mimeType': mime}


def shortcut(file_id, name, target, mime='image/png'):
    return dict(file(file_id, name, SHORTCUT_MIME),
                shortcutDetails={'targetId': target, 'targetMimeType': mime})


class FakeDrive(DriveClient):
    def __init__(self, tree):
        super().__init__('test-key')
        self.tree = tree
        self.visited = []

    def get_folder(self, folder_id):
        return file(folder_id, 'Omitted root name', FOLDER_MIME)

    def list_children(self, folder_id):
        self.visited.append(folder_id)
        return self.tree[folder_id]


class ExportTests(unittest.TestCase):
    def test_direct_and_module_entry_points(self):
        directory = Path(__file__).resolve().parent
        commands = [
            (directory, ['export_drive_shortcuts.py', '--help']),
            (directory.parent, ['-m', 'drive_shortcuts.export_drive_shortcuts', '--help']),
        ]
        for cwd, arguments in commands:
            with self.subTest(arguments=arguments):
                result = subprocess.run(
                    [sys.executable, *arguments], cwd=cwd, capture_output=True, text=True,
                    timeout=30,
                )
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIn('--root-folder-id', result.stdout)

    def test_exact_importer_shape_and_root_paths(self):
        drive = FakeDrive({
            'base': [file('album', 'Album', FOLDER_MIME), file('image', 'actual.png'),
                     shortcut('alias', 'Different label', 'nested'),
                     shortcut('duplicate', 'Second label', 'nested')],
            'album': [file('nested', 'snow \u2603.png'),
                      shortcut('root-target', 'Renamed', 'image')],
        })
        data, warnings = export_shortcuts(drive, 'base')
        self.assertEqual(data, {'shortcuts': {
            'Album': ['root/actual.png'], 'root': ['Album/snow \u2603.png'],
        }})
        self.assertEqual(warnings, [])
        for files in data['shortcuts'].values():
            parent, _, name = files[0].rpartition('/')
            self.assertIn((parent, name), [('root', 'actual.png'), ('Album', 'snow \u2603.png')])

    def test_reports_skips_and_does_not_follow_folder_alias(self):
        drive = FakeDrive({'base': [
            shortcut('folder', 'Folder alias', 'outside-folder', FOLDER_MIME),
            shortcut('external', 'External', 'outside'),
            file('broken', 'Broken', SHORTCUT_MIME),
        ]})
        data, warnings = export_shortcuts(drive, 'base')
        self.assertEqual(data, {'shortcuts': {}})
        self.assertEqual(len(warnings), 3)
        self.assertEqual(drive.visited, ['base'])

    def test_actual_target_type_takes_precedence_over_stale_shortcut_metadata(self):
        drive = FakeDrive({
            'base': [file('image', 'image.png'), file('folder', 'Album', FOLDER_MIME),
                     shortcut('stale-file', 'File alias', 'image', FOLDER_MIME),
                     shortcut('stale-folder', 'Folder alias', 'folder')],
            'folder': [],
        })
        data, warnings = export_shortcuts(drive, 'base')
        self.assertEqual(data['shortcuts'], {'root': ['root/image.png']})
        self.assertEqual(data['directory_shortcuts'], {
            'root': [{'name': 'Folder alias', 'target_path': 'Album'}],
        })
        self.assertEqual(warnings, [])

    def test_directory_aliases_preserve_names_and_root_targets_without_recursion(self):
        drive = FakeDrive({
            'base': [file('album', 'Album', FOLDER_MIME),
                     shortcut('alias', 'My favourites', 'album', FOLDER_MIME)],
            'album': [shortcut('back', 'Home', 'base', FOLDER_MIME),
                      shortcut('self', 'Self', 'album', FOLDER_MIME)],
        })
        data, warnings = export_shortcuts(drive, 'base')
        self.assertEqual(data, {'shortcuts': {}, 'directory_shortcuts': {
            'Album': [{'name': 'Home', 'target_path': 'root'}, {'name': 'Self', 'target_path': 'Album'}],
            'root': [{'name': 'My favourites', 'target_path': 'Album'}],
        }})
        self.assertEqual(warnings, [])
        self.assertEqual(drive.visited, ['base', 'album'])

    def test_invalid_or_duplicate_directory_alias_names_are_skipped(self):
        for aliases in [
            [shortcut('bad', 'a/b', 'base', FOLDER_MIME)],
            [shortcut('bad', 'x' * 256, 'base', FOLDER_MIME)],
            [shortcut('a', 'Alias', 'base', FOLDER_MIME),
             shortcut('b', 'alias', 'base', FOLDER_MIME)],
        ]:
            with self.subTest(aliases=aliases):
                data, warnings = export_shortcuts(FakeDrive({'base': aliases}), 'base')
                self.assertEqual(data, {'shortcuts': {}})
                self.assertTrue(warnings)

    def test_output_runs_through_actual_gallery_importer(self):
        drive = FakeDrive({
            'base': [file('image', 'image.png'), file('folder', 'Album', FOLDER_MIME),
                     shortcut('nested-alias', 'Nested alias', 'nested')],
            'folder': [file('nested', 'nested.png'), shortcut('root-alias', 'Root alias', 'image')],
        })
        data, _ = export_shortcuts(drive, 'base')
        importer = Path(__file__).resolve().parent.parent / 'path_loader' / 'build_shortcuts.py'
        function = next(node for node in ast.parse(importer.read_text(encoding='utf-8')).body
                        if isinstance(node, ast.FunctionDef) and node.name == 'process_shortcuts')
        namespace = {
            'json': json, 'logging': logging, 'SHORTCUTS_FILENAME': '.shortcuts.json',
            'LEGACY_SHORTCUTS_FILENAME': 'shortcuts.json',
            'STORE': SimpleNamespace(exists=lambda *args: True, read_text=lambda *args: json.dumps(data)),
            'ensure_directory_shortcuts_table': lambda cursor: None,
            'process_directory_shortcuts': lambda cursor, data: None,
        }
        exec(compile(ast.Module(body=[function], type_ignores=[]), str(importer), 'exec'), namespace)
        cursor = unittest.mock.Mock()
        cursor.fetchone.side_effect = [(1, 'image.png', 'root'), (2, 'nested.png', 'Album')]
        namespace['process_shortcuts'](cursor)
        calls = cursor.execute.call_args_list
        self.assertEqual([call.args[1] for call in calls], [
            ('image.png', 'root'), (1, 'image.png', 'root', 'Album'),
            ('nested.png', 'Album'), (2, 'nested.png', 'Album', 'root'),
        ])

    def test_collisions_and_invalid_names_are_skipped(self):
        cases = [
            [file('a', 'same.png'), file('b', 'SAME.png')],
            [file('a', '../bad.png')],
            [file('a', 'bad\\name.png')],
            [file('a', 'bad:name.png')],
            [file('a', 'root', FOLDER_MIME)],
            [file('a', 'x' * 256, FOLDER_MIME)],
            [file('base', 'cycle', FOLDER_MIME)],
        ]
        for children in cases:
            with self.subTest(children=children):
                data, warnings = export_shortcuts(FakeDrive({'base': children}), 'base')
                self.assertEqual(data, {'shortcuts': {}})
                self.assertTrue(warnings)

    def test_ambiguous_files_and_folders_excluded_but_valid_shortcuts_export(self):
        drive = FakeDrive({
            'base': [file('a', 'same.png'), file('b', 'SAME.png'),
                     file('folder-a', 'Album', FOLDER_MIME), file('folder-b', 'album', FOLDER_MIME),
                     file('ok', 'valid.png'),
                     shortcut('alias-a', 'First', 'a'), shortcut('alias-b', 'Second', 'b'),
                     shortcut('folder-alias', 'Folder', 'folder-a', FOLDER_MIME),
                     shortcut('valid-alias', 'Valid', 'ok')],
        })
        data, warnings = export_shortcuts(drive, 'base')
        self.assertEqual(data, {'shortcuts': {'root': ['root/valid.png']}})
        self.assertEqual(drive.visited, ['base'])
        self.assertEqual(sum('Duplicate gallery path' in warning for warning in warnings), 2)

    def test_api_failure_still_aborts_export(self):
        drive = FakeDrive({})
        with patch.object(drive, 'list_children', side_effect=DriveError('Drive HTTP 403')):
            with self.assertRaisesRegex(DriveError, 'HTTP 403'):
                export_shortcuts(drive, 'base')

    def test_non_folder_root_and_invalid_id(self):
        with patch.object(DriveClient, 'get_folder', return_value={'mimeType': 'image/png'}):
            with self.assertRaisesRegex(DriveError, 'not a Drive folder'):
                export_shortcuts(DriveClient('test'), 'base')
        with self.assertRaises(ValueError):
            export_shortcuts(FakeDrive({}), "base' or trashed = true")

    def test_paginated_http_requests(self):
        pages = [
            {'files': [], 'nextPageToken': 'next'},
            {'files': [file('a', 'a.png')]},
        ]
        with patch('drive_shortcuts.export_drive_shortcuts.urlopen', side_effect=[
            io.BytesIO(json.dumps(page).encode()) for page in pages
        ]) as http:
            self.assertEqual(DriveClient('secret-test-key').list_children('base'), pages[1]['files'])
        first, second = [call.args[0] for call in http.call_args_list]
        self.assertNotIn('secret-test-key', first.full_url)
        self.assertIn('secret-test-key', first.headers.values())
        query = parse_qs(urlparse(first.full_url).query)
        self.assertEqual(query['q'], ["'base' in parents and trashed = false"])
        self.assertEqual(query['pageSize'], ['1000'])
        self.assertEqual(parse_qs(urlparse(second.full_url).query)['pageToken'], ['next'])

    def test_resource_keys_sent_for_root_and_discovered_child_on_all_pages(self):
        client = DriveClient('test')
        client.set_resource_key('base', 'root-key')
        child = dict(file('child', 'Album', FOLDER_MIME), resourceKey='child-key')
        responses = [
            {'mimeType': FOLDER_MIME},
            {'files': [child], 'nextPageToken': 'next'},
            {'files': []},
            {'files': []},
        ]
        with patch('drive_shortcuts.export_drive_shortcuts.urlopen', side_effect=[
            io.BytesIO(json.dumps(data).encode()) for data in responses
        ]) as http:
            client.get_folder('base')
            client.list_children('base')
            client.list_children('child')
        requests = [call.args[0] for call in http.call_args_list]
        for request, expected in zip(requests, ['base/root-key'] * 3 + ['child/child-key']):
            self.assertEqual(request.get_header('X-goog-drive-resource-keys'), expected)
            self.assertNotIn('root-key', request.full_url)
            self.assertNotIn('child-key', request.full_url)

    def test_invalid_resource_key_is_rejected(self):
        for key in ('bad\r\nheader', 'a/b', 'a,b', ''):
            with self.subTest(key=key), self.assertRaises(ValueError):
                DriveClient('test').set_resource_key('base', key)

    def test_incomplete_and_repeated_pages_fail(self):
        for responses in [
            [{'incompleteSearch': True, 'files': []}],
            [{'files': [], 'nextPageToken': 'same'}] * 2,
            [{'files': 'invalid'}],
        ]:
            with self.subTest(responses=responses):
                with patch.object(DriveClient, 'request', side_effect=responses):
                    with self.assertRaises(DriveError):
                        DriveClient('test').list_children('base')

    def test_http_errors_and_retry(self):
        denied = HTTPError('https://example.invalid?key=secret', 403, 'denied', {}, None)
        with patch('drive_shortcuts.export_drive_shortcuts.urlopen', side_effect=denied):
            with self.assertRaises(DriveError) as result:
                DriveClient('secret').get_folder('base')
        self.assertIn('OAuth', str(result.exception))
        self.assertNotIn('secret', str(result.exception))
        busy = HTTPError('https://example.invalid', 429, 'busy', {}, None)
        with patch('drive_shortcuts.export_drive_shortcuts.time.sleep') as sleep:
            with patch('drive_shortcuts.export_drive_shortcuts.urlopen', side_effect=[
                busy, io.BytesIO(b'{"mimeType": "folder"}'),
            ]):
                self.assertEqual(DriveClient('test').get_folder('base')['mimeType'], 'folder')
        sleep.assert_called_once_with(1)
        with patch('drive_shortcuts.export_drive_shortcuts.time.sleep'):
            with patch('drive_shortcuts.export_drive_shortcuts.urlopen', side_effect=[
                HTTPError('https://example.invalid', 503, 'busy', {}, None) for _ in range(3)
            ]) as http:
                with self.assertRaisesRegex(DriveError, 'HTTP 503'):
                    DriveClient('test').get_folder('base')
        self.assertEqual(http.call_count, 3)
        with patch('drive_shortcuts.export_drive_shortcuts.urlopen', side_effect=URLError('secret')):
            with self.assertRaises(DriveError) as result:
                DriveClient('test').get_folder('base')
        self.assertNotIn('secret', str(result.exception))

    def test_atomic_output_no_overwrite_and_cleanup(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / '.shortcuts.json'
            write_output(output, {'shortcuts': {}}, False)
            with self.assertRaises(FileExistsError):
                write_output(output, {'changed': True}, False)
            self.assertEqual(json.loads(output.read_text()), {'shortcuts': {}})
            write_output(output, {'changed': True}, True)
            self.assertEqual(json.loads(output.read_text()), {'changed': True})
            self.assertEqual(list(Path(directory).iterdir()), [output])

    def test_main_failure_does_not_change_output(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / '.shortcuts.json'
            output.write_text('existing')
            with patch.dict(os.environ, {'GOOGLE_DRIVE_API_KEY': 'secret'}):
                with patch('drive_shortcuts.export_drive_shortcuts.export_shortcuts', side_effect=DriveError('failure')):
                    with redirect_stderr(io.StringIO()):
                        result = main(['--root-folder-id', 'base', '--output', str(output), '--overwrite'])
            self.assertEqual(result, 1)
            self.assertEqual(output.read_text(), 'existing')

    def test_main_exports_with_visible_skip_warning(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / '.shortcuts.json'
            with patch.dict(os.environ, {'GOOGLE_DRIVE_API_KEY': 'test'}):
                with patch('drive_shortcuts.export_drive_shortcuts.export_shortcuts', return_value=(
                    {'shortcuts': {'root': ['Album/photo.png']}}, ['External: skipped.'],
                )):
                    stderr, stdout = io.StringIO(), io.StringIO()
                    with redirect_stderr(stderr), redirect_stdout(stdout):
                        result = main(['--root-folder-id', 'base', '--output', str(output)])
            self.assertEqual(result, 0)
            self.assertEqual(json.loads(output.read_text()), {'shortcuts': {'root': ['Album/photo.png']}})
            self.assertIn('WARNING:', stderr.getvalue())
            self.assertIn('1 warnings', stdout.getvalue())


if __name__ == '__main__':
    unittest.main()
