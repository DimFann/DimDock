using System.Net;
using System.Text.RegularExpressions;

namespace UnitTests;

public sealed partial class NavigationSettingsTests
{
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
