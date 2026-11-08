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
    private readonly AppDbContext _context;
    private readonly IAntiforgery _antiforgery;
    private readonly AdminOnlyTagsProvider _adminOnlyTagsProvider;

    public AdminModel(
        IConfiguration configuration,
        AppDbContext context,
        IAntiforgery antiforgery,
        AdminOnlyTagsProvider adminOnlyTagsProvider)
    {
        _configuration = configuration;
        _context = context;
        _antiforgery = antiforgery;
        _adminOnlyTagsProvider = adminOnlyTagsProvider;
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
        if (!AdminAccess.IsConfigured(_configuration))
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

        const string imageRoot = "/img_src";
        var fullImageRoot = Path.GetFullPath(imageRoot);
        var rootPrefix = fullImageRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullImageRoot
            : fullImageRoot + Path.DirectorySeparatorChar;
        await EnsurePendingItemDeletionsTableAsync();
        var pendingItemDeletions = await _context.PendingItemDeletions
            .AsNoTracking()
            .OrderBy(entry => entry.Id)
            .ToListAsync(HttpContext.RequestAborted);
        foreach (var pendingDeletion in pendingItemDeletions)
        {
            var filePath = ResolveItemFilePath(
                fullImageRoot,
                rootPrefix,
                pendingDeletion.ParentPath,
                pendingDeletion.OriginalName);
            if (Directory.Exists(filePath))
            {
                throw new InvalidOperationException(
                    $"Pending item deletion resolves to a directory: {pendingDeletion.ParentPath}/{pendingDeletion.OriginalName}");
            }
            if (System.IO.File.Exists(filePath))
            {
                if ((System.IO.File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        $"Pending item deletion resolves to a linked file: {pendingDeletion.ParentPath}/{pendingDeletion.OriginalName}");
                }

                System.IO.File.Delete(filePath);
            }

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
            var directory = ResolvePathDirectory(fullImageRoot, rootPrefix, pendingDeletion.Path);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
            else if (System.IO.File.Exists(directory))
            {
                throw new InvalidOperationException(
                    $"Pending path deletion is not a directory: {pendingDeletion.Path}");
            }

            await DeletePathRecordsAsync(pendingDeletion.Path);
            await _context.PendingPathDeletions
                .Where(entry => entry.Path == pendingDeletion.Path)
                .ExecuteDeleteAsync(HttpContext.RequestAborted);
        }

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
            var relativePath = entry.Path == "root"
                ? string.Empty
                : entry.Path.Replace('/', Path.DirectorySeparatorChar);
            var directory = Path.GetFullPath(Path.Combine(fullImageRoot, relativePath));
            if (directory != fullImageRoot && !directory.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Path entry resolves outside the image directory: {entry.Path}");
            }

            if (!Directory.Exists(directory))
            {
                skipped++;
                continue;
            }

            var descriptionPath = Path.Combine(directory, "description.md");
            if (string.IsNullOrWhiteSpace(entry.Description))
            {
                if (System.IO.File.Exists(descriptionPath))
                {
                    System.IO.File.Delete(descriptionPath);
                    deleted++;
                }
            }
            else if (System.IO.File.Exists(descriptionPath) &&
                string.Equals(await System.IO.File.ReadAllTextAsync(descriptionPath), entry.Description, StringComparison.Ordinal))
            {
                unchanged++;
            }
            else
            {
                await System.IO.File.WriteAllTextAsync(
                    descriptionPath,
                    entry.Description!,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
                        .ToArray()
                })
                .Where(item => item.Tags.Length > 0)
                .ToDictionary(item => item.OriginalName, item => new { item.Tags }, StringComparer.Ordinal);
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

            var manifestPath = Path.Combine(directory, ".dimdock.json");

            if (manifestType is null && directoryTags.Length == 0 && itemTags.Count == 0)
            {
                if (System.IO.File.Exists(manifestPath))
                {
                    System.IO.File.Delete(manifestPath);
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
            if (!System.IO.File.Exists(manifestPath) ||
                !string.Equals(
                    await System.IO.File.ReadAllTextAsync(manifestPath),
                    manifest,
                    StringComparison.Ordinal))
            {
                await System.IO.File.WriteAllTextAsync(
                    manifestPath,
                    manifest,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                manifestsWritten++;
            }
        }

        const string aboutFile = "/about/about.md";
        var aboutContent = await _context.About
            .AsNoTracking()
            .Where(entry => entry.Id == 1)
            .Select(entry => entry.Description)
            .FirstOrDefaultAsync(HttpContext.RequestAborted) ?? string.Empty;
        var aboutWritten = false;
        if (!System.IO.File.Exists(aboutFile) ||
            !string.Equals(
                await System.IO.File.ReadAllTextAsync(aboutFile, HttpContext.RequestAborted),
                aboutContent,
                StringComparison.Ordinal))
        {
            await System.IO.File.WriteAllTextAsync(
                aboutFile,
                aboutContent,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                HttpContext.RequestAborted);
            aboutWritten = true;
        }

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

    private static string ResolvePathDirectory(string fullImageRoot, string rootPrefix, string path)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(path, "root", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The root path cannot be deleted.");
        }
        if (Directory.Exists(fullImageRoot) &&
            (System.IO.File.GetAttributes(fullImageRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The image directory cannot be a linked directory.");
        }

        var segments = path.Split('/');
        if (segments.Length == 0 ||
            segments.Any(segment =>
                segment.Length == 0 ||
                segment is "." or ".." ||
                segment.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_')))
        {
            throw new InvalidOperationException($"Invalid pending path deletion: {path}");
        }

        var relativePath = string.Join(Path.DirectorySeparatorChar, segments);
        var directory = Path.GetFullPath(Path.Combine(fullImageRoot, relativePath));
        if (!directory.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidOperationException($"Path entry resolves outside the image directory: {path}");
        }

        var current = fullImageRoot;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) &&
                (System.IO.File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"Pending path contains a linked directory: {path}");
            }
        }

        return directory;
    }

    private static string ResolveItemFilePath(
        string fullImageRoot,
        string rootPrefix,
        string parentPath,
        string originalName)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.IsNullOrWhiteSpace(originalName) ||
            originalName is "." or ".." ||
            originalName != Path.GetFileName(originalName) ||
            originalName.Contains('/') ||
            originalName.Contains('\\') ||
            originalName.Any(char.IsControl))
        {
            throw new InvalidOperationException($"Invalid pending item filename: {originalName}");
        }

        var segments = parentPath == "root"
            ? Array.Empty<string>()
            : parentPath.Split('/');
        if (segments.Any(segment =>
            string.IsNullOrWhiteSpace(segment) ||
            segment is "." or ".." ||
            segment.Contains('\\') ||
            segment.Contains(':')))
        {
            throw new InvalidOperationException($"Invalid pending item path: {parentPath}");
        }

        if (Directory.Exists(fullImageRoot) &&
            (System.IO.File.GetAttributes(fullImageRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The image directory cannot be a linked directory.");
        }

        var current = fullImageRoot;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) &&
                (System.IO.File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"Pending item path contains a linked directory: {parentPath}");
            }
        }

        var filePath = Path.GetFullPath(Path.Combine(current, originalName));
        if (!filePath.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidOperationException(
                $"Pending item deletion resolves outside the image directory: {parentPath}/{originalName}");
        }

        return filePath;
    }

    private bool IsAdminEnabled() =>
        AdminAccess.IsAdmin(_configuration, User);

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
