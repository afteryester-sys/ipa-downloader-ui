using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using IPAStudio.Core.Diagnostics;

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

/// <summary>Why the last update check did not yield a downloadable newer build.</summary>
public enum UpdateFailureReason
{
    None,
    /// <summary>Could not reach the server (no internet / DNS / TLS).</summary>
    Network,
    /// <summary>Request timed out.</summary>
    Timeout,
    /// <summary>Server reachable but no releases have been published yet (404).</summary>
    NoReleases,
    /// <summary>Server returned an unexpected HTTP error.</summary>
    ServerError,
    /// <summary>Response could not be understood.</summary>
    BadResponse,
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
    public UpdateFailureReason FailureReason { get; private set; } = UpdateFailureReason.None;
    /// <summary>Technical detail of the last failure (HTTP status, exception message).</summary>
    public string? LastErrorDetail { get; private set; }
    public Version CurrentVersion { get; }
    public Version? LatestVersion { get; private set; }
    public string? ReleaseNotes { get; private set; }
    public string ReleasesUrl => ReleasesPage;

    private string? _downloadUrl;
    private string? _downloadFallbackUrl;   // always the browser_download_url (public CDN)
    private string? _downloadFileName;
    private string? _downloadedInstallerPath;

    public event Action? StateChanged;

