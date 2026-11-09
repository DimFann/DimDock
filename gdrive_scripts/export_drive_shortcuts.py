import argparse
from collections import deque
import json
import os
from pathlib import Path
import re
import sys
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen

if not __package__:
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from path_loader.file_store import validate_key
from gdrive_scripts.configuration import drive_settings


FOLDER_MIME = 'application/vnd.google-apps.folder'
SHORTCUT_MIME = 'application/vnd.google-apps.shortcut'
FILE_FIELDS = 'id,name,mimeType,description,resourceKey,shortcutDetails(targetId,targetMimeType)'
DEFAULT_OUTPUT = Path(__file__).resolve().parent / 'output' / 'shortcuts' / '.shortcuts.json'


class DriveError(Exception):
    pass


def validate_id(value: str) -> str:
    if not re.fullmatch(r'[A-Za-z0-9_-]+', value):
        raise ValueError('Use a Drive folder ID, not a sharing URL.')
    return value


class DriveClient:
    def __init__(self, api_key: str):
        self.api_key = api_key
        self.resource_keys: dict[str, str] = {}

    def set_resource_key(self, file_id: str, resource_key: str) -> None:
        validate_id(file_id)
        if not isinstance(resource_key, str) or not re.fullmatch(r'[A-Za-z0-9_-]+', resource_key):
            raise ValueError('Invalid Drive resource key; use only the resourcekey value from the sharing URL.')
        self.resource_keys[file_id] = resource_key

    def request(self, path: str, parameters: dict, resource_id: str | None = None) -> dict:
        url = 'https://www.googleapis.com/drive/v3/' + path + '?' + urlencode(parameters)
        headers = {'X-Goog-Api-Key': self.api_key}
        if resource_id in self.resource_keys:
            headers['X-Goog-Drive-Resource-Keys'] = f'{resource_id}/{self.resource_keys[resource_id]}'
        request = Request(url, headers=headers)
        for attempt in range(3):
            try:
                with urlopen(request, timeout=30) as response:
                    data = json.load(response)
                if not isinstance(data, dict):
                    raise DriveError('Drive returned an invalid JSON response.')
                return data
            except HTTPError as error:
                status = error.code
                error.close()
                if status in (429, 500, 502, 503, 504) and attempt < 2:
                    time.sleep(2 ** attempt)
                    continue
                raise DriveError(
                    f'Drive HTTP {status}. Check that the Drive API is enabled, the API key '
                    'permits it, and the root and descendants are accessible. For link-shared '
                    'folders, supply GOOGLE_DRIVE_ROOT_RESOURCE_KEY from the sharing URL. An API key '
                    'does not authenticate a user; OAuth may be required to list this folder.'
                ) from None
            except (URLError, TimeoutError) as error:
                raise DriveError(f'Drive connection failed ({type(error).__name__}).') from None
            except (ValueError, UnicodeError):
                raise DriveError('Drive returned invalid JSON.') from None
        raise DriveError('Drive request retries exhausted.')

    def get_folder(self, folder_id: str) -> dict:
        return self.request('files/' + validate_id(folder_id), {
            'fields': 'id,mimeType,description', 'supportsAllDrives': 'true',
        }, resource_id=folder_id)

    def list_children(self, folder_id: str) -> list[dict]:
        validate_id(folder_id)
        parameters = {
            'q': f"'{folder_id}' in parents and trashed = false",
            'fields': f'nextPageToken,incompleteSearch,files({FILE_FIELDS})',
            'pageSize': 1000,
            'supportsAllDrives': 'true',
            'includeItemsFromAllDrives': 'true',
        }
        files = []
        tokens = set()
        while True:
            data = self.request('files', parameters, resource_id=folder_id)
            if data.get('incompleteSearch'):
                raise DriveError(f'Drive returned an incomplete listing for folder {folder_id}.')
            page = data.get('files')
            if not isinstance(page, list) or any(not isinstance(item, dict) for item in page):
                raise DriveError(f'Drive returned an invalid file listing for folder {folder_id}.')
            files.extend(page)
            for item in page:
                if item.get('resourceKey'):
                    self.set_resource_key(item.get('id'), item['resourceKey'])
            token = data.get('nextPageToken')
            if not token:
                return files
            if not isinstance(token, str) or token in tokens:
                raise DriveError('Drive returned an invalid or repeated pagination token.')
            tokens.add(token)
            parameters['pageToken'] = token


