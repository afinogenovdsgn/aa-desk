using System.IO;
using DownloadsStack.Services;

namespace DownloadsStack.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("0.1.1", "0.1.0", true)]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("0.0.9", "0.1.0", false)]
    [InlineData("broken", "0.1.0", false)]
    public void OnlyNewerValidVersionsAreAvailable(string candidate, string current, bool expected) =>
        Assert.Equal(expected, UpdateService.IsNewerVersion(candidate, current));

    [Fact]
    public void InstallerMustBelongToTheConfiguredRepository()
    {
        var accepted = UpdateService.AllowedAsset("https://github.com/afinogenovdsgn/aa-desk/releases/download/v0.2.0/AADesk-0.2.0-setup-win-x64.exe");
        Assert.Equal("github.com", accepted.Host);
        Assert.Throws<InvalidDataException>(() => UpdateService.AllowedAsset("https://example.com/setup.exe"));
        Assert.Throws<InvalidDataException>(() => UpdateService.AllowedAsset("https://github.com/other/repo/releases/download/v0.2.0/setup.exe"));
    }
}
