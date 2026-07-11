using System.Text.Json;
using System.Text.RegularExpressions;
using IPAStudio.Core.Models;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services;

/// <summary>Progress snapshot reported while downloading an IPA.</summary>
public readonly record struct DownloadProgress(double Percent, long DownloadedBytes, long TotalBytes, double SpeedBps);

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

    [GeneratedRegex(@"(\d{1,3}(?:[.,]\d+)?)\s*%")]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"license.*required|not.*purchased|purchase.*required|9610", RegexOptions.IgnoreCase)]
    private static partial Regex LicenseRequiredRegex();

    public DownloadService(ToolLocator tools, ProcessRunner runner)
    {
        _tools = tools;
        _runner = runner;
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
                new[] { "purchase", "-i", appId.ToString(), "--format", "json", "--non-interactive",
                        "--keychain-passphrase", ToolLocator.KeychainPassphrase },
                ct: ct).ConfigureAwait(false);

            if (result.Success) return LicenseState.Owned;

            var output = result.CombinedOutput;
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
            new[] { "purchase", "-i", appId.ToString(), "--format", "json", "--non-interactive",
                    "--keychain-passphrase", ToolLocator.KeychainPassphrase },
            ct: ct).ConfigureAwait(false);

        if (result.Success || result.CombinedOutput.Contains("already", StringComparison.OrdinalIgnoreCase))
            return (true, null);

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

        var safeName = string.Join("_", app.Name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var version = app.LatestVersion ?? "latest";
        var outputPath = Path.Combine(_tools.AppsFolder, $"{safeName}_{app.AppStoreId}_{version}.ipa");

        var args = new List<string>
        {
            "download",
            "-i", app.AppStoreId.ToString(),
            "-o", outputPath,
            "--format", "json",
            "--non-interactive",
            "--keychain-passphrase", ToolLocator.KeychainPassphrase,
        };
        if (autoPurchase) args.Add("--purchase");

        var totalBytes = app.FileSizeBytes ?? 0L;
        var lastBytes = 0L;
        var lastTime = DateTimeOffset.Now;

        void ParseLine(string line)
        {
            // ipatool renders a progress bar / percentage on stdout; extract "NN%".
            var match = PercentRegex().Match(line);
            if (!match.Success) return;

            if (!double.TryParse(match.Groups[1].Value.Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var percent))
                return;

            percent = Math.Clamp(percent, 0, 100);
            var downloaded = totalBytes > 0 ? (long)(totalBytes * percent / 100.0) : 0L;

            var now = DateTimeOffset.Now;
            var elapsed = (now - lastTime).TotalSeconds;
            var speed = elapsed > 0.2 && downloaded > lastBytes
                ? (downloaded - lastBytes) / elapsed
                : 0;
            if (elapsed > 0.2)
            {
                lastBytes = downloaded;
                lastTime = now;
            }

            progress?.Report(new DownloadProgress(percent, downloaded, totalBytes, speed));
        }

        var result = await _runner.RunAsync(
            _tools.IpatoolPath, args,
            onOutputLine: ParseLine,
            onErrorLine: ParseLine,
            ct: ct).ConfigureAwait(false);

        if (result.Success && File.Exists(outputPath))
        {
            progress?.Report(new DownloadProgress(100, totalBytes, totalBytes, 0));
            return DownloadResult.Ok(outputPath);
        }

        // ipatool may write to a different resolved path; try to parse it from JSON output.
        var resolvedPath = ExtractOutputPath(result.CombinedOutput);
        if (result.Success && resolvedPath is not null && File.Exists(resolvedPath))
            return DownloadResult.Ok(resolvedPath);

        return DownloadResult.Fail(ExtractError(result.CombinedOutput));
    }

    /// <summary>Searches the App Store (ipatool search).</summary>
    public async Task<IReadOnlyList<AppEntry>> SearchAsync(string term, int limit = 20, CancellationToken ct = default)
    {
        var result = await _runner.RunAsync(
            _tools.IpatoolPath,
            new[] { "search", term, "--limit", limit.ToString(), "--format", "json", "--non-interactive",
                    "--keychain-passphrase", ToolLocator.KeychainPassphrase },
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
            new[] { "download", "-i", appId.ToString(), "--list-versions", "--format", "json", "--non-interactive",
                    "--keychain-passphrase", ToolLocator.KeychainPassphrase },
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
