from contextlib import redirect_stderr, redirect_stdout
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from gdrive_scripts.export_drive_shortcuts import DriveError, FOLDER_MIME, SHORTCUT_MIME
from gdrive_scripts.export_folder_redirects import build_redirects, gallery_target, main, resolve_folders
from gdrive_scripts.test_export_drive_shortcuts import FakeDrive, file


class RedirectTests(unittest.TestCase):
    def test_maps_every_folder_id_to_its_gallery_path(self):
        drive = FakeDrive({
            'base': [file('misc', 'Misc', FOLDER_MIME), file('alias', 'Not followed', SHORTCUT_MIME),
                     file('img', 'image.png', 'image/png')],
            'misc': [file('ref', 'Reference', FOLDER_MIME)],
            'ref': [file('pony', 'Pony Anatomy', FOLDER_MIME)],
            'pony': [],
        })
        folders, warnings = resolve_folders(drive, 'base')
        self.assertEqual(warnings, [])
        self.assertEqual(build_redirects(folders), [
            {'Path': '/sketch', 'Query': {'folderID': 'base'}, 'Target': '/Gallery'},
            {'Path': '/sketch', 'Query': {'folderID': 'misc'}, 'Target': '/Gallery/Misc'},
            {'Path': '/sketch', 'Query': {'folderID': 'ref'}, 'Target': '/Gallery/Misc/Reference'},
            {'Path': '/sketch', 'Query': {'folderID': 'pony'}, 'Target': '/Gallery/Misc/Reference/Pony%20Anatomy'},
            {'Path': '/sketch', 'Target': '/Gallery'},
        ])

    def test_skipped_folders_and_their_contents_map_to_nearest_gallery_ancestor(self):
        drive = FakeDrive({
            'base': [file('misc', 'Misc', FOLDER_MIME)],
            'misc': [file('bad', 'a/b', FOLDER_MIME), file('dup1', 'Same', FOLDER_MIME),
                     file('dup2', 'same', FOLDER_MIME), file('ok', 'Ok', FOLDER_MIME)],
            'bad': [file('inner', 'Inner', FOLDER_MIME), file('ok', 'Ok again', FOLDER_MIME)],
            'inner': [],
            'dup1': [],
            'dup2': [file('deep', 'Deep', FOLDER_MIME)],
            'deep': [],
            'ok': [],
        })
        folders, notes = resolve_folders(drive, 'base')
        targets = {rule['Query']['folderID']: rule['Target'] for rule in build_redirects(folders) if 'Query' in rule}
        self.assertEqual(targets, {
            'base': '/Gallery', 'misc': '/Gallery/Misc', 'ok': '/Gallery/Misc/Ok',
            'bad': '/Gallery/Misc', 'inner': '/Gallery/Misc',
            'dup1': '/Gallery/Misc', 'dup2': '/Gallery/Misc', 'deep': '/Gallery/Misc',
        })
        self.assertEqual(len(notes), 5)
        self.assertEqual(build_redirects(folders), build_redirects(list(reversed(folders))))

    def test_unusable_folder_id_fails_instead_of_partial_export(self):
        for bad_id in [None, 'not a valid id']:
            drive = FakeDrive({'base': [file('x', 'a/b', FOLDER_MIME)], 'x': [file(bad_id, 'Child', FOLDER_MIME)]})
            with self.assertRaises(DriveError):
                resolve_folders(drive, 'base')

    def test_target_escapes_segments(self):
        self.assertEqual(gallery_target(''), '/Gallery')
        self.assertEqual(gallery_target('A&B/C?#'), '/Gallery/A%26B/C%3F%23')

    def test_main_writes_appsettings_section(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'nested' / 'redirects.json'
            with patch.dict(os.environ, {'GOOGLE_DRIVE_API_KEY': 'test', 'GOOGLE_DRIVE_ROOT_RESOURCE_KEY': 'resource'}):
                with patch('gdrive_scripts.export_folder_redirects.DriveClient') as client:
                    with patch('gdrive_scripts.export_folder_redirects.resolve_folders',
                               return_value=([('base', ''), ('x', 'X')], [])):
                        with redirect_stdout(io.StringIO()):
                            self.assertEqual(main(['--root-folder-id', 'base', '--output', str(output),
                                                   '--legacy-path', '/old', '--query-key', 'id']), 0)
                client.return_value.set_resource_key.assert_called_once_with('base', 'resource')
            rules = json.loads(output.read_text(encoding='utf-8'))['LegacyRedirects']
            self.assertEqual(rules[1], {'Path': '/old', 'Query': {'id': 'x'}, 'Target': '/Gallery/X'})
            self.assertEqual(rules[-1], {'Path': '/old', 'Target': '/Gallery'})

    def test_root_folder_id_from_environment(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'redirects.json'
            with patch.dict(os.environ, {'GOOGLE_DRIVE_API_KEY': 'test', 'GOOGLE_DRIVE_ROOT_FOLDER_ID': 'env-root',
                                         'GOOGLE_DRIVE_ROOT_RESOURCE_KEY': ''}):
                with patch('gdrive_scripts.export_folder_redirects.DriveClient'):
                    with patch('gdrive_scripts.export_folder_redirects.resolve_folders',
                               return_value=([], [])) as walk:
                        with redirect_stdout(io.StringIO()):
                            self.assertEqual(main(['--output', str(output)]), 0)
                walk.assert_called_once()
                self.assertEqual(walk.call_args.args[1], 'env-root')

    def test_api_failure_produces_no_output(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'redirects.json'
            with patch.dict(os.environ, {'GOOGLE_DRIVE_API_KEY': 'test'}):
                with patch('gdrive_scripts.export_folder_redirects.resolve_folders', side_effect=DriveError('HTTP 403')):
                    with redirect_stderr(io.StringIO()):
                        self.assertEqual(main(['--root-folder-id', 'base', '--output', str(output)]), 1)
            self.assertFalse(output.exists())

    def test_direct_and_module_entry_points(self):
        directory = Path(__file__).resolve().parent
        for cwd, arguments in [
            (directory, ['export_folder_redirects.py', '--help']),
            (directory.parent, ['-m', 'gdrive_scripts.export_folder_redirects', '--help']),
        ]:
            result = subprocess.run([sys.executable, *arguments], cwd=cwd, capture_output=True,
                                    text=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == '__main__':
    unittest.main()
