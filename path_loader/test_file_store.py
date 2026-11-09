import io
import tempfile
import unittest
from contextlib import redirect_stdout
from unittest.mock import patch

import boto3
from botocore.config import Config
from botocore.exceptions import ClientError
from moto import mock_aws

from file_store import LocalFileStore, create_file_store
from linode_file_store import LinodeFileStore
import storage_smoke
import storage_clean


class LocalProviderIsolationTests(unittest.TestCase):
    def test_local_storage_never_creates_s3_client_or_uses_network(self):
        with tempfile.TemporaryDirectory() as root:
            with (
                patch.dict('os.environ', {
                    'FILE_STORAGE_PROVIDER': 'Local', 'DIMDOCK_DATA_ROOT': root,
                    'LINODE_STORAGE_ENDPOINT': 'https://example.invalid',
                    'LINODE_STORAGE_BUCKET': 'unused',
                }),
                patch('boto3.client', side_effect=AssertionError('Local must not create S3 clients')) as client,
                patch('socket.socket.connect', side_effect=AssertionError('Local must not use network')) as connect,
            ):
                store = create_file_store()
                self.assertIsInstance(store, LocalFileStore)
                for area in ('gallery', 'thumbnails', 'blogs', 'about', 'admin', 'state'):
                    store.create_directory(area)
                    store.write(area, 'sample.txt', b'content')
                    self.assertTrue(store.exists(area, 'sample.txt'))
                    self.assertEqual(store.read_text(area, 'sample.txt'), 'content')
                    self.assertEqual(store.list_files(area), ['sample.txt'])
                    self.assertTrue(list(store.walk(area)))
                    store.move_file(area, 'sample.txt', 'renamed.txt')
                    store.delete_file(area, 'renamed.txt')
                client.assert_not_called()
                connect.assert_not_called()


