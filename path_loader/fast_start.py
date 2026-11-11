import os
import time

import mysql.connector

from file_store import LocalFileStore


REQUIRED_MARKERS = ('.initialized', '.blogs_initialized', '.shortcuts_initialized')
REQUIRED_TABLES = (
    'Paths', 'Items', 'Tags', 'ItemTags', 'PathTags', 'Shortcuts',
    'DirectoryShortcuts', 'Blogs', 'About',
)


def check_initialized():
    store = LocalFileStore(os.environ.get('DIMDOCK_DATA_ROOT', '/data'))
    for marker in REQUIRED_MARKERS:
        if not store.exists('state', marker):
            raise RuntimeError(
                f'Fast startup requires completed initialization ({marker} is missing). '
                'Run again with PATH_LOADER_FAST=false.'
            )

    connection = None
    try:
        for attempt in range(30):
            try:
                connection = mysql.connector.connect(
                    host=os.environ['DB_HOST'],
                    port=int(os.environ.get('DB_PORT', '3306')),
                    user=os.environ['DB_USER'],
                    password=os.environ['DB_PASSWORD'],
                    database=os.environ['DB_NAME'],
                    connection_timeout=5,
                )
                break
            except mysql.connector.Error as error:
                if error.errno not in (2002, 2003, 2013) or attempt == 29:
                    raise
                print('Waiting for database connection...', flush=True)
                time.sleep(2)
        with connection.cursor() as cursor:
            for table in REQUIRED_TABLES:
                cursor.execute(f'SELECT 1 FROM `{table}` LIMIT 0')
                cursor.fetchall()
            cursor.execute("SELECT id FROM Paths WHERE path = 'root' LIMIT 1")
            if cursor.fetchone() is None:
                raise RuntimeError('The gallery root has not been initialized.')
    except (mysql.connector.Error, RuntimeError) as error:
        raise RuntimeError(
            'Fast startup requires an available, initialized database. '
            'Run again with PATH_LOADER_FAST=false if setup or a schema update is needed.'
        ) from error
    finally:
        if connection is not None:
            connection.close()


if __name__ == '__main__':
    check_initialized()
    print('Fast startup: database ready; skipping gallery, thumbnails, blog/About imports and shortcuts.', flush=True)
