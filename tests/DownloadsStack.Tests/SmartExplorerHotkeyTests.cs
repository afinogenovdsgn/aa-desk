using DownloadsStack.Services;
using DownloadsStack.Models;
using System.Text.Json;

namespace DownloadsStack.Tests;

public sealed class SmartExplorerHotkeyTests
{
    [Fact]
    public void OldModuleSettingsKeepTheDefaultShortcut() =>
        Assert.Equal("Win+Shift+E", JsonSerializer.Deserialize<ModuleSettings>("{}")!.SmartExplorerHotkey);

    [Theory]
    [InlineData("Win+Shift+E", 0x000C, 0x45)]
    [InlineData("Ctrl+Alt+F12", 0x0003, 0x7B)]
    [InlineData("Ctrl+1", 0x0002, 0x31)]
    public void ValidShortcutsHaveWindowsModifiersAndVirtualKey(string shortcut, uint modifiers, uint key)
    {
        Assert.True(SmartExplorerService.TryParseHotkey(shortcut, out var actualModifiers, out var actualKey));
        Assert.Equal(modifiers, actualModifiers);
        Assert.Equal(key, actualKey);
    }

    [Theory]
    [InlineData("E")]
    [InlineData("Win+Win+E")]
    [InlineData("Win+Tab")]
    public void UnsupportedShortcutsAreRejected(string shortcut) =>
        Assert.False(SmartExplorerService.TryParseHotkey(shortcut, out _, out _));
}
