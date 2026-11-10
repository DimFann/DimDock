using Website.Services;

namespace UnitTests;

public class TagNameNormalizerTests
{
    [Fact]
    public void TagsAreTrimmedLowercasedDeduplicatedAndSorted()
    {
        Assert.True(TagNameNormalizer.TryNormalize(" Zebra, apple ,,APPLE, ", out var tags, out var error));

        Assert.Null(error);
        Assert.Equal(["apple", "zebra"], tags);
    }

    [Fact]
    public void EmptyInputGivesNoTags()
    {
        Assert.True(TagNameNormalizer.TryNormalize(null, out var tags, out _));
        Assert.Empty(tags);
    }

    [Theory]
    [InlineData("bad\ttag")]
    public void ControlCharactersAreRejected(string value)
    {
        Assert.False(TagNameNormalizer.TryNormalize(value, out var tags, out var error));
        Assert.Empty(tags);
        Assert.NotNull(error);
    }

    [Fact]
    public void LengthLimitsAreEnforced()
    {
        Assert.True(TagNameNormalizer.TryNormalize(new string('a', 100), out _, out _));
        Assert.False(TagNameNormalizer.TryNormalize(new string('a', 101), out _, out _));
        Assert.False(TagNameNormalizer.TryNormalize(new string(',', 4001), out _, out _));
    }
}

public class DatePathTagsTests
{
    [Fact]
    public void YearAndMonthFoldersBecomeTags()
    {
        Assert.Equal(["2020", "2020-02"], DatePathTags.ForPath("Art/2020/2020-02"));
        Assert.Equal(["2019", "2019-12"], DatePathTags.ForPath("2019-12"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Misc/Reference")]
    [InlineData("2020-13")]
    [InlineData("2020-00")]
    [InlineData("20201")]
    public void NonDateSegmentsAreIgnored(string? path) =>
        Assert.Empty(DatePathTags.ForPath(path));
}

public class FileStoreKeysTests
{
    [Theory]
    [InlineData("a/b/c.png", "a/b/c.png")]
    [InlineData("a/b/", "a/b")]
    [InlineData("", "")]
    public void ValidKeysNormalize(string key, string expected) =>
        Assert.Equal(expected, FileStoreKeys.Normalize(key));

    [Theory]
    [InlineData("/absolute")]
    [InlineData("a\\b")]
    [InlineData("a//b")]
    [InlineData("a/../b")]
    [InlineData("./a")]
    [InlineData("a/b?.png")]
    [InlineData("a:b")]
    public void UnsafeKeysAreRejected(string key) =>
        Assert.Throws<InvalidDataException>(() => FileStoreKeys.Normalize(key));

    [Fact]
    public void EmptyKeyRequiresAllowEmpty()
    {
        Assert.Throws<InvalidDataException>(() => FileStoreKeys.Validate("", allowEmpty: false));
        Assert.Throws<InvalidDataException>(() => FileStoreKeys.Validate("a/", allowEmpty: false));
    }

    [Fact]
    public void GallerySourceUrlsAreEscapedAndRootIsOmitted()
    {
        Assert.Equal("/img_src/photo%20one.png", FileStoreKeys.GallerySourceUrl("root", "photo one.png"));
        Assert.Equal("/img_src/With%20space/a%23b.png", FileStoreKeys.GallerySourceUrl("With space", "a#b.png"));
    }
}
