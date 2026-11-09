namespace Website.Services;

public enum FileStoreArea
{
    Gallery,
    Thumbnails,
    Blogs,
    About,
    Admin
}

// Stores that can assemble one file from sequentially numbered chunks (1-based, at most 10,000).
// Every chunk except the last must be at least 5 MB for S3-compatible stores.
public interface IChunkedUploadStore
{
    Task<string> BeginChunkedUploadAsync(
        FileStoreArea area,
        string key,
        CancellationToken cancellationToken = default);

    Task UploadChunkAsync(
        FileStoreArea area,
        string key,
        string uploadId,
        int partNumber,
        Stream content,
        CancellationToken cancellationToken = default);

    // Assembles parts 1..partCount; throws IOException if the destination already exists.
    Task CompleteChunkedUploadAsync(
        FileStoreArea area,
        string key,
        string uploadId,
        int partCount,
        CancellationToken cancellationToken = default);

    Task AbortChunkedUploadAsync(
        FileStoreArea area,
        string key,
        string uploadId,
        CancellationToken cancellationToken = default);
}

public static class ChunkedUploadLimits
{
    public const int MaximumParts = 10_000;
}

public interface IFileStore
{
    Task<bool> FileExistsAsync(
        FileStoreArea area,
        string key,
        CancellationToken cancellationToken = default);

    Task<bool> DirectoryExistsAsync(
        FileStoreArea area,
        string key,
        CancellationToken cancellationToken = default);

    Task CreateDirectoryAsync(
        FileStoreArea area,
        string key,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(
        FileStoreArea area,
        string key,
        CancellationToken cancellationToken = default);

    Task WriteAsync(
        FileStoreArea area,
        string key,
        Stream content,
        bool overwrite = true,
        CancellationToken cancellationToken = default);

    Task<string> ReadAllTextAsync(
        FileStoreArea area,
        string key,
        CancellationToken cancellationToken = default);

    Task WriteAllTextAsync(
        FileStoreArea area,
        string key,
        string content,
        CancellationToken cancellationToken = default);

    Task DeleteFileAsync(
        FileStoreArea area,
        string key,
        CancellationToken cancellationToken = default);

    Task DeleteDirectoryAsync(
        FileStoreArea area,
        string key,
        bool recursive = false,
        CancellationToken cancellationToken = default);

    Task MoveFileAsync(
        FileStoreArea area,
        string sourceKey,
        string destinationKey,
        CancellationToken cancellationToken = default);

    Task MoveDirectoryAsync(
        FileStoreArea area,
        string sourceKey,
        string destinationKey,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListFilesAsync(
        FileStoreArea area,
        string directoryKey,
        bool recursive = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListDirectoriesAsync(
        FileStoreArea area,
        string directoryKey,
        CancellationToken cancellationToken = default);
}
