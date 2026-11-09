using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Website.Services;
using Website.Pages;
using System.Reflection;

var endpoint = Environment.GetEnvironmentVariable("STORAGE_TEST_ENDPOINT")
    ?? throw new InvalidOperationException("Set STORAGE_TEST_ENDPOINT to a disposable local Moto S3 service.");
if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback)
    throw new InvalidOperationException("Contract checks only allow a loopback test endpoint, never a live bucket.");
using var client = new AmazonS3Client(new BasicAWSCredentials("test", "test"), new AmazonS3Config
{
    ServiceURL = endpoint,
    AuthenticationRegion = "us-east-1",
    ForcePathStyle = true,
    RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
    ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
});
var bucket = $"dimdock-tests-{Guid.NewGuid():N}";
var root = Path.Combine(Path.GetTempPath(), bucket);
foreach (var parent in new string?[] { "root", "ROOT", "", null })
    Check(FileStoreKeys.GallerySourceUrl(parent, "image café.png") == "/img_src/image%20caf%C3%A9.png", "root image URL");
Check(FileStoreKeys.GallerySourceUrl("2023/2023-01/", "image.png") ==
    "/img_src/2023/2023-01/image.png", "nested image URL");
Console.WriteLine("PASS root/nested gallery source URLs");
await client.PutBucketAsync(bucket);
try
{
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["FileStorage:RootPath"] = root }).Build();
    IFileStore local = new LocalFileStore(configuration, new TestEnvironment());
    var options = new LinodeStorageOptions
    {
        Bucket = bucket, Prefix = "deployment"
    };
    var remote = new LinodeFileStore(client, options);
    var snapshots = new List<string>();
    foreach (var store in new IFileStore[] { local, remote })
    {
        await store.CreateDirectoryAsync(FileStoreArea.Gallery, "2023/2023-01/empty");
        await store.WriteAllTextAsync(FileStoreArea.Gallery, "2023/2023-01/Mixed café.txt", "content");
        await store.WriteAllTextAsync(FileStoreArea.Gallery, "2023/2023-01/.dimdock.json", "{\"version\":1}");
        snapshots.Add(string.Join('|', await store.ListFilesAsync(FileStoreArea.Gallery, "", recursive: true)));
        Check((await store.ListDirectoriesAsync(FileStoreArea.Gallery, "")).SequenceEqual(["2023"]), "directory listing");
        Check((await store.ListDirectoriesAsync(FileStoreArea.Gallery, "2023/2023-01")).SequenceEqual(["2023/2023-01/empty"]), "empty directory");
        await ExpectAsync<IOException>(async () =>
        {
            await using var input = new MemoryStream([1]);
            await store.WriteAsync(FileStoreArea.Gallery, "2023/2023-01/Mixed café.txt", input, overwrite: false);
        });
        await store.MoveFileAsync(FileStoreArea.Gallery, "2023/2023-01/Mixed café.txt", "2023/2023-01/moved.txt");
        Check(!await store.FileExistsAsync(FileStoreArea.Gallery, "2023/2023-01/Mixed café.txt"), "file move source removed");
        Check(await store.ReadAllTextAsync(FileStoreArea.Gallery, "2023/2023-01/moved.txt") == "content", "file move bytes");
        await store.MoveDirectoryAsync(FileStoreArea.Gallery, "2023", "2024");
        Check(!await store.DirectoryExistsAsync(FileStoreArea.Gallery, "2023"), "directory move source removed");
        Check(await store.DirectoryExistsAsync(FileStoreArea.Gallery, "2024/2023-01/empty"), "empty directory moved");
        await ExpectAsync<IOException>(() => store.DeleteDirectoryAsync(FileStoreArea.Gallery, "2024"));
        await store.DeleteDirectoryAsync(FileStoreArea.Gallery, "2024", recursive: true);
        Check(!await store.DirectoryExistsAsync(FileStoreArea.Gallery, "2024"), "recursive deletion");
        await ExpectAsync<FileNotFoundException>(() => store.OpenReadAsync(FileStoreArea.Gallery, "missing.txt"));
        foreach (var invalid in new[] { "../escape", "/absolute", "a\\b", "a//b", "a:b" })
            await ExpectAsync<InvalidDataException>(() => store.WriteAllTextAsync(FileStoreArea.Gallery, invalid, "invalid"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => store.OpenReadAsync(FileStoreArea.Gallery, "missing.txt", cancelled.Token));
        Console.WriteLine($"PASS {store.GetType().Name} CRUD/list/move/empty-directory/validation/cancellation");
    }
    Check(snapshots[0] == snapshots[1], "local/S3 logical key parity");
    if (Environment.GetEnvironmentVariable("THUMBNAIL_TEST_ENDPOINT") is { } thumbnailEndpoint)
    {
        if (!Uri.TryCreate(thumbnailEndpoint, UriKind.Absolute, out var thumbnailUri) || !thumbnailUri.IsLoopback)
            throw new InvalidOperationException("Thumbnail checks require a loopback endpoint.");
        var image = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
        await using (var input = new MemoryStream(image))
            await remote.WriteAsync(FileStoreArea.Gallery, "thumbnail-source.png", input);
        using var thumbnailClient = new HttpClient { BaseAddress = thumbnailUri };
        var page = new GalleryModel(null!, null!, null!, null!, null!, null!, null!, remote,
            new TestClientFactory(thumbnailClient));
        var generate = typeof(GalleryModel).GetMethod("GenerateThumbnailAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new Exception("Thumbnail generation method not found");
        var thumbnailKey = new string('a', 64);
        var task = generate.Invoke(page, ["thumbnail-source.png", thumbnailKey, CancellationToken.None]) as Task
            ?? throw new Exception("Thumbnail generation did not return a task");
        await task;
        await using var thumbnail = await remote.OpenReadAsync(FileStoreArea.Thumbnails, thumbnailKey + ".png");
        var signature = new byte[8];
        await thumbnail.ReadExactlyAsync(signature);
        Check(signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "generated thumbnail PNG");
        Console.WriteLine("PASS actual gallery thumbnail generation from non-seekable S3 stream through HTTP service");
    }
    await using (var body = new NonSeekableStream(new MemoryStream(new byte[8 * 1024 * 1024])))
        await remote.WriteAsync(FileStoreArea.Gallery, "stream.png", body);
    await using (var read = await remote.OpenReadAsync(FileStoreArea.Gallery, "stream.png"))
    {
        Check(!read.CanSeek, "remote reads stream without buffering");
        Check(read.Length == 8 * 1024 * 1024, "large streamed object length");
        await read.CopyToAsync(Stream.Null);
    }
    for (var index = 0; index < 1005; index++)
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = $"deployment/img_src/pages/{index:0000}.txt",
            ContentBody = "page", DisablePayloadSigning = uri.Scheme == "https",
            DisableDefaultChecksumValidation = true, UseChunkEncoding = false
        });
    Check((await remote.ListFilesAsync(FileStoreArea.Gallery, "pages")).Count == 1005, "pagination");
    await remote.WriteAllTextAsync(FileStoreArea.Gallery, "source/a.txt", "a");
    await remote.WriteAllTextAsync(FileStoreArea.Gallery, "source/b.txt", "b");
    using var failing = new FailingClient(endpoint);
    var interrupted = new LinodeFileStore(failing, options);
    await ExpectAsync<AmazonS3Exception>(() => interrupted.MoveDirectoryAsync(FileStoreArea.Gallery, "source", "destination"));
    Check(await remote.FileExistsAsync(FileStoreArea.Gallery, "source/a.txt") &&
        await remote.FileExistsAsync(FileStoreArea.Gallery, "source/b.txt"), "copy failure retains all source files");
    using var denied = new DeniedClient(endpoint);
    var inaccessible = new LinodeFileStore(denied, options);
    await ExpectAsync<AmazonS3Exception>(() => inaccessible.FileExistsAsync(FileStoreArea.Gallery, "source/a.txt"));
    Console.WriteLine("PASS cross-backend parity/8MB non-seekable stream/pagination/interrupted copy");
}
finally
{
    string? continuation = null;
    do
    {
        var page = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket, ContinuationToken = continuation
        });
        foreach (var item in page.S3Objects ?? [])
            await client.DeleteObjectAsync(bucket, item.Key);
        continuation = page.IsTruncated == true ? page.NextContinuationToken : null;
    } while (continuation is not null);
    await client.DeleteBucketAsync(bucket);
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

