using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Markdig;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Website.Data;
using Website.Models;
using Website.Services;

public class GalleryModel : PageModel
{
    private static readonly HashSet<string> ThumbnailImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif" };

    private readonly AppDbContext _context;
    private readonly ILogger<GalleryModel> _logger;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly IAntiforgery _antiforgery;
    private readonly AdminOnlyTagsProvider _adminOnlyTagsProvider;
    private readonly FilesystemPersistenceService _filesystemPersistence;
    private readonly IFileStore _fileStore;
    private readonly HttpClient _thumbnailClient;
    private readonly UploadOptions _uploadOptions;
    private readonly GalleryFilterPreferences _filterPreferences;

    public GalleryModel(
        AppDbContext context,
        ILogger<GalleryModel> logger,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IAntiforgery antiforgery,
        AdminOnlyTagsProvider adminOnlyTagsProvider,
        FilesystemPersistenceService filesystemPersistence,
        IFileStore fileStore,
        IHttpClientFactory httpClientFactory,
        UploadOptions uploadOptions,
        GalleryFilterPreferences filterPreferences)
    {
        _context = context;
        _logger = logger;
        _configuration = configuration;
        _environment = environment;
        _antiforgery = antiforgery;
        _adminOnlyTagsProvider = adminOnlyTagsProvider;
        _filesystemPersistence = filesystemPersistence;
        _fileStore = fileStore;
        _thumbnailClient = httpClientFactory.CreateClient("ThumbnailService");
        _uploadOptions = uploadOptions;
        _filterPreferences = filterPreferences;
    }

    // In Bypass mode, set when this request is not on the upload host: the Upload button opens this URL instead.
    public string? UploadRedirectUrl =>
        _uploadOptions.Mode == UploadMode.Bypass && !_uploadOptions.IsBypassHost(Request)
            ? $"https://{_uploadOptions.BypassHost}{Request.PathBase}/Login?ReturnUrl={Uri.EscapeDataString(Request.PathBase + Request.Path + Request.QueryString)}"
            : null;

    [BindProperty(SupportsGet = true)]
    public string? Path { get; set; }

    public List<Item> Items { get; set; } = new();
    public List<PathEntry> Paths { get; set; } = new();
    public List<GalleryDirectoryLink> DirectoryLinks { get; set; } = new();
    public List<Shortcut> Shortcuts { get; set; } = new();

