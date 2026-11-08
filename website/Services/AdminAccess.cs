using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Website.Services;

public static class AdminAccess
{
    private const int MinimumPasswordLength = 12;

    public static bool IsFeatureEnabled(IConfiguration configuration) =>
        configuration.GetValue<bool?>("Features:AdminEnabled") ?? true;

    public static bool IsConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["AdminAuth:Username"]) &&
        configuration["AdminAuth:Password"] is { Length: >= MinimumPasswordLength };

    public static bool IsAdmin(IConfiguration configuration, ClaimsPrincipal user) =>
        IsFeatureEnabled(configuration) &&
        IsConfigured(configuration) &&
        user.IsInRole("Admin");

    public static bool ValidateCredentials(
        IConfiguration configuration,
        string username,
        string password)
    {
        var configuredUsername = configuration["AdminAuth:Username"];
        var configuredPassword = configuration["AdminAuth:Password"];
        if (string.IsNullOrWhiteSpace(configuredUsername) ||
            configuredPassword is not { Length: >= MinimumPasswordLength } ||
            !string.Equals(username, configuredUsername, StringComparison.Ordinal))
        {
            return false;
        }

        var providedPasswordBytes = Encoding.UTF8.GetBytes(password);
        var configuredPasswordBytes = Encoding.UTF8.GetBytes(configuredPassword);
        return CryptographicOperations.FixedTimeEquals(providedPasswordBytes, configuredPasswordBytes);
    }
}
