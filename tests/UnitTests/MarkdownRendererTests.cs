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
}
