using System.Text;

namespace Website.Services;

public sealed class LocalFileStore : IFileStore, IChunkedUploadStore
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly TimeSpan AbandonedChunkLifetime = TimeSpan.FromDays(1);
    private static readonly string ChunkStagingRoot = Path.Combine(Path.GetTempPath(), "dimdock-chunks");
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
            [FileStoreArea.Admin] = Path.Combine(fullRoot, "admin"),
            [FileStoreArea.Commissions] = Path.Combine(fullRoot, "commissions")
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

    public Task<string> BeginChunkedUploadAsync(
        FileStoreArea area,
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(area, key, allowEmpty: false);
        EnsureNoLinkedPath(area, key);
        if (Directory.Exists(path))
        {
            throw new IOException($"File key resolves to a directory: {key}");
        }

        RemoveAbandonedChunkUploads();
        var uploadId = Guid.NewGuid().ToString("N");
        var staging = Directory.CreateDirectory(Path.Combine(ChunkStagingRoot, uploadId)).FullName;
        File.WriteAllText(Path.Combine(staging, "target"), ChunkTarget(area, key), Utf8WithoutBom);
        return Task.FromResult(uploadId);
    }

    public async Task UploadChunkAsync(
        FileStoreArea area,
        string key,
        string uploadId,
        int partNumber,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidatePartNumber(partNumber);
        var staging = ChunkStaging(area, key, uploadId);
        var temporaryPath = Path.Combine(staging, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var destination = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await content.CopyToAsync(destination, cancellationToken);
            }

            // Overwrite so a retried chunk replaces a partially sent one.
            File.Move(temporaryPath, Path.Combine(staging, PartFileName(partNumber)), overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task CompleteChunkedUploadAsync(
        FileStoreArea area,
        string key,
        string uploadId,
        int partCount,
        CancellationToken cancellationToken = default)
    {
        ValidatePartNumber(partCount);
        var staging = ChunkStaging(area, key, uploadId);
        var parts = Enumerable.Range(1, partCount)
            .Select(part => Path.Combine(staging, PartFileName(part)))
            .ToList();
        var stored = Directory.GetFiles(staging, "*.part").Length;
        if (stored != partCount || parts.Any(part => !File.Exists(part)))
        {
            throw new InvalidDataException($"Upload is incomplete: expected {partCount} parts, storage has {stored}.");
        }

        var path = ResolvePath(area, key, allowEmpty: false);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("File key has no parent directory.");
        EnsureSafeDirectory(directory);
        Directory.CreateDirectory(directory);
        EnsureNoLinkedPath(area, key);
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException($"Destination file already exists: {key}");
        }

        var temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var destination = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                foreach (var part in parts)
                {
                    await using var source = new FileStream(
                        part, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                    await source.CopyToAsync(destination, cancellationToken);
                }

                await destination.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        Directory.Delete(staging, recursive: true);
    }

    public Task AbortChunkedUploadAsync(
        FileStoreArea area,
        string key,
        string uploadId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string staging;
        try
        {
            staging = ChunkStaging(area, key, uploadId);
        }
        catch (FileNotFoundException)
        {
            return Task.CompletedTask;
        }

        Directory.Delete(staging, recursive: true);
        return Task.CompletedTask;
    }

    private string ChunkStaging(FileStoreArea area, string key, string uploadId)
    {
        ResolvePath(area, key, allowEmpty: false);
        if (uploadId.Length != 32 || !uploadId.All(char.IsAsciiHexDigitLower))
        {
            throw new InvalidDataException("Invalid upload ID.");
        }

        var staging = Path.Combine(ChunkStagingRoot, uploadId);
        var targetFile = Path.Combine(staging, "target");
        if (!File.Exists(targetFile))
        {
            throw new FileNotFoundException($"Upload not found: {uploadId}");
        }

        if (File.ReadAllText(targetFile, Utf8WithoutBom) != ChunkTarget(area, key))
        {
            throw new InvalidDataException("Upload ID belongs to a different file.");
        }

        return staging;
    }

    private static string ChunkTarget(FileStoreArea area, string key) =>
        $"{area}\n{FileStoreKeys.Normalize(key)}";

    private static string PartFileName(int partNumber) => $"{partNumber:D5}.part";

    private static void ValidatePartNumber(int partNumber)
    {
        if (partNumber is < 1 or > ChunkedUploadLimits.MaximumParts)
        {
            throw new InvalidDataException($"Part number must be between 1 and {ChunkedUploadLimits.MaximumParts}.");
        }
    }

    private static void RemoveAbandonedChunkUploads()
    {
        if (!Directory.Exists(ChunkStagingRoot))
        {
            return;
        }

        foreach (var staging in Directory.EnumerateDirectories(ChunkStagingRoot))
        {
            try
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(staging) > AbandonedChunkLifetime)
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
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
        bool recursive = false,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(
            Enumerate(area, directoryKey, recursive, directories: true, cancellationToken));

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

    private static string[] ValidateKey(string key, bool allowEmpty) =>
        FileStoreKeys.Validate(key, allowEmpty);

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
