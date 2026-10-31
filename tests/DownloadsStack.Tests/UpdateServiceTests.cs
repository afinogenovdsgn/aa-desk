using System.IO;
using System.Security.Cryptography;
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

    [Fact]
    public async Task VerifiedDownloadCanReplaceExistingInstallerOnWindows()
    {
        var folder = Directory.CreateTempSubdirectory("aa-desk-update-");
        try
        {
            var pending = Path.Combine(folder.FullName, "setup.exe.download");
            var setup = Path.Combine(folder.FullName, "setup.exe");
            var bytes = new byte[] { 1, 2, 3, 4 };
            await File.WriteAllBytesAsync(pending, bytes);
            await File.WriteAllTextAsync(setup, "previous download");

            await UpdateService.VerifyAndMoveAsync(pending, setup, Convert.ToHexString(SHA256.HashData(bytes)));

            Assert.False(File.Exists(pending));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(setup));
        }
        finally { folder.Delete(true); }
    }
}
