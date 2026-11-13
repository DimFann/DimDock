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
    builder.Services.AddScoped<GalleryPathReloader>();
    builder.Services.AddSingleton<GalleryPathOperations>();
    builder.Services.AddScoped<AdminOnlyTagsProvider>();
    builder.Services.AddSingleton<GalleryFilterPreferences>();
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
    foreach (var isAdmin in new[] { false, true })
    {
        var missing = await GetGalleryAsync("root", admin: isAdmin);
        Check(await missing.OnGetAsync("Missing/Folder") is RedirectToPageResult
            { PageName: "/Gallery" } redirect && redirect.RouteValues!["path"] is null,
            "missing gallery path redirects to root without ambient path");
        missing.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
        Check(await missing.OnGetAsync("Missing/Folder") is RedirectToPageResult { PageName: "/Gallery" },
            "missing partial gallery path redirects to root");
        var existingEmpty = await GetGalleryAsync("root", admin: isAdmin);
        Check(await existingEmpty.OnGetAsync("Comic") is PageResult, "existing empty folder stays accessible");
    }
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
    Check(updated.RootElement.GetProperty("shortcuts").GetProperty("root")[0].GetString() == "Renamed/file.png", "file shortcut source follows renamed path");
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

    var bulkItem = new Item { OriginalName = "bulk.png", ParentPath = "root", Type = "image", ThumbnailKey = ThumbnailKey("root", "bulk.png") };
    var keptTag = new Tag { Name = "keep-me" };
    bulkItem.TagLinks.Add(new ItemTag { Tag = keptTag });
    context.Items.Add(bulkItem);
    await context.SaveChangesAsync();
    var photoShortcut = await context.Shortcuts.AsNoTracking().SingleAsync(shortcut => shortcut.OriginalId == photo.Id);

    Check(await AddTagsAsync("root", [bulkItem.Id], [photoShortcut.Id], " New-Tag, NSFW, new-tag ") is JsonResult,
        "bulk add tags accepts originals and shortcuts");
    Check(await AddTagsAsync("root", [bulkItem.Id], [photoShortcut.Id], "new-tag, nsfw") is JsonResult,
        "repeated bulk additions are idempotent");
    context.ChangeTracker.Clear();
    var bulkTags = await context.ItemTags.Where(link => link.ItemId == bulkItem.Id).Select(link => link.Tag.Name).ToListAsync();
    Check(bulkTags.Order().SequenceEqual(new[] { "keep-me", "new-tag", "nsfw" }), "bulk additions preserve existing tags and deduplicate");
    var photoTags = await context.ItemTags.Where(link => link.ItemId == photo.Id).Select(link => link.Tag.Name).ToListAsync();
    Check(photoTags.Order().SequenceEqual(new[] { "new-tag", "nsfw" }), "shortcut selection tags its original item");
    using (var manifest = JsonDocument.Parse(await store.ReadAllTextAsync(FileStoreArea.Gallery, ".dimdock.json")))
        Check(manifest.RootElement.ToString().Contains("keep-me") && manifest.RootElement.ToString().Contains("nsfw"), "bulk tags persisted in current directory manifest");
    using (var manifest = JsonDocument.Parse(await store.ReadAllTextAsync(FileStoreArea.Gallery, "With space/.dimdock.json")))
        Check(manifest.RootElement.ToString().Contains("new-tag") && manifest.RootElement.ToString().Contains("nsfw"), "shortcut tags persisted in original directory manifest");
    Check(await AddTagsAsync("root", [bulkItem.Id, other.Id], [], "must-not-add") is BadRequestObjectResult,
        "bulk tags reject original items outside current gallery");
    Check(await AddTagsAsync("Empty", [], [photoShortcut.Id], "must-not-add") is BadRequestObjectResult,
        "bulk tags reject shortcuts outside current gallery");
    Check(await AddTagsAsync("root", [], [], "must-not-add") is BadRequestObjectResult, "bulk tags reject empty selection");
    Check(await AddTagsAsync("root", [bulkItem.Id], [], ", ,") is BadRequestObjectResult, "bulk tags reject empty tags");
    Check(await AddTagsAsync("root", [bulkItem.Id], [], new string('x', 101)) is BadRequestObjectResult, "bulk tags validate tag lengths");
    Check(await AddTagsAsync("root", Enumerable.Range(1, 501).Select(value => (ulong)value).ToList(), [], "must-not-add") is BadRequestObjectResult,
        "bulk tags enforce selection limit");
    Check(await AddTagsAsync("root", [bulkItem.Id], [], "must-not-add", admin: false) is NotFoundResult,
        "bulk tags require admin authorization");
    Check(await AddTagsAsync("root", [bulkItem.Id], [], "must-not-add", validToken: false) is BadRequestObjectResult,
        "bulk tags require antiforgery");
    Check(!await context.Tags.AnyAsync(tag => tag.Name == "must-not-add"), "invalid requests do not create tags");
    Console.WriteLine("PASS bulk add tags: preservation, idempotency, shortcuts, manifests, scope, limits, authorization and antiforgery");

    var movePaths = new[]
    {
        new PathEntry { Path = "2020", ParentPath = "root" },
        new PathEntry { Path = "2020/Album", ParentPath = "2020", Description = "Keep description" },
        new PathEntry { Path = "2020/Album/Child", ParentPath = "2020/Album" },
        new PathEntry { Path = "2021", ParentPath = "root" },
        new PathEntry { Path = "2021/Album", ParentPath = "2021" }
    };
    context.Paths.AddRange(movePaths);
    await context.SaveChangesAsync();
    foreach (var entry in movePaths)
        await store.CreateDirectoryAsync(FileStoreArea.Gallery, entry.Path);
    var childItem = new Item { OriginalName = "child.png", ParentPath = "2020/Album/Child",
        ThumbnailKey = ThumbnailKey("2020/Album/Child", "child.png"), Type = "image" };
    childItem.TagLinks.Add(new ItemTag { Tag = new Tag { Name = "2020" } });
    childItem.TagLinks.Add(new ItemTag { TagId = keptTag.Id });
    context.Items.Add(childItem);
    await context.SaveChangesAsync();
    context.Shortcuts.AddRange(
        new Shortcut { OriginalId = childItem.Id, OriginalName = "child.png", ParentPath = childItem.ParentPath, ShortcutPath = "root" },
        new Shortcut { OriginalId = photo.Id, OriginalName = "photo new.png", ParentPath = "With space", ShortcutPath = "2020/Album/Child" });
    context.DirectoryShortcuts.Add(Link(movePaths[2], paths[6], "Outside"));
    context.DirectoryShortcuts.Add(Link(paths[0], movePaths[2], "Moved child"));
    await context.SaveChangesAsync();
    await store.WriteAllTextAsync(FileStoreArea.Gallery, "2020/Album/Child/child.png", "child-bytes");
    await store.WriteAllTextAsync(FileStoreArea.Thumbnails, childItem.ThumbnailKey + ".png", "child-thumb");
    await store.WriteAllTextAsync(FileStoreArea.Gallery, "2020/Album/.description.md", "Keep description");
    var shortcutConfig = System.Text.Json.Nodes.JsonNode.Parse(await store.ReadAllTextAsync(FileStoreArea.Gallery, ".shortcuts.json"))!;
    shortcutConfig["shortcuts"]!["2020/Album/Child"] = new System.Text.Json.Nodes.JsonArray("With space/photo new.png");
    shortcutConfig["shortcuts"]!["root"]!.AsArray().Add("2020/Album/Child/child.png");
    await store.WriteAllTextAsync(FileStoreArea.Gallery, ".shortcuts.json", shortcutConfig.ToJsonString());
    foreach (var (source, target) in new[] { ("root", "2021"), ("2020/Album", "2020/Album/Child"),
        ("2020/Album", "2020/Album"), ("2020/Album", "2020"), ("2020/Album", "missing") })
    {
        try { await persistence.MovePathAsync(source, target); Check(false, $"reject move {source} to {target}"); }
        catch (GalleryPathRenameException) { }
    }
    await context.Paths.Where(entry => entry.Path == "2021/Album").ExecuteDeleteAsync();
    await store.DeleteDirectoryAsync(FileStoreArea.Gallery, "2021/Album");
    Check(await MovePathAsync("2020/Album", "2021", admin: false) is NotFoundResult, "move requires admin");
    Check(await MovePathAsync("2020/Album", "2021", validToken: false) is BadRequestObjectResult, "move requires antiforgery");
    Check(await MovePathAsync("../2020/Album", "2021") is BadRequestObjectResult, "move rejects unsafe path");
    Check(await MovePathAsync("2020/Album", "2020/Album/Child") is ObjectResult { StatusCode: 409 }, "handler rejects descendant destination");
    var picker = (JsonResult)await (await GetGalleryAsync("2020/Album", admin: true))
        .OnGetSearchMovePathsAsync("2020/Album", "2020", movingPath: true);
    using (var json = JsonDocument.Parse(JsonSerializer.Serialize(picker.Value)))
        Check(json.RootElement.EnumerateArray().All(entry => entry.GetProperty("unavailable").GetBoolean()),
            "path picker disables parent, self and descendant destinations");
    Check(await MovePathAsync("2020/Album", "2021") is JsonResult { Value: not null } moveResult &&
        JsonSerializer.Serialize(moveResult.Value).Contains("/Gallery/2021/Album"), "move handler returns new path");
    context.ChangeTracker.Clear();
    Check(await context.Paths.AnyAsync(entry => entry.Path == "2021/Album" && entry.ParentPath == "2021"), "moved root gets destination parent");
    Check(await context.Paths.AnyAsync(entry => entry.Path == "2021/Album/Child" && entry.ParentPath == "2021/Album"), "child path and parent updated");
    Check(await context.Items.AnyAsync(item => item.Id == childItem.Id && item.ParentPath == "2021/Album/Child"), "child item parent updated");
    Check(!await context.Paths.AnyAsync(entry => entry.Path.StartsWith("2020/Album")), "old subtree paths removed");
    Check(await store.ReadAllTextAsync(FileStoreArea.Gallery, "2021/Album/Child/child.png") == "child-bytes", "descendant file moved");
    Check(await store.ReadAllTextAsync(FileStoreArea.Gallery, "2021/Album/.description.md") == "Keep description", "description moved");
    Check(await store.FileExistsAsync(FileStoreArea.Thumbnails, childItem.ThumbnailKey + ".png"), "thumbnail references remain usable");
    Check(await context.Shortcuts.AnyAsync(link => link.OriginalId == childItem.Id && link.ParentPath == "2021/Album/Child"), "external item shortcut original path updated");
    Check(await context.Shortcuts.AnyAsync(link => link.OriginalId == photo.Id && link.ShortcutPath == "2021/Album/Child"), "contained item shortcut moved");
    Check(await context.DirectoryShortcuts.AnyAsync(link => link.ParentPath.Path == "2021/Album/Child" && link.Name == "Outside"), "contained directory shortcut moved");
    using (var manifest = JsonDocument.Parse(await store.ReadAllTextAsync(FileStoreArea.Gallery, "2021/Album/Child/.dimdock.json")))
        Check(manifest.RootElement.GetProperty("items").GetProperty("child.png").GetProperty("tags")[0].GetString() == "keep-me", "descendant metadata persisted");
    var childTags = await context.ItemTags.Where(link => link.ItemId == childItem.Id).Select(link => link.Tag.Name).ToListAsync();
    Check(childTags.Contains("2021") && childTags.Contains("keep-me") && !childTags.Contains("2020"), "derived date tags updated without losing explicit tags");
    Check((await GetGalleryAsync("root")).DirectoryLinks.Any(link => link.Name == "Moved child" && link.Path == "2021/Album/Child"), "directory shortcuts follow moved subtree");
    using (var manifest = JsonDocument.Parse(await store.ReadAllTextAsync(FileStoreArea.Gallery, ".shortcuts.json")))
    {
        Check(manifest.RootElement.ToString().Contains("2021/Album/Child"), "directory shortcut manifest rewritten");
        var fileShortcuts = manifest.RootElement.GetProperty("shortcuts");
        Check(fileShortcuts.GetProperty("root").EnumerateArray().Any(value => value.GetString() == "2021/Album/Child/child.png"), "persisted file shortcut source follows moved subtree");
        Check(fileShortcuts.GetProperty("2021/Album/Child")[0].GetString() == "With space/photo new.png", "persisted file shortcut destination follows moved subtree");
    }
    Check((await persistence.MovePathAsync("2021/Album", "root")).Path == "Album", "subtree can move to root");
    context.ChangeTracker.Clear();
    Check(await context.Paths.AnyAsync(entry => entry.Path == "Album" && entry.ParentPath == "root"), "root move updates parent");
    Check(await context.Items.AnyAsync(item => item.Id == childItem.Id && item.ParentPath == "Album/Child"), "root move updates descendants");

    // Simulate an interrupted earlier move: the destination exists and holds some of the files.
    context.Paths.Add(new PathEntry { Path = "2021/Album", ParentPath = "2021" });
    var dupeItem = new Item { OriginalName = "dupe.png", ParentPath = "Album/Child",
        ThumbnailKey = ThumbnailKey("Album/Child", "dupe.png"), Type = "image" };
    var alreadyMovedItem = new Item { OriginalName = "already.png", ParentPath = "Album/Child",
        ThumbnailKey = ThumbnailKey("Album/Child", "already.png"), Type = "image" };
    context.Items.AddRange(dupeItem, alreadyMovedItem);
    await context.SaveChangesAsync();
    await store.CreateDirectoryAsync(FileStoreArea.Gallery, "2021/Album/Child");
    await store.WriteAllTextAsync(FileStoreArea.Gallery, "Album/Child/dupe.png", "dupe-source");
    await store.WriteAllTextAsync(FileStoreArea.Gallery, "2021/Album/Child/dupe.png", "dupe-destination");
    await store.WriteAllTextAsync(FileStoreArea.Gallery, "2021/Album/Child/already.png", "already-bytes");
    var merge = await persistence.MovePathAsync("Album", "2021");
    context.ChangeTracker.Clear();
    Check(merge.Path == "2021/Album" && merge.Message?.Contains("1 item(s) already existed") == true, "merge reports left-behind duplicates");
    Check(await context.Items.AnyAsync(item => item.Id == childItem.Id && item.ParentPath == "2021/Album/Child") &&
        await store.ReadAllTextAsync(FileStoreArea.Gallery, "2021/Album/Child/child.png") == "child-bytes", "merge moves non-duplicate items");
    Check(await context.Items.AnyAsync(item => item.Id == alreadyMovedItem.Id && item.ParentPath == "2021/Album/Child"), "merge re-links files moved by an interrupted move");
    Check(await context.Items.AnyAsync(item => item.Id == dupeItem.Id && item.ParentPath == "Album/Child") &&
        await store.ReadAllTextAsync(FileStoreArea.Gallery, "Album/Child/dupe.png") == "dupe-source" &&
        await store.ReadAllTextAsync(FileStoreArea.Gallery, "2021/Album/Child/dupe.png") == "dupe-destination", "merge leaves duplicates untouched");
    Check(await context.Paths.AnyAsync(entry => entry.Path == "Album/Child") && await context.Paths.AnyAsync(entry => entry.Path == "Album"), "merge keeps sources with leftovers");
    Check(await context.Paths.AnyAsync(entry => entry.Path == "2021/Album/Child" && entry.ParentPath == "2021/Album"), "merge creates missing destination paths");
    Check(await context.Paths.AnyAsync(entry => entry.Path == "2021/Album" && entry.Description == "Keep description"), "merge fills blank destination description");
    Check(await context.Shortcuts.AnyAsync(link => link.OriginalId == photo.Id && link.ShortcutPath == "2021/Album/Child"), "merge moves displayed item shortcuts");
    Check(await context.ItemTags.AnyAsync(link => link.ItemId == childItem.Id && link.Tag.Name == "2021"), "merge updates date tags");

    await context.Items.Where(item => item.Id == dupeItem.Id).ExecuteDeleteAsync();
    await store.DeleteFileAsync(FileStoreArea.Gallery, "Album/Child/dupe.png");
    var cleanMerge = await persistence.MovePathAsync("Album", "2021");
    context.ChangeTracker.Clear();
    Check(cleanMerge.Path == "2021/Album" && !await context.Paths.AnyAsync(entry => entry.Path == "Album" || entry.Path == "Album/Child") &&
        !await store.DirectoryExistsAsync(FileStoreArea.Gallery, "Album"), "merge removes emptied sources");
    Check(await context.DirectoryShortcuts.AnyAsync(link => link.ParentPath.Path == "2021/Album/Child" && link.Name == "Outside") &&
        await context.DirectoryShortcuts.AnyAsync(link => link.TargetPath.Path == "2021/Album/Child" && link.Name == "Moved child"), "merge repoints directory shortcuts of removed sources");
    await context.Items.Where(item => item.Id == alreadyMovedItem.Id).ExecuteDeleteAsync();
    await store.DeleteFileAsync(FileStoreArea.Gallery, "2021/Album/Child/already.png");
    Check((await persistence.MovePathAsync("2021/Album", "root")).Message is null, "move without collision is not a merge");
    context.ChangeTracker.Clear();
    Console.WriteLine("PASS moving path subtree: destinations, collisions, child paths/items/files, shortcuts, date tags, thumbnails, manifests, root and merges");

    var deletionTarget = await context.Paths.SingleAsync(entry => entry.Path == "Album/Child");
    var deletionParent = await context.Paths.SingleAsync(entry => entry.Path == "root");
    var removableAlias = Link(deletionParent, deletionTarget, "Remove only this alias");
    context.DirectoryShortcuts.Add(removableAlias);
    await context.SaveChangesAsync();
    await persistence.PersistDirectoryShortcutsAsync();
    var aliasCount = await context.DirectoryShortcuts.CountAsync();
    var targetItemCount = await context.Items.CountAsync(item => item.ParentPath == deletionTarget.Path);
    Check(await DeleteDirectoryShortcutAsync("root", removableAlias.Id, admin: false, moveTo: "2021") is NotFoundResult, "shortcut move requires admin");
    Check(await DeleteDirectoryShortcutAsync("root", removableAlias.Id, validToken: false, moveTo: "2021") is BadRequestObjectResult, "shortcut move requires antiforgery");
    Check(await DeleteDirectoryShortcutAsync("../root", removableAlias.Id, moveTo: "2021") is BadRequestObjectResult, "shortcut move rejects unsafe source");
    Check(await DeleteDirectoryShortcutAsync("Album", removableAlias.Id, moveTo: "2021") is NotFoundObjectResult, "shortcut move validates source gallery");
    Check(await DeleteDirectoryShortcutAsync("root", removableAlias.Id, moveTo: "missing") is BadRequestObjectResult, "shortcut move requires existing destination");
    Check(await DeleteDirectoryShortcutAsync("root", removableAlias.Id, moveTo: "root") is ConflictObjectResult, "shortcut move rejects unchanged destination");
    var collision = Link(await context.Paths.SingleAsync(entry => entry.Path == "2021"), deletionTarget, removableAlias.Name);
    context.DirectoryShortcuts.Add(collision);
    await context.SaveChangesAsync();
    Check(await DeleteDirectoryShortcutAsync("root", removableAlias.Id, moveTo: "2021") is ConflictObjectResult, "shortcut move rejects duplicate alias name");
    context.DirectoryShortcuts.Remove(collision);
    await context.SaveChangesAsync();
    Check(await DeleteDirectoryShortcutAsync("root", removableAlias.Id, moveTo: "2021") is JsonResult, "shortcut move succeeds");
    context.ChangeTracker.Clear();
    Check(await context.DirectoryShortcuts.AnyAsync(link => link.Id == removableAlias.Id &&
        link.ParentPath.Path == "2021" && link.TargetPathId == deletionTarget.Id && link.Name == removableAlias.Name), "shortcut move preserves identity, name and target");
    Check(await context.DirectoryShortcuts.CountAsync() == aliasCount, "shortcut move does not add or remove other aliases");
    using (var manifest = JsonDocument.Parse(await store.ReadAllTextAsync(FileStoreArea.Gallery, ".shortcuts.json")))
    {
        var aliases = manifest.RootElement.GetProperty("directory_shortcuts");
        Check(aliases.GetProperty("2021").EnumerateArray().Any(entry => entry.GetProperty("name").GetString() == removableAlias.Name), "moved shortcut persisted at destination");
        Check(!aliases.GetProperty("root").EnumerateArray().Any(entry => entry.GetProperty("name").GetString() == removableAlias.Name), "moved shortcut removed from persisted source");
    }
    Check(await DeleteDirectoryShortcutAsync("2021", removableAlias.Id, moveTo: "root") is JsonResult, "shortcut can move back to root");
    context.ChangeTracker.Clear();
    Check(await store.ReadAllTextAsync(FileStoreArea.Gallery, "Album/Child/child.png") == "child-bytes" &&
        await context.Items.CountAsync(item => item.ParentPath == deletionTarget.Path) == targetItemCount, "shortcut move preserves linked folder contents");
    Console.WriteLine("PASS directory shortcut move: authorization, antiforgery, scope, collisions, root, JSON persistence and unchanged target");
    Check(await DeleteDirectoryShortcutAsync("root", removableAlias.Id, admin: false) is NotFoundResult, "shortcut delete requires admin");
    Check(await DeleteDirectoryShortcutAsync("root", removableAlias.Id, validToken: false) is BadRequestObjectResult, "shortcut delete requires antiforgery");
    Check(await DeleteDirectoryShortcutAsync("../root", removableAlias.Id) is BadRequestObjectResult, "shortcut delete rejects unsafe path");
    Check(await DeleteDirectoryShortcutAsync("Album", removableAlias.Id) is NotFoundObjectResult, "shortcut delete validates parent gallery");
    Check(await context.DirectoryShortcuts.CountAsync() == aliasCount, "rejected deletes preserve shortcuts");
    Check(await DeleteDirectoryShortcutAsync("root", removableAlias.Id) is JsonResult, "shortcut delete succeeds");
    Check(await context.DirectoryShortcuts.CountAsync() == aliasCount - 1 &&
        !await context.DirectoryShortcuts.AnyAsync(link => link.Id == removableAlias.Id), "only selected alias deleted");
    Check(await context.Paths.AnyAsync(entry => entry.Id == deletionTarget.Id) &&
        await context.Items.CountAsync(item => item.ParentPath == deletionTarget.Path) == targetItemCount, "shortcut deletion preserves target folder and items");
    Check(await store.ReadAllTextAsync(FileStoreArea.Gallery, "Album/Child/child.png") == "child-bytes", "shortcut deletion preserves target file");
    using (var manifest = JsonDocument.Parse(await store.ReadAllTextAsync(FileStoreArea.Gallery, ".shortcuts.json")))
    {
        Check(!manifest.RootElement.ToString().Contains("Remove only this alias"), "shortcut deletion persisted to JSON");
        Check(manifest.RootElement.ToString().Contains("Moved child"), "other aliases preserved in JSON");
        Check(manifest.RootElement.GetProperty("custom").GetProperty("preserved").GetBoolean() &&
            manifest.RootElement.GetProperty("shortcuts").GetProperty("root").GetArrayLength() > 0, "unrelated shortcut configuration preserved");
    }
    Check(await DeleteDirectoryShortcutAsync("root", removableAlias.Id) is NotFoundObjectResult, "already deleted alias returns not found");
    Console.WriteLine("PASS directory shortcut delete: authorization, antiforgery, scope, persistence and target folder/item/file preservation");

    Check(await CreateDirectoryShortcutAsync("Album/Child", "2021", admin: false) is NotFoundResult, "shortcut creation requires admin");
    Check(await CreateDirectoryShortcutAsync("Album/Child", "2021", validToken: false) is BadRequestObjectResult, "shortcut creation requires antiforgery");
    Check(await CreateDirectoryShortcutAsync("../Album", "2021") is BadRequestObjectResult, "shortcut creation rejects unsafe path");
    Check(await CreateDirectoryShortcutAsync("Album/Child", "missing") is NotFoundObjectResult, "shortcut creation requires destination");
    Check(await CreateDirectoryShortcutAsync("Album/Child", "Album") is ConflictObjectResult, "shortcut creation rejects existing child directory");
    Check(await CreateDirectoryShortcutAsync("Album/Child", "2021") is JsonResult, "shortcut creation succeeds");
    Check(await context.DirectoryShortcuts.AnyAsync(link => link.TargetPathId == deletionTarget.Id &&
        link.ParentPath.Path == "2021" && link.Name == "Child"), "shortcut creation retains canonical target and folder name");
    Check(await CreateDirectoryShortcutAsync("Album/Child", "2021") is ConflictObjectResult, "shortcut creation rejects duplicates");
    Check(await CreateDirectoryShortcutAsync("root", "2021") is JsonResult, "gallery root can be linked as Home");
    using (var manifest = JsonDocument.Parse(await store.ReadAllTextAsync(FileStoreArea.Gallery, ".shortcuts.json")))
    {
        var aliases = manifest.RootElement.GetProperty("directory_shortcuts").GetProperty("2021");
        Check(aliases.EnumerateArray().Any(entry => entry.GetProperty("name").GetString() == "Child" &&
            entry.GetProperty("target_path").GetString() == "Album/Child"), "created shortcut persisted");
        Check(aliases.EnumerateArray().Any(entry => entry.GetProperty("name").GetString() == "Home" &&
            entry.GetProperty("target_path").GetString() == "root"), "root shortcut persisted");
    }
    Check(await store.ReadAllTextAsync(FileStoreArea.Gallery, "Album/Child/child.png") == "child-bytes", "creation preserves folder files");
    Console.WriteLine("PASS directory shortcut creation: authorization, antiforgery, validation, duplicate prevention, root and persistence");

    async Task<IActionResult> RegenerateAsync(ulong id, bool admin = true, bool token = true,
        System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK)
    {
        var gallery = await GetGalleryAsync("root", admin);
        gallery.Request.Method = "POST";
        if (token)
        {
            var issuing = new DefaultHttpContext { RequestServices = services, User = gallery.User };
            var tokens = services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(issuing);
            gallery.Request.Headers.Cookie = issuing.Response.Headers.SetCookie.ToString().Split(';')[0];
            gallery.Request.Headers[tokens.HeaderName!] = tokens.RequestToken;
        }
        var image = new Website.Pages.ImageModel(context, builder.Configuration,
            services.GetRequiredService<IWebHostEnvironment>(), services.GetRequiredService<IAntiforgery>(),
            services.GetRequiredService<AdminOnlyTagsProvider>(), persistence) { PageContext = gallery.PageContext };
        return await image.OnPostRegenerateThumbnailAsync(id, store, new TestThumbnailClientFactory(status));
    }
    await context.Items.Where(item => item.Id == childItem.Id).ExecuteUpdateAsync(
        update => update.SetProperty(item => item.Metadata, """{"custom":true}"""));
    var beforeRegeneration = await context.Items.AsNoTracking().SingleAsync(item => item.Id == childItem.Id);
    Check(await RegenerateAsync(childItem.Id, admin: false) is NotFoundResult, "thumbnail regeneration requires admin");
    Check(await RegenerateAsync(childItem.Id, token: false) is BadRequestObjectResult, "thumbnail regeneration requires antiforgery");
    Check(await RegenerateAsync(ulong.MaxValue) is NotFoundResult, "thumbnail regeneration rejects missing item");
    var regenerated = await RegenerateAsync(childItem.Id) as JsonResult;
    Check(regenerated is not null, "thumbnail regeneration succeeds");
    var afterRegeneration = await context.Items.AsNoTracking().SingleAsync(item => item.Id == childItem.Id);
    Check(afterRegeneration.ThumbnailUrl != beforeRegeneration.ThumbnailUrl, "regeneration persists a fresh URL");
    var regenerationResponse = JsonSerializer.SerializeToElement(regenerated!.Value);
    Check(regenerationResponse.GetProperty("saved").GetBoolean() &&
        regenerationResponse.GetProperty("thumbnailUrl").GetString() == afterRegeneration.ThumbnailUrl,
        "regeneration returns the persisted URL");
    using (var metadata = JsonDocument.Parse(afterRegeneration.Metadata!))
        Check(metadata.RootElement.GetProperty("custom").GetBoolean(), "regeneration preserves unrelated metadata");
    context.ChangeTracker.Clear();
    var regeneratedGallery = await GetGalleryAsync("Album/Child", admin: true);
    Check((await RenderAsync(regeneratedGallery)).Contains($"src=\"{afterRegeneration.ThumbnailUrl}\""),
        "gallery renders persisted version");
    var shortcutGallery = await GetGalleryAsync("root", admin: true);
    Check(shortcutGallery.Items.Single(item => item.Id == childItem.Id && item.ShortcutId.HasValue).ThumbnailUrl ==
        afterRegeneration.ThumbnailUrl, "file shortcuts use the original item's persisted version");
    Check((await RenderAsync(shortcutGallery)).Contains($"src=\"{afterRegeneration.ThumbnailUrl}\""),
        "shortcut tiles render the persisted version");
    Check(await RegenerateAsync(childItem.Id, status: System.Net.HttpStatusCode.InternalServerError) is ObjectResult { StatusCode: 502 },
        "generation failures surface as errors");
    Check((await context.Items.AsNoTracking().SingleAsync(item => item.Id == childItem.Id)).ThumbnailUrl == afterRegeneration.ThumbnailUrl,
        "failed generation leaves the URL unchanged");
    Check(await RegenerateAsync(childItem.Id) is JsonResult, "repeated regeneration succeeds");
    var repeatedRegeneration = await context.Items.AsNoTracking().SingleAsync(item => item.Id == childItem.Id);
    Check(repeatedRegeneration.ThumbnailUrl != afterRegeneration.ThumbnailUrl, "each regeneration changes the URL");
    Check(await store.ReadAllTextAsync(FileStoreArea.Thumbnails, childItem.ThumbnailKey + ".png") == "regenerated",
        "thumbnail regeneration replaces existing thumbnail");
    Check(await store.ReadAllTextAsync(FileStoreArea.Gallery, "Album/Child/child.png") == "child-bytes",
        "thumbnail regeneration preserves original");
    Console.WriteLine("PASS viewer thumbnail regeneration: authorization, antiforgery, overwrite, persistent versions, gallery/shortcuts and failure preservation");

    async Task<IActionResult> CreateDirectoryShortcutAsync(string path, string destination, bool admin = true, bool validToken = true)
    {
        var model = await GetGalleryAsync("root", admin);
        model.Request.Method = "POST";
        if (validToken)
        {
            var issuing = new DefaultHttpContext { RequestServices = services, User = model.User };
            var tokens = services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(issuing);
            model.Request.Headers.Cookie = issuing.Response.Headers.SetCookie.ToString().Split(';')[0];
            model.Request.Headers[tokens.HeaderName!] = tokens.RequestToken;
        }
        return await model.OnPostCreateDirectoryShortcutAsync(path, destination);
    }

    async Task<IActionResult> DeleteDirectoryShortcutAsync(string path, ulong id, bool admin = true, bool validToken = true, string? moveTo = null)
    {
        var model = await GetGalleryAsync("root", admin);
        model.Request.Method = "POST";
        if (validToken)
        {
            var issuing = new DefaultHttpContext { RequestServices = services, User = model.User };
            var tokens = services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(issuing);
            model.Request.Headers.Cookie = issuing.Response.Headers.SetCookie.ToString().Split(';')[0];
            model.Request.Headers[tokens.HeaderName!] = tokens.RequestToken;
        }
        return moveTo is null
            ? await model.OnPostDeleteDirectoryShortcutAsync(path, id)
            : await model.OnPostMoveDirectoryShortcutAsync(path, id, moveTo);
    }

    async Task<IActionResult> MovePathAsync(string path, string target, bool admin = true, bool validToken = true)
    {
        var model = await GetGalleryAsync("root", admin);
        if (validToken)
        {
            var tokenHttp = new DefaultHttpContext { RequestServices = services, User = model.HttpContext.User };
            var tokens = services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(tokenHttp);
            model.Request.Headers.Cookie = tokenHttp.Response.Headers.SetCookie.ToString().Split(';')[0];
            model.Request.Headers[tokens.HeaderName!] = tokens.RequestToken;
        }
        return await model.OnPostMovePathAsync(path, target);
    }

    async Task<IActionResult> AddTagsAsync(string path, List<ulong> ids, List<ulong> shortcutIds, string tags,
        bool admin = true, bool validToken = true)
    {
        var model = await GetGalleryAsync("root", admin);
        model.HttpContext.Request.Method = "POST";
        if (validToken)
        {
            var issuing = new DefaultHttpContext { RequestServices = services, User = model.User };
            var tokens = services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(issuing);
            model.HttpContext.Request.Headers.Cookie = issuing.Response.Headers.SetCookie.ToString().Split(';')[0];
            model.HttpContext.Request.Headers[tokens.HeaderName!] = tokens.RequestToken;
        }
        return await model.OnPostBulkAddTagsAsync(path, ids, shortcutIds, tags);
    }

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

    context.ChangeTracker.Clear();
    var reloadEntry = new PathEntry { Path = "Reload", ParentPath = "root" };
    context.Paths.Add(reloadEntry);
    context.Tags.Add(new Tag { Name = "Fresh" });
    var reloadExisting = new Item
    {
        OriginalName = "old.png", ParentPath = "Reload", ThumbnailKey = "legacy-key", Type = "file", Metadata = "{}"
    };
    reloadExisting.TagLinks.Add(new ItemTag { Item = reloadExisting, Tag = new Tag { Name = "stale-reload" } });
    context.Items.AddRange(reloadExisting,
        new Item { OriginalName = "gone.png", ParentPath = "Reload", ThumbnailKey = "gone", Type = "image", Metadata = "{}" });
    context.PendingItemDeletions.Add(new PendingItemDeletion { ParentPath = "Reload", OriginalName = "pending.png" });
    context.PendingPathDeletions.Add(new PendingPathDeletion { Path = "Reload/Doomed" });
    await context.SaveChangesAsync();
    var reloadVersion = reloadExisting.ThumbnailUrl;
    foreach (var (key, content) in new[]
             {
                 ("Reload/old.png", "old"), ("Reload/new.png", "new"), ("Reload/notes.txt", "notes"),
                 ("Reload/pending.png", "pending"), ("Reload/.description.md", "ignored for existing path"),
                 ("Reload/.dimdock.json",
                     """{"version":1,"tags":["Dir-Tag"],"items":{"old.png":{"tags":["fresh"],"description":"Old desc"},"orphan.png":{}}}"""),
                 ("Reload/Sub/description.md", "Sub description"), ("Reload/Sub/inner.gif", "gif"),
                 ("Reload/comic_Strip/page.png", "page"), ("Reload/wip_Draft/a.png", "draft"),
                 ("Reload/.hidden/x.png", "hidden"), ("Reload/Doomed/x.png", "doomed"),
                 ("Unrelated/outside.png", "outside")
             })
        await store.WriteAllTextAsync(FileStoreArea.Gallery, key, content);
    await store.CreateDirectoryAsync(FileStoreArea.Gallery, "Reload/Sub/Empty");
    GalleryOperationResult reloadResult;
    using (var reloadScope = app.Services.CreateScope())
    {
        var reloader = new GalleryPathReloader(
            reloadScope.ServiceProvider.GetRequiredService<AppDbContext>(), store,
            new TestThumbnailClientFactory(System.Net.HttpStatusCode.OK),
            reloadScope.ServiceProvider.GetRequiredService<ILogger<GalleryPathReloader>>());
        reloadResult = await reloader.ReloadAsync("Reload");
        Check(reloadResult.Path == "Reload" && reloadResult.Message!.Contains("added 4 paths and 5 items") &&
            reloadResult.Message.Contains("updated 2 entries") && reloadResult.Message.Contains("generated 5 thumbnails") &&
            reloadResult.Message.Contains("1 indexed item were not found") &&
            reloadResult.Message.Contains("1 manifest entry had no matching file"), $"reload summary: {reloadResult.Message}");
        var repeated = await reloader.ReloadAsync("Reload");
        Check(repeated.Message!.Contains("Everything was already up to date"), $"repeated reload is idempotent: {repeated.Message}");
        await ExpectReloadErrorAsync(reloader, "Missing");
        await store.WriteAllTextAsync(FileStoreArea.Gallery, "Reload/Sub/.dimdock.json", """{"version":2}""");
        await ExpectReloadErrorAsync(reloader, "Reload");
        await store.DeleteFileAsync(FileStoreArea.Gallery, "Reload/Sub/.dimdock.json");
    }
    var reloadedPaths = await context.Paths.AsNoTracking().Include(path => path.TagLinks).ThenInclude(link => link.Tag)
        .Where(path => path.Path.StartsWith("Reload")).ToDictionaryAsync(path => path.Path);
    Check(reloadedPaths.Keys.Order(StringComparer.Ordinal).SequenceEqual(
        ["Reload", "Reload/Sub", "Reload/Sub/Empty", "Reload/comic_Strip", "Reload/wip_Draft"]),
        "reload indexes new directories, skipping dot and pending-deletion directories");
    Check(reloadedPaths["Reload"].Description is null && reloadedPaths["Reload/Sub"].Description == "Sub description" &&
        await store.FileExistsAsync(FileStoreArea.Gallery, "Reload/Sub/.description.md"),
        "reload migrates legacy descriptions and only sets them on new paths");
    Check(reloadedPaths["Reload"].TagLinks.Select(link => link.Tag.Name).SequenceEqual(["dir-tag"]) &&
        reloadedPaths["Reload/comic_Strip"].Type == "comic" &&
        reloadedPaths["Reload/wip_Draft"].TagLinks.Any(link => link.Tag.Name == "hidden"),
        "reload applies directory tags, comic_ and wip_ rules");
    Check(!await context.Paths.AnyAsync(path => path.Path == "Unrelated"), "reload stays within the selected subtree");
    var reloadedItems = await context.Items.AsNoTracking().Include(item => item.TagLinks).ThenInclude(link => link.Tag)
        .Where(item => item.ParentPath!.StartsWith("Reload")).ToListAsync();
    var reloadedOld = reloadedItems.Single(item => item.OriginalName == "old.png");
    Check(reloadedOld.Type == "image" && reloadedOld.Description == "Old desc" &&
        reloadedOld.TagLinks.Select(link => link.Tag.Name).SequenceEqual(["Fresh"]) &&
        reloadedOld.ThumbnailKey != "legacy-key" && reloadedOld.ThumbnailUrl != reloadVersion,
        "reload updates existing item type, manifest tags/description, thumbnail key and version");
    Check(reloadedItems.Any(item => item.OriginalName == "gone.png") &&
        !reloadedItems.Any(item => item.OriginalName is "pending.png" or "x.png"),
        "reload keeps missing rows and skips pending deletions and hidden directories");
    Check(reloadedItems.Single(item => item.OriginalName == "notes.txt").Type == "file" &&
        await store.ReadAllTextAsync(FileStoreArea.Thumbnails,
            reloadedItems.Single(item => item.OriginalName == "inner.gif").ThumbnailKey + ".png") == "regenerated",
        "reload indexes files and generates thumbnails for new images");
    Check(await context.ItemTags.CountAsync(link => link.ItemId == reloadedOld.Id) == 1,
        "replaced item tag links are deleted");

    async Task<IActionResult> ReloadHandlerAsync(string? path, bool admin = true, bool token = true)
    {
        var gallery = await GetGalleryAsync("root", admin);
        gallery.Request.Method = "POST";
        if (token)
        {
            var issuing = new DefaultHttpContext { RequestServices = services, User = gallery.User };
            var tokens = services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(issuing);
            gallery.Request.Headers.Cookie = issuing.Response.Headers.SetCookie.ToString().Split(';')[0];
            gallery.Request.Headers[tokens.HeaderName!] = tokens.RequestToken;
        }
        return await gallery.OnPostReloadPathAsync(path);
    }
    Check(await ReloadHandlerAsync("Reload", admin: false) is NotFoundResult, "reload handler requires admin");
    Check(await ReloadHandlerAsync("Reload", token: false) is BadRequestObjectResult, "reload handler requires antiforgery");
    Check(await ReloadHandlerAsync("Reload/../x") is BadRequestObjectResult, "reload handler rejects invalid paths");
    Check(await ReloadHandlerAsync("Reload/comic_Strip") is JsonResult handlerResult &&
        JsonSerializer.SerializeToElement(handlerResult.Value).GetProperty("message").GetString()!
            .Contains("Everything was already up to date"), "reload handler returns the summary");
    Check(await ReloadHandlerAsync("root") is JsonResult rootReload &&
        JsonSerializer.SerializeToElement(rootReload.Value).GetProperty("path").GetString() == "root" &&
        await context.Paths.AnyAsync(path => path.Path == "Unrelated"), "root reload scans the whole gallery");
    Console.WriteLine("PASS path reload: new paths/items, manifests, legacy files, comic/wip rules, pending deletions, scope, idempotency, thumbnails and handler guards");

    async Task ExpectReloadErrorAsync(GalleryPathReloader reloader, string path)
    {
        try
        {
            await reloader.ReloadAsync(path);
        }
        catch (GalleryReloadException)
        {
            return;
        }
        throw new InvalidOperationException($"FAIL: reload of {path} should be rejected");
    }

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
            persistence, store, services.GetRequiredService<IHttpClientFactory>(), new UploadOptions(),
            services.GetRequiredService<GalleryFilterPreferences>())
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

sealed class TestThumbnailClientFactory(System.Net.HttpStatusCode status) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new(new Handler(status)) { BaseAddress = new Uri("http://thumbnail.test/") };

    private sealed class Handler(System.Net.HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("regenerated") });
    }
}
