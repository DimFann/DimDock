using System.Text.Json;

namespace Website.Services;

public sealed class AdminOnlyTagsProvider(IWebHostEnvironment environment)
{
    private readonly string _tagFilePath = Path.Combine(environment.ContentRootPath, "admin", "admin-only-tags.json");

    public async Task<HashSet<string>> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(_tagFilePath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("admin-only-tags", out var tagsElement) ||
            tagsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Admin-only tag configuration must contain an 'admin-only-tags' array: {_tagFilePath}");
        }

        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tagElement in tagsElement.EnumerateArray())
        {
            if (tagElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(tagElement.GetString()))
            {
                throw new InvalidDataException($"Admin-only tag configuration contains an invalid tag: {_tagFilePath}");
            }

            tags.Add(tagElement.GetString()!.Trim());
        }

        return tags;
    }
}
