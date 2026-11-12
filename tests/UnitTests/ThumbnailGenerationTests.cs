using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Website.Data;
using Website.Models;
using Website.Pages;
using Website.Services;

namespace UnitTests;

public sealed partial class NavigationSettingsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    public void ExistingThumbnailsHaveStableVersionedUrls(string? metadata)
    {
        var item = new Item { ThumbnailKey = "key", Metadata = metadata };
        Assert.Equal("/img_db_thumbs/key.png?v=1", item.ThumbnailUrl);
        Assert.Equal(item.ThumbnailUrl, new Item { ThumbnailKey = "key", Metadata = metadata }.ThumbnailUrl);
    }

    [Fact]
    public void ThumbnailVersionsChangeAndPersistWithoutReplacingOtherMetadata()
    {
        var item = new Item { ThumbnailKey = "key", Metadata = """{"custom":{"preserved":true}}""" };
        var oldUrl = item.ThumbnailUrl;
        item.RefreshThumbnailVersion();
        var firstUrl = item.ThumbnailUrl;
        Assert.NotEqual(oldUrl, firstUrl);
        Assert.Equal(firstUrl, new Item { ThumbnailKey = "key", Metadata = item.Metadata }.ThumbnailUrl);
        item.RefreshThumbnailVersion();
        Assert.NotEqual(firstUrl, item.ThumbnailUrl);
        using var metadata = JsonDocument.Parse(item.Metadata!);
        Assert.True(metadata.RootElement.GetProperty("custom").GetProperty("preserved").GetBoolean());
    }

    [Fact]
    public void ThumbnailVersionIsUrlEncoded()
    {
        var item = new Item { ThumbnailKey = "key", Metadata = """{"thumbnailVersion":"a&b"}""" };
        Assert.Equal("/img_db_thumbs/key.png?v=a%26b", item.ThumbnailUrl);
    }

    [Fact]
    public async Task SearchRendersPersistedThumbnailVersion()
    {
        var model = new SearchModel(Services.GetRequiredService<AppDbContext>(), Configuration,
            Services.GetRequiredService<IWebHostEnvironment>(), Services.GetRequiredService<AdminOnlyTagsProvider>())
        {
            PageContext = CreatePageContext("/Search", admin: false)
        };
        var item = new Item { OriginalName = "photo.png", ThumbnailKey = "key", Type = "image" };
        item.RefreshThumbnailVersion();
        model.Results.Add(item);
        var html = await RenderAsync("/Pages/Search.cshtml", model);
        Assert.Contains($"src=\"{item.ThumbnailUrl}\"", html);
    }

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
