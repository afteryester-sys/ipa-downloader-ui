using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Tools;

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

    private const string ReleasesListApi =
        "https://api.github.com/repos/afteryester-sys/ipa-downloader-ui/releases?per_page=30";

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

    /// <summary>
    /// Where the downloaded installer is written: the app's own sub-folder of the system temp
    /// directory, created if missing.
    ///
    /// These used to land in the temp root, which "clear cache" does not sweep — so every
    /// update left its installer, some 60 MB, on the disk forever with no way to remove it
    /// from inside the app. The sub-folder is already part of the cache sweep.
    /// </summary>
    private static string InstallerFolder()
    {
        var folder = ToolLocator.SharedTempFolder;
        Directory.CreateDirectory(folder);
        return folder;
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

        var asset = PickAsset(release);

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
    public Task<bool> DownloadUpdateAsync(
        IProgress<double>? progress = null, CancellationToken ct = default) =>
        DownloadAssetAsync(progress, ct);

    /// <summary>
    /// Core download shared by <see cref="DownloadUpdateAsync"/> and the rollback path in
    /// <see cref="DownloadReleaseAsync"/>: both just point <c>_downloadUrl</c> et al. at a
    /// different release first and then call this.
    /// </summary>
    private async Task<bool> DownloadAssetAsync(
        IProgress<double>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_downloadUrl))
        {
            AppLog.Warn("DownloadAssetAsync: no download URL — opening releases page.");
            OpenReleasesPage();
            return false;
        }

        Set(UpdateState.Downloading);

        var fileName = _downloadFileName;
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = Path.GetFileName(new Uri(_downloadUrl).LocalPath);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "IPAStudio-Update.exe";

        var dest = Path.Combine(InstallerFolder(), fileName);
        var part = dest + ".part";

        // The installer bundles the tool binaries and is ~70 MB, so the transfer is
        // measured in minutes on an ordinary connection and a single dropped
        // connection used to mean starting over. Resume from the partial file and
        // retry instead: the budget below is per stall, not for the whole download,
        // because a slow-but-moving transfer is healthy and must not be killed.
        const int maxAttempts = 6;
        var url = _downloadUrl!;
        var usingFallback = false;

        for (var attempt = 1; attempt <= maxAttempts && !ct.IsCancellationRequested; attempt++)
        {
            long resumeFrom = File.Exists(part) ? new FileInfo(part).Length : 0;

            try
            {
                // GitHub asset downloads always redirect from api.github.com to
                // objects.githubusercontent.com. HttpClient follows the 302
                // automatically and strips Authorization on that cross-origin hop,
                // which is exactly what GitHub expects — so octet-stream is only set
                // when going straight to browser_download_url.
                var isApiUrl = url.Contains("api.github.com", StringComparison.OrdinalIgnoreCase);
                using var request = usingFallback
                    ? new HttpRequestMessage(HttpMethod.Get, url)
                    : BuildRequest(url, octetStream: !isApiUrl);
                if (resumeFrom > 0)
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);

                using var response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);

                // An invalid or expired token gives 401/403; the public
                // browser_download_url needs no auth on a public repo.
                if (!usingFallback
                    && (response.StatusCode == HttpStatusCode.Unauthorized
                        || response.StatusCode == HttpStatusCode.Forbidden)
                    && !string.IsNullOrWhiteSpace(_downloadFallbackUrl)
                    && _downloadFallbackUrl != url)
                {
                    AppLog.Warn($"Asset download: got {(int)response.StatusCode}, switching to browser_download_url.");
                    url = _downloadFallbackUrl!;
                    usingFallback = true;
                    attempt--;
                    continue;
                }

                // The range is unsatisfiable when the partial file is already as long
                // as (or longer than) the asset - a leftover from an interrupted or
                // superseded download. Start clean rather than resuming into garbage.
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && resumeFrom > 0)
                {
                    AppLog.Warn("Resume rejected (416) — discarding the partial file and restarting.");
                    TryDelete(part);
                    attempt--;
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized
                    || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    AppLog.Warn("No usable download URL — opening the releases page.");
                    OpenReleasesPage();
                    Set(UpdateState.Failed);
                    FailureReason = UpdateFailureReason.ServerError;
                    LastErrorDetail = "Authentication failed; visit the releases page to download manually.";
                    return false;
                }

                response.EnsureSuccessStatusCode();

                // A server that ignores Range answers 200 with the whole asset, in
                // which case appending would corrupt the file.
                var resumed = response.StatusCode == HttpStatusCode.PartialContent;
                if (resumeFrom > 0 && !resumed)
                {
                    AppLog.Warn("Server ignored the Range header — restarting the download from zero.");
                    resumeFrom = 0;
                }

                var total = response.Content.Headers.ContentLength is { } len && len >= 0
                    ? len + (resumed ? resumeFrom : 0)
                    : -1;
                AppLog.Info(resumeFrom > 0
                    ? $"Resuming update download at {resumeFrom / 1024} KB (attempt {attempt}/{maxAttempts})."
                    : $"Downloading update asset: {url} → {dest} (attempt {attempt}/{maxAttempts}).");

                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                await using (var file = new FileStream(
                    part,
                    resumeFrom > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    var read = resumeFrom;

                    while (true)
                    {
                        // Cancel only when the transfer actually stops producing
                        // bytes. The previous fixed three-minute ceiling on the whole
                        // download could not fit a 70 MB installer on any connection
                        // slower than ~400 KB/s and aborted a perfectly healthy
                        // transfer.
                        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        stall.CancelAfter(TimeSpan.FromSeconds(45));

                        int count;
                        try
                        {
                            count = await source.ReadAsync(buffer, stall.Token);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            throw new IOException("The download stalled for 45 seconds.");
                        }

                        if (count <= 0) break;

                        await file.WriteAsync(buffer.AsMemory(0, count), ct);
                        read += count;
                        if (total > 0) progress?.Report(Math.Min(1.0, (double)read / total));
                    }

                    // A truncated body still ends the read loop cleanly, so verify the
                    // length rather than trusting the stream, or a half-installer
                    // would be handed to the user as ready to install.
                    if (total > 0 && read < total)
                        throw new IOException($"Connection closed after {read} of {total} bytes.");
                }

                File.Move(part, dest, overwrite: true);
                _downloadedInstallerPath = dest;
                progress?.Report(1.0);
                AppLog.Info($"Download complete: {dest} ({new FileInfo(dest).Length / 1024} KB).");
                Set(UpdateState.ReadyToInstall);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            // An OperationCanceledException while the caller's token is still unset is
            // HttpClient's own per-request ceiling firing, which on a slow link means a
            // transfer worth resuming rather than a reason to give up.
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                var progressed = File.Exists(part) && new FileInfo(part).Length > resumeFrom;
                AppLog.Warn($"Update download attempt {attempt}/{maxAttempts} failed ({ex.Message})."
                            + (progressed ? " Partial data kept for resume." : string.Empty));

                if (attempt == maxAttempts)
                {
                    FailureReason = UpdateFailureReason.Network;
                    LastErrorDetail = ex.Message;
                    AppLog.Error("Update download failed after retrying.", ex);
                    Set(UpdateState.Failed);
                    return false;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                }
                catch (OperationCanceledException) { throw; }
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

        ct.ThrowIfCancellationRequested();

        FailureReason = UpdateFailureReason.Network;
        LastErrorDetail = "The download could not be completed.";
        Set(UpdateState.Failed);
        return false;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { AppLog.Warn($"Could not delete '{path}': {ex.Message}"); }
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

    /// <summary>
    /// Lists published releases with an installer asset attached, newest first, for the
    /// rollback picker. Unlike <see cref="CheckForUpdatesAsync"/> this does not touch
    /// <see cref="State"/> — it is a lookup, not a check, and the corner dot has nothing to
    /// say about it either way.
    /// </summary>
    public async Task<List<ReleaseSummary>> ListReleasesAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = BuildRequest(ReleasesListApi);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Warn($"ListReleasesAsync: HTTP {(int)response.StatusCode}.");
                return [];
            }

            var releases = await response.Content.ReadFromJsonAsync<List<GitHubRelease>>(cancellationToken: ct);
            if (releases is null) return [];

            var result = new List<ReleaseSummary>();
            foreach (var r in releases)
            {
                if (string.IsNullOrWhiteSpace(r.TagName)) continue;
                if (PickAsset(r) is null) continue; // nothing installable in this release
                result.Add(new ReleaseSummary(r.TagName, ParseVersion(r.TagName), r.PublishedAt));
            }
            return result;
        }
        catch (Exception ex)
        {
            AppLog.Error("ListReleasesAsync failed.", ex);
            return [];
        }
    }

    /// <summary>
    /// Downloads the installer asset for a specific past release by tag, for the password-
    /// gated rollback tool. Shares the same download core and the same
    /// <see cref="LaunchInstaller"/> hand-off as a normal update, so a rollback runs the
    /// real installer for that build rather than trying to reconstruct one.
    /// </summary>
    public async Task<bool> DownloadReleaseAsync(
        string tagName, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        GitHubRelease? release;
        try
        {
            using var request = BuildRequest(
                $"https://api.github.com/repos/afteryester-sys/ipa-downloader-ui/releases/tags/{Uri.EscapeDataString(tagName)}");
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Warn($"DownloadReleaseAsync: HTTP {(int)response.StatusCode} for tag '{tagName}'.");
                FailureReason = UpdateFailureReason.ServerError;
                LastErrorDetail = $"HTTP {(int)response.StatusCode}";
                Set(UpdateState.Failed);
                return false;
            }
            release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            AppLog.Error($"DownloadReleaseAsync failed to fetch release '{tagName}'.", ex);
            FailureReason = UpdateFailureReason.Network;
            LastErrorDetail = ex.Message;
            Set(UpdateState.Failed);
            return false;
        }

        var asset = release is null ? null : PickAsset(release);
        if (asset is null)
        {
            AppLog.Warn($"DownloadReleaseAsync: release '{tagName}' has no installable asset.");
            FailureReason = UpdateFailureReason.NoReleases;
            LastErrorDetail = "That release has no installer attached.";
            Set(UpdateState.Failed);
            return false;
        }

        _downloadFallbackUrl = asset.DownloadUrl;
        _downloadUrl = HasToken ? (asset.ApiUrl.Length > 0 ? asset.ApiUrl : asset.DownloadUrl) : asset.DownloadUrl;
        _downloadFileName = asset.Name;

        AppLog.Info($"Rollback: downloading '{tagName}' asset '{asset.Name}'.");
        return await DownloadAssetAsync(progress, ct);
    }

    /// <summary>Prefers an installer (Setup*.exe), otherwise any .exe/.zip — same rule the
    /// update check uses, kept in one place so rollback picks the same file a normal
    /// update would have.</summary>
    private static GitHubAsset? PickAsset(GitHubRelease release) =>
        release.Assets?.FirstOrDefault(a =>
            a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        ?? release.Assets?.FirstOrDefault(a =>
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        ?? release.Assets?.FirstOrDefault(a =>
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

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
        [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
        /// <summary>API URL of the asset — required to download from a private repo with a token.</summary>
        [JsonPropertyName("url")] public string ApiUrl { get; set; } = "";
    }
}

/// <summary>One entry in the rollback picker.</summary>
public sealed record ReleaseSummary(string Tag, Version? Version, DateTimeOffset? PublishedAt)
{
    public string DisplayVersion => Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : Tag;
    public string DisplayDate => PublishedAt is { } d ? d.LocalDateTime.ToString("dd.MM.yyyy") : "";
}
