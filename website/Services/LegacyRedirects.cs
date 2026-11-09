using Microsoft.Extensions.Options;

namespace Website.Services;

public sealed class LegacyRedirectRule
{
    public string Path { get; set; } = string.Empty;
    public Dictionary<string, string> Query { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Target { get; set; } = string.Empty;
    public bool Permanent { get; set; } = true;
}

// Redirects old-website links using rules from the "LegacyRedirects" config section.
// When several rules share a path, the one matching the most query parameters wins.
public static class LegacyRedirects
{
    public const string SectionName = "LegacyRedirects";

    public static IServiceCollection AddLegacyRedirects(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<List<LegacyRedirectRule>>(configuration.GetSection(SectionName));
        return services;
    }

    public static IApplicationBuilder UseLegacyRedirects(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            {
                var rules = context.RequestServices.GetRequiredService<IOptionsMonitor<List<LegacyRedirectRule>>>().CurrentValue;
                var rule = FindMatch(rules, context.Request.Path, context.Request.Query);
                if (rule is not null)
                {
                    context.Response.Redirect(rule.Target, rule.Permanent);
                    return;
                }
            }

            await next();
        });

    public static LegacyRedirectRule? FindMatch(IEnumerable<LegacyRedirectRule> rules, PathString path, IQueryCollection query)
    {
        var requestPath = NormalizePath(path.Value);
        LegacyRedirectRule? best = null;
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Target) ||
                !string.Equals(NormalizePath(rule.Path), requestPath, StringComparison.OrdinalIgnoreCase) ||
                !rule.Query.All(q => string.Equals(query[q.Key].ToString(), q.Value, StringComparison.Ordinal)))
            {
                continue;
            }

            if (best is null || rule.Query.Count > best.Query.Count)
            {
                best = rule;
            }
        }

        return best;
    }

    private static string NormalizePath(string? path)
    {
        var trimmed = (path ?? string.Empty).Trim().TrimEnd('/');
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }
}
