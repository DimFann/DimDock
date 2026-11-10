using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Website.Data;
using Website.Models;
using Website.Services;

var supplied = Environment.GetEnvironmentVariable("DIRECTORY_SHORTCUT_TEST_CONNECTION")
    ?? throw new InvalidOperationException("Set DIRECTORY_SHORTCUT_TEST_CONNECTION to a disposable loopback MariaDB service.");
var connectionSettings = new MySqlConnectionStringBuilder(supplied);
if (connectionSettings.Server is not ("127.0.0.1" or "localhost") ||
    !string.IsNullOrEmpty(connectionSettings.Database))
    throw new InvalidOperationException("Checks require a loopback server with no database selected, never production.");
var database = $"dimdock_directory_checks_{Guid.NewGuid():N}";
var root = Path.Combine(Path.GetTempPath(), database);
await using var connection = new MySqlConnection(connectionSettings.ConnectionString);
await connection.OpenAsync();
await ExecuteAsync($"CREATE DATABASE `{database}` CHARACTER SET utf8mb4");
try
{
    await ExecuteAsync($"USE `{database}`");
    var schema = await File.ReadAllTextAsync(Path.Combine("init", "01-create-tables.sql"));
    foreach (var statement in Regex.Replace(schema, @"--[^\r\n]*", "").Split(';'))
        if (!string.IsNullOrWhiteSpace(statement))
            await ExecuteAsync(statement);
    connectionSettings.Database = database;

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        ApplicationName = typeof(GalleryModel).Assembly.GetName().Name,
        ContentRootPath = Path.GetFullPath("website")
    });
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["FileStorage:RootPath"] = root,
        ["AdminAuth:Username"] = "test-admin",
        ["AdminAuth:Password"] = "test-password-only",
    });
    builder.Logging.ClearProviders();
    builder.Services.AddRazorPages();
    builder.Services.AddHttpClient();
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseMySql(connectionSettings.ConnectionString, new MariaDbServerVersion(new Version(11, 4, 0))));
    builder.Services.AddSingleton<IFileStore, LocalFileStore>();
    builder.Services.AddScoped<FilesystemPersistenceService>();
    builder.Services.AddScoped<AdminOnlyTagsProvider>();
    await using var app = builder.Build();
    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;
    var context = services.GetRequiredService<AppDbContext>();
    var store = services.GetRequiredService<IFileStore>();
    var persistence = services.GetRequiredService<FilesystemPersistenceService>();
    var paths = new[]
    {
        new PathEntry { Path = "root", ParentPath = "root" },
        new PathEntry { Path = "Album", ParentPath = "root" },
        new PathEntry { Path = "Hidden", ParentPath = "root" },
        new PathEntry { Path = "Hidden/Child", ParentPath = "Hidden" },
        new PathEntry { Path = "Empty", ParentPath = "root" },
        new PathEntry { Path = "Comic", ParentPath = "root", Type = "comic" },
        new PathEntry { Path = "With space", ParentPath = "root" },
    };
    context.Paths.AddRange(paths);
    await context.SaveChangesAsync();
    var hidden = new Tag { Name = "hidden" };
    context.PathTags.Add(new PathTag { PathId = paths[2].Id, Tag = hidden });
    context.AdminOnlyTagSettings.Add(new AdminOnlyTagSettings { Id = 1, TagsJson = "[\"hidden\"]" });
    context.DirectoryShortcuts.AddRange(
        Link(paths[0], paths[1], "My & favourites"),
        Link(paths[0], paths[3], "Secret alias"),
        Link(paths[0], paths[5], "Comic alias"),
        Link(paths[0], paths[6], "Space alias"),
        Link(paths[4], paths[0], "Home"));
    await context.SaveChangesAsync();
    foreach (var path in paths)
        await store.CreateDirectoryAsync(FileStoreArea.Gallery, path.Path == "root" ? "" : path.Path);
    await store.WriteAllTextAsync(FileStoreArea.Gallery, ".shortcuts.json",
        """{"shortcuts":{"root":["Album/file.png"]},"custom":{"preserved":true}}""");

    var visitor = await GetGalleryAsync("root");
    Check(visitor.DirectoryLinks.Any(link => link.Name == "My & favourites" && link.Path == "Album"), "directory alias alongside ordinary entries");
    Check(visitor.DirectoryLinks.Any(link => link.Name == "Album" && !link.IsShortcut), "ordinary navigation preserved");
    Check(!visitor.DirectoryLinks.Any(link => link.Name == "Secret alias"), "hidden ancestor hides directory alias");
    Check(visitor.DirectoryLinks.Select(link => link.Name).SequenceEqual(
        visitor.DirectoryLinks.Select(link => link.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)), "combined navigation sorting");
    var html = await RenderAsync(visitor);
    Check(html.Contains("href=\"/Gallery/Album\">My &amp; favourites</a>"), "actual Razor alias link and HTML encoding");
    Check(html.Contains("href=\"/Gallery/With%20space\">Space alias</a>"), "actual Razor target URL encoding");
    Check(!html.Contains("Secret alias"), "hidden alias absent from rendered navigation");
    var admin = await GetGalleryAsync("root", admin: true);
    Check(admin.DirectoryLinks.Single(link => link.Name == "Secret alias").IsAdminOnly, "admin alias inherits hidden ancestor styling");
    Check((await RenderAsync(admin)).Contains("class=\"admin-only-tagged\""), "actual Razor admin styling");
    var empty = await GetGalleryAsync("Empty");
    Check(empty.Message is null && empty.DirectoryLinks.Single().Path == "root", "alias-only directory has no empty warning");
    Check((await RenderAsync(empty)).Contains("href=\"/Gallery/\">Home</a>"), "root shortcut navigation");
    var comic = await GetGalleryAsync("Comic");
    Check(comic.PathEntry?.Type == "comic", "canonical comic target retains display type");
    Check(await GetGalleryAsync("Hidden/Child") is { Message: "NOT_FOUND" }, "canonical hidden target rejects visitors");
    Console.WriteLine("PASS database-backed navigation, hidden visibility, comic/root targets and actual Razor rendering");

    await persistence.PersistDirectoryShortcutsAsync();
    await CheckManifestAsync("Album", "root");
    await persistence.RenamePathAsync("Album", "Renamed");
    await CheckManifestAsync("Renamed", "root");
    await persistence.RenamePathAsync("Empty", "Destination");
    await CheckManifestAsync("Renamed", "Destination");
    Check((await GetGalleryAsync("root")).DirectoryLinks.Any(link => link.Name == "My & favourites" && link.Path == "Renamed"),
        "renamed target keeps alias label");
    await context.Paths.Where(path => path.Path == "Renamed").ExecuteDeleteAsync();
    await persistence.DeletePathDirectoryAsync("Renamed");
    Check(!await context.DirectoryShortcuts.AnyAsync(link => link.Name == "My & favourites"), "target deletion cascades");
    await context.Paths.Where(path => path.Path == "Destination").ExecuteDeleteAsync();
    await persistence.DeletePathDirectoryAsync("Destination");
    Check(!await context.DirectoryShortcuts.AnyAsync(link => link.Name == "Home"), "destination deletion cascades");
    using var updated = JsonDocument.Parse(await store.ReadAllTextAsync(FileStoreArea.Gallery, ".shortcuts.json"));
    Check(!updated.RootElement.GetProperty("directory_shortcuts").TryGetProperty("Destination", out _), "deleted destination removed from persisted JSON");
    Check(updated.RootElement.GetProperty("shortcuts").GetProperty("root")[0].GetString() == "Album/file.png", "existing file shortcut data preserved");
    Console.WriteLine("PASS target/destination renames, deletion cascades, JSON persistence and file-shortcut preservation");

    static string ThumbnailKey(string parent, string name) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(parent == "root" ? name : $"{parent}/{name}")));
    var photo = new Item { OriginalName = "photo.png", ParentPath = "With space", ThumbnailKey = ThumbnailKey("With space", "photo.png"), Type = "image", Description = "kept" };
    var other = new Item { OriginalName = "other.png", ParentPath = "With space", ThumbnailKey = ThumbnailKey("With space", "other.png"), Type = "image" };
    context.Items.AddRange(photo, other);
    await context.SaveChangesAsync();
    context.Shortcuts.Add(new Shortcut { OriginalId = photo.Id, OriginalName = "photo.png", ParentPath = "With space", ShortcutPath = "root" });
    await context.SaveChangesAsync();
    await store.WriteAllTextAsync(FileStoreArea.Gallery, "With space/photo.png", "photo-bytes");
    await store.WriteAllTextAsync(FileStoreArea.Gallery, "With space/other.png", "other-bytes");
    await store.WriteAllTextAsync(FileStoreArea.Thumbnails, $"{photo.ThumbnailKey}.png", "thumb");
    context.ChangeTracker.Clear();

    var renamed = await persistence.RenameItemAsync(photo.Id, " Photo New.png ");
    Check(renamed.OriginalName == "Photo New.png", "rename trims and returns the new name");
    Check(await store.ReadAllTextAsync(FileStoreArea.Gallery, "With space/Photo New.png") == "photo-bytes" &&
        !await store.FileExistsAsync(FileStoreArea.Gallery, "With space/photo.png"), "item file moved");
    Check(renamed.ThumbnailKey == ThumbnailKey("With space", "Photo New.png") &&
        await store.FileExistsAsync(FileStoreArea.Thumbnails, $"{renamed.ThumbnailKey}.png") &&
        !await store.FileExistsAsync(FileStoreArea.Thumbnails, $"{photo.ThumbnailKey}.png"), "thumbnail moved to the new path key");
    var stored = await context.Items.AsNoTracking().SingleAsync(item => item.Id == photo.Id);
    Check(stored.OriginalName == "Photo New.png" && stored.ThumbnailKey == renamed.ThumbnailKey && stored.Description == "kept", "database item updated");
    Check((await context.Shortcuts.AsNoTracking().SingleAsync(shortcut => shortcut.OriginalId == photo.Id)).OriginalName == "Photo New.png", "shortcut name updated");
    using (var manifest = JsonDocument.Parse(await store.ReadAllTextAsync(FileStoreArea.Gallery, "With space/.dimdock.json")))
        Check(manifest.RootElement.ToString().Contains("Photo New.png") && !manifest.RootElement.ToString().Contains("\"photo.png\""), "directory manifest rewritten");

    await persistence.RenameItemAsync(photo.Id, "photo new.png");
    Check((await store.ListFilesAsync(FileStoreArea.Gallery, "With space")).Any(key => key.EndsWith("/photo new.png", StringComparison.Ordinal)),
        "case-only rename changes stored casing");
    foreach (var (name, reason) in new[] { ("photo.jpg", "extension change"), (".hidden.png", "leading dot"), ("a/b.png", "separator"), ("other.png", "duplicate") })
    {
        try
        {
            await persistence.RenameItemAsync(photo.Id, name);
            Check(false, $"rename rejects {reason}");
        }
        catch (GalleryItemRenameException)
        {
        }
    }
    Check(await store.FileExistsAsync(FileStoreArea.Gallery, "With space/photo new.png"), "rejected renames leave the file in place");
    // Gallery URLs strip spaces, so render the root where the item's shortcut appears.
    var adminHtml = await RenderAsync(await GetGalleryAsync("root", admin: true));
    Check(adminHtml.Contains($"data-item-id=\"{photo.Id}\"") && adminHtml.Contains("photo new.png"), "admin gallery renders rename controls");
    var visitorHtml = await RenderAsync(await GetGalleryAsync("root"));
    Check(visitorHtml.Contains("photo new.png") && !visitorHtml.Contains("data-open-rename-item"), "visitors get no rename controls");
    Console.WriteLine("PASS item rename: file, thumbnail, database, shortcuts, manifest, case-only and validation");

    var draftId = FilesystemPersistenceService.CreateBlogDraftId();
    async Task<string> UploadDraftImageAsync(string id)
    {
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "image", "pic.png");
        return await persistence.SaveBlogDraftAssetAsync(id, file);
    }
    var keptUrl = await UploadDraftImageAsync(draftId);
    var droppedUrl = await UploadDraftImageAsync(draftId);
    Check(keptUrl.StartsWith($"/blogs/assets/drafts/{draftId}/", StringComparison.Ordinal), "draft images staged per draft");
    var published = await persistence.PublishBlogDraftAssetsAsync(4242, draftId, $"# T\n\n![pic]({keptUrl} \"scale=50%\")");
    var keptName = keptUrl[(keptUrl.LastIndexOf('/') + 1)..];
    Check(published.Contains($"(/blogs/assets/4242/{keptName} ") && !published.Contains("/drafts/"), "published Markdown points at the post's assets");
    Check(await store.FileExistsAsync(FileStoreArea.Blogs, $"assets/4242/{keptName}"), "referenced draft image moved to the post");
    Check(!await store.FileExistsAsync(FileStoreArea.Blogs, $"assets/4242/{droppedUrl[(droppedUrl.LastIndexOf('/') + 1)..]}") &&
        !await store.DirectoryExistsAsync(FileStoreArea.Blogs, $"assets/drafts/{draftId}"), "unreferenced draft images deleted with the draft");
    var discarded = FilesystemPersistenceService.CreateBlogDraftId();
    await UploadDraftImageAsync(discarded);
    await persistence.DeleteBlogDraftAssetsAsync(discarded);
    Check(!await store.DirectoryExistsAsync(FileStoreArea.Blogs, $"assets/drafts/{discarded}"), "discarded draft deleted");
    var stale = $"{DateTime.UtcNow.AddDays(-2):yyyyMMddHHmmss}{Guid.NewGuid():N}";
    await UploadDraftImageAsync(stale);
    await UploadDraftImageAsync(FilesystemPersistenceService.CreateBlogDraftId());
    Check(!await store.DirectoryExistsAsync(FileStoreArea.Blogs, $"assets/drafts/{stale}"), "stale drafts swept on upload");
    Check(!FilesystemPersistenceService.IsValidBlogDraftId("../x") && !FilesystemPersistenceService.IsValidBlogDraftId(draftId.ToUpperInvariant()), "draft IDs validated");
    Console.WriteLine("PASS new-post draft images: staging, publish move/rewrite, discard and stale sweep");

    DirectoryShortcut Link(PathEntry parent, PathEntry target, string name) =>
        new() { ParentPathId = parent.Id, TargetPathId = target.Id, Name = name };

    async Task<GalleryModel> GetGalleryAsync(string path, bool admin = false)
    {
        var http = new DefaultHttpContext { RequestServices = services };
        if (admin)
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")], "test"));
        var action = new ActionContext(http, new RouteData(), new Microsoft.AspNetCore.Mvc.RazorPages.PageActionDescriptor());
        var model = new GalleryModel(context, services.GetRequiredService<ILogger<GalleryModel>>(),
            builder.Configuration, services.GetRequiredService<IWebHostEnvironment>(),
            services.GetRequiredService<IAntiforgery>(), services.GetRequiredService<AdminOnlyTagsProvider>(),
            persistence, store, services.GetRequiredService<IHttpClientFactory>(), new UploadOptions())
        {
            PageContext = new PageContext(action)
        };
        var result = await model.OnGetAsync(path);
        if (result is NotFoundResult)
            model.Message = "NOT_FOUND";
        return model;
    }

    async Task<string> RenderAsync(GalleryModel model)
    {
        var engine = services.GetRequiredService<IRazorViewEngine>();
        var view = engine.GetView(null, "/Pages/Shared/_GalleryResults.cshtml", false);
        Check(view.Success, "compiled gallery partial found");
        var galleryView = view.View ?? throw new InvalidOperationException("Compiled gallery view is missing.");
        using var output = new StringWriter();
        var viewData = new ViewDataDictionary<GalleryModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            Model = model
        };
        var viewContext = new ViewContext(model.PageContext, galleryView, viewData,
            new TempDataDictionary(model.HttpContext, services.GetRequiredService<ITempDataProvider>()),
            output, new HtmlHelperOptions());
        await galleryView.RenderAsync(viewContext);
        return output.ToString();
    }

    async Task CheckManifestAsync(string target, string destination)
    {
        using var document = JsonDocument.Parse(await store.ReadAllTextAsync(FileStoreArea.Gallery, ".shortcuts.json"));
        var data = document.RootElement;
        Check(data.GetProperty("custom").GetProperty("preserved").GetBoolean(), "unrelated JSON properties preserved");
        Check(data.GetProperty("directory_shortcuts").GetProperty("root").EnumerateArray()
            .Any(entry => entry.GetProperty("name").GetString() == "My & favourites" &&
                entry.GetProperty("target_path").GetString() == target), "target path persisted");
        Check(data.GetProperty("directory_shortcuts").GetProperty(destination == "root" ? "Empty" : destination)[0]
            .GetProperty("target_path").GetString() == "root", "destination path persisted");
    }
}
finally
{
    await ExecuteAsync($"DROP DATABASE IF EXISTS `{database}`");
    if (Directory.Exists(root))
        Directory.Delete(root, recursive: true);
}

async Task ExecuteAsync(string sql)
{
    await using var command = new MySqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
}

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException($"FAIL: {message}");
}
