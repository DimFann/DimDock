using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Http;
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
    private const string BlogAssetsRoot = "/blogs/assets";
    private const string AboutAssetsRoot = "/about/assets";
    private const long MaximumImageAssetBytes = 20L * 1024 * 1024;
    private static readonly HashSet<string> ImageAssetExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".gif", ".jpeg", ".jpg", ".png", ".webp" };
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

        var descriptionPath = Path.Combine(directory, ".description.md");
        var legacyDescriptionPath = Path.Combine(directory, "description.md");
        if (File.Exists(legacyDescriptionPath) && !File.Exists(descriptionPath))
        {
            File.Move(legacyDescriptionPath, descriptionPath);
        }
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

        var descriptionTrackedItems = ReadDescriptionTrackedItemNames(
            Path.Combine(directory, ".dimdock.json"));
        var itemMetadata = items
            .Select(item =>
            {
                var tags = item.TagLinks
                    .Select(link => link.Tag.Name)
                    .Where(name => !DatePathTags.ForPath(item.ParentPath).Contains(name, StringComparer.Ordinal))
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();
                var hasDescription = !string.IsNullOrWhiteSpace(item.Description);
                return new
                {
                    item.OriginalName,
                    Tags = tags,
                    item.Description,
                    IncludeDescription = hasDescription || descriptionTrackedItems.Contains(item.OriginalName)
                };
            })
            .Where(item => item.Tags.Length > 0 || item.IncludeDescription)
            .ToDictionary(
                item => item.OriginalName,
                item => new { item.Tags, item.Description },
                StringComparer.Ordinal);
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
        if (manifestType is null && directoryTags.Length == 0 && itemMetadata.Count == 0)
        {
            if (File.Exists(manifestPath))
            {
                File.Delete(manifestPath);
            }
            return;
        }

        var manifest = JsonSerializer.Serialize(
            new { version = 1, type = manifestType, tags = directoryTags, items = itemMetadata },
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
        await File.WriteAllTextAsync(manifestPath, manifest, Utf8WithoutBom, cancellationToken);
    }

    private static HashSet<string> ReadDescriptionTrackedItemNames(string manifestPath)
    {
        var itemNames = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(manifestPath))
        {
            return itemNames;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!document.RootElement.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Object)
        {
            return itemNames;
        }

        foreach (var item in items.EnumerateObject())
        {
            if (item.Value.ValueKind == JsonValueKind.Object &&
                item.Value.TryGetProperty("description", out _))
            {
                itemNames.Add(item.Name);
            }
        }

        return itemNames;
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

    public async Task<string> SaveBlogAssetAsync(
        ulong postId,
        IFormFile file,
        CancellationToken cancellationToken = default)
    {
        if (postId == 0)
        {
            throw new InvalidDataException("The blog post is not ready to receive images.");
        }

        Directory.CreateDirectory(BlogAssetsRoot);
        EnsureNotLinkedDirectory(BlogAssetsRoot);

        var postDirectory = ResolveBlogAssetsDirectory(postId);
        Directory.CreateDirectory(postDirectory);
        EnsureNotLinkedDirectory(postDirectory);

        var fileName = await SaveImageAssetAsync(postDirectory, file, cancellationToken);
        return $"/blogs/assets/{postId}/{fileName}";
    }

    public async Task<string> SaveAboutAssetAsync(
        IFormFile file,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(AboutAssetsRoot);
        EnsureNotLinkedDirectory(AboutAssetsRoot);
        var fileName = await SaveImageAssetAsync(AboutAssetsRoot, file, cancellationToken);
        return $"/about/assets/{fileName}";
    }

    public void DeleteUnreferencedAboutAssets(
        string? previousMarkdown,
        string? currentMarkdown,
        IEnumerable<string> uploadedUrls)
    {
        if (!Directory.Exists(AboutAssetsRoot))
        {
            return;
        }

        EnsureNotLinkedDirectory(AboutAssetsRoot);
        var candidates = GetAboutAssetNames(previousMarkdown);
        foreach (var url in uploadedUrls)
        {
            var name = GetAboutAssetName(url);
            if (name is not null)
            {
                candidates.Add(name);
            }
        }

        candidates.ExceptWith(GetAboutAssetNames(currentMarkdown));
        foreach (var name in candidates)
        {
            var path = Path.Combine(AboutAssetsRoot, name);
            if (File.Exists(path))
            {
                EnsureNotLinkedFile(path);
                File.Delete(path);
            }
        }
    }

    private static HashSet<string> GetAboutAssetNames(string? markdown) =>
        Markdown.Parse(markdown ?? string.Empty, BlogMarkdownRenderer.Pipeline)
            .Descendants<LinkInline>()
            .Select(link => GetAboutAssetName(link.Url))
            .Where(name => name is not null)
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);

    private static string? GetAboutAssetName(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var path = Uri.UnescapeDataString(url.Split('?', '#')[0]);
        var prefix = path.StartsWith("/about/assets/", StringComparison.Ordinal)
            ? "/about/assets/"
            : path.StartsWith("about/assets/", StringComparison.Ordinal)
                ? "about/assets/"
                : path.StartsWith("assets/", StringComparison.Ordinal)
                    ? "assets/"
                    : null;
        if (prefix is null)
        {
            return null;
        }

        var name = path[prefix.Length..];
        var extension = Path.GetExtension(name);
        return name == Path.GetFileName(name) &&
               !name.Contains('\\') &&
               ImageAssetExtensions.Contains(extension) &&
               Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _)
            ? name
            : null;
    }

    private static async Task<string> SaveImageAssetAsync(
        string directory,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file.Length == 0 || file.Length > MaximumImageAssetBytes)
        {
            throw new InvalidDataException("Images must be between 1 byte and 20 MB.");
        }

        var extension = Path.GetExtension(file.FileName);
        if (!ImageAssetExtensions.Contains(extension))
        {
            throw new InvalidDataException("Upload a PNG, JPEG, GIF, or WebP image.");
        }

        var fileName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        var filePath = Path.Combine(directory, fileName);
        await using var input = file.OpenReadStream();
        var signature = new byte[12];
        var signatureLength = 0;
        while (signatureLength < signature.Length)
        {
            var bytesRead = await input.ReadAsync(signature.AsMemory(signatureLength), cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            signatureLength += bytesRead;
        }

        if (!HasImageSignature(extension, signature.AsSpan(0, signatureLength)))
        {
            throw new InvalidDataException("The uploaded file does not match its image extension.");
        }

        input.Position = 0;
        try
        {
            await using var output = new FileStream(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
            await input.CopyToAsync(output, cancellationToken);
        }
        catch
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            throw;
        }

        return fileName;
    }

    public void DeleteBlogAssets(ulong postId)
    {
        if (postId == 0 || !Directory.Exists(BlogAssetsRoot))
        {
            return;
        }

        EnsureNotLinkedDirectory(BlogAssetsRoot);
        var directory = ResolveBlogAssetsDirectory(postId);
        if (Directory.Exists(directory))
        {
            EnsureNotLinkedDirectory(directory);
            Directory.Delete(directory, recursive: true);
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

    public string GetItemFilePath(string parentPath, string originalName)
    {
        var filePath = ResolveItemFile(parentPath, originalName);
        if (File.Exists(filePath))
        {
            EnsureNotLinkedFile(filePath);
        }
        return filePath;
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

    public async Task<string> RenamePathAsync(
        string path,
        string newName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            string.Equals(path, "root", StringComparison.OrdinalIgnoreCase))
        {
            throw new GalleryPathRenameException("The root path cannot be renamed.");
        }
        if (string.IsNullOrWhiteSpace(newName) ||
            newName.Length > 255 ||
            newName.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_'))
        {
            throw new GalleryPathRenameException(
                "Path names must be 1-255 letters, numbers, underscores, or hyphens.");
        }

        var sourceEntry = await context.Paths
            .AsNoTracking()
            .Where(entry => entry.Path == path)
            .Select(entry => new { entry.Path, entry.ParentPath })
            .FirstOrDefaultAsync(cancellationToken);
        if (sourceEntry is null)
        {
            throw new GalleryPathRenameException("The path no longer exists.");
        }

        var oldPath = sourceEntry.Path;
        if (string.Equals(oldPath, "root", StringComparison.OrdinalIgnoreCase))
        {
            throw new GalleryPathRenameException("The root path cannot be renamed.");
        }
        var parentPath = sourceEntry.ParentPath ?? "root";
        var newPath = parentPath == "root" ? newName : $"{parentPath}/{newName}";
        if (newPath.Length > 1024)
        {
            throw new GalleryPathRenameException("The full path cannot exceed 1024 characters.");
        }
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new GalleryPathRenameException("Choose a different path name.");
        }

        var oldPrefix = oldPath + "/";
        var subtreePaths = await context.Paths
            .AsNoTracking()
            .Where(entry => entry.Path == oldPath || entry.Path.StartsWith(oldPrefix))
            .Select(entry => entry.Path)
            .ToListAsync(cancellationToken);
        if (subtreePaths.Count == 0)
        {
            throw new GalleryPathRenameException("The path no longer exists.");
        }

        var subtreePathSet = subtreePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var renamedPaths = subtreePaths.ToDictionary(
            subtreePath => subtreePath,
            subtreePath => newPath + subtreePath[oldPath.Length..],
            StringComparer.OrdinalIgnoreCase);
        if (renamedPaths.Values.Any(candidate => candidate.Length > 1024))
        {
            throw new GalleryPathRenameException("A descendant path would exceed the 1024 character path limit.");
        }

        var existingPaths = await context.Paths
            .AsNoTracking()
            .Select(entry => entry.Path)
            .ToListAsync(cancellationToken);
        var existingPathsOutsideSubtree = existingPaths
            .Where(existing => !subtreePathSet.Contains(existing))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (renamedPaths.Values.Any(existingPathsOutsideSubtree.Contains))
        {
            throw new GalleryPathRenameException("A path with that name already exists.");
        }

        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS PendingPathDeletions (
                path VARCHAR(1024) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY
            )
            """,
            cancellationToken);
        var pendingPathDeletions = await context.PendingPathDeletions
            .AsNoTracking()
            .Select(entry => entry.Path)
            .ToListAsync(cancellationToken);
        if (pendingPathDeletions.Any(pendingPath =>
            IsSameOrChildPath(oldPath, pendingPath) ||
            IsSameOrChildPath(pendingPath, oldPath) ||
            IsSameOrChildPath(newPath, pendingPath) ||
            IsSameOrChildPath(pendingPath, newPath)))
        {
            throw new GalleryPathRenameException(
                "Finish queued path deletions before renaming this path.");
        }

        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS PendingItemDeletions (
                id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
                parent_path VARCHAR(1024) NOT NULL,
                original_name VARCHAR(255) NOT NULL
            )
            """,
            cancellationToken);
        var hasPendingItemDeletions = await context.PendingItemDeletions
            .AnyAsync(entry => entry.ParentPath == oldPath || entry.ParentPath.StartsWith(oldPrefix), cancellationToken);
        if (hasPendingItemDeletions)
        {
            throw new GalleryPathRenameException(
                "Finish queued item deletions before renaming this path.");
        }

        var sourceDirectory = ResolveImageDirectory(oldPath, allowRoot: false);
        var destinationDirectory = ResolveImageDirectory(newPath, allowRoot: false);
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"Gallery directory does not exist: {sourceDirectory}");
        }
        foreach (var subtreePath in subtreePaths)
        {
            var subtreeDirectory = ResolveImageDirectory(subtreePath, allowRoot: false);
            if (!Directory.Exists(subtreeDirectory))
            {
                throw new DirectoryNotFoundException($"Gallery directory does not exist: {subtreeDirectory}");
            }
        }
        if (Directory.Exists(destinationDirectory) || File.Exists(destinationDirectory))
        {
            throw new GalleryPathRenameException("A filesystem path with that name already exists.");
        }

        var itemPathMappings = await context.Items
            .AsNoTracking()
            .Where(item => item.ParentPath == oldPath || (item.ParentPath != null && item.ParentPath.StartsWith(oldPrefix)))
            .Select(item => new { item.Id, item.ParentPath })
            .ToListAsync(cancellationToken);
        var dateTagChanges = itemPathMappings
            .Select(item =>
            {
                var oldItemPath = item.ParentPath ?? oldPath;
                var newItemPath = newPath + oldItemPath[oldPath.Length..];
                return new
                {
                    item.Id,
                    NewPath = newItemPath,
                    OldTags = DatePathTags.ForPath(oldItemPath),
                    NewTags = DatePathTags.ForPath(newItemPath)
                };
            })
            .ToList();

        Directory.Move(sourceDirectory, destinationDirectory);
        try
        {
            var executionStrategy = context.Database.CreateExecutionStrategy();
            await executionStrategy.ExecuteAsync(async () =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                await context.Paths
                    .Where(entry => entry.Path == oldPath || entry.Path.StartsWith(oldPrefix))
                    .ExecuteUpdateAsync(
                        update => update
                            .SetProperty(
                                entry => entry.Path,
                                entry => entry.Path == oldPath
                                    ? newPath
                                    : newPath + entry.Path.Substring(oldPath.Length))
                            .SetProperty(
                                entry => entry.ParentPath,
                                entry => entry.ParentPath == oldPath
                                    ? newPath
                                    : entry.ParentPath != null && entry.ParentPath.StartsWith(oldPrefix)
                                        ? newPath + entry.ParentPath.Substring(oldPath.Length)
                                        : entry.ParentPath),
                        cancellationToken);

                await context.Items
                    .Where(item => item.ParentPath == oldPath || (item.ParentPath != null && item.ParentPath.StartsWith(oldPrefix)))
                    .ExecuteUpdateAsync(
                        update => update.SetProperty(
                            item => item.ParentPath,
                            item => item.ParentPath == oldPath
                                ? newPath
                                : newPath + item.ParentPath!.Substring(oldPath.Length)),
                        cancellationToken);

                await context.Shortcuts
                    .Where(shortcut =>
                        (shortcut.ShortcutPath == oldPath || (shortcut.ShortcutPath != null && shortcut.ShortcutPath.StartsWith(oldPrefix))) ||
                        (shortcut.ParentPath == oldPath || (shortcut.ParentPath != null && shortcut.ParentPath.StartsWith(oldPrefix))))
                    .ExecuteUpdateAsync(
                        update => update
                            .SetProperty(
                                shortcut => shortcut.ShortcutPath,
                                shortcut => shortcut.ShortcutPath == oldPath
                                    ? newPath
                                    : shortcut.ShortcutPath != null && shortcut.ShortcutPath.StartsWith(oldPrefix)
                                        ? newPath + shortcut.ShortcutPath.Substring(oldPath.Length)
                                        : shortcut.ShortcutPath)
                            .SetProperty(
                                shortcut => shortcut.ParentPath,
                                shortcut => shortcut.ParentPath == oldPath
                                    ? newPath
                                    : shortcut.ParentPath != null && shortcut.ParentPath.StartsWith(oldPrefix)
                                        ? newPath + shortcut.ParentPath.Substring(oldPath.Length)
                                        : shortcut.ParentPath),
                        cancellationToken);

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
                            .Where(link => link.ItemId == change.Id && removedTagIds.Contains(link.TagId))
                            .ExecuteDeleteAsync(cancellationToken);
                    }

                    foreach (var tagName in change.NewTags)
                    {
                        await context.Database.ExecuteSqlInterpolatedAsync(
                            $"INSERT IGNORE INTO ItemTags (item_id, tag_id) VALUES ({change.Id}, {dateTagIds[tagName]})",
                            cancellationToken);
                    }
                }

                await transaction.CommitAsync(cancellationToken);
            });
        }
        catch (Exception renameError)
        {
            try
            {
                Directory.Move(destinationDirectory, sourceDirectory);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "Renaming the gallery path failed, and the directory could not be restored.",
                    renameError,
                    rollbackError);
            }

            throw;
        }

        foreach (var renamedPath in renamedPaths.Values.OrderBy(candidate => candidate.Length))
        {
            await PersistPathAsync(renamedPath, cancellationToken);
        }

        return newPath;
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

    private static string ResolveBlogAssetsDirectory(ulong postId)
    {
        var root = Path.GetFullPath(BlogAssetsRoot);
        var directory = Path.GetFullPath(Path.Combine(root, postId.ToString()));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!directory.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidOperationException($"Blog assets resolve outside their root: {postId}");
        }

        return directory;
    }

    private static bool HasImageSignature(string extension, ReadOnlySpan<byte> signature) =>
        extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" =>
                signature.Length >= 3 &&
                signature[0] == 0xff &&
                signature[1] == 0xd8 &&
                signature[2] == 0xff,
            ".png" =>
                signature.Length >= 8 &&
                signature[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
            ".gif" =>
                signature.Length >= 6 &&
                (signature[..6].SequenceEqual("GIF87a"u8) || signature[..6].SequenceEqual("GIF89a"u8)),
            ".webp" =>
                signature.Length >= 12 &&
                signature[..4].SequenceEqual("RIFF"u8) &&
                signature[8..12].SequenceEqual("WEBP"u8),
            _ => false
        };

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

    private static bool IsSameOrChildPath(string candidate, string parent) =>
        string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
}

public sealed class GalleryMoveException(string message) : Exception(message)
{
}

public sealed class GalleryPathRenameException(string message) : Exception(message)
{
}
