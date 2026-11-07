using Markdig;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Website.Data;
using Website.Models;

namespace Website.Pages;

public class BlogPostModel : PageModel
{
    private static readonly MarkdownPipeline MarkdownPipeline =
        new MarkdownPipelineBuilder().DisableHtml().Build();

    private readonly AppDbContext _context;

    public BlogPostModel(AppDbContext context)
    {
        _context = context;
    }

    public Blog Post { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(ulong id)
    {
        var post = await _context.Blogs
            .AsNoTracking()
            .FirstOrDefaultAsync(blog => blog.Id == id);

        if (post is null)
        {
            return NotFound();
        }

        Post = post;
        return Page();
    }

    public string RenderMarkdown(string? text) =>
        Markdig.Markdown.ToHtml(text ?? string.Empty, MarkdownPipeline);
}
