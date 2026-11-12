namespace Website.Services;

public static class ThumbnailGenerator
{
    public static async Task GenerateAsync(
        IFileStore store, HttpClient client, string sourceKey, string thumbnailKey,
        CancellationToken cancellationToken)
    {
        await using var source = await store.OpenReadAsync(FileStoreArea.Gallery, sourceKey, cancellationToken);
        if (source.Length > 50L * 1024 * 1024)
        {
            throw new InvalidThumbnailImageException("Images cannot exceed 50 MB.");
        }
        using var content = new StreamContent(source);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = source.Length;
        using var response = await client.PostAsync($"thumbnail/{thumbnailKey}", content, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.RequestEntityTooLarge)
        {
            throw new InvalidThumbnailImageException(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        response.EnsureSuccessStatusCode();
        await using var thumbnail = await response.Content.ReadAsStreamAsync(cancellationToken);
        await store.WriteAsync(FileStoreArea.Thumbnails, $"{thumbnailKey}.png", thumbnail,
            overwrite: true, cancellationToken);
    }
}

public sealed class InvalidThumbnailImageException(string message) : Exception(message);
