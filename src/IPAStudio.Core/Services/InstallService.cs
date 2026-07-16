using System.Text.RegularExpressions;
using IPAStudio.Core.Diagnostics;
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

        // The bundled ideviceinstaller uses libzip, whose zip_open() opens files with
        // the narrow (ANSI) CRT and therefore FAILS ("zip_open: ...: 18" = ZIP_ER_OPEN)
        // when the path contains characters outside the current code page — e.g. a
        // Cyrillic folder name like "C:\...\iPa Файлы\MAX.ipa". To be safe we stage any
        // non-ASCII path into a guaranteed-ASCII folder and install from there.
        string installPath = ipaPath;
        string? stagedCopy = null;
        if (!IsAsciiPath(ipaPath))
        {
            try
            {
                stagedCopy = CreateAsciiStagingCopy(ipaPath);
                installPath = stagedCopy;
                AppLog.Info($"IPA path has non-ASCII chars; staged to ASCII path: {installPath}");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to stage IPA to ASCII path, using original: {ex.Message}");
                installPath = ipaPath;
            }
        }

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
                new[] { "-u", udid, "-i", installPath },
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

            // Remove the temporary ASCII copy (if we made one).
            if (stagedCopy is not null)
            {
                try { File.Delete(stagedCopy); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>True when every character in the path is plain 7-bit ASCII.</summary>
    private static bool IsAsciiPath(string path)
    {
        foreach (var c in path)
            if (c > 127) return false;
        return true;
    }

    /// <summary>
    /// Copies an IPA whose path contains non-ASCII characters into a folder whose
    /// full path is guaranteed to be ASCII (<c>%PUBLIC%\Documents\IPAStudio\stage</c>,
    /// the "Public" account name is never localised on disk) using an ASCII file name.
    /// Returns the new path. The caller must delete it after installing.
    /// </summary>
    private static string CreateAsciiStagingCopy(string sourceIpa)
    {
        var stageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
            "IPAStudio", "stage");
        Directory.CreateDirectory(stageRoot);

        var dest = Path.Combine(stageRoot, $"{Guid.NewGuid():N}.ipa");
        File.Copy(sourceIpa, dest, overwrite: true);
        return dest;
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
