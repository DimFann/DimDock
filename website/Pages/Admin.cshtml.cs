using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Website.Data;
using Website.Services;

namespace Website.Pages;

public class AdminModel : PageModel
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly AppDbContext _context;
    private readonly IAntiforgery _antiforgery;
    private readonly AdminOnlyTagsProvider _adminOnlyTagsProvider;
    private readonly FilesystemPersistenceService _filesystemPersistence;
    private readonly IFileStore _fileStore;

    public AdminModel(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        AppDbContext context,
        IAntiforgery antiforgery,
        AdminOnlyTagsProvider adminOnlyTagsProvider,
        FilesystemPersistenceService filesystemPersistence,
        IFileStore fileStore)
    {
        _configuration = configuration;
        _environment = environment;
        _context = context;
        _antiforgery = antiforgery;
        _adminOnlyTagsProvider = adminOnlyTagsProvider;
        _filesystemPersistence = filesystemPersistence;
        _fileStore = fileStore;
    }

    [BindProperty]
    public string AdminOnlyTagsInput { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync()
    {
        var isAdminEnabled = AdminAccess.IsFeatureEnabled(_configuration);
        if (!isAdminEnabled)
        {
            return NotFound();
        }
        if (!AdminAccess.IsConfigured(
                _configuration,
                AdminAccess.IsDevelopmentPasswordBypassEnabled(_configuration, _environment)))
        {
            return RedirectToPage("/Login");
        }
        if (!User.IsInRole("Admin"))
        {
            return RedirectToPage("/Login", new { returnUrl = "/Admin" });
        }

        var tags = await _adminOnlyTagsProvider.GetTagsAsync(HttpContext.RequestAborted);
        AdminOnlyTagsInput = string.Join(", ", tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase));
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAdminOnlyTagsAsync()
    {
        if (!IsAdminEnabled())
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        if (!TagNameNormalizer.TryNormalize(AdminOnlyTagsInput, out var tags, out var error))
        {
            return BadRequest(error ?? "Invalid admin-only tags.");
        }

        var tagsJson = JsonSerializer.Serialize(tags);
        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO AdminOnlyTagSettings (id, tags) VALUES (1, {tagsJson}) ON DUPLICATE KEY UPDATE tags = VALUES(tags)",
            HttpContext.RequestAborted);
        await _filesystemPersistence.PersistAdminOnlyTagsAsync(tags, HttpContext.RequestAborted);

        return new JsonResult(new { tags });
    }

    public async Task<IActionResult> OnPostSyncDescriptionsAsync()
    {
        if (!IsAdminEnabled())
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        await EnsurePendingItemDeletionsTableAsync();
        var pendingItemDeletions = await _context.PendingItemDeletions
            .AsNoTracking()
            .OrderBy(entry => entry.Id)
            .ToListAsync(HttpContext.RequestAborted);
        foreach (var pendingDeletion in pendingItemDeletions)
        {
            var fileKey = GalleryItemKey(pendingDeletion.ParentPath, pendingDeletion.OriginalName);
            if (await _fileStore.DirectoryExistsAsync(
                    FileStoreArea.Gallery,
                    fileKey,
                    HttpContext.RequestAborted))
            {
                throw new InvalidOperationException(
                    $"Pending item deletion resolves to a directory: {pendingDeletion.ParentPath}/{pendingDeletion.OriginalName}");
            }
            await _fileStore.DeleteFileAsync(FileStoreArea.Gallery, fileKey, HttpContext.RequestAborted);

            await _context.PendingItemDeletions
                .Where(entry => entry.Id == pendingDeletion.Id)
                .ExecuteDeleteAsync(HttpContext.RequestAborted);
        }

        await EnsurePendingPathDeletionsTableAsync();
        var pendingPathDeletions = await _context.PendingPathDeletions
            .AsNoTracking()
            .OrderBy(entry => entry.Path.Length)
            .ToListAsync(HttpContext.RequestAborted);
        foreach (var pendingDeletion in pendingPathDeletions)
        {
            if (string.Equals(pendingDeletion.Path, "root", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The root gallery path cannot be deleted.");
            }
            var directoryKey = GalleryDirectoryKey(pendingDeletion.Path);
            if (await _fileStore.FileExistsAsync(
                    FileStoreArea.Gallery,
                    directoryKey,
                    HttpContext.RequestAborted))
            {
                throw new InvalidOperationException(
                    $"Pending path deletion is not a directory: {pendingDeletion.Path}");
            }
            await _fileStore.DeleteDirectoryAsync(
                FileStoreArea.Gallery,
                directoryKey,
                recursive: true,
                HttpContext.RequestAborted);

            await DeletePathRecordsAsync(pendingDeletion.Path);
            await _context.PendingPathDeletions
                .Where(entry => entry.Path == pendingDeletion.Path)
                .ExecuteDeleteAsync(HttpContext.RequestAborted);
        }

        await _filesystemPersistence.PersistDirectoryShortcutsAsync(HttpContext.RequestAborted);

        var paths = await _context.Paths
            .Include(path => path.TagLinks)
            .ThenInclude(link => link.Tag)
            .ToListAsync();
        var items = await _context.Items
            .Include(item => item.TagLinks)
            .ThenInclude(link => link.Tag)
            .ToListAsync();

        var written = 0;
        var unchanged = 0;
        var deleted = 0;
        var skipped = 0;
        var manifestsWritten = 0;
        var manifestsDeleted = 0;
        var itemsByPath = items
            .GroupBy(item => item.ParentPath ?? string.Empty)
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var entry in paths)
        {
            var directoryKey = GalleryDirectoryKey(entry.Path);
            if (!await _fileStore.DirectoryExistsAsync(
                    FileStoreArea.Gallery,
                    directoryKey,
                    HttpContext.RequestAborted))
            {
                skipped++;
                continue;
            }

            var descriptionKey = JoinKey(directoryKey, ".description.md");
            var legacyDescriptionKey = JoinKey(directoryKey, "description.md");
            if (await _fileStore.FileExistsAsync(
                    FileStoreArea.Gallery,
                    legacyDescriptionKey,
                    HttpContext.RequestAborted) &&
                !await _fileStore.FileExistsAsync(
                    FileStoreArea.Gallery,
                    descriptionKey,
                    HttpContext.RequestAborted))
            {
                await _fileStore.MoveFileAsync(
                    FileStoreArea.Gallery,
                    legacyDescriptionKey,
                    descriptionKey,
                    HttpContext.RequestAborted);
            }
            if (string.IsNullOrWhiteSpace(entry.Description))
            {
                if (await _fileStore.FileExistsAsync(
                        FileStoreArea.Gallery,
                        descriptionKey,
                        HttpContext.RequestAborted))
                {
                    await _fileStore.DeleteFileAsync(
                        FileStoreArea.Gallery,
                        descriptionKey,
                        HttpContext.RequestAborted);
                    deleted++;
                }
            }
            else if (await _fileStore.FileExistsAsync(
                         FileStoreArea.Gallery,
                         descriptionKey,
                         HttpContext.RequestAborted) &&
                     string.Equals(
                         await _fileStore.ReadAllTextAsync(
                             FileStoreArea.Gallery,
                             descriptionKey,
                             HttpContext.RequestAborted),
                         entry.Description,
                         StringComparison.Ordinal))
            {
                unchanged++;
            }
            else
            {
                await _fileStore.WriteAllTextAsync(
                    FileStoreArea.Gallery,
                    descriptionKey,
                    entry.Description!,
                    HttpContext.RequestAborted);
                written++;
            }

            var pathItems = itemsByPath.GetValueOrDefault(entry.Path, [])
                .OrderBy(item => item.OriginalName, StringComparer.Ordinal)
                .ToList();
            var itemTags = pathItems
                .Select(item => new
                {
                    item.OriginalName,
                    Tags = item.TagLinks
                        .Select(link => link.Tag.Name)
                        .Where(name => !DatePathTags.ForPath(item.ParentPath).Contains(name, StringComparer.Ordinal))
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .ToArray(),
                    item.Description
                })
                .Where(item => item.Tags.Length > 0 || !string.IsNullOrWhiteSpace(item.Description))
                .ToDictionary(item => item.OriginalName, item => new { item.Tags, item.Description }, StringComparer.Ordinal);
            var directoryTags = entry.TagLinks
                .Select(link => link.Tag.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            var manifestType = entry.Type?.Trim().ToLowerInvariant();
            if (manifestType == "directory")
            {
                manifestType = null;
            }
            if (manifestType is not null && manifestType != "comic")
            {
                throw new InvalidOperationException($"Unsupported path type '{entry.Type}' for path '{entry.Path}'.");
            }

            var manifestKey = JoinKey(directoryKey, ".dimdock.json");

            if (manifestType is null && directoryTags.Length == 0 && itemTags.Count == 0)
            {
                if (await _fileStore.FileExistsAsync(
                        FileStoreArea.Gallery,
                        manifestKey,
                        HttpContext.RequestAborted))
                {
                    await _fileStore.DeleteFileAsync(
                        FileStoreArea.Gallery,
                        manifestKey,
                        HttpContext.RequestAborted);
                    manifestsDeleted++;
                }
                continue;
            }

            var manifest = JsonSerializer.Serialize(
                new { version = 1, type = manifestType, tags = directoryTags, items = itemTags },
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                });
            if (!await _fileStore.FileExistsAsync(
                    FileStoreArea.Gallery,
                    manifestKey,
                    HttpContext.RequestAborted) ||
                !string.Equals(
                    await _fileStore.ReadAllTextAsync(
                        FileStoreArea.Gallery,
                        manifestKey,
                        HttpContext.RequestAborted),
                    manifest,
                    StringComparison.Ordinal))
            {
                await _fileStore.WriteAllTextAsync(
                    FileStoreArea.Gallery,
                    manifestKey,
                    manifest,
                    HttpContext.RequestAborted);
                manifestsWritten++;
            }
        }

        const string aboutFile = "about.md";
        var aboutContent = await _context.About
            .AsNoTracking()
            .Where(entry => entry.Id == 1)
            .Select(entry => entry.Description)
            .FirstOrDefaultAsync(HttpContext.RequestAborted) ?? string.Empty;
        var aboutWritten = false;
        if (!await _fileStore.FileExistsAsync(
                FileStoreArea.About,
                aboutFile,
                HttpContext.RequestAborted) ||
            !string.Equals(
                await _fileStore.ReadAllTextAsync(
                    FileStoreArea.About,
                    aboutFile,
                    HttpContext.RequestAborted),
                aboutContent,
                StringComparison.Ordinal))
        {
            await _fileStore.WriteAllTextAsync(
                FileStoreArea.About,
                aboutFile,
                aboutContent,
                HttpContext.RequestAborted);
            aboutWritten = true;
        }

        var blogs = await _context.Blogs
            .AsNoTracking()
            .ToListAsync(HttpContext.RequestAborted);
        foreach (var blog in blogs)
        {
            await _filesystemPersistence.PersistBlogAsync(blog, HttpContext.RequestAborted);
        }

        var adminOnlyTags = await _adminOnlyTagsProvider.GetTagsAsync(HttpContext.RequestAborted);
        await _filesystemPersistence.PersistAdminOnlyTagsAsync(adminOnlyTags, HttpContext.RequestAborted);

        var unusedTags = await _context.Tags
            .Where(tag => !tag.ItemLinks.Any() && !tag.PathLinks.Any())
            .ToListAsync();
        _context.Tags.RemoveRange(unusedTags);
        await _context.SaveChangesAsync();

        return new JsonResult(new
        {
            written,
            unchanged,
            deleted,
            skipped,
            manifestsWritten,
            manifestsDeleted,
            aboutWritten,
            blogsPersisted = blogs.Count,
            itemFilesRemoved = pendingItemDeletions.Count,
            pathsRemoved = pendingPathDeletions.Count,
            unusedTagsRemoved = unusedTags.Count
        });
    }

    private async Task EnsurePendingPathDeletionsTableAsync()
    {
        await _context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS PendingPathDeletions (
                path VARCHAR(1024) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY
            )
            """,
            HttpContext.RequestAborted);
    }

    private async Task EnsurePendingItemDeletionsTableAsync()
    {
        await _context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS PendingItemDeletions (
                id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
                parent_path VARCHAR(1024) NOT NULL,
                original_name VARCHAR(255) NOT NULL
            )
            """,
            HttpContext.RequestAborted);
    }

    private async Task DeletePathRecordsAsync(string path)
    {
        var pathPrefix = path + "/";
        await _context.Shortcuts
            .Where(shortcut =>
                shortcut.ShortcutPath == path ||
                (shortcut.ShortcutPath != null && shortcut.ShortcutPath.StartsWith(pathPrefix)))
            .ExecuteDeleteAsync(HttpContext.RequestAborted);
        await _context.Items
            .Where(item =>
                item.ParentPath == path ||
                (item.ParentPath != null && item.ParentPath.StartsWith(pathPrefix)))
            .ExecuteDeleteAsync(HttpContext.RequestAborted);
        await _context.Paths
            .Where(entry => entry.Path == path || entry.Path.StartsWith(pathPrefix))
            .ExecuteDeleteAsync(HttpContext.RequestAborted);
    }

    private static string GalleryDirectoryKey(string path)
    {
        if (string.Equals(path, "root", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/'))
        {
            throw new InvalidOperationException($"Invalid pending path deletion: {path}");
        }

        return path.TrimEnd('/');
    }

    private static string GalleryItemKey(string parentPath, string originalName)
    {
        if (string.IsNullOrWhiteSpace(originalName) ||
            originalName is "." or ".." ||
            originalName.Contains('/') ||
            originalName.Contains('\\') ||
            originalName.Any(char.IsControl))
        {
            throw new InvalidOperationException($"Invalid pending item filename: {originalName}");
        }

        var directory = string.Equals(parentPath, "root", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : GalleryDirectoryKey(parentPath);
        return JoinKey(directory, originalName);
    }

    private static string JoinKey(string directory, string name) =>
        string.IsNullOrEmpty(directory) ? name : $"{directory.TrimEnd('/')}/{name}";

    private bool IsAdminEnabled() =>
        AdminAccess.IsAdmin(
            _configuration,
            User,
            AdminAccess.IsDevelopmentPasswordBypassEnabled(_configuration, _environment));

    private async Task<bool> IsAntiForgeryTokenValidAsync()
    {
        try
        {
            await _antiforgery.ValidateRequestAsync(HttpContext);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }
}
