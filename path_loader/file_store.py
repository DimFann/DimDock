import os
import shutil
import tempfile


class LocalFileStore:
    _areas = {
        'gallery': 'img_src',
        'thumbnails': 'img_db_thumbs',
        'blogs': 'blogs',
        'about': 'about',
        'admin': 'admin',
        'state': 'app_state',
    }

    def __init__(self, root):
        self.root = os.path.abspath(root)

    def _path(self, area, key='', allow_root=True):
        if area not in self._areas:
            raise ValueError(f'Unknown file-store area: {area}')
        if not isinstance(key, str) or '\\' in key or key.startswith('/'):
            raise ValueError(f'Invalid relative file key: {key!r}')
        if key.endswith('/'):
            if not allow_root:
                raise ValueError(f'Invalid relative file key: {key!r}')
            key = key[:-1]
        normalized = key
        segments = normalized.split('/') if normalized else []
        if (not allow_root and not segments) or any(
            not segment or segment in ('.', '..') or
            any(ord(char) < 32 or char in '<>:"|?*' for char in segment)
            for segment in segments
        ):
            raise ValueError(f'Invalid relative file key: {key!r}')

        area_root = os.path.join(self.root, self._areas[area])
        path = os.path.abspath(os.path.join(area_root, *segments))
        if os.path.commonpath((area_root, path)) != area_root:
            raise ValueError(f'File key resolves outside its storage area: {key!r}')

        current = area_root
        for segment in segments:
            current = os.path.join(current, segment)
            if os.path.lexists(current) and os.path.islink(current):
                raise ValueError(f'Linked paths are not supported by local file storage: {current}')
        if os.path.lexists(area_root) and os.path.islink(area_root):
            raise ValueError(f'Storage root cannot be a linked directory: {area_root}')
        return path

    def exists(self, area, key):
        return os.path.isfile(self._path(area, key, allow_root=False))

    def is_directory(self, area, key=''):
        return os.path.isdir(self._path(area, key))

    def create_directory(self, area, key=''):
        path = self._path(area, key)
        os.makedirs(path, exist_ok=True)
        self._path(area, key)

    def open_read(self, area, key):
        return open(self._path(area, key, allow_root=False), 'rb')

    def read_text(self, area, key):
        with open(self._path(area, key, allow_root=False), 'r', encoding='utf-8') as file:
            return file.read()

    def write(self, area, key, content, overwrite=True):
        path = self._path(area, key, allow_root=False)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        self._path(area, key, allow_root=False)
        if not overwrite and os.path.exists(path):
            raise FileExistsError(path)
        temporary_path = None
        try:
            with tempfile.NamedTemporaryFile(
                mode='wb',
                prefix='.dimdock-',
                dir=os.path.dirname(path),
                delete=False,
            ) as temporary:
                temporary_path = temporary.name
                if hasattr(content, 'read'):
                    shutil.copyfileobj(content, temporary, length=1024 * 1024)
                else:
                    temporary.write(content)
                temporary.flush()
                os.fsync(temporary.fileno())
            if overwrite:
                os.replace(temporary_path, path)
                temporary_path = None
            else:
                os.link(temporary_path, path)
                os.unlink(temporary_path)
                temporary_path = None
        finally:
            if temporary_path is not None and os.path.exists(temporary_path):
                os.unlink(temporary_path)

    def move_file(self, area, source_key, destination_key):
        source = self._path(area, source_key, allow_root=False)
        destination = self._path(area, destination_key, allow_root=False)
        os.makedirs(os.path.dirname(destination), exist_ok=True)
        self._path(area, destination_key, allow_root=False)
        if os.path.exists(destination):
            raise FileExistsError(destination)
        os.rename(source, destination)

    def delete_file(self, area, key):
        path = self._path(area, key, allow_root=False)
        if os.path.isdir(path):
            raise IsADirectoryError(path)
        if os.path.exists(path):
            os.unlink(path)

    def delete_directory(self, area, key, recursive=False):
        path = self._path(area, key, allow_root=False)
        if os.path.isfile(path):
            raise NotADirectoryError(path)
        if os.path.isdir(path):
            if recursive:
                shutil.rmtree(path)
            else:
                os.rmdir(path)

    def list_files(self, area, directory_key=''):
        directory = self._path(area, directory_key)
        if not os.path.isdir(directory):
            return []
        self._path(area, directory_key)
        return sorted(
            entry.name for entry in os.scandir(directory)
            if entry.is_file(follow_symlinks=False)
        )

    def walk(self, area):
        root = self._path(area)
        if not os.path.isdir(root):
            return
        self._path(area)
        for current, directories, files in os.walk(root, followlinks=False):
            relative = os.path.relpath(current, root)
            key = '' if relative == '.' else relative.replace(os.sep, '/')
            directories[:] = sorted(
                directory for directory in directories
                if not os.path.islink(os.path.join(current, directory))
            )
            if any(os.path.islink(os.path.join(current, filename)) for filename in files):
                raise ValueError(f'Linked files are not supported by local file storage: {current}')
            yield key, directories, files
