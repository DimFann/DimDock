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

[IgnoreAntiforgeryToken]
public class GalleryModel : PageModel
{
    private static readonly MarkdownPipeline DescriptionMarkdownPipeline =
        new MarkdownPipelineBuilder().DisableHtml().Build();

    private readonly AppDbContext _context;
    private readonly ILogger<GalleryModel> _logger;
    private readonly IConfiguration _configuration;
    private readonly IAntiforgery _antiforgery;

    public GalleryModel(
        AppDbContext context,
        ILogger<GalleryModel> logger,
        IConfiguration configuration,
        IAntiforgery antiforgery)
    {
        _context = context;
        _logger = logger;
        _configuration = configuration;
        _antiforgery = antiforgery;
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

    public async Task<IActionResult> OnGetAsync(string? path)
    {
        var input = string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(Path)
            ? "root"
            : path ?? Path ?? "root";

        var sanitized = SanitizePath(input).TrimStart('/');
        _logger.LogDebug("Gallery path: {Path}", sanitized);

        CurrentPath = sanitized;

        Items = await _context.Items
            .Where(i => i.ParentPath == sanitized)
            .ToListAsync();

        Paths = await _context.Paths
            .Where(p => p.ParentPath == sanitized && p.Path != p.ParentPath)
            .OrderBy(p => p.Path)
            .ToListAsync();

        Shortcuts = await _context.Shortcuts
            .Include(s => s.OriginalItem)
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
                    Uploaded = shortcut.OriginalItem.Uploaded
                };
                Items.Add(clone);
            }
        }

        // Apply sorting rules
        Items = Items
            .OrderBy(i => i.DisplayIndex)
            .ThenBy(i => i.OriginalName)
            .ThenBy(i => i.Created)
            .ThenBy(i => i.Uploaded)
            .ToList();

        PathEntry = await _context.Paths
            .FirstOrDefaultAsync(p => p.Path == sanitized);

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
