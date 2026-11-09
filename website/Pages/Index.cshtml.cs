using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Website.Data;
using Website.Models;
using Website.Services;

namespace website.Pages;

public class IndexModel(
    AppDbContext context,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    FilesystemPersistenceService filesystemPersistence,
    IAntiforgery antiforgery) : PageModel
{
    public string AboutMarkdown { get; private set; } = string.Empty;
    public bool AdminEnabled => AdminAccess.IsAdmin(
        configuration,
        User,
        AdminAccess.IsDevelopmentPasswordBypassEnabled(configuration, environment));

    public async Task OnGetAsync()
    {
        AboutMarkdown = await context.About
            .AsNoTracking()
            .Where(entry => entry.Id == 1)
            .Select(entry => entry.Description)
            .FirstOrDefaultAsync(HttpContext.RequestAborted) ?? string.Empty;
    }

    public string RenderMarkdown(string? markdown) =>
        BlogMarkdownRenderer.Render(markdown);

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

        return Content(RenderMarkdown(markdown), "text/html");
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

        var about = await context.About.FirstOrDefaultAsync(
            entry => entry.Id == 1,
            HttpContext.RequestAborted);
        if (about is null)
        {
            about = new AboutContent { Id = 1 };
            context.About.Add(about);
        }

        var previousMarkdown = about.Description;
        about.Description = markdown ?? string.Empty;
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        await filesystemPersistence.PersistAboutAsync(HttpContext.RequestAborted);
        await filesystemPersistence.DeleteUnreferencedAboutAssetsAsync(
            previousMarkdown,
            about.Description,
            uploadedImageUrls ?? [],
            HttpContext.RequestAborted);

        return new JsonResult(new { html = RenderMarkdown(about.Description) });
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
            var imageUrl = await filesystemPersistence.SaveAboutAssetAsync(image, HttpContext.RequestAborted);
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
