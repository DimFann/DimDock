using Website.Services;

namespace UnitTests;

public class NameValidationTests
{
    [Theory]
    [InlineData("photo.png")]
    [InlineData("Photo New (2).png")]
    [InlineData("ünïcode.jpg")]
    public void ValidItemFileNames(string name) =>
        Assert.True(FilesystemPersistenceService.IsValidItemFileName(name));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" leading.png")]
    [InlineData("trailing.png ")]
    [InlineData(".hidden.png")]
    [InlineData("a/b.png")]
    [InlineData("a\\b.png")]
    [InlineData("what?.png")]
    [InlineData("tab\t.png")]
    public void InvalidItemFileNames(string name) =>
        Assert.False(FilesystemPersistenceService.IsValidItemFileName(name));

    [Fact]
    public void ItemFileNamesAreLimitedTo255Characters()
    {
        Assert.True(FilesystemPersistenceService.IsValidItemFileName(new string('a', 251) + ".png"));
        Assert.False(FilesystemPersistenceService.IsValidItemFileName(new string('a', 252) + ".png"));
    }

    [Fact]
    public void CreatedBlogDraftIdsAreValid()
    {
        var id = FilesystemPersistenceService.CreateBlogDraftId();

        Assert.True(FilesystemPersistenceService.IsValidBlogDraftId(id));
        Assert.NotEqual(id, FilesystemPersistenceService.CreateBlogDraftId());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../../assets/1")]
    [InlineData("20261310000000" + "0123456789abcdef0123456789abcdef")]
    [InlineData("20260101000000" + "0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("20260101000000" + "0123456789abcdef0123456789abcde")]
    public void InvalidBlogDraftIds(string? id) =>
        Assert.False(FilesystemPersistenceService.IsValidBlogDraftId(id));
}
