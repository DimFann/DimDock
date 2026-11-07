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
        var isAdminEnabled = _configuration.GetValue<bool?>("Features:AdminEnabled") ?? true;
        if (!isAdminEnabled)
        {
            return NotFound();
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
            var manifestPath = Path.Combine(directory, ".dimdock.json");

            if (directoryTags.Length == 0 && itemTags.Count == 0)
            {
                if (System.IO.File.Exists(manifestPath))
                {
                    System.IO.File.Delete(manifestPath);
                    manifestsDeleted++;
                }
                continue;
            }

            var manifest = JsonSerializer.Serialize(
                new { version = 1, tags = directoryTags, items = itemTags },
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
            unusedTagsRemoved = unusedTags.Count
        });
    }

    private bool IsAdminEnabled() =>
        _configuration.GetValue<bool?>("Features:AdminEnabled") ?? true;

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
