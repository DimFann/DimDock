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
            DirectoryLinks =
            [
                new GalleryDirectoryLink("Folder", "Folder", false, false),
                new GalleryDirectoryLink("Alias & name", "Target", false, true, 7)
            ],
            Items = hasItems
                ? [new Item { Id = 1, OriginalName = "image.png", ParentPath = path, ThumbnailKey = "image", Type = "image" }]
                : []
        };
        var html = await RenderAsync("/Pages/Gallery.cshtml", model);
        Assert.Equal(admin && !root, html.Contains("data-open-move-path>Move Path</button>"));
        Assert.Equal(admin, html.Contains("data-bulk-move-dialog"));
        Assert.Equal(admin, html.Contains("let movingGalleryPath = false;"));
        Assert.Equal(admin, html.Contains("class=\"directory-shortcut-delete-form\""));
        Assert.Equal(admin, html.Contains("data-move-directory-shortcut=\"7\""));
        Assert.Equal(admin, html.Contains("data-open-directory-shortcut>Shortcut To</button>"));
        Assert.DoesNotContain("Delete directory shortcut Folder", html);
        if (admin)
        {
            Assert.Contains("Delete directory shortcut Alias &amp; name", html);
            Assert.Contains("name=\"shortcutId\" value=\"7\"", html);
            Assert.Contains("handler=DeleteDirectoryShortcut", html);
            Assert.Contains("Move directory shortcut Alias &amp; name", html);
            Assert.True(html.IndexOf("data-move-directory-shortcut=\"7\"", StringComparison.Ordinal) <
                html.IndexOf("aria-label=\"Delete directory shortcut", StringComparison.Ordinal));
            Assert.Contains("src=\"/img/next.png\"", html);
            Assert.Contains("data-move-path-note", html);
            Assert.Contains("movePath ? \"Move Path\" : \"Move selected items\"", html);
            Assert.Contains("movingGalleryPath ? \"MovePath\" : \"BulkMove\"", html);
            Assert.Contains("creatingDirectoryShortcut ? \"CreateDirectoryShortcut\"", html);
            Assert.Contains("if (creatingDirectoryShortcut) {\n                    bulkMoveDialog.close();", html.Replace("\r\n", "\n"));
        }
    }
}
