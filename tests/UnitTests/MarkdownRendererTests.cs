using Website.Services;

namespace UnitTests;

public class MarkdownRendererTests
{
    public static TheoryData<string> Renderers => ["safe", "blog"];

    private static string Render(string renderer, string? markdown) =>
        renderer == "blog" ? BlogMarkdownRenderer.Render(markdown) : SafeMarkdownRenderer.Render(markdown);

    [Theory]
    [MemberData(nameof(Renderers))]
    public void RawHtmlIsDisabled(string renderer)
    {
        Assert.DoesNotContain("<script>", Render(renderer, "<script>alert(1)</script>"));
        Assert.DoesNotContain("<img ", Render(renderer, "<img src=x onerror=alert(1)>"));
        Assert.DoesNotContain("<br ", Render(renderer, "<br onclick=\"alert(1)\">"));
    }

    [Theory]
    [MemberData(nameof(Renderers))]
    public void PlainBreaksAndNewlinesRenderAsLineBreaks(string renderer)
    {
        Assert.Contains("before<br />", Render(renderer, "before<BR/>after"));
        Assert.Contains("line one<br />", Render(renderer, "line one\nline two"));
        Assert.DoesNotContain("<br />", Render(renderer, "```\na\nb\n```"));
        Assert.DoesNotContain("<br />", Render(renderer, "`<br>`"));
    }

    [Theory]
    [MemberData(nameof(Renderers))]
    public void NullRendersEmpty(string renderer) =>
        Assert.Equal("", Render(renderer, null));

    [Fact]
    public void BlogImagesHonourScaleTitle() =>
        Assert.Contains("width: 50%", BlogMarkdownRenderer.Render("![a](image.png \"scale=50%\")"));

    [Theory]
    [MemberData(nameof(Renderers))]
    public void HiddenTextRendersInSourceWithoutVisibleContentOrMarkdownFormatting(string renderer)
    {
        var html = Render(renderer, "Before\n\n```hidden\nSource-only **text**\nSecond line\n```\n\nAfter");

        Assert.Equal("<p>Before</p>\n<div hidden style=\"display: none\">Source-only **text**\nSecond line</div>\n<p>After</p>\n", html);
    }

    [Theory]
    [MemberData(nameof(Renderers))]
    public void HiddenTextEscapesHtmlAndCannotCloseItsContainer(string renderer)
    {
        var html = Render(renderer, "```hidden\n</div><script>alert(1)</script>\n& <img src=x onerror=alert(1)>\n```");

        Assert.Contains("&lt;/div&gt;&lt;script&gt;", html);
        Assert.Contains("&amp; &lt;img", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img ", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "</div>"));
    }

    [Theory]
    [MemberData(nameof(Renderers))]
    public void HiddenTextDoesNotChangeOrdinaryCodeOrInlineCode(string renderer)
    {
        Assert.Contains("<pre><code class=\"language-text\">", Render(renderer, "```text\nExample\n```"));
        Assert.Contains("<pre><code>", Render(renderer, "    ```hidden\n    Example\n    ```"));
        Assert.DoesNotContain("<div hidden", Render(renderer, "`hidden`"));
    }

    [Theory]
    [MemberData(nameof(Renderers))]
    public void HiddenTextSupportsEmptyAndMultipleBlocks(string renderer)
    {
        Assert.Equal("<div hidden style=\"display: none\"></div>\n", Render(renderer, "```hidden\n```"));
        var html = Render(renderer, "```hidden\nOne\n```\n\nVisible\n\n~~~hidden\nTwo\n~~~");
        Assert.Contains(">One</div>", html);
        Assert.Contains("<p>Visible</p>", html);
        Assert.Contains(">Two</div>", html);
    }
}
