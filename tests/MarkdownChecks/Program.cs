using Website.Services;

foreach (var render in new Func<string?, string>[] { SafeMarkdownRenderer.Render, BlogMarkdownRenderer.Render })
{
    foreach (var tag in new[] { "<br>", "<br/>", "<br />", "<BR>" })
        Check(render($"before{tag}after").Contains("before<br />"), $"break supported: {tag}");
    Check(!render("`<br>`").Contains("<br />"), "inline code preserved");
    Check(!render("```\n<br>\n```").Contains("<br />"), "fenced code preserved");
    Check(!render(@"before\<br>after").Contains("<br />"), "escaped tag preserved");
    Check(!render("<br onclick=\"alert(1)\">").Contains("<br "), "attributes not allowed");
    Check(!render("<script>alert(1)</script>").Contains("<script>"), "raw script disabled");
    Check(!render("<img src=x onerror=alert(1)>").Contains("<img "), "raw image disabled");
    Check(render("**bold**").Contains("<strong>bold</strong>"), "Markdown preserved");
    Check(render(null) == "", "null preserved");
}
Check(BlogMarkdownRenderer.Render("![test](image.png \"scale=50%\")").Contains("width: 50%"), "scaled images preserved");
Console.WriteLine("PASS Markdown breaks, code/escape handling, HTML restrictions and scaled images");

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException($"FAIL: {message}");
}
