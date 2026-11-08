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

[IgnoreAntiforgeryToken]
public class GalleryModel : PageModel
{
    private static readonly MarkdownPipeline DescriptionMarkdownPipeline =
        new MarkdownPipelineBuilder().DisableHtml().Build();

    private readonly AppDbContext _context;
    private readonly ILogger<GalleryModel> _logger;
    private readonly IConfiguration _configuration;
    private readonly IAntiforgery _antiforgery;
    private readonly AdminOnlyTagsProvider _adminOnlyTagsProvider;

    public GalleryModel(
        AppDbContext context,
        ILogger<GalleryModel> logger,
        IConfiguration configuration,
        IAntiforgery antiforgery,
        AdminOnlyTagsProvider adminOnlyTagsProvider)
    {
        _context = context;
        _logger = logger;
        _configuration = configuration;
        _antiforgery = antiforgery;
        _adminOnlyTagsProvider = adminOnlyTagsProvider;
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
    public bool AdminEnabled => _configuration.GetValue<bool?>("Features:AdminEnabled") ?? true;
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
