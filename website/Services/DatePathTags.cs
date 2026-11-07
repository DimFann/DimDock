using System.Text.RegularExpressions;

namespace Website.Services;

public static partial class DatePathTags
{
    [GeneratedRegex(@"^(?<year>\d{4})(?:-(?<month>\d{2}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex DateSegmentRegex();

    public static IReadOnlyCollection<string> ForPath(string? path)
    {
        var tags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var segment in (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = DateSegmentRegex().Match(segment);
            if (!match.Success)
            {
                continue;
            }

            if (match.Groups["month"].Success &&
                (!int.TryParse(match.Groups["month"].Value, out var month) || month is < 1 or > 12))
            {
                continue;
            }

            tags.Add(segment);
            if (match.Groups["month"].Success)
            {
                tags.Add(match.Groups["year"].Value);
            }
        }

        return tags;
    }
}
