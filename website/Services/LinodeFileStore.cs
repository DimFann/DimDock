using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.StaticFiles;

namespace Website.Services;

public sealed class LinodeFileStore(IAmazonS3 client, LinodeStorageOptions options) : IFileStore, IDirectUploadStore
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private bool UseUnsignedPayload => client.Config.ServiceURL.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private string ObjectKey(FileStoreArea area, string key, bool allowEmpty = false)
    {
        var relative = FileStoreKeys.Normalize(key, allowEmpty);
        var prefix = string.IsNullOrEmpty(options.Prefix) ? "" : options.Prefix + "/";
        return prefix + FileStoreKeys.AreaName(area) + "/" + relative;
    }

    private string DirectoryPrefix(FileStoreArea area, string key) =>
        ObjectKey(area, key, allowEmpty: true).TrimEnd('/') + "/";

    private async Task<GetObjectMetadataResponse?> MetadataAsync(string key, CancellationToken token)
    {
        try
        {
            return await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = options.Bucket,
                Key = key
            }, token);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound &&
            error.ErrorCode != "NoSuchBucket")
        {
            return null;
        }
    }

    private async Task<List<S3Object>> ObjectsAsync(string prefix, CancellationToken token)
    {
        var objects = new List<S3Object>();
        string? continuation = null;
        do
        {
            var page = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = options.Bucket,
                Prefix = prefix,
                ContinuationToken = continuation
            }, token);
            objects.AddRange(page.S3Objects ?? []);
            continuation = page.IsTruncated == true
                ? page.NextContinuationToken ?? throw new IOException("S3 omitted a continuation token.")
                : null;
        } while (continuation is not null);
        return objects;
    }

    public async Task<bool> FileExistsAsync(FileStoreArea area, string key, CancellationToken cancellationToken = default) =>
        await MetadataAsync(ObjectKey(area, key), cancellationToken) is not null;

    public async Task<bool> DirectoryExistsAsync(FileStoreArea area, string key, CancellationToken cancellationToken = default)
    {
        var page = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = options.Bucket,
            Prefix = DirectoryPrefix(area, key),
            MaxKeys = 1
        }, cancellationToken);
        return page.S3Objects?.Count > 0;
    }

    public async Task CreateDirectoryAsync(FileStoreArea area, string key, CancellationToken cancellationToken = default)
    {
        var normalized = FileStoreKeys.Normalize(key);
        if (normalized.Length > 0 && await FileExistsAsync(area, normalized, cancellationToken))
        {
            throw new IOException($"Directory key resolves to a file: {key}");
        }
        // Markers preserve empty directories when migrating between stores.
        var segments = FileStoreKeys.Validate(normalized, allowEmpty: true);
        for (var length = 0; length <= segments.Length; length++)
        {
            var parent = string.Join('/', segments.Take(length));
            if (parent.Length > 0 && await FileExistsAsync(area, parent, cancellationToken))
            {
                throw new IOException($"Directory key resolves to a file: {parent}");
            }
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = options.Bucket,
                Key = DirectoryPrefix(area, parent),
                ContentBody = "",
                ContentType = "application/x-directory",
                DisablePayloadSigning = UseUnsignedPayload,
                DisableDefaultChecksumValidation = true,
                UseChunkEncoding = false
            }, cancellationToken);
        }
    }

    public async Task<Stream> OpenReadAsync(FileStoreArea area, string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = options.Bucket,
                Key = ObjectKey(area, key)
            }, cancellationToken);
            return new ObjectReadStream(response);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound &&
            error.ErrorCode != "NoSuchBucket")
        {
            throw new FileNotFoundException($"Stored file not found: {key}", error);
        }
    }

    public async Task WriteAsync(FileStoreArea area, string key, Stream content, bool overwrite = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var objectKey = ObjectKey(area, key);
        if (await DirectoryExistsAsync(area, key, cancellationToken))
        {
            throw new IOException($"File key resolves to a directory: {key}");
        }
        await CreateDirectoryAsync(area, key.Contains('/') ? key[..key.LastIndexOf('/')] : "", cancellationToken);
        // The SDK needs a length and a replayable body. Spool non-seekable HTTP streams
        // to scratch disk rather than buffering originals in the Nanode's memory.
        await using var spool = content.CanSeek ? null : new FileStream(
            Path.Combine(Path.GetTempPath(), $"dimdock-{Guid.NewGuid():N}.upload"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        if (spool is not null)
        {
            await content.CopyToAsync(spool, cancellationToken);
            spool.Position = 0;
        }
        var input = spool ?? content;
        try
        {
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = options.Bucket,
                Key = objectKey,
                InputStream = input,
                AutoCloseStream = false,
                AutoResetStreamPosition = false,
                ContentType = ContentTypes.TryGetContentType(key, out var type) ? type : "application/octet-stream",
                IfNoneMatch = overwrite ? null : "*",
                DisablePayloadSigning = UseUnsignedPayload,
                DisableDefaultChecksumValidation = true,
                UseChunkEncoding = false
            }, cancellationToken);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new IOException($"Destination file already exists: {key}", error);
        }
    }

    public async Task<DirectUpload> CreateDirectUploadAsync(FileStoreArea area, string key, TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        var objectKey = ObjectKey(area, key);
        if (await DirectoryExistsAsync(area, key, cancellationToken))
        {
            throw new IOException($"File key resolves to a directory: {key}");
        }
        var contentType = ContentTypes.TryGetContentType(key, out var type) ? type : "application/octet-stream";
        var url = await client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = options.Bucket,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = DateTime.UtcNow.Add(lifetime),
            Protocol = client.Config.ServiceURL?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true
                ? Protocol.HTTP
                : Protocol.HTTPS
        });
        return new DirectUpload(url, contentType);
    }

    public async Task<string> ReadAllTextAsync(FileStoreArea area, string key, CancellationToken cancellationToken = default)
    {
        await using var stream = await OpenReadAsync(area, key, cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public async Task WriteAllTextAsync(FileStoreArea area, string key, string content, CancellationToken cancellationToken = default)
    {
        await using var stream = new MemoryStream(new UTF8Encoding(false).GetBytes(content));
        await WriteAsync(area, key, stream, cancellationToken: cancellationToken);
    }

    public async Task DeleteFileAsync(FileStoreArea area, string key, CancellationToken cancellationToken = default)
    {
        var objectKey = ObjectKey(area, key);
        if (await DirectoryExistsAsync(area, key, cancellationToken))
        {
            throw new IOException($"File key resolves to a directory: {key}");
        }
        await DeleteObjectAsync(objectKey, cancellationToken);
    }

    private async Task DeleteObjectAsync(string key, CancellationToken token) =>
        await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = options.Bucket, Key = key }, token);

    public async Task DeleteDirectoryAsync(FileStoreArea area, string key, bool recursive = false,
        CancellationToken cancellationToken = default)
    {
        var normalized = FileStoreKeys.Normalize(key, allowEmpty: false);
        if (await FileExistsAsync(area, normalized, cancellationToken))
        {
            throw new IOException($"Directory key resolves to a file: {key}");
        }
        var prefix = DirectoryPrefix(area, normalized);
        var objects = await ObjectsAsync(prefix, cancellationToken);
        if (!recursive && objects.Any(item => item.Key != prefix))
        {
            throw new IOException($"Directory is not empty: {key}");
        }
        foreach (var item in objects)
        {
            await DeleteObjectAsync(item.Key, cancellationToken);
        }
    }

    private async Task CopyVerifiedAsync(string source, string destination, CancellationToken token)
    {
        var original = await MetadataAsync(source, token)
            ?? throw new FileNotFoundException($"Move source not found: {source}");
        await client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = options.Bucket,
            SourceKey = source,
            DestinationBucket = options.Bucket,
            DestinationKey = destination,
            ETagToMatch = original.ETag
        }, token);
        var copied = await MetadataAsync(destination, token)
            ?? throw new IOException($"Move destination could not be verified: {destination}. Source retained.");
        if (copied.ContentLength != original.ContentLength ||
            !string.Equals(copied.ETag, original.ETag, StringComparison.Ordinal))
        {
            throw new IOException($"Move destination verification failed: {destination}. Source retained.");
        }
    }

    public async Task MoveFileAsync(FileStoreArea area, string sourceKey, string destinationKey,
        CancellationToken cancellationToken = default)
    {
        var source = ObjectKey(area, sourceKey);
        var destination = ObjectKey(area, destinationKey);
        if (await FileExistsAsync(area, destinationKey, cancellationToken) ||
            await DirectoryExistsAsync(area, destinationKey, cancellationToken))
        {
            throw new IOException($"Move destination already exists: {destinationKey}");
        }
        await CreateDirectoryAsync(area,
            destinationKey.Contains('/') ? destinationKey[..destinationKey.LastIndexOf('/')] : "", cancellationToken);
        if (await DirectoryExistsAsync(area, sourceKey, cancellationToken))
        {
            throw new IOException($"File move source resolves to a directory: {sourceKey}");
        }
        await CopyVerifiedAsync(source, destination, cancellationToken);
        await DeleteObjectAsync(source, cancellationToken);
    }

    public async Task MoveDirectoryAsync(FileStoreArea area, string sourceKey, string destinationKey,
        CancellationToken cancellationToken = default)
    {
        FileStoreKeys.Validate(sourceKey, allowEmpty: false);
        FileStoreKeys.Validate(destinationKey, allowEmpty: false);
        var source = DirectoryPrefix(area, sourceKey);
        var destination = DirectoryPrefix(area, destinationKey);
        if (source.StartsWith(destination, StringComparison.Ordinal) ||
            destination.StartsWith(source, StringComparison.Ordinal))
        {
            throw new IOException("A directory cannot be moved into itself or an ancestor.");
        }
        if (await FileExistsAsync(area, FileStoreKeys.Normalize(destinationKey), cancellationToken) ||
            await DirectoryExistsAsync(area, destinationKey, cancellationToken))
        {
            throw new IOException($"Move destination already exists: {destinationKey}");
        }
        var objects = await ObjectsAsync(source, cancellationToken);
        if (objects.Count == 0)
        {
            throw new DirectoryNotFoundException($"Move source directory not found: {sourceKey}");
        }
        var normalizedDestination = FileStoreKeys.Normalize(destinationKey);
        await CreateDirectoryAsync(area,
            normalizedDestination.Contains('/') ? normalizedDestination[..normalizedDestination.LastIndexOf('/')] : "",
            cancellationToken);
        foreach (var item in objects)
        {
            await CopyVerifiedAsync(item.Key, destination + item.Key[source.Length..], cancellationToken);
        }
        // Copy and verify the entire subtree before deleting anything. Interrupted
        // deletion leaves recoverable duplicates and surfaces an error to the caller.
        foreach (var item in objects)
        {
            await DeleteObjectAsync(item.Key, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(FileStoreArea area, string directoryKey, bool recursive = false,
        CancellationToken cancellationToken = default)
    {
        var prefix = DirectoryPrefix(area, directoryKey);
        var root = DirectoryPrefix(area, "");
        return (await ObjectsAsync(prefix, cancellationToken))
            .Where(item => !item.Key.EndsWith('/') && (recursive || !item.Key[prefix.Length..].Contains('/')))
            .Select(item => item.Key[root.Length..])
            .Order(StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<string>> ListDirectoriesAsync(FileStoreArea area, string directoryKey,
        CancellationToken cancellationToken = default)
    {
        var prefix = DirectoryPrefix(area, directoryKey);
        var root = DirectoryPrefix(area, "");
        return (await ObjectsAsync(prefix, cancellationToken))
            .Select(item => item.Key[prefix.Length..])
            .Where(relative => relative.Contains('/'))
            .Select(relative => prefix[root.Length..] + relative[..relative.IndexOf('/')])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    private sealed class ObjectReadStream(GetObjectResponse response) : Stream
    {
        private readonly Stream _inner = response.ResponseStream;
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => response.ContentLength;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) response.Dispose();
            base.Dispose(disposing);
        }
    }
}
