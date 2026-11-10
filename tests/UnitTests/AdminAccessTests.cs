using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Website.Services;

namespace UnitTests;

public class AdminAccessTests
{
    private const string Password = "correct-horse-battery";

    private static IConfiguration Config(string? username = "admin", string? password = Password, bool? enabled = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdminAuth:Username"] = username,
            ["AdminAuth:Password"] = password,
            ["Features:AdminEnabled"] = enabled?.ToString()
        }).Build();

    private static ClaimsPrincipal User(string? role) => role is null
        ? new ClaimsPrincipal(new ClaimsIdentity())
        : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));

    [Fact]
    public void ValidCredentialsAreAccepted() =>
        Assert.True(AdminAccess.ValidateCredentials(Config(), "admin", Password));

    [Theory]
    [InlineData("admin", "wrong-password-123")]
    [InlineData("Admin", Password)]
    [InlineData("other", Password)]
    public void WrongCredentialsAreRejected(string username, string password) =>
        Assert.False(AdminAccess.ValidateCredentials(Config(), username, password));

    [Fact]
    public void ShortOrMissingConfiguredPasswordDisablesLogin()
    {
        Assert.False(AdminAccess.ValidateCredentials(Config(password: "short"), "admin", "short"));
        Assert.False(AdminAccess.ValidateCredentials(Config(password: null), "admin", ""));
        Assert.False(AdminAccess.IsConfigured(Config(password: "short")));
        Assert.False(AdminAccess.ValidateCredentials(Config(username: " "), " ", Password));
    }

    [Fact]
    public void DevelopmentBypassAcceptsAnyPasswordForConfiguredUser()
    {
        Assert.True(AdminAccess.ValidateCredentials(Config(password: null), "admin", "x", allowAnyPassword: true));
        Assert.False(AdminAccess.ValidateCredentials(Config(password: null), "other", "x", allowAnyPassword: true));
    }

    [Fact]
    public void IsAdminRequiresRoleConfigurationAndFeature()
    {
        Assert.True(AdminAccess.IsAdmin(Config(), User("Admin")));
        Assert.False(AdminAccess.IsAdmin(Config(), User(null)));
        Assert.False(AdminAccess.IsAdmin(Config(), User("Viewer")));
        Assert.False(AdminAccess.IsAdmin(Config(enabled: false), User("Admin")));
        Assert.False(AdminAccess.IsAdmin(Config(password: "short"), User("Admin")));
    }
}
