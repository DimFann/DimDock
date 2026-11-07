using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Website.Data;

namespace Website.Pages;

public class AdminModel : PageModel
{
    private readonly IConfiguration _configuration;
    private readonly AppDbContext _context;
    private readonly IAntiforgery _antiforgery;

    public AdminModel(
        IConfiguration configuration,
        AppDbContext context,
        IAntiforgery antiforgery)
    {
        _configuration = configuration;
        _context = context;
        _antiforgery = antiforgery;
    }

    public IActionResult OnGet()
    {
        var isAdminEnabled = _configuration.GetValue<bool?>("Features:AdminEnabled") ?? true;
        return isAdminEnabled ? Page() : NotFound();
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
            .AsNoTracking()
            .ToListAsync();

        var written = 0;
        var unchanged = 0;
        var deleted = 0;
        var skipped = 0;
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
                continue;
            }

            if (System.IO.File.Exists(descriptionPath) &&
                string.Equals(await System.IO.File.ReadAllTextAsync(descriptionPath), entry.Description, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            await System.IO.File.WriteAllTextAsync(
                descriptionPath,
                entry.Description!,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            written++;
        }

        return new JsonResult(new { written, unchanged, deleted, skipped });
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
