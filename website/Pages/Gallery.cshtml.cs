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
    private static readonly MarkdownPipeline DescriptionMarkdownPipeline =
        new MarkdownPipelineBuilder().DisableHtml().Build();
    private const long MaximumUploadFileBytes = 50L * 1024 * 1024;
    private const int MaximumUploadFiles = 50;
    private static readonly HashSet<string> SupportedUploadExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif" };

    private readonly AppDbContext _context;
    private readonly ILogger<GalleryModel> _logger;
    private readonly IConfiguration _configuration;
    private readonly IAntiforgery _antiforgery;
    private readonly AdminOnlyTagsProvider _adminOnlyTagsProvider;
    private readonly HttpClient _thumbnailClient;

    public GalleryModel(
        AppDbContext context,
        ILogger<GalleryModel> logger,
        IConfiguration configuration,
        IAntiforgery antiforgery,
        AdminOnlyTagsProvider adminOnlyTagsProvider,
        IHttpClientFactory httpClientFactory)
    {
        _context = context;
        _logger = logger;
        _configuration = configuration;
        _antiforgery = antiforgery;
        _adminOnlyTagsProvider = adminOnlyTagsProvider;
        _thumbnailClient = httpClientFactory.CreateClient("ThumbnailService");
    }

    [BindProperty(SupportsGet = true)]
    public string? Path { get; set; }

    public List<Item> Items { get; set; } = new();
    public List<PathEntry> Paths { get; set; } = new();
    public List<Shortcut> Shortcuts { get; set; } = new();

    public PathEntry? PathEntry { get; set; }
    public string? Message { get; set; }
    public string? CurrentPath { get; set; }
    public string? ParentPath { get; set; }
    public bool AdminEnabled => AdminAccess.IsAdmin(_configuration, User);
    public HashSet<string> AdminOnlyTags { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> GetItemTags(Item item) =>
        item.TagLinks.Select(link => link.Tag.Name)
            .Concat(DatePathTags.ForPath(item.ParentPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(tag => AdminEnabled || !AdminOnlyTags.Contains(tag))
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async Task<IActionResult> OnGetAsync(string? path)
    {
        AdminOnlyTags = await _adminOnlyTagsProvider.GetTagsAsync(HttpContext.RequestAborted);
        var input = string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(Path)
            ? "root"
            : path ?? Path ?? "root";

        var sanitized = SanitizePath(input).TrimStart('/');
        _logger.LogDebug("Gallery path: {Path}", sanitized);

        CurrentPath = sanitized;
        var hiddenPaths = AdminEnabled
            ? []
            : await _context.Paths
                .Where(entry => entry.TagLinks.Any(link => AdminOnlyTags.Contains(link.Tag.Name)))
                .Select(entry => entry.Path)
                .ToListAsync(HttpContext.RequestAborted);
        if (!AdminEnabled && hiddenPaths.Any(hidden => IsSameOrChildPath(sanitized, hidden)))
        {
            return NotFound();
        }

        PathEntry = await _context.Paths
            .Include(entry => entry.TagLinks)
            .ThenInclude(link => link.Tag)
            .FirstOrDefaultAsync(entry => entry.Path == sanitized);
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

        // Apply sorting rules
        Items = Items
            .OrderBy(i => i.DisplayIndex)
            .ThenBy(i => i.OriginalName)
            .ThenBy(i => i.Created)
            .ThenBy(i => i.Uploaded)
            .ToList();

        ParentPath = sanitized.Contains('/')
            ? sanitized.Substring(0, sanitized.LastIndexOf('/'))
            : "root";

        if (!Items.Any() && !Paths.Any())
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
        var segments = (parentPath ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Append(fileName)
            .Select(Uri.EscapeDataString);
        return "/img_src/" + string.Join('/', segments);
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

    public string RenderMarkdown(string? markdown) =>
        Markdig.Markdown.ToHtml(markdown ?? string.Empty, DescriptionMarkdownPipeline);

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
        await _context.SaveChangesAsync();

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
        await _context.SaveChangesAsync();
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
        await _context.SaveChangesAsync();

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

        var directory = ResolveImageDirectory("/img_src", newPath, allowRoot: false);
        Directory.CreateDirectory(directory);

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
        await _context.SaveChangesAsync();

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

    public async Task<IActionResult> OnPostUploadAsync(
        [FromForm] string? path,
        [FromForm] List<IFormFile>? files)
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

        if (files is null || files.Count == 0)
        {
            return BadRequest("Select at least one image to upload.");
        }
        if (files.Count > MaximumUploadFiles)
        {
            return BadRequest($"Select no more than {MaximumUploadFiles} files at a time.");
        }

        var pathExists = await _context.Paths.AnyAsync(
            entry => entry.Path == sanitizedPath,
            HttpContext.RequestAborted);
        if (!pathExists)
        {
            return NotFound("Gallery path not found.");
        }

        var imageDirectory = ResolveImageDirectory("/img_src", sanitizedPath, allowRoot: true);
        if (!Directory.Exists(imageDirectory))
        {
            throw new DirectoryNotFoundException($"Gallery directory does not exist: {imageDirectory}");
        }

        const string thumbnailDirectory = "/img_db_thumbs";
        Directory.CreateDirectory(thumbnailDirectory);
        var uploaded = new List<string>();
        var errors = new List<string>();
        foreach (var file in files)
        {
            HttpContext.RequestAborted.ThrowIfCancellationRequested();
            var fileName = System.IO.Path.GetFileName((file.FileName ?? string.Empty).Replace('\\', '/'));
            if (!IsValidUploadFileName(fileName))
            {
                errors.Add($"{file.FileName}: invalid filename.");
                continue;
            }

            var extension = System.IO.Path.GetExtension(fileName);
            if (!SupportedUploadExtensions.Contains(extension))
            {
                errors.Add($"{fileName}: only JPEG, PNG, and GIF images are supported.");
                continue;
            }
            if (file.Length <= 0 || file.Length > MaximumUploadFileBytes)
            {
                errors.Add($"{fileName}: file must be between 1 byte and 50 MB.");
                continue;
            }
            if (await _context.Items.AnyAsync(
                item => item.ParentPath == sanitizedPath && item.OriginalName == fileName,
                HttpContext.RequestAborted))
            {
                errors.Add($"{fileName}: a file with that name already exists in this gallery.");
                continue;
            }

            var sourcePath = System.IO.Path.Combine(imageDirectory, fileName);
            if (System.IO.File.Exists(sourcePath))
            {
                errors.Add($"{fileName}: a file with that name already exists on disk.");
                continue;
            }

            var thumbnailKey = CreateThumbnailKey(sanitizedPath, fileName);
            var thumbnailPath = System.IO.Path.Combine(thumbnailDirectory, $"{thumbnailKey}.png");
            var temporarySourcePath = System.IO.Path.Combine(imageDirectory, $".{Guid.NewGuid():N}.upload");
            var sourceCreated = false;
            var thumbnailCreated = false;
            var uploadSucceeded = false;
            try
            {
                await using (var destination = new FileStream(
                    temporarySourcePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true))
                {
                    await file.CopyToAsync(destination, HttpContext.RequestAborted);
                }

                System.IO.File.Move(temporarySourcePath, sourcePath);
                sourceCreated = true;
                await GenerateThumbnailAsync(
                    sourcePath,
                    thumbnailKey,
                    HttpContext.RequestAborted);
                thumbnailCreated = true;

                var now = DateTime.Now;
                var item = new Item
                {
                    OriginalName = fileName,
                    ParentPath = sanitizedPath,
                    ThumbnailKey = thumbnailKey,
                    Uploaded = now,
                    Created = GetCreatedDate(fileName, sanitizedPath, now),
                    Type = "image",
                    Metadata = "{}"
                };
                _context.Items.Add(item);

                var dateTags = DatePathTags.ForPath(sanitizedPath);
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
                uploaded.Add(fileName);
            }
            catch (InvalidImageUploadException error)
            {
                errors.Add($"{fileName}: {error.Message}");
            }
            catch (HttpRequestException error)
            {
                errors.Add($"{fileName}: thumbnail service failed: {error.Message}");
            }
            finally
            {
                if (System.IO.File.Exists(temporarySourcePath))
                {
                    System.IO.File.Delete(temporarySourcePath);
                }
                if (!uploadSucceeded)
                {
                    if (sourceCreated && System.IO.File.Exists(sourcePath))
                    {
                        System.IO.File.Delete(sourcePath);
                    }
                    if (thumbnailCreated && System.IO.File.Exists(thumbnailPath))
                    {
                        System.IO.File.Delete(thumbnailPath);
                    }
                    _context.ChangeTracker.Clear();
                }
            }
        }

        return new JsonResult(new { uploaded, errors });
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
        string sourcePath,
        string thumbnailKey,
        CancellationToken cancellationToken)
    {
        await using var source = System.IO.File.OpenRead(sourcePath);
        using var content = new StreamContent(source);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await _thumbnailClient.PostAsync(
            $"thumbnail/{thumbnailKey}",
            content,
            cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or
            System.Net.HttpStatusCode.RequestEntityTooLarge)
        {
            throw new InvalidImageUploadException(
                await response.Content.ReadAsStringAsync(cancellationToken));
        }

        response.EnsureSuccessStatusCode();
    }

    private sealed class InvalidImageUploadException(string message) : Exception(message)
    {
    }

    private static DateTime GetCreatedDate(string fileName, string path, DateTime fallback)
    {
        var filenameDate = System.Text.RegularExpressions.Regex.Match(
            fileName,
            @"^(\d{4})-?(\d{2})(\d{2})");
        if (filenameDate.Success &&
            int.TryParse(filenameDate.Groups[1].Value, out var fileYear) &&
            int.TryParse(filenameDate.Groups[2].Value, out var fileMonth) &&
            int.TryParse(filenameDate.Groups[3].Value, out var fileDay) &&
            fileYear >= 2000 &&
            fileYear <= fallback.Year &&
            fileMonth is >= 1 and <= 12 &&
            fileDay is >= 1 and <= 31 &&
            DateTime.TryParseExact(
                $"{fileYear:D4}-{fileMonth:D2}-{fileDay:D2}",
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedFilenameDate))
        {
            return parsedFilenameDate;
        }

        var monthSegment = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(segment =>
                System.Text.RegularExpressions.Regex.IsMatch(segment, @"^\d{4}-\d{2}$"));
        if (monthSegment is not null &&
            DateTime.TryParseExact(
                $"{monthSegment}-01",
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedMonthDate) &&
            parsedMonthDate.Year >= 2000 &&
            parsedMonthDate.Year <= fallback.Year)
        {
            return parsedMonthDate;
        }

        var yearSegment = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(segment =>
                System.Text.RegularExpressions.Regex.IsMatch(segment, @"^\d{4}$"));
        if (yearSegment is not null &&
            int.TryParse(yearSegment, out var pathYear) &&
            pathYear >= 2000 &&
            pathYear <= fallback.Year)
        {
            return new DateTime(pathYear, 1, 1);
        }

        return new DateTime(2000, 1, 1);
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

    private static string ResolveImageDirectory(string imageRoot, string path, bool allowRoot)
    {
        var fullRoot = System.IO.Path.GetFullPath(imageRoot);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Image directory does not exist: {fullRoot}");
        }
        if ((System.IO.File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The image directory cannot be a linked directory.");
        }

        var rootPrefix = fullRoot.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + System.IO.Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(path, "root", StringComparison.OrdinalIgnoreCase))
        {
            if (allowRoot)
            {
                return fullRoot;
            }

            throw new InvalidOperationException("The root path cannot be modified.");
        }

        var segments = path.Split('/');
        if (segments.Length == 0 ||
            segments.Any(segment =>
                segment.Length == 0 ||
                segment is "." or ".." ||
                segment.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_')))
        {
            throw new InvalidOperationException($"Invalid image path: {path}");
        }

        var relativePath = string.Join(System.IO.Path.DirectorySeparatorChar, segments);
        var directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, relativePath));
        if (!directory.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidOperationException($"Path entry resolves outside the image directory: {path}");
        }

        var current = fullRoot;
        foreach (var segment in segments)
        {
            current = System.IO.Path.Combine(current, segment);
            if (Directory.Exists(current) &&
                (System.IO.File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"Path entry contains a linked directory: {path}");
            }
        }

        return directory;
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
