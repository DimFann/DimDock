namespace Website.Services;

public sealed class LinodeStorageOptions
{
    public string Endpoint { get; set; } = "";
    public string Region { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string PublicBaseUrl { get; set; } = "";

    public void Validate()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            endpoint.AbsolutePath != "/" || !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidOperationException("LinodeStorage:Endpoint must be an HTTPS S3 endpoint without a bucket, path, or credentials.");
        }
        if (string.IsNullOrWhiteSpace(Region) || string.IsNullOrWhiteSpace(Bucket) ||
            Bucket.Contains('/') || string.IsNullOrWhiteSpace(AccessKey) || string.IsNullOrWhiteSpace(SecretKey))
        {
            throw new InvalidOperationException("LinodeStorage requires Region, Bucket, AccessKey, and SecretKey.");
        }
        Prefix = FileStoreKeys.Normalize(Prefix);
        if (!string.IsNullOrEmpty(PublicBaseUrl) &&
            (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var publicUri) ||
             publicUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(publicUri.UserInfo) ||
             !string.IsNullOrEmpty(publicUri.Query) || !string.IsNullOrEmpty(publicUri.Fragment)))
        {
            throw new InvalidOperationException("LinodeStorage:PublicBaseUrl must be an HTTPS bucket/CDN URL without credentials, query, or fragment.");
        }
    }
}
