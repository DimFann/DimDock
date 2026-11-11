using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Website.Data;
using Website.Models;
using Website.Services;

namespace UnitTests;

public sealed partial class NavigationSettingsTests
{
    [Fact]
    public async Task SettingsArePublicEvenWhenEverySectionIsHidden()
    {
        await Provider.SaveAsync(new NavigationSettings { Gallery = false, Search = false, Blog = false, Commissions = false });
        using var client = CreateClient();
        var html = await client.GetStringAsync("/Settings");
        Assert.Contains(">Settings</a>", html);
        Assert.Contains("Blur NSFW (Default)", html);
        Assert.Contains("value=\"BlurNsfw\" selected=\"selected\"", html);

        using var attempt = await client.PostAsync("/Settings",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = ExtractToken(html),
                ["Filter"] = "ShowAll"
            }));
        Assert.Equal(HttpStatusCode.OK, attempt.StatusCode);
        var prompt = await attempt.Content.ReadAsStringAsync();
        Assert.Contains("I am 18 or older", prompt);
        Assert.Matches("<dialog[^>]*open=\"open\"", prompt);

        using var confirm = await client.PostAsync("/Settings",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = ExtractToken(prompt),
                ["Filter"] = "ShowAll",
                ["confirmAdult"] = "true"
            }));
        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
        var saved = await client.GetStringAsync("/Settings");
        Assert.Contains("value=\"ShowAll\" selected=\"selected\"", saved);
        Assert.Contains("data-adult-confirmed=\"true\"", saved);
    }

    [Fact]
    public async Task SettingsRejectInvalidFilterAndMissingAntiforgery()
    {
        using var client = CreateClient();
        var html = await client.GetStringAsync("/Settings");
        using var invalid = await client.PostAsync("/Settings",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = ExtractToken(html),
                ["Filter"] = "999"
            }));
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Contains("field-validation-error", await invalid.Content.ReadAsStringAsync());
        using var noToken = await client.PostAsync("/Settings",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["Filter"] = "ShowAll", ["confirmAdult"] = "true" }));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ActualGalleryAndComicMarkupBlurOnlyNsfwForVisitors(bool admin, bool comic)
    {
        var model = new GalleryModel(
            Services.GetRequiredService<AppDbContext>(), Services.GetRequiredService<ILogger<GalleryModel>>(),
            Configuration, Services.GetRequiredService<IWebHostEnvironment>(),
            Services.GetRequiredService<IAntiforgery>(), Services.GetRequiredService<AdminOnlyTagsProvider>(),
            Services.GetRequiredService<FilesystemPersistenceService>(), Store,
            Services.GetRequiredService<IHttpClientFactory>(), new UploadOptions(),
            Services.GetRequiredService<GalleryFilterPreferences>())
        {
            PageContext = CreatePageContext("/Gallery", admin),
            PathEntry = new PathEntry { Path = "Album", Type = comic ? "comic" : null },
            CurrentPath = "Album",
            ParentPath = "root",
            Items =
            [
                new Item { Id = 1, OriginalName = "1-adult.png", ParentPath = "Album", ThumbnailKey = "adult", Type = "image",
                    TagLinks = [new ItemTag { Tag = new Tag { Name = "nsfw" } }] },
                new Item { Id = 2, OriginalName = "2-safe.png", ParentPath = "Album", ThumbnailKey = "safe", Type = "image" }
            ]
        };
        var view = Services.GetRequiredService<IRazorViewEngine>().GetView(null, "/Pages/Shared/_GalleryResults.cshtml", false);
        Assert.True(view.Success);
        using var output = new StringWriter();
        var data = new ViewDataDictionary<GalleryModel>(new EmptyModelMetadataProvider(), model.ModelState) { Model = model };
        await view.View.RenderAsync(new ViewContext(model.PageContext, view.View, data,
            new TempDataDictionary(model.HttpContext, Services.GetRequiredService<ITempDataProvider>()),
            output, new HtmlHelperOptions()));
        var html = output.ToString();

        Assert.Equal(!admin, Regex.IsMatch(html, "class=\"gallery-image-result [^\"]*nsfw-blurred[^\"]*\" data-gallery-item-id=\"1\""));
        Assert.DoesNotMatch("class=\"gallery-image-result [^\"]*nsfw-blurred[^\"]*\" data-gallery-item-id=\"2\"", html);
        Assert.Equal(admin, Regex.IsMatch(html, "class=\"gallery-image-result [^\"]*nsfw-admin-item[^\"]*\" data-gallery-item-id=\"1\""));
        if (comic || admin)
        {
            Assert.Contains($"data-blur=\"{(!admin).ToString().ToLowerInvariant()}\"", html);
            Assert.Contains("data-blur=\"false\"", html);
            Assert.Contains($"data-nsfw-admin=\"{admin.ToString().ToLowerInvariant()}\"", html);
        }
    }
}
