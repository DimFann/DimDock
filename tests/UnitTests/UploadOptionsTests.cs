using Microsoft.AspNetCore.Http;
using Website.Services;

namespace UnitTests;

public class UploadOptionsTests
{
    [Theory]
    [InlineData("https://example.com/path")]
    [InlineData("https://user@example.com")]
    [InlineData("https://upload.example.com")]
    [InlineData("https://example.com?x=1")]
    [InlineData("javascript:alert(1)")]
    public void BypassRejectsInvalidMainSiteOrigin(string url)
    {
        var options = new UploadOptions { Mode = UploadMode.Bypass, BypassHost = "upload.example.com", MainSiteUrl = url };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData("upload.example.com", "https://dimdock.com/site/Gallery")]
    [InlineData("dimdock.com", "/site/Gallery")]
    public void HeaderLinksReturnToMainSiteOnlyOnBypassHost(string host, string expected)
    {
        var options = new UploadOptions { Mode = UploadMode.Bypass, BypassHost = "upload.example.com" };
        options.Validate();
        var request = new DefaultHttpContext().Request;
        request.Host = new HostString(host);
        Assert.Equal(expected, options.HeaderUrl(request, "/site/Gallery"));
        options.Mode = UploadMode.Chunked;
        Assert.Equal("/site/Gallery", options.HeaderUrl(request, "/site/Gallery"));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(96)]
    public void ChunkSizeOutsideRangeIsRejected(int chunkSizeMB)
    {
        var options = new UploadOptions { ChunkSizeMB = chunkSizeMB };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void ChunkSizeIsConvertedToBytes()
    {
        var options = new UploadOptions { ChunkSizeMB = 5 };

        options.Validate();

        Assert.Equal(5L * 1024 * 1024, options.ChunkSizeBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://upload.example.com")]
    [InlineData("upload.example.com/path")]
    [InlineData("user@upload.example.com")]
    public void BypassRequiresPlainHost(string host)
    {
        var options = new UploadOptions { Mode = UploadMode.Bypass, BypassHost = host };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData(" upload.example.com ")]
    [InlineData("upload.example.com:8443")]
    public void BypassAcceptsHostAndOptionalPort(string host)
    {
        var options = new UploadOptions { Mode = UploadMode.Bypass, BypassHost = host };

        options.Validate();

        Assert.Equal(host.Trim(), options.BypassHost);
    }

    [Fact]
    public void ChunkedModeIgnoresBypassHost()
    {
        new UploadOptions { BypassHost = "not a host/" }.Validate();
    }

    [Fact]
    public void BypassHostMatchesRequestHostCaseInsensitively()
    {
        var options = new UploadOptions { Mode = UploadMode.Bypass, BypassHost = "upload.example.com" };
        options.Validate();
        var request = new DefaultHttpContext().Request;

        request.Host = new HostString("UPLOAD.example.com");
        Assert.True(options.IsBypassHost(request));

        request.Host = new HostString("example.com");
        Assert.False(options.IsBypassHost(request));
    }
}
