import io
import mimetypes
import os
import tempfile
from contextlib import closing
from urllib.parse import urlsplit

import boto3
from botocore.config import Config
from botocore.exceptions import ClientError

from file_store import LocalFileStore, validate_key


class LinodeFileStore:
    def __init__(self, client, bucket, prefix='', state_store=None):
        self.client = client
        self.bucket = bucket
        self.prefix = validate_key(prefix)
        self.state_store = state_store

    @classmethod
    def from_environment(cls, state_store):
        required = ('ENDPOINT', 'REGION', 'BUCKET', 'ACCESS_KEY', 'SECRET_KEY')
        settings = {name: os.environ.get(f'LINODE_STORAGE_{name}', '') for name in required}
        if any(not value.strip() for value in settings.values()):
            raise ValueError('Linode storage requires ENDPOINT, REGION, BUCKET, ACCESS_KEY, and SECRET_KEY.')
        endpoint = urlsplit(settings['ENDPOINT'])
        if (endpoint.scheme != 'https' or not endpoint.netloc or endpoint.username or
                endpoint.path not in ('', '/') or endpoint.query or endpoint.fragment):
            raise ValueError('LINODE_STORAGE_ENDPOINT must be an HTTPS S3 endpoint without a bucket or path.')
        if '/' in settings['BUCKET']:
            raise ValueError('LINODE_STORAGE_BUCKET must be a bucket name, not a path.')
        client = boto3.client(
            's3',
            endpoint_url=settings['ENDPOINT'],
            region_name=settings['REGION'],
            aws_access_key_id=settings['ACCESS_KEY'],
            aws_secret_access_key=settings['SECRET_KEY'],
            config=Config(
                signature_version='s3v4',
                s3={'addressing_style': 'path'},
                request_checksum_calculation='when_required',
                response_checksum_validation='when_required',
                retries={'mode': 'standard', 'max_attempts': 3},
            ),
        )
        return cls(client, settings['BUCKET'], os.environ.get('LINODE_STORAGE_PREFIX', ''), state_store)

    def _key(self, area, key='', allow_root=True):
        if area == 'state' or area not in LocalFileStore._areas:
            raise ValueError(f'Unknown object-storage area: {area}')
        relative = validate_key(key, allow_root)
        prefix = f'{self.prefix}/' if self.prefix else ''
        return f'{prefix}{LocalFileStore._areas[area]}/{relative}'

    def _prefix(self, area, key=''):
        return self._key(area, key).rstrip('/') + '/'

    def _objects(self, prefix, delimiter=None):
        arguments = {'Bucket': self.bucket, 'Prefix': prefix}
        if delimiter:
            arguments['Delimiter'] = delimiter
        yield from self.client.get_paginator('list_objects_v2').paginate(**arguments)

    def _metadata(self, key):
        try:
            return self.client.head_object(Bucket=self.bucket, Key=key)
        except ClientError as error:
            if error.response['Error']['Code'] in ('404', 'NoSuchKey', 'NotFound'):
                return None
            raise

    def exists(self, area, key):
        if area == 'state':
            return self.state_store.exists(area, key)
        return self._metadata(self._key(area, key, False)) is not None

    def is_directory(self, area, key=''):
        if area == 'state':
            return self.state_store.is_directory(area, key)
        response = self.client.list_objects_v2(
            Bucket=self.bucket, Prefix=self._prefix(area, key), MaxKeys=1)
        return bool(response.get('Contents'))

    def create_directory(self, area, key=''):
        if area == 'state':
            return self.state_store.create_directory(area, key)
        normalized = validate_key(key)
        if normalized and self.exists(area, normalized):
            raise FileExistsError(f'Directory key resolves to a file: {key}')
        parts = normalized.split('/') if normalized else []
        for length in range(len(parts) + 1):
            parent = '/'.join(parts[:length])
            if parent and self.exists(area, parent):
                raise NotADirectoryError(parent)
            marker = self._prefix(area, parent)
            self.client.put_object(
                Bucket=self.bucket, Key=marker, Body=b'', ContentType='application/x-directory')

    def open_read(self, area, key):
        if area == 'state':
            return self.state_store.open_read(area, key)
        try:
            response = self.client.get_object(Bucket=self.bucket, Key=self._key(area, key, False))
        except ClientError as error:
            if error.response['Error']['Code'] in ('404', 'NoSuchKey', 'NotFound'):
                raise FileNotFoundError(key) from error
            raise
        # Pillow seeks while decoding. Spill larger originals to scratch disk,
        # and always close the HTTP body, including on an interrupted download.
        temporary = tempfile.SpooledTemporaryFile(max_size=1024 * 1024, mode='w+b')
        try:
            with closing(response['Body']) as body:
                for chunk in body.iter_chunks(chunk_size=1024 * 1024):
                    temporary.write(chunk)
            temporary.seek(0)
            return temporary
        except BaseException:
            temporary.close()
            raise

    def read_text(self, area, key):
        with self.open_read(area, key) as stream:
            return stream.read().decode('utf-8-sig')

    def write(self, area, key, content, overwrite=True):
        if area == 'state':
            return self.state_store.write(area, key, content, overwrite)
        object_key = self._key(area, key, False)
        if self.is_directory(area, key):
            raise IsADirectoryError(key)
        self.create_directory(area, key.rpartition('/')[0])
        body = content if hasattr(content, 'read') else io.BytesIO(content)
        arguments = {
            'Bucket': self.bucket, 'Key': object_key, 'Body': body,
            'ContentType': mimetypes.guess_type(key)[0] or 'application/octet-stream',
        }
        if not overwrite:
            arguments['IfNoneMatch'] = '*'
        try:
            self.client.put_object(**arguments)
        except ClientError as error:
            if error.response['Error']['Code'] in ('412', 'PreconditionFailed'):
                raise FileExistsError(key) from error
            raise

    def move_file(self, area, source_key, destination_key):
        if area == 'state':
            return self.state_store.move_file(area, source_key, destination_key)
        source = self._key(area, source_key, False)
        destination = self._key(area, destination_key, False)
        original = self._metadata(source)
        if original is None:
            raise FileNotFoundError(source_key)
        if self.is_directory(area, source_key):
            raise IsADirectoryError(source_key)
        if self.exists(area, destination_key) or self.is_directory(area, destination_key):
            raise FileExistsError(destination_key)
        self.create_directory(area, destination_key.rpartition('/')[0])
        self.client.copy_object(
            Bucket=self.bucket, Key=destination,
            CopySource={'Bucket': self.bucket, 'Key': source},
            CopySourceIfMatch=original['ETag'],
        )
        copied = self._metadata(destination)
        if (copied is None or copied['ContentLength'] != original['ContentLength'] or
                copied['ETag'] != original['ETag']):
            raise OSError(f'Move verification failed: {destination_key}. Source retained.')
        self.client.delete_object(Bucket=self.bucket, Key=source)

    def delete_file(self, area, key):
        if area == 'state':
            return self.state_store.delete_file(area, key)
        object_key = self._key(area, key, False)
        if self.is_directory(area, key):
            raise IsADirectoryError(key)
        self.client.delete_object(Bucket=self.bucket, Key=object_key)

    def delete_directory(self, area, key, recursive=False):
        if area == 'state':
            return self.state_store.delete_directory(area, key, recursive)
        validate_key(key, False)
        if self.exists(area, key.rstrip('/')):
            raise NotADirectoryError(key)
        prefix = self._prefix(area, key)
        keys = [item['Key'] for page in self._objects(prefix) for item in page.get('Contents', [])]
        if not recursive and any(item != prefix for item in keys):
            raise OSError(f'Directory is not empty: {key}')
        for object_key in keys:
            self.client.delete_object(Bucket=self.bucket, Key=object_key)

    def list_files(self, area, directory_key=''):
        if area == 'state':
            return self.state_store.list_files(area, directory_key)
        prefix = self._prefix(area, directory_key)
        return sorted(
            item['Key'][len(prefix):]
            for page in self._objects(prefix, '/')
            for item in page.get('Contents', [])
            if not item['Key'].endswith('/')
        )

    def walk(self, area):
        if area == 'state':
            yield from self.state_store.walk(area)
            return
        if not self.is_directory(area):
            return

        def visit(key):
            prefix = self._prefix(area, key)
            directories = []
            files = []
            for page in self._objects(prefix, '/'):
                directories.extend(
                    entry['Prefix'][len(prefix):].rstrip('/')
                    for entry in page.get('CommonPrefixes', []))
                files.extend(
                    entry['Key'][len(prefix):]
                    for entry in page.get('Contents', [])
                    if not entry['Key'].endswith('/'))
            directories.sort()
            files.sort()
            yield key, directories, files
            # The caller prunes directories in-place, just as with os.walk.
            for directory in directories:
                yield from visit(f'{key}/{directory}' if key else directory)

        yield from visit('')
