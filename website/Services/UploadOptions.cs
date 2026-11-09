namespace Website.Services;

public enum UploadMode
{
    // Browser sends files in chunks through the main site, so every request stays under proxy/CDN body limits.
    Chunked,
    // Uploads happen on a separate hostname that is not behind the CDN; whole files are posted in one request.
    Bypass
}

public sealed class UploadOptions
{
    public const int MinimumChunkSizeMB = 5;
    public const int MaximumChunkSizeMB = 95;

    public UploadMode Mode { get; set; } = UploadMode.Chunked;
    public string BypassHost { get; set; } = "";
    public int ChunkSizeMB { get; set; } = 50;

    public long ChunkSizeBytes => ChunkSizeMB * 1024L * 1024;

    public void Validate()
    {
        if (ChunkSizeMB is < MinimumChunkSizeMB or > MaximumChunkSizeMB)
        {
            throw new InvalidOperationException(
                $"Uploads:ChunkSizeMB must be between {MinimumChunkSizeMB} and {MaximumChunkSizeMB}.");
        }

        BypassHost = BypassHost.Trim();
        if (Mode != UploadMode.Bypass)
        {
            return;
        }

        if (string.IsNullOrEmpty(BypassHost) ||
            !Uri.TryCreate($"https://{BypassHost}/", UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Authority, BypassHost, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(
                "Uploads:Mode=Bypass requires Uploads:BypassHost (UPLOAD_BYPASS_HOST) set to a hostname such as upload.example.com.");
        }
    }

    public bool IsBypassHost(HttpRequest request) =>
        Mode == UploadMode.Bypass &&
        string.Equals(request.Host.Value, BypassHost, StringComparison.OrdinalIgnoreCase);
}
