namespace Website.Services;

public static class TagNameNormalizer
{
    public static bool TryNormalize(string? value, out List<string> tags, out string? error)
    {
        if (value?.Length > 4000)
        {
            tags = [];
            error = "The tag list cannot exceed 4000 characters.";
            return false;
        }

        var normalizedTags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var rawTag in (value ?? string.Empty).Split(','))
        {
            var tag = rawTag.Trim().ToLowerInvariant();
            if (tag.Length == 0)
            {
                continue;
            }

            if (tag.Length > 100 || tag.Any(char.IsControl))
            {
                tags = [];
                error = "Tags must be 100 characters or fewer and cannot contain control characters.";
                return false;
            }

            normalizedTags.Add(tag);
        }

        tags = normalizedTags.ToList();
        error = null;
        return true;
    }
}