@mock_aws
class FileStoreTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.client = boto3.client('s3', region_name='us-east-1', config=Config(
            request_checksum_calculation='when_required', response_checksum_validation='when_required'))
        self.client.create_bucket(Bucket='dimdock-tests')
        self.local = LocalFileStore(self.temporary.name)
        self.remote = LinodeFileStore(self.client, 'dimdock-tests', 'deployment', self.local)

    def test_local_and_remote_content_parity(self):
        snapshots = []
        for store in (self.local, self.remote):
            store.create_directory('gallery', '2023/2023-01/empty')
            store.write('gallery', '2023/2023-01/Mixed café.png', b'image')
            store.write('gallery', '2023/2023-01/.dimdock.json', b'{"version":1}')
            store.write('gallery', '2023/2023-01/.description.md', b'description')
            store.write('gallery', 'root.txt', b'root')
            snapshot = [(key, sorted(dirs), sorted(files)) for key, dirs, files in store.walk('gallery')]
            snapshots.append(snapshot)
            self.assertEqual(store.read_text('gallery', '2023/2023-01/.description.md'), 'description')
            with self.assertRaises(FileExistsError):
                store.write('gallery', 'root.txt', b'replacement', overwrite=False)
            self.assertEqual(store.read_text('gallery', 'root.txt'), 'root')
            store.move_file('gallery', 'root.txt', 'moved.txt')
            self.assertFalse(store.exists('gallery', 'root.txt'))
            self.assertEqual(store.read_text('gallery', 'moved.txt'), 'root')
            store.delete_file('gallery', 'missing.txt')
        self.assertEqual(snapshots[0], snapshots[1])

    def test_key_validation_is_shared(self):
        for key in ('../escape', '/absolute', 'x\\y', 'a//b', './x', 'a:b', 'a/\x7f'):
            for store in (self.local, self.remote):
                with self.subTest(key=key, store=type(store).__name__), self.assertRaises(ValueError):
                    store.write('gallery', key, b'invalid')

    def test_paginated_listing_and_prunable_walk(self):
        for number in range(1005):
            self.client.put_object(Bucket='dimdock-tests', Key=f'deployment/img_src/nested/{number:04}.txt', Body=b'x')
        self.remote.create_directory('gallery', '.hidden')
        self.assertEqual(len(self.remote.list_files('gallery', 'nested')), 1005)
        walked = self.remote.walk('gallery')
        key, dirs, _ = next(walked)
        self.assertEqual(key, '')
        dirs.remove('.hidden')
        self.assertEqual([(key, len(files)) for key, _, files in walked], [('nested', 1005)])

    def test_file_directory_conflicts(self):
        for store in (self.local, self.remote):
            store.write('gallery', 'file', b'file')
            with self.assertRaises((OSError, ValueError)):
                store.write('gallery', 'file/child', b'child')
            store.create_directory('gallery', 'directory')
            with self.assertRaises((OSError, ValueError)):
                store.write('gallery', 'directory', b'file')
            with self.assertRaises((OSError, ValueError)):
                store.delete_file('gallery', 'directory')

    def test_copy_failure_preserves_source(self):
        self.remote.write('gallery', 'source.txt', b'source')
        failure = ClientError({'Error': {'Code': 'AccessDenied'}}, 'CopyObject')
        with patch.object(self.client, 'copy_object', side_effect=failure):
            with self.assertRaises(ClientError):
                self.remote.move_file('gallery', 'source.txt', 'destination.txt')
        self.assertEqual(self.remote.read_text('gallery', 'source.txt'), 'source')
        self.assertFalse(self.remote.exists('gallery', 'destination.txt'))

    def test_delete_failure_leaves_verified_copy(self):
        self.remote.write('gallery', 'source.txt', b'source')
        failure = ClientError({'Error': {'Code': 'AccessDenied'}}, 'DeleteObject')
        with patch.object(self.client, 'delete_object', side_effect=failure):
            with self.assertRaises(ClientError):
                self.remote.move_file('gallery', 'source.txt', 'destination.txt')
        self.assertEqual(self.remote.read_text('gallery', 'source.txt'), 'source')
        self.assertEqual(self.remote.read_text('gallery', 'destination.txt'), 'source')

    def test_permission_errors_are_not_missing_files(self):
        failure = ClientError({'Error': {'Code': 'AccessDenied'}}, 'HeadObject')
        with patch.object(self.client, 'head_object', side_effect=failure):
            with self.assertRaises(ClientError):
                self.remote.exists('gallery', 'file.txt')

    def test_stream_read_and_content_type(self):
        payload = b'12345678' * (1024 * 1024)
        self.remote.write('gallery', 'large.png', io.BytesIO(payload))
        with self.remote.open_read('gallery', 'large.png') as stream:
            stream.seek(4)
            self.assertEqual(stream.read(4), payload[4:8])
            stream.seek(0)
            self.assertEqual(stream.read(), payload)
        head = self.client.head_object(Bucket='dimdock-tests', Key='deployment/img_src/large.png')
        self.assertEqual(head['ContentType'], 'image/png')

    def test_runtime_state_never_enters_bucket(self):
        self.remote.write('state', '.initialized', b'')
        self.assertTrue(self.local.exists('state', '.initialized'))
        self.assertEqual(self.client.list_objects_v2(Bucket='dimdock-tests').get('KeyCount'), 0)

    def test_delete_directory_is_prefix_bounded(self):
        self.remote.write('gallery', 'a/nested/file.txt', b'a')
        self.remote.write('gallery', 'ab/file.txt', b'ab')
        with self.assertRaises(OSError):
            self.remote.delete_directory('gallery', 'a')
        self.remote.delete_directory('gallery', 'a', recursive=True)
        self.assertFalse(self.remote.is_directory('gallery', 'a'))
        self.assertEqual(self.remote.read_text('gallery', 'ab/file.txt'), 'ab')
        with self.assertRaises(ValueError):
            self.remote.delete_directory('gallery', '', recursive=True)

    def test_provider_selection_rejects_bad_config(self):
        with patch.dict('os.environ', {'FILE_STORAGE_PROVIDER': 'typo'}):
            with self.assertRaises(ValueError):
                create_file_store()
        with patch.dict('os.environ', {'FILE_STORAGE_PROVIDER': 'Linode', 'LINODE_STORAGE_ENDPOINT': ''}):
            with self.assertRaises(ValueError):
                create_file_store()

    def test_live_smoke_command_is_scoped_and_cleans_its_objects(self):
        self.remote.write('gallery', 'existing.txt', b'untouched')
        with patch.object(storage_smoke, 'create_file_store', return_value=self.remote), redirect_stdout(io.StringIO()):
            storage_smoke.main()
        self.assertEqual(self.remote.read_text('gallery', 'existing.txt'), 'untouched')
        for page in self.remote._objects('deployment/'):
            self.assertFalse(any('.dimdock-smoke-' in item['Key'] for item in page.get('Contents', [])))

    def test_text_encoding_parity(self):
        for store in (self.local, self.remote):
            store.write('gallery', 'bom.txt', b'\xef\xbb\xbftext')
            self.assertEqual(store.read_text('gallery', 'bom.txt'), 'text')

    def test_remote_cleanup_only_deletes_deployment_thumbnails(self):
        self.remote.write('thumbnails', 'generated.png', b'thumb')
        self.remote.write('gallery', 'original.png', b'original')
        self.client.put_object(Bucket='dimdock-tests', Key='another/img_db_thumbs/keep.png', Body=b'keep')
        with redirect_stdout(io.StringIO()):
            storage_clean.clean_thumbnails(self.remote, dry_run=True)
        self.assertTrue(self.remote.exists('thumbnails', 'generated.png'))
        with redirect_stdout(io.StringIO()):
            storage_clean.clean_thumbnails(self.remote)
        self.assertFalse(self.remote.exists('thumbnails', 'generated.png'))
        self.assertTrue(self.remote.exists('gallery', 'original.png'))
        self.assertIsNotNone(self.remote._metadata('another/img_db_thumbs/keep.png'))


if __name__ == '__main__':
    unittest.main()
