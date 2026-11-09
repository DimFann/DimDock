from contextlib import redirect_stderr, redirect_stdout
import io
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from gdrive_scripts.export_drive_shortcuts import DriveError, FOLDER_MIME, SHORTCUT_MIME
from gdrive_scripts.export_folder_descriptions import DEFAULT_OUTPUT, main, prune_empty_folders, scrape_descriptions, write_descriptions
from gdrive_scripts.test_export_drive_shortcuts import FakeDrive, file


class DescriptionTests(unittest.TestCase):
    def test_exact_hierarchy_content_root_and_empty_folders(self):
        drive = FakeDrive({
            'base': [dict(file('album', 'Album', FOLDER_MIME), description='Hello \u2603\n\n**World**'),
                     file('alias', 'Not followed', SHORTCUT_MIME)],
            'album': [file('empty', 'Empty', FOLDER_MIME)],
            'empty': [],
        })
        with patch.object(drive, 'get_folder', return_value={
            'mimeType': FOLDER_MIME, 'description': 'Root description',
        }):
            descriptions, warnings = scrape_descriptions(drive, 'base')
        self.assertEqual(warnings, [])
        self.assertEqual(descriptions, {
            '': 'Root description', 'Album': 'Hello \u2603\n\n**World**', 'Album/Empty': '',
        })
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'tree'
            self.assertEqual(write_descriptions(output, descriptions), 2)
            self.assertEqual((output / '.description.md').read_text(), 'Root description')
            self.assertEqual((output / 'Album' / '.description.md').read_text(encoding='utf-8'),
                             descriptions['Album'])
            self.assertFalse((output / 'Album' / 'Empty').exists())
            self.assertFalse((output / 'Album' / 'Empty' / '.description.md').exists())
            self.assertFalse(list(output.rglob('description.md')))
            with self.assertRaises(FileExistsError):
                write_descriptions(output, descriptions)

    def test_prunes_empty_branches_preserving_descriptions_ancestors_and_other_files(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            (output / 'Empty' / 'Child').mkdir(parents=True)
            (output / 'Parent' / 'Described').mkdir(parents=True)
            (output / 'Parent' / 'Described' / '.description.md').write_text('Keep')
            (output / 'Other').mkdir()
            (output / 'Other' / 'file.txt').write_text('Keep')
            self.assertEqual(prune_empty_folders(output), 2)
            self.assertTrue((output / 'Parent' / 'Described' / '.description.md').exists())
            self.assertTrue((output / 'Other' / 'file.txt').exists())
            self.assertTrue(output.exists())
            with patch('gdrive_scripts.export_folder_descriptions.drive_settings') as settings:
                with redirect_stdout(io.StringIO()):
                    self.assertEqual(main(['--prune-only', '--output', str(output)]), 0)
                settings.assert_not_called()

    def test_only_creates_description_ancestors(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'tree'
            write_descriptions(output, {'': '', 'Empty': ' \n', 'Ancestor': '', 'Ancestor/Child': 'Keep'})
            self.assertFalse((output / 'Empty').exists())
            self.assertTrue((output / 'Ancestor' / 'Child' / '.description.md').exists())

    def test_invalid_duplicate_and_cycle_folders_skipped(self):
        drive = FakeDrive({'base': [
            file('a', 'Album', FOLDER_MIME), file('b', 'ALBUM', FOLDER_MIME),
            file('bad', '../bad', FOLDER_MIME), file('base', 'Cycle', FOLDER_MIME),
        ]})
        descriptions, warnings = scrape_descriptions(drive, 'base')
        self.assertEqual(descriptions, {'': ''})
        self.assertEqual(len(warnings), 3)
        self.assertEqual(drive.visited, ['base'])

    def test_api_failure_produces_no_output(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'tree'
            with patch.dict(os.environ, {'GOOGLE_DRIVE_API_KEY': 'test'}):
                with patch('gdrive_scripts.export_folder_descriptions.scrape_descriptions',
                           side_effect=DriveError('HTTP 403')):
                    with redirect_stderr(io.StringIO()):
                        self.assertEqual(main(['--root-folder-id', 'base', '--output', str(output)]), 1)
            self.assertFalse(output.exists())

    def test_main_default_location_and_resource_key(self):
        self.assertEqual(DEFAULT_OUTPUT, Path(__file__).resolve().parent / 'output' / 'folder_descriptions')
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'nested' / 'tree'
            with patch.dict(os.environ, {
                'GOOGLE_DRIVE_API_KEY': 'test', 'GOOGLE_DRIVE_ROOT_RESOURCE_KEY': 'resource',
            }):
                with patch('gdrive_scripts.export_folder_descriptions.DriveClient') as client:
                    with patch('gdrive_scripts.export_folder_descriptions.scrape_descriptions',
                               return_value=({'': 'Description'}, [])):
                        with redirect_stdout(io.StringIO()):
                            self.assertEqual(main(['--root-folder-id', 'base', '--output', str(output)]), 0)
                client.return_value.set_resource_key.assert_called_once_with('base', 'resource')

    def test_direct_and_module_entry_points(self):
        directory = Path(__file__).resolve().parent
        for cwd, arguments in [
            (directory, ['export_folder_descriptions.py', '--help']),
            (directory.parent, ['-m', 'gdrive_scripts.export_folder_descriptions', '--help']),
        ]:
            result = subprocess.run([sys.executable, *arguments], cwd=cwd, capture_output=True,
                                    text=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == '__main__':
    unittest.main()
