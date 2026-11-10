using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Markdig;
using System.IO;
using System.Text;
using Website.Data;
using Website.Models;
using Website.Services;

namespace Website.Pages;

public class ImageModel : PageModel
{
    private static readonly HashSet<string> TextFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".mdown", ".mkd", ".rst", ".adoc", ".asciidoc", ".tex",
        ".c", ".h", ".cc", ".hh", ".cpp", ".hpp", ".cxx", ".hxx", ".inl", ".inc",
        ".cs", ".csx", ".java", ".kt", ".kts", ".scala", ".sc", ".groovy", ".gvy",
        ".go", ".rs", ".py", ".pyi", ".pyw", ".pyx", ".pxd", ".pxi", ".rb", ".rake", ".gemspec",
        ".php", ".phtml", ".phar", ".swift", ".m", ".mm", ".r", ".rmd", ".jl",
        ".pl", ".pm", ".t", ".lua", ".ex", ".exs", ".erl", ".hrl", ".hs", ".lhs", ".clj", ".cljs",
        ".cljc", ".edn", ".elm", ".dart", ".fs", ".fsi", ".fsx", ".vb", ".vbs",
        ".js", ".mjs", ".cjs", ".jsx", ".ts", ".mts", ".cts", ".tsx", ".vue", ".svelte",
        ".html", ".htm", ".xhtml", ".css", ".scss", ".sass", ".less", ".styl",
        ".xml", ".xsd", ".xsl", ".xslt", ".wsdl", ".xaml", ".svg", ".plist",
        ".json", ".jsonc", ".json5", ".jsonl", ".ndjson", ".yaml", ".yml", ".toml",
        ".ini", ".cfg", ".conf", ".config", ".properties",
        ".sql", ".psql", ".graphql", ".gql", ".proto", ".thrift",
        ".sh", ".bash", ".zsh", ".fish", ".ps1", ".psm1", ".psd1", ".bat", ".cmd",
        ".dockerfile", ".containerfile", ".make", ".mk", ".cmake", ".gradle", ".gradle.kts",
        ".tf", ".tfvars", ".hcl", ".nix", ".bzl", ".bazel", ".lock",
        ".csv", ".tsv", ".log", ".diff", ".patch", ".gitignore", ".gitattributes"
    };
    private static readonly HashSet<string> MarkdownFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdown", ".mkd"
    };
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly IAntiforgery _antiforgery;
    private readonly AdminOnlyTagsProvider _adminOnlyTagsProvider;
    private readonly FilesystemPersistenceService _filesystemPersistence;

    public ImageModel(
        AppDbContext context,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IAntiforgery antiforgery,
        AdminOnlyTagsProvider adminOnlyTagsProvider,
        FilesystemPersistenceService filesystemPersistence)
    {
        _context = context;
        _configuration = configuration;
        _environment = environment;
        _antiforgery = antiforgery;
        _adminOnlyTagsProvider = adminOnlyTagsProvider;
        _filesystemPersistence = filesystemPersistence;
    }

    public Item Item { get; private set; } = null!;
    public string SourceUrl { get; private set; } = string.Empty;
    public string DownloadUrl { get; private set; } = string.Empty;
    public string? TextContent { get; private set; }
    public string GalleryUrl { get; private set; } = "/Gallery";
    public ulong? CurrentShortcutId { get; private set; }
    public string TagsInput { get; set; } = string.Empty;
    public string? TagError { get; private set; }
    public string RenderDescription(string? markdown) =>
        SafeMarkdownRenderer.Render(markdown);
    public bool IsTextDocument => TextFileExtensions.Contains(Path.GetExtension(Item.OriginalName));
    public bool IsMarkdownDocument => MarkdownFileExtensions.Contains(Path.GetExtension(Item.OriginalName));

    public bool AdminEnabled => AdminAccess.IsAdmin(
        _configuration,
        User,
        AdminAccess.IsDevelopmentPasswordBypassEnabled(_configuration, _environment));
    public HashSet<string> AdminOnlyTags { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public IEnumerable<string> Tags =>
        Item.TagLinks.Select(link => link.Tag.Name)
            .Where(tag => AdminEnabled || !AdminOnlyTags.Contains(tag))
            .OrderBy(name => name, StringComparer.Ordinal);

    public async Task<IActionResult> OnGetAsync(ulong id, ulong? shortcutId)
    {
        if (!await LoadItemAsync(id))
        {
            return NotFound();
        }
        if (shortcutId.HasValue)
        {
            var shortcut = await _context.Shortcuts
                .Where(shortcut => shortcut.Id == shortcutId.Value && shortcut.OriginalId == id)
                .Select(shortcut => new { shortcut.Id, shortcut.ShortcutPath })
                .FirstOrDefaultAsync(HttpContext.RequestAborted);
            if (shortcut is not null)
            {
                CurrentShortcutId = shortcut.Id;
                GalleryUrl = CreateGalleryUrl(shortcut.ShortcutPath);
            }
        }
        if (!await IsItemVisibleAsync())
        {
            return NotFound();
        }

        if (IsTextDocument)
        {
            if (!await _filesystemPersistence.ItemFileExistsAsync(
                    Item.ParentPath ?? "root",
                    Item.OriginalName,
                    HttpContext.RequestAborted))
            {
                return NotFound();
            }
            TextContent = await _filesystemPersistence.ReadItemFileTextAsync(
                Item.ParentPath ?? "root",
                Item.OriginalName,
                HttpContext.RequestAborted);
        }

        TagsInput = string.Join(", ", Tags);
        return Page();
    }

    public async Task<IActionResult> OnGetFileAsync(ulong id)
    {
        if (!await LoadItemAsync(id))
        {
            return NotFound();
        }
        if (Item.IsImage)
        {
            return NotFound();
        }

        if (!await IsItemVisibleAsync())
        {
            return NotFound();
        }

        if (!await _filesystemPersistence.ItemFileExistsAsync(
                Item.ParentPath ?? "root",
                Item.OriginalName,
                HttpContext.RequestAborted))
        {
            return NotFound();
        }

        var stream = await _filesystemPersistence.OpenItemFileAsync(
            Item.ParentPath ?? "root",
            Item.OriginalName,
            HttpContext.RequestAborted);
        return File(stream, "application/octet-stream", Item.OriginalName);
    }

    public async Task<IActionResult> OnGetSourceAsync(ulong id)
    {
        if (!await LoadItemAsync(id) || !IsTextDocument)
        {
            return NotFound();
        }
        if (!await IsItemVisibleAsync())
        {
            return NotFound();
        }

        if (!await _filesystemPersistence.ItemFileExistsAsync(
                Item.ParentPath ?? "root",
                Item.OriginalName,
                HttpContext.RequestAborted))
        {
            return NotFound();
        }

        var content = await _filesystemPersistence.ReadItemFileTextAsync(
            Item.ParentPath ?? "root",
            Item.OriginalName,
            HttpContext.RequestAborted);
        return Content(content, "text/plain; charset=utf-8");
    }

    public async Task<IActionResult> OnPostSaveFileContentAsync(
        ulong id,
        [FromForm] string? content)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        if (!await LoadItemAsync(id) || !IsTextDocument)
        {
            return NotFound();
        }

        if (!await _filesystemPersistence.ItemFileExistsAsync(
                Item.ParentPath ?? "root",
                Item.OriginalName,
                HttpContext.RequestAborted))
        {
            return NotFound();
        }

        await _filesystemPersistence.WriteItemFileTextAsync(
            Item.ParentPath ?? "root",
            Item.OriginalName,
            content ?? string.Empty,
            HttpContext.RequestAborted);

        return RedirectToPage("/Image", new { id });
    }

    public async Task<IActionResult> OnPostPreviewFileContentAsync(
        ulong id,
        [FromForm] string? content)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        if (!await LoadItemAsync(id) || !IsMarkdownDocument)
        {
            return NotFound();
        }

        return Content(RenderDescription(content), "text/html");
    }

    public async Task<IActionResult> OnGetSearchPathsAsync(ulong id, [FromQuery] string? query)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        var sourceItem = await _context.Items
            .Where(item => item.Id == id)
            .Select(item => new { item.ParentPath })
            .FirstOrDefaultAsync(HttpContext.RequestAborted);
        if (sourceItem is null)
        {
            return NotFound();
        }
        var sourceParentPath = sourceItem.ParentPath ?? "root";

        var search = query?.Trim();
        if (string.IsNullOrEmpty(search) || search.Length > 256)
        {
            return new JsonResult(Array.Empty<object>());
        }

        var linkedPaths = await _context.Shortcuts
            .Where(shortcut => shortcut.OriginalId == id)
            .Select(shortcut => shortcut.ShortcutPath)
            .ToListAsync(HttpContext.RequestAborted);
        var paths = await _context.Paths
            .Where(path => path.Path != sourceParentPath && path.Path.Contains(search))
            .OrderBy(path => path.Path)
            .Select(path => path.Path)
            .Take(50)
            .ToListAsync(HttpContext.RequestAborted);

        return new JsonResult(paths.Select(path => new
        {
            path,
            displayPath = path == "root" ? "/" : path,
            alreadyLinked = linkedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)
        }));
    }

    public async Task<IActionResult> OnGetSearchMovePathsAsync(
        ulong id,
        [FromQuery] ulong? shortcutId,
        [FromQuery] string? query)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        var item = await _context.Items
            .Where(entry => entry.Id == id)
            .Select(entry => new { entry.ParentPath })
            .FirstOrDefaultAsync(HttpContext.RequestAborted);
        if (item is null)
        {
            return NotFound();
        }

        var currentShortcutPath = (string?)null;
        if (shortcutId.HasValue)
        {
            var shortcut = await _context.Shortcuts
                .Where(shortcut => shortcut.Id == shortcutId.Value && shortcut.OriginalId == id)
                .Select(shortcut => new { shortcut.ShortcutPath })
                .FirstOrDefaultAsync(HttpContext.RequestAborted);
            if (shortcut is null)
            {
                return NotFound();
            }
            currentShortcutPath = shortcut.ShortcutPath ?? "root";
        }

        var search = query?.Trim();
        if (string.IsNullOrEmpty(search) || search.Length > 256)
        {
            return new JsonResult(Array.Empty<object>());
        }

        var originalPath = item.ParentPath ?? "root";
        var existingShortcutPaths = await _context.Shortcuts
            .Where(shortcut => shortcut.OriginalId == id &&
                (!shortcutId.HasValue || shortcut.Id != shortcutId.Value))
            .Select(shortcut => shortcut.ShortcutPath)
            .ToListAsync(HttpContext.RequestAborted);
        var paths = await _context.Paths
            .Where(path => path.Path.Contains(search))
            .OrderBy(path => path.Path)
            .Select(path => path.Path)
            .Take(50)
            .ToListAsync(HttpContext.RequestAborted);
        var currentPath = currentShortcutPath ?? originalPath;

        return new JsonResult(paths.Select(path => new
        {
            path,
            displayPath = path == "root" ? "/" : path,
            unavailable = string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase) ||
                existingShortcutPaths.Contains(path, StringComparer.OrdinalIgnoreCase) ||
                (shortcutId.HasValue && string.Equals(path, originalPath, StringComparison.OrdinalIgnoreCase))
        }));
    }

    public async Task<IActionResult> OnGetShortcutLocationsAsync(ulong id)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await _context.Items.AnyAsync(item => item.Id == id, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        var shortcuts = await _context.Shortcuts
            .Where(shortcut => shortcut.OriginalId == id)
            .OrderBy(shortcut => shortcut.ShortcutPath)
            .Select(shortcut => new
            {
                id = shortcut.Id.ToString(),
                path = shortcut.ShortcutPath,
                displayPath = shortcut.ShortcutPath == "root" ? "/" : shortcut.ShortcutPath
            })
            .ToListAsync(HttpContext.RequestAborted);

        return new JsonResult(shortcuts);
    }

    public async Task<IActionResult> OnPostCreateShortcutAsync(
        ulong id,
        [FromForm] string? targetPath)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        var item = await _context.Items.FirstOrDefaultAsync(
            entry => entry.Id == id,
            HttpContext.RequestAborted);
        if (item is null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(targetPath) ||
            targetPath.Length > 1024 ||
            !await _context.Paths.AnyAsync(
                path => path.Path == targetPath,
                HttpContext.RequestAborted))
        {
            return BadRequest("Select an existing directory.");
        }

        var alreadyExists = await _context.Shortcuts.AnyAsync(
            shortcut => shortcut.OriginalId == id && shortcut.ShortcutPath == targetPath,
            HttpContext.RequestAborted);
        if (alreadyExists)
        {
            return BadRequest("This item is already linked to that directory.");
        }

        _context.Shortcuts.Add(new Shortcut
        {
            OriginalId = item.Id,
            OriginalName = item.OriginalName,
            ParentPath = item.ParentPath,
            ShortcutPath = targetPath
        });
        await _context.SaveChangesAsync(HttpContext.RequestAborted);

        return new JsonResult(new { saved = true });
    }

    public async Task<IActionResult> OnPostRenameItemAsync(ulong id, [FromForm] string? newName)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        try
        {
            var item = await _filesystemPersistence.RenameItemAsync(id, newName, HttpContext.RequestAborted);
            return new JsonResult(new { id = item.Id, name = item.OriginalName, thumbnailKey = item.ThumbnailKey });
        }
        catch (GalleryItemRenameException error)
        {
            return BadRequest(error.Message);
        }
    }

    public async Task<IActionResult> OnPostMoveToAsync(
        ulong id,
        [FromForm] ulong? shortcutId,
        [FromForm] string? targetPath)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }
        if (string.IsNullOrWhiteSpace(targetPath) ||
            targetPath.Length > 1024 ||
            !await _context.Paths.AnyAsync(path => path.Path == targetPath, HttpContext.RequestAborted))
        {
            return BadRequest("Select an existing directory.");
        }

        if (shortcutId.HasValue)
        {
            var shortcut = await _context.Shortcuts
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    entry => entry.Id == shortcutId.Value && entry.OriginalId == id,
                    HttpContext.RequestAborted);
            if (shortcut is null)
            {
                return NotFound();
            }

            try
            {
                await _filesystemPersistence.MoveEntriesAsync(
                    Array.Empty<Item>(),
                    [shortcut],
                    targetPath,
                    HttpContext.RequestAborted);
            }
            catch (GalleryMoveException error)
            {
                return StatusCode(StatusCodes.Status409Conflict, error.Message);
            }
        }
        else
        {
            var item = await _context.Items
                .AsNoTracking()
                .FirstOrDefaultAsync(entry => entry.Id == id, HttpContext.RequestAborted);
            if (item is null)
            {
                return NotFound();
            }

            try
            {
                await _filesystemPersistence.MoveEntriesAsync(
                    [item],
                    Array.Empty<Shortcut>(),
                    targetPath,
                    HttpContext.RequestAborted);
            }
            catch (GalleryMoveException error)
            {
                return StatusCode(StatusCodes.Status409Conflict, error.Message);
            }
        }

        return new JsonResult(new
        {
            redirectPath = CreateGalleryUrl(targetPath)
        });
    }

    public async Task<IActionResult> OnPostDeleteShortcutsAsync(
        ulong id,
        [FromForm] List<ulong>? shortcutIds)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        if (shortcutIds is null || shortcutIds.Count == 0)
        {
            return BadRequest("Select at least one shortcut to remove.");
        }

        var uniqueIds = shortcutIds.Distinct().ToArray();
        var shortcuts = await _context.Shortcuts
            .Where(shortcut => shortcut.OriginalId == id && uniqueIds.Contains(shortcut.Id))
            .ToListAsync(HttpContext.RequestAborted);
        if (shortcuts.Count != uniqueIds.Length)
        {
            return BadRequest("One or more selected shortcuts are no longer available.");
        }

        _context.Shortcuts.RemoveRange(shortcuts);
        await _context.SaveChangesAsync(HttpContext.RequestAborted);

        return new JsonResult(new
        {
            deletedIds = shortcuts.Select(shortcut => shortcut.Id.ToString()).ToArray()
        });
    }

    public async Task<IActionResult> OnPostDeleteItemAsync(
        ulong id,
        [FromForm] ulong? shortcutId)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        if (shortcutId.HasValue)
        {
            return BadRequest("Delete the current shortcut instead of the original item.");
        }

        var item = await _context.Items
            .AsNoTracking()
            .FirstOrDefaultAsync(entry => entry.Id == id, HttpContext.RequestAborted);
        if (item is null)
        {
            return NotFound();
        }

        await EnsurePendingItemDeletionsTableAsync();
        _context.PendingItemDeletions.Add(new PendingItemDeletion
        {
            ParentPath = item.ParentPath ?? "root",
            OriginalName = item.OriginalName
        });
        await _context.SaveChangesAsync(HttpContext.RequestAborted);

        await _context.Items
            .Where(entry => entry.Id == id)
            .ExecuteDeleteAsync(HttpContext.RequestAborted);
        var parentPath = item.ParentPath ?? "root";
        await _filesystemPersistence.DeleteItemFileAsync(
            parentPath,
            item.OriginalName,
            HttpContext.RequestAborted);
        await _filesystemPersistence.PersistPathAsync(parentPath, HttpContext.RequestAborted);

        return new JsonResult(new
        {
            redirectPath = CreateGalleryUrl(item.ParentPath ?? "root"),
            deletedShortcut = false
        });
    }

    public async Task<IActionResult> OnPostDeleteCurrentShortcutAsync(
        ulong id,
        [FromForm] ulong? shortcutId)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }
        if (!shortcutId.HasValue)
        {
            return BadRequest("No shortcut was selected.");
        }

        var shortcut = await _context.Shortcuts
            .FirstOrDefaultAsync(
                entry => entry.Id == shortcutId.Value && entry.OriginalId == id,
                HttpContext.RequestAborted);
        if (shortcut is null)
        {
            return NotFound();
        }

        var redirectPath = CreateGalleryUrl(shortcut.ShortcutPath);
        _context.Shortcuts.Remove(shortcut);
        await _context.SaveChangesAsync(HttpContext.RequestAborted);

        return new JsonResult(new
        {
            redirectPath,
            deletedShortcut = true
        });
    }

    public async Task<IActionResult> OnPostSaveTagsAsync(ulong id, [FromForm] string? tags)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        if (!await LoadItemAsync(id))
        {
            return NotFound();
        }
        AdminOnlyTags = await _adminOnlyTagsProvider.GetTagsAsync(HttpContext.RequestAborted);
        if (!AdminEnabled &&
            (Item.TagLinks.Any(link => AdminOnlyTags.Contains(link.Tag.Name)) ||
             await IsInAdminOnlyPathAsync(Item.ParentPath)))
        {
            return NotFound();
        }

        TagsInput = tags ?? string.Empty;
        if (!TagNameNormalizer.TryNormalize(TagsInput, out var normalizedTags, out var error))
        {
            TagError = error;
            return Page();
        }
        normalizedTags = normalizedTags
            .Concat(DatePathTags.ForPath(Item.ParentPath))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToList();

        var tagEntities = await _context.Tags
            .Where(tag => normalizedTags.Contains(tag.Name))
            .ToListAsync();
        var existingNames = tagEntities.Select(tag => tag.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var tagName in normalizedTags.Where(name => !existingNames.Contains(name)))
        {
            var tag = new Tag { Name = tagName };
            tagEntities.Add(tag);
            _context.Tags.Add(tag);
        }

        var existingLinks = await _context.ItemTags
            .Where(link => link.ItemId == Item.Id)
            .ToListAsync();
        _context.ItemTags.RemoveRange(existingLinks);
        _context.ItemTags.AddRange(tagEntities.Select(tag => new ItemTag
        {
            ItemId = Item.Id,
            Tag = tag
        }));
        await _context.SaveChangesAsync(HttpContext.RequestAborted);
        await _filesystemPersistence.PersistPathAsync(
            Item.ParentPath ?? "root",
            HttpContext.RequestAborted);

        return RedirectToPage("/Image", new { id });
    }

    public async Task<IActionResult> OnPostPreviewDescriptionAsync(
        ulong id,
        [FromForm] string? description)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }
        if (!await _context.Items.AnyAsync(item => item.Id == id, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        return Content(RenderDescription(description), "text/html");
    }

    public async Task<IActionResult> OnPostSaveDescriptionAsync(
        ulong id,
        [FromForm] string? description)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        var item = await _context.Items
            .FirstOrDefaultAsync(entry => entry.Id == id, HttpContext.RequestAborted);
        if (item is null)
        {
            return NotFound();
        }

        item.Description = string.IsNullOrWhiteSpace(description) ? null : description;
        await _context.SaveChangesAsync(HttpContext.RequestAborted);
        await _filesystemPersistence.PersistPathAsync(
            item.ParentPath ?? "root",
            HttpContext.RequestAborted);

        return new JsonResult(new
        {
            hasDescription = !string.IsNullOrWhiteSpace(item.Description),
            html = RenderDescription(item.Description)
        });
    }

    private async Task<bool> LoadItemAsync(ulong id)
    {
        var item = await _context.Items
            .Include(entry => entry.TagLinks)
            .ThenInclude(link => link.Tag)
            .FirstOrDefaultAsync(entry => entry.Id == id);
        if (item is null)
        {
            return false;
        }

        Item = item;
        if (item.IsImage)
        {
            SourceUrl = FileStoreKeys.GallerySourceUrl(item.ParentPath, item.OriginalName);
            DownloadUrl = SourceUrl;
        }
        else if (IsTextDocument)
        {
            SourceUrl = $"/image/{item.Id}?handler=Source";
            DownloadUrl = $"/image/{item.Id}?handler=File";
        }
        else
        {
            SourceUrl = string.Empty;
            DownloadUrl = $"/image/{item.Id}?handler=File";
        }
        GalleryUrl = CreateGalleryUrl(item.ParentPath ?? "root");
        return true;
    }

    private async Task<bool> IsItemVisibleAsync()
    {
        AdminOnlyTags = await _adminOnlyTagsProvider.GetTagsAsync(HttpContext.RequestAborted);
        return AdminEnabled ||
            (!Item.TagLinks.Any(link => AdminOnlyTags.Contains(link.Tag.Name)) &&
             !await IsInAdminOnlyPathAsync(Item.ParentPath));
    }

    private static string CreateGalleryUrl(string? path)
    {
        var gallerySegments = (path is null or "root" ? string.Empty : path)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString)
            .ToArray();
        return gallerySegments.Length == 0
            ? "/Gallery"
            : "/Gallery/" + string.Join('/', gallerySegments);
    }

    private async Task EnsurePendingItemDeletionsTableAsync()
    {
        await _context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS PendingItemDeletions (
                id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
                parent_path VARCHAR(1024) NOT NULL,
                original_name VARCHAR(255) NOT NULL
            )
            """,
            HttpContext.RequestAborted);
    }

    private async Task<bool> IsAntiForgeryTokenValidAsync()
    {
        try
        {
            await _antiforgery.ValidateRequestAsync(HttpContext);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private async Task<bool> IsInAdminOnlyPathAsync(string? parentPath)
    {
        var hiddenPaths = await _context.Paths
            .Where(path => path.TagLinks.Any(link => AdminOnlyTags.Contains(link.Tag.Name)))
            .Select(path => path.Path)
            .ToListAsync(HttpContext.RequestAborted);

        return hiddenPaths.Any(path =>
            path == "root" ||
            string.Equals(path, parentPath, StringComparison.OrdinalIgnoreCase) ||
            parentPath?.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase) == true);
    }
}