static void Check(bool success, string name)
{
    if (!success) throw new Exception($"FAIL {name}");
}

static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}

sealed class FailingClient(string endpoint) : AmazonS3Client(new BasicAWSCredentials("test", "test"), new AmazonS3Config
{
    ServiceURL = endpoint, AuthenticationRegion = "us-east-1", ForcePathStyle = true
})
{
    private int _copies;
    public override Task<CopyObjectResponse> CopyObjectAsync(CopyObjectRequest request, CancellationToken cancellationToken = default)
    {
        if (++_copies == 2) throw new AmazonS3Exception("Injected copy failure") { StatusCode = HttpStatusCode.Forbidden };
        return base.CopyObjectAsync(request, cancellationToken);
    }
}

sealed class NonSeekableStream(Stream inner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

sealed class DeniedClient(string endpoint) : AmazonS3Client(new BasicAWSCredentials("test", "test"), new AmazonS3Config
{
    ServiceURL = endpoint, AuthenticationRegion = "us-east-1", ForcePathStyle = true
})
{
    public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request,
        CancellationToken cancellationToken = default) =>
        throw new AmazonS3Exception("Injected permission failure") { StatusCode = HttpStatusCode.Forbidden };
}

sealed class TestEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "FileStoreChecks";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}

sealed class TestClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}
