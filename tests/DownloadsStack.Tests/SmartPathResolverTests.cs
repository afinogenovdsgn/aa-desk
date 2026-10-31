using DownloadsStack.Services;

namespace DownloadsStack.Tests;

public sealed class SmartPathResolverTests
{
    [Fact]
    public void CopiedFileTakesPriorityOverTextAndSavedPath()
    {
        using var folder = new TestDirectory();
        var file = folder.File("новый файл.txt");
        Assert.Equal(file, SmartPathResolver.Resolve([file], folder.Path, folder.Path));
    }

    [Fact]
    public void TextPathAndFallbackAreRevalidated()
    {
        using var folder = new TestDirectory();
        var file = folder.File("item.txt");
        Assert.Equal(file, SmartPathResolver.Resolve(null, '"' + file + '"', folder.Path));
        Assert.Equal(folder.Path, SmartPathResolver.Resolve(["missing", file], file, folder.Path));
        Assert.Null(SmartPathResolver.Resolve(null, "not a full path", folder.Path + "-deleted"));
    }
}
