using Markdig;
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
    IAntiforgery antiforgery) : PageModel
{
    private static readonly MarkdownPipeline MarkdownPipeline =
        new MarkdownPipelineBuilder().DisableHtml().Build();

    public string AboutMarkdown { get; private set; } = string.Empty;
    public bool AdminEnabled => AdminAccess.IsAdmin(configuration, User);

    public async Task OnGetAsync()
    {
        AboutMarkdown = await context.About
            .AsNoTracking()
            .Where(entry => entry.Id == 1)
            .Select(entry => entry.Description)
            .FirstOrDefaultAsync(HttpContext.RequestAborted) ?? string.Empty;
    }

    public string RenderMarkdown(string? markdown) =>
        Markdig.Markdown.ToHtml(markdown ?? string.Empty, MarkdownPipeline);

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

    public async Task<IActionResult> OnPostSaveAsync([FromForm] string? markdown)
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

        about.Description = markdown ?? string.Empty;
        await context.SaveChangesAsync(HttpContext.RequestAborted);

        return new JsonResult(new { html = RenderMarkdown(about.Description) });
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
