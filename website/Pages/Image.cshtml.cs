using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Website.Data;
using Website.Models;
using Website.Services;

namespace Website.Pages;

public class ImageModel : PageModel
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IAntiforgery _antiforgery;
    private readonly AdminOnlyTagsProvider _adminOnlyTagsProvider;

    public ImageModel(
        AppDbContext context,
        IConfiguration configuration,
        IAntiforgery antiforgery,
        AdminOnlyTagsProvider adminOnlyTagsProvider)
    {
        _context = context;
        _configuration = configuration;
        _antiforgery = antiforgery;
        _adminOnlyTagsProvider = adminOnlyTagsProvider;
    }

    public Item Item { get; private set; } = null!;
    public string SourceUrl { get; private set; } = string.Empty;
    public string GalleryUrl { get; private set; } = "/Gallery";
    public ulong? CurrentShortcutId { get; private set; }
    public string TagsInput { get; set; } = string.Empty;
    public string? TagError { get; private set; }
    public bool AdminEnabled => _configuration.GetValue<bool?>("Features:AdminEnabled") ?? true;
    public HashSet<string> AdminOnlyTags { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public IEnumerable<string> Tags =>
        Item.TagLinks.Select(link => link.Tag.Name)
            .Where(tag => AdminEnabled || !AdminOnlyTags.Contains(tag))
            .OrderBy(name => name, StringComparer.Ordinal);

    public async Task<IActionResult> OnGetAsync(ulong id, ulong? shortcutId)
    {
        if (!await LoadItemAsync(id))
        {
            return NotFound();
        }
        if (shortcutId.HasValue)
        {
            CurrentShortcutId = await _context.Shortcuts
                .Where(shortcut => shortcut.Id == shortcutId.Value && shortcut.OriginalId == id)
                .Select(shortcut => (ulong?)shortcut.Id)
                .FirstOrDefaultAsync(HttpContext.RequestAborted);
        }
        AdminOnlyTags = await _adminOnlyTagsProvider.GetTagsAsync(HttpContext.RequestAborted);
        if (!AdminEnabled &&
            (Item.TagLinks.Any(link => AdminOnlyTags.Contains(link.Tag.Name)) ||
             await IsInAdminOnlyPathAsync(Item.ParentPath)))
        {
            return NotFound();
        }

        TagsInput = string.Join(", ", Tags);
        return Page();
    }

    public async Task<IActionResult> OnGetSearchPathsAsync(ulong id, [FromQuery] string? query)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        var sourceItem = await _context.Items
            .Where(item => item.Id == id)
            .Select(item => new { item.ParentPath })
            .FirstOrDefaultAsync(HttpContext.RequestAborted);
        if (sourceItem is null)
        {
            return NotFound();
        }
        var sourceParentPath = sourceItem.ParentPath ?? "root";

        var search = query?.Trim();
        if (string.IsNullOrEmpty(search) || search.Length > 256)
        {
            return new JsonResult(Array.Empty<object>());
        }

        var linkedPaths = await _context.Shortcuts
            .Where(shortcut => shortcut.OriginalId == id)
            .Select(shortcut => shortcut.ShortcutPath)
            .ToListAsync(HttpContext.RequestAborted);
        var paths = await _context.Paths
            .Where(path => path.Path != sourceParentPath && path.Path.Contains(search))
            .OrderBy(path => path.Path)
            .Select(path => path.Path)
            .Take(50)
            .ToListAsync(HttpContext.RequestAborted);

        return new JsonResult(paths.Select(path => new
        {
            path,
            displayPath = path == "root" ? "/" : path,
            alreadyLinked = linkedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)
        }));
    }

    public async Task<IActionResult> OnGetShortcutLocationsAsync(ulong id)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await _context.Items.AnyAsync(item => item.Id == id, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        var shortcuts = await _context.Shortcuts
            .Where(shortcut => shortcut.OriginalId == id)
            .OrderBy(shortcut => shortcut.ShortcutPath)
            .Select(shortcut => new
            {
                id = shortcut.Id.ToString(),
                path = shortcut.ShortcutPath,
                displayPath = shortcut.ShortcutPath == "root" ? "/" : shortcut.ShortcutPath
            })
            .ToListAsync(HttpContext.RequestAborted);

        return new JsonResult(shortcuts);
    }

    public async Task<IActionResult> OnPostCreateShortcutAsync(
        ulong id,
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

        var item = await _context.Items.FirstOrDefaultAsync(
            entry => entry.Id == id,
            HttpContext.RequestAborted);
        if (item is null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(targetPath) ||
            targetPath.Length > 1024 ||
            !await _context.Paths.AnyAsync(
                path => path.Path == targetPath,
                HttpContext.RequestAborted))
        {
            return BadRequest("Select an existing directory.");
        }

        var alreadyExists = await _context.Shortcuts.AnyAsync(
            shortcut => shortcut.OriginalId == id && shortcut.ShortcutPath == targetPath,
            HttpContext.RequestAborted);
        if (alreadyExists)
        {
            return BadRequest("This item is already linked to that directory.");
        }

        _context.Shortcuts.Add(new Shortcut
        {
            OriginalId = item.Id,
            OriginalName = item.OriginalName,
            ParentPath = item.ParentPath,
            ShortcutPath = targetPath
        });
        await _context.SaveChangesAsync(HttpContext.RequestAborted);

        return new JsonResult(new { saved = true });
    }

    public async Task<IActionResult> OnPostDeleteShortcutsAsync(
        ulong id,
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

        if (shortcutIds is null || shortcutIds.Count == 0)
        {
            return BadRequest("Select at least one shortcut to remove.");
        }

        var uniqueIds = shortcutIds.Distinct().ToArray();
        var shortcuts = await _context.Shortcuts
            .Where(shortcut => shortcut.OriginalId == id && uniqueIds.Contains(shortcut.Id))
            .ToListAsync(HttpContext.RequestAborted);
        if (shortcuts.Count != uniqueIds.Length)
        {
            return BadRequest("One or more selected shortcuts are no longer available.");
        }

        _context.Shortcuts.RemoveRange(shortcuts);
        await _context.SaveChangesAsync(HttpContext.RequestAborted);

        return new JsonResult(new
        {
            deletedIds = shortcuts.Select(shortcut => shortcut.Id.ToString()).ToArray()
        });
    }

    public async Task<IActionResult> OnPostSaveTagsAsync(ulong id, [FromForm] string? tags)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        if (!await LoadItemAsync(id))
        {
            return NotFound();
        }
        AdminOnlyTags = await _adminOnlyTagsProvider.GetTagsAsync(HttpContext.RequestAborted);
        if (!AdminEnabled &&
            (Item.TagLinks.Any(link => AdminOnlyTags.Contains(link.Tag.Name)) ||
             await IsInAdminOnlyPathAsync(Item.ParentPath)))
        {
            return NotFound();
        }

        TagsInput = tags ?? string.Empty;
        if (!TagNameNormalizer.TryNormalize(TagsInput, out var normalizedTags, out var error))
        {
            TagError = error;
            return Page();
        }
        normalizedTags = normalizedTags
            .Concat(DatePathTags.ForPath(Item.ParentPath))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToList();

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

        var existingLinks = await _context.ItemTags
            .Where(link => link.ItemId == Item.Id)
            .ToListAsync();
        _context.ItemTags.RemoveRange(existingLinks);
        _context.ItemTags.AddRange(tagEntities.Select(tag => new ItemTag
        {
            ItemId = Item.Id,
            Tag = tag
        }));
        await _context.SaveChangesAsync();

        return RedirectToPage("/Image", new { id });
    }

    private async Task<bool> LoadItemAsync(ulong id)
    {
        var item = await _context.Items
            .Include(entry => entry.TagLinks)
            .ThenInclude(link => link.Tag)
            .FirstOrDefaultAsync(entry => entry.Id == id);
        if (item is null)
        {
            return false;
        }

        Item = item;
        var segments = (item.ParentPath ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Append(item.OriginalName)
            .Select(Uri.EscapeDataString);
        SourceUrl = "/img_src/" + string.Join('/', segments);
        var gallerySegments = (item.ParentPath ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString)
            .ToArray();
        GalleryUrl = gallerySegments.Length == 0
            ? "/Gallery"
            : "/Gallery/" + string.Join('/', gallerySegments);
        return true;
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

    private async Task<bool> IsInAdminOnlyPathAsync(string? parentPath)
    {
        var hiddenPaths = await _context.Paths
            .Where(path => path.TagLinks.Any(link => AdminOnlyTags.Contains(link.Tag.Name)))
            .Select(path => path.Path)
            .ToListAsync(HttpContext.RequestAborted);

        return hiddenPaths.Any(path =>
            path == "root" ||
            string.Equals(path, parentPath, StringComparison.OrdinalIgnoreCase) ||
            parentPath?.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase) == true);
    }
}