    public PathEntry? PathEntry { get; set; }
    public string? Message { get; set; }
    public string? CurrentPath { get; set; }
    public string? ParentPath { get; set; }
    public bool AdminEnabled => AdminAccess.IsAdmin(
        _configuration,
        User,
        AdminAccess.IsDevelopmentPasswordBypassEnabled(_configuration, _environment));
    public HashSet<string> AdminOnlyTags { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public GalleryFilter ContentFilter { get; private set; } = GalleryFilter.BlurNsfw;
    public bool AdultConfirmed => _filterPreferences.Read(Request).AdultConfirmed;

    public bool IsNsfw(Item item) => GalleryFilterPreferences.IsNsfw(item);
    public bool ShouldBlur(Item item) => GalleryFilterPreferences.ShouldBlur(item, ContentFilter, AdminEnabled);

    public IReadOnlyCollection<string> GetItemTags(Item item) =>
        item.TagLinks.Select(link => link.Tag.Name)
            .Concat(DatePathTags.ForPath(item.ParentPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(tag => AdminEnabled || !AdminOnlyTags.Contains(tag))
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async Task<IActionResult> OnGetAsync(string? path)
    {
        Response.Headers.CacheControl = "private, no-store";
        ContentFilter = _filterPreferences.Read(Request).Filter;
        AdminOnlyTags = await _adminOnlyTagsProvider.GetTagsAsync(HttpContext.RequestAborted);
        var input = string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(Path)
            ? "root"
            : path ?? Path ?? "root";

        var sanitized = SanitizePath(input).TrimStart('/');
        _logger.LogDebug("Gallery path: {Path}", sanitized);

        CurrentPath = sanitized;
        PathEntry = await _context.Paths
            .Include(entry => entry.TagLinks)
            .ThenInclude(link => link.Tag)
            .FirstOrDefaultAsync(entry => entry.Path == sanitized);
        if (PathEntry is null)
        {
            return string.Equals(sanitized, "root", StringComparison.OrdinalIgnoreCase)
                ? NotFound()
                : RedirectToPage("/Gallery", new { path = (string?)null });
        }
        var hiddenPaths = await _context.Paths
                .Where(entry => entry.TagLinks.Any(link => AdminOnlyTags.Contains(link.Tag.Name)))
                .Select(entry => entry.Path)
                .ToListAsync(HttpContext.RequestAborted);
        if (!AdminEnabled && hiddenPaths.Any(hidden => IsSameOrChildPath(sanitized, hidden)))
        {
            return NotFound();
        }

        Items = await _context.Items
            .Include(item => item.TagLinks)
            .ThenInclude(link => link.Tag)
            .Where(i => i.ParentPath == sanitized)
            .ToListAsync();

        Paths = await _context.Paths
            .Include(entry => entry.TagLinks)
            .ThenInclude(link => link.Tag)
            .Where(p => p.ParentPath == sanitized && p.Path != p.ParentPath)
            .OrderBy(p => p.Path)
            .ToListAsync();

        if (!AdminEnabled)
        {
            Paths = Paths
                .Where(entry =>
                    !entry.TagLinks.Any(link => AdminOnlyTags.Contains(link.Tag.Name)) &&
                    !hiddenPaths.Any(hidden => IsSameOrChildPath(entry.Path, hidden)))
                .ToList();
            Items = Items
                .Where(item =>
                    !item.TagLinks.Any(link => AdminOnlyTags.Contains(link.Tag.Name)) &&
                    !hiddenPaths.Any(hidden => IsSameOrChildPath(item.ParentPath ?? string.Empty, hidden)))
                .ToList();
        }

        var directoryShortcuts = await _context.DirectoryShortcuts
            .AsNoTracking()
            .Include(shortcut => shortcut.TargetPath)
            .Where(shortcut => shortcut.ParentPath.Path == sanitized)
            .ToListAsync(HttpContext.RequestAborted);
        DirectoryLinks = Paths.Select(entry => new GalleryDirectoryLink(
                entry.Path.Split('/').Last(), entry.Path,
                hiddenPaths.Any(hidden => IsSameOrChildPath(entry.Path, hidden)), false))
            .Concat(directoryShortcuts
                .Where(shortcut => AdminEnabled ||
                    !hiddenPaths.Any(hidden => IsSameOrChildPath(shortcut.TargetPath.Path, hidden)))
                .Select(shortcut => new GalleryDirectoryLink(
                    shortcut.Name, shortcut.TargetPath.Path,
                    hiddenPaths.Any(hidden => IsSameOrChildPath(shortcut.TargetPath.Path, hidden)), true, shortcut.Id)))
            .OrderBy(link => link.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(link => link.Path, StringComparer.Ordinal)
            .ToList();

        Shortcuts = await _context.Shortcuts
            .Include(s => s.OriginalItem)
            .ThenInclude(item => item!.TagLinks)
            .ThenInclude(link => link.Tag)
            .Where(i => i.ShortcutPath == sanitized)
            .ToListAsync();

        _logger.LogDebug($"Path: {sanitized}\nItems: {Items.Count}\nPaths: {Paths.Count}\nShortcuts: {Shortcuts.Count}\n{string.Join('\n',Shortcuts)}");

        // Add Shortcuts to Items as Item for display purposes
        foreach (var shortcut in Shortcuts)
        {
            if (shortcut.OriginalItem != null)
            {
                var clone = new Item
                {
                    Id = shortcut.OriginalItem.Id,
                    OriginalName = shortcut.OriginalName,
                    ParentPath = shortcut.ShortcutPath,
                    Created = shortcut.OriginalItem.Created,
                    DisplayIndex = shortcut.OriginalItem.DisplayIndex,
                    Metadata = shortcut.OriginalItem.Metadata,
                    Type = shortcut.OriginalItem.Type,
                    Uploaded = shortcut.OriginalItem.Uploaded,
                    ThumbnailKey = shortcut.OriginalItem.ThumbnailKey,
                    ShortcutId = shortcut.Id,
                    TagLinks = shortcut.OriginalItem.TagLinks
                };
                if (AdminEnabled || !clone.TagLinks.Any(link => AdminOnlyTags.Contains(link.Tag.Name)))
                {
                    Items.Add(clone);
                }
            }
        }

        Items = Items.Where(item => !GalleryFilterPreferences.ShouldHide(item, ContentFilter, AdminEnabled)).ToList();

        if (string.Equals(PathEntry?.Type, "comic", StringComparison.OrdinalIgnoreCase))
        {
            Items = Items
                .OrderBy(item => item.DisplayIndex)
                .ThenBy(item => item.OriginalName)
                .ThenBy(item => item.Created)
                .ThenBy(item => item.Uploaded)
                .ToList();
        }
        else
        {
            Items = Items
                .Select(item => new { Item = item, DatePrefix = ParseDatePrefix(item.OriginalName) })
                .OrderByDescending(entry => entry.DatePrefix.HasValue)
                .ThenByDescending(entry => entry.DatePrefix)
                .ThenBy(entry => entry.Item.OriginalName, StringComparer.OrdinalIgnoreCase)
                .Select(entry => entry.Item)
                .ToList();
        }

        ParentPath = sanitized.Contains('/')
            ? sanitized.Substring(0, sanitized.LastIndexOf('/'))
            : "root";

        if (!Items.Any() && !DirectoryLinks.Any())
        {
            Message = $"No items or subpaths found for path '{sanitized}'.";
        }

        Path = sanitized;

        if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
        {
            return Partial("_GalleryResults", this);
        }

        return Page();
    }

    public string SanitizePath(string path)
    {
        var sb = new StringBuilder(path.Length + 1);
        foreach (char c in path)
        {
            if (char.IsDigit(c) || char.IsAsciiLetter(c) || c == '-' || c == '_' || c == '/')
                sb.Append(c);
        }

        return sb.ToString();
    }

    private static bool IsSameOrChildPath(string candidate, string parent) =>
        string.Equals(parent, "root", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);

    public string GetExtension(string originalName)
    {
        var dotIndex = originalName.LastIndexOf('.');
        return (dotIndex >= 0 && dotIndex < originalName.Length - 1)
            ? originalName.Substring(dotIndex + 1)
            : "bin";
    }

    public string GetSourceUrl(Item item)
    {
        var shortcut = Shortcuts.FirstOrDefault(entry =>
            entry.OriginalId == item.Id &&
            string.Equals(entry.ShortcutPath, item.ParentPath, StringComparison.Ordinal));
        var parentPath = shortcut?.OriginalItem?.ParentPath ?? item.ParentPath;
        var fileName = shortcut?.OriginalItem?.OriginalName ?? item.OriginalName;
        return FileStoreKeys.GallerySourceUrl(parentPath, fileName);
    }

    public BigInteger? GetComicLeadingNumber(string fileName)
    {
        var digitCount = fileName.TakeWhile(char.IsAsciiDigit).Count();
        return digitCount > 0 &&
            BigInteger.TryParse(
                fileName.AsSpan(0, digitCount),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var number)
            ? number
            : null;
    }

    private static DateOnly? ParseDatePrefix(string fileName)
    {
        if (fileName.Length >= 8 &&
            DateOnly.TryParseExact(
                fileName.AsSpan(0, 8),
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var compactDate))
        {
            return compactDate;
        }

        if (fileName.Length >= 10)
        {
            foreach (var format in new[] { "yyyy-MM-dd", "yyyy_MM_dd", "yyyy.MM.dd" })
            {
                if (DateOnly.TryParseExact(
                    fileName.AsSpan(0, 10),
                    format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var separatedDate))
                {
                    return separatedDate;
                }
            }
        }

        return null;
    }

    public string RenderMarkdown(string? markdown) =>
        SafeMarkdownRenderer.Render(markdown);

    public async Task<IActionResult> OnPostPreviewDescriptionAsync([FromForm] string? description)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        return Content(RenderMarkdown(description), "text/html");
    }

    public async Task<IActionResult> OnPostSaveDescriptionAsync(
        [FromForm] string? path,
        [FromForm] string? description)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        var sanitizedPath = SanitizePath(path ?? string.Empty).TrimStart('/');
        if (!string.Equals(sanitizedPath, path?.TrimStart('/'), StringComparison.Ordinal))
        {
            return BadRequest("Invalid gallery path.");
        }

        if (string.IsNullOrEmpty(sanitizedPath))
        {
            sanitizedPath = "root";
        }

        var pathEntry = await _context.Paths
            .FirstOrDefaultAsync(entry => entry.Path == sanitizedPath);
        if (pathEntry is null)
        {
            return NotFound();
        }

        pathEntry.Description = string.IsNullOrWhiteSpace(description) ? null : description;
        await _context.SaveChangesAsync(HttpContext.RequestAborted);
        await _filesystemPersistence.PersistPathAsync(sanitizedPath, HttpContext.RequestAborted);

        return new JsonResult(new
        {
            hasDescription = !string.IsNullOrWhiteSpace(pathEntry.Description),
            html = RenderMarkdown(pathEntry.Description)
        });
    }

    public async Task<IActionResult> OnPostSavePathTypeAsync(
        [FromForm] string? path,
        [FromForm] string? type)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        var sanitizedPath = SanitizePath(path ?? string.Empty).TrimStart('/');
        if (!string.Equals(sanitizedPath, path?.TrimStart('/'), StringComparison.Ordinal))
        {
            return BadRequest("Invalid gallery path.");
        }
        if (string.IsNullOrEmpty(sanitizedPath))
        {
            sanitizedPath = "root";
        }

        var normalizedType = string.IsNullOrWhiteSpace(type) ? null : type.Trim().ToLowerInvariant();
        if (normalizedType is not null && normalizedType != "comic")
        {
            return BadRequest("Unsupported directory display type.");
        }

        var pathEntry = await _context.Paths
            .FirstOrDefaultAsync(entry => entry.Path == sanitizedPath);
        if (pathEntry is null)
        {
            return NotFound();
        }

        pathEntry.Type = normalizedType;
        await _context.SaveChangesAsync(HttpContext.RequestAborted);
        await _filesystemPersistence.PersistPathAsync(sanitizedPath, HttpContext.RequestAborted);
        return new JsonResult(new { type = normalizedType });
    }

    public async Task<IActionResult> OnPostSaveDirectoryTagsAsync(
        [FromForm] string? path,
        [FromForm] string? tags)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        var sanitizedPath = SanitizePath(path ?? string.Empty).TrimStart('/');
        if (!string.Equals(sanitizedPath, path?.TrimStart('/'), StringComparison.Ordinal))
        {
            return BadRequest("Invalid gallery path.");
        }
        if (string.IsNullOrEmpty(sanitizedPath))
        {
            sanitizedPath = "root";
        }
        if (!TagNameNormalizer.TryNormalize(tags, out var normalizedTags, out var error))
        {
            return BadRequest(error ?? "Invalid tags.");
        }

        var pathEntry = await _context.Paths
            .Include(entry => entry.TagLinks)
            .FirstOrDefaultAsync(entry => entry.Path == sanitizedPath);
        if (pathEntry is null)
        {
            return NotFound();
        }

        var tagEntities = await _context.Tags
            .Where(tag => normalizedTags.Contains(tag.Name))
            .ToListAsync();
        var existingNames = tagEntities.Select(tag => tag.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var tagName in normalizedTags.Where(name => !existingNames.Contains(name)))
        {
            var tag = new Tag { Name = tagName };
            tagEntities.Add(tag);
            _context.Tags.Add(tag);
        }

        _context.PathTags.RemoveRange(pathEntry.TagLinks);
        _context.PathTags.AddRange(tagEntities.Select(tag => new PathTag
        {
            PathId = pathEntry.Id,
            Tag = tag
        }));
        await _context.SaveChangesAsync(HttpContext.RequestAborted);
        await _filesystemPersistence.PersistPathAsync(sanitizedPath, HttpContext.RequestAborted);

        return new JsonResult(new { tags = normalizedTags });
    }

