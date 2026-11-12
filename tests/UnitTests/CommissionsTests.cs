using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Website.Models;
using Website.Services;

namespace UnitTests;

public sealed partial class NavigationSettingsTests
{
    [Fact]
    public async Task ExistingNavigationFilesRemainCompatibleWithCommissions()
    {
        await Store.WriteAllTextAsync(FileStoreArea.Admin, "navigation.json",
            """{"Gallery":false,"Search":true,"Blog":false}""");

        var settings = await Provider.GetAsync();

        Assert.True(settings.Commissions);
        Assert.False(settings.Gallery);
        Assert.True(settings.Search);
        Assert.False(settings.Blog);
    }

    [Fact]
    public async Task StartupCreatesEveryStorageAreaAndPreservesExistingContent()
    {
        foreach (var area in Enum.GetValues<FileStoreArea>())
        {
            Assert.True(await Store.DirectoryExistsAsync(area, ""));
            Assert.True(Directory.Exists(Path.Combine(_root, FileStoreKeys.AreaName(area))));
        }

        await Store.WriteAllTextAsync(FileStoreArea.About, "about.md", "Preserve content");
        Directory.Delete(Path.Combine(_root, "commissions"));

        await new FileStoreInitializationService(Store).StartAsync(CancellationToken.None);

        Assert.True(await Store.DirectoryExistsAsync(FileStoreArea.Commissions, ""));
        Assert.Equal("Preserve content", await Store.ReadAllTextAsync(FileStoreArea.About, "about.md"));
    }

