import tempfile
from pathlib import Path
import unittest
from unittest.mock import patch

import clean


class CleanupTests(unittest.TestCase):
    def test_local_cleanup_and_dry_run_preserve_content(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            for area in ('db_data', 'app_state', 'img_db_thumbs', 'img_src'):
                (root / area / 'nested').mkdir(parents=True)
                (root / area / 'nested' / 'file').write_text('data')
            clean.clean_contents(root, 'db_data', dry_run=True)
            self.assertTrue((root / 'db_data' / 'nested' / 'file').exists())
            for area in ('db_data', 'app_state', 'img_db_thumbs'):
                clean.clean_contents(root, area)
                self.assertEqual(list((root / area).iterdir()), [])
            self.assertTrue((root / 'img_src' / 'nested' / 'file').exists())
            with self.assertRaises(ValueError):
                clean.clean_contents(root, 'img_src')

    def test_dotenv_parser_never_executes_values(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / '.env'
            path.write_text('FILE_STORAGE_PROVIDER=Linode\nDATA_DIR="some path" # comment\nVALUE=$(do-not-run)\n')
            settings = clean.read_settings(path)
            self.assertEqual(settings['DATA_DIR'], 'some path')
            self.assertEqual(settings['VALUE'], '$(do-not-run)')

    def test_linode_uses_remote_thumbnails_not_local(self):
        with (
            patch.object(clean, 'read_settings', return_value={'FILE_STORAGE_PROVIDER': 'Linode'}),
            patch.object(clean, 'data_root', return_value=Path('test-root')),
            patch.object(clean, 'clean_contents') as local,
            patch.object(clean.subprocess, 'run') as remote,
        ):
            clean.main(['--thumbs', '--dry-run'])
        self.assertEqual([args.args[1] for args in local.call_args_list], ['app_state', 'db_data'])
        self.assertIn('/app/storage_clean.py', remote.call_args.args[0])
        self.assertIn('--dry-run', remote.call_args.args[0])

    def test_remote_failure_does_not_reset_local_state(self):
        with (
            patch.object(clean, 'read_settings', return_value={'FILE_STORAGE_PROVIDER': 'Linode'}),
            patch.object(clean, 'data_root', return_value=Path('test-root')),
            patch.object(clean, 'clean_contents') as local,
            patch.object(clean.subprocess, 'run', side_effect=OSError('failed')),
            self.assertRaises(OSError),
        ):
            clean.main(['--thumbs'])
        local.assert_not_called()


if __name__ == '__main__':
    unittest.main()