    public async Task<IActionResult> OnPostCreatePathAsync(
        [FromForm] string? parentPath,
        [FromForm] string? name,
        [FromForm] string? tags)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        var sanitizedParent = SanitizePath(parentPath ?? string.Empty).TrimStart('/');
        if (!string.Equals(sanitizedParent, parentPath?.TrimStart('/'), StringComparison.Ordinal))
        {
            return BadRequest("Invalid parent path.");
        }
        if (string.IsNullOrEmpty(sanitizedParent))
        {
            sanitizedParent = "root";
        }

        var pathName = name?.Trim();
        if (string.IsNullOrEmpty(pathName) ||
            pathName.Length > 255 ||
            pathName.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_'))
        {
            return BadRequest("Path names must be 1-255 letters, numbers, underscores, or hyphens.");
        }

        if (!TagNameNormalizer.TryNormalize(tags, out var normalizedTags, out var error))
        {
            return BadRequest(error ?? "Invalid tags.");
        }

        var parentEntry = await _context.Paths
            .FirstOrDefaultAsync(entry => entry.Path == sanitizedParent);
        if (parentEntry is null)
        {
            return NotFound("Parent path not found.");
        }

        var newPath = sanitizedParent == "root"
            ? pathName
            : $"{sanitizedParent}/{pathName}";
        if (newPath.Length > 1024)
        {
            return BadRequest("The full path cannot exceed 1024 characters.");
        }
        if (await _context.Paths.AnyAsync(entry => entry.Path == newPath))
        {
            return StatusCode(StatusCodes.Status409Conflict, "That path already exists.");
        }

        await EnsurePendingPathDeletionsTableAsync();
        var pendingDeletions = await _context.PendingPathDeletions
            .AsNoTracking()
            .Select(entry => entry.Path)
            .ToListAsync();
        if (pendingDeletions.Any(deletedPath =>
            string.Equals(newPath, deletedPath, StringComparison.OrdinalIgnoreCase) ||
            newPath.StartsWith(deletedPath + "/", StringComparison.OrdinalIgnoreCase) ||
            deletedPath.StartsWith(newPath + "/", StringComparison.OrdinalIgnoreCase)))
        {
            return StatusCode(
                StatusCodes.Status409Conflict,
                "Sync pending path deletions before creating a path in that location.");
        }

        await _fileStore.CreateDirectoryAsync(
            FileStoreArea.Gallery,
            GalleryDirectoryKey(newPath),
            HttpContext.RequestAborted);

        var pathEntry = new PathEntry
        {
            Path = newPath,
            ParentPath = sanitizedParent
        };
        _context.Paths.Add(pathEntry);

        var tagEntities = await _context.Tags
            .Where(tag => normalizedTags.Contains(tag.Name))
            .ToListAsync();
        var existingNames = tagEntities.Select(tag => tag.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var tagName in normalizedTags.Where(tagName => !existingNames.Contains(tagName)))
        {
            var tag = new Tag { Name = tagName };
            tagEntities.Add(tag);
            _context.Tags.Add(tag);
        }

        _context.PathTags.AddRange(tagEntities.Select(tag => new PathTag
        {
            Path = pathEntry,
            Tag = tag
        }));
        await _context.SaveChangesAsync(HttpContext.RequestAborted);
        await _filesystemPersistence.PersistPathAsync(newPath, HttpContext.RequestAborted);

        return new JsonResult(new { path = newPath });
    }

    public async Task<IActionResult> OnPostDeletePathAsync([FromForm] string? path)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        var sanitizedPath = SanitizePath(path ?? string.Empty).TrimStart('/');
        if (!string.Equals(sanitizedPath, path?.TrimStart('/'), StringComparison.Ordinal) ||
            string.IsNullOrEmpty(sanitizedPath))
        {
            return BadRequest("Invalid gallery path.");
        }
        if (string.Equals(sanitizedPath, "root", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("The root path cannot be deleted.");
        }

        if (!await _context.Paths.AnyAsync(entry => entry.Path == sanitizedPath))
        {
            return NotFound();
        }

        await EnsurePendingPathDeletionsTableAsync();
        var pendingDeletions = await _context.PendingPathDeletions
            .AsNoTracking()
            .Select(entry => entry.Path)
            .ToListAsync();
        if (pendingDeletions.Any(deletedPath =>
            string.Equals(sanitizedPath, deletedPath, StringComparison.OrdinalIgnoreCase) ||
            sanitizedPath.StartsWith(deletedPath + "/", StringComparison.OrdinalIgnoreCase)))
        {
            return StatusCode(
                StatusCodes.Status409Conflict,
                "This path is already queued for deletion. Run Sync DB to disk to finish deleting it.");
        }

        _context.PendingPathDeletions.Add(new PendingPathDeletion { Path = sanitizedPath });
        await _context.SaveChangesAsync();
        var removed = await DeletePathRecordsAsync(sanitizedPath);
        await _filesystemPersistence.DeletePathDirectoryAsync(sanitizedPath, HttpContext.RequestAborted);

        var redirectPath = sanitizedPath.Contains('/')
            ? sanitizedPath[..sanitizedPath.LastIndexOf('/')]
            : "root";

        return new JsonResult(new
        {
            redirectPath = redirectPath == "root" ? "/Gallery" : $"/Gallery/{redirectPath}",
            removed.Paths,
            removed.Items,
            removed.Shortcuts
        });
    }

