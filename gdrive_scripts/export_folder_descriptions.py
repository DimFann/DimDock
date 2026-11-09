import argparse
from collections import deque
import os
from pathlib import Path
import sys

if not __package__:
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from gdrive_scripts.export_drive_shortcuts import DriveClient, DriveError, FOLDER_MIME, validate_id
from path_loader.file_store import validate_key
from gdrive_scripts.configuration import drive_settings
from clean import is_link


DEFAULT_OUTPUT = Path(__file__).resolve().parent / 'output' / 'folder_descriptions'


def scrape_descriptions(client: DriveClient, root_id: str) -> tuple[dict[str, str], list[str]]:
    validate_id(root_id)
    root = client.get_folder(root_id)
    if root.get('mimeType') != FOLDER_MIME:
        raise DriveError('The selected root is not a Drive folder.')
    pending = deque([(root_id, '', root)])
    seen = {root_id}
    descriptions = {}
    warnings = []
    while pending:
        folder_id, parent, folder = pending.popleft()
        description = folder.get('description', '')
        if not isinstance(description, str):
            warnings.append(f'{parent or "root"}: invalid description; description skipped.')
            description = ''
        descriptions[parent] = description
        candidates = {}
        for item in client.list_children(folder_id):
            if item.get('mimeType') != FOLDER_MIME:
                continue
            name, child_id = item.get('name'), item.get('id')
            try:
                if not isinstance(child_id, str):
                    raise ValueError('Missing folder ID.')
                validate_id(child_id)
                validate_key(name, allow_root=False)
                if '/' in name or len(name) > 255:
                    raise ValueError('Invalid gallery folder name.')
                path = f'{parent}/{name}' if parent else name
                if len(path) > 1024 or (not parent and name.casefold() == 'root'):
                    raise ValueError('Folder path exceeds limits or conflicts with gallery root.')
            except ValueError as error:
                warnings.append(f'Folder {name!r}: {error} Folder and contents skipped.')
                continue
            if child_id in seen:
                warnings.append(f'Folder {path!r}: repeated ID or cycle; skipped.')
                continue
            seen.add(child_id)
            candidates.setdefault(path.casefold(), []).append((child_id, path, item))
        for entries in candidates.values():
            if len(entries) != 1:
                warnings.append(f'Duplicate folder path {entries[0][1]!r}: all matching folders and contents skipped.')
                continue
            pending.append(entries[0])
    return descriptions, warnings


def write_descriptions(output: Path, descriptions: dict[str, str]) -> int:
    if output.exists():
        raise FileExistsError(f'Output directory already exists: {output}. Choose a fresh --output directory.')
    output.mkdir(parents=True)
    written = 0
    for relative, description in descriptions.items():
        if not description.strip():
            continue
        directory = output.joinpath(*relative.split('/')) if relative else output
        directory.mkdir(parents=True, exist_ok=True)
        with (directory / '.description.md').open('x', encoding='utf-8', newline='') as document:
            document.write(description)
        written += 1
    return written


def prune_empty_folders(output: Path) -> int:
    if not output.is_dir() or is_link(output):
        raise ValueError('Pruning requires an existing, non-linked output directory.')
    removed = 0
    for parent, directories, _ in os.walk(output, topdown=False, followlinks=False):
        for name in directories:
            directory = Path(parent) / name
            if is_link(directory):
                continue
            if not any(directory.iterdir()):
                directory.rmdir()
                removed += 1
    return removed


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description='Export Drive folder descriptions as a gallery directory tree.')
    parser.add_argument('--root-folder-id')
    parser.add_argument('--output', type=Path, default=DEFAULT_OUTPUT, help='New output directory for the hierarchy.')
    parser.add_argument('--prune-only', action='store_true', help='Remove empty subfolders from existing output without accessing Drive.')
    args = parser.parse_args(argv)
    if args.prune_only:
        try:
            removed = prune_empty_folders(args.output)
        except (OSError, ValueError) as error:
            print(f'ERROR: {error}', file=sys.stderr)
            return 1
        print(f'Pruned {removed} empty folders from {args.output}.')
        return 0
    if not args.root_folder_id:
        parser.error('--root-folder-id is required unless using --prune-only.')
    try:
        settings = drive_settings()
    except (OSError, ValueError) as error:
        parser.error(f'Cannot read repository .env: {error}')
    key = settings['GOOGLE_DRIVE_API_KEY']
    if not key:
        parser.error('Set GOOGLE_DRIVE_API_KEY in your environment or repository .env.')
    try:
        if args.output.exists():
            raise FileExistsError(f'Output directory already exists: {args.output}. Choose a fresh --output directory.')
        client = DriveClient(key)
        resource_key = settings['GOOGLE_DRIVE_ROOT_RESOURCE_KEY']
        if resource_key:
            client.set_resource_key(args.root_folder_id, resource_key)
        descriptions, warnings = scrape_descriptions(client, args.root_folder_id)
        count = write_descriptions(args.output, descriptions)
    except (DriveError, OSError, ValueError) as error:
        print(f'ERROR: {error}', file=sys.stderr)
        return 1
    for warning in warnings:
        print(f'WARNING: {warning}', file=sys.stderr)
    print(f'Exported {count} .description.md files from {len(descriptions)} scanned folders to {args.output}; '
          f'{len(warnings)} warnings.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
