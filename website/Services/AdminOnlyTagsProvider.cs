using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Website.Data;

namespace Website.Services;

public sealed class AdminOnlyTagsProvider(AppDbContext context, IFileStore fileStore)
{
    public async Task<HashSet<string>> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await context.AdminOnlyTagSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(settings => settings.Id == 1, cancellationToken);
        if (settings is not null)
        {
            var configuredTags = JsonSerializer.Deserialize<string[]>(settings.TagsJson)
                ?? throw new InvalidDataException("Admin-only tags stored in the database must be a JSON array.");
            return ValidateTags(configuredTags);
        }

        await using var stream = await fileStore.OpenReadAsync(
            FileStoreArea.Admin,
            "admin-only-tags.json",
            cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("admin-only-tags", out var tagsElement) ||
            tagsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Admin-only tag configuration must contain an 'admin-only-tags' array.");
        }

        var configuredFileTags = new List<string>();
        foreach (var tagElement in tagsElement.EnumerateArray())
        {
            if (tagElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(tagElement.GetString()))
            {
                throw new InvalidDataException("Admin-only tag configuration contains an invalid tag.");
            }

            configuredFileTags.Add(tagElement.GetString()!);
        }

        return ValidateTags(configuredFileTags);
    }

    private static HashSet<string> ValidateTags(IEnumerable<string> configuredTags)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in configuredTags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                throw new InvalidDataException("Admin-only tag configuration contains an invalid tag.");
            }

            tags.Add(tag.Trim());
        }

        return tags;
    }
}