    public UpdateService(HttpClient http)
    {
        _http = http;
        CurrentVersion = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0, 0);
    }

    /// <summary>
    /// True when a usable embedded token is present. The placeholder value
    /// (left untouched by non-CI/local builds) is treated as "no token".
    /// </summary>
    private static bool HasToken =>
        !string.IsNullOrWhiteSpace(UpdateSecrets.GitHubToken) &&
        UpdateSecrets.GitHubToken != "__UPDATE_TOKEN__";

    /// <summary>
    /// Builds a GitHub API request with the standard headers and, when
    /// available, the embedded read-only token so private-repo releases can
    /// be read. <paramref name="octetStream"/> switches Accept to the raw
    /// asset download media type.
    /// </summary>
    private static HttpRequestMessage BuildRequest(string url, bool octetStream = false)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd(octetStream
            ? "application/octet-stream"
            : "application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (HasToken)
            req.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", UpdateSecrets.GitHubToken);
        return req;
    }

    private void Set(UpdateState state)
    {
        State = state;
        StateChanged?.Invoke();
    }

    /// <summary>Checks GitHub for a newer release. Safe to call repeatedly.</summary>
    public async Task<bool> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        FailureReason = UpdateFailureReason.None;
        LastErrorDetail = null;
        Set(UpdateState.Checking);
        AppLog.Info($"Update check: current v{CurrentVersion}, querying {LatestReleaseApi}");

        HttpResponseMessage response;
        try
        {
            using var request = BuildRequest(LatestReleaseApi);
            response = await _http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // GetAsync throws TaskCanceledException (an OCE) on HttpClient timeout.
            FailureReason = UpdateFailureReason.Timeout;
            LastErrorDetail = "Request timed out.";
            AppLog.Error("Update check timed out.");
            Set(UpdateState.Failed);
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            FailureReason = UpdateFailureReason.Network;
            LastErrorDetail = ex.Message;
            AppLog.Error("Update check failed: cannot reach the update server.", ex);
            Set(UpdateState.Failed);
            return false;
        }
        catch (Exception ex)
        {
            FailureReason = UpdateFailureReason.Network;
            LastErrorDetail = ex.Message;
            AppLog.Error("Update check failed (unexpected).", ex);
            Set(UpdateState.Failed);
            return false;
        }

        // The server was reached; interpret the HTTP status precisely.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            FailureReason = UpdateFailureReason.NoReleases;
            LastErrorDetail = "GitHub returned 404 (no published releases).";
            AppLog.Warn("Update check: repository has no published releases yet (HTTP 404).");
            Set(UpdateState.Failed);
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            FailureReason = UpdateFailureReason.ServerError;
            LastErrorDetail = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            AppLog.Error($"Update check: server error {LastErrorDetail}.");
            Set(UpdateState.Failed);
            return false;
        }

        GitHubRelease? release;
        try
        {
            release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            FailureReason = UpdateFailureReason.BadResponse;
            LastErrorDetail = ex.Message;
            AppLog.Error("Update check: could not parse the server response.", ex);
            Set(UpdateState.Failed);
            return false;
        }

        if (release is null || string.IsNullOrWhiteSpace(release.TagName))
        {
            FailureReason = UpdateFailureReason.NoReleases;
            LastErrorDetail = "Latest release had no tag.";
            AppLog.Warn("Update check: latest release response was empty.");
            Set(UpdateState.Failed);
            return false;
        }

        LatestVersion = ParseVersion(release.TagName);
        ReleaseNotes = release.Body;
        AppLog.Info($"Update check: latest published release is '{release.TagName}' (parsed {LatestVersion?.ToString() ?? "n/a"}).");

        // Prefer an installer asset (Setup*.exe), otherwise any .exe/.zip.
        var asset =
            release.Assets?.FirstOrDefault(a =>
                a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            ?? release.Assets?.FirstOrDefault(a =>
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            ?? release.Assets?.FirstOrDefault(a =>
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        // For a private repo we must download via the asset's API URL with a
        // token; browser_download_url only works for public repos / browsers.
        // We always keep both URLs: ApiUrl for the initial attempt (auth'd) and
        // DownloadUrl as a public CDN fallback when auth fails or is absent.
        _downloadFallbackUrl = asset?.DownloadUrl;
        _downloadUrl = HasToken
            ? (asset?.ApiUrl ?? asset?.DownloadUrl)
            : asset?.DownloadUrl;
        _downloadFileName = asset?.Name;

        if (LatestVersion is not null && LatestVersion > CurrentVersion)
        {
            AppLog.Info($"Update available: v{LatestVersion} > v{CurrentVersion}.");
            Set(UpdateState.Available);
            return true;
        }

        AppLog.Info("Update check: already on the latest version.");
        Set(UpdateState.UpToDate);
        return false;
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
            AppLog.Warn("DownloadUpdateAsync: no download URL — opening releases page.");
            OpenReleasesPage();
            return false;
        }

        Set(UpdateState.Downloading);

        // Use a firm per-download timeout so a stalled connection never freezes
        // the UI indefinitely. 3 minutes is generous for a ~20 MB installer.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var effectiveCt   = linked.Token;

        try
        {
            // Prefer the real asset name (API URLs have no filename in the path).
            var fileName = _downloadFileName;
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = Path.GetFileName(new Uri(_downloadUrl).LocalPath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "IPAStudio-Update.exe";
            var dest = Path.Combine(Path.GetTempPath(), fileName);

            AppLog.Info($"Downloading update asset: {_downloadUrl} → {dest}");

            // GitHub asset downloads always redirect from api.github.com to
            // objects.githubusercontent.com. The HttpClient follows the 302
            // automatically, but strips the Authorization header on the
            // cross-origin redirect, which is exactly what GitHub expects for
            // the final CDN hop. So we DON'T set octetStream on the API URL —
            // we set it only when downloading via browser_download_url directly.
            bool isApiUrl = _downloadUrl.Contains("api.github.com", StringComparison.OrdinalIgnoreCase);
            using var request = BuildRequest(_downloadUrl, octetStream: !isApiUrl);

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, effectiveCt);

            // If the token is invalid / expired the API returns 401; fall back
            // to the browser_download_url (public URL, no auth needed).
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                AppLog.Warn($"Asset download: got {(int)response.StatusCode}, retrying with browser_download_url.");
                response.Dispose();
                request.Dispose();

                // Retry with the public browser_download_url (no auth needed for public repos).
                if (!string.IsNullOrWhiteSpace(_downloadFallbackUrl)
                    && _downloadFallbackUrl != _downloadUrl)
                {
                    AppLog.Info("Retrying download with browser_download_url.");
                    using var fallbackReq = new HttpRequestMessage(HttpMethod.Get, _downloadFallbackUrl);
                    using var fallbackResp = await _http.SendAsync(
                        fallbackReq, HttpCompletionOption.ResponseHeadersRead, effectiveCt);
                    fallbackResp.EnsureSuccessStatusCode();

                    var total2 = fallbackResp.Content.Headers.ContentLength ?? -1;
                    var dest2   = Path.Combine(Path.GetTempPath(), _downloadFileName ?? "IPAStudio-Update.exe");
                    await using var src2  = await fallbackResp.Content.ReadAsStreamAsync(effectiveCt);
                    await using var file2 = File.Create(dest2);
                    var buf2 = new byte[81920]; long rd2 = 0; int n2;
                    while ((n2 = await src2.ReadAsync(buf2, effectiveCt)) > 0)
                    {
                        await file2.WriteAsync(buf2.AsMemory(0, n2), effectiveCt);
                        rd2 += n2;
                        if (total2 > 0) progress?.Report(Math.Min(1.0, (double)rd2 / total2));
                    }
                    _downloadedInstallerPath = dest2;
                    progress?.Report(1.0);
                    AppLog.Info($"Fallback download complete: {dest2}.");
                    Set(UpdateState.ReadyToInstall);
                    return true;
                }

                AppLog.Warn("No fallback URL available — opening releases page.");
                OpenReleasesPage();
                Set(UpdateState.Failed);
                FailureReason = UpdateFailureReason.ServerError;
                LastErrorDetail = "Authentication failed; visit the releases page to download manually.";
                return false;
            }
            else
            {
                response.EnsureSuccessStatusCode();
            }

            var total = response.Content.Headers.ContentLength ?? -1;
            AppLog.Info($"Download started, content-length: {(total > 0 ? $"{total / 1024} KB" : "unknown")}.");

            await using (var source = await response.Content.ReadAsStreamAsync(effectiveCt))
            await using (var file = File.Create(dest))
            {
                var buffer = new byte[81920];
                long read = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, effectiveCt)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, count), effectiveCt);
                    read += count;
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)read / total));
                }
            }

            _downloadedInstallerPath = dest;
            progress?.Report(1.0);
            AppLog.Info($"Download complete: {dest} ({new FileInfo(dest).Length / 1024} KB).");
            Set(UpdateState.ReadyToInstall);
            return true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            FailureReason = UpdateFailureReason.Timeout;
            LastErrorDetail = "Download timed out after 3 minutes.";
            AppLog.Error("Update download timed out.");
            Set(UpdateState.Failed);
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            FailureReason = UpdateFailureReason.Network;
            LastErrorDetail = ex.Message;
            AppLog.Error("Update download network error.", ex);
            Set(UpdateState.Failed);
            return false;
        }
        catch (Exception ex)
        {
            FailureReason = UpdateFailureReason.Network;
            LastErrorDetail = ex.Message;
            AppLog.Error("Update download failed.", ex);
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
        /// <summary>API URL of the asset — required to download from a private repo with a token.</summary>
        [JsonPropertyName("url")] public string ApiUrl { get; set; } = "";
    }
}
