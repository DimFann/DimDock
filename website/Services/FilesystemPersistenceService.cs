using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Website.Data;
using Website.Models;

namespace Website.Services;

public sealed class FilesystemPersistenceService(
    AppDbContext context,
    IWebHostEnvironment environment)
{
    private const string ImageRoot = "/img_src";
    private const string BlogRoot = "/blogs";
    private const string AboutFile = "/about/about.md";
    private static readonly Regex ImportableBlogFilename =
        new(@"^[A-Z]{2,5}_\d{8}_\d{6}\.md$", RegexOptions.Compiled);
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    public async Task PersistPathAsync(string path, CancellationToken cancellationToken = default)
    {
        var entry = await context.Paths
            .AsNoTracking()
            .Include(value => value.TagLinks)
            .ThenInclude(link => link.Tag)
            .FirstOrDefaultAsync(value => value.Path == path, cancellationToken)
            ?? throw new InvalidOperationException($"Gallery path was not found while persisting it: {path}");
        var items = await context.Items
            .AsNoTracking()
            .Include(item => item.TagLinks)
            .ThenInclude(link => link.Tag)
            .Where(item => item.ParentPath == path)
            .ToListAsync(cancellationToken);

        var directory = ResolveImageDirectory(path, allowRoot: true);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Gallery directory does not exist: {directory}");
        }

        var descriptionPath = Path.Combine(directory, "description.md");
        if (string.IsNullOrWhiteSpace(entry.Description))
        {
            if (File.Exists(descriptionPath))
            {
                File.Delete(descriptionPath);
            }
        }
        else
        {
            await File.WriteAllTextAsync(descriptionPath, entry.Description, Utf8WithoutBom, cancellationToken);
        }

        var itemTags = items
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
        var manifestType = entry.Type?.Trim().ToLowerInvariant();
        if (manifestType == "directory")
        {
            manifestType = null;
        }
        if (manifestType is not null && manifestType != "comic")
        {
            throw new InvalidOperationException($"Unsupported path type '{entry.Type}' for path '{entry.Path}'.");
        }

        var manifestPath = Path.Combine(directory, ".dimdock.json");
        if (manifestType is null && directoryTags.Length == 0 && itemTags.Count == 0)
        {
            if (File.Exists(manifestPath))
            {
                File.Delete(manifestPath);
            }
            return;
        }

        var manifest = JsonSerializer.Serialize(
            new { version = 1, type = manifestType, tags = directoryTags, items = itemTags },
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
        await File.WriteAllTextAsync(manifestPath, manifest, Utf8WithoutBom, cancellationToken);
    }

    public async Task PersistAboutAsync(CancellationToken cancellationToken = default)
    {
        var content = await context.About
            .AsNoTracking()
            .Where(entry => entry.Id == 1)
            .Select(entry => entry.Description)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
        await File.WriteAllTextAsync(AboutFile, content, Utf8WithoutBom, cancellationToken);
    }

    public async Task PersistAdminOnlyTagsAsync(
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken = default)
    {
        var tagsFile = Path.Combine(environment.ContentRootPath, "admin", "admin-only-tags.json");
        var directory = Path.GetDirectoryName(tagsFile)
            ?? throw new InvalidOperationException("Admin-only tags file has no parent directory.");
        Directory.CreateDirectory(directory);
        var content = JsonSerializer.Serialize(
            new Dictionary<string, IReadOnlyCollection<string>>
            {
                ["admin-only-tags"] = tags
            },
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(tagsFile, content, Utf8WithoutBom, cancellationToken);
    }

    public bool BlogFileExists(string sourceFilename) =>
        File.Exists(ResolveBlogFile(sourceFilename));

    public async Task PersistBlogAsync(Blog post, CancellationToken cancellationToken = default)
    {
        var oldFilename = post.SourceFilename;
        if (!ImportableBlogFilename.IsMatch(post.SourceFilename))
        {
            var timestamp = post.Timestamp ?? DateTime.UtcNow;
            var candidate = $"UTC_{timestamp:yyyyMMdd_HHmmss}.md";
            while (await context.Blogs.AnyAsync(
                       blog => blog.Id != post.Id && blog.SourceFilename == candidate,
                       cancellationToken) ||
                   File.Exists(ResolveBlogFile(candidate)))
            {
                timestamp = timestamp.AddSeconds(1);
                candidate = $"UTC_{timestamp:yyyyMMdd_HHmmss}.md";
            }

            if (post.Id != 0)
            {
                await context.Blogs
                    .Where(blog => blog.Id == post.Id)
                    .ExecuteUpdateAsync(
                        update => update.SetProperty(blog => blog.SourceFilename, candidate),
                        cancellationToken);
            }
            post.SourceFilename = candidate;
        }

        var filePath = ResolveBlogFile(post.SourceFilename);
        Directory.CreateDirectory(BlogRoot);
        await File.WriteAllTextAsync(filePath, post.Text ?? string.Empty, Utf8WithoutBom, cancellationToken);
        if (!string.IsNullOrWhiteSpace(oldFilename) &&
            !string.Equals(oldFilename, post.SourceFilename, StringComparison.Ordinal))
        {
            DeleteBlogFile(oldFilename);
        }
    }

    public void DeleteBlogFile(string sourceFilename)
    {
        var filePath = ResolveBlogFile(sourceFilename);
        if (File.Exists(filePath))
        {
            EnsureNotLinkedFile(filePath);
            File.Delete(filePath);
        }
    }

    public async Task DeleteItemFileAsync(
        string parentPath,
        string originalName,
        CancellationToken cancellationToken = default)
    {
        var filePath = ResolveItemFile(parentPath, originalName);
        if (Directory.Exists(filePath))
        {
            throw new InvalidOperationException($"Item deletion resolves to a directory: {parentPath}/{originalName}");
        }
        if (File.Exists(filePath))
        {
            EnsureNotLinkedFile(filePath);
            File.Delete(filePath);
        }

        await context.PendingItemDeletions
            .Where(entry => entry.ParentPath == parentPath && entry.OriginalName == originalName)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task MoveEntriesAsync(
        IReadOnlyCollection<Item> items,
        IReadOnlyCollection<Shortcut> shortcuts,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0 && shortcuts.Count == 0)
        {
            throw new GalleryMoveException("Select at least one item to move.");
        }
        if (!await context.Paths.AnyAsync(path => path.Path == targetPath, cancellationToken))
        {
            throw new GalleryMoveException("Select an existing destination directory.");
        }

        var itemIds = items.Select(item => item.Id).Distinct().ToList();
        var shortcutIds = shortcuts.Select(shortcut => shortcut.Id).Distinct().ToList();
        var affectedItemIds = items.Select(item => item.Id)
            .Concat(shortcuts.Select(shortcut => shortcut.OriginalId))
            .Distinct()
            .ToList();
        if (items.Any(item => string.Equals(item.ParentPath ?? "root", targetPath, StringComparison.OrdinalIgnoreCase)) ||
            shortcuts.Any(shortcut => string.Equals(shortcut.ShortcutPath, targetPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new GalleryMoveException("The selected item is already in that directory.");
        }
        if (items.Select(item => item.Id)
            .Intersect(shortcuts.Select(shortcut => shortcut.OriginalId))
            .Any())
        {
            throw new GalleryMoveException("An original item and one of its shortcuts cannot both be moved to the same directory.");
        }
        if (shortcuts.GroupBy(shortcut => shortcut.OriginalId).Any(group => group.Count() > 1))
        {
            throw new GalleryMoveException("Only one shortcut per image can be moved to the same directory.");
        }

        var occupiedOriginalIds = await context.Items
            .Where(item => item.ParentPath == targetPath && affectedItemIds.Contains(item.Id))
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        var occupiedShortcutItemIds = await context.Shortcuts
            .Where(shortcut => shortcut.ShortcutPath == targetPath &&
                affectedItemIds.Contains(shortcut.OriginalId))
            .Select(shortcut => shortcut.OriginalId)
            .ToListAsync(cancellationToken);
        if (occupiedOriginalIds.Count > 0 || occupiedShortcutItemIds.Count > 0)
        {
            throw new GalleryMoveException("The destination already contains one or more selected images.");
        }

        var fileNameComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        if (items.Select(item => item.OriginalName).Distinct(fileNameComparer).Count() != items.Count)
        {
            throw new GalleryMoveException("The selected images contain duplicate filenames that cannot share a directory.");
        }

        var fileMoves = items.Select(item =>
        {
            var sourceParentPath = item.ParentPath ?? "root";
            var source = ResolveItemFile(sourceParentPath, item.OriginalName);
            var destination = ResolveItemFile(targetPath, item.OriginalName);
            if (!File.Exists(source))
            {
                if (Directory.Exists(source))
                {
                    throw new GalleryMoveException($"The image source resolves to a directory: {sourceParentPath}/{item.OriginalName}");
                }
                throw new GalleryMoveException($"The image source file was not found: {sourceParentPath}/{item.OriginalName}");
            }
            EnsureNotLinkedFile(source);
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                throw new GalleryMoveException($"The destination already contains a file named '{item.OriginalName}'.");
            }

            return (Item: item, SourceParentPath: sourceParentPath, Source: source, Destination: destination);
        }).ToList();

        var movedFiles = new List<(string Source, string Destination)>();
        try
        {
            foreach (var fileMove in fileMoves)
            {
                File.Move(fileMove.Source, fileMove.Destination);
                movedFiles.Add((fileMove.Source, fileMove.Destination));
            }

            var executionStrategy = context.Database.CreateExecutionStrategy();
            await executionStrategy.ExecuteAsync(async () =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
                if (itemIds.Count > 0)
                {
                    await context.Items
                        .Where(item => itemIds.Contains(item.Id))
                        .ExecuteUpdateAsync(
                            update => update.SetProperty(item => item.ParentPath, targetPath),
                            cancellationToken);
                    await context.Shortcuts
                        .Where(shortcut => itemIds.Contains(shortcut.OriginalId))
                        .ExecuteUpdateAsync(
                            update => update.SetProperty(shortcut => shortcut.ParentPath, targetPath),
                            cancellationToken);
                }

                if (shortcutIds.Count > 0)
                {
                    await context.Shortcuts
                        .Where(shortcut => shortcutIds.Contains(shortcut.Id))
                        .ExecuteUpdateAsync(
                            update => update.SetProperty(shortcut => shortcut.ShortcutPath, targetPath),
                            cancellationToken);
                }

                var dateTagChanges = fileMoves
                    .Select(fileMove => new
                    {
                        ItemId = fileMove.Item.Id,
                        OldTags = DatePathTags.ForPath(fileMove.SourceParentPath),
                        NewTags = DatePathTags.ForPath(targetPath)
                    })
                    .ToList();
                var newDateTagNames = dateTagChanges
                    .SelectMany(change => change.NewTags)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                foreach (var tagName in newDateTagNames)
                {
                    await context.Database.ExecuteSqlInterpolatedAsync(
                        $"INSERT IGNORE INTO Tags (name) VALUES ({tagName})",
                        cancellationToken);
                }

                var dateTagNames = dateTagChanges
                    .SelectMany(change => change.OldTags.Concat(change.NewTags))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var dateTagIds = (await context.Tags
                    .Where(tag => dateTagNames.Contains(tag.Name))
                    .Select(tag => new { tag.Name, tag.Id })
                    .ToListAsync(cancellationToken))
                    .ToDictionary(tag => tag.Name, tag => tag.Id, StringComparer.Ordinal);
                foreach (var change in dateTagChanges)
                {
                    var removedTagIds = change.OldTags
                        .Where(tagName => !change.NewTags.Contains(tagName, StringComparer.Ordinal))
                        .Where(dateTagIds.ContainsKey)
                        .Select(tagName => dateTagIds[tagName])
                        .ToList();
                    if (removedTagIds.Count > 0)
                    {
                        await context.ItemTags
                            .Where(link => link.ItemId == change.ItemId && removedTagIds.Contains(link.TagId))
                            .ExecuteDeleteAsync(cancellationToken);
                    }

                    foreach (var tagName in change.NewTags)
                    {
                        await context.Database.ExecuteSqlInterpolatedAsync(
                            $"INSERT IGNORE INTO ItemTags (item_id, tag_id) VALUES ({change.ItemId}, {dateTagIds[tagName]})",
                            cancellationToken);
                    }
                }

                await transaction.CommitAsync(cancellationToken);
            });
        }
        catch (Exception moveError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var movedFile in movedFiles.AsEnumerable().Reverse())
            {
                try
                {
                    File.Move(movedFile.Destination, movedFile.Source);
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }

            if (rollbackErrors.Count > 0)
            {
                throw new AggregateException(
                    "Moving the selected items failed, and one or more files could not be restored.",
                    new[] { moveError }.Concat(rollbackErrors));
            }

            throw;
        }

        foreach (var sourcePath in fileMoves
            .Select(fileMove => fileMove.SourceParentPath)
            .Append(targetPath)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await PersistPathAsync(sourcePath, cancellationToken);
        }
    }

    public async Task DeletePathDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var directory = ResolveImageDirectory(path, allowRoot: false);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
        else if (File.Exists(directory))
        {
            throw new InvalidOperationException($"Path deletion resolves to a file: {path}");
        }

        await context.PendingPathDeletions
            .Where(entry => entry.Path == path)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static string ResolveImageDirectory(string path, bool allowRoot)
    {
        var root = Path.GetFullPath(ImageRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Image directory does not exist: {root}");
        }
        EnsureNotLinkedDirectory(root);

        if (string.Equals(path, "root", StringComparison.OrdinalIgnoreCase))
        {
            return allowRoot
                ? root
                : throw new InvalidOperationException("The root gallery path cannot be deleted.");
        }

        var segments = path.Split('/');
        if (segments.Length == 0 ||
            segments.Any(segment =>
                segment.Length == 0 ||
                segment is "." or ".." ||
                segment.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_')))
        {
            throw new InvalidOperationException($"Invalid gallery path: {path}");
        }

        var directory = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!directory.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidOperationException($"Gallery path resolves outside the image directory: {path}");
        }

        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current))
            {
                EnsureNotLinkedDirectory(current);
            }
        }

        return directory;
    }

    private static string ResolveItemFile(string parentPath, string originalName)
    {
        if (string.IsNullOrWhiteSpace(originalName) ||
            originalName is "." or ".." ||
            originalName != Path.GetFileName(originalName) ||
            originalName.Contains('/') ||
            originalName.Contains('\\') ||
            originalName.Any(char.IsControl))
        {
            throw new InvalidOperationException($"Invalid image filename: {originalName}");
        }

        var directory = ResolveImageDirectory(parentPath, allowRoot: true);
        var filePath = Path.GetFullPath(Path.Combine(directory, originalName));
        var root = Path.GetFullPath(ImageRoot);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!filePath.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidOperationException($"Image file resolves outside the image directory: {parentPath}/{originalName}");
        }
        return filePath;
    }

    private static string ResolveBlogFile(string sourceFilename)
    {
        if (string.IsNullOrWhiteSpace(sourceFilename) ||
            sourceFilename != Path.GetFileName(sourceFilename) ||
            !string.Equals(Path.GetExtension(sourceFilename), ".md", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Invalid blog source filename: {sourceFilename}");
        }
        return Path.GetFullPath(Path.Combine(BlogRoot, sourceFilename));
    }

    private static void EnsureNotLinkedDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"Filesystem path cannot be a linked directory: {path}");
        }
    }

    private static void EnsureNotLinkedFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"Filesystem path cannot be a linked file: {path}");
        }
    }
}

public sealed class GalleryMoveException(string message) : Exception(message)
{
}
