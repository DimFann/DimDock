using System.Net;
using System.Text.RegularExpressions;

namespace UnitTests;

public sealed partial class NavigationSettingsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("example.com")]
    public async Task LoginCookieScopeAndLogoutUseConfiguredDomain(string? domain)
    {
        Configuration["AdminAuth:CookieDomain"] = domain;
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = new Uri(_app.Urls.Single())
        };
        client.DefaultRequestHeaders.Host = "example.com";
        using var loginPage = await client.GetAsync("/Login");
        var html = await loginPage.Content.ReadAsStringAsync();
        var antiforgeryCookie = loginPage.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", antiforgeryCookie);
        using var login = await client.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "test-admin",
            ["Password"] = "test-password-only",
            ["__RequestVerificationToken"] = ExtractToken(html)
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var authCookie = login.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("DimDock.Admin="));
        Assert.Equal(domain is not null, authCookie.Contains("domain=example.com", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("httponly", authCookie, StringComparison.OrdinalIgnoreCase);
        var cookies = new CookieContainer();
        cookies.SetCookies(new Uri("https://example.com/"), authCookie);
        Assert.NotEmpty(cookies.GetCookieHeader(new Uri("https://example.com/")));
        Assert.Equal(domain is not null,
            cookies.GetCookieHeader(new Uri("https://upload.example.com/")).Contains("DimDock.Admin="));
        Assert.Empty(cookies.GetCookieHeader(new Uri("https://unrelated.test/")));

        client.DefaultRequestHeaders.Host = domain is null ? "example.com" : "upload.example.com";
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", antiforgeryCookie + "; " +
            cookies.GetCookieHeader(new Uri($"https://{client.DefaultRequestHeaders.Host}/")));
        using var signedIn = await client.GetAsync("/Login?ReturnUrl=%2FGallery%2FAlbum");
        Assert.Equal("/Gallery/Album", signedIn.Headers.Location!.OriginalString);

        var signedInHtml = await client.GetStringAsync("/commissions");
        using var logout = await client.PostAsync("/Login?handler=Logout",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = ExtractToken(signedInHtml),
                ["ReturnUrl"] = "/"
            }));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        var deleted = logout.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("DimDock.Admin="));
        Assert.Equal(domain is not null, deleted.Contains("domain=example.com", StringComparison.OrdinalIgnoreCase));
        cookies.SetCookies(new Uri($"https://{client.DefaultRequestHeaders.Host}/"), deleted);
        Assert.Empty(cookies.GetCookieHeader(new Uri("https://example.com/")));
        Assert.Empty(cookies.GetCookieHeader(new Uri("https://upload.example.com/")));
    }

    [Theory]
    [InlineData("/Gallery/Album?sort=date&page=2", "/Gallery/Album?sort=date&page=2")]
    [InlineData("/image/123", "/image/123")]
    [InlineData("https://example.com", "/Admin")]
    [InlineData("//example.com", "/Admin")]
    public async Task LoginReturnsToLocalPageOnly(string returnUrl, string expected)
    {
        using var client = CreateClient();
        var loginUrl = "/Login?ReturnUrl=" + Uri.EscapeDataString(returnUrl);
        var html = await client.GetStringAsync(loginUrl);
        Assert.DoesNotContain("<h1>Admin Login</h1>", html);
        Assert.Contains("<title>Login - ", html);
        using var response = await client.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "test-admin",
            ["Password"] = "test-password-only",
            ["ReturnUrl"] = returnUrl,
            ["__RequestVerificationToken"] = ExtractToken(html)
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expected, response.Headers.Location!.OriginalString);
        using var alreadySignedIn = await client.GetAsync(loginUrl);
        Assert.Equal(expected, alreadySignedIn.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task HeaderLoginCarriesCurrentPathAndQueryThroughFailedAttempt()
    {
        using var client = CreateClient();
        var html = await client.GetStringAsync("/commissions?example=one%20two");
        var href = WebUtility.HtmlDecode(Regex.Match(html, "href=\"([^\"]+)\"[^>]*>Login</a>").Groups[1].Value);
        Assert.Equal("/Login?ReturnUrl=%2Fcommissions%3Fexample%3Done%2520two", href);
        var login = await client.GetStringAsync(href);
        Assert.Contains("value=\"/commissions?example=one%20two\"", login);
        using var response = await client.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "test-admin",
            ["Password"] = "wrong",
            ["ReturnUrl"] = "/commissions?example=one%20two",
            ["__RequestVerificationToken"] = ExtractToken(login)
        }));
        var failure = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid username or password.", failure);
        Assert.Contains("value=\"/commissions?example=one%20two\"", failure);
    }
}
