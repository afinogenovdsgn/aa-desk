using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DownloadsStack.Services;

internal enum UpdateCheckResult { Unknown, Checking, NoRelease, Current, Available, Error }

/// <summary>Checks the AA Desk GitHub release, caches the result, and verifies setup before running it.</summary>
internal sealed class UpdateService : INotifyPropertyChanged
{
    internal const string Repository = "afinogenovdsgn/aa-desk";
    private static readonly Uri LatestRelease = new($"https://api.github.com/repos/{Repository}/releases/latest");
    private static readonly HttpClient Client = CreateClient();
    private readonly string _cachePath = Path.Combine(LocalLog.DataDirectory, "updates.json");
    private UpdateSnapshot _snapshot;
    private bool _busy;
    private string _status = "";
    private UpdateCheckResult _result;

    internal UpdateService()
    {
        _snapshot = ReadCache();
        UpdateStatus();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string CurrentVersion => typeof(UpdateService).Assembly.GetName().Version is { } version
        ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.1.0";
    public string Status { get => _status; private set { _status = value; Changed(nameof(Status)); } }
    public UpdateCheckResult Result { get => _result; private set { _result = value; Changed(nameof(Result)); } }
    public bool IsBusy { get => _busy; private set { _busy = value; Changed(nameof(IsBusy)); } }
    public bool HasUpdate => IsNewerVersion(_snapshot.Version, CurrentVersion)
        && _snapshot.SetupUrl is not null && _snapshot.HashUrl is not null;
    public string? AvailableVersion => HasUpdate ? _snapshot.Version : null;

    internal async Task CheckIfDueAsync()
    {
        if (DateTimeOffset.UtcNow - _snapshot.CheckedAt >= TimeSpan.FromDays(1)) await CheckAsync();
    }

    internal async Task CheckAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Result = UpdateCheckResult.Checking;
        Status = "Проверка обновлений…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await Client.GetAsync(LatestRelease, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _snapshot = new(DateTimeOffset.UtcNow, null, null, null, null);
                Result = UpdateCheckResult.NoRelease;
                Status = "Опубликованных релизов пока нет.";
            }
            else
            {
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync();
                var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream)
                    ?? throw new InvalidDataException("Пустой ответ GitHub Releases.");
                var number = release.TagName?.TrimStart('v', 'V');
                if (!Version.TryParse(number, out var version)) throw new InvalidDataException("Неверный номер версии релиза.");
                var setupName = $"AADesk-{version.Major}.{version.Minor}.{version.Build}-setup-win-x64.exe";
                var setup = release.Assets?.FirstOrDefault(a => a.Name == setupName)?.DownloadUrl;
                var hash = release.Assets?.FirstOrDefault(a => a.Name == setupName + ".sha256")?.DownloadUrl;
                _snapshot = new(DateTimeOffset.UtcNow, version.ToString(3), release.HtmlUrl, setup, hash);
                UpdateStatus();
            }
            SaveCache();
            Changed(nameof(HasUpdate)); Changed(nameof(AvailableVersion));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or IOException)
        {
            LocalLog.Write("Check updates", ex);
            Result = UpdateCheckResult.Error;
            Status = "Не удалось проверить обновления: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    internal async Task DownloadAndRunAsync()
    {
        if (!HasUpdate || IsBusy) return;
        IsBusy = true;
        Status = "Загрузка AA Desk " + AvailableVersion + "…";
        try
        {
            var setupUri = AllowedAsset(_snapshot.SetupUrl!);
            var hashUri = AllowedAsset(_snapshot.HashUrl!);
            var setupName = $"AADesk-{AvailableVersion}-setup-win-x64.exe";
            var hashText = await Client.GetStringAsync(hashUri);
            var expected = hashText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
                throw new InvalidDataException("У релиза нет корректной SHA-256 суммы установщика.");
            var folder = Path.Combine(Path.GetTempPath(), "AADesk", "updates", AvailableVersion!);
            Directory.CreateDirectory(folder);
            var pending = Path.Combine(folder, setupName + ".download");
            var setup = Path.Combine(folder, setupName);
            try
            {
                await using (var source = await Client.GetStreamAsync(setupUri))
                await using (var destination = File.Create(pending)) await source.CopyToAsync(destination);
                await VerifyAndMoveAsync(pending, setup, expected);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
            Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true });
            Status = "Установщик запущен. Следуй его инструкциям.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or Win32Exception or UriFormatException)
        {
            LocalLog.Write("Install update", ex);
            Status = "Не удалось установить обновление: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    internal static bool IsNewerVersion(string? candidate, string current) =>
        Version.TryParse(candidate, out var available) && Version.TryParse(current, out var installed)
        && available > installed;

    internal static async Task VerifyAndMoveAsync(string pending, string setup, string expected)
    {
        string actual;
        await using (var file = File.OpenRead(pending))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(file));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA-256 установщика не совпадает с опубликованной суммой.");
        File.Move(pending, setup, true);
    }

    internal static Uri AllowedAsset(string value)
    {
        var uri = new Uri(value, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Адрес файла релиза не принадлежит AA Desk.");
        return uri;
    }
    private void UpdateStatus()
    {
        Result = HasUpdate ? UpdateCheckResult.Available
            : _snapshot.CheckedAt == default ? UpdateCheckResult.Unknown
            : _snapshot.Version is null ? UpdateCheckResult.NoRelease : UpdateCheckResult.Current;
        Status = Result switch
        {
            UpdateCheckResult.Available => "Доступна версия " + _snapshot.Version,
            UpdateCheckResult.Unknown or UpdateCheckResult.NoRelease => "Опубликованных релизов пока нет.",
            _ => "Установлена последняя версия."
        };
    }
    private UpdateSnapshot ReadCache()
    {
        try { return File.Exists(_cachePath) ? JsonSerializer.Deserialize<UpdateSnapshot>(File.ReadAllText(_cachePath)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { LocalLog.Write("Update cache", ex); return new(); }
    }
    private void SaveCache()
    {
        try { Directory.CreateDirectory(LocalLog.DataDirectory); File.WriteAllText(_cachePath, JsonSerializer.Serialize(_snapshot)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LocalLog.Write("Update cache", ex); }
    }
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AA-Desk-Updater/0.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private sealed record UpdateSnapshot(DateTimeOffset CheckedAt = default, string? Version = null,
        string? ReleaseUrl = null, string? SetupUrl = null, string? HashUrl = null);
    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("assets")] List<GitHubAsset>? Assets);
    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("browser_download_url")] string? DownloadUrl);
}
