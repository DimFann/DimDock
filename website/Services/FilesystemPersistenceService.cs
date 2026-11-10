using System.Text.Json;
using System.Text.Json.Nodes;
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
    IFileStore fileStore)
{
    private const long MaximumImageAssetBytes = 20L * 1024 * 1024;
    private static readonly HashSet<string> ImageAssetExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".gif", ".jpeg", ".jpg", ".png", ".webp" };
    private static readonly Regex ImportableBlogFilename =
        new(@"^[A-Z]{2,5}_\d{8}_\d{6}\.md$", RegexOptions.Compiled);

    public async Task PersistDirectoryShortcutsAsync(CancellationToken cancellationToken = default)
    {
        const string key = ".shortcuts.json";
        var sourceKey = await fileStore.FileExistsAsync(FileStoreArea.Gallery, key, cancellationToken)
            ? key : "shortcuts.json";
        var exists = await fileStore.FileExistsAsync(FileStoreArea.Gallery, sourceKey, cancellationToken);
        var document = exists
            ? JsonNode.Parse(await fileStore.ReadAllTextAsync(FileStoreArea.Gallery, sourceKey, cancellationToken)) as JsonObject
                ?? throw new InvalidDataException("Shortcut configuration must be a JSON object.")
            : new JsonObject();
        var shortcuts = await context.DirectoryShortcuts.AsNoTracking()
            .Include(shortcut => shortcut.TargetPath)
            .Include(shortcut => shortcut.ParentPath)
            .ToListAsync(cancellationToken);
        if (!exists && shortcuts.Count == 0)
        {
            return;
        }
        document["directory_shortcuts"] = JsonSerializer.SerializeToNode(
            shortcuts.GroupBy(shortcut => shortcut.ParentPath.Path)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group
                    .OrderBy(shortcut => shortcut.Name, StringComparer.Ordinal)
                    .ThenBy(shortcut => shortcut.TargetPath.Path, StringComparer.Ordinal)
                    .Select(shortcut => new { name = shortcut.Name, target_path = shortcut.TargetPath.Path })
                    .ToArray()));
        await fileStore.WriteAllTextAsync(
            FileStoreArea.Gallery, key,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }

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

        var directoryKey = GalleryDirectoryKey(path);
        if (!await fileStore.DirectoryExistsAsync(FileStoreArea.Gallery, directoryKey, cancellationToken))
        {
            throw new DirectoryNotFoundException($"Gallery directory does not exist: {path}");
        }

        var descriptionKey = JoinKey(directoryKey, ".description.md");
        var legacyDescriptionKey = JoinKey(directoryKey, "description.md");
        if (await fileStore.FileExistsAsync(FileStoreArea.Gallery, legacyDescriptionKey, cancellationToken) &&
            !await fileStore.FileExistsAsync(FileStoreArea.Gallery, descriptionKey, cancellationToken))
        {
            await fileStore.MoveFileAsync(
                FileStoreArea.Gallery,
                legacyDescriptionKey,
                descriptionKey,
                cancellationToken);
        }
        if (string.IsNullOrWhiteSpace(entry.Description))
        {
            if (await fileStore.FileExistsAsync(FileStoreArea.Gallery, descriptionKey, cancellationToken))
            {
                await fileStore.DeleteFileAsync(FileStoreArea.Gallery, descriptionKey, cancellationToken);
            }
        }
        else
        {
            await fileStore.WriteAllTextAsync(
                FileStoreArea.Gallery,
                descriptionKey,
                entry.Description,
                cancellationToken);
        }

        var descriptionTrackedItems = await ReadDescriptionTrackedItemNames(
            JoinKey(directoryKey, ".dimdock.json"),
            cancellationToken);
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

        var manifestKey = JoinKey(directoryKey, ".dimdock.json");
        if (manifestType is null && directoryTags.Length == 0 && itemMetadata.Count == 0)
        {
            if (await fileStore.FileExistsAsync(FileStoreArea.Gallery, manifestKey, cancellationToken))
            {
                await fileStore.DeleteFileAsync(FileStoreArea.Gallery, manifestKey, cancellationToken);
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
        await fileStore.WriteAllTextAsync(
            FileStoreArea.Gallery,
            manifestKey,
            manifest,
            cancellationToken);
    }

    private async Task<HashSet<string>> ReadDescriptionTrackedItemNames(
        string manifestKey,
        CancellationToken cancellationToken)
    {
        var itemNames = new HashSet<string>(StringComparer.Ordinal);
        if (!await fileStore.FileExistsAsync(FileStoreArea.Gallery, manifestKey, cancellationToken))
        {
            return itemNames;
        }

        using var document = JsonDocument.Parse(
            await fileStore.ReadAllTextAsync(FileStoreArea.Gallery, manifestKey, cancellationToken));
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
        await fileStore.WriteAllTextAsync(
            FileStoreArea.About,
            "about.md",
            content,
            cancellationToken);
    }

    public async Task PersistAdminOnlyTagsAsync(
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken = default)
    {
        var content = JsonSerializer.Serialize(
            new Dictionary<string, IReadOnlyCollection<string>>
            {
                ["admin-only-tags"] = tags
            },
            new JsonSerializerOptions { WriteIndented = true });
        await fileStore.WriteAllTextAsync(
            FileStoreArea.Admin,
            "admin-only-tags.json",
            content,
            cancellationToken);
    }

    public Task<bool> BlogFileExistsAsync(
        string sourceFilename,
        CancellationToken cancellationToken = default) =>
        fileStore.FileExistsAsync(FileStoreArea.Blogs, ResolveBlogFileKey(sourceFilename), cancellationToken);

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
                   await BlogFileExistsAsync(candidate, cancellationToken))
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

        var fileKey = ResolveBlogFileKey(post.SourceFilename);
        await fileStore.WriteAllTextAsync(
            FileStoreArea.Blogs,
            fileKey,
            post.Text ?? string.Empty,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(oldFilename) &&
            !string.Equals(oldFilename, post.SourceFilename, StringComparison.Ordinal))
        {
            await DeleteBlogFileAsync(oldFilename, cancellationToken);
        }

        if (!HasImageEmbeds(post.Text))
        {
            await DeleteBlogAssetsAsync(post.Id, cancellationToken);
        }
    }

    public Task DeleteBlogFileAsync(
        string sourceFilename,
        CancellationToken cancellationToken = default) =>
        fileStore.DeleteFileAsync(
            FileStoreArea.Blogs,
            ResolveBlogFileKey(sourceFilename),
            cancellationToken);

    public async Task<string> SaveBlogAssetAsync(
        ulong postId,
        IFormFile file,
        CancellationToken cancellationToken = default)
    {
        if (postId == 0)
        {
            throw new InvalidDataException("The blog post is not ready to receive images.");
        }

        var postDirectory = $"assets/{postId}";
        await fileStore.CreateDirectoryAsync(FileStoreArea.Blogs, postDirectory, cancellationToken);
        var fileName = await SaveImageAssetAsync(
            FileStoreArea.Blogs,
            postDirectory,
            file,
            cancellationToken);
        return $"/blogs/assets/{postId}/{fileName}";
    }

    // New-post images are staged under assets/drafts/<draft-id> until the post is published.
    // Draft IDs start with their UTC creation time so abandoned drafts can be swept.
    private const string BlogDraftAssetsDirectory = "assets/drafts";
    private static readonly TimeSpan BlogDraftLifetime = TimeSpan.FromDays(1);

    public static string CreateBlogDraftId() => $"{DateTime.UtcNow:yyyyMMddHHmmss}{Guid.NewGuid():N}";

    public static bool IsValidBlogDraftId(string? draftId) =>
        TryGetBlogDraftCreated(draftId, out _);

    private static bool TryGetBlogDraftCreated(string? draftId, out DateTime created)
    {
        created = default;
        return draftId is { Length: 46 } &&
               draftId.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f') &&
               DateTime.TryParseExact(draftId[..14], "yyyyMMddHHmmss",
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                   out created);
    }

    public async Task<string> SaveBlogDraftAssetAsync(
        string draftId,
        IFormFile file,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidBlogDraftId(draftId))
        {
            throw new InvalidDataException("The draft is not ready to receive images.");
        }

        await DeleteStaleBlogDraftAssetsAsync(cancellationToken);
        var draftDirectory = $"{BlogDraftAssetsDirectory}/{draftId}";
        await fileStore.CreateDirectoryAsync(FileStoreArea.Blogs, draftDirectory, cancellationToken);
        var fileName = await SaveImageAssetAsync(
            FileStoreArea.Blogs,
            draftDirectory,
            file,
            cancellationToken);
        return $"/blogs/{draftDirectory}/{fileName}";
    }

    // Moves draft images referenced by the post into its asset directory, rewrites their URLs
    // and deletes the draft. Returns the rewritten Markdown.
    public async Task<string> PublishBlogDraftAssetsAsync(
        ulong postId,
        string? draftId,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        if (postId == 0 || !IsValidBlogDraftId(draftId))
        {
            return markdown;
        }

        var draftDirectory = $"{BlogDraftAssetsDirectory}/{draftId}";
        if (!await fileStore.DirectoryExistsAsync(FileStoreArea.Blogs, draftDirectory, cancellationToken))
        {
            return markdown;
        }

        var draftUrlPrefix = $"/blogs/{draftDirectory}/";
        if (markdown.Contains(draftUrlPrefix, StringComparison.Ordinal))
        {
            var postDirectory = $"assets/{postId}";
            await fileStore.CreateDirectoryAsync(FileStoreArea.Blogs, postDirectory, cancellationToken);
            foreach (var key in await fileStore.ListFilesAsync(FileStoreArea.Blogs, draftDirectory, cancellationToken: cancellationToken))
            {
                var name = key[(key.LastIndexOf('/') + 1)..];
                if (markdown.Contains(draftUrlPrefix + name, StringComparison.Ordinal))
                {
                    await fileStore.MoveFileAsync(
                        FileStoreArea.Blogs,
                        JoinKey(draftDirectory, name),
                        JoinKey(postDirectory, name),
                        cancellationToken);
                }
            }

            markdown = markdown.Replace(draftUrlPrefix, $"/blogs/{postDirectory}/", StringComparison.Ordinal);
        }

        await DeleteBlogDraftAssetsAsync(draftId, cancellationToken);
        return markdown;
    }

    public async Task DeleteBlogDraftAssetsAsync(
        string? draftId,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidBlogDraftId(draftId))
        {
            return;
        }

        var draftDirectory = $"{BlogDraftAssetsDirectory}/{draftId}";
        if (await fileStore.DirectoryExistsAsync(FileStoreArea.Blogs, draftDirectory, cancellationToken))
        {
            await fileStore.DeleteDirectoryAsync(FileStoreArea.Blogs, draftDirectory, recursive: true, cancellationToken);
        }
    }

    private async Task DeleteStaleBlogDraftAssetsAsync(CancellationToken cancellationToken)
    {
        if (!await fileStore.DirectoryExistsAsync(FileStoreArea.Blogs, BlogDraftAssetsDirectory, cancellationToken))
        {
            return;
        }

        foreach (var key in await fileStore.ListDirectoriesAsync(FileStoreArea.Blogs, BlogDraftAssetsDirectory, cancellationToken))
        {
            var draftId = key.TrimEnd('/')[(key.TrimEnd('/').LastIndexOf('/') + 1)..];
            if (!TryGetBlogDraftCreated(draftId, out var created) || DateTime.UtcNow - created > BlogDraftLifetime)
            {
                await fileStore.DeleteDirectoryAsync(
                    FileStoreArea.Blogs,
                    $"{BlogDraftAssetsDirectory}/{draftId}",
                    recursive: true,
                    cancellationToken);
            }
        }
    }

    public async Task<string> SaveAboutAssetAsync(
        IFormFile file,
        CancellationToken cancellationToken = default)
    {
        await fileStore.CreateDirectoryAsync(FileStoreArea.About, "assets", cancellationToken);
        var fileName = await SaveImageAssetAsync(
            FileStoreArea.About,
            "assets",
            file,
            cancellationToken);
        return $"/about/assets/{fileName}";
    }

    public async Task DeleteUnreferencedAboutAssetsAsync(
        string? previousMarkdown,
        string? currentMarkdown,
        IEnumerable<string> uploadedUrls,
        CancellationToken cancellationToken = default)
    {
        if (!await fileStore.DirectoryExistsAsync(FileStoreArea.About, "assets", cancellationToken))
        {
            return;
        }

        if (!HasImageEmbeds(currentMarkdown))
        {
            await fileStore.DeleteDirectoryAsync(
                FileStoreArea.About,
                "assets",
                recursive: true,
                cancellationToken);
            return;
        }

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
            if (await fileStore.FileExistsAsync(FileStoreArea.About, $"assets/{name}", cancellationToken))
            {
                await fileStore.DeleteFileAsync(FileStoreArea.About, $"assets/{name}", cancellationToken);
            }
        }
    }

    private static bool HasImageEmbeds(string? markdown) =>
        Markdown.Parse(markdown ?? string.Empty, BlogMarkdownRenderer.Pipeline)
            .Descendants<LinkInline>()
            .Any(link => link.IsImage);

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

    private async Task<string> SaveImageAssetAsync(
        FileStoreArea area,
        string directoryKey,
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

        await input.DisposeAsync();
        await using var content = file.OpenReadStream();
        await fileStore.WriteAsync(
            area,
            JoinKey(directoryKey, fileName),
            content,
            overwrite: false,
            cancellationToken);

        return fileName;
    }

    public async Task DeleteBlogAssetsAsync(
        ulong postId,
        CancellationToken cancellationToken = default)
    {
        if (postId == 0)
        {
            return;
        }

        var directoryKey = $"assets/{postId}";
        if (await fileStore.DirectoryExistsAsync(FileStoreArea.Blogs, directoryKey, cancellationToken))
        {
            await fileStore.DeleteDirectoryAsync(
                FileStoreArea.Blogs,
                directoryKey,
                recursive: true,
                cancellationToken);
        }
    }

    public async Task DeleteItemFileAsync(
        string parentPath,
        string originalName,
        CancellationToken cancellationToken = default)
    {
        var key = ItemFileKey(parentPath, originalName);
        if (await fileStore.DirectoryExistsAsync(FileStoreArea.Gallery, key, cancellationToken))
        {
            throw new InvalidOperationException($"Item deletion resolves to a directory: {parentPath}/{originalName}");
        }
        await fileStore.DeleteFileAsync(FileStoreArea.Gallery, key, cancellationToken);

        await context.PendingItemDeletions
            .Where(entry => entry.ParentPath == parentPath && entry.OriginalName == originalName)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public Task<bool> ItemFileExistsAsync(
        string parentPath,
        string originalName,
        CancellationToken cancellationToken = default) =>
        fileStore.FileExistsAsync(
            FileStoreArea.Gallery,
            ItemFileKey(parentPath, originalName),
            cancellationToken);

    public Task<Stream> OpenItemFileAsync(
        string parentPath,
        string originalName,
        CancellationToken cancellationToken = default) =>
        fileStore.OpenReadAsync(
            FileStoreArea.Gallery,
            ItemFileKey(parentPath, originalName),
            cancellationToken);

    public Task<string> ReadItemFileTextAsync(
        string parentPath,
        string originalName,
        CancellationToken cancellationToken = default) =>
        fileStore.ReadAllTextAsync(
            FileStoreArea.Gallery,
            ItemFileKey(parentPath, originalName),
            cancellationToken);

    public Task WriteItemFileTextAsync(
        string parentPath,
        string originalName,
        string content,
        CancellationToken cancellationToken = default) =>
        fileStore.WriteAllTextAsync(
            FileStoreArea.Gallery,
            ItemFileKey(parentPath, originalName),
            content,
            cancellationToken);

    public async Task<Item> RenameItemAsync(
        ulong itemId,
        string? newName,
        CancellationToken cancellationToken = default)
    {
        var item = await context.Items.AsNoTracking().FirstOrDefaultAsync(value => value.Id == itemId, cancellationToken)
            ?? throw new GalleryItemRenameException("The item was not found.");
        var name = newName?.Trim() ?? string.Empty;
        if (!IsValidItemFileName(name))
        {
            throw new GalleryItemRenameException(
                "Enter a file name up to 255 characters without / \\ : * ? \" < > | or a leading dot.");
        }
        var extension = Path.GetExtension(item.OriginalName);
        if (!string.Equals(Path.GetExtension(name), extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new GalleryItemRenameException(
                string.IsNullOrEmpty(extension)
                    ? "The new name cannot add a file extension."
                    : $"The new name must keep the {extension} extension.");
        }
        if (string.Equals(name, item.OriginalName, StringComparison.Ordinal))
        {
            return item;
        }

        var parentPath = item.ParentPath ?? "root";
        var caseOnly = string.Equals(name, item.OriginalName, StringComparison.OrdinalIgnoreCase);
        if (await context.Items.AnyAsync(
                value => value.ParentPath == item.ParentPath && value.Id != item.Id && value.OriginalName == name,
                cancellationToken))
        {
            throw new GalleryItemRenameException($"This directory already contains an item named '{name}'.");
        }

        var directoryKey = GalleryDirectoryKey(parentPath);
        var source = ItemFileKey(parentPath, item.OriginalName);
        var destination = ItemFileKey(parentPath, name);
        if (!await fileStore.FileExistsAsync(FileStoreArea.Gallery, source, cancellationToken))
        {
            throw new GalleryItemRenameException($"The item file was not found: {parentPath}/{item.OriginalName}");
        }
        // A case-only rename "exists" on case-insensitive stores, so compare listed names exactly instead.
        var destinationTaken = caseOnly
            ? (await fileStore.ListFilesAsync(FileStoreArea.Gallery, directoryKey, cancellationToken: cancellationToken))
                .Any(key => string.Equals(key[(key.LastIndexOf('/') + 1)..], name, StringComparison.Ordinal))
            : await fileStore.FileExistsAsync(FileStoreArea.Gallery, destination, cancellationToken) ||
              await fileStore.DirectoryExistsAsync(FileStoreArea.Gallery, destination, cancellationToken);
        if (destinationTaken)
        {
            throw new GalleryItemRenameException($"Storage already contains a file named '{name}' in this directory.");
        }

        async Task MoveItemFileAsync(string from, string to)
        {
            if (caseOnly)
            {
                var temporary = JoinKey(directoryKey, $".rename-{Guid.NewGuid():N}{extension}");
                await fileStore.MoveFileAsync(FileStoreArea.Gallery, from, temporary, cancellationToken);
                await fileStore.MoveFileAsync(FileStoreArea.Gallery, temporary, to, cancellationToken);
                return;
            }
            await fileStore.MoveFileAsync(FileStoreArea.Gallery, from, to, cancellationToken);
        }

        await MoveItemFileAsync(source, destination);

        var thumbnailKey = CreateThumbnailKey(parentPath, name);
        var oldThumbnail = $"{item.ThumbnailKey}.png";
        var newThumbnail = $"{thumbnailKey}.png";
        var thumbnailMoved = false;
        try
        {
            if (!string.Equals(item.ThumbnailKey, thumbnailKey, StringComparison.Ordinal) &&
                await fileStore.FileExistsAsync(FileStoreArea.Thumbnails, oldThumbnail, cancellationToken))
            {
                if (await fileStore.FileExistsAsync(FileStoreArea.Thumbnails, newThumbnail, cancellationToken))
                {
                    await fileStore.DeleteFileAsync(FileStoreArea.Thumbnails, newThumbnail, cancellationToken);
                }
                await fileStore.MoveFileAsync(FileStoreArea.Thumbnails, oldThumbnail, newThumbnail, cancellationToken);
                thumbnailMoved = true;
            }

            var executionStrategy = context.Database.CreateExecutionStrategy();
            await executionStrategy.ExecuteAsync(async () =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
                await context.Items
                    .Where(value => value.Id == item.Id)
                    .ExecuteUpdateAsync(
                        update => update
                            .SetProperty(value => value.OriginalName, name)
                            .SetProperty(value => value.ThumbnailKey, thumbnailKey),
                        cancellationToken);
                await context.Shortcuts
                    .Where(shortcut => shortcut.OriginalId == item.Id)
                    .ExecuteUpdateAsync(
                        update => update.SetProperty(shortcut => shortcut.OriginalName, name),
                        cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            });
        }
        catch
        {
            if (thumbnailMoved)
            {
                await fileStore.MoveFileAsync(FileStoreArea.Thumbnails, newThumbnail, oldThumbnail, CancellationToken.None);
            }
            await MoveItemFileAsync(destination, source);
            throw;
        }

        await PersistPathAsync(parentPath, cancellationToken);
        item.OriginalName = name;
        item.ThumbnailKey = thumbnailKey;
        return item;
    }

    public static bool IsValidItemFileName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        fileName.Length <= 255 &&
        fileName == fileName.Trim() &&
        !fileName.StartsWith('.') &&
        !fileName.Any(character =>
            char.IsControl(character) ||
            character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*');

    private static string CreateThumbnailKey(string parentPath, string originalName)
    {
        var normalizedPath = parentPath == "root" ? originalName : $"{parentPath}/{originalName}";
        return Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedPath)));
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
            var source = ItemFileKey(sourceParentPath, item.OriginalName);
            var destination = ItemFileKey(targetPath, item.OriginalName);
            return (Item: item, SourceParentPath: sourceParentPath, Source: source, Destination: destination);
        }).ToList();
        foreach (var fileMove in fileMoves)
        {
            if (!await fileStore.FileExistsAsync(FileStoreArea.Gallery, fileMove.Source, cancellationToken))
            {
                if (await fileStore.DirectoryExistsAsync(FileStoreArea.Gallery, fileMove.Source, cancellationToken))
                {
                    throw new GalleryMoveException(
                        $"The image source resolves to a directory: {fileMove.SourceParentPath}/{fileMove.Item.OriginalName}");
                }
                throw new GalleryMoveException(
                    $"The image source file was not found: {fileMove.SourceParentPath}/{fileMove.Item.OriginalName}");
            }
            if (await fileStore.FileExistsAsync(FileStoreArea.Gallery, fileMove.Destination, cancellationToken) ||
                await fileStore.DirectoryExistsAsync(FileStoreArea.Gallery, fileMove.Destination, cancellationToken))
            {
                throw new GalleryMoveException(
                    $"The destination already contains a file named '{fileMove.Item.OriginalName}'.");
            }
        }

        var movedFiles = new List<(string Source, string Destination)>();
        try
        {
            foreach (var fileMove in fileMoves)
            {
                await fileStore.MoveFileAsync(
                    FileStoreArea.Gallery,
                    fileMove.Source,
                    fileMove.Destination,
                    cancellationToken);
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
                    await fileStore.MoveFileAsync(
                        FileStoreArea.Gallery,
                        movedFile.Destination,
                        movedFile.Source,
                        CancellationToken.None);
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

        var sourceDirectory = GalleryDirectoryKey(oldPath);
        var destinationDirectory = GalleryDirectoryKey(newPath);
        if (!await fileStore.DirectoryExistsAsync(FileStoreArea.Gallery, sourceDirectory, cancellationToken))
        {
            throw new DirectoryNotFoundException($"Gallery directory does not exist: {oldPath}");
        }
        foreach (var subtreePath in subtreePaths)
        {
            var subtreeDirectory = GalleryDirectoryKey(subtreePath);
            if (!await fileStore.DirectoryExistsAsync(FileStoreArea.Gallery, subtreeDirectory, cancellationToken))
            {
                throw new DirectoryNotFoundException($"Gallery directory does not exist: {subtreePath}");
            }
        }
        if (await fileStore.DirectoryExistsAsync(FileStoreArea.Gallery, destinationDirectory, cancellationToken) ||
            await fileStore.FileExistsAsync(FileStoreArea.Gallery, destinationDirectory, cancellationToken))
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

        await fileStore.MoveDirectoryAsync(
            FileStoreArea.Gallery,
            sourceDirectory,
            destinationDirectory,
            cancellationToken);
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
                await fileStore.MoveDirectoryAsync(
                    FileStoreArea.Gallery,
                    destinationDirectory,
                    sourceDirectory,
                    CancellationToken.None);
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
        await PersistDirectoryShortcutsAsync(cancellationToken);

        return newPath;
    }

    public async Task DeletePathDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.Equals(path, "root", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The root gallery path cannot be deleted.");
        }

        var directory = GalleryDirectoryKey(path);
        if (await fileStore.FileExistsAsync(FileStoreArea.Gallery, directory, cancellationToken))
        {
            throw new InvalidOperationException($"Path deletion resolves to a file: {path}");
        }
        await fileStore.DeleteDirectoryAsync(
            FileStoreArea.Gallery,
            directory,
            recursive: true,
            cancellationToken);

        await PersistDirectoryShortcutsAsync(cancellationToken);
        await context.PendingPathDeletions
            .Where(entry => entry.Path == path)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static string GalleryDirectoryKey(string path)
    {
        if (string.Equals(path, "root", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/'))
        {
            throw new InvalidDataException($"Invalid gallery path: {path}");
        }

        return path.TrimEnd('/');
    }

    private static string ItemFileKey(string parentPath, string originalName)
    {
        if (string.IsNullOrWhiteSpace(originalName) ||
            originalName is "." or ".." ||
            originalName.Contains('/') ||
            originalName.Contains('\\') ||
            originalName.Any(char.IsControl))
        {
            throw new InvalidDataException($"Invalid image filename: {originalName}");
        }

        return JoinKey(GalleryDirectoryKey(parentPath), originalName);
    }

    private static string ResolveBlogFileKey(string sourceFilename)
    {
        if (string.IsNullOrWhiteSpace(sourceFilename) ||
            sourceFilename.Contains('/') ||
            sourceFilename.Contains('\\') ||
            !string.Equals(Path.GetExtension(sourceFilename), ".md", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Invalid blog source filename: {sourceFilename}");
        }
        return sourceFilename;
    }

    private static string JoinKey(string directoryKey, string name) =>
        string.IsNullOrEmpty(directoryKey) ? name : $"{directoryKey.TrimEnd('/')}/{name}";

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

public sealed class GalleryItemRenameException(string message) : Exception(message)
{
}
