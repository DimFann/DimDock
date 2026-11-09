import io
import os
import tempfile
import unittest
import uuid
from pathlib import Path
from unittest.mock import patch

import boto3
import mysql.connector
from moto import mock_aws
from PIL import Image

from file_store import LocalFileStore
from linode_file_store import LinodeFileStore

with patch.dict(os.environ, {
    'FILE_STORAGE_PROVIDER': 'Local',
    'DB_HOST': 'test',
    'DB_USER': 'test',
    'DB_PASSWORD': 'test',
    'DB_NAME': 'test',
}):
    import load_paths
    import build_shortcuts


@unittest.skipUnless(os.environ.get('STORAGE_TEST_DB_HOST'), 'requires disposable MariaDB test service')
@mock_aws
class IndexingParityTests(unittest.TestCase):
    def test_mixed_fixture_indexes_equivalently(self):
        connection = mysql.connector.connect(
            host=os.environ['STORAGE_TEST_DB_HOST'],
            user='root',
            password=os.environ['STORAGE_TEST_DB_PASSWORD'],
        )
        cursor = connection.cursor()
        names = [f'dimdock_contract_{uuid.uuid4().hex}' for _ in range(2)]
        with tempfile.TemporaryDirectory() as root:
            local = LocalFileStore(os.path.join(root, 'local'))
            client = boto3.client('s3', region_name='us-east-1')
            client.create_bucket(Bucket='dimdock-indexing-tests')
            remote = LinodeFileStore(
                client, 'dimdock-indexing-tests', 'deployment',
                LocalFileStore(os.path.join(root, 'state')))
            image_bytes = io.BytesIO()
            Image.new('RGB', (8, 8), (12, 34, 56)).save(image_bytes, format='PNG')
            fixture = {
                ('gallery', '2023/2023-01/Photo café.png'): image_bytes.getvalue(),
                ('gallery', '2023/2023-01/note.txt'): b'example text',
                ('gallery', '2023/2023-01/.description.md'): b'Directory description',
                ('gallery', '2023/2023-01/.dimdock.json'): (
                    b'{"version":1,"type":"comic","tags":["hidden","sample"],'
                    b'"items":{"note.txt":{"tags":["text"],"description":"Item description"}}}'),
                ('gallery', 'WIP_draft/draft.txt'): b'draft',
                ('gallery', '.shortcuts.json'): (
                    b'{"shortcuts":{"root":["2023/2023-01/note.txt"]}}'),
                ('blogs', 'UTC_20230101_120000.md'): b'# Fixture blog\n\nBlog body.',
                ('about', 'about.md'): b'About fixture',
                ('admin', 'admin-only-tags.json'): b'{"admin-only-tags":["hidden"]}',
            }
            snapshots = []
            try:
                for database, store in zip(names, (local, remote)):
                    cursor.execute(f'CREATE DATABASE `{database}` CHARACTER SET utf8mb4')
                    cursor.execute(f'USE `{database}`')
                    schema = Path('/app/01-create-tables.sql').read_text()
                    schema = '\n'.join(line.split('--', 1)[0] for line in schema.splitlines())
                    for statement in schema.split(';'):
                        if statement.strip():
                            cursor.execute(statement)
                    for (area, key), value in fixture.items():
                        store.write(area, key, value)
                    with (
                        patch.object(load_paths, 'STORE', store),
                        patch.object(load_paths, 'DB_HOST', os.environ['STORAGE_TEST_DB_HOST']),
                        patch.object(load_paths, 'DB_USER', 'root'),
                        patch.object(load_paths, 'DB_PASSWORD', os.environ['STORAGE_TEST_DB_PASSWORD']),
                        patch.object(load_paths, 'DB_NAME', database),
                    ):
                        load_paths.main()
                        load_paths.main()
                    with patch.object(build_shortcuts, 'STORE', store):
                        build_shortcuts.process_shortcuts(cursor)
                    connection.commit()
                    queries = (
                        'SELECT path,parent_path,description,type FROM Paths ORDER BY path',
                        'SELECT original_name,parent_path,thumbnail_key,description,created,type FROM Items ORDER BY parent_path,original_name',
                        'SELECT p.path,t.name FROM PathTags pt JOIN Paths p ON p.id=pt.path_id JOIN Tags t ON t.id=pt.tag_id ORDER BY p.path,t.name',
                        'SELECT i.parent_path,i.original_name,t.name FROM ItemTags it JOIN Items i ON i.id=it.item_id JOIN Tags t ON t.id=it.tag_id ORDER BY i.parent_path,i.original_name,t.name',
                        'SELECT original_name,parent_path,shortcut_path FROM Shortcuts ORDER BY original_name',
                        'SELECT source_filename,alias,title,timestamp,`text` FROM Blogs ORDER BY source_filename',
                        'SELECT description FROM About',
                    )
                    snapshot = []
                    for query in queries:
                        cursor.execute(query)
                        snapshot.append(cursor.fetchall())
                    snapshots.append(snapshot)
                    thumbnail_key = load_paths.create_thumbnail_key('2023/2023-01', 'Photo café.png')
                    self.assertTrue(store.exists('thumbnails', f'{thumbnail_key}.png'))
                self.assertEqual(snapshots[0], snapshots[1])
                self.assertEqual(len(snapshots[0][1]), 3)
                self.assertEqual(len(snapshots[0][4]), 1)
            finally:
                for database in names:
                    cursor.execute(f'DROP DATABASE IF EXISTS `{database}`')
                cursor.close()
                connection.close()


if __name__ == '__main__':
    unittest.main()
