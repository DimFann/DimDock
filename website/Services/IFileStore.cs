namespace Website.Services;

public enum FileStoreArea
{
    Gallery,
    Thumbnails,
    Blogs,
    About,
    Admin
}

public sealed record DirectUpload(string Url, string ContentType);

// Stores that let the browser upload straight to storage with a short-lived signed URL.
public interface IDirectUploadStore
{
    Task<DirectUpload> CreateDirectUploadAsync(
        FileStoreArea area,
        string key,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);
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
