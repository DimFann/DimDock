"""Explicit, isolated read/write smoke test; never changes bucket access policy."""
import uuid

from file_store import create_file_store


def check(condition, message):
    if not condition:
        raise RuntimeError(f'Storage smoke test failed: {message}')


def multipart_check(store, directory):
    """Exercise the S3 multipart calls used by chunked gallery uploads."""
    client, bucket = store.client, store.bucket
    key = store._key('gallery', f'{directory}/multipart.bin')
    parts_data = [b'a' * (5 * 1024 * 1024), b'tail']
    upload = client.create_multipart_upload(Bucket=bucket, Key=key)['UploadId']
    try:
        parts = [
            {'PartNumber': number, 'ETag': client.upload_part(
                Bucket=bucket, Key=key, UploadId=upload, PartNumber=number, Body=data)['ETag']}
            for number, data in enumerate(parts_data, start=1)
        ]
        listed = client.list_parts(Bucket=bucket, Key=key, UploadId=upload).get('Parts', [])
        check([part['PartNumber'] for part in listed] == [1, 2], 'multipart part listing')
        client.complete_multipart_upload(
            Bucket=bucket, Key=key, UploadId=upload, MultipartUpload={'Parts': parts})
        upload = None
        body = client.get_object(Bucket=bucket, Key=key)['Body'].read()
        check(body == b''.join(parts_data), 'multipart assembled bytes')
    finally:
        if upload:
            client.abort_multipart_upload(Bucket=bucket, Key=key, UploadId=upload)

    abandoned = client.create_multipart_upload(Bucket=bucket, Key=key + '.aborted')['UploadId']
    client.upload_part(Bucket=bucket, Key=key + '.aborted', UploadId=abandoned, PartNumber=1, Body=b'x')
    client.abort_multipart_upload(Bucket=bucket, Key=key + '.aborted', UploadId=abandoned)
    check(not store.exists('gallery', f'{directory}/multipart.bin.aborted'), 'aborted multipart stays invisible')
    store.delete_directory('gallery', directory, recursive=True)
    print('PASS gallery: multipart upload/list/complete/abort', flush=True)


def main():
    store = create_file_store()
    directory = f'.dimdock-smoke-{uuid.uuid4().hex}'
    areas = ('gallery', 'thumbnails', 'blogs', 'about', 'admin')
    try:
        for area in areas:
            store.create_directory(area, directory)
            store.write(area, f'{directory}/original.txt', b'DimDock storage smoke test', overwrite=False)
            check(store.read_text(area, f'{directory}/original.txt') == 'DimDock storage smoke test', f'{area} read bytes')
            check(store.list_files(area, directory) == ['original.txt'], f'{area} listing')
            try:
                store.write(area, f'{directory}/original.txt', b'must not overwrite', overwrite=False)
            except FileExistsError:
                pass
            else:
                raise RuntimeError(f'Storage smoke test failed: {area} conditional write overwrote an object')
            check(store.read_text(area, f'{directory}/original.txt') == 'DimDock storage smoke test', f'{area} conditional write')
            store.move_file(area, f'{directory}/original.txt', f'{directory}/moved.txt')
            check(not store.exists(area, f'{directory}/original.txt'), f'{area} move source deletion')
            check(store.exists(area, f'{directory}/moved.txt'), f'{area} move destination')
            store.delete_directory(area, directory, recursive=True)
            check(not store.is_directory(area, directory), f'{area} directory deletion')
            print(f'PASS {area}: write/read/list/copy/verify/delete', flush=True)
        if hasattr(store, 'client'):
            multipart_check(store, directory)
    finally:
        for area in areas:
            store.delete_directory(area, directory, recursive=True)
    print('Storage smoke test passed; temporary objects removed.', flush=True)


if __name__ == '__main__':
    main()
