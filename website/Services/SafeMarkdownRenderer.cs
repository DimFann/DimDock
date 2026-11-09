using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
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
        return builder.Build();
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
