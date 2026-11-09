import json
import os
import unittest
from unittest.mock import Mock, patch

with patch.dict(os.environ, {
    'FILE_STORAGE_PROVIDER': 'Local',
    'DB_HOST': 'test', 'DB_USER': 'test', 'DB_PASSWORD': 'test', 'DB_NAME': 'test',
}):
    import build_shortcuts


class DirectoryShortcutImportTests(unittest.TestCase):
    def test_loading_progress_and_directory_count_are_logged(self):
        store, cursor = Mock(), Mock()
        store.exists.return_value = True
        store.read_text.return_value = '{"directory_shortcuts":{}}'
        with patch.object(build_shortcuts, 'STORE', store), self.assertLogs(level='INFO') as logs:
            build_shortcuts.process_shortcuts(cursor, directories_only=True)
        self.assertTrue(any('Loading directory shortcuts' in line for line in logs.output))
        self.assertTrue(any('Loaded 0 directory shortcuts' in line for line in logs.output))

    def test_missing_manifest_is_logged(self):
        store, cursor = Mock(), Mock()
        store.exists.return_value = False
        with patch.object(build_shortcuts, 'STORE', store), self.assertLogs(level='INFO') as logs:
            build_shortcuts.process_shortcuts(cursor)
        self.assertTrue(any('skipping shortcut processing' in line for line in logs.output))

    def test_resolves_root_and_nested_paths(self):
        cursor = Mock()
        cursor.fetchone.side_effect = [(1,), (2,), (2,), (1,)]
        build_shortcuts.process_directory_shortcuts(cursor, {'directory_shortcuts': {
            'root': [{'name': 'Alias', 'target_path': 'Album'}],
            'Album': [{'name': 'Home', 'target_path': 'root'}],
        }})
        self.assertEqual([call.args[1] for call in cursor.execute.call_args_list if 'INSERT' in call.args[0]],
                         [(2, 1, 'Alias'), (1, 2, 'Home')])
        self.assertEqual(sum('DELETE' in call.args[0] for call in cursor.execute.call_args_list), 1)

    def test_invalid_data_does_not_delete_existing_index(self):
        cases = [
            {'directory_shortcuts': []},
            {'directory_shortcuts': {'root': 'invalid'}},
            {'directory_shortcuts': {'root': [{'name': '../bad', 'target_path': 'Album'}]}},
            {'directory_shortcuts': {'root': [{'name': 'Alias', 'target_path': '../bad'}]}},
            {'directory_shortcuts': {'root': [
                {'name': 'Alias', 'target_path': 'Album'}, {'name': 'alias', 'target_path': 'Album'}]}},
        ]
        for data in cases:
            cursor = Mock()
            cursor.fetchone.return_value = (1,)
            with self.subTest(data=data), self.assertRaises(ValueError):
                build_shortcuts.process_directory_shortcuts(cursor, data)
            self.assertFalse(any('DELETE' in call.args[0] for call in cursor.execute.call_args_list))

    def test_missing_source_or_destination_aborts_before_deletion(self):
        data = {'directory_shortcuts': {'root': [{'name': 'Alias', 'target_path': 'Missing'}]}}
        for results in ([None], [(1,), None]):
            cursor = Mock()
            cursor.fetchone.side_effect = results
            with self.subTest(results=results), self.assertRaisesRegex(ValueError, 'not indexed'):
                build_shortcuts.process_directory_shortcuts(cursor, data)
            self.assertFalse(any('DELETE' in call.args[0] for call in cursor.execute.call_args_list))

    def test_directories_only_does_not_insert_file_shortcuts(self):
        cursor = Mock()
        cursor.fetchone.side_effect = [(1,), (2,)]
        store = Mock()
        store.exists.return_value = True
        store.read_text.return_value = json.dumps({
            'shortcuts': {'root': ['Album/image.png']},
            'directory_shortcuts': {'root': [{'name': 'Alias', 'target_path': 'Album'}]},
        })
        with patch.object(build_shortcuts, 'STORE', store):
            build_shortcuts.process_shortcuts(cursor, directories_only=True)
        self.assertTrue('CREATE TABLE IF NOT EXISTS' in cursor.execute.call_args_list[0].args[0])
        self.assertFalse(any('FROM Items' in call.args[0] for call in cursor.execute.call_args_list))

    def test_missing_manifest_still_upgrades_schema_without_clearing_links(self):
        store, cursor = Mock(), Mock()
        store.exists.return_value = False
        with patch.object(build_shortcuts, 'STORE', store):
            build_shortcuts.process_shortcuts(cursor, directories_only=True)
        self.assertEqual(cursor.execute.call_count, 1)
        self.assertIn('CREATE TABLE IF NOT EXISTS DirectoryShortcuts', cursor.execute.call_args.args[0])


if __name__ == '__main__':
    unittest.main()