    [Fact]
    public async Task StartupStorageErrorsAreNotIgnored()
    {
        Directory.Delete(Path.Combine(_root, "commissions"));
        await File.WriteAllTextAsync(Path.Combine(_root, "commissions"), "Not a directory");

        await Assert.ThrowsAsync<IOException>(() =>
            new FileStoreInitializationService(Store).StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CommissionsMarkdownCanBeLoadedPreviewedSavedAndReloaded()
    {
        using var visitor = CreateClient();
        var empty = await visitor.GetStringAsync("/commissions");
        Assert.Contains(">Commissions</a>", empty);
        Assert.DoesNotContain("data-open-about-editor", empty);

        await Store.WriteAllTextAsync(FileStoreArea.Commissions, "commissions.md", "# Current content");
        using var client = await SignInAsync();
        var html = await client.GetStringAsync("/commissions");
        Assert.Contains("<h1>Current content</h1>", html);
        Assert.Contains("Edit Commissions Markdown", html);
        Assert.Contains("data-about-image-input", html);
        Assert.Contains("data-preview-about", html);
        var token = ExtractToken(html);

        using var preview = await client.PostAsync("/commissions?handler=Preview",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["markdown"] = "**Preview only**"
            }));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Contains("<strong>Preview only</strong>", await preview.Content.ReadAsStringAsync());
        Assert.Equal("# Current content", await Store.ReadAllTextAsync(FileStoreArea.Commissions, "commissions.md"));

        using var save = await client.PostAsync("/commissions?handler=Save",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["markdown"] = "# Updated\n\nNew **content**."
            }));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        using var result = JsonDocument.Parse(await save.Content.ReadAsStringAsync());
        Assert.Contains("<strong>content</strong>", result.RootElement.GetProperty("html").GetString());
        Assert.Equal("# Updated\n\nNew **content**.",
            await File.ReadAllTextAsync(Path.Combine(_root, "commissions", "commissions.md")));
        var reloaded = await visitor.GetStringAsync("/commissions");
        Assert.Contains("<h1>Updated</h1>", reloaded);
        Assert.DoesNotContain("data-open-about-editor", reloaded);
    }

    [Fact]
    public async Task CommissionsUploadsAreIsolatedAndUnusedImagesAreCleanedUp()
    {
        using var client = await SignInAsync();
        var token = ExtractToken(await client.GetStringAsync("/commissions"));
        using var upload = new MultipartFormDataContent();
        upload.Add(new StringContent(token), "__RequestVerificationToken");
        var image = new ByteArrayContent(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="));
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        upload.Add(image, "image", "example.png");

        using var response = await client.PostAsync("/commissions?handler=UploadImage", upload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var url = result.RootElement.GetProperty("url").GetString()!;
        Assert.StartsWith("/commissions/assets/", url);
        var key = url["/commissions/".Length..];
        Assert.True(await Store.FileExistsAsync(FileStoreArea.Commissions, key));
        Assert.False(await Store.FileExistsAsync(FileStoreArea.About, key));

        using var save = await client.PostAsync("/commissions?handler=Save",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["markdown"] = $"![Example]({url})",
                ["uploadedImageUrls"] = url
            }));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        Assert.True(await Store.FileExistsAsync(FileStoreArea.Commissions, key));

        using var remove = await client.PostAsync("/commissions?handler=Save",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["markdown"] = "No images"
            }));
        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
        Assert.False(await Store.FileExistsAsync(FileStoreArea.Commissions, key));
    }

    [Fact]
    public async Task CommissionsVisibilityProtectsPageHandlersAndAssetsButKeepsAdminLink()
    {
        await Provider.SaveAsync(new NavigationSettings { Commissions = false });
        using var visitor = CreateClient();
        foreach (var path in new[] { "/commissions", "/COMMISSIONS", "/commissions/assets/example.png" })
        {
            using var response = await visitor.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Login?ReturnUrl=", response.Headers.Location!.OriginalString);
        }
        Assert.DoesNotContain(">Commissions</a>", await visitor.GetStringAsync("/Login"));

        using var admin = await SignInAsync();
        var html = await admin.GetStringAsync("/commissions");
        Assert.Contains("Edit Commissions Markdown", html);
        Assert.Matches("<a\\b[^>]*class=\"[^\"]*admin-only-tagged[^\"]*\"[^>]*>Commissions</a>", html);
        using var responseFromAdmin = await admin.PostAsync("/commissions?handler=Preview",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = ExtractToken(html),
                ["markdown"] = "Hidden preview"
            }));
        Assert.Equal(HttpStatusCode.OK, responseFromAdmin.StatusCode);
    }

    [Theory]
    [InlineData("Save")]
    [InlineData("Preview")]
    [InlineData("UploadImage")]
    public async Task CommissionsEditingRequiresAdminAndAntiforgery(string handler)
    {
        using var visitor = CreateClient();
        var token = ExtractToken(await visitor.GetStringAsync("/Login"));
        using var denied = await visitor.PostAsync($"/commissions?handler={handler}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["markdown"] = "Unauthorized"
            }));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var admin = await SignInAsync();
        using var missingToken = await admin.PostAsync($"/commissions?handler={handler}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["markdown"] = "Unauthorized" }));
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
        Assert.False(await Store.FileExistsAsync(FileStoreArea.Commissions, "commissions.md"));
    }

    [Theory]
    [InlineData(FileStoreArea.About)]
    [InlineData(FileStoreArea.Commissions)]
    public async Task SharedPageAssetCleanupPreservesReferencedImagesAndOtherAreas(FileStoreArea area)
    {
        var persistence = Services.GetRequiredService<FilesystemPersistenceService>();
        var name = $"{Guid.NewGuid():N}.png";
        var unused = $"{Guid.NewGuid():N}.png";
        await Store.WriteAllTextAsync(area, $"assets/{name}", "referenced");
        await Store.WriteAllTextAsync(area, $"assets/{unused}", "unused");
        await Store.WriteAllTextAsync(FileStoreArea.Blogs, $"assets/{unused}", "other area");
        var prefix = FileStoreKeys.AreaName(area);

        await persistence.DeleteUnreferencedPageAssetsAsync(area,
            $"![Before](/{prefix}/assets/{unused})", $"![Keep](/{prefix}/assets/{name})", []);

        Assert.True(await Store.FileExistsAsync(area, $"assets/{name}"));
        Assert.False(await Store.FileExistsAsync(area, $"assets/{unused}"));
        Assert.True(await Store.FileExistsAsync(FileStoreArea.Blogs, $"assets/{unused}"));
    }

    [Fact]
    public async Task SharedAboutEditorStillRendersTheSameControls()
    {
        var model = new EditableMarkdownContent("# About", "About", true);
        var view = Services.GetRequiredService<IRazorViewEngine>()
            .GetView(null, "/Pages/Shared/_EditableMarkdown.cshtml", isMainPage: false);
        Assert.True(view.Success);
        using var output = new StringWriter();
        var page = CreatePageContext("/Index", admin: true);
        var data = new ViewDataDictionary<EditableMarkdownContent>(
            new EmptyModelMetadataProvider(), page.ModelState) { Model = model };
        await view.View.RenderAsync(new ViewContext(page, view.View, data,
            new TempDataDictionary(page.HttpContext, Services.GetRequiredService<ITempDataProvider>()),
            output, new HtmlHelperOptions()));
        Assert.Contains("<h1>About</h1>", output.ToString());
        Assert.Contains("Edit About Markdown", output.ToString());
        Assert.Contains("DOMContentLoaded", output.ToString());
    }

    private HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = new Uri(_app.Urls.Single())
    };

    private async Task<HttpClient> SignInAsync()
    {
        var client = CreateClient();
        try
        {
            var html = await client.GetStringAsync("/Login");
            using var response = await client.PostAsync("/Login",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["Username"] = "test-admin",
                    ["Password"] = "test-password-only",
                    ["__RequestVerificationToken"] = ExtractToken(html)
                }));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static string ExtractToken(string html) => WebUtility.HtmlDecode(
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
}
