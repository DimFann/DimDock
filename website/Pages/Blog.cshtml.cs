using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using Website.Data;
using Website.Models;
using Website.Services;

namespace Website.Pages;

public class BlogModel : PageModel
{
    private static readonly Regex LeadingTitlePattern =
        new(@"^ {0,3}#[ \t]+[^\r\n]+(?:\r?\n|$)", RegexOptions.Compiled);

    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly IAntiforgery _antiforgery;
    private readonly FilesystemPersistenceService _filesystemPersistence;
    private const int PageSize = 10;

    public BlogModel(
        AppDbContext context,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IAntiforgery antiforgery,
        FilesystemPersistenceService filesystemPersistence)
    {
        _context = context;
        _configuration = configuration;
        _environment = environment;
        _antiforgery = antiforgery;
        _filesystemPersistence = filesystemPersistence;
    }

    public IReadOnlyList<Blog> Posts { get; private set; } = [];
    [BindProperty(SupportsGet = true, Name = "pageNumber")]
    public int PageNumber { get; set; } = 1;
    public int PageCount { get; private set; } = 1;
    public bool AdminEnabled => AdminAccess.IsAdmin(
        _configuration,
        User,
        AdminAccess.IsDevelopmentPasswordBypassEnabled(_configuration, _environment));

    public async Task OnGetAsync()
    {
        var totalPosts = await _context.Blogs.CountAsync();
        PageCount = Math.Max(1, (int)Math.Ceiling(totalPosts / (double)PageSize));
        PageNumber = Math.Clamp(PageNumber, 1, PageCount);
        Posts = await _context.Blogs
            .AsNoTracking()
            .OrderByDescending(blog => blog.Timestamp)
            .ThenByDescending(blog => blog.Id)
            .Skip((PageNumber - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostDeleteAsync([FromForm] ulong id, [FromForm] int page)
    {
        if (!AdminEnabled)
        {
            return NotFound();
        }

        if (!await IsAntiForgeryTokenValidAsync())
        {
            return BadRequest("Invalid anti-forgery token.");
        }

        var post = await _context.Blogs.FirstOrDefaultAsync(blog => blog.Id == id);
        if (post is null)
        {
            return NotFound();
        }

        _context.Blogs.Remove(post);
        await _context.SaveChangesAsync(HttpContext.RequestAborted);
        _filesystemPersistence.DeleteBlogAssets(post.Id);
        _filesystemPersistence.DeleteBlogFile(post.SourceFilename);

        var remainingPosts = await _context.Blogs.CountAsync(HttpContext.RequestAborted);
        var lastPage = Math.Max(1, (int)Math.Ceiling(remainingPosts / (double)PageSize));
        return RedirectToPage("/Blog", new { pageNumber = Math.Clamp(page, 1, lastPage) });
    }

    public async Task<IActionResult> OnPostCreateAsync(
        [FromForm] string? title,
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

        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();
        if (normalizedTitle.Length > 1024)
        {
            return BadRequest("Titles cannot exceed 1024 characters.");
        }

        var markdown = $"# {normalizedTitle}{Environment.NewLine}{Environment.NewLine}{content?.Trim() ?? string.Empty}";
        if (System.Text.Encoding.UTF8.GetByteCount(markdown) > 65535)
        {
            return BadRequest("The post content is too large.");
        }

        var timestamp = DateTime.UtcNow;
        var sourceFilename = $"UTC_{timestamp:yyyyMMdd_HHmmss}.md";
        while (await _context.Blogs.AnyAsync(
                   blog => blog.SourceFilename == sourceFilename,
                   HttpContext.RequestAborted) ||
               _filesystemPersistence.BlogFileExists(sourceFilename))
        {
            timestamp = timestamp.AddSeconds(1);
            sourceFilename = $"UTC_{timestamp:yyyyMMdd_HHmmss}.md";
        }

        var post = new Blog
        {
            Alias = $"new-post-{Guid.NewGuid():N}",
            SourceFilename = sourceFilename,
            Title = normalizedTitle,
            Timestamp = timestamp,
            Text = markdown
        };
        _context.Blogs.Add(post);
        await _context.SaveChangesAsync(HttpContext.RequestAborted);
        await _filesystemPersistence.PersistBlogAsync(post, HttpContext.RequestAborted);

        return Redirect($"/blog/{Uri.EscapeDataString(post.Alias)}");
    }

    public string RenderExcerpt(string? text)
    {
        var excerpt = text ?? string.Empty;
        if (LeadingTitlePattern.IsMatch(excerpt))
        {
            excerpt = LeadingTitlePattern.Replace(excerpt, string.Empty, 1);
        }

        return BlogMarkdownRenderer.Render(excerpt);
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