    public async Task<IActionResult> OnPostRenameItemAsync(
        [FromForm] ulong itemId,
        [FromForm] string? newName)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        try
        {
            var item = await _filesystemPersistence.RenameItemAsync(itemId, newName, HttpContext.RequestAborted);
            return new JsonResult(new { id = item.Id, name = item.OriginalName, thumbnailKey = item.ThumbnailKey, thumbnailUrl = item.ThumbnailUrl });
        }
        catch (GalleryItemRenameException error)
        {
            return BadRequest(error.Message);
        }
    }

    public async Task<IActionResult> OnPostRenamePathAsync(
        [FromForm] string? path,
        [FromForm] string? newName)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        var sanitizedPath = SanitizePath(path ?? string.Empty).TrimStart('/');
        if (!string.Equals(sanitizedPath, path?.TrimStart('/'), StringComparison.Ordinal) ||
            string.IsNullOrEmpty(sanitizedPath))
        {
            return BadRequest("Invalid gallery path.");
        }

        return await RunPathOperationAsync(async (services, token) => new GalleryOperationResult(
            await services.GetRequiredService<FilesystemPersistenceService>()
                .RenamePathAsync(sanitizedPath, newName ?? string.Empty, token)));
    }

    public async Task<IActionResult> OnPostBulkAddTagsAsync(
        [FromForm] string? path,
        [FromForm] List<ulong>? itemIds,
        [FromForm] List<ulong>? shortcutIds,
        [FromForm] string? tags)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }
        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }
        if (!ModelState.IsValid)
        {
            return BadRequest("Invalid item selection or tags.");
        }
        if (!TagNameNormalizer.TryNormalize(tags, out var normalizedTags, out var error))
        {
            return BadRequest(error ?? "Invalid tags.");
        }
        if (normalizedTags.Count == 0)
        {
            return BadRequest("Enter at least one tag to add.");
        }

        var sanitizedPath = SanitizePath(path ?? string.Empty).TrimStart('/');
        if (!string.Equals(sanitizedPath, path?.TrimStart('/'), StringComparison.Ordinal))
        {
            return BadRequest("Invalid gallery path.");
        }
        if (string.IsNullOrEmpty(sanitizedPath))
        {
            sanitizedPath = "root";
        }

        var selectedItemIds = itemIds?.Distinct().ToList() ?? [];
        var selectedShortcutIds = shortcutIds?.Distinct().ToList() ?? [];
        if (selectedItemIds.Count + selectedShortcutIds.Count == 0)
        {
            return BadRequest("Select at least one item to tag.");
        }
        if (selectedItemIds.Count + selectedShortcutIds.Count > 500)
        {
            return BadRequest("Select no more than 500 items at a time.");
        }
        var cancellationToken = HttpContext.RequestAborted;
        var items = await _context.Items
            .Where(item => item.ParentPath == sanitizedPath && selectedItemIds.Contains(item.Id))
            .ToListAsync(cancellationToken);
        if (items.Count != selectedItemIds.Count)
        {
            return BadRequest("One or more selected items are not in this gallery.");
        }
        var shortcuts = await _context.Shortcuts
            .Include(shortcut => shortcut.OriginalItem)
            .Where(shortcut => shortcut.ShortcutPath == sanitizedPath && selectedShortcutIds.Contains(shortcut.Id))
            .ToListAsync(cancellationToken);
        if (shortcuts.Count != selectedShortcutIds.Count || shortcuts.Any(shortcut => shortcut.OriginalItem is null))
        {
            return BadRequest("One or more selected shortcuts are not in this gallery or have no original item.");
        }
        var targets = items.Concat(shortcuts.Select(shortcut => shortcut.OriginalItem!))
            .DistinctBy(item => item.Id).ToList();
        var targetIds = targets.Select(item => item.Id).ToList();
        var tagEntities = await _context.Tags.Where(tag => normalizedTags.Contains(tag.Name)).ToListAsync(cancellationToken);
        var existingNames = tagEntities.Select(tag => tag.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in normalizedTags.Where(name => !existingNames.Contains(name)))
        {
            var tag = new Tag { Name = name };
            _context.Tags.Add(tag);
            tagEntities.Add(tag);
        }
        var existingLinks = await _context.ItemTags.Where(link => targetIds.Contains(link.ItemId))
            .ToListAsync(cancellationToken);
        foreach (var item in targets)
        {
            var linkedTagIds = existingLinks.Where(link => link.ItemId == item.Id)
                .Select(link => link.TagId).ToHashSet();
            foreach (var tag in tagEntities.Where(tag => tag.Id == 0 || !linkedTagIds.Contains(tag.Id)))
            {
                _context.ItemTags.Add(new ItemTag { ItemId = item.Id, Tag = tag });
            }
        }
        await _context.SaveChangesAsync(cancellationToken);
        foreach (var parentPath in targets.Select(item => item.ParentPath ?? "root").Distinct(StringComparer.Ordinal))
        {
            await _filesystemPersistence.PersistPathAsync(parentPath, cancellationToken);
        }
        return new JsonResult(new { taggedItems = targets.Count, tags = normalizedTags });
    }

    public async Task<IActionResult> OnPostBulkDeleteAsync(
        [FromForm] string? path,
        [FromForm] List<ulong>? itemIds,
        [FromForm] List<ulong>? shortcutIds)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        var sanitizedPath = SanitizePath(path ?? string.Empty).TrimStart('/');
        if (!string.Equals(sanitizedPath, path?.TrimStart('/'), StringComparison.Ordinal))
        {
            return BadRequest("Invalid gallery path.");
        }
        if (string.IsNullOrEmpty(sanitizedPath))
        {
            sanitizedPath = "root";
        }

        var selectedItemIds = itemIds?.Distinct().ToList() ?? [];
        var selectedShortcutIds = shortcutIds?.Distinct().ToList() ?? [];
        if (selectedItemIds.Count + selectedShortcutIds.Count == 0)
        {
            return BadRequest("Select at least one item to delete.");
        }
        if (selectedItemIds.Count + selectedShortcutIds.Count > 500)
        {
            return BadRequest("Select no more than 500 items at a time.");
        }

        var items = await _context.Items
            .AsNoTracking()
            .Where(item => item.ParentPath == sanitizedPath && selectedItemIds.Contains(item.Id))
            .ToListAsync(HttpContext.RequestAborted);
        if (items.Count != selectedItemIds.Count)
        {
            return BadRequest("One or more selected items are not in this gallery.");
        }

        var shortcuts = await _context.Shortcuts
            .AsNoTracking()
            .Where(shortcut =>
                shortcut.ShortcutPath == sanitizedPath &&
                selectedShortcutIds.Contains(shortcut.Id))
            .ToListAsync(HttpContext.RequestAborted);
        if (shortcuts.Count != selectedShortcutIds.Count)
        {
            return BadRequest("One or more selected shortcuts are not in this gallery.");
        }

        if (items.Count > 0)
        {
            await EnsurePendingItemDeletionsTableAsync();
        }

        var cancellationToken = HttpContext.RequestAborted;
        var executionStrategy = _context.Database.CreateExecutionStrategy();
        await executionStrategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            foreach (var item in items)
            {
                await _context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO PendingItemDeletions (parent_path, original_name) VALUES ({item.ParentPath ?? "root"}, {item.OriginalName})",
                    cancellationToken);
            }

            if (selectedShortcutIds.Count > 0)
            {
                await _context.Shortcuts
                    .Where(shortcut =>
                        shortcut.ShortcutPath == sanitizedPath &&
                        selectedShortcutIds.Contains(shortcut.Id))
                    .ExecuteDeleteAsync(cancellationToken);
            }

            if (selectedItemIds.Count > 0)
            {
                await _context.Items
                    .Where(item => item.ParentPath == sanitizedPath && selectedItemIds.Contains(item.Id))
                    .ExecuteDeleteAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        });
        foreach (var item in items)
        {
            await _filesystemPersistence.DeleteItemFileAsync(
                item.ParentPath ?? "root",
                item.OriginalName,
                cancellationToken);
        }
        await _filesystemPersistence.PersistPathAsync(sanitizedPath, cancellationToken);

        var redirectPath = sanitizedPath == "root"
            ? "/Gallery"
            : "/Gallery/" + string.Join(
                '/',
                sanitizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Select(Uri.EscapeDataString));
        return new JsonResult(new
        {
            redirectPath,
            deletedItems = items.Count,
            deletedShortcuts = shortcuts.Count
        });
    }

    public async Task<IActionResult> OnGetSearchMovePathsAsync(
        [FromQuery] string? path,
        [FromQuery] string? query,
        [FromQuery] bool movingPath = false,
        [FromQuery] bool creatingShortcut = false)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        var sanitizedPath = SanitizePath(path ?? string.Empty).TrimStart('/');
        if (!string.Equals(sanitizedPath, path?.TrimStart('/'), StringComparison.Ordinal))
        {
            return BadRequest("Invalid gallery path.");
        }
        if (string.IsNullOrEmpty(sanitizedPath))
        {
            sanitizedPath = "root";
        }
        if (!await _context.Paths.AnyAsync(entry => entry.Path == sanitizedPath, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        var search = query?.Trim();
        if (string.IsNullOrEmpty(search) || search.Length > 256)
        {
            return new JsonResult(Array.Empty<object>());
        }

        var sourceParent = await _context.Paths.Where(entry => entry.Path == sanitizedPath)
            .Select(entry => entry.ParentPath).FirstAsync(HttpContext.RequestAborted);
        var paths = await _context.Paths
            .Where(entry => entry.Path.Contains(search) || (search == "/" && entry.Path == "root"))
            .OrderBy(entry => entry.Path)
            .Select(entry => entry.Path)
            .Take(50)
            .ToListAsync(HttpContext.RequestAborted);

        var linkedPaths = creatingShortcut
            ? await _context.DirectoryShortcuts.Where(entry => entry.TargetPath.Path == sanitizedPath)
                .Select(entry => entry.ParentPath.Path).ToListAsync(HttpContext.RequestAborted)
            : [];
        return new JsonResult(paths.Select(entry => new
        {
            path = entry,
            displayPath = entry == "root" ? "/" : entry,
            unavailable = (creatingShortcut
                ? linkedPaths.Contains(entry, StringComparer.OrdinalIgnoreCase) ||
                    string.Equals(entry, sourceParent, StringComparison.OrdinalIgnoreCase)
                : string.Equals(entry, sanitizedPath, StringComparison.OrdinalIgnoreCase)) ||
                (movingPath && (IsSameOrChildPath(entry, sanitizedPath) ||
                    string.Equals(entry, sourceParent ?? "root", StringComparison.OrdinalIgnoreCase)))
        }));
    }

    public async Task<IActionResult> OnPostCreateDirectoryShortcutAsync(
        [FromForm] string? path,
        [FromForm] string? targetPath)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }
        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }
        var sanitizedPath = SanitizePath(path ?? string.Empty).TrimStart('/');
        if (!ModelState.IsValid || string.IsNullOrEmpty(sanitizedPath) ||
            !string.Equals(sanitizedPath, path?.TrimStart('/'), StringComparison.Ordinal))
        {
            return BadRequest("Invalid gallery path.");
        }
        if (string.IsNullOrWhiteSpace(targetPath) || targetPath.Length > 1024)
        {
            return BadRequest("Select an existing destination directory.");
        }
        var source = await _context.Paths.FirstOrDefaultAsync(entry => entry.Path == sanitizedPath, HttpContext.RequestAborted);
        var destination = await _context.Paths.FirstOrDefaultAsync(entry => entry.Path == targetPath, HttpContext.RequestAborted);
        if (source is null || destination is null)
        {
            return NotFound("The source or destination directory no longer exists.");
        }
        var name = source.Path == "root" ? "Home" : source.Path.Split('/').Last();
        if (await _context.DirectoryShortcuts.AnyAsync(entry => entry.ParentPathId == destination.Id &&
                (entry.TargetPathId == source.Id || entry.Name == name), HttpContext.RequestAborted) ||
            await _context.Paths.AnyAsync(entry => entry.ParentPath == destination.Path && entry.Path != entry.ParentPath &&
                entry.Path == (destination.Path == "root" ? name : destination.Path + "/" + name), HttpContext.RequestAborted))
        {
            return new ConflictObjectResult("The destination already contains this directory or a shortcut with that name.");
        }
        _context.DirectoryShortcuts.Add(new DirectoryShortcut
        {
            ParentPathId = destination.Id,
            TargetPathId = source.Id,
            Name = name
        });
        await _context.SaveChangesAsync(HttpContext.RequestAborted);
        await _filesystemPersistence.PersistDirectoryShortcutsAsync(HttpContext.RequestAborted);
        return new JsonResult(new { saved = true });
    }

    public async Task<IActionResult> OnPostMoveDirectoryShortcutAsync(
        [FromForm] string? path,
        [FromForm] ulong shortcutId,
        [FromForm] string? targetPath)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }
        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }
        var sanitizedPath = SanitizePath(path ?? string.Empty).TrimStart('/');
        if (!ModelState.IsValid || string.IsNullOrEmpty(sanitizedPath) ||
            !string.Equals(sanitizedPath, path?.TrimStart('/'), StringComparison.Ordinal))
        {
            return BadRequest("Invalid gallery path.");
        }
        if (string.IsNullOrWhiteSpace(targetPath) || targetPath.Length > 1024)
        {
            return BadRequest("Select an existing destination directory.");
        }
        var shortcut = await _context.DirectoryShortcuts
            .FirstOrDefaultAsync(entry => entry.Id == shortcutId && entry.ParentPath.Path == sanitizedPath,
                HttpContext.RequestAborted);
        if (shortcut is null)
        {
            return NotFound("The directory shortcut is no longer in this gallery.");
        }
        var destination = await _context.Paths.FirstOrDefaultAsync(entry => entry.Path == targetPath,
            HttpContext.RequestAborted);
        if (destination is null)
        {
            return BadRequest("Select an existing destination directory.");
        }
        if (destination.Id == shortcut.ParentPathId)
        {
            return new ConflictObjectResult("The shortcut is already in that directory.");
        }
        if (await _context.DirectoryShortcuts.AnyAsync(entry =>
            entry.ParentPathId == destination.Id && entry.Name == shortcut.Name, HttpContext.RequestAborted) ||
            await _context.Paths.AnyAsync(entry => entry.ParentPath == destination.Path &&
                entry.Path == (destination.Path == "root" ? shortcut.Name : destination.Path + "/" + shortcut.Name),
                HttpContext.RequestAborted))
        {
            return new ConflictObjectResult("The destination already contains a directory or shortcut with that name.");
        }
        shortcut.ParentPathId = destination.Id;
        await _context.SaveChangesAsync(HttpContext.RequestAborted);
        await _filesystemPersistence.PersistDirectoryShortcutsAsync(HttpContext.RequestAborted);
        return new JsonResult(new
        {
            redirectPath = destination.Path == "root" ? "/Gallery" :
                "/Gallery/" + string.Join('/', destination.Path.Split('/').Select(Uri.EscapeDataString))
        });
    }

    public async Task<IActionResult> OnPostDeleteDirectoryShortcutAsync(
        [FromForm] string? path,
        [FromForm] ulong shortcutId)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }
        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }
        var sanitizedPath = SanitizePath(path ?? string.Empty).TrimStart('/');
        if (!ModelState.IsValid || string.IsNullOrEmpty(sanitizedPath) ||
            !string.Equals(sanitizedPath, path?.TrimStart('/'), StringComparison.Ordinal))
        {
            return BadRequest("Invalid gallery path.");
        }
        var shortcut = await _context.DirectoryShortcuts
            .FirstOrDefaultAsync(entry => entry.Id == shortcutId && entry.ParentPath.Path == sanitizedPath,
                HttpContext.RequestAborted);
        if (shortcut is null)
        {
            return NotFound("The directory shortcut is no longer in this gallery.");
        }
        _context.DirectoryShortcuts.Remove(shortcut);
        await _context.SaveChangesAsync(HttpContext.RequestAborted);
        await _filesystemPersistence.PersistDirectoryShortcutsAsync(HttpContext.RequestAborted);
        return new JsonResult(new { deletedShortcutId = shortcutId });
    }

    public async Task<IActionResult> OnPostMovePathAsync(
        [FromForm] string? path,
        [FromForm] string? targetPath)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }
        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }
        var sanitizedPath = SanitizePath(path ?? string.Empty).TrimStart('/');
        if (!ModelState.IsValid || string.IsNullOrEmpty(sanitizedPath) ||
            !string.Equals(sanitizedPath, path?.TrimStart('/'), StringComparison.Ordinal))
        {
            return BadRequest("Invalid gallery path.");
        }
        if (string.IsNullOrWhiteSpace(targetPath) || targetPath.Length > 1024)
        {
            return BadRequest("Select an existing destination directory.");
        }
        return await RunPathOperationAsync((services, token) =>
            services.GetRequiredService<FilesystemPersistenceService>()
                .MovePathAsync(sanitizedPath, targetPath, token));
    }

    public async Task<IActionResult> OnPostReloadPathAsync([FromForm] string? path)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }
        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }
        var sanitizedPath = SanitizePath(path ?? string.Empty).TrimStart('/');
        if (string.IsNullOrEmpty(sanitizedPath) ||
            !string.Equals(sanitizedPath, path?.TrimStart('/'), StringComparison.Ordinal))
        {
            return BadRequest("Invalid gallery path.");
        }
        return await RunPathOperationAsync((services, token) =>
            services.GetRequiredService<GalleryPathReloader>().ReloadAsync(sanitizedPath, token));
    }

    public IActionResult OnGetPathOperation([FromQuery] string? id)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }
        var operation = HttpContext.RequestServices.GetRequiredService<GalleryPathOperations>().Find(id);
        return operation is null
            ? NotFound("The path operation is no longer tracked. Refresh the page to check its current state.")
            : PathOperationResult(operation);
    }

    private async Task<IActionResult> RunPathOperationAsync(
        Func<IServiceProvider, CancellationToken, Task<GalleryOperationResult>> run)
    {
        var operation = HttpContext.RequestServices.GetRequiredService<GalleryPathOperations>().TryStart(run);
        if (operation is null)
        {
            return StatusCode(
                StatusCodes.Status409Conflict,
                "Another path move, rename or reload is still running. Try again when it finishes.");
        }
        await Task.WhenAny(operation.Task, Task.Delay(GalleryPathOperations.InlineWait, HttpContext.RequestAborted));
        return PathOperationResult(operation);
    }

    private IActionResult PathOperationResult(GalleryPathOperations.Operation operation)
    {
        if (!operation.Task.IsCompleted)
        {
            return StatusCode(StatusCodes.Status202Accepted, new { operationId = operation.Id });
        }
        if (operation.Task.IsCompletedSuccessfully)
        {
            var (path, message) = operation.Task.Result;
            return new JsonResult(new
            {
                path,
                message,
                redirectPath = "/Gallery/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString))
            });
        }
        var error = operation.Task.Exception?.GetBaseException() ?? new OperationCanceledException();
        return StatusCode(
            GalleryPathOperations.IsUserError(error) ? StatusCodes.Status409Conflict : StatusCodes.Status500InternalServerError,
            GalleryPathOperations.ErrorMessage(error));
    }

    public async Task<IActionResult> OnPostBulkMoveAsync(
        [FromForm] string? path,
        [FromForm] List<ulong>? itemIds,
        [FromForm] List<ulong>? shortcutIds,
        [FromForm] string? targetPath)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        var sanitizedPath = SanitizePath(path ?? string.Empty).TrimStart('/');
        if (!string.Equals(sanitizedPath, path?.TrimStart('/'), StringComparison.Ordinal))
        {
            return BadRequest("Invalid gallery path.");
        }
        if (string.IsNullOrEmpty(sanitizedPath))
        {
            sanitizedPath = "root";
        }
        if (string.IsNullOrWhiteSpace(targetPath) || targetPath.Length > 1024)
        {
            return BadRequest("Select an existing destination directory.");
        }

        var selectedItemIds = itemIds?.Distinct().ToList() ?? [];
        var selectedShortcutIds = shortcutIds?.Distinct().ToList() ?? [];
        if (selectedItemIds.Count + selectedShortcutIds.Count == 0)
        {
            return BadRequest("Select at least one item to move.");
        }
        if (selectedItemIds.Count + selectedShortcutIds.Count > 500)
        {
            return BadRequest("Select no more than 500 items at a time.");
        }

        var items = await _context.Items
            .AsNoTracking()
            .Where(item => item.ParentPath == sanitizedPath && selectedItemIds.Contains(item.Id))
            .ToListAsync(HttpContext.RequestAborted);
        if (items.Count != selectedItemIds.Count)
        {
            return BadRequest("One or more selected items are not in this gallery.");
        }

        var shortcuts = await _context.Shortcuts
            .AsNoTracking()
            .Where(shortcut =>
                shortcut.ShortcutPath == sanitizedPath &&
                selectedShortcutIds.Contains(shortcut.Id))
            .ToListAsync(HttpContext.RequestAborted);
        if (shortcuts.Count != selectedShortcutIds.Count)
        {
            return BadRequest("One or more selected shortcuts are not in this gallery.");
        }

        try
        {
            await _filesystemPersistence.MoveEntriesAsync(
                items,
                shortcuts,
                targetPath,
                HttpContext.RequestAborted);
        }
        catch (GalleryMoveException error)
        {
            return StatusCode(StatusCodes.Status409Conflict, error.Message);
        }

        var redirectPath = targetPath == "root"
            ? "/Gallery"
            : "/Gallery/" + string.Join(
                '/',
                targetPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Select(Uri.EscapeDataString));
        return new JsonResult(new
        {
            redirectPath,
            movedItems = items.Count,
            movedShortcuts = shortcuts.Count
        });
    }

    public async Task<IActionResult> OnPostUploadAsync(
        [FromForm] string? path,
        [FromForm] List<IFormFile>? files)
    {
        var (target, failure) = await ResolveUploadTargetAsync(path);
        if (target is null)
        {
            return failure!;
        }

        if (files is null || files.Count == 0)
        {
            return BadRequest("Select at least one file to upload.");
        }

        var uploaded = new List<string>();
        var errors = new List<string>();
        foreach (var file in files)
        {
            HttpContext.RequestAborted.ThrowIfCancellationRequested();
            var fileName = System.IO.Path.GetFileName((file.FileName ?? string.Empty).Replace('\\', '/'));
            var error = await CheckUploadNameAsync(target, fileName);
            if (error is null)
            {
                await using (var uploadStream = file.OpenReadStream())
                {
                    await _fileStore.WriteAsync(
                        FileStoreArea.Gallery,
                        JoinStorageKey(target.Directory, fileName),
                        uploadStream,
                        overwrite: false,
                        HttpContext.RequestAborted);
                }
                error = await FinalizeUploadAsync(target, fileName);
            }

            if (error is null)
            {
                uploaded.Add(fileName);
            }
            else
            {
                errors.Add(error);
            }
        }

        return new JsonResult(new { uploaded, errors });
    }

    // Step 1: validate the name and choose how the browser sends the file.
    //   recover - the file is already in storage but not in the gallery (an interrupted upload); offer to register it.
    //   chunked - send chunks to OnPostUploadChunkAsync, then call OnPostCompleteUploadAsync with the upload ID.
    //   whole   - post the whole file to OnPostUploadAsync (Bypass mode, or a store without chunk support).
    public async Task<IActionResult> OnPostBeginUploadAsync(
        [FromForm] string? path,
        [FromForm] string? fileName)
    {
        var (target, failure) = await ResolveUploadTargetAsync(path);
        if (target is null)
        {
            return failure!;
        }

        var name = fileName ?? string.Empty;
        if (UploadRedirectUrl is not null)
        {
            return BadRequest($"{name}: uploads must be made from https://{_uploadOptions.BypassHost}.");
        }

        if (!IsValidUploadFileName(name))
        {
            return BadRequest($"{name}: invalid filename.");
        }

        if (await _context.Items.AnyAsync(
                item => item.ParentPath == target.Path && item.OriginalName == name,
                HttpContext.RequestAborted))
        {
            return BadRequest($"{name}: a file with that name already exists in this gallery.");
        }

        var key = JoinStorageKey(target.Directory, name);
        if (await _fileStore.FileExistsAsync(FileStoreArea.Gallery, key, HttpContext.RequestAborted))
        {
            return new JsonResult(new { mode = "recover" });
        }

        if (_uploadOptions.Mode == UploadMode.Bypass || _fileStore is not IChunkedUploadStore chunkedStore)
        {
            return new JsonResult(new { mode = "whole" });
        }

        var uploadId = await chunkedStore.BeginChunkedUploadAsync(FileStoreArea.Gallery, key, HttpContext.RequestAborted);
        return new JsonResult(new { mode = "chunked", uploadId, chunkSize = _uploadOptions.ChunkSizeBytes });
    }

    // Step 2 (chunked): the request body is one raw chunk; the anti-forgery token is sent as a header.
    public async Task<IActionResult> OnPostUploadChunkAsync(
        [FromQuery] string? path,
        [FromQuery] string? fileName,
        [FromQuery] string? uploadId,
        [FromQuery] int partNumber)
    {
        var (target, failure) = await ResolveUploadTargetAsync(path);
        if (target is null)
        {
            return failure!;
        }

        var name = fileName ?? string.Empty;
        if (_fileStore is not IChunkedUploadStore chunkedStore || string.IsNullOrEmpty(uploadId) ||
            !IsValidUploadFileName(name))
        {
            return BadRequest($"{name}: invalid chunk upload.");
        }

        if (partNumber is < 1 or > ChunkedUploadLimits.MaximumParts)
        {
            return BadRequest($"{name}: the file needs more than {ChunkedUploadLimits.MaximumParts} chunks.");
        }

        if (Request.ContentLength is null or 0 && partNumber > 1)
        {
            return BadRequest($"{name}: empty chunk.");
        }

        try
        {
            await chunkedStore.UploadChunkAsync(
                FileStoreArea.Gallery,
                JoinStorageKey(target.Directory, name),
                uploadId,
                partNumber,
                Request.Body,
                HttpContext.RequestAborted);
        }
        catch (Exception error) when (error is InvalidDataException or FileNotFoundException)
        {
            return BadRequest($"{name}: {error.Message}");
        }

        return new JsonResult(new { partNumber });
    }

    public async Task<IActionResult> OnPostAbortUploadAsync(
        [FromForm] string? path,
        [FromForm] string? fileName,
        [FromForm] string? uploadId)
    {
        var (target, failure) = await ResolveUploadTargetAsync(path);
        if (target is null)
        {
            return failure!;
        }

        var name = fileName ?? string.Empty;
        if (_fileStore is not IChunkedUploadStore chunkedStore || string.IsNullOrEmpty(uploadId) ||
            !IsValidUploadFileName(name))
        {
            return BadRequest($"{name}: invalid chunk upload.");
        }

        try
        {
            await chunkedStore.AbortChunkedUploadAsync(
                FileStoreArea.Gallery,
                JoinStorageKey(target.Directory, name),
                uploadId,
                HttpContext.RequestAborted);
        }
        catch (InvalidDataException error)
        {
            return BadRequest($"{name}: {error.Message}");
        }

        return new JsonResult(new { aborted = name });
    }

    // Final step: assemble the chunks (if uploadId is given) or register a recovered file, then thumbnail and record it.
    public async Task<IActionResult> OnPostCompleteUploadAsync(
        [FromForm] string? path,
        [FromForm] string? fileName,
        [FromForm] string? uploadId,
        [FromForm] int? partCount)
    {
        var (target, failure) = await ResolveUploadTargetAsync(path);
        if (target is null)
        {
            return failure!;
        }

        var name = fileName ?? string.Empty;
        if (!IsValidUploadFileName(name))
        {
            return BadRequest($"{name}: invalid filename.");
        }

        var key = JoinStorageKey(target.Directory, name);
        var chunkedStore = _fileStore as IChunkedUploadStore;
        if (!string.IsNullOrEmpty(uploadId) && (chunkedStore is null || partCount is null))
        {
            return BadRequest($"{name}: invalid chunk upload.");
        }

        if (await _context.Items.AnyAsync(
                item => item.ParentPath == target.Path && item.OriginalName == name,
                HttpContext.RequestAborted))
        {
            if (!string.IsNullOrEmpty(uploadId))
            {
                await chunkedStore!.AbortChunkedUploadAsync(FileStoreArea.Gallery, key, uploadId, CancellationToken.None);
            }

            return BadRequest($"{name}: a file with that name already exists in this gallery.");
        }

        if (!string.IsNullOrEmpty(uploadId))
        {
            try
            {
                await chunkedStore!.CompleteChunkedUploadAsync(
                    FileStoreArea.Gallery,
                    key,
                    uploadId,
                    partCount!.Value,
                    HttpContext.RequestAborted);
            }
            catch (Exception error) when (error is IOException or InvalidDataException)
            {
                await chunkedStore!.AbortChunkedUploadAsync(FileStoreArea.Gallery, key, uploadId, CancellationToken.None);
                return BadRequest($"{name}: {error.Message}");
            }
        }
        else if (!await _fileStore.FileExistsAsync(FileStoreArea.Gallery, key, HttpContext.RequestAborted))
        {
            return BadRequest($"{name}: the uploaded file was not found in storage.");
        }

        var finalizeError = await FinalizeUploadAsync(target, name);
        return finalizeError is null ? new JsonResult(new { uploaded = name }) : BadRequest(finalizeError);
    }

    private sealed record UploadTarget(string Path, string Directory);

    private async Task<(UploadTarget? Target, IActionResult? Failure)> ResolveUploadTargetAsync(string? path)
    {
        if (!AdminEnabled)
        {
            return (null, NotFound());
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return (null, BadRequest("Invalid anti-forgery token."));
        }

        var sanitizedPath = SanitizePath(path ?? string.Empty).TrimStart('/');
        if (!string.Equals(sanitizedPath, path?.TrimStart('/'), StringComparison.Ordinal) ||
            string.IsNullOrEmpty(sanitizedPath))
        {
            return (null, BadRequest("Invalid gallery path."));
        }

        if (!await _context.Paths.AnyAsync(entry => entry.Path == sanitizedPath, HttpContext.RequestAborted))
        {
            return (null, NotFound("Gallery path not found."));
        }

        var imageDirectory = GalleryDirectoryKey(sanitizedPath);
        if (!await _fileStore.DirectoryExistsAsync(
                FileStoreArea.Gallery,
                imageDirectory,
                HttpContext.RequestAborted))
        {
            throw new DirectoryNotFoundException($"Gallery directory does not exist: {sanitizedPath}");
        }

        return (new UploadTarget(sanitizedPath, imageDirectory), null);
    }

    // Returns an error message, or null when a new file can be stored under this name.
    private async Task<string?> CheckUploadNameAsync(UploadTarget target, string fileName)
    {
        if (!IsValidUploadFileName(fileName))
        {
            return $"{fileName}: invalid filename.";
        }

        if (await _context.Items.AnyAsync(
                item => item.ParentPath == target.Path && item.OriginalName == fileName,
                HttpContext.RequestAborted))
        {
            return $"{fileName}: a file with that name already exists in this gallery.";
        }

        if (await _fileStore.FileExistsAsync(
                FileStoreArea.Gallery,
                JoinStorageKey(target.Directory, fileName),
                HttpContext.RequestAborted))
        {
            return $"{fileName}: a file with that name already exists in storage.";
        }

        return null;
    }

    // Thumbnails (images only) and records a file already in storage. On failure the stored file and any
    // thumbnail are deleted and an error message is returned.
    private async Task<string?> FinalizeUploadAsync(UploadTarget target, string fileName)
    {
        var sourceKey = JoinStorageKey(target.Directory, fileName);
        var isImage = ThumbnailImageExtensions.Contains(System.IO.Path.GetExtension(fileName));
        var thumbnailKey = CreateThumbnailKey(target.Path, fileName);
        var thumbnailStorageKey = $"{thumbnailKey}.png";
        var thumbnailCreated = false;
        var uploadSucceeded = false;
        try
        {
            if (isImage)
            {
                await GenerateThumbnailAsync(sourceKey, thumbnailKey, HttpContext.RequestAborted);
                thumbnailCreated = true;
            }

            var now = DateTime.Now;
            var item = new Item
            {
                OriginalName = fileName,
                ParentPath = target.Path,
                ThumbnailKey = thumbnailKey,
                Uploaded = now,
                Created = GetCreatedDate(fileName, target.Path, now),
                Type = isImage ? "image" : "file",
                Metadata = "{}"
            };
            _context.Items.Add(item);

            var dateTags = DatePathTags.ForPath(target.Path);
            if (dateTags.Count > 0)
            {
                var tagEntities = await _context.Tags
                    .Where(tag => dateTags.Contains(tag.Name))
                    .ToListAsync(HttpContext.RequestAborted);
                var existingNames = tagEntities.Select(tag => tag.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var tagName in dateTags.Where(tagName => !existingNames.Contains(tagName)))
                {
                    var tag = new Tag { Name = tagName };
                    tagEntities.Add(tag);
                    _context.Tags.Add(tag);
                }

                foreach (var tag in tagEntities)
                {
                    item.TagLinks.Add(new ItemTag { Item = item, Tag = tag });
                }
            }

            await _context.SaveChangesAsync(HttpContext.RequestAborted);
            uploadSucceeded = true;
            return null;
        }
        catch (InvalidThumbnailImageException error)
        {
            return $"{fileName}: {error.Message}";
        }
        catch (HttpRequestException error)
        {
            return $"{fileName}: thumbnail service failed: {error.Message}";
        }
        finally
        {
            if (!uploadSucceeded)
            {
                await _fileStore.DeleteFileAsync(FileStoreArea.Gallery, sourceKey, CancellationToken.None);
                if (thumbnailCreated)
                {
                    await _fileStore.DeleteFileAsync(
                        FileStoreArea.Thumbnails,
                        thumbnailStorageKey,
                        CancellationToken.None);
                }
                _context.ChangeTracker.Clear();
            }
        }
    }

    private static bool IsValidUploadFileName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        fileName.Length <= 255 &&
        fileName is not "." and not ".." &&
        fileName == fileName.Trim() &&
        !fileName.Any(character =>
            char.IsControl(character) ||
            character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*');

    private static string CreateThumbnailKey(string parentPath, string originalName)
    {
        var normalizedPath = parentPath == "root"
            ? originalName
            : $"{parentPath}/{originalName}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
    }

    private async Task GenerateThumbnailAsync(
        string sourceKey,
        string thumbnailKey,
        CancellationToken cancellationToken)
    {
        await ThumbnailGenerator.GenerateAsync(_fileStore, _thumbnailClient, sourceKey, thumbnailKey, cancellationToken);
    }

    private static string GalleryDirectoryKey(string path)
    {
        if (string.Equals(path, "root", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return path.TrimEnd('/');
    }

    private static string JoinStorageKey(string directory, string name) =>
        string.IsNullOrEmpty(directory) ? name : $"{directory}/{name}";

    private static DateTime GetCreatedDate(string fileName, string path, DateTime fallback) =>
        GalleryItemDates.Created(fileName, path, fallback);

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

    private async Task<(int Paths, int Items, int Shortcuts)> DeletePathRecordsAsync(string path)
    {
        var pathPrefix = path + "/";
        var shortcuts = await _context.Shortcuts
            .Where(shortcut =>
                shortcut.ShortcutPath == path ||
                (shortcut.ShortcutPath != null && shortcut.ShortcutPath.StartsWith(pathPrefix)))
            .ExecuteDeleteAsync(HttpContext.RequestAborted);
        var items = await _context.Items
            .Where(item =>
                item.ParentPath == path ||
                (item.ParentPath != null && item.ParentPath.StartsWith(pathPrefix)))
            .ExecuteDeleteAsync(HttpContext.RequestAborted);
        var paths = await _context.Paths
            .Where(entry => entry.Path == path || entry.Path.StartsWith(pathPrefix))
            .ExecuteDeleteAsync(HttpContext.RequestAborted);

        return (paths, items, shortcuts);
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
