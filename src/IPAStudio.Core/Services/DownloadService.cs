using System.Text.Json;
using System.Text.RegularExpressions;
using IPAStudio.Core.Models;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services;

/// <summary>Progress snapshot reported while downloading an IPA.</summary>
/// <param name="Finalizing">
/// True once the raw byte transfer is done and ipatool is repackaging the archive
/// (license/sinf injection) — a phase that can take several seconds with no byte
/// movement. Lets the UI show "packaging" instead of a frozen bar.
/// </param>
public readonly record struct DownloadProgress(
    double Percent, long DownloadedBytes, long TotalBytes, double SpeedBps, bool Finalizing = false);

/// <summary>Result of a completed download.</summary>
public sealed class DownloadResult
{
    public bool Success { get; init; }
    public string? IpaPath { get; init; }
    public string? Error { get; init; }
    public static DownloadResult Ok(string path) => new() { Success = true, IpaPath = path };
    public static DownloadResult Fail(string error) => new() { Error = error };
}

/// <summary>
/// App Store operations via ipatool:
///   search, purchase (obtain license), download (with progress), list-versions.
/// </summary>
public sealed partial class DownloadService
{
    private readonly ToolLocator _tools;
    private readonly ProcessRunner _runner;
    private readonly HttpClient _http;

