"""Explicit, isolated read/write smoke test; never changes bucket access policy."""
import uuid

from file_store import create_file_store


def check(condition, message):
    if not condition:
        raise RuntimeError(f'Storage smoke test failed: {message}')


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
    finally:
        for area in areas:
            store.delete_directory(area, directory, recursive=True)
    print('Storage smoke test passed; temporary objects removed.', flush=True)


if __name__ == '__main__':
    main()
