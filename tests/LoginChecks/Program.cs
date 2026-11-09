using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Website.Pages;

var services = new ServiceCollection();
services.AddLogging();
services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
using var provider = services.BuildServiceProvider();

foreach (var returnUrl in new string?[]
{
    "/Gallery/2020/2020-02?sort=date&page=2",
    "/gallery-app/Image/Album/photo%20one.png",
    "/Search?q=red%20%26%20blue",
    "/Admin",
    null,
    "",
    "https://example.com",
    "//example.com",
    @"/\example.com"
})
{
    using var scope = provider.CreateScope();
    var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
    var actionContext = new ActionContext(context, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
    var model = new LoginModel(new ConfigurationBuilder().Build(), new TestEnvironment())
    {
        PageContext = new PageContext(actionContext),
        Url = new UrlHelper(actionContext),
        ReturnUrl = returnUrl
    };

    var result = await model.OnPostLogoutAsync();
    if (!string.IsNullOrWhiteSpace(returnUrl) && model.Url.IsLocalUrl(returnUrl))
    {
        Check(result is LocalRedirectResult redirect && redirect.Url == returnUrl, $"preserve {returnUrl}");
    }
    else
    {
        Check(result is RedirectToPageResult redirect && redirect.PageName == "/Index", $"reject nonlocal return URL: {returnUrl}");
    }
    Check(context.Response.Headers.SetCookie.Any(cookie =>
        cookie?.Contains(".AspNetCore.Cookies=;") == true), "expire authentication cookie");
}

Console.WriteLine("PASS logout preserves local paths and queries, rejects external redirects, and expires authentication cookies");

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException($"FAIL: {message}");
}

sealed class TestEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "LoginChecks";
    public string EnvironmentName { get; set; } = "Production";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
