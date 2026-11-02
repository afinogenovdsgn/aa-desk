using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DownloadsStack.Services;

/// <summary>One optional AA Desk module. It uses the tray's existing HWND; it creates no second tray icon.</summary>
internal sealed class SmartExplorerService : IDisposable
{
    private const int HotkeyId = 0x4141;
    private const int WmHotkey = 0x0312;
    private readonly HwndSource _source;
    private readonly Action<string> _report;
    private readonly string _lastPathFile = Path.Combine(LocalLog.DataDirectory, "smart-explorer-last-path.txt");
    private bool _registered, _enabled;
    private string _hotkey = "Win+Shift+E";
    internal bool UseLastPath { get; set; } = true;
    internal string Hotkey
    {
        get => _hotkey;
        set => TrySetHotkey(value);
    }
    internal bool TrySetHotkey(string value)
    {
        if (!TryParseHotkey(value, out _, out _)) return false;
        if (_hotkey == value) return true;
        var previous = _hotkey;
        if (_registered) { UnregisterHotKey(_source.Handle, HotkeyId); _registered = false; }
        _hotkey = value;
        if (!_enabled || RegisterCurrent()) return true;
        _hotkey = previous;
        RegisterCurrent();
        return false;
    }

    internal static bool TryParseHotkey(string? gesture, out uint modifiers, out uint key)
    {
        modifiers = 0; key = 0;
        if (string.IsNullOrWhiteSpace(gesture)) return false;
        var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        foreach (var part in parts[..^1])
        {
            var flag = part.ToUpperInvariant() switch { "WIN" => 0x0008u, "CTRL" => 0x0002u, "ALT" => 0x0001u, "SHIFT" => 0x0004u, _ => 0u };
            if (flag == 0 || (modifiers & flag) != 0) return false;
            modifiers |= flag;
        }
        var last = parts[^1].ToUpperInvariant();
        if (last.Length == 1 && last[0] is >= 'A' and <= 'Z' or >= '0' and <= '9') key = last[0];
        else if (last.Length > 1 && last[0] == 'F' && int.TryParse(last[1..], out var number) && number is >= 1 and <= 12) key = (uint)(0x70 + number - 1);
        return key != 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint ILCreateFromPathW(string path);
    [DllImport("shell32.dll")]
    private static extern void ILFree(nint pidl);
    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(nint pidl, uint count, nint items, uint flags);

    internal SmartExplorerService(HwndSource source, Action<string> report)
    {
        _source = source;
        _report = report;
        _source.AddHook(Message);
    }

    internal void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (enabled && _registered) return;
        if (!enabled) { UnregisterHotKey(_source.Handle, HotkeyId); _registered = false; return; }
        // Repeat suppression avoids opening several Explorer windows while the key is held.
        RegisterCurrent();
    }
    private bool RegisterCurrent()
    {
        TryParseHotkey(_hotkey, out var modifiers, out var key);
        if (RegisterHotKey(_source.Handle, HotkeyId, modifiers | 0x4000, key)) { _registered = true; return true; }
        _report("SmartExplorer: " + _hotkey + " недоступно (" + new Win32Exception(Marshal.GetLastWin32Error()).Message + "). Список загрузок продолжает работать.");
        return false;
    }

    private nint Message(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmHotkey || wParam != HotkeyId) return 0;
        handled = true;
        Open();
        return 0;
    }

    internal void Open()
    {
        try
        {
            var files = ReadFiles();
            var text = files is null or { Count: 0 } ? ReadText() : null;
            var last = UseLastPath ? ReadLast() : null;
            var current = SmartPathResolver.Resolve(files, text, last);
            if (current is not null && !string.Equals(current, last, StringComparison.OrdinalIgnoreCase)) SaveLast(current);
            if (current is null) Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            else if (File.Exists(current))
            {
                var pidl = ILCreateFromPathW(current);
                if (pidl == 0) throw new IOException("Не удалось найти файл в Проводнике: " + current);
                try { Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(pidl, 0, 0, 0)); }
                finally { ILFree(pidl); }
            }
            else Process.Start(new ProcessStartInfo(current) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LocalLog.Write("SmartExplorer", ex);
            _report("SmartExplorer: " + ex.Message);
        }
    }

    private static IReadOnlyList<string>? ReadFiles()
    {
        try { return Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToArray() : null; }
        catch (ExternalException) { return null; }
    }
    private static string? ReadText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (ExternalException) { return null; }
    }
    private string? ReadLast()
    {
        try { return File.Exists(_lastPathFile) ? File.ReadAllText(_lastPathFile) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LocalLog.Write("Read last path", ex); return null; }
    }
    private void SaveLast(string path)
    {
        try { Directory.CreateDirectory(LocalLog.DataDirectory); File.WriteAllText(_lastPathFile, path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LocalLog.Write("Save last path", ex); }
    }
    public void Dispose()
    {
        SetEnabled(false);
        _source.RemoveHook(Message);
    }
}
