using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Website.Data;
using Website.Models;
using Website.Services;

namespace Website.Pages;

public class SearchModel : PageModel
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly AdminOnlyTagsProvider _adminOnlyTagsProvider;

    public SearchModel(
        AppDbContext context,
        IConfiguration configuration,
        AdminOnlyTagsProvider adminOnlyTagsProvider)
    {
        _context = context;
        _configuration = configuration;
        _adminOnlyTagsProvider = adminOnlyTagsProvider;
    }

    [BindProperty(SupportsGet = true, Name = "tag")]
    public string? Tag { get; set; }

    public List<Item> Results { get; private set; } = new();
    public List<PathEntry> DirectoryResults { get; private set; } = new();
    public string? Message { get; private set; }
    public bool AdminEnabled => _configuration.GetValue<bool?>("Features:AdminEnabled") ?? true;
    public HashSet<string> AdminOnlyTags { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        AdminOnlyTags = await _adminOnlyTagsProvider.GetTagsAsync(cancellationToken);
        Tag = Tag?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(Tag))
        {
            Tag = null;
            return;
        }

        if (!AdminEnabled && AdminOnlyTags.Contains(Tag))
        {
            Message = $"No results found for tag '{Tag}'.";
            return;
        }

        var hiddenPaths = AdminEnabled
            ? []
            : await _context.Paths
                .Where(path => path.TagLinks.Any(link => AdminOnlyTags.Contains(link.Tag.Name)))
                .Select(path => path.Path)
                .ToListAsync(cancellationToken);

        DirectoryResults = await _context.Paths
            .Include(path => path.TagLinks)
            .ThenInclude(link => link.Tag)
            .Where(path => path.TagLinks.Any(link => link.Tag.Name == Tag))
            .OrderBy(path => path.Path)
            .ToListAsync(cancellationToken);

        Results = await _context.Items
            .Include(item => item.TagLinks)
            .ThenInclude(link => link.Tag)
            .Where(item => item.TagLinks.Any(link => link.Tag.Name == Tag))
            .OrderBy(item => item.DisplayIndex)
            .ThenBy(item => item.OriginalName)
            .ToListAsync(cancellationToken);

        if (!AdminEnabled)
        {
            DirectoryResults = DirectoryResults
                .Where(path =>
                    !path.TagLinks.Any(link => AdminOnlyTags.Contains(link.Tag.Name)) &&
                    !hiddenPaths.Any(hidden => IsSameOrChildPath(path.Path, hidden)))
                .ToList();
            Results = Results
                .Where(item =>
                    !item.TagLinks.Any(link => AdminOnlyTags.Contains(link.Tag.Name)) &&
                    !hiddenPaths.Any(hidden => IsSameOrChildPath(item.ParentPath ?? string.Empty, hidden)))
                .ToList();
        }

        if (Results.Count == 0 && DirectoryResults.Count == 0)
        {
            Message = $"No results found for tag '{Tag}'.";
        }
    }

    public IReadOnlyCollection<string> GetItemTags(Item item) =>
        item.TagLinks.Select(link => link.Tag.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(tag => AdminEnabled || !AdminOnlyTags.Contains(tag))
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyCollection<string> GetPathTags(PathEntry path) =>
        path.TagLinks.Select(link => link.Tag.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(tag => AdminEnabled || !AdminOnlyTags.Contains(tag))
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsSameOrChildPath(string candidate, string parent) =>
        string.Equals(parent, "root", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
}
