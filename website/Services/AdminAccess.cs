using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;

namespace Website.Services;

public static class AdminAccess
{
    private const int MinimumPasswordLength = 12;

    public static string? GetCookieDomain(IConfiguration configuration)
    {
        var domain = configuration["AdminAuth:CookieDomain"]?.Trim();
        if (string.IsNullOrEmpty(domain))
        {
            return null;
        }
        domain = domain.StartsWith('.') ? domain[1..] : domain;
        if (Uri.CheckHostName(domain) != UriHostNameType.Dns ||
            !domain.Contains('.') || domain.EndsWith('.') || domain.Contains(".."))
        {
            throw new InvalidOperationException(
                "AdminAuth:CookieDomain (ADMIN_COOKIE_DOMAIN) must be a DNS domain such as example.com, without a scheme, port or path.");
        }
        return domain;
    }

    public static bool IsFeatureEnabled(IConfiguration configuration) =>
        configuration.GetValue<bool?>("Features:AdminEnabled") ?? true;

    public static bool IsConfigured(IConfiguration configuration, bool allowAnyPassword = false) =>
        !string.IsNullOrWhiteSpace(configuration["AdminAuth:Username"]) &&
        (allowAnyPassword ||
         configuration["AdminAuth:Password"] is { Length: >= MinimumPasswordLength });

    public static bool IsAdmin(
        IConfiguration configuration,
        ClaimsPrincipal user,
        bool allowAnyPassword = false) =>
        IsFeatureEnabled(configuration) &&
        IsConfigured(configuration, allowAnyPassword) &&
        user.IsInRole("Admin");

    public static bool IsDevelopmentPasswordBypassEnabled(
        IConfiguration configuration,
        IHostEnvironment environment) =>
        environment.IsDevelopment() &&
        configuration.GetValue<bool>("Development:AllowAnyAdminPassword");

    public static bool ValidateCredentials(
        IConfiguration configuration,
        string username,
        string password,
        bool allowAnyPassword = false)
    {
        var configuredUsername = configuration["AdminAuth:Username"];
        var configuredPassword = configuration["AdminAuth:Password"];
        if (string.IsNullOrWhiteSpace(configuredUsername) ||
            (!allowAnyPassword && configuredPassword is not { Length: >= MinimumPasswordLength }) ||
            !string.Equals(username, configuredUsername, StringComparison.Ordinal))
        {
            return false;
        }

        if (allowAnyPassword)
        {
            return true;
        }

        var providedPasswordBytes = Encoding.UTF8.GetBytes(password);
        var configuredPasswordBytes = Encoding.UTF8.GetBytes(configuredPassword!);
        return CryptographicOperations.FixedTimeEquals(providedPasswordBytes, configuredPasswordBytes);
    }
}
