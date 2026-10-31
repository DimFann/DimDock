using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Website.Data;
using Website.Models;

[IgnoreAntiforgeryToken]
public class GalleryModel : PageModel
{
    private readonly AppDbContext _context;
    private readonly ILogger<GalleryModel> _logger;

    public GalleryModel(AppDbContext context, ILogger<GalleryModel> logger)
    {
        _context = context;
        _logger = logger;
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

    public async Task<IActionResult> OnGetAsync(string? path)
    {
        var input = string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(Path)
            ? "root"
            : path ?? Path ?? "root";

        var sanitized = SanitizePath(input).TrimStart('/');
        _logger.LogInformation("Gallery path: {Path}", sanitized);

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

        _logger.LogInformation($"Path: {sanitized}\nItems: {Items.Count}\nPaths: {Paths.Count}\nShortcuts: {Shortcuts.Count}\n{string.Join('\n',Shortcuts)}");

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
}
