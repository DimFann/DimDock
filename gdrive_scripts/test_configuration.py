import os
import unittest
from unittest.mock import patch
from pathlib import Path

from gdrive_scripts.configuration import drive_settings


class ConfigurationTests(unittest.TestCase):
    def test_repository_env_and_environment_precedence(self):
        with patch.dict(os.environ, {'GOOGLE_DRIVE_API_KEY': ' override '}, clear=True):
            with patch('gdrive_scripts.configuration.Path.exists', return_value=True):
                with patch('gdrive_scripts.configuration.read_settings', return_value={
                    'GOOGLE_DRIVE_API_KEY': 'file-key', 'GOOGLE_DRIVE_ROOT_RESOURCE_KEY': ' file-resource ',
                    'UNRELATED': 'ignored',
                }) as reader:
                    self.assertEqual(drive_settings(), {
                        'GOOGLE_DRIVE_API_KEY': 'override',
                        'GOOGLE_DRIVE_ROOT_RESOURCE_KEY': 'file-resource',
                    })
                    self.assertEqual(reader.call_args.args[0].name, '.env')
                    self.assertEqual(reader.call_args.args[0].parent, Path(__file__).resolve().parent.parent)

    def test_missing_env_is_allowed(self):
        with patch.dict(os.environ, {}, clear=True):
            with patch('gdrive_scripts.configuration.Path.exists', return_value=False):
                self.assertEqual(drive_settings(), {
                    'GOOGLE_DRIVE_API_KEY': '', 'GOOGLE_DRIVE_ROOT_RESOURCE_KEY': '',
                })

    def test_invalid_env_errors_propagate(self):
        with patch('gdrive_scripts.configuration.Path.exists', return_value=True):
            with patch('gdrive_scripts.configuration.read_settings', side_effect=ValueError('invalid assignment')):
                with self.assertRaises(ValueError):
                    drive_settings()


if __name__ == '__main__':
    unittest.main()
