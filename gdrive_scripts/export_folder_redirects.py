import argparse
from collections import deque
import json
from pathlib import Path
import sys
from urllib.parse import quote

if not __package__:
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from gdrive_scripts.configuration import drive_settings, root_folder_id
from gdrive_scripts.export_drive_shortcuts import DriveClient, DriveError, FOLDER_MIME, validate_id
from gdrive_scripts.export_folder_descriptions import walk_folders


DEFAULT_OUTPUT = Path(__file__).resolve().parent / 'output' / 'folder_redirects.json'
DEFAULT_LEGACY_PATH = '/sketch'
DEFAULT_QUERY_KEY = 'folderID'


def gallery_target(path: str) -> str:
    # Match the website's per-segment Uri.EscapeDataString gallery links.
    return '/Gallery/' + '/'.join(quote(segment, safe='') for segment in path.split('/')) if path else '/Gallery'


def resolve_folders(client: DriveClient, root_id: str) -> tuple[list[tuple[str, str]], list[str]]:
    """Map every folder ID under the root to a gallery path.

    Folders without a gallery page of their own (invalid or duplicate names) and everything inside them map to
    their nearest ancestor that has one. Raises instead of returning a partial list when a folder ID is unusable.
    """
    skipped: deque[tuple[object, str]] = deque()
    folders, _ = walk_folders(client, root_id, on_skip=lambda child_id, parent: skipped.append((child_id, parent)))
    targets = {folder_id: path for folder_id, path, _ in folders}
    fallbacks: dict[str, str] = {}
    while skipped:
        folder_id, ancestor = skipped.popleft()
        if not isinstance(folder_id, str):
            raise DriveError(f'A folder under {ancestor or "the root"!r} has no Drive ID; cannot export every folder.')
        try:
            validate_id(folder_id)
        except ValueError:
            raise DriveError(f'Folder ID {folder_id!r} under {ancestor or "the root"!r} is not a valid Drive ID.') from None
        if folder_id in targets or folder_id in fallbacks:
            continue
        fallbacks[folder_id] = ancestor
        for item in client.list_children(folder_id):
            if item.get('mimeType') == FOLDER_MIME:
                skipped.append((item.get('id'), ancestor))
    notes = [f'Folder {folder_id} has no gallery page; redirecting to {gallery_target(path)}.'
             for folder_id, path in fallbacks.items()]
    return [*targets.items(), *fallbacks.items()], notes


def build_redirects(folders: list[tuple[str, str]], legacy_path: str = DEFAULT_LEGACY_PATH,
                    query_key: str = DEFAULT_QUERY_KEY) -> list[dict]:
    rules = [{'Path': legacy_path, 'Query': {query_key: folder_id}, 'Target': gallery_target(path)}
             for folder_id, path in sorted(folders, key=lambda entry: (entry[1].casefold(), entry[0]))]
    rules.append({'Path': legacy_path, 'Target': '/Gallery'})
    return rules


def write_redirects(output: Path, rules: list[dict]) -> None:
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open('w', encoding='utf-8', newline='\n') as document:
        json.dump({'LegacyRedirects': rules}, document, indent=2, ensure_ascii=False)
        document.write('\n')


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description='Export Drive folder IDs as website LegacyRedirects rules (old ?folderID= links to gallery paths).')
    parser.add_argument('--root-folder-id',
                        help='Drive folder that maps to the gallery root (default: GOOGLE_DRIVE_ROOT_FOLDER_ID).')
    parser.add_argument('--output', type=Path, default=DEFAULT_OUTPUT, help='JSON file to write (overwritten).')
    parser.add_argument('--legacy-path', default=DEFAULT_LEGACY_PATH, help='Old website path the links used.')
    parser.add_argument('--query-key', default=DEFAULT_QUERY_KEY, help='Query parameter holding the folder ID.')
    args = parser.parse_args(argv)
    try:
        settings = drive_settings()
    except (OSError, ValueError) as error:
        parser.error(f'Cannot read repository .env: {error}')
    root_id = root_folder_id(args.root_folder_id, settings)
    if not root_id:
        parser.error('Pass --root-folder-id or set GOOGLE_DRIVE_ROOT_FOLDER_ID in your environment or repository .env.')
    key = settings['GOOGLE_DRIVE_API_KEY']
    if not key:
        parser.error('Set GOOGLE_DRIVE_API_KEY in your environment or repository .env.')
    try:
        client = DriveClient(key)
        resource_key = settings['GOOGLE_DRIVE_ROOT_RESOURCE_KEY']
        if resource_key:
            client.set_resource_key(root_id, resource_key)
        folders, warnings = resolve_folders(client, root_id)
        rules = build_redirects(folders, args.legacy_path, args.query_key)
        write_redirects(args.output, rules)
    except (DriveError, OSError, ValueError) as error:
        print(f'ERROR: {error}', file=sys.stderr)
        return 1
    for warning in warnings:
        print(f'WARNING: {warning}', file=sys.stderr)
    print(f'Exported {len(folders)} folder redirects to {args.output}; '
          f'{len(warnings)} redirect to the nearest gallery ancestor.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
