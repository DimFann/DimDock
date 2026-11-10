using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Website.Services;

namespace UnitTests;

public class LegacyRedirectIndexTests
{
    private static readonly LegacyRedirectRule Folder = new()
    {
        Path = "/sketch",
        Query = new(StringComparer.OrdinalIgnoreCase) { ["folderID"] = "abc" },
        Target = "/Gallery/Misc"
    };

    private static readonly LegacyRedirectRule Fallback = new() { Path = "/sketch", Target = "/Gallery" };

    private static IQueryCollection Query(params (string Key, string Value)[] values) =>
        new QueryCollection(values.ToDictionary(v => v.Key, v => new StringValues(v.Value), StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void MostSpecificQueryRuleWinsRegardlessOfOrder()
    {
        var index = new LegacyRedirectIndex([Fallback, Folder]);

        Assert.Same(Folder, index.FindMatch("/sketch", Query(("folderID", "abc"))));
        Assert.Same(Fallback, index.FindMatch("/sketch", Query(("folderID", "other"))));
        Assert.Same(Fallback, index.FindMatch("/sketch", Query()));
    }

    [Theory]
    [InlineData("/SKETCH")]
    [InlineData("/sketch/")]
    public void PathMatchingIgnoresCaseAndTrailingSlash(string path)
    {
        var index = new LegacyRedirectIndex([Folder]);

        Assert.Same(Folder, index.FindMatch(path, Query(("folderid", "abc"))));
    }

    [Fact]
    public void QueryValuesMatchExactly()
    {
        var index = new LegacyRedirectIndex([Folder]);

        Assert.Null(index.FindMatch("/sketch", Query(("folderID", "ABC"))));
    }

    [Fact]
    public void UnknownPathsAndRulesWithoutTargetsDoNotMatch()
    {
        var index = new LegacyRedirectIndex([new LegacyRedirectRule { Path = "/old", Target = " " }]);

        Assert.Null(index.FindMatch("/old", Query()));
        Assert.Null(index.FindMatch("/missing", Query()));
    }

    [Fact]
    public void FirstDuplicateRuleIsKept()
    {
        var second = new LegacyRedirectRule { Path = "/sketch", Target = "/Elsewhere" };
        var index = new LegacyRedirectIndex([Fallback, second]);

        Assert.Same(Fallback, index.FindMatch("/sketch", Query()));
    }
}