    [GeneratedRegex(@"(\d{1,3}(?:[.,]\d+)?)\s*%")]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"license.*required|not.*purchased|purchase.*required|9610", RegexOptions.IgnoreCase)]
    private static partial Regex LicenseRequiredRegex();

    /// <summary>Canonical error string used when the ipatool session has expired.
    /// QueueService and the UI key on this to redirect the user to the login screen.</summary>
    public const string SessionExpiredMessage =
        "SESSION_EXPIRED: account file is not protected. Please sign in again.";

    public DownloadService(ToolLocator tools, ProcessRunner runner, HttpClient http)
    {
        _tools = tools;
        _runner = runner;
        _http = http;
    }

    /// <summary>
    /// Checks whether the signed-in Apple ID owns a license for the app.
    /// Implemented by asking ipatool to download without --purchase into a probe
    /// run that is cancelled early; a cheaper heuristic is the license error string.
    /// In practice we simply run "purchase" which is idempotent: it succeeds fast
    /// when the license already exists.
    /// </summary>
    public async Task<LicenseState> CheckLicenseAsync(long appId, CancellationToken ct = default)
    {
        try
        {
            var result = await _runner.RunAsync(
                _tools.IpatoolPath,
                new[] { "purchase", "-i", appId.ToString(), "--keychain-passphrase", ToolLocator.KeychainPassphrase,
                        "--format", "json" },
                closeStdin: true,
                ct: ct).ConfigureAwait(false);

            if (result.Success) return LicenseState.Owned;

            var output = result.CombinedOutput;

            // Session is stale / keychain unprotected -> bubble up so the UI can re-login.
            if (AuthService.IsSessionExpiredError(output))
                return LicenseState.SessionExpired;

            // "already purchased" style errors also mean the license exists.
            if (output.Contains("already", StringComparison.OrdinalIgnoreCase))
                return LicenseState.Owned;
            if (LicenseRequiredRegex().IsMatch(output) ||
                output.Contains("price", StringComparison.OrdinalIgnoreCase))
                return LicenseState.NotOwned;

            return LicenseState.CheckFailed;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return LicenseState.CheckFailed;
        }
    }

    /// <summary>Obtains a license for a free app (ipatool purchase).</summary>
    public async Task<(bool Success, string? Error)> PurchaseAsync(long appId, CancellationToken ct = default)
    {
        var result = await _runner.RunAsync(
            _tools.IpatoolPath,
            new[] { "purchase", "-i", appId.ToString(), "--keychain-passphrase", ToolLocator.KeychainPassphrase,
                    "--format", "json" },
            closeStdin: true,
            ct: ct).ConfigureAwait(false);

        if (result.Success || result.CombinedOutput.Contains("already", StringComparison.OrdinalIgnoreCase))
            return (true, null);

        if (AuthService.IsSessionExpiredError(result.CombinedOutput))
            return (false, SessionExpiredMessage);

        return (false, ExtractError(result.CombinedOutput));
    }

    /// <summary>
    /// Downloads the IPA into the Apps folder, reporting live progress parsed
    /// from ipatool output. Output file: Name_AppID_Version.ipa
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(
        AppEntry app,
        bool autoPurchase = true,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        _tools.EnsureFolders();

        // IMPORTANT: the output path is handed to native tools (ipatool v3 is Go +
        // C++ nlohmann/json + libzip). Non-ASCII bytes in the path break them:
        //   - nlohmann json.dump() throws "invalid UTF-8 byte" (type_error.316)
        //   - libzip zip_open fails with ENOENT (18) on the mangled name
        // So we build a strictly ASCII-safe filename (transliterating Cyrillic
        // for readability) and fall back to the bundle id / app id if nothing
        // usable remains.
        var safeName = MakeAsciiSafeName(app.Name);
        if (string.IsNullOrEmpty(safeName))
            safeName = MakeAsciiSafeName(app.BundleId ?? "") ;
        if (string.IsNullOrEmpty(safeName))
            safeName = "app";

        var version = MakeAsciiSafeName(app.LatestVersion ?? "latest");
        if (string.IsNullOrEmpty(version)) version = "latest";

        var outputPath = Path.Combine(_tools.AppsFolder, $"{safeName}_{app.AppStoreId}_{version}.ipa");

        var args = new List<string>
        {
            "download",
            "-i", app.AppStoreId.ToString(),
            "-o", outputPath,
            "--keychain-passphrase", ToolLocator.KeychainPassphrase,
            "--format", "json",
        };
        if (autoPurchase) args.Add("--purchase");

        // We need the total size to show a real percentage. It usually comes from the
        // catalog (iTunes lookup), but apps added via search/Bundle-ID may not carry
        // it — in that case look it up now by App Store id so the progress bar can fill
        // instead of just showing raw downloaded bytes.
        var totalBytes = app.FileSizeBytes ?? 0L;
        if (totalBytes <= 0 && app.AppStoreId > 0)
        {
            totalBytes = await TryLookupFileSizeAsync(app.AppStoreId, ct).ConfigureAwait(false);
            if (totalBytes > 0) app.FileSizeBytes = totalBytes;
        }

        // Under "--format json" ipatool prints NO textual progress bar (only a final
        // JSON line), so parsing stdout for a percentage never fires. Instead we watch
        // the output file grow on disk and derive an accurate percentage from its size
        // versus the known total (from the iTunes lookup). Falls back to text parsing
        // if the tool ever does emit "NN%".
        void ParseLine(string line)
        {
            var match = PercentRegex().Match(line);
            if (!match.Success) return;
            if (!double.TryParse(match.Groups[1].Value.Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var percent))
                return;
            percent = Math.Clamp(percent, 0, 100);
            var downloaded = totalBytes > 0 ? (long)(totalBytes * percent / 100.0) : 0L;
            progress?.Report(new DownloadProgress(percent, downloaded, totalBytes, 0));
        }

        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pollTask = PollFileProgressAsync(outputPath, totalBytes, progress, pollCts.Token);

        var result = await _runner.RunAsync(
            _tools.IpatoolPath, args,
            onOutputLine: ParseLine,
            onErrorLine: ParseLine,
            closeStdin: true,
            ct: ct).ConfigureAwait(false);

        pollCts.Cancel();
        try { await pollTask.ConfigureAwait(false); } catch { /* ignore poller shutdown */ }

        if (result.Success && File.Exists(outputPath))
        {
            progress?.Report(new DownloadProgress(100, totalBytes, totalBytes, 0));
            return DownloadResult.Ok(outputPath);
        }

        // ipatool may write to a different resolved path; try to parse it from JSON output.
        var resolvedPath = ExtractOutputPath(result.CombinedOutput);
        if (result.Success && resolvedPath is not null && File.Exists(resolvedPath))
            return DownloadResult.Ok(resolvedPath);

        // Session expired / keychain unprotected -> return a recognisable error string.
        if (AuthService.IsSessionExpiredError(result.CombinedOutput))
            return DownloadResult.Fail(SessionExpiredMessage);

        return DownloadResult.Fail(ExtractError(result.CombinedOutput));
    }

    /// <summary>
    /// Looks up an app's IPA size (bytes) from the public iTunes Lookup API by its
    /// App Store id. Returns 0 on any failure so the caller falls back to an
    /// indeterminate/bytes-only display. Bounded by a short timeout so it can never
    /// delay the download start noticeably.
    /// </summary>
    private async Task<long> TryLookupFileSizeAsync(long appId, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));

            var url = $"https://itunes.apple.com/lookup?id={appId}&entity=software";
            using var response = await _http.GetAsync(url, timeoutCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var body = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: timeoutCts.Token).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("results", out var results))
            {
                foreach (var item in results.EnumerateArray())
                {
                    if (!item.TryGetProperty("fileSizeBytes", out var size)) continue;
                    var bytes = size.ValueKind == JsonValueKind.String
                        ? long.TryParse(size.GetString(), out var parsed) ? parsed : 0
                        : size.GetInt64();
                    if (bytes > 0) return bytes;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* network/timeout — fall back to bytes-only display */ }
        return 0;
    }

    /// <summary>
    /// Polls the growing download file every 100 ms and reports accurate byte/percent
    /// progress derived from the real on-disk file size (independent of ipatool's
    /// output format). Also detects the post-transfer "finalizing" phase — when the
    /// bytes stop growing near completion but ipatool is still running (repackaging /
    /// license injection) — so the UI can show activity instead of a frozen bar.
    /// </summary>
    private static async Task PollFileProgressAsync(
        string outputPath, long totalBytes, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (progress is null) return;

        var dir = Path.GetDirectoryName(outputPath)!;
        var stem = Path.GetFileNameWithoutExtension(outputPath);

        // Speed over a ~0.5 s sliding window (smooth, not per-tick noisy).
        long windowBytes = 0;
        var windowTime = DateTimeOffset.UtcNow;
        double lastSpeed = 0;

        // Stall detection for the finalizing phase.
        long lastSize = 0;
        var lastGrowth = DateTimeOffset.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);

                long size = 0;
                try
                {
                    var fi = new FileInfo(outputPath);
                    if (fi.Exists)
                    {
                        size = fi.Length;
                    }
                    else if (Directory.Exists(dir))
                    {
                        // ipatool may stream into a temp/partial file first (e.g. "name.ipa.part").
                        var partial = new DirectoryInfo(dir)
                            .GetFiles("*", SearchOption.TopDirectoryOnly)
                            .Where(f => f.Name.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(f => f.Length)
                            .FirstOrDefault();
                        if (partial is not null) size = partial.Length;
                    }
                }
                catch { /* file locked mid-write; try again next tick */ }

                if (size <= 0) continue;

                var now = DateTimeOffset.UtcNow;

                // Track growth for stall/finalize detection.
                if (size > lastSize)
                {
                    lastSize = size;
                    lastGrowth = now;
                }

                // Speed: refresh roughly twice a second.
                var windowElapsed = (now - windowTime).TotalSeconds;
                if (windowElapsed >= 0.5)
                {
                    lastSpeed = size > windowBytes ? (size - windowBytes) / windowElapsed : 0;
                    windowBytes = size;
                    windowTime = now;
                }

                // Use the larger of the estimate and the actual size so we never get
                // stuck reporting 99% while bytes are clearly still arriving (the
                // iTunes estimate can be a little smaller than the real download).
                var effectiveTotal = Math.Max(totalBytes, size);
                var percent = effectiveTotal > 0
                    ? Math.Clamp(size / (double)effectiveTotal * 100.0, 0, 99)
                    : 0;

                // Finalizing: bytes have essentially stopped near the end, but the
                // ipatool process is still running (repackaging the archive). Require
                // "near complete" (>=90% of the estimate — the real file can be a bit
                // smaller than iTunes reports) AND "no growth for >2 s" so a brief
                // mid-download network stall isn't mistaken for finalizing.
                var stalledFor = (now - lastGrowth).TotalSeconds;
                var nearComplete = totalBytes > 0 && size >= totalBytes * 0.90;
                var finalizing = nearComplete && stalledFor > 2.0;

                progress.Report(new DownloadProgress(
                    finalizing ? 99 : percent, size, effectiveTotal,
                    finalizing ? 0 : lastSpeed, finalizing));
            }
        }
        catch (OperationCanceledException) { /* expected on completion */ }
    }

    /// <summary>Searches the App Store (ipatool search).</summary>
    public async Task<IReadOnlyList<AppEntry>> SearchAsync(string term, int limit = 20, CancellationToken ct = default)
    {
        var result = await _runner.RunAsync(
            _tools.IpatoolPath,
            new[] { "search", term, "-l", limit.ToString(), "--keychain-passphrase", ToolLocator.KeychainPassphrase,
                    "--format", "json" },
            closeStdin: true,
            ct: ct).ConfigureAwait(false);

        var apps = new List<AppEntry>();
        if (!result.Success) return apps;

        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("apps", out var array)) continue;

                foreach (var item in array.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idEl) ? idEl.GetInt64() : 0;
                    var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    if (id == 0 || name is null) continue;

                    apps.Add(new AppEntry
                    {
                        Name = name,
                        AppStoreId = id,
                        BundleId = item.TryGetProperty("bundleID", out var b) ? b.GetString() : null,
                        LatestVersion = item.TryGetProperty("version", out var v) ? v.GetString() : null,
                    });
                }
            }
            catch (JsonException) { }
        }
        return apps;
    }

    /// <summary>Lists available external version identifiers (ipatool v3+ only).</summary>
    public async Task<IReadOnlyList<string>> ListVersionsAsync(long appId, CancellationToken ct = default)
    {
        var result = await _runner.RunAsync(
            _tools.IpatoolPath,
            new[] { "list-versions", "-i", appId.ToString(), "--keychain-passphrase", ToolLocator.KeychainPassphrase,
                    "--format", "json" },
            closeStdin: true,
            ct: ct).ConfigureAwait(false);

        var versions = new List<string>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("externalVersions", out var array))
                    versions.AddRange(array.EnumerateArray()
                        .Select(v => v.ToString())
                        .Where(v => !string.IsNullOrEmpty(v)));
            }
            catch (JsonException) { }
        }
        return versions;
    }

    // Russian Cyrillic -> Latin transliteration table (covers the common case
    // for App Store names shown to Russian users). Anything not covered here
    // and not already ASCII is dropped.
    private static readonly Dictionary<char, string> Translit = new()
    {
        ['а']="a",['б']="b",['в']="v",['г']="g",['д']="d",['е']="e",['ё']="e",
        ['ж']="zh",['з']="z",['и']="i",['й']="y",['к']="k",['л']="l",['м']="m",
        ['н']="n",['о']="o",['п']="p",['р']="r",['с']="s",['т']="t",['у']="u",
        ['ф']="f",['х']="h",['ц']="ts",['ч']="ch",['ш']="sh",['щ']="sch",
        ['ъ']="",['ы']="y",['ь']="",['э']="e",['ю']="yu",['я']="ya",
    };

    /// <summary>
    /// Produces a strictly ASCII, filesystem-safe token from an arbitrary app
    /// name. Cyrillic is transliterated; every remaining non-[A-Za-z0-9] run is
    /// collapsed to a single underscore. Returns "" when nothing usable remains.
    /// </summary>
    private static string MakeAsciiSafeName(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";

        var sb = new System.Text.StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (ch <= 0x7F && (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_'))
            {
                sb.Append(ch);
            }
            else if (Translit.TryGetValue(char.ToLowerInvariant(ch), out var mapped))
            {
                // Preserve capitalisation of the first letter for readability.
                if (char.IsUpper(ch) && mapped.Length > 0)
                    sb.Append(char.ToUpperInvariant(mapped[0])).Append(mapped.AsSpan(1));
                else
                    sb.Append(mapped);
            }
            else
            {
                sb.Append('_');
            }
        }

        // Collapse repeated / leading / trailing underscores.
        var collapsed = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "_+", "_");
        return collapsed.Trim('_', '.');
    }

    private static string? ExtractOutputPath(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("output", out var path))
                    return path.GetString();
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static string ExtractError(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("error", out var err))
                    return err.GetString() ?? line;
            }
            catch (JsonException) { }
        }
        return string.IsNullOrWhiteSpace(output) ? "Unknown error" : output.Trim();
    }
}
