using System.Text;

namespace Website.Services;

public sealed class LocalFileStore : IFileStore
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly HashSet<char> InvalidPortableFileNameCharacters =
        ['<', '>', ':', '"', '|', '?', '*'];
    private readonly Dictionary<FileStoreArea, string> _roots;

    public LocalFileStore(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configuredRoot = configuration["FileStorage:RootPath"];
        if (configuredRoot is not null && string.IsNullOrWhiteSpace(configuredRoot))
        {
            throw new InvalidOperationException("FileStorage:RootPath cannot be empty.");
        }

        var root = configuredRoot;
        if (root is null)
        {
            root = Environment.GetEnvironmentVariable("DATA_DIR");
        }
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(environment.ContentRootPath, "..", "data");
        }

        var fullRoot = Path.GetFullPath(root);
        _roots = new Dictionary<FileStoreArea, string>
        {
            [FileStoreArea.Gallery] = Path.Combine(fullRoot, "img_src"),
            [FileStoreArea.Thumbnails] = Path.Combine(fullRoot, "img_db_thumbs"),
            [FileStoreArea.Blogs] = Path.Combine(fullRoot, "blogs"),
            [FileStoreArea.About] = Path.Combine(fullRoot, "about"),
            [FileStoreArea.Admin] = Path.Combine(fullRoot, "admin")
        };
    }

    public Task<bool> FileExistsAsync(
        FileStoreArea area,
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNoLinkedPath(area, key);
        return Task.FromResult(File.Exists(ResolvePath(area, key, allowEmpty: false)));
    }

    public Task<bool> DirectoryExistsAsync(
        FileStoreArea area,
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNoLinkedPath(area, key);
        return Task.FromResult(Directory.Exists(ResolvePath(area, key, allowEmpty: true)));
    }

    public Task CreateDirectoryAsync(
        FileStoreArea area,
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(area, key, allowEmpty: true);
        EnsureNoLinkedPath(area, key);
        EnsureSafeDirectory(path);
        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }

    public Task<Stream> OpenReadAsync(
        FileStoreArea area,
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNoLinkedPath(area, key);
        Stream stream = new FileStream(
            ResolvePath(area, key, allowEmpty: false),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        return Task.FromResult(stream);
    }

    public async Task WriteAsync(
        FileStoreArea area,
        string key,
        Stream content,
        bool overwrite = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = ResolvePath(area, key, allowEmpty: false);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("File key has no parent directory.");
        EnsureSafeDirectory(directory);
        Directory.CreateDirectory(directory);
        EnsureNoLinkedPath(area, key);

        var temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                await content.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            if (overwrite)
            {
                File.Move(temporaryPath, path, overwrite: true);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<string> ReadAllTextAsync(
        FileStoreArea area,
        string key,
        CancellationToken cancellationToken = default)
    {
        await using var stream = await OpenReadAsync(area, key, cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public async Task WriteAllTextAsync(
        FileStoreArea area,
        string key,
        string content,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new MemoryStream(Utf8WithoutBom.GetBytes(content));
        await WriteAsync(area, key, stream, cancellationToken: cancellationToken);
    }

    public Task DeleteFileAsync(
        FileStoreArea area,
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(area, key, allowEmpty: false);
        EnsureNoLinkedPath(area, key);
        if (Directory.Exists(path))
        {
            throw new InvalidOperationException($"File key resolves to a directory: {key}");
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task DeleteDirectoryAsync(
        FileStoreArea area,
        string key,
        bool recursive = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(area, key, allowEmpty: false);
        EnsureNoLinkedPath(area, key);
        if (File.Exists(path))
        {
            throw new InvalidOperationException($"Directory key resolves to a file: {key}");
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive);
        }

        return Task.CompletedTask;
    }

    public Task MoveFileAsync(
        FileStoreArea area,
        string sourceKey,
        string destinationKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = ResolvePath(area, sourceKey, allowEmpty: false);
        var destination = ResolvePath(area, destinationKey, allowEmpty: false);
        EnsureNoLinkedPath(area, sourceKey);
        EnsureSafeDirectory(Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Destination file key has no parent directory."));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        EnsureNoLinkedPath(area, destinationKey);
        File.Move(source, destination);
        return Task.CompletedTask;
    }

    public Task MoveDirectoryAsync(
        FileStoreArea area,
        string sourceKey,
        string destinationKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = ResolvePath(area, sourceKey, allowEmpty: false);
        var destination = ResolvePath(area, destinationKey, allowEmpty: false);
        EnsureNoLinkedPath(area, sourceKey);
        EnsureSafeDirectory(Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Destination directory key has no parent directory."));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        EnsureNoLinkedPath(area, destinationKey);
        Directory.Move(source, destination);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListFilesAsync(
        FileStoreArea area,
        string directoryKey,
        bool recursive = false,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(
            Enumerate(area, directoryKey, recursive, directories: false, cancellationToken));

    public Task<IReadOnlyList<string>> ListDirectoriesAsync(
        FileStoreArea area,
        string directoryKey,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(
            Enumerate(area, directoryKey, recursive: false, directories: true, cancellationToken));

    private List<string> Enumerate(
        FileStoreArea area,
        string directoryKey,
        bool recursive,
        bool directories,
        CancellationToken cancellationToken)
    {
        var path = ResolvePath(area, directoryKey, allowEmpty: true);
        EnsureNoLinkedPath(area, directoryKey);
        if (!Directory.Exists(path))
        {
            return [];
        }

        var root = _roots[area];
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var results = new List<string>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(path);
        while (pendingDirectories.TryPop(out var currentDirectory))
        {
            foreach (var entry in Directory.EnumerateDirectories(currentDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException($"Linked paths are not supported by local file storage: {entry}");
                }

                if (directories && (recursive || string.Equals(currentDirectory, path, comparison)))
                {
                    results.Add(ToStorageKey(root, prefix, entry, comparison));
                }

                if (recursive)
                {
                    pendingDirectories.Push(entry);
                }
            }

            if (!directories)
            {
                foreach (var entry in Directory.EnumerateFiles(currentDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException($"Linked paths are not supported by local file storage: {entry}");
                    }

                    results.Add(ToStorageKey(root, prefix, entry, comparison));
                }
            }
        }

        results.Sort(StringComparer.Ordinal);
        return results;
    }

    private static string ToStorageKey(
        string root,
        string prefix,
        string entry,
        StringComparison comparison)
    {
        var fullPath = Path.GetFullPath(entry);
        if (!fullPath.StartsWith(prefix, comparison))
        {
            throw new InvalidOperationException($"Enumerated path escaped the configured storage root: {entry}");
        }

        return Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
    }

    private string ResolvePath(FileStoreArea area, string key, bool allowEmpty)
    {
        if (!_roots.TryGetValue(area, out var root))
        {
            throw new ArgumentOutOfRangeException(nameof(area), area, "Unknown file storage area.");
        }

        var segments = ValidateKey(key, allowEmpty);
        var path = segments.Length == 0
            ? root
            : Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (segments.Length > 0 && !path.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidDataException($"File key resolves outside its storage area: {key}");
        }

        return path;
    }

    private static string[] ValidateKey(string key, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Contains('\\'))
        {
            throw new InvalidDataException($"Invalid relative file key: {key}");
        }

        var normalized = key;
        if (normalized.StartsWith('/') || Path.IsPathRooted(key) ||
            (!allowEmpty && string.IsNullOrWhiteSpace(normalized)))
        {
            throw new InvalidDataException($"Invalid relative file key: {key}");
        }

        if (normalized.EndsWith('/'))
        {
            if (!allowEmpty)
            {
                throw new InvalidDataException($"Invalid relative file key: {key}");
            }

            normalized = normalized[..^1];
        }

        var segments = normalized.Length == 0 ? [] : normalized.Split('/');
        if (segments.Any(segment =>
            string.IsNullOrEmpty(segment) ||
            segment is "." or ".." ||
            segment.Any(character =>
                char.IsControl(character) ||
                InvalidPortableFileNameCharacters.Contains(character))))
        {
            throw new InvalidDataException($"Invalid relative file key: {key}");
        }

        return segments;
    }

    private void EnsureNoLinkedPath(FileStoreArea area, string key)
    {
        var current = _roots[area];
        if (Directory.Exists(current) &&
            (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"Storage root cannot be a linked directory: {current}");
        }

        foreach (var segment in ValidateKey(key, allowEmpty: true))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"Linked paths are not supported by local file storage: {current}");
            }
        }
    }

    private static void EnsureSafeDirectory(string directory)
    {
        var current = Path.GetFullPath(directory);
        while (Directory.Exists(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"Linked paths are not supported by local file storage: {current}");
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current)
            {
                break;
            }

            current = parent;
        }
    }
}