def export_shortcuts(client: DriveClient, root_id: str) -> tuple[dict, list[str]]:
    validate_id(root_id)
    if client.get_folder(root_id).get('mimeType') != FOLDER_MIME:
        raise DriveError('The selected root is not a Drive folder.')
    pending = deque([(root_id, '')])
    seen_ids = {root_id}
    folder_paths = {root_id: 'root'}
    targets = {}
    shortcuts = []
    warnings = []
    while pending:
        folder_id, parent = pending.popleft()
        candidates = {}
        for item in client.list_children(folder_id):
            file_id, name, mime = item.get('id'), item.get('name'), item.get('mimeType')
            if not all(isinstance(value, str) and value for value in (file_id, name, mime)):
                warnings.append(f'Folder {folder_id}: incomplete file metadata; entry skipped.')
                continue
            if file_id in seen_ids:
                warnings.append(f'Drive item {file_id}: repeated item or cycle; entry skipped.')
                continue
            seen_ids.add(file_id)
            if mime == SHORTCUT_MIME:
                shortcuts.append((parent or 'root', name, item.get('shortcutDetails')))
                continue
            # Drive permits duplicate names; the gallery's path-based format does not.
            try:
                if '/' in name or '\\' in name:
                    raise ValueError(f'Filename cannot be represented in the gallery: {name!r}.')
                path = validate_key(f'{parent}/{name}' if parent else name, allow_root=False)
                if len(name) > 255 or len(path) > 1024:
                    raise ValueError(f'Gallery name or path exceeds the database limits: {path!r}.')
                if mime == FOLDER_MIME and not parent and name.casefold() == 'root':
                    raise ValueError("A top-level folder named 'root' conflicts with the gallery root.")
            except ValueError as error:
                warnings.append(f'{error} Entry skipped.')
                continue
            candidates.setdefault(path.casefold(), []).append((file_id, path, name, mime))
        for entries in candidates.values():
            if len(entries) > 1:
                warnings.append(f'Duplicate gallery path: {entries[0][1]!r}; '
                                f'all {len(entries)} ambiguous entries skipped (including folder contents).')
                continue
            file_id, path, name, mime = entries[0]
            if mime == FOLDER_MIME:
                folder_paths[file_id] = path
                pending.append((file_id, path))
            else:
                # The importer queries root-level Items with parent_path='root'.
                targets[file_id] = (f'root/{name}' if not parent else path)

    destinations = {}
    directory_destinations = {}
    directory_names = {}
    ambiguous_names = set()
    for destination, label, details in shortcuts:
        description = f'{destination}/{label}'
        if not isinstance(details, dict) or not isinstance(details.get('targetId'), str):
            warnings.append(f'{description}: missing target metadata; skipped.')
        elif details['targetId'] in targets:
            destinations.setdefault(destination, set()).add(targets[details['targetId']])
        elif details['targetId'] in folder_paths:
            try:
                validate_key(label, allow_root=False)
                if '/' in label or len(label) > 255:
                    raise ValueError(f'Invalid directory shortcut label: {description!r}.')
            except ValueError as error:
                warnings.append(f'{error} Directory shortcut skipped.')
                continue
            identity = (destination.casefold(), label.casefold())
            if identity in directory_names:
                ambiguous_names.add(identity)
                warnings.append(f'Duplicate directory shortcut label: {description!r}; all matching aliases skipped.')
                continue
            directory_names[identity] = (destination, {
                'name': label, 'target_path': folder_paths[details['targetId']],
            })
        else:
            warnings.append(f'{description}: target is outside the root or unavailable; skipped.')
    data = {'shortcuts': {key: sorted(destinations[key]) for key in sorted(destinations)}}
    for identity, (destination, entry) in directory_names.items():
        if identity not in ambiguous_names:
            directory_destinations.setdefault(destination, []).append(entry)
    if directory_destinations:
        data['directory_shortcuts'] = {
            key: sorted(directory_destinations[key], key=lambda entry: (entry['name'], entry['target_path']))
            for key in sorted(directory_destinations)
        }
    return data, warnings


def write_output(path: Path, data: dict, overwrite: bool) -> None:
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(
            mode='w', encoding='utf-8', dir=path.parent, prefix='.drive-shortcuts-', delete=False,
        ) as output:
            temporary = Path(output.name)
            json.dump(data, output, ensure_ascii=False, indent=2)
            output.write('\n')
        if overwrite:
            os.replace(temporary, path)
        else:
            os.link(temporary, path)
    finally:
        if temporary is not None and temporary.exists():
            temporary.unlink()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description='Export public Google Drive file and directory shortcuts to DimDock .shortcuts.json (Python 3.10+).',
    )
    parser.add_argument('--root-folder-id', required=True, help='ID of the gallery source root in Drive.')
    parser.add_argument('--output', type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument('--overwrite', action='store_true', help='Replace existing output (does not merge).')
    args = parser.parse_args(argv)
    try:
        settings = drive_settings()
    except (OSError, ValueError) as error:
        parser.error(f'Cannot read repository .env: {error}')
    api_key = settings['GOOGLE_DRIVE_API_KEY']
    if not api_key:
        parser.error('Set GOOGLE_DRIVE_API_KEY in your environment or repository .env; do not commit it.')
    try:
        if args.output.exists() and not args.overwrite:
            raise ValueError(f'Output already exists: {args.output}. Use --overwrite to replace it.')
        client = DriveClient(api_key)
        resource_key = settings['GOOGLE_DRIVE_ROOT_RESOURCE_KEY']
        if resource_key:
            client.set_resource_key(args.root_folder_id, resource_key)
        data, warnings = export_shortcuts(client, args.root_folder_id)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        write_output(args.output, data, args.overwrite)
    except (DriveError, OSError, ValueError) as error:
        print(f'ERROR: {error}', file=sys.stderr)
        return 1
    for warning in warnings:
        print(f'WARNING: {warning}', file=sys.stderr)
    count = sum(len(files) for files in data['shortcuts'].values())
    directories = sum(len(entries) for entries in data.get('directory_shortcuts', {}).values())
    print(f'Exported {count} unique file shortcuts and {directories} directory shortcuts '
          f'to {args.output}; {len(warnings)} warnings (skipped entries/shortcuts).')
    return 0


if __name__ == '__main__':
    sys.exit(main())
