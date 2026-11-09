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
    public const string FileName = "legacy_redirects.json";

    public static IServiceCollection AddLegacyRedirects(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<List<LegacyRedirectRule>>(configuration.GetSection(SectionName));
        return services;
    }

    public static IApplicationBuilder UseLegacyRedirects(this IApplicationBuilder app)
    {
        var monitor = app.ApplicationServices.GetRequiredService<IOptionsMonitor<List<LegacyRedirectRule>>>();
        // Rebuilt only when the bound rule list changes (config reload), so requests just do dictionary lookups.
        var cached = new LegacyRedirectIndex([]);

        return app.Use(async (context, next) =>
        {
            if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            {
                var rules = monitor.CurrentValue;
                var index = cached;
                if (!ReferenceEquals(index.Source, rules))
                {
                    cached = index = new LegacyRedirectIndex(rules);
                }

                var rule = index.FindMatch(context.Request.Path, context.Request.Query);
                if (rule is not null)
                {
                    context.Response.Redirect(rule.Target, rule.Permanent);
                    return;
                }
            }

            await next();
        });
    }

    internal static string NormalizePath(string? path)
    {
        var trimmed = (path ?? string.Empty).Trim().TrimEnd('/');
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }
}

public sealed class LegacyRedirectIndex
{
    private sealed record QueryGroup(string[] Keys, Dictionary<string, LegacyRedirectRule> RulesByValues);

    // Per path: rule groups sharing the same query keys, most keys first (ties keep config order).
    private readonly Dictionary<string, QueryGroup[]> _paths = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<LegacyRedirectRule> Source { get; }

    public LegacyRedirectIndex(IEnumerable<LegacyRedirectRule> rules)
    {
        Source = rules;
        var groups = new Dictionary<string, Dictionary<string, QueryGroup>>(StringComparer.OrdinalIgnoreCase);
        var order = new Dictionary<string, List<QueryGroup>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Target))
            {
                continue;
            }

            var path = LegacyRedirects.NormalizePath(rule.Path);
            if (!groups.TryGetValue(path, out var byKeys))
            {
                groups[path] = byKeys = new(StringComparer.OrdinalIgnoreCase);
                order[path] = [];
            }

            var keys = rule.Query.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var keySignature = string.Join('\0', keys).ToUpperInvariant();
            if (!byKeys.TryGetValue(keySignature, out var group))
            {
                byKeys[keySignature] = group = new QueryGroup(keys, new(StringComparer.Ordinal));
                order[path].Add(group);
            }

            group.RulesByValues.TryAdd(string.Join('\0', keys.Select(k => rule.Query[k])), rule);
        }

        foreach (var (path, list) in order)
        {
            _paths[path] = [.. list.OrderByDescending(g => g.Keys.Length)];
        }
    }

    public LegacyRedirectRule? FindMatch(PathString path, IQueryCollection query)
    {
        if (!_paths.TryGetValue(LegacyRedirects.NormalizePath(path.Value), out var groups))
        {
            return null;
        }

        foreach (var group in groups)
        {
            var values = group.Keys.Length == 0
                ? string.Empty
                : string.Join('\0', group.Keys.Select(k => query[k].ToString()));
            if (group.RulesByValues.TryGetValue(values, out var rule))
            {
                return rule;
            }
        }

        return null;
    }
}
