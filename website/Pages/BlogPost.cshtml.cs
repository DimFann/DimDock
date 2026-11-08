using Markdig;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Website.Data;
using Website.Models;
using Website.Services;

namespace Website.Pages;

public class BlogPostModel : PageModel
{
    private static readonly MarkdownPipeline MarkdownPipeline =
        new MarkdownPipelineBuilder().DisableHtml().Build();
    private static readonly Regex LeadingTitlePattern =
        new(@"^ {0,3}#[ \t]+(.+?)(?:[ \t]+#+)?[ \t]*(?:\r?\n|$)", RegexOptions.Compiled);

    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly IAntiforgery _antiforgery;

    public BlogPostModel(
        AppDbContext context,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IAntiforgery antiforgery)
    {
        _context = context;
        _configuration = configuration;
        _environment = environment;
        _antiforgery = antiforgery;
    }

    public Blog Post { get; private set; } = null!;
    public bool AdminEnabled => AdminAccess.IsAdmin(
        _configuration,
        User,
        AdminAccess.IsDevelopmentPasswordBypassEnabled(_configuration, _environment));

    public async Task<IActionResult> OnGetAsync(string slugOrId)
    {
        var query = _context.Blogs
            .AsNoTracking()
            .AsQueryable();

        var post = await query.FirstOrDefaultAsync(blog => blog.Alias == slugOrId);
        if (post is null && ulong.TryParse(slugOrId, out var id))
        {
            post = await query.FirstOrDefaultAsync(blog => blog.Id == id);
        }

        if (post is null)
        {
            return NotFound();
        }

        Post = post;
        return Page();
    }

    public string RenderMarkdown(string? text) =>
        Markdig.Markdown.ToHtml(text ?? string.Empty, MarkdownPipeline);

    public async Task<IActionResult> OnPostPreviewAsync(string slugOrId, [FromForm] string? markdown)
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

    public async Task<IActionResult> OnPostSaveAsync(string slugOrId, [FromForm] string? markdown)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        var post = await FindPostAsync(slugOrId);
        if (post is null)
        {
            return NotFound();
        }

        post.Text = markdown ?? string.Empty;
        post.Title = ExtractTitle(post.Text);
        await _context.SaveChangesAsync();

        return new JsonResult(new
        {
            title = string.IsNullOrWhiteSpace(post.Title) ? "Untitled" : post.Title,
            html = RenderMarkdown(post.Text)
        });
    }

    public async Task<IActionResult> OnPostSaveTitleAsync(string slugOrId, [FromForm] string? title)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        var normalizedTitle = title?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTitle) || normalizedTitle.Length > 1024)
        {
            return BadRequest("Enter a title between 1 and 1024 characters.");
        }

        var post = await FindPostAsync(slugOrId);
        if (post is null)
        {
            return NotFound();
        }

        post.Title = normalizedTitle;
        post.Text = ReplaceLeadingTitle(post.Text, normalizedTitle);
        await _context.SaveChangesAsync(HttpContext.RequestAborted);

        return new JsonResult(new
        {
            title = normalizedTitle,
            html = RenderMarkdown(post.Text)
        });
    }

    public async Task<IActionResult> OnPostDeleteAsync(string slugOrId)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        var post = await FindPostAsync(slugOrId);
        if (post is null)
        {
            return NotFound();
        }

        _context.Blogs.Remove(post);
        await _context.SaveChangesAsync(HttpContext.RequestAborted);
        return RedirectToPage("/Blog");
    }

    private async Task<Blog?> FindPostAsync(string slugOrId)
    {
        var post = await _context.Blogs.FirstOrDefaultAsync(blog => blog.Alias == slugOrId);
        if (post is null && ulong.TryParse(slugOrId, out var id))
        {
            post = await _context.Blogs.FirstOrDefaultAsync(blog => blog.Id == id);
        }

        return post;
    }

    private static string? ExtractTitle(string text)
    {
        var match = LeadingTitlePattern.Match(text);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string ReplaceLeadingTitle(string? markdown, string title)
    {
        var text = markdown ?? string.Empty;
        if (LeadingTitlePattern.IsMatch(text))
        {
            return LeadingTitlePattern.Replace(text, $"# {title}{Environment.NewLine}", 1);
        }

        return $"# {title}{Environment.NewLine}{Environment.NewLine}{text}";
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
}
