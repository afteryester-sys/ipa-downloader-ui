using System.Text.RegularExpressions;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services;

/// <summary>Progress snapshot reported while installing onto a device.</summary>
public readonly record struct InstallProgress(double Percent, string Status);

/// <summary>Result of an install attempt.</summary>
public sealed class InstallResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public static InstallResult Ok() => new() { Success = true };
    public static InstallResult Fail(string error) => new() { Error = error };
}

/// <summary>
/// Installs IPA files onto a connected device via ideviceinstaller and lists
/// installed apps for status badges. Device installs must run one at a time.
/// </summary>
public sealed partial class InstallService
{
    private readonly ToolLocator _tools;
    private readonly ProcessRunner _runner;
    private readonly SemaphoreSlim _deviceLock = new(1, 1);

    [GeneratedRegex(@"(\d{1,3})\s*%")]
    private static partial Regex PercentRegex();

    public InstallService(ToolLocator tools, ProcessRunner runner)
    {
        _tools = tools;
        _runner = runner;
    }

    /// <summary>
    /// Installs an IPA on the device, reporting stage/percent progress parsed from
    /// ideviceinstaller output ("Copying ...", "Installing (NN%)", "Install: Complete").
    /// Serialized per process: only one install runs at a time.
    /// </summary>
    public async Task<InstallResult> InstallAsync(
        string udid,
        string ipaPath,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(ipaPath))
            return InstallResult.Fail($"IPA file not found: {ipaPath}");

        await _deviceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var failed = false;
            string? errorLine = null;

            void ParseLine(string line)
            {
                // ideviceinstaller emits phase lines such as:
                //   "Copying '...' to device..."
                //   "Installing 'com.bundle.id'"
                //   "CreatingStagingDirectory (5%)"  /  "Install: Complete (100%)"
                //   "Complete"
                var match = PercentRegex().Match(line);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var pct))
                {
                    // The device reports the real 0-100 percentage; use it directly so
                    // the bar reflects genuine install progress.
                    var status = line.Contains("Complete", StringComparison.OrdinalIgnoreCase)
                        ? "Complete"
                        : "Installing";
                    progress?.Report(new InstallProgress(Math.Clamp(pct, 1, 100), status));
                }
                else if (line.Contains("Copying", StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report(new InstallProgress(3, "Copying"));
                }
                else if (line.Contains("Installing", StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report(new InstallProgress(6, "Installing"));
                }
                else if (line.Contains("Complete", StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report(new InstallProgress(100, "Complete"));
                }

                if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("failed", StringComparison.OrdinalIgnoreCase))
                {
                    failed = true;
                    errorLine = line.Trim();
                }
            }

            // Bundled ideviceinstaller (libimobiledevice 1.x) selects the mode via a
            // flag: -i/--install ARCHIVE (NOT an "install" subcommand). Using a
            // subcommand caused "ERROR: No mode/command was supplied."
            var result = await _runner.RunAsync(
                _tools.IdeviceInstallerPath,
                new[] { "-u", udid, "-i", ipaPath },
                onOutputLine: ParseLine,
                onErrorLine: ParseLine,
                closeStdin: true,
                ct: ct).ConfigureAwait(false);

            if (result.Success && !failed)
                return InstallResult.Ok();

            return InstallResult.Fail(errorLine ?? Truncate(result.CombinedOutput) ?? "Installation failed");
        }
        finally
        {
            _deviceLock.Release();
        }
    }

    /// <summary>
    /// Returns bundle IDs of apps installed on the device
    /// (ideviceinstaller -u UDID list, lines: "bundleid, \"version\", \"name\"").
    /// </summary>
    public async Task<IReadOnlySet<string>> GetInstalledBundleIdsAsync(string udid, CancellationToken ct = default)
    {
        var bundleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var result = await _runner.RunAsync(
                _tools.IdeviceInstallerPath,
                new[] { "-u", udid, "-l" },
                closeStdin: true,
                ct: ct).ConfigureAwait(false);

            foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var idx = line.IndexOf(',');
                var candidate = (idx > 0 ? line[..idx] : line).Trim();
                if (candidate.Contains('.') && !candidate.Contains(' '))
                    bundleIds.Add(candidate);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Device disconnected or tool failure; return what we have.
        }
        return bundleIds;
    }

    private static string? Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }
}
