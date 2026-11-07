using Markdig;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using Website.Data;
using Website.Models;

namespace Website.Pages;

public class BlogModel : PageModel
{
    private static readonly MarkdownPipeline MarkdownPipeline =
        new MarkdownPipelineBuilder().DisableHtml().Build();
    private static readonly Regex LeadingTitlePattern =
        new(@"^ {0,3}#[ \t]+[^\r\n]+(?:\r?\n|$)", RegexOptions.Compiled);

    private readonly AppDbContext _context;

    public BlogModel(AppDbContext context)
    {
        _context = context;
    }

    public IReadOnlyList<Blog> Posts { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Posts = await _context.Blogs
            .AsNoTracking()
            .OrderByDescending(blog => blog.Timestamp)
            .ThenByDescending(blog => blog.Id)
            .ToListAsync();
    }

    public string RenderExcerpt(string? text)
    {
        var excerpt = text ?? string.Empty;
        if (LeadingTitlePattern.IsMatch(excerpt))
        {
            excerpt = LeadingTitlePattern.Replace(excerpt, string.Empty, 1);
        }

        return Markdig.Markdown.ToHtml(excerpt, MarkdownPipeline);
    }
}
