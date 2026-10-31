using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using Website.Data;
using Website.Models;

[IgnoreAntiforgeryToken]
public class SearchModel : PageModel
{
    private readonly AppDbContext _context;
    private readonly ILogger<SearchModel> _logger;

    public SearchModel(AppDbContext context, ILogger<SearchModel> logger)
    {
        _context = context;
        _logger = logger;
    }

    [BindProperty]
    public string? InputPath { get; set; }

    public List<Item> Results { get; set; } = new();
    public string? Message { get; set; }

    public void OnPost()
    {
        if (string.IsNullOrWhiteSpace(InputPath))
        {
            Message = "Please enter a valid path.";
            return;
        }

        var sanitized = SanitizePath(InputPath).TrimStart('/');
        _logger.LogInformation("Sanitized path: {Path}", sanitized);

        Results = _context.Items
            .Where(i => i.ParentPath == sanitized)
            .OrderBy(i => i.DisplayIndex)
            .ToList();

        if (!Results.Any())
        {
            Message = $"No items found for path '{sanitized}'.";
        }
    }

    public string SanitizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

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
