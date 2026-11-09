import unittest
import json
from unittest.mock import Mock, call, patch

import mysql.connector

with patch.dict('os.environ', {
    'DB_HOST': 'test',
    'DB_USER': 'test',
    'DB_PASSWORD': 'test',
    'DB_NAME': 'test',
}):
    import load_paths


class AdminTagInitializationTests(unittest.TestCase):
    def test_missing_configuration_is_created_with_hidden_tag(self):
        store = Mock()
        store.read_text.side_effect = [FileNotFoundError(), '{"admin-only-tags":["hidden"]}']
        with patch.object(load_paths, 'STORE', store):
            load_paths.initialize_admin_only_tags()
        args = store.write.call_args
        self.assertEqual(args.args[:2], ('admin', 'admin-only-tags.json'))
        self.assertEqual(json.loads(args.args[2]), {'admin-only-tags': ['hidden']})
        self.assertEqual(args.kwargs, {'overwrite': False})

    def test_existing_configuration_is_preserved(self):
        store = Mock()
        store.read_text.return_value = '{"admin-only-tags":["draft","hidden"]}'
        with patch.object(load_paths, 'STORE', store):
            load_paths.initialize_admin_only_tags()
        store.write.assert_not_called()

    def test_concurrent_creation_is_preserved(self):
        store = Mock()
        store.read_text.side_effect = [FileNotFoundError(), '{"admin-only-tags":["draft"]}']
        store.write.side_effect = FileExistsError()
        with patch.object(load_paths, 'STORE', store):
            load_paths.initialize_admin_only_tags()
        self.assertEqual(store.read_text.call_count, 2)
        self.assertEqual(store.write.call_count, 1)

    def test_read_and_creation_failures_are_reported(self):
        for missing in (False, True):
            store = Mock()
            if missing:
                store.read_text.side_effect = FileNotFoundError()
                store.write.side_effect = PermissionError('denied')
            else:
                store.read_text.side_effect = PermissionError('denied')
            with patch.object(load_paths, 'STORE', store), self.assertRaises(RuntimeError):
                load_paths.initialize_admin_only_tags()


class AboutInitializationTests(unittest.TestCase):
    def test_missing_about_is_created_and_imported(self):
        store = Mock()
        store.read_text.side_effect = [FileNotFoundError(), '']
        cursor = Mock()
        with patch.object(load_paths, 'STORE', store):
            load_paths.process_about(cursor)
        store.write.assert_called_once_with('about', 'about.md', b'', overwrite=False)
        self.assertEqual(cursor.execute.call_args.args[1], ('',))

    def test_existing_about_is_not_overwritten(self):
        store = Mock()
        store.read_text.return_value = 'Existing About'
        cursor = Mock()
        with patch.object(load_paths, 'STORE', store):
            load_paths.process_about(cursor)
        store.write.assert_not_called()
        self.assertEqual(cursor.execute.call_args.args[1], ('Existing About',))

    def test_concurrent_creation_imports_other_writers_content(self):
        store = Mock()
        store.read_text.side_effect = [FileNotFoundError(), 'Concurrent About']
        store.write.side_effect = FileExistsError()
        cursor = Mock()
        with patch.object(load_paths, 'STORE', store):
            load_paths.process_about(cursor)
        self.assertEqual(cursor.execute.call_args.args[1], ('Concurrent About',))

    def test_read_and_creation_failures_do_not_import_empty_content(self):
        for failure in (PermissionError('denied'), OSError('network failure')):
            for missing in (False, True):
                store = Mock()
                if missing:
                    store.read_text.side_effect = FileNotFoundError()
                    store.write.side_effect = failure
                else:
                    store.read_text.side_effect = failure
                cursor = Mock()
                with patch.object(load_paths, 'STORE', store), self.assertRaises(RuntimeError):
                    load_paths.process_about(cursor)
                cursor.execute.assert_not_called()


