using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Text.RegularExpressions;

namespace Website.Services;

public static class SafeMarkdownRenderer
{
    internal static readonly MarkdownPipeline Pipeline = CreatePipeline();

    public static string Render(string? markdown) =>
        Markdown.ToHtml(markdown ?? string.Empty, Pipeline);

    private static MarkdownPipeline CreatePipeline()
    {
        var builder = new MarkdownPipelineBuilder().DisableHtml().UseSoftlineBreakAsHardlineBreak();
        builder.InlineParsers.Insert(0, new BreakInlineParser());
        builder.Extensions.Add(new HiddenTextExtension());
        return builder.Build();
    }

    private sealed class HiddenTextExtension : IMarkdownExtension
    {
        public void Setup(MarkdownPipelineBuilder pipeline) { }

        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
        {
            if (renderer is HtmlRenderer htmlRenderer)
            {
                htmlRenderer.ObjectRenderers.Insert(0, new HiddenTextRenderer());
            }
        }
    }

    private sealed class HiddenTextRenderer : CodeBlockRenderer
    {
        protected override void Write(HtmlRenderer renderer, CodeBlock block)
        {
            if (block is not FencedCodeBlock { Info: "hidden" })
            {
                base.Write(renderer, block);
                return;
            }

            renderer.EnsureLine();
            renderer.Write("<div hidden style=\"display: none\">");
            renderer.WriteEscape(block.Lines.ToString());
            renderer.WriteLine("</div>");
        }
    }

    private sealed class BreakInlineParser : InlineParser
    {
        private static readonly Regex BreakTag = new(
            @"\G<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public BreakInlineParser()
        {
            OpeningCharacters = ['<'];
        }

        public override bool Match(InlineProcessor processor, ref StringSlice slice)
        {
            var match = BreakTag.Match(slice.Text, slice.Start);
            if (!match.Success || match.Index + match.Length > slice.End + 1)
            {
                return false;
            }
            slice.Start += match.Length;
            processor.Inline = new LineBreakInline { IsHard = true };
            return true;
        }
    }
}
