using System.Diagnostics;
using IPAStudio.Core.Localization;

namespace IPAStudio.Core.Tools;

/// <summary>
/// A single actionable finding about local download throughput.
/// </summary>
/// <param name="Kind">Stable identifier so the UI can pick an icon / action.</param>
/// <param name="Title">Short user-facing summary.</param>
/// <param name="Detail">Explanation of the impact.</param>
/// <param name="CanAutoFix">True when <see cref="TransferTuning.TryAutoFixAsync"/> can address it.</param>
public sealed record ThroughputFinding(
    string Kind,
    string Title,
    string Detail,
    bool CanAutoFix);

/// <summary>
/// Local (non-network) throughput diagnostics and fixes.
///
/// Context: the IPA bytes are transferred by the bundled ipatool process, which this
/// app cannot modify. That means the wire speed of a single stream is not ours to
/// tune. What *is* ours are the local costs layered on top of that stream, and on
/// Windows those costs are large and routinely dominate a multi-gigabyte download:
///
///   1. Defender real-time scanning. Every buffer written into a growing .ipa is
///      inspected, and the finished archive is scanned again in full. On multi-GB
///      files this is commonly the single largest slowdown.
///   2. Cross-volume staging. If the temp/staging directory and the final Apps
///      folder are on different volumes, the "move" at the end degrades into a
///      full byte-for-byte copy of the entire archive.
///   3. Compressed or deduplicated NTFS targets, which turn sequential writes into
///      read-modify-write cycles.
///
/// Everything here is advisory and best-effort: a failure to inspect or fix must
/// never block a download.
/// </summary>
public static class TransferTuning
{
    /// <summary>Kind identifier for the Defender exclusion finding.</summary>
    public const string KindDefender = "defender-exclusion";

    /// <summary>Kind identifier for the cross-volume staging finding.</summary>
    public const string KindCrossVolume = "cross-volume-staging";

    /// <summary>Kind identifier for the NTFS compression finding.</summary>
    public const string KindCompressed = "compressed-target";

    /// <summary>Kind identifier for the low-disk-space finding.</summary>
    public const string KindLowSpace = "low-disk-space";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan FixTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Inspects the download and staging folders and returns whatever is measurably
    /// hurting throughput. An empty list means nothing local to improve.
    /// </summary>
    public static async Task<IReadOnlyList<ThroughputFinding>> AnalyzeAsync(
        string appsFolder, string stagingFolder, CancellationToken ct = default)
    {
        var findings = new List<ThroughputFinding>();

        if (!OperatingSystem.IsWindows())
            return findings;

        // --- Defender real-time scanning -------------------------------------
        try
        {
            var realtimeOn = await IsDefenderRealtimeEnabledAsync(ct).ConfigureAwait(false);
            if (realtimeOn == true)
            {
                var excluded = await AreFoldersExcludedAsync(
                    new[] { appsFolder, stagingFolder }, ct).ConfigureAwait(false);

                if (excluded == false)
                {
                    findings.Add(new ThroughputFinding(
                        KindDefender,
                        Loc.Get("L.Tuning.Defender.Title"),
                        Loc.Get("L.Tuning.Defender.Detail"),
                        CanAutoFix: true));
                }
            }
        }
        catch
        {
            // Defender may be absent, replaced by a third-party AV, or managed by
            // policy. Not knowing is fine; we simply offer no advice.
        }

        // --- Cross-volume staging -------------------------------------------
        try
        {
            var appsRoot = SafeVolumeRoot(appsFolder);
            var stagingRoot = SafeVolumeRoot(stagingFolder);

            if (appsRoot is not null && stagingRoot is not null &&
                !string.Equals(appsRoot, stagingRoot, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new ThroughputFinding(
                    KindCrossVolume,
                    Loc.Get("L.Tuning.CrossVolume.Title"),
                    Loc.Format("L.Tuning.CrossVolume.Detail", stagingRoot, appsRoot),
                    CanAutoFix: false));
            }
        }
        catch { /* advisory only */ }

        // --- NTFS compression on the target ---------------------------------
        try
        {
            if (IsCompressed(appsFolder))
            {
                findings.Add(new ThroughputFinding(
                    KindCompressed,
                    Loc.Get("L.Tuning.Compressed.Title"),
                    Loc.Get("L.Tuning.Compressed.Detail"),
                    CanAutoFix: false));
            }
        }
        catch { /* advisory only */ }

        // --- Free space ------------------------------------------------------
        try
        {
            var root = SafeVolumeRoot(appsFolder);
            if (root is not null)
            {
                var free = new DriveInfo(root).AvailableFreeSpace;
                const long threshold = 8L * 1024 * 1024 * 1024;
                if (free < threshold)
                {
                    findings.Add(new ThroughputFinding(
                        KindLowSpace,
                        Loc.Get("L.Tuning.LowSpace.Title"),
                        Loc.Format("L.Tuning.LowSpace.Detail",
                            (free / (1024.0 * 1024 * 1024)).ToString("F1")),
                        CanAutoFix: false));
                }
            }
        }
        catch { /* advisory only */ }

        return findings;
    }

