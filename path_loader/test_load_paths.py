import unittest
from unittest.mock import Mock, call, patch

import mysql.connector

with patch.dict('os.environ', {
    'DB_HOST': 'test',
    'DB_USER': 'test',
    'DB_PASSWORD': 'test',
    'DB_NAME': 'test',
}):
    import load_paths


class GalleryTransactionTests(unittest.TestCase):
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