class GalleryTransactionTests(unittest.TestCase):
    def test_statistics_print_once_after_all_folders_finish(self):
        cursor = Mock()
        cursor.fetchall.side_effect = [
            [('root', 1, None), ('Album', 2, None)], [], [], [],
        ]
        store = Mock()
        store.walk.return_value = iter([('', ['Album'], []), ('Album', [], [])])
        store.list_files.return_value = []
        store.exists.return_value = False
        with (
            patch.object(load_paths, 'STORE', store),
            patch.object(load_paths, 'get_entity_tags', return_value=set()),
            self.assertLogs(level='INFO') as logs,
        ):
            load_paths.traverse_and_insert(cursor)
        headers = [index for index, line in enumerate(logs.output) if 'Gallery statistics (' in line]
        finishes = [index for index, line in enumerate(logs.output) if 'Finished processing folder:' in line]
        self.assertEqual(len(headers), 1)
        self.assertEqual(len(finishes), 2)
        self.assertGreater(headers[0], max(finishes))

    def test_statistics_mean_median_and_normalized_rates(self):
        with self.assertLogs(level='INFO') as logs:
            load_paths.log_scan_statistics({
                'files': ('file', [(0.010, 2), (0.030, 3), (0.020, 0)]),
                'thumbnails': ('task', [(0.001, 0)]),
                'empty': ('folder', []),
            })
        text = '\n'.join(logs.output)
        self.assertIn('mean=20.000ms/folder median=20.000ms/folder', text)
        self.assertIn('pooled=12.000ms/file', text)
        self.assertIn('normalized_mean=7.500ms/file normalized_median=7.500ms/file', text)
        self.assertIn('rate=83.3 file/s', text)
        self.assertIn('normalized time/rate=N/A', text)
        self.assertIn('no folders measured', text)

    def test_unchanged_item_tags_do_not_issue_redundant_tag_inserts(self):
        cursor = Mock()
        path = '2020/2020-02'
        name = 'image.png'
        key = load_paths.create_thumbnail_key(path, name)
        tags = load_paths.date_path_tags(path)
        cursor.fetchall.side_effect = [
            [(path, 1, None)], [(2, path, name, key, 'image')],
            [(2, tag) for tag in tags],
        ]
        store = Mock()
        store.walk.return_value = iter([(path, [], [name])])
        store.list_files.return_value = [f'{key}.png']
        store.exists.return_value = False
        with (
            patch.object(load_paths, 'STORE', store),
            patch.object(load_paths, 'get_entity_tags', return_value=set()),
            patch.object(load_paths, 'replace_entity_tags') as replace_tags,
        ):
            self.assertEqual(load_paths.traverse_and_insert(cursor), 0)
        replace_tags.assert_not_called()
        self.assertFalse(any('INSERT' in call.args[0] for call in cursor.execute.call_args_list))
        store.list_files.assert_called_once_with('thumbnails')
        self.assertFalse(any(call.args[0] == 'thumbnails' for call in store.exists.call_args_list))

    def test_thumbnail_snapshot_still_generates_missing_images(self):
        cursor = Mock()
        path, name = '2020', 'missing.png'
        key = load_paths.create_thumbnail_key(path, name)
        cursor.fetchall.side_effect = [
            [(path, 1, None)], [(2, path, name, key, 'image')],
            [(2, tag) for tag in load_paths.date_path_tags(path)],
        ]
        store = Mock()
        store.walk.return_value = iter([(path, [], [name])])
        store.list_files.return_value = []
        store.exists.return_value = False
        with (
            patch.object(load_paths, 'STORE', store),
            patch.object(load_paths, 'get_entity_tags', return_value=set()),
            patch.object(load_paths, 'process_image') as image,
        ):
            load_paths.traverse_and_insert(cursor)
        image.assert_called_once_with(f'{path}/{name}', key)

    def test_thumbnail_listing_failure_aborts_instead_of_rebuilding_all(self):
        store, cursor = Mock(), Mock()
        store.list_files.side_effect = PermissionError('denied')
        with patch.object(load_paths, 'STORE', store), self.assertRaises(PermissionError):
            load_paths.traverse_and_insert(cursor)
        cursor.execute.assert_not_called()

    def test_folder_timings_include_listing_and_no_thumbnail_scan(self):
        cursor = Mock()
        cursor.fetchall.side_effect = [[('root', 1, None)], [], []]
        store = Mock()
        store.walk.return_value = iter([('', [], [])])
        store.list_files.return_value = []
        store.exists.return_value = False
        with (
            patch.object(load_paths, 'STORE', store),
            patch.object(load_paths, 'get_entity_tags', return_value=set()),
            self.assertLogs(level='INFO') as logs,
        ):
            self.assertEqual(load_paths.traverse_and_insert(cursor), 0)
        for stage in ('listing=', 'metadata=', 'path_db=', 'files/thumbnail_checks=',
                      'tags_db=', 'thumbnail_tasks=', 'Gallery timing: 1 folders',
                      'Gallery statistics', 'Processing folder: root', 'Finished processing folder: root'):
            self.assertTrue(any(stage in line for line in logs.output), stage)
        self.assertFalse(any('folder_total=' in line or 'thumbnails queued' in line for line in logs.output))

    def test_startup_stages_are_logged(self):
        connection = Mock()
        with (
            patch.object(load_paths, 'STORE'),
            patch.object(load_paths.mysql.connector, 'connect', return_value=connection),
            patch.object(load_paths, 'initialize_admin_only_tags'),
            patch.object(load_paths, 'traverse_and_insert', return_value=2),
            patch.object(load_paths, 'process_blogs', return_value=0),
            patch.object(load_paths, 'process_about'),
            self.assertLogs(level='INFO') as logs,
        ):
            load_paths.main()
        for stage in ('Starting path-loader', 'Ensuring storage area', 'Connecting to gallery database',
                      'Starting gallery scan', 'Gallery scan committed', 'Checking blog imports',
                      'Importing About content', 'Finished scanning'):
            self.assertTrue(any(stage in line for line in logs.output), stage)

    def run_loader(self, scan_results, commit_results=None):
        connection = Mock()
        connection.commit.side_effect = commit_results
        scan = Mock(side_effect=scan_results)
        with (
            patch.object(load_paths, 'STORE'),
            patch.object(load_paths.mysql.connector, 'connect', return_value=connection),
            patch.object(load_paths, 'traverse_and_insert', scan),
            patch.object(load_paths, 'process_blogs', return_value=0) as blogs,
            patch.object(load_paths, 'process_about') as about,
            patch.object(load_paths.time, 'sleep') as sleep,
            patch.object(load_paths, 'logging'),
        ):
            error = None
            try:
                load_paths.main()
            except mysql.connector.Error as exception:
                error = exception
        return connection, scan, blogs, about, sleep, error

    def test_success_without_retry(self):
        connection, scan, blogs, about, sleep, error = self.run_loader([1])
        self.assertIsNone(error)
        scan.assert_called_once_with(connection.cursor.return_value)
        self.assertEqual(connection.commit.call_count, 2)
        connection.rollback.assert_not_called()
        blogs.assert_called_once()
        about.assert_called_once()
        sleep.assert_not_called()
        connection.close.assert_called_once()
        connection.cursor.return_value.close.assert_called_once()

    def test_transient_errors_restart_the_whole_scan(self):
        for errno in (1020, 1205, 1213):
            with self.subTest(errno=errno):
                conflict = mysql.connector.DatabaseError('conflict', errno=errno)
                connection, scan, blogs, _, sleep, error = self.run_loader([conflict, 1])
                self.assertIsNone(error)
                self.assertEqual(scan.call_count, 2)
                connection.rollback.assert_called_once()
                sleep.assert_called_once_with(1)
                blogs.assert_called_once()
                self.assertEqual(connection.commit.call_count, 2)

    def test_retry_limit_fails_without_importing_other_content(self):
        conflict = mysql.connector.DatabaseError('conflict', errno=1020)
        connection, scan, blogs, about, sleep, error = self.run_loader([conflict] * 3)
        self.assertIs(error, conflict)
        self.assertEqual(scan.call_count, 3)
        self.assertEqual(connection.rollback.call_count, 3)
        self.assertEqual(sleep.call_args_list, [call(1), call(2)])
        connection.commit.assert_not_called()
        blogs.assert_not_called()
        about.assert_not_called()
        connection.close.assert_called_once()

    def test_nontransient_errors_are_not_retried(self):
        failure = mysql.connector.DatabaseError('invalid SQL', errno=1064)
        connection, scan, blogs, _, sleep, error = self.run_loader([failure])
        self.assertIs(error, failure)
        scan.assert_called_once()
        connection.rollback.assert_called_once()
        blogs.assert_not_called()
        sleep.assert_not_called()

    def test_commit_conflict_also_restarts_the_scan(self):
        conflict = mysql.connector.DatabaseError('deadlock', errno=1213)
        connection, scan, _, _, sleep, error = self.run_loader(
            [1, 1], [conflict, None, None])
        self.assertIsNone(error)
        self.assertEqual(scan.call_count, 2)
        connection.rollback.assert_called_once()
        self.assertEqual(connection.commit.call_count, 3)
        sleep.assert_called_once_with(1)


if __name__ == '__main__':
    unittest.main()
