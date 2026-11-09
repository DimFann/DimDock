using System.Text.RegularExpressions;
using Markdig;

namespace Website.Services;

public static class BlogMarkdownRenderer
{
    internal static readonly MarkdownPipeline Pipeline =
        SafeMarkdownRenderer.Pipeline;
    private static readonly Regex ScaledImagePattern = new(
        "(?<image><img\\b[^>]*?)\\s+title=\"scale=(?<scale>25|50|75|100)%\"(?<ending>\\s*/?>)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static string Render(string? markdown)
    {
        var html = Markdown.ToHtml(markdown ?? string.Empty, Pipeline);
        return ScaledImagePattern.Replace(
            html,
            match => $"{match.Groups["image"].Value} style=\"width: {match.Groups["scale"].Value}%; height: auto\"{match.Groups["ending"].Value}");
    }
}
