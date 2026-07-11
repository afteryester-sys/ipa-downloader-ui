using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace IPAStudio.Core.Services;

public enum UpdateState
{
    Unknown,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToInstall,
    Failed,
}

/// <summary>
/// In-app updater. Queries the GitHub Releases API of the project repository,
/// compares the latest published tag with the running assembly version and,
/// when a newer build exists, downloads the installer asset and launches it.
/// </summary>
public sealed class UpdateService
{
    // GitHub repository that publishes releases with the installer attached.
    private const string LatestReleaseApi =
        "https://api.github.com/repos/afteryester-sys/ipa-downloader-ui/releases/latest";

    private const string ReleasesPage =
        "https://github.com/afteryester-sys/ipa-downloader-ui/releases/latest";

    private readonly HttpClient _http;

    public UpdateState State { get; private set; } = UpdateState.Unknown;
    public Version CurrentVersion { get; }
    public Version? LatestVersion { get; private set; }
    public string? ReleaseNotes { get; private set; }
    public string ReleasesUrl => ReleasesPage;

    private string? _downloadUrl;
    private string? _downloadedInstallerPath;

    public event Action? StateChanged;

    public UpdateService(HttpClient http)
    {
        _http = http;
        CurrentVersion = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0, 0);
    }

    private void Set(UpdateState state)
    {
        State = state;
        StateChanged?.Invoke();
    }

    /// <summary>Checks GitHub for a newer release. Safe to call repeatedly.</summary>
    public async Task<bool> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        Set(UpdateState.Checking);
        try
        {
            var release = await _http.GetFromJsonAsync<GitHubRelease>(LatestReleaseApi, ct);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                Set(UpdateState.Failed);
                return false;
            }

            LatestVersion = ParseVersion(release.TagName);
            ReleaseNotes = release.Body;

            // Prefer an installer asset (Setup*.exe), otherwise any .exe/.zip.
            var asset =
                release.Assets?.FirstOrDefault(a =>
                    a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase) &&
                    a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                ?? release.Assets?.FirstOrDefault(a =>
                    a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                ?? release.Assets?.FirstOrDefault(a =>
                    a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

            _downloadUrl = asset?.DownloadUrl;

            if (LatestVersion is not null && LatestVersion > CurrentVersion)
            {
                Set(UpdateState.Available);
                return true;
            }

            Set(UpdateState.UpToDate);
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            Set(UpdateState.Failed);
            return false;
        }
    }

    /// <summary>
    /// Downloads the installer for the latest release. If no direct asset is
    /// available, opens the releases page in the browser instead.
    /// </summary>
    public async Task<bool> DownloadUpdateAsync(
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_downloadUrl))
        {
            OpenReleasesPage();
            return false;
        }

        Set(UpdateState.Downloading);
        try
        {
            var fileName = Path.GetFileName(new Uri(_downloadUrl).LocalPath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "IPAStudio-Update.exe";
            var dest = Path.Combine(Path.GetTempPath(), fileName);

            using var response = await _http.GetAsync(
                _downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1;
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = File.Create(dest))
            {
                var buffer = new byte[81920];
                long read = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, count), ct);
                    read += count;
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)read / total));
                }
            }

            _downloadedInstallerPath = dest;
            progress?.Report(1.0);
            Set(UpdateState.ReadyToInstall);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            Set(UpdateState.Failed);
            return false;
        }
    }

    /// <summary>
    /// Launches the downloaded installer and requests the app to exit so the
    /// files can be replaced. Returns false if nothing has been downloaded.
    /// </summary>
    public bool LaunchInstaller()
    {
        if (string.IsNullOrWhiteSpace(_downloadedInstallerPath) ||
            !File.Exists(_downloadedInstallerPath))
            return false;

        try
        {
            var isExe = _downloadedInstallerPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            if (isExe)
            {
                Process.Start(new ProcessStartInfo(_downloadedInstallerPath)
                {
                    UseShellExecute = true,
                });
            }
            else
            {
                // Portable ZIP: reveal it in Explorer for a manual replace.
                Process.Start(new ProcessStartInfo("explorer.exe",
                    $"/select,\"{_downloadedInstallerPath}\"")
                {
                    UseShellExecute = true,
                });
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true });
        }
        catch { /* best effort */ }
    }

    private static Version? ParseVersion(string tag)
    {
        // Tags look like "v1.2.3" or "1.2.3".
        var cleaned = tag.TrimStart('v', 'V').Trim();
        var dash = cleaned.IndexOf('-');
        if (dash > 0) cleaned = cleaned[..dash];
        return Version.TryParse(cleaned, out var v) ? v : null;
    }

    // ------------------------------------------------------ GitHub API models

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
    }
}
