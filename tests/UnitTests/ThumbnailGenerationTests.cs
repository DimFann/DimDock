using System.Net;
using Website.Services;

namespace UnitTests;

public sealed partial class NavigationSettingsTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ThumbnailRegenerationOverwritesOnlyAfterServiceSuccess(HttpStatusCode status)
    {
        await Store.WriteAllTextAsync(FileStoreArea.Gallery, "source.png", "original");
        await Store.WriteAllTextAsync(FileStoreArea.Thumbnails, "key.png", "old-thumbnail");
        using var client = new HttpClient(new ThumbnailResponseHandler(status))
        {
            BaseAddress = new Uri("http://thumbnail.test/")
        };
        if (status == HttpStatusCode.OK)
        {
            await ThumbnailGenerator.GenerateAsync(Store, client, "source.png", "key", default);
        }
        else if (status == HttpStatusCode.BadRequest)
        {
            await Assert.ThrowsAsync<InvalidThumbnailImageException>(() =>
                ThumbnailGenerator.GenerateAsync(Store, client, "source.png", "key", default));
        }
        else
        {
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                ThumbnailGenerator.GenerateAsync(Store, client, "source.png", "key", default));
        }
        Assert.Equal(status == HttpStatusCode.OK ? "new-thumbnail" : "old-thumbnail",
            await Store.ReadAllTextAsync(FileStoreArea.Thumbnails, "key.png"));
        Assert.Equal("original", await Store.ReadAllTextAsync(FileStoreArea.Gallery, "source.png"));
    }

    private sealed class ThumbnailResponseHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/thumbnail/key", request.RequestUri!.AbsolutePath);
            Assert.Equal("original", await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status) { Content = new StringContent("new-thumbnail") };
        }
    }
}
