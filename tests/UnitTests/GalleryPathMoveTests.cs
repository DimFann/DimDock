using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Website.Data;
using Website.Models;
using Website.Services;

namespace UnitTests;

public sealed partial class NavigationSettingsTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public async Task GalleryPathMoveUsesSharedPickerIncludingEmptyPaths(bool admin, bool root, bool hasItems)
    {
        var path = root ? "root" : "Album";
        var model = new GalleryModel(
            Services.GetRequiredService<AppDbContext>(), Services.GetRequiredService<ILogger<GalleryModel>>(),
            Configuration, Services.GetRequiredService<IWebHostEnvironment>(),
            Services.GetRequiredService<IAntiforgery>(), Services.GetRequiredService<AdminOnlyTagsProvider>(),
            Services.GetRequiredService<FilesystemPersistenceService>(), Store,
            Services.GetRequiredService<IHttpClientFactory>(), new UploadOptions(),
            Services.GetRequiredService<GalleryFilterPreferences>())
        {
            PageContext = CreatePageContext("/Gallery", admin),
            PathEntry = new PathEntry { Path = path },
            CurrentPath = path,
            ParentPath = root ? null : "root",
            Items = hasItems
                ? [new Item { Id = 1, OriginalName = "image.png", ParentPath = path, ThumbnailKey = "image", Type = "image" }]
                : []
        };
        var html = await RenderAsync("/Pages/Gallery.cshtml", model);
        Assert.Equal(admin && !root, html.Contains("data-open-move-path>Move Path</button>"));
        Assert.Equal(admin, html.Contains("data-bulk-move-dialog"));
        Assert.Equal(admin, html.Contains("let movingGalleryPath = false;"));
        if (admin)
        {
            Assert.Contains("data-move-path-note", html);
            Assert.Contains("movePath ? \"Move Path\" : \"Move selected items\"", html);
            Assert.Contains("movingGalleryPath ? \"MovePath\" : \"BulkMove\"", html);
        }
    }
}
