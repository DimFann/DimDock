import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys
import stat


def is_link(path):
    if not path.exists() and not path.is_symlink():
        return False
    attributes = getattr(path.lstat(), 'st_file_attributes', 0)
    return path.is_symlink() or bool(attributes & getattr(stat, 'FILE_ATTRIBUTE_REPARSE_POINT', 0))


def read_settings(path):
    settings = {}
    for number, line in enumerate(path.read_text(encoding='utf-8-sig').splitlines(), 1):
        line = line.strip()
        if not line or line.startswith('#'):
            continue
        if line.startswith('export '):
            line = line[7:]
        key, separator, value = line.partition('=')
        if not separator:
            raise ValueError(f'Invalid .env assignment at line {number}')
        value = value.strip()
        if value.startswith(("'", '"')):
            quote = value[0]
            end = value.find(quote, 1)
            if end < 0 or value[end + 1:].strip().split('#', 1)[0].strip():
                raise ValueError(f'Invalid quoted .env value at line {number}')
            value = value[1:end]
        else:
            value = value.split(' #', 1)[0].rstrip()
        settings[key.strip()] = value
    return settings


def data_root(settings):
    value = settings.get('DATA_DIR', '')
    if os.name != 'nt':
        value = settings.get('WSL_DATA_DIR') or value
        if len(value) > 2 and value[1:3] in (':/', ':\\'):
            converter = shutil.which('wslpath') or shutil.which('cygpath')
            if converter:
                value = subprocess.check_output([converter, '-u', value], text=True).strip()
    root = Path(value)
    if not value or not root.is_absolute():
        raise ValueError('DATA_DIR must be an absolute host path (or set WSL_DATA_DIR under WSL).')
    if is_link(root) or not root.is_dir():
        raise ValueError('DATA_DIR must be an existing, non-linked directory.')
    root = root.resolve()
    if root == Path(root.anchor) or root == Path.home().resolve() or root == Path(__file__).resolve().parent:
        raise ValueError('Refusing to clean a filesystem, home, or repository root.')
    return root


def clean_contents(root, name, dry_run=False):
    if name not in ('app_state', 'db_data', 'img_db_thumbs'):
        raise ValueError('Unsupported cleanup directory.')
    directory = root / name
    if is_link(directory):
        raise ValueError(f'Refusing linked cleanup directory: {directory}')
    if not directory.exists():
        print(f'Skipping absent {name}.')
        return
    if not directory.is_dir() or directory.resolve().parent != root:
        raise ValueError(f'Unsafe cleanup directory: {directory}')
    print(f'{"Would clean" if dry_run else "Cleaning"} {directory}')
    if dry_run:
        return
    for entry in directory.iterdir():
        if is_link(entry):
            raise ValueError(f'Refusing linked cleanup entry: {entry}')
        if entry.is_dir():
            shutil.rmtree(entry)
        else:
            entry.unlink()


def main(argv=None):
    parser = argparse.ArgumentParser(description='Reset deployment state/database; preserve originals and metadata. Stop services first.')
    parser.add_argument('--thumbs', action='store_true', help='Also remove generated thumbnails from the selected storage backend.')
    parser.add_argument('--dry-run', action='store_true', help='Show local scope and list remote thumbnail count without deleting.')
    arguments = parser.parse_args(['--thumbs' if arg.lower() == '/thumbs' else arg for arg in (sys.argv[1:] if argv is None else argv)])
    repository = Path(__file__).resolve().parent
    settings = read_settings(repository / '.env')
    provider = settings.get('FILE_STORAGE_PROVIDER', 'Local').lower()
    if provider not in ('local', 'linode'):
        raise ValueError('FILE_STORAGE_PROVIDER must be Local or Linode.')
    root = data_root(settings)
    print(f'Cleaning {provider} deployment. Originals and metadata are preserved. Services must be stopped.')
    # Do remote work first: credential/permission failures must not reset local state.
    if provider == 'linode' and arguments.thumbs:
        command = ['docker', 'compose', '--env-file', str(repository / '.env'), 'run',
                   '--rm', '--no-deps', '-e', 'FILE_STORAGE_PROVIDER=Linode',
                   '--entrypoint', 'python', 'path-loader', '/app/storage_clean.py']
        if arguments.dry_run:
            command.append('--dry-run')
        subprocess.run(command, cwd=repository, check=True)
    for name in ('app_state', 'db_data'):
        clean_contents(root, name, arguments.dry_run)
    if arguments.thumbs and provider == 'local':
        clean_contents(root, 'img_db_thumbs', arguments.dry_run)
    elif not arguments.thumbs:
        print('Keeping thumbnails. Use --thumbs to remove them.')
    print('Done.')


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        print(f'Cleanup failed: {error}', file=sys.stderr)
        sys.exit(1)