    /// <summary>
    /// Applies the fix for a finding. Currently only the Defender exclusion is
    /// automatable, and it triggers a UAC prompt.
    /// </summary>
    /// <returns>True when the fix was applied and verified.</returns>
    public static async Task<bool> TryAutoFixAsync(
        string kind, string appsFolder, string stagingFolder, CancellationToken ct = default)
    {
        if (kind != KindDefender || !OperatingSystem.IsWindows())
            return false;

        return await TryAddDefenderExclusionsAsync(
            new[] { appsFolder, stagingFolder }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Environment overrides handed to the ipatool child process so its own
    /// temporary files land next to the final archive rather than on the system
    /// volume. Without this the tool writes into %TEMP%, which is frequently a
    /// different (and often slower) disk, forcing a full copy at the end.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildChildEnvironment(string stagingFolder)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(stagingFolder))
            return env;

        try
        {
            Directory.CreateDirectory(stagingFolder);

            // Honoured by Go's os.TempDir on Windows (TMP/TEMP) and on Unix (TMPDIR).
            env["TMP"] = stagingFolder;
            env["TEMP"] = stagingFolder;
            env["TMPDIR"] = stagingFolder;
        }
        catch
        {
            // If the folder cannot be created, leave the child on its defaults.
            env.Clear();
        }

        return env;
    }

    // ---------------------------------------------------------------------
    // Defender interop (PowerShell, because the Defender API has no stable
    // managed surface and the cmdlets are present on every supported Windows).
    // ---------------------------------------------------------------------

    /// <summary>
    /// True when Defender real-time protection is on, false when off,
    /// null when it could not be determined.
    /// </summary>
    private static async Task<bool?> IsDefenderRealtimeEnabledAsync(CancellationToken ct)
    {
        var output = await RunPowerShellAsync(
            "(Get-MpPreference).DisableRealtimeMonitoring", elevated: false, ProbeTimeout, ct)
            .ConfigureAwait(false);

        if (output is null) return null;

        var text = output.Trim();
        if (text.Equals("False", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Equals("True", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    /// <summary>
    /// True when every folder is already excluded, false when at least one is not,
    /// null when it could not be determined.
    /// </summary>
    private static async Task<bool?> AreFoldersExcludedAsync(
        IEnumerable<string> folders, CancellationToken ct)
    {
        var output = await RunPowerShellAsync(
            "(Get-MpPreference).ExclusionPath -join \"`n\"", elevated: false, ProbeTimeout, ct)
            .ConfigureAwait(false);

        if (output is null) return null;

        var existing = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeForCompare)
            .Where(p => p.Length > 0)
            .ToList();

        foreach (var folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)))
        {
            var target = NormalizeForCompare(folder);
            if (target.Length == 0) continue;

            // An exclusion on a parent directory covers its children.
            var covered = existing.Any(e =>
                target.Equals(e, StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith(e + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

            if (!covered) return false;
        }

        return true;
    }

    /// <summary>
    /// Adds the folders to Defender's exclusion list via an elevated PowerShell,
    /// then verifies the change actually took effect.
    /// </summary>
    private static async Task<bool> TryAddDefenderExclusionsAsync(
        IEnumerable<string> folders, CancellationToken ct)
    {
        var paths = folders
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count == 0) return false;

        // Single-quoted PowerShell literals; ' is escaped by doubling.
        var literals = string.Join(
            ",", paths.Select(p => "'" + p.Replace("'", "''") + "'"));

        var script = $"Add-MpPreference -ExclusionPath {literals} -ErrorAction Stop";

        var ok = await RunPowerShellAsync(script, elevated: true, FixTimeout, ct)
            .ConfigureAwait(false) is not null;

        if (!ok) return false;

        // Trust the observed state, not the exit code: policy can silently discard
        // the request even when the cmdlet reports success.
        return await AreFoldersExcludedAsync(paths, ct).ConfigureAwait(false) == true;
    }

    /// <summary>
    /// Runs a PowerShell snippet.
    /// </summary>
    /// <param name="elevated">
    /// When true the process is launched through the shell verb "runas", which shows
    /// a UAC prompt. Elevated launches require UseShellExecute, so their stdout
    /// cannot be captured — success is reported as an empty string and must be
    /// verified separately by the caller.
    /// </param>
    /// <returns>Captured stdout, an empty string for a successful elevated run, or null on failure.</returns>
    private static async Task<string?> RunPowerShellAsync(
        string script, bool elevated, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        if (elevated)
        {
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            psi.WindowStyle = ProcessWindowStyle.Hidden;
        }
        else
        {
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return null;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            string captured = "";

            if (!elevated)
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

                captured = await stdoutTask.ConfigureAwait(false);
                _ = await stderrTask.ConfigureAwait(false);
            }
            else
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }

            return process.ExitCode == 0 ? captured : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            // Missing powershell.exe, or the user dismissed the UAC prompt
            // (which surfaces as a Win32Exception).
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // Filesystem helpers
    // ---------------------------------------------------------------------

    private static string NormalizeForCompare(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch
        {
            return "";
        }
    }

    private static string? SafeVolumeRoot(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : root;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsCompressed(string folder)
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            if (!Directory.Exists(folder)) return false;
            return (File.GetAttributes(folder) & FileAttributes.Compressed) != 0;
        }
        catch
        {
            return false;
        }
    }
}
