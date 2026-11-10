using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Website.Services;

namespace Website.Pages;

public sealed class CommissionsModel(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    IFileStore fileStore,
    FilesystemPersistenceService filesystemPersistence,
    IAntiforgery antiforgery) : PageModel
{
    private const string MarkdownFile = "commissions.md";
    public string Markdown { get; private set; } = string.Empty;
    public bool AdminEnabled => AdminAccess.IsAdmin(
        configuration, User, AdminAccess.IsDevelopmentPasswordBypassEnabled(configuration, environment));

    public async Task OnGetAsync() => Markdown = await ReadMarkdownAsync();

    public async Task<IActionResult> OnPostPreviewAsync([FromForm] string? markdown)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }
        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }
        return Content(BlogMarkdownRenderer.Render(markdown), "text/html");
    }

    public async Task<IActionResult> OnPostSaveAsync(
        [FromForm] string? markdown,
        [FromForm] string[]? uploadedImageUrls)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }
        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        var previousMarkdown = await ReadMarkdownAsync();
        var currentMarkdown = markdown ?? string.Empty;
        await fileStore.WriteAllTextAsync(
            FileStoreArea.Commissions, MarkdownFile, currentMarkdown, HttpContext.RequestAborted);
        await filesystemPersistence.DeleteUnreferencedPageAssetsAsync(
            FileStoreArea.Commissions, previousMarkdown, currentMarkdown,
            uploadedImageUrls ?? [], HttpContext.RequestAborted);
        return new JsonResult(new { html = BlogMarkdownRenderer.Render(currentMarkdown) });
    }

    public async Task<IActionResult> OnPostUploadImageAsync([FromForm] IFormFile? image)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }
        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }
        if (image is null)
        {
            return BadRequest("Choose an image to upload.");
        }

        try
        {
            var imageUrl = await filesystemPersistence.SavePageAssetAsync(
                FileStoreArea.Commissions, image, HttpContext.RequestAborted);
            return new JsonResult(new
            {
                url = imageUrl,
                fileName = Path.GetFileName(image.FileName.Replace('\\', '/'))
            });
        }
        catch (InvalidDataException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    private async Task<string> ReadMarkdownAsync() =>
        await fileStore.FileExistsAsync(FileStoreArea.Commissions, MarkdownFile, HttpContext.RequestAborted)
            ? await fileStore.ReadAllTextAsync(FileStoreArea.Commissions, MarkdownFile, HttpContext.RequestAborted)
            : string.Empty;

    private async Task<bool> IsAntiForgeryTokenValidAsync()
    {
        try
        {
            await antiforgery.ValidateRequestAsync(HttpContext);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }
}
