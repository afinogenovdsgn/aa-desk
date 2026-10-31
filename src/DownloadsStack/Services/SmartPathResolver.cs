using System.IO;

namespace DownloadsStack.Services;

/// <summary>Chooses one existing absolute filesystem path without changing the clipboard.</summary>
internal static class SmartPathResolver
{
    internal static string? Resolve(IReadOnlyList<string>? copiedFiles, string? copiedText, string? lastPath)
    {
        if (copiedFiles is { Count: 1 } && Existing(copiedFiles[0]) is { } file) return file;
        if (copiedFiles is null or { Count: 0 } && Existing(copiedText) is { } text) return text;
        return Existing(lastPath);
    }

    internal static string? Existing(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var path = value.Trim();
        if (path.Length > 1 && path[0] == '"' && path[^1] == '"') path = path[1..^1];
        try
        {
            if (!Path.IsPathFullyQualified(path)) return null;
            path = Path.GetFullPath(path);
            return File.Exists(path) || Directory.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        { return null; }
    }
}
