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
    public string TagsInput { get; set; } = string.Empty;
    public string? TagError { get; private set; }
    public bool AdminEnabled => _configuration.GetValue<bool?>("Features:AdminEnabled") ?? true;
    public HashSet<string> AdminOnlyTags { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public IEnumerable<string> Tags =>
        Item.TagLinks.Select(link => link.Tag.Name)
            .Where(tag => AdminEnabled || !AdminOnlyTags.Contains(tag))
            .OrderBy(name => name, StringComparer.Ordinal);

    public async Task<IActionResult> OnGetAsync(ulong id)
    {
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

        TagsInput = string.Join(", ", Tags);
        return Page();
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
