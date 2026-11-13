using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Website.Data;
using Website.Models;

namespace Website.Services;

public sealed class GalleryReloadException(string message) : Exception(message);

/// <summary>
/// Browser-initiated equivalent of the path loader's gallery scan, limited to one path and its
/// subdirectories. Like the loader it is additive: new directories and files are indexed, manifest
/// types/tags/descriptions are applied and missing thumbnails are generated; nothing is deleted.
/// </summary>
public sealed class GalleryPathReloader(
    AppDbContext context,
    IFileStore fileStore,
    IHttpClientFactory httpClientFactory,
    ILogger<GalleryPathReloader> logger)
{
    private const string ManifestFileName = ".dimdock.json";
    private const string DescriptionFileName = ".description.md";
    private const string LegacyDescriptionFileName = "description.md";
    private const string ShortcutsFileName = ".shortcuts.json";
    private const string LegacyShortcutsFileName = "shortcuts.json";
    private const int TagNameMaxLength = 100;
    private const int ThumbnailParallelism = 4;
    private const int ThumbnailExistenceCheckLimit = 500;
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif" };

    private sealed record ManifestItem(IReadOnlyList<string> Tags, bool HasDescription, string? Description);

    private sealed record Manifest(
        string? Type,
        IReadOnlyList<string> Tags,
        IReadOnlyDictionary<string, ManifestItem> Items)
    {
        public static readonly Manifest Empty = new(null, [], new Dictionary<string, ManifestItem>());
    }

    private sealed record ScannedDirectory(
        string Key,
        string Path,
        string ParentPath,
        string? Type,
        IReadOnlySet<string> Tags,
        string? Description,
        Manifest Manifest,
        IReadOnlyList<string> Files);

    public async Task<GalleryOperationResult> ReloadAsync(string path, CancellationToken cancellationToken = default)
    {
        var entry = await context.Paths.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Path == path, cancellationToken)
            ?? throw new GalleryReloadException("The path no longer exists.");
        path = entry.Path;
        var isRoot = string.Equals(path, "root", StringComparison.OrdinalIgnoreCase);
        var rootKey = isRoot ? string.Empty : path;
        if (!isRoot && !await fileStore.DirectoryExistsAsync(FileStoreArea.Gallery, rootKey, cancellationToken))
        {
            throw new GalleryReloadException($"The directory {path} does not exist in storage.");
        }

        var (pendingPaths, pendingItems) = await PendingDeletionsAsync(cancellationToken);
        var directories = await ScanStorageAsync(rootKey, pendingPaths, cancellationToken);
        var fileCount = directories.Sum(directory => directory.Files.Count);

        var strategy = context.Database.CreateExecutionStrategy();
        var outcome = await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var result = await ApplyAsync(path, isRoot, directories, pendingItems, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
        context.ChangeTracker.Clear();

        var (generated, failed) = await GenerateMissingThumbnailsAsync(outcome.ThumbnailCandidates, cancellationToken);

        var parts = new List<string>
        {
            $"Scanned {Plural(directories.Count, "directory", "directories")} and {Plural(fileCount, "file")}"
        };
        if (outcome.PathsAdded > 0 || outcome.ItemsAdded > 0)
        {
            parts.Add($"added {Plural(outcome.PathsAdded, "path")} and {Plural(outcome.ItemsAdded, "item")}");
        }
        if (outcome.EntriesUpdated > 0)
        {
            parts.Add($"updated {Plural(outcome.EntriesUpdated, "entry", "entries")}");
        }
        if (generated > 0 || failed > 0)
        {
            parts.Add(failed > 0
                ? $"generated {Plural(generated, "thumbnail")} ({failed} failed; see the server log)"
                : $"generated {Plural(generated, "thumbnail")}");
        }
        var message = string.Join(", ", parts) + ".";
        if (outcome.PathsAdded == 0 && outcome.ItemsAdded == 0 && outcome.EntriesUpdated == 0 && generated == 0 && failed == 0)
        {
            message += " Everything was already up to date.";
        }
        if (outcome.MissingItems > 0 || outcome.MissingPaths > 0)
        {
            message += $" {Plural(outcome.MissingPaths, "indexed path")} and {Plural(outcome.MissingItems, "indexed item")} " +
                "were not found in storage and were left unchanged.";
        }
        if (outcome.OrphanManifestEntries > 0)
        {
            message += $" {Plural(outcome.OrphanManifestEntries, "manifest entry", "manifest entries")} had no matching file.";
        }
        logger.LogInformation("Reloaded gallery path {Path}: {Summary}", path, message);
        return new GalleryOperationResult(path, message);
    }

    private sealed record ApplyOutcome(
        int PathsAdded,
        int ItemsAdded,
        int EntriesUpdated,
        int MissingPaths,
        int MissingItems,
        int OrphanManifestEntries,
        IReadOnlyList<(string SourceKey, string ThumbnailKey, ulong? ExistingItemId)> ThumbnailCandidates);

    private async Task<ApplyOutcome> ApplyAsync(
        string path,
        bool isRoot,
        IReadOnlyList<ScannedDirectory> directories,
        IReadOnlySet<(string ParentPath, string Name)> pendingItems,
        CancellationToken cancellationToken)
    {
        var prefix = path + "/";
        var paths = await context.Paths
            .Include(candidate => candidate.TagLinks).ThenInclude(link => link.Tag)
            .Where(candidate => isRoot || candidate.Path == path || candidate.Path.StartsWith(prefix))
            .ToListAsync(cancellationToken);
        var pathsByName = paths.ToDictionary(candidate => candidate.Path, StringComparer.Ordinal);
        var items = await context.Items
            .Include(item => item.TagLinks).ThenInclude(link => link.Tag)
            .Where(item => isRoot || item.ParentPath == path ||
                (item.ParentPath != null && item.ParentPath.StartsWith(prefix)))
            .ToListAsync(cancellationToken);
        var itemsByPath = items
            .GroupBy(item => item.ParentPath ?? "root", StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.GroupBy(item => item.OriginalName, StringComparer.Ordinal)
                    .ToDictionary(named => named.Key, named => named.First(), StringComparer.Ordinal),
                StringComparer.Ordinal);

        var tags = await EnsureTagsAsync(directories, cancellationToken);
        var changed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var added = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pathsAdded = 0;
        var itemsAdded = 0;
        var orphanManifestEntries = 0;
        var thumbnailCandidates = new List<(string, string, ulong?)>();
        var now = DateTime.Now;

        foreach (var directory in directories)
        {
            if (!pathsByName.TryGetValue(directory.Path, out var pathEntry))
            {
                pathEntry = new PathEntry
                {
                    Path = directory.Path,
                    ParentPath = directory.ParentPath,
                    Description = directory.Description,
                    Metadata = "{}"
                };
                context.Paths.Add(pathEntry);
                pathsByName[directory.Path] = pathEntry;
                added.Add(pathEntry);
                pathsAdded++;
            }
            if (!string.Equals(pathEntry.Type, directory.Type, StringComparison.Ordinal))
            {
                pathEntry.Type = directory.Type;
                changed.Add(pathEntry);
            }
            if (SyncTags(pathEntry.TagLinks, directory.Tags, link => link.Tag.Name,
                    tag => new PathTag { Path = pathEntry, Tag = tag }, tags))
            {
                changed.Add(pathEntry);
            }

            if (!itemsByPath.TryGetValue(directory.Path, out var directoryItems))
            {
                directoryItems = new Dictionary<string, Item>(StringComparer.Ordinal);
                itemsByPath[directory.Path] = directoryItems;
            }
            foreach (var name in directory.Files)
            {
                if (pendingItems.Contains((directory.Path, name)))
                {
                    continue;
                }
                var type = ImageExtensions.Contains(System.IO.Path.GetExtension(name)) ? "image" : "file";
                var thumbnailKey = CreateThumbnailKey(directory.Path, name);
                if (!directoryItems.TryGetValue(name, out var item))
                {
                    item = new Item
                    {
                        OriginalName = name,
                        ParentPath = directory.Path,
                        ThumbnailKey = thumbnailKey,
                        Uploaded = now,
                        Created = GalleryItemDates.Created(name, directory.Path, now),
                        Type = type,
                        Metadata = "{}"
                    };
                    context.Items.Add(item);
                    directoryItems[name] = item;
                    added.Add(item);
                    itemsAdded++;
                }
                else
                {
                    if (!string.Equals(item.Type, type, StringComparison.Ordinal))
                    {
                        item.Type = type;
                        changed.Add(item);
                    }
                    if (!string.Equals(item.ThumbnailKey, thumbnailKey, StringComparison.Ordinal))
                    {
                        item.ThumbnailKey = thumbnailKey;
                        changed.Add(item);
                    }
                }
                if (type == "image")
                {
                    thumbnailCandidates.Add((
                        JoinKey(directory.Key, name),
                        thumbnailKey,
                        item.Id == 0 ? null : item.Id));
                }
            }

            var dateTags = DatePathTags.ForPath(directory.Path);
            foreach (var (name, item) in directoryItems)
            {
                directory.Manifest.Items.TryGetValue(name, out var manifestItem);
                var desired = (manifestItem?.Tags ?? []).Concat(dateTags).ToHashSet(StringComparer.Ordinal);
                if (SyncTags(item.TagLinks, desired, link => link.Tag.Name,
                        tag => new ItemTag { Item = item, Tag = tag }, tags))
                {
                    changed.Add(item);
                }
                if (manifestItem is { HasDescription: true } &&
                    !string.Equals(item.Description, manifestItem.Description, StringComparison.Ordinal))
                {
                    item.Description = manifestItem.Description;
                    changed.Add(item);
                }
            }
            foreach (var name in directory.Manifest.Items.Keys.Where(name => !directoryItems.ContainsKey(name)))
            {
                logger.LogWarning("Tag manifest entry has no indexed item in {Path}: {Name}", directory.Path, name);
                orphanManifestEntries++;
            }
        }

        var scannedPaths = directories.Select(directory => directory.Path).ToHashSet(StringComparer.Ordinal);
        var scannedFiles = directories
            .SelectMany(directory => directory.Files.Select(name => (directory.Path, name)))
            .ToHashSet();
        var missingPaths = paths.Count(candidate => !scannedPaths.Contains(candidate.Path));
        var missingItems = items.Count(item => !scannedFiles.Contains((item.ParentPath ?? "root", item.OriginalName)));

        await context.SaveChangesAsync(cancellationToken);
        return new ApplyOutcome(
            pathsAdded, itemsAdded, changed.Count(entity => !added.Contains(entity)),
            missingPaths, missingItems, orphanManifestEntries, thumbnailCandidates);
    }

    private static bool SyncTags<TLink>(
        ICollection<TLink> links,
        IReadOnlySet<string> desired,
        Func<TLink, string> linkName,
        Func<Tag, TLink> createLink,
        IReadOnlyDictionary<string, Tag> tags)
    {
        // Compare using the stored tag spelling so case-insensitive collation matches don't churn links.
        desired = desired.Select(name => tags[name].Name).ToHashSet(StringComparer.Ordinal);
        var current = links.Select(linkName).ToHashSet(StringComparer.Ordinal);
        if (current.SetEquals(desired))
        {
            return false;
        }
        foreach (var link in links.Where(link => !desired.Contains(linkName(link))).ToList())
        {
            links.Remove(link);
        }
        var byStoredName = tags.Values.DistinctBy(tag => tag.Name, StringComparer.Ordinal)
            .ToDictionary(tag => tag.Name, StringComparer.Ordinal);
        foreach (var name in desired.Where(name => !current.Contains(name)))
        {
            links.Add(createLink(byStoredName[name]));
        }
        return true;
    }

    private async Task<IReadOnlyDictionary<string, Tag>> EnsureTagsAsync(
        IReadOnlyList<ScannedDirectory> directories,
        CancellationToken cancellationToken)
    {
        var names = directories
            .SelectMany(directory => directory.Tags
                .Concat(directory.Manifest.Items.Values.SelectMany(item => item.Tags))
                .Concat(DatePathTags.ForPath(directory.Path)))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var name in names)
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT IGNORE INTO Tags (name) VALUES ({name})",
                cancellationToken);
        }
        var nameList = names.ToList();
        var tags = await context.Tags
            .Where(tag => nameList.Contains(tag.Name))
            .ToListAsync(cancellationToken);
        var byName = new Dictionary<string, Tag>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            byName.TryAdd(tag.Name, tag);
        }
        foreach (var name in names.Where(name => !byName.ContainsKey(name)))
        {
            // Case-insensitive collations can return the stored spelling; reuse it.
            byName[name] = tags.First(tag => string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        return byName;
    }

    private async Task<List<ScannedDirectory>> ScanStorageAsync(
        string rootKey,
        IReadOnlyCollection<string> pendingPaths,
        CancellationToken cancellationToken)
    {
        var directoryKeys = (await fileStore.ListDirectoriesAsync(
                FileStoreArea.Gallery, rootKey, recursive: true, cancellationToken))
            .Prepend(rootKey)
            .Where(key => !IsHidden(rootKey, key) && !pendingPaths.Any(pending =>
                IsSameOrChildPath(PathForKey(key), pending)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key.Length == 0 ? 0 : key.Count(character => character == '/') + 1)
            .ThenBy(key => key, StringComparer.Ordinal)
            .ToList();
        var filesByDirectory = (await fileStore.ListFilesAsync(
                FileStoreArea.Gallery, rootKey, recursive: true, cancellationToken))
            .GroupBy(key => key.Contains('/') ? key[..key.LastIndexOf('/')] : string.Empty, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(key => key[(key.LastIndexOf('/') + 1)..]).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        var directories = new List<ScannedDirectory>();
        foreach (var key in directoryKeys)
        {
            var names = filesByDirectory.GetValueOrDefault(key) ?? new HashSet<string>(StringComparer.Ordinal);
            await MigrateLegacyMetadataAsync(key, names, cancellationToken);
            var path = PathForKey(key);
            var manifest = names.Contains(ManifestFileName)
                ? await ReadManifestAsync(JoinKey(key, ManifestFileName), cancellationToken)
                : Manifest.Empty;
            var directoryName = key[(key.LastIndexOf('/') + 1)..];
            var type = directoryName.StartsWith("comic_", StringComparison.OrdinalIgnoreCase) ? "comic" : manifest.Type;
            var tags = manifest.Tags.ToHashSet(StringComparer.Ordinal);
            if (directoryName.StartsWith("wip_", StringComparison.OrdinalIgnoreCase))
            {
                tags.Add("hidden");
            }
            string? description = null;
            if (names.Contains(DescriptionFileName))
            {
                try
                {
                    description = await fileStore.ReadAllTextAsync(
                        FileStoreArea.Gallery, JoinKey(key, DescriptionFileName), cancellationToken);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
                {
                    logger.LogError(error, "Error reading {File} in {Path}.", DescriptionFileName, path);
                }
            }
            var files = names
                .Where(name => !name.StartsWith('.') &&
                    name is not LegacyDescriptionFileName and not LegacyShortcutsFileName)
                .Order(StringComparer.Ordinal)
                .ToList();
            directories.Add(new ScannedDirectory(
                key,
                path,
                key.Contains('/') ? key[..key.LastIndexOf('/')] : "root",
                type,
                tags,
                description,
                manifest,
                files));
        }
        return directories;
    }

    private async Task MigrateLegacyMetadataAsync(
        string directoryKey,
        HashSet<string> names,
        CancellationToken cancellationToken)
    {
        foreach (var (legacyName, hiddenName) in new[]
                 {
                     (LegacyDescriptionFileName, DescriptionFileName),
                     (LegacyShortcutsFileName, ShortcutsFileName)
                 })
        {
            if (!names.Contains(legacyName))
            {
                continue;
            }
            if (names.Contains(hiddenName))
            {
                logger.LogError(
                    "Both legacy and hidden metadata files exist in {Directory}; keeping {Hidden}.",
                    directoryKey.Length == 0 ? "root" : directoryKey, hiddenName);
                continue;
            }
            await fileStore.MoveFileAsync(
                FileStoreArea.Gallery,
                JoinKey(directoryKey, legacyName),
                JoinKey(directoryKey, hiddenName),
                cancellationToken);
            names.Remove(legacyName);
            names.Add(hiddenName);
        }
    }

    private async Task<Manifest> ReadManifestAsync(string manifestKey, CancellationToken cancellationToken)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(
                await fileStore.ReadAllTextAsync(FileStoreArea.Gallery, manifestKey, cancellationToken));
            root = document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or IOException or DecoderFallbackException)
        {
            throw new GalleryReloadException($"Unable to read tag manifest {manifestKey}: {error.Message}");
        }
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var versionNumber) ||
            versionNumber != 1)
        {
            throw new GalleryReloadException($"Unsupported or missing version in tag manifest {manifestKey}.");
        }

        var directoryTags = NormalizeTags(root, "tags", $"{manifestKey}: tags");
        string? type = null;
        if (root.TryGetProperty("type", out var rawType) && rawType.ValueKind != JsonValueKind.Null)
        {
            if (rawType.ValueKind != JsonValueKind.String)
            {
                throw new GalleryReloadException($"{manifestKey}: type must be a string or null.");
            }
            var value = rawType.GetString()!.Trim().ToLowerInvariant();
            type = value.Length == 0 ? null : value;
            if (type is not null and not "comic")
            {
                throw new GalleryReloadException($"Unsupported directory type in {manifestKey}: '{rawType.GetString()}'.");
            }
        }

        var items = new Dictionary<string, ManifestItem>(StringComparer.Ordinal);
        if (root.TryGetProperty("items", out var rawItems))
        {
            if (rawItems.ValueKind != JsonValueKind.Object)
            {
                throw new GalleryReloadException($"{manifestKey}: items must be an object.");
            }
            foreach (var property in rawItems.EnumerateObject())
            {
                var filename = property.Name;
                if (filename.Length == 0 || filename is "." or ".." || filename.Contains('/') || filename.Contains('\\'))
                {
                    throw new GalleryReloadException($"Invalid item filename in tag manifest {manifestKey}: '{filename}'.");
                }
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    throw new GalleryReloadException($"{manifestKey}: metadata for '{filename}' must be an object.");
                }
                var itemTags = NormalizeTags(property.Value, "tags", $"{manifestKey}: '{filename}' tags");
                var hasDescription = property.Value.TryGetProperty("description", out var description);
                if (hasDescription && description.ValueKind is not JsonValueKind.String and not JsonValueKind.Null)
                {
                    throw new GalleryReloadException(
                        $"{manifestKey}: description for '{filename}' must be a string or null.");
                }
                items[filename] = new ManifestItem(
                    itemTags,
                    hasDescription,
                    hasDescription && description.ValueKind == JsonValueKind.String ? description.GetString() : null);
            }
        }
        return new Manifest(type, directoryTags, items);
    }

    private static List<string> NormalizeTags(JsonElement owner, string propertyName, string source)
    {
        if (!owner.TryGetProperty(propertyName, out var rawTags))
        {
            return [];
        }
        if (rawTags.ValueKind != JsonValueKind.Array)
        {
            throw new GalleryReloadException($"{source} must be a list of tag strings.");
        }
        var tags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var rawTag in rawTags.EnumerateArray())
        {
            if (rawTag.ValueKind != JsonValueKind.String)
            {
                throw new GalleryReloadException($"{source} contains a non-string tag.");
            }
            var tag = rawTag.GetString()!.Trim().ToLowerInvariant();
            if (tag.Length == 0)
            {
                continue;
            }
            if (tag.Length > TagNameMaxLength || tag.Contains(',') || tag.Any(character => character < ' '))
            {
                throw new GalleryReloadException($"Invalid tag in {source}: '{rawTag.GetString()}'.");
            }
            tags.Add(tag);
        }
        return tags.ToList();
    }

    private async Task<(int Generated, int Failed)> GenerateMissingThumbnailsAsync(
        IReadOnlyList<(string SourceKey, string ThumbnailKey, ulong? ExistingItemId)> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return (0, 0);
        }
        HashSet<string>? listed = null;
        if (candidates.Count > ThumbnailExistenceCheckLimit)
        {
            listed = (await fileStore.ListFilesAsync(FileStoreArea.Thumbnails, string.Empty, cancellationToken: cancellationToken))
                .ToHashSet(StringComparer.Ordinal);
        }
        var missing = new ConcurrentBag<(string SourceKey, string ThumbnailKey, ulong? ExistingItemId)>();
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            async (candidate, token) =>
            {
                var key = $"{candidate.ThumbnailKey}.png";
                var exists = listed?.Contains(key) ??
                    await fileStore.FileExistsAsync(FileStoreArea.Thumbnails, key, token);
                if (!exists)
                {
                    missing.Add(candidate);
                }
            });
        if (missing.IsEmpty)
        {
            return (0, 0);
        }

        var client = httpClientFactory.CreateClient("ThumbnailService");
        var generatedForExisting = new ConcurrentBag<ulong>();
        var generated = 0;
        var failed = 0;
        await Parallel.ForEachAsync(
            missing,
            new ParallelOptions { MaxDegreeOfParallelism = ThumbnailParallelism, CancellationToken = cancellationToken },
            async (candidate, token) =>
            {
                try
                {
                    await ThumbnailGenerator.GenerateAsync(
                        fileStore, client, candidate.SourceKey, candidate.ThumbnailKey, token);
                    Interlocked.Increment(ref generated);
                    if (candidate.ExistingItemId is { } id)
                    {
                        generatedForExisting.Add(id);
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Interlocked.Increment(ref failed);
                    logger.LogWarning(error, "Thumbnail generation failed for {Source}.", candidate.SourceKey);
                }
            });

        if (!generatedForExisting.IsEmpty)
        {
            // Bust any cached 404 for thumbnails that were previously missing.
            var ids = generatedForExisting.Distinct().ToList();
            var existingItems = await context.Items.Where(item => ids.Contains(item.Id)).ToListAsync(cancellationToken);
            foreach (var item in existingItems)
            {
                item.RefreshThumbnailVersion();
            }
            await context.SaveChangesAsync(cancellationToken);
            context.ChangeTracker.Clear();
        }
        return (generated, failed);
    }

    private async Task<(IReadOnlyCollection<string> Paths, IReadOnlySet<(string, string)> Items)> PendingDeletionsAsync(
        CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS PendingPathDeletions (
                path VARCHAR(1024) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY
            )
            """,
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS PendingItemDeletions (
                id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
                parent_path VARCHAR(1024) NOT NULL,
                original_name VARCHAR(255) NOT NULL
            )
            """,
            cancellationToken);
        var paths = await context.PendingPathDeletions.AsNoTracking()
            .Select(entry => entry.Path)
            .ToListAsync(cancellationToken);
        var items = (await context.PendingItemDeletions.AsNoTracking()
                .Select(entry => new { entry.ParentPath, entry.OriginalName })
                .ToListAsync(cancellationToken))
            .Select(entry => (entry.ParentPath, entry.OriginalName))
            .ToHashSet();
        return (paths, items);
    }

    private static bool IsHidden(string rootKey, string key)
    {
        var relative = rootKey.Length == 0 ? key : key.Length == rootKey.Length ? string.Empty : key[(rootKey.Length + 1)..];
        return relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment.StartsWith('.'));
    }

    private static bool IsSameOrChildPath(string path, string parent) =>
        string.Equals(path, parent, StringComparison.Ordinal) ||
        path.StartsWith(parent + "/", StringComparison.Ordinal);

    private static string PathForKey(string key) => key.Length == 0 ? "root" : key;

    private static string JoinKey(string directory, string name) =>
        directory.Length == 0 ? name : $"{directory}/{name}";

    private static string CreateThumbnailKey(string parentPath, string originalName)
    {
        var normalizedPath = parentPath == "root" ? originalName : $"{parentPath}/{originalName}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
    }

    private static string Plural(int count, string singular, string? plural = null) =>
        $"{count} {(count == 1 ? singular : plural ?? singular + "s")}";
}
