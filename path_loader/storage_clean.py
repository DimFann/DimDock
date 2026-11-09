import argparse

from file_store import create_file_store
from linode_file_store import LinodeFileStore


def clean_thumbnails(store, dry_run=False):
    if not isinstance(store, LinodeFileStore):
        raise ValueError('Remote thumbnail cleanup requires the Linode provider.')
    prefix = store._prefix('thumbnails')
    keys = [item['Key'] for page in store._objects(prefix) for item in page.get('Contents', [])]
    print(f'{"Would delete" if dry_run else "Deleting"} {len(keys)} thumbnail objects in {store.bucket}/{prefix}', flush=True)
    for key in keys:
        if not key.startswith(prefix):
            raise ValueError('Listing returned an object outside the thumbnail prefix.')
    if not dry_run:
        for key in keys:
            store.client.delete_object(Bucket=store.bucket, Key=key)
    print('Thumbnail cleanup complete. Other content and bucket policy unchanged.', flush=True)


def main():
    parser = argparse.ArgumentParser(description='Delete only generated thumbnails in the configured Linode deployment.')
    parser.add_argument('--dry-run', action='store_true')
    args = parser.parse_args()
    clean_thumbnails(create_file_store(), args.dry_run)


if __name__ == '__main__':
    main()
