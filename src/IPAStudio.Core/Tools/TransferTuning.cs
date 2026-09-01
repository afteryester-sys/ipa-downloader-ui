using System.Diagnostics;
using IPAStudio.Core.Diagnostics;
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
/// Result of applying a finding's fix. Distinguishing these matters because the
/// remedy differs: a dismissed UAC prompt is retryable, a policy-managed Defender
/// is not, and both used to be reported with the same misleading message.
/// </summary>
public enum ThroughputFixOutcome
{
    /// <summary>The change was made and confirmed by an elevated read-back.</summary>
    Applied,

    /// <summary>The user dismissed the elevation prompt. Retryable.</summary>
    Cancelled,

    /// <summary>Defender refused or silently discarded the change (group policy, tamper protection).</summary>
    Blocked,

    /// <summary>The attempt could not be carried out or verified at all.</summary>
    Failed,
}

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
    /// <param name="verifiedExclusions">
    /// Folders a previous elevated run confirmed as excluded. Used only when Defender
    /// hides its exclusion list from this unelevated process, so that a fix which
    /// already succeeded is not reported as an outstanding problem on every rescan.
    /// </param>
    public static async Task<IReadOnlyList<ThroughputFinding>> AnalyzeAsync(
        string appsFolder, string stagingFolder,
        IReadOnlyCollection<string>? verifiedExclusions = null,
        CancellationToken ct = default)
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
                    new[] { appsFolder, stagingFolder }, verifiedExclusions, ct).ConfigureAwait(false);

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
    /// <returns>What actually happened, so the UI can say something true.</returns>
    public static async Task<ThroughputFixOutcome> TryAutoFixAsync(
        string kind, string appsFolder, string stagingFolder, CancellationToken ct = default)
    {
        if (kind != KindDefender || !OperatingSystem.IsWindows())
        {
            AppLog.Warn($"Throughput fix: nothing automatable for '{kind}' " +
                        $"(Windows: {OperatingSystem.IsWindows()}).");
            return ThroughputFixOutcome.Failed;
        }

        return await AddDefenderExclusionsAsync(
            new[] { appsFolder, stagingFolder }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The folders <see cref="TryAutoFixAsync"/> would exclude, so a caller can persist
    /// them once the fix is confirmed.
    /// </summary>
    public static IReadOnlyList<string> DefenderExclusionTargets(
        string appsFolder, string stagingFolder)
    {
        return new[] { appsFolder, stagingFolder }
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(NormalizeForCompare)
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            "(Get-MpPreference).DisableRealtimeMonitoring", ProbeTimeout, ct)
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
        IEnumerable<string> folders, IReadOnlyCollection<string>? verifiedExclusions,
        CancellationToken ct)
    {
        var output = await RunPowerShellAsync(
            "(Get-MpPreference).ExclusionPath -join \"`n\"", ProbeTimeout, ct)
            .ConfigureAwait(false);

        if (output is null) return null;

        var existing = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeForCompare)
            .Where(p => p.Length > 0)
            .ToList();

        // Defender reveals its exclusion list only to an administrator, so an empty
        // list here usually means "not allowed to look" rather than "nothing excluded".
        // In that blind case fall back to what an earlier elevated run confirmed;
        // when the list *is* visible it is authoritative and the fallback is ignored.
        if (existing.Count == 0 && verifiedExclusions is { Count: > 0 })
        {
            existing = verifiedExclusions
                .Select(NormalizeForCompare)
                .Where(p => p.Length > 0)
                .ToList();
        }

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
    /// Adds the folders to Defender's exclusion list via an elevated PowerShell and
    /// verifies the result inside that same elevated process.
    ///
    /// The read-back has to happen there rather than here: Defender only discloses its
    /// exclusion list to an administrator, so verifying from this (unelevated) process
    /// sees an empty list and reports every successful fix as a failure — which is
    /// exactly what users saw. An elevated process cannot have its stdout redirected
    /// (elevation requires UseShellExecute), so it reports back through a file in our
    /// own temp directory, which it can write to and we can read.
    /// </summary>
    private static async Task<ThroughputFixOutcome> AddDefenderExclusionsAsync(
        IEnumerable<string> folders, CancellationToken ct)
    {
        var paths = folders
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Logged at every outcome below. Without this the button was undebuggable: the log
        // held not one line about Defender, so "it still does not work" could not be told
        // apart from a dismissed UAC prompt, a policy-managed Defender, or a fix that had
        // in fact been applied.
        if (paths.Count == 0)
        {
            AppLog.Warn("Defender fix: no folders to exclude (apps/staging folder unset).");
            return ThroughputFixOutcome.Failed;
        }

        AppLog.Info($"Defender fix: requesting exclusions for {string.Join(", ", paths)}");

        var literals = string.Join(",", paths.Select(PsLiteral));
        var report = Path.Combine(
            Path.GetTempPath(), $"ipastudio-defender-{Guid.NewGuid():N}.txt");

        var script =
            "$ErrorActionPreference='Stop'; " +
            $"$out={PsLiteral(report)}; " +
            "try { " +
            $"Add-MpPreference -ExclusionPath {literals}; " +
            "$list=@(@((Get-MpPreference).ExclusionPath) | Where-Object { $_ }); " +
            "Set-Content -LiteralPath $out -Encoding UTF8 -Value (@('OK') + $list); " +
            "} catch { " +
            "Set-Content -LiteralPath $out -Encoding UTF8 -Value @('ERROR'); " +
            "exit 1; }";

        var run = await RunElevatedPowerShellAsync(script, FixTimeout, ct).ConfigureAwait(false);

        var lines = Array.Empty<string>();
        try
        {
            if (File.Exists(report))
                lines = await File.ReadAllLinesAsync(report, ct).ConfigureAwait(false);
        }
        catch { /* unreadable report is handled as "no report" below */ }
        finally
        {
            try { File.Delete(report); } catch { /* best effort */ }
        }

        if (run == ElevatedRun.Cancelled)
        {
            AppLog.Info("Defender fix: the UAC prompt was dismissed, nothing was changed.");
            return ThroughputFixOutcome.Cancelled;
        }

        var head = lines.Length > 0 ? lines[0].Trim() : "";

        // Add-MpPreference itself threw: managed by policy, or tamper protection.
        if (head == "ERROR")
        {
            AppLog.Warn("Defender fix: Add-MpPreference failed. Defender is most likely " +
                        "managed by group policy or protected by tamper protection.");
            return ThroughputFixOutcome.Blocked;
        }

        if (head != "OK")
        {
            AppLog.Warn($"Defender fix: no usable report from the elevated process " +
                        $"(run={run}, report lines={lines.Length}).");
            return ThroughputFixOutcome.Failed;
        }

        var visible = lines.Skip(1)
            .Select(NormalizeForCompare)
            .Where(p => p.Length > 0)
            .ToList();

        // An entirely empty list after a cmdlet that reported success means the list is
        // withheld (HideExclusionsFromLocalAdmins) rather than that our paths were
        // dropped, so the cmdlet's own success is the best evidence available.
        if (visible.Count == 0)
        {
            AppLog.Info("Defender fix: applied. The exclusion list is hidden from local " +
                        "admins, so it could not be read back for confirmation.");
            return ThroughputFixOutcome.Applied;
        }

        var allCovered = paths.All(p =>
        {
            var target = NormalizeForCompare(p);
            return visible.Any(e =>
                target.Equals(e, StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith(e + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        });

        // The list is visible and our paths are not in it: silently discarded.
        if (allCovered)
        {
            AppLog.Info("Defender fix: applied and confirmed in Defender's exclusion list.");
            return ThroughputFixOutcome.Applied;
        }

        AppLog.Warn($"Defender fix: the cmdlet reported success but the paths are absent from " +
                    $"the exclusion list ({visible.Count} entries read back), so they were " +
                    "discarded — Defender is being managed centrally.");
        return ThroughputFixOutcome.Blocked;
    }

    /// <summary>Single-quoted PowerShell literal; an embedded ' is escaped by doubling.</summary>
    private static string PsLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>Outcome of an elevated launch.</summary>
    private enum ElevatedRun
    {
        /// <summary>The process ran to completion and exited zero.</summary>
        Ok,

        /// <summary>The user dismissed the UAC prompt.</summary>
        Cancelled,

        /// <summary>It could not be started, timed out, or exited non-zero.</summary>
        Failed,
    }

    private static void AddCommonArguments(ProcessStartInfo psi, string script)
    {
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
    }

    /// <summary>
    /// Runs an unelevated PowerShell snippet and captures its stdout.
    /// </summary>
    /// <returns>Captured stdout, or null when it failed or exited non-zero.</returns>
    private static async Task<string?> RunPowerShellAsync(
        string script, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        AddCommonArguments(psi, script);

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return null;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            var captured = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);

            return process.ExitCode == 0 ? captured : null;
        }
        catch
        {
            // Missing powershell.exe, a timeout, or cancellation.
            return null;
        }
    }

    /// <summary>
    /// Runs a PowerShell snippet through the shell verb "runas", which shows a UAC
    /// prompt. Elevation requires UseShellExecute, so stdout cannot be redirected —
    /// callers that need output must have the script write it to a file.
    /// </summary>
    private static async Task<ElevatedRun> RunElevatedPowerShellAsync(
        string script, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            CreateNoWindow = true,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        AddCommonArguments(psi, script);

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return ElevatedRun.Failed;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            return process.ExitCode == 0 ? ElevatedRun.Ok : ElevatedRun.Failed;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the elevation prompt was dismissed. Worth telling apart,
            // since the user only has to accept it next time.
            return ElevatedRun.Cancelled;
        }
        catch
        {
            return ElevatedRun.Failed;
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
