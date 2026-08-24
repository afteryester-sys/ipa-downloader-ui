using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services;

/// <summary>Which part of the download is currently happening.</summary>
public enum DownloadPhase
{
    /// <summary>
    /// ipatool has started but no bytes have moved yet: keychain unlock, Apple
    /// authentication handshake (and anisette provisioning on v3). On a slow link
    /// this can legitimately take 5-20 s, so the UI must show it as live activity
    /// rather than a frozen "preparing" label.
    /// </summary>
    Connecting,

    /// <summary>Bytes are actively moving.</summary>
    Transferring,

    /// <summary>
    /// Transfer finished; ipatool is repackaging the archive (sinf / iTunesMetadata
    /// injection). No byte movement, but the tool is busy.
    /// </summary>
    Finalizing,
}

/// <summary>Progress snapshot reported while downloading an IPA.</summary>
public readonly record struct DownloadProgress(
    double Percent,
    long DownloadedBytes,
    long TotalBytes,
    double SpeedBps,
    DownloadPhase Phase = DownloadPhase.Transferring,
    TimeSpan Elapsed = default,
    int Attempt = 1)
{
    /// <summary>True while ipatool is repackaging after the transfer.</summary>
    public bool Finalizing => Phase == DownloadPhase.Finalizing;

    /// <summary>True while authenticating / before the first byte arrives.</summary>
    public bool Connecting => Phase == DownloadPhase.Connecting;
}

/// <summary>Result of a completed download.</summary>
public sealed class DownloadResult
{
    public bool Success { get; init; }

    public string? IpaPath { get; init; }

    /// <summary>Localized, user-facing failure message. Safe to show verbatim.</summary>
    public string? Error { get; init; }

    /// <summary>Raw tool output behind <see cref="Error"/>. For the log, never for the UI.</summary>
    public string? Detail { get; init; }

    /// <summary>True when the ipatool session is no longer valid and the user must sign in again.</summary>
    public bool SessionExpired { get; init; }

    /// <summary>True when the failure was "the Apple ID does not own this app".</summary>
    public bool LicenseRequired { get; init; }

    public static DownloadResult Ok(string path) => new() { Success = true, IpaPath = path };

    public static DownloadResult Fail(string error, string? detail = null) =>
        new() { Error = error, Detail = detail };

    public static DownloadResult Expired(string detail) =>
        new() { Error = Loc.Get("L.Error.SessionExpired"), Detail = detail, SessionExpired = true };

    public static DownloadResult NeedsLicense(string detail) =>
        new() { Error = Loc.Get("L.Error.NotPurchased"), Detail = detail, LicenseRequired = true };
}

/// <summary>What to do when the target .ipa already exists on disk.</summary>
public enum FileConflictDecision
{
    /// <summary>Overwrite the existing file (only after the new one downloads).</summary>
    Replace,

    /// <summary>Keep the old file and save the new one under a numbered name.</summary>
    KeepBoth,

    /// <summary>Skip this download entirely and leave the old file untouched.</summary>
    Cancel,
}

/// <summary>Details shown to the user when a download would overwrite an existing file.</summary>
public sealed record FileConflictRequest(
    string AppName,
    string ExistingPath,
    long ExistingSizeBytes,
    DateTime ExistingModifiedLocal);

/// <summary>
/// The user's answer. <paramref name="ApplyToAll"/> reuses this decision for every
/// later conflict in the same queue run, so a large batch is not interrupted once per
/// item.
/// </summary>
public sealed record FileConflictResponse(FileConflictDecision Decision, bool ApplyToAll);

/// <summary>
/// App Store operations via ipatool:
///   search, purchase (obtain license), download (with progress), list-versions.
/// </summary>
public sealed partial class DownloadService
{
    private readonly ToolLocator _tools;
    private readonly ProcessRunner _runner;
    private readonly HttpClient _http;
    private readonly AuthService _auth;

    // ---- Progress-bar parsing (ipatool v2 and v3 both render a CR progress bar) ----

    [GeneratedRegex(@"(\d{1,3}(?:[.,]\d+)?)\s*%")]
    private static partial Regex PercentRegex();

    /// <summary>Matches "12.3/45.6 MB" and "12.3 MB/45.6 MB" inside the progress bar.</summary>
    [GeneratedRegex(@"([\d]+(?:[.,]\d+)?)\s*([KkMmGg]i?B|B)?\s*/\s*([\d]+(?:[.,]\d+)?)\s*([KkMmGg]i?B|B)")]
    private static partial Regex BytesPairRegex();

    /// <summary>
    /// Matches a lone size such as "149.5 MB" — the form the bar falls back to when the
    /// total is unknown and it renders as a spinner instead of a percentage. The negative
    /// lookahead for "/" keeps it off both the left half of a "149.5/172 MB" pair and the
    /// "18.3 MB/s" speed, either of which would otherwise be read as bytes transferred.
    /// </summary>
    [GeneratedRegex(@"([\d]+(?:[.,]\d+)?)\s*([KkMmGg]i?B)\b(?!\s*/)")]
    private static partial Regex SingleBytesRegex();

    /// <summary>Matches "1.23 MB/s".</summary>
    [GeneratedRegex(@"([\d]+(?:[.,]\d+)?)\s*([KkMmGg]i?B|B)\s*/\s*s\b")]
    private static partial Regex SpeedRegex();

    [GeneratedRegex(@"license.*required|not.*purchased|purchase.*required|9610", RegexOptions.IgnoreCase)]
    private static partial Regex LicenseRequiredRegex();

    /// <summary>
    /// Network-level failures that are worth retrying. Deliberately excludes auth and
    /// license errors — retrying those just burns another Apple handshake.
    /// </summary>
    [GeneratedRegex(@"timeout|timed out|deadline exceeded|connection reset|connection refused|reset by peer|broken pipe|unexpected EOF|\bEOF\b|i/o timeout|no such host|temporary failure|network is unreachable|TLS handshake|\b50[234]\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex TransientRegex();

    /// <summary>Windows absolute path ending in .ipa, used to recover the real output path.</summary>
    [GeneratedRegex(@"([A-Za-z]:\\[^\r\n""']+\.ipa)")]
    private static partial Regex IpaPathRegex();

    /// <summary>Log marker written when the ipatool session has expired. Consumers key on
    /// <see cref="DownloadResult.SessionExpired"/> (not on message text) to send the user
    /// back to the login screen, so the wording can be localized freely.</summary>
    public const string SessionExpiredDetail =
        "SESSION_EXPIRED: account file is not protected. Please sign in again.";

    /// <summary>
    /// Total attempts for a single download. Retries only happen for transient network
    /// failures and stalls, never for auth/license errors.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// Kill-and-retry threshold once bytes have started moving. On a bad connection
    /// ipatool can sit on a dead socket indefinitely; restarting is far faster than
    /// waiting for the OS TCP timeout.
    /// </summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(75);

    /// <summary>Kill-and-retry threshold while still authenticating (no bytes yet).</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>How often the UI receives a progress snapshot.</summary>
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Asks the user what to do when the target file already exists. Set once by the UI
    /// layer at startup (Core must not reference WPF), mirroring how the 2FA prompt is
    /// supplied to <see cref="AuthService"/>.
    ///
    /// When left null nothing prompts, and downloads fall back to
    /// <see cref="FileConflictDecision.KeepBoth"/> — the choice that cannot destroy a
    /// file the user already has.
    /// </summary>
    public Func<FileConflictRequest, CancellationToken, Task<FileConflictResponse>>? FileConflictResolver { get; set; }

    /// <summary>"Apply to all" answer, valid until <see cref="ResetFileConflictScope"/>.</summary>
    private FileConflictDecision? _stickyConflictDecision;

    /// <summary>
    /// Forgets a previous "apply to all" answer. Called when a queue run starts so the
    /// choice never silently leaks into an unrelated batch later in the session.
    /// </summary>
    public void ResetFileConflictScope() => _stickyConflictDecision = null;

    /// <summary>
    /// How an interrupted transfer treats work already on disk. Supplied as a callback
    /// rather than by taking a <c>SettingsService</c> dependency, matching
    /// <see cref="FileConflictResolver"/> above: the setting is read at the moment it
    /// matters, so toggling it takes effect on the next attempt without a restart.
    ///
    /// Defaults to <see cref="ResumeMode.RestartFromScratch"/> — the historical
    /// behaviour — so any caller that does not wire this up is unaffected.
    /// </summary>
    public Func<ResumeMode>? ResumeModeProvider { get; set; }

    private ResumeMode CurrentResumeMode
    {
        get
        {
            try { return ResumeModeProvider?.Invoke() ?? ResumeMode.RestartFromScratch; }
            catch { return ResumeMode.RestartFromScratch; }
        }
    }

    public DownloadService(ToolLocator tools, ProcessRunner runner, HttpClient http, AuthService auth)
    {
        _tools = tools;
        _runner = runner;
        _http = http;
        _auth = auth;
    }

    /// <summary>True when the output indicates the Apple ID has no license for the app.</summary>
    public static bool IsLicenseError(string? output) =>
        !string.IsNullOrEmpty(output) && LicenseRequiredRegex().IsMatch(output);

    /// <summary>
    /// Turns raw ipatool output into a localized sentence. ipatool prints Go-level
    /// diagnostics ("dial tcp: lookup p25-buy.itunes.apple.com: no such host"), which
    /// told the user nothing and could never be translated; the raw line still goes to
    /// the log via <see cref="DownloadResult.Detail"/>.
    /// </summary>
    public static string DescribeStoreFailure(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return Loc.Get("L.Error.DownloadFailed");

        var lower = output.ToLowerInvariant();

        if (AuthService.IsSessionExpiredError(output)) return Loc.Get("L.Error.SessionExpired");
        if (LicenseRequiredRegex().IsMatch(output))    return Loc.Get("L.Error.NotPurchased");

        if (lower.Contains("no such host") || lower.Contains("dial tcp")
            || lower.Contains("network is unreachable") || lower.Contains("tls handshake")
            || lower.Contains("connection reset") || lower.Contains("connection refused")
            || lower.Contains("timed out") || lower.Contains("i/o timeout") || lower.Contains("timeout"))
            return Loc.Get("L.Error.Network");

        if (lower.Contains("500") || lower.Contains("502") || lower.Contains("503")
            || lower.Contains("504") || lower.Contains("temporarily unavailable")
            || lower.Contains("try again later"))
            return Loc.Get("L.Error.StoreUnavailable");

        if (IsNotInStoreOutput(output)) return Loc.Get("L.Error.NotInStore");

        return Loc.Get("L.Error.DownloadFailed");
    }

    /// <summary>
    /// Whether ipatool's output says the store does not have this app.
    ///
    /// Kept as its own test so the retry path can ask the same question of the raw output.
    /// Matching the localized sentence instead would silently stop working the moment a
    /// translation was reworded.
    /// </summary>
    private static bool IsNotInStoreOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;

        var lower = output.ToLowerInvariant();
        return lower.Contains("not available") || lower.Contains("no such app")
            || lower.Contains("could not find") || lower.Contains("not found")
            || lower.Contains("item not available") || lower.Contains("invalid item");
    }

    /// <summary>
    /// Asks ipatool to obtain a license for the app.
    ///
    /// WARNING: this is NOT a read-only probe. ipatool has no "is it owned" query, so
    /// this runs <c>purchase</c>, which performs a full Apple authentication handshake
    /// and acquires the license as a side effect.
    ///
    /// Do NOT call this on the download hot path: <c>download --purchase</c> already
    /// does the same thing, so calling both pays the (multi-second) handshake twice.
    /// Use this only for explicit, user-initiated license checks in the app picker.
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
                workingDirectory: _tools.IpatoolWorkingDirectory,
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

    /// <summary>
    /// Obtains a license for a free app (ipatool purchase). <c>Error</c> is already
    /// localized; <c>SessionExpired</c> tells the caller to route the user to sign-in.
    /// </summary>
    public async Task<(bool Success, string? Error, bool SessionExpired)> PurchaseAsync(AppEntry app, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(app.BundleId))
            return (false, Loc.Get("L.Error.LicenseFailed"), false);

        var result = await _runner.RunAsync(
            _tools.IpatoolPath,
            new[] { "purchase", "-b", app.BundleId, "--keychain-passphrase", ToolLocator.KeychainPassphrase,
                    "--format", "json" },
            closeStdin: true,
            workingDirectory: _tools.IpatoolWorkingDirectory,
            ct: ct).ConfigureAwait(false);

        if (result.Success || result.CombinedOutput.Contains("already", StringComparison.OrdinalIgnoreCase))
            return (true, null, false);

        if (AuthService.IsSessionExpiredError(result.CombinedOutput))
        {
            AppLog.Warn($"Purchase {app.AppStoreId}: {SessionExpiredDetail}");
            return (false, Loc.Get("L.Error.SessionExpired"), true);
        }

        var raw = ExtractError(result.CombinedOutput);
        AppLog.Warn($"Purchase {app.AppStoreId} failed: {raw}");
        return (false, DescribeStoreFailure(result.CombinedOutput), false);
    }

    /// <summary>
    /// Downloads the IPA, reporting live progress.
    /// Output file: Name_AppID_Version.ipa
    ///
    /// Transient network failures and dead sockets are retried up to
    /// <see cref="MaxAttempts"/> times; auth/license errors fail immediately.
    /// </summary>
    /// <param name="destinationFolder">
    /// Folder to save into. When null the managed Apps folder is used (queue downloads).
    /// The direct download screen passes a user-chosen folder here.
    /// </param>
    public async Task<DownloadResult> DownloadAsync(
        AppEntry app,
        bool autoPurchase = true,
        IProgress<DownloadProgress>? progress = null,
        string? destinationFolder = null,
        CancellationToken ct = default)
    {
        _tools.EnsureFolders();

        var targetFolder = string.IsNullOrWhiteSpace(destinationFolder)
            ? _tools.AppsFolder
            : destinationFolder!;

        // Creating the destination up front turns "folder was deleted / not writable"
        // into an error before the Apple handshake, instead of after a full transfer.
        try
        {
            Directory.CreateDirectory(targetFolder);
        }
        catch (Exception ex)
        {
            return DownloadResult.Fail(
                Loc.Format("L.Error.FolderUnusable", targetFolder, ex.Message), ex.ToString());
        }

        var outputPath = BuildOutputPath(app, targetFolder);

        // ---- Existing file? Ask before touching it. --------------------------------
        // Historically the download simply deleted whatever sat at this path (see
        // TryDeleteStaleFiles below), so re-downloading an app destroyed the copy the
        // user already had — and if the new attempt then failed, both were gone.
        //
        // replaceTarget != null means "the user agreed to overwrite this path". Even
        // then the transfer runs to a fresh, unique file first and only swaps at the
        // very end, so a failed download can never take the old file with it.
        // In "keep partial files" mode, a complete and correctly licensed archive that is
        // already sitting at the destination is the finished job — hand it straight back.
        // This is what makes an interrupted batch cheap to resume: apps that made it to
        // disk before the program was closed are not downloaded a second time, and no
        // conflict prompt appears for a file the user is not really replacing.
        if (CurrentResumeMode == ResumeMode.KeepPartialFiles
            && TryReuseExistingArchive(app, outputPath) is { } reused)
        {
            progress?.Report(new DownloadProgress(
                100, reused.Length, reused.Length, 0, DownloadPhase.Transferring, TimeSpan.Zero, 1));
            return DownloadResult.Ok(reused.FullName);
        }

        string? replaceTarget = null;
        if (File.Exists(outputPath)
            && IpaLicense.BelongsToAnotherAccount(outputPath, _auth.CurrentAccount?.Email, out var otherAccount))
        {
            // The file in the way belongs to a different Apple ID, which is precisely why a
            // download was started for it in the first place. Asking "this file already
            // exists, replace it?" would be a question about a file the user is not
            // replacing, once per app, in the middle of a queue run. Keep both silently: the
            // other account's copy is still theirs to install, so it is not ours to delete.
            AppLog.Info($"'{Path.GetFileName(outputPath)}' is licensed to {otherAccount}; " +
                        "keeping it and downloading a separate copy for this account.");
            outputPath = MakeUniquePath(outputPath);
        }
        else if (File.Exists(outputPath))
        {
            var decision = await ResolveConflictAsync(app, outputPath, ct).ConfigureAwait(false);
            switch (decision)
            {
                case FileConflictDecision.Cancel:
                    return DownloadResult.Fail(
                        Loc.Format("L.Error.FileExists", Path.GetFileName(outputPath)));

                case FileConflictDecision.Replace:
                    replaceTarget = outputPath;
                    outputPath = MakeUniquePath(outputPath);
                    break;

                default: // KeepBoth
                    outputPath = MakeUniquePath(outputPath);
                    break;
            }
        }

        // Stage temp files on the SAME volume as the destination. Two wins:
        //   1. The poller knows where to look, so progress works regardless of where
        //      ipatool chooses to buffer.
        //   2. ipatool's final move becomes a rename instead of a full-size cross-volume
        //      copy (which on a 2 GB IPA with %TEMP% on C: and Apps on D: adds a long,
        //      completely silent tail after the download "finishes").
        //
        // This derives from the *destination*, not from AppsFolder: with a user-chosen
        // folder on another drive, staging under AppsFolder would put the temp file on
        // the wrong volume and bring that silent cross-volume copy straight back.
        var stagingDir = Path.Combine(targetFolder, ".staging");
        try { Directory.CreateDirectory(stagingDir); } catch { /* fall back to system temp */ }

        // Kick off the catalog size lookup once, shared across attempts. The progress
        // bar itself reports the authoritative total, so this is only a seed used
        // during the first seconds (and never blocks the start).
        var sizeHint = new long[] { app.FileSizeBytes ?? 0L };
        // No longer gated on a store id: an app known only by bundle identifier has both a
        // learned size and an Apple lookup available to it now, and those are the only two
        // sources a repacked entry ever has.
        if (sizeHint[0] <= 0)
        {
            // A copy already on disk is the last resort, and the only one that works for
            // apps Apple has pulled from every storefront (mail.ru and the like): the
            // catalog entry is gone, so the lookup answers nothing, and Apple's download
            // carries no Content-Length either. Seed only - the progress bar supersedes it
            // as soon as it prints a real total, and an overshoot throws it away instead of
            // pinning the bar just short of the end.
            var onDisk = TrySizeOfExistingCopy(app);
            if (onDisk > 0)
            {
                Volatile.Write(ref sizeHint[0], onDisk);
                AppLog.Info($"size: seeded from an existing copy ({onDisk / 1048576.0:F1}MB)");
            }

            _ = Task.Run(async () =>
            {
                var looked = await TryLookupFileSizeAsync(app, ct).ConfigureAwait(false);
                if (looked > 0)
                {
                    Volatile.Write(ref sizeHint[0], looked);
                    app.FileSizeBytes = looked;
                }
            }, ct);
        }

        string? lastError = null;
        var attempts = Math.Max(1, MaxAttempts);

        // Guards the bundle-id fallback below so it is attempted once, not on every retry.
        var triedBundleId = false;

        // Guards the silent re-login below, likewise once per download.
        var sessionRenewed = false;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var (result, transient) = await DownloadOnceAsync(
                app, outputPath, stagingDir, autoPurchase, sizeHint, progress, attempt, ct).ConfigureAwait(false);

            // The swap happens only now, with a complete file in hand.
            if (result.Success) return FinishReplace(result, replaceTarget);

            lastError = result.Error;

            // The store refused the numeric id, but the app has a bundle identifier we have
            // not tried yet. Worth one last go, though only as a long shot: resolving a
            // bundle identifier searches the storefront catalog, so it cannot rescue an app
            // pulled from sale - it only helps when the id the device reported is stale and
            // the app is still listed. Hence the original error is the one reported.
            if (!triedBundleId && ShouldRetryByBundleId(result, app))
            {
                triedBundleId = true;
                AppLog.Info($"download: id {app.AppStoreId} was refused; retrying as {app.BundleId}");

                var byBundle = new AppEntry
                {
                    Name = app.Name,
                    // Zero is what steers the argument builder onto "-b".
                    AppStoreId = 0,
                    BundleId = app.BundleId,
                    LatestVersion = app.LatestVersion,
                    FileSizeBytes = app.FileSizeBytes,
                };

                var (retry, _) = await DownloadOnceAsync(
                    byBundle, outputPath, stagingDir, autoPurchase, sizeHint, progress, attempt, ct)
                    .ConfigureAwait(false);

                if (retry.Success) return FinishReplace(retry, replaceTarget);

                // Keep the numeric-id error: that request named the exact app, so its
                // verdict says why the download failed. The bundle-id attempt can only ever
                // add "not found", which would bury the real reason.
                return result;
            }

            // Apple expired the token mid-session. When the credentials from this session's
            // sign-in are still in memory, renew it and carry on rather than failing the app
            // and sending the user to the login screen — that interruption is what makes a
            // long queue need babysitting. Guarded by CanReauthenticate, and attempted once
            // (sessionRenewed) so a genuinely dead password cannot spin here.
            if (result.SessionExpired && !sessionRenewed && _auth.CanReauthenticate)
            {
                sessionRenewed = true;

                progress?.Report(new DownloadProgress(
                    0, 0, Volatile.Read(ref sizeHint[0]), 0, DownloadPhase.Connecting, TimeSpan.Zero, attempt));

                if (await _auth.TryReauthenticateAsync(ct).ConfigureAwait(false))
                {
                    // Retry the same attempt number: the renewal is not the app's fault and
                    // should not consume one of its tries.
                    attempt--;
                    continue;
                }
            }

            // Auth, license, disk and "app not available" errors will not fix themselves.
            if (!transient || attempt == attempts) return result;

            // Back off briefly, then start over. Show the user that we are retrying
            // instead of leaving the bar frozen at wherever it died.
            progress?.Report(new DownloadProgress(
                0, 0, Volatile.Read(ref sizeHint[0]), 0, DownloadPhase.Connecting, TimeSpan.Zero, attempt + 1));

            await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
        }

        return DownloadResult.Fail(lastError ?? Loc.Get("L.Error.DownloadFailed"));
    }

    /// <summary>
    /// Whether a failed download is worth retrying by bundle identifier.
    ///
    /// Limited to the "not in the store" verdict: any other error (wrong password, no
    /// licence, full disk, network) would fail the same way a second time, and re-running a
    /// multi-second Apple handshake for nothing is a delay the user feels.
    ///
    /// Note this is the weaker of the two ways to name an app, not a stronger fallback: it
    /// is worth trying only because a device can report an id that the store has since
    /// retired.
    /// </summary>
    private static bool ShouldRetryByBundleId(DownloadResult result, AppEntry app)
        => app.AppStoreId > 0
           && !string.IsNullOrWhiteSpace(app.BundleId)
           && !result.SessionExpired
           && !result.LicenseRequired
           && IsNotInStoreOutput(result.Detail);

    /// <summary>One ipatool download invocation. Returns the result plus whether the
    /// failure looks retryable.</summary>
    private async Task<(DownloadResult Result, bool Transient)> DownloadOnceAsync(
        AppEntry app,
        string outputPath,
        string stagingDir,
        bool autoPurchase,
        long[] sizeHint,
        IProgress<DownloadProgress>? progress,
        int attempt,
        CancellationToken ct)
    {
        // A leftover file from a previous attempt would be read by the poller as
        // instant 100% at an absurd speed, so clear it (and any partials) first.
        TryDeleteStaleFiles(outputPath);
        TryCleanStaging(stagingDir);

        // The numeric store id is preferred: it names the exact app and still works for
        // apps Apple has pulled from every storefront. Only when the device gave no id at
        // all is the bundle identifier used instead - ipatool then resolves it through the
        // store, which is weaker (a delisted app is not found), but refusing outright would
        // block apps that are perfectly downloadable.
        var byBundleId = app.AppStoreId <= 0 && !string.IsNullOrWhiteSpace(app.BundleId);

        var args = new List<string>
        {
            "download",
        };
        if (byBundleId)
            // "-b" and nothing else: the bundled ipatool rejects --bundle-identifier and
            // answers with its usage text, which the caller then reports as a failed
            // download. Its own usage line spells the flag as "-b BUNDLE_ID".
            args.AddRange(new[] { "-b", app.BundleId! });
        else
            args.AddRange(new[] { "-i", app.AppStoreId.ToString() });

        args.AddRange(new[]
        {
            "-o", outputPath,
            "--keychain-passphrase", ToolLocator.KeychainPassphrase,
        });
        if (autoPurchase) args.Add("--purchase");

        // NOTE: "--format json" is deliberately NOT passed here.
        // In JSON mode ipatool suppresses the progress bar entirely and prints a single
        // summary line at the end, so there is nothing to parse while the download runs.
        // Text mode gives us percent, transferred/total bytes and speed in real time.

        // Redirect the child's temp directory onto the destination volume so its
        // final move is a rename, not a full-size cross-volume copy.
        var env = TransferTuning.BuildChildEnvironment(stagingDir);

        var state = new TransferState();
        var startedUtc = DateTimeOffset.UtcNow;
        state.Touch();

        // Diagnostics: the plain-text progress format is an ipatool implementation
        // detail that varies by version, so record a bounded sample of raw segments.
        // Without this, a format the regexes do not cover is invisible — the bar just
        // silently never moves. Capped so a chatty tool cannot flood the log.
        var loggedSegments = 0;
        var loggedNoTotal = 0;

        // ---- Parse each progress-bar frame / log line -------------------------------
        void OnSegment(string segment)
        {
            if (string.IsNullOrWhiteSpace(segment)) return;

            // Any output at all means the process is alive.
            state.TouchOutput();

            // "We understood this frame" and "the transfer actually advanced" are two
            // different things, and conflating them is what disabled the stall watchdog.
            // sawNumbers drives the diagnostics below; movedForward drives the watchdog.
            var sawNumbers = false;
            var movedForward = false;

            var pair = BytesPairRegex().Match(segment);
            if (pair.Success &&
                TryParseNumber(pair.Groups[1].Value, out var doneVal) &&
                TryParseNumber(pair.Groups[3].Value, out var totalVal))
            {
                var totalUnit = pair.Groups[4].Value;
                var doneUnit = pair.Groups[2].Success && pair.Groups[2].Value.Length > 0
                    ? pair.Groups[2].Value
                    : totalUnit;

                var done = ToBytes(doneVal, doneUnit);
                var total = ToBytes(totalVal, totalUnit);

                // The progress bar is the authoritative source for the total size —
                // the iTunes lookup value is for a generic device and can be off by
                // tens of percent after app thinning, which is what made the bar
                // stall at ~80% or slam into the 99% clamp.
                if (total > 0) Volatile.Write(ref sizeHint[0], total);
                if (done >= 0 && state.SetDownloaded(done)) movedForward = true;
                sawNumbers = true;
            }
            else
            {
                // No pair, so the bar is running without a known total. It still prints
                // the bytes transferred, and taking them is better than relying only on
                // the on-disk probe: ipatool downloads into its own .tmp and repackages
                // afterwards, so the file we poll can lag well behind the real transfer.
                var single = SingleBytesRegex().Match(segment);
                if (single.Success && TryParseNumber(single.Groups[1].Value, out var oneVal))
                {
                    var done = ToBytes(oneVal, single.Groups[2].Value);
                    if (done > 0)
                    {
                        if (state.SetDownloaded(done)) movedForward = true;
                        sawNumbers = true;
                    }
                }
            }

            var speed = SpeedRegex().Match(segment);
            if (speed.Success && TryParseNumber(speed.Groups[1].Value, out var speedVal))
            {
                // Note: a reported speed is NOT proof of life. A stalled ipatool keeps
                // printing its last frame, and "0 B/s" is the clearest evidence the
                // transfer is dead — it must never refresh the watchdog.
                state.ReportedSpeed = ToBytes(speedVal, speed.Groups[2].Value);
                sawNumbers = true;
            }

            var pct = PercentRegex().Match(segment);
            if (pct.Success && TryParseNumber(pct.Groups[1].Value, out var pctVal))
            {
                if (state.AdvancePercent(Math.Clamp(pctVal, 0, 100))) movedForward = true;
                sawNumbers = true;
            }

            // Only real forward movement resets the stall timer.
            if (movedForward) state.Touch();

            if (sawNumbers)
            {

                // A segment we partly understood but which yielded no total is exactly
                // the case that leaves the user with "total unknown" — and it used to
                // be invisible here, because matching the speed alone marked the segment
                // as recognised and skipped the logging below. Sample a few of these so
                // the real bar format can be read off the log instead of guessed at.
                if (Volatile.Read(ref sizeHint[0]) <= 0 &&
                    loggedNoTotal < 5 && segment.Trim().Length > 1)
                {
                    loggedNoTotal++;
                    AppLog.Info($"ipatool|no-total| {segment.Trim()}");
                }
            }
            else if (loggedSegments < 40 && segment.Trim().Length > 1)
            {
                // A segment carrying no numbers we recognise. Logging these is what
                // makes an unsupported progress format diagnosable from a user's log
                // instead of guesswork.
                loggedSegments++;
                AppLog.Info($"ipatool| {segment.Trim()}");
            }
        }

        // ---- Single reporter loop: file polling + parsed values ----------------------
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watchdogFired = false;

        var reporter = Task.Run(async () =>
        {
            double emaSpeed = 0;
            long derivedTotal = 0;
            long prevBytes = 0;
            var prevTime = DateTimeOffset.UtcNow;
            var sawBytes = false;

            try
            {
                while (!attemptCts.IsCancellationRequested)
                {
                    await Task.Delay(ReportInterval, attemptCts.Token).ConfigureAwait(false);

                    var now = DateTimeOffset.UtcNow;

                    // On-disk size is the fallback when the tool prints no numbers.
                    var onDisk = ProbeSize(outputPath, stagingDir, startedUtc.UtcDateTime);
                    var parsed = state.Downloaded;
                    var downloaded = Math.Max(onDisk, parsed);
                    var total = Volatile.Read(ref sizeHint[0]);

                    // A seeded total can be stale - the app was updated since the copy on
                    // disk, or since the size was last measured. Once the transfer passes
                    // it, keeping it would clamp the bar just short of the end for the rest
                    // of the download; an honest indeterminate bar is better than a fake
                    // one stuck at 99%.
                    if (total > 0 && downloaded > total * 1.02)
                    {
                        AppLog.Info($"size: dropping a stale total ({total / 1048576.0:F1}MB) " +
                                    $"passed by {downloaded / 1048576.0:F1}MB");
                        Volatile.Write(ref sizeHint[0], 0);
                        total = 0;
                    }

                    // Still no total: derive one from the percentage the tool prints and the
                    // bytes we can see. This is the case for apps Apple has delisted - the
                    // catalog has no entry to look up and the transfer carries no
                    // Content-Length - so without this the bar stays indeterminate for the
                    // whole download and the size is never shown at all.
                    //
                    // Deliberately computed against the on-disk size rather than ipatool's
                    // own byte count: the tool does not always print bytes next to the
                    // percentage, and the file it is writing into is always measurable.
                    // Recomputed every tick because a rounded percentage is coarse early on
                    // (at 5% it can be out by a tenth) and sharpens as the download runs;
                    // kept in a local so it is never mistaken for a measured size, learned,
                    // or remembered across runs.
                    if (total <= 0 && downloaded > 0 && state.ReportedPercent >= 5)
                    {
                        var derived = (long)(downloaded / (state.ReportedPercent / 100.0));
                        if (derived > downloaded)
                        {
                            if (derivedTotal <= 0)
                                AppLog.Info(
                                    $"size: no catalog entry and no Content-Length; deriving " +
                                    $"the total from {state.ReportedPercent:F0}% of " +
                                    $"{downloaded / 1048576.0:F1}MB -> {derived / 1048576.0:F1}MB");
                            derivedTotal = derived;
                        }
                    }
                    if (total <= 0) total = derivedTotal;

                    if (downloaded > 0)
                    {
                        if (!sawBytes)
                        {
                            // First real byte reading: baseline the speed window here so
                            // we never divide a large initial size by a tiny elapsed and
                            // report a nonsense speed on the first tick.
                            sawBytes = true;
                            prevBytes = downloaded;
                            prevTime = now;

                            // Which source actually produced the first bytes, and how
                            // long it took to leave the Connecting phase.
                            AppLog.Info(
                                $"progress: first bytes after {(now - startedUtc).TotalSeconds:F1}s " +
                                $"(disk={onDisk / 1048576.0:F1}MB parsed={parsed / 1048576.0:F1}MB " +
                                $"total={total / 1048576.0:F1}MB)");
                        }
                        if (downloaded > prevBytes) state.Touch();
                    }

                    // Speed: prefer the tool's own figure, else a smoothed local estimate.
                    // EMA (not a hard window reset) so a single locked read doesn't make
                    // the number jump to zero and back.
                    var elapsedSince = (now - prevTime).TotalSeconds;
                    if (elapsedSince >= 0.4)
                    {
                        var instant = downloaded > prevBytes ? (downloaded - prevBytes) / elapsedSince : 0;
                        emaSpeed = emaSpeed <= 0 ? instant : emaSpeed * 0.7 + instant * 0.3;
                        prevBytes = downloaded;
                        prevTime = now;
                    }
                    var speed = state.ReportedSpeed > 0 ? state.ReportedSpeed : emaSpeed;

                    // Percent: byte ratio when the total is known, else the tool's own
                    // percent, else 0 (UI shows an indeterminate bar — never a fake value).
                    double percent;
                    if (total > 0 && downloaded > 0)
                        percent = Math.Clamp(downloaded / (double)total * 100.0, 0, 99.5);
                    else if (state.ReportedPercent > 0)
                        percent = Math.Min(state.ReportedPercent, 99.5);
                    else
                        percent = 0;

                    var idleFor = now - state.LastActivity;

                    DownloadPhase phase;
                    if (downloaded <= 0)
                    {
                        phase = DownloadPhase.Connecting;
                    }
                    else if (total > 0 && downloaded >= total * 0.98 && idleFor.TotalSeconds > 1.5)
                    {
                        phase = DownloadPhase.Finalizing;
                    }
                    else if (total <= 0 && idleFor.TotalSeconds > 4.0)
                    {
                        // Unknown total and the bytes stopped: most likely repackaging.
                        phase = DownloadPhase.Finalizing;
                    }
                    else
                    {
                        phase = DownloadPhase.Transferring;
                    }

                    progress?.Report(new DownloadProgress(
                        phase == DownloadPhase.Finalizing ? Math.Max(percent, 99) : percent,
                        downloaded,
                        total,
                        phase == DownloadPhase.Finalizing ? 0 : speed,
                        phase,
                        now - startedUtc,
                        attempt));

                    // ---- Watchdog: a dead socket must not hang the queue forever ----
                    var limit = phase switch
                    {
                        DownloadPhase.Connecting => ConnectTimeout,
                        // Repackaging a large IPA is legitimately slow and silent.
                        DownloadPhase.Finalizing => TimeSpan.FromMinutes(10),
                        _ => StallTimeout,
                    };

                    // While connecting, any output counts as proof of life: the
                    // handshake produces no byte counts, so judging it by bytes alone
                    // would kill a download that is simply slow to start.
                    var silentFor = phase == DownloadPhase.Connecting
                        ? now - state.LastOutput
                        : idleFor;

                    if (silentFor > limit)
                    {
                        // Say why out loud. A stall that is retried silently looks to the
                        // user exactly like the hang this watchdog exists to prevent.
                        AppLog.Warn(
                            $"watchdog: no progress for {silentFor.TotalSeconds:F0}s " +
                            $"(limit {limit.TotalSeconds:F0}s, phase {phase}, " +
                            $"attempt {attempt}, {downloaded / 1048576.0:F1}MB" +
                            (total > 0 ? $"/{total / 1048576.0:F1}MB" : "") +
                            ") - restarting the transfer");

                        watchdogFired = true;
                        try { attemptCts.Cancel(); } catch { }
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
        });

        ProcessResult result;
        try
        {
            result = await _runner.RunStreamingAsync(
                _tools.IpatoolPath,
                args,
                onSegment: OnSegment,
                environment: env.Count > 0 ? env : null,
                workingDirectory: _tools.IpatoolWorkingDirectory,
                closeStdin: true,
                ct: attemptCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Distinguish "user pressed cancel" (rethrow) from "the watchdog killed a
            // dead socket" (retryable).
            ct.ThrowIfCancellationRequested();

            TryDeleteStaleFiles(outputPath);
            return (DownloadResult.Fail(Loc.Get("L.Error.ConnectionStalled")), true);
        }
        finally
        {
            attemptCts.Cancel();
            try { await reporter.ConfigureAwait(false); } catch { /* reporter shutdown */ }
        }

        if (watchdogFired)
        {
            TryDeleteStaleFiles(outputPath);
            return (DownloadResult.Fail(Loc.Get("L.Error.ConnectionStalled")), true);
        }

        var output = result.CombinedOutput;

        // ---- Success ---------------------------------------------------------------
        var finalPath = File.Exists(outputPath) ? outputPath : ResolveOutputPath(output, outputPath);
        if (result.Success && finalPath is not null && File.Exists(finalPath))
        {
            var finalTotal = new FileInfo(finalPath).Length;
            if (finalTotal > 0)
            {
                Volatile.Write(ref sizeHint[0], finalTotal);
                app.FileSizeBytes = finalTotal;

                // Remember the measured size on disk. For delisted apps this is the only
                // way to ever learn it: they are absent from Apple's catalog, and Apple
                // sends no Content-Length for them either, so the first download can only
                // show bytes-so-far. Recording it now means the next one has a real total
                // and a bar that fills.
                RememberSize(app, finalTotal);
            }
            progress?.Report(new DownloadProgress(
                100, finalTotal, finalTotal, 0, DownloadPhase.Transferring, DateTimeOffset.UtcNow - startedUtc, attempt));
            TryCleanStaging(stagingDir);

            // The repackaging step that puts the FairPlay licence into the archive belongs to
            // ipatool, and a download that exits 0 without it is indistinguishable from a good
            // one by size or exit code alone. Record what actually landed on disk: an archive
            // missing its licence installs cleanly and then will not launch, and this log line
            // is what tells that apart from a device fault later on.
            var license = IpaLicense.Inspect(finalPath);
            if (license.IsDefinitelyUnlicensed)
                AppLog.Warn($"Downloaded {app.Name} WITHOUT a FairPlay licence — " +
                            $"it will install but not launch: {license.Describe()}");
            else if (license.IsPartiallyLicensed)
                AppLog.Warn($"Downloaded {app.Name} without the blob its manifest names for " +
                            $"the main binary: {license.Describe()}");
            else
                AppLog.Info($"Licence check for {app.Name}: {license.Describe()}");

            return (DownloadResult.Ok(finalPath), false);
        }

        // ---- Failure classification -------------------------------------------------
        if (AuthService.IsSessionExpiredError(output))
        {
            AppLog.Warn($"Download {app.Name}: {SessionExpiredDetail}");
            return (DownloadResult.Expired(output), false);
        }

        var error = ExtractError(output);
        AppLog.Warn($"Download {app.Name} failed: {error}");

        // Never retry a license problem: the caller resolves it with an explicit
        // purchase, and a blind retry would just pay another handshake for nothing.
        if (LicenseRequiredRegex().IsMatch(output))
            return (DownloadResult.NeedsLicense(error), false);

        var isTransient = TransientRegex().IsMatch(output);
        if (isTransient) TryDeleteStaleFiles(outputPath);

        return (DownloadResult.Fail(DescribeStoreFailure(output), error), isTransient);
    }

    /// <summary>
    /// Mutable per-attempt transfer state shared by the output parser (tool thread)
    /// and the reporter loop.
    ///
    /// Note: C# does not permit <c>volatile double</c>, so the speed and percent are
    /// stored as integers (speed in bytes/sec, percent scaled by 100) and exposed as
    /// doubles.
    /// </summary>
    private sealed class TransferState
    {
        private long _downloaded;
        private long _lastActivityTicks = DateTime.UtcNow.Ticks;
        private long _lastOutputTicks = DateTime.UtcNow.Ticks;
        private long _speedBps;
        private int _percentX100;

        /// <summary>Bytes reported by the tool's own progress bar.</summary>
        public long Downloaded => Volatile.Read(ref _downloaded);

        /// <summary>Speed reported by the tool, bytes/sec. 0 when unknown.</summary>
        public double ReportedSpeed
        {
            get => Volatile.Read(ref _speedBps);
            set => Volatile.Write(ref _speedBps, (long)Math.Max(0, value));
        }

        /// <summary>
        /// Percent reported by the tool, 0-100. 0 when unknown. Read-only by design:
        /// it advances through <see cref="AdvancePercent"/> so a repainted frame can
        /// neither walk it backwards nor be mistaken for progress.
        /// </summary>
        public double ReportedPercent => Volatile.Read(ref _percentX100) / 100.0;

        public DateTimeOffset LastActivity =>
            new(Volatile.Read(ref _lastActivityTicks), TimeSpan.Zero);

        /// <summary>
        /// Last moment the tool emitted anything at all, numeric or not. Used as
        /// proof-of-life while connecting: a handshake can be slow and completely
        /// silent on numbers, and killing it then is wrong.
        /// </summary>
        public DateTimeOffset LastOutput =>
            new(Volatile.Read(ref _lastOutputTicks), TimeSpan.Zero);

        public void TouchOutput() =>
            Volatile.Write(ref _lastOutputTicks, DateTime.UtcNow.Ticks);

        /// <summary>
        /// Records the byte count reported by the tool. Returns true only when the
        /// number actually moved forward.
        ///
        /// The return value is what the stall watchdog is judged on, and that is the
        /// whole point: ipatool keeps repainting "63.7% ... 0 B/s" long after the
        /// socket is dead. Treating a repaint as progress refreshed LastActivity on
        /// every frame, so the timeout never elapsed and the transfer hung forever
        /// instead of being retried.
        /// </summary>
        public bool SetDownloaded(long value)
        {
            // Monotonic: a re-rendered bar frame must never walk the number backwards.
            if (value > Volatile.Read(ref _downloaded))
            {
                Volatile.Write(ref _downloaded, value);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Records the tool's own percentage, returning true only on a real increase.
        /// Fallback proof-of-progress for the bar format that prints a percentage but
        /// no byte totals.
        /// </summary>
        public bool AdvancePercent(double value)
        {
            var scaled = (int)Math.Clamp(value * 100, 0, 10000);
            if (scaled > Volatile.Read(ref _percentX100))
            {
                Volatile.Write(ref _percentX100, scaled);
                return true;
            }
            return false;
        }

        public void Touch() => Volatile.Write(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// Builds the strictly ASCII output path.
    ///
    /// The path is handed to native tools (ipatool v3 is Go + C++ nlohmann/json +
    /// libzip). Non-ASCII bytes break them: nlohmann json.dump() throws
    /// "invalid UTF-8 byte" (type_error.316) and libzip zip_open fails with ENOENT
    /// on the mangled name.
    /// </summary>
    private string BuildOutputPath(AppEntry app, string? targetFolder = null)
    {
        var safeName = MakeAsciiSafeName(app.Name);
        if (string.IsNullOrEmpty(safeName))
            safeName = MakeAsciiSafeName(app.BundleId ?? "");
        if (string.IsNullOrEmpty(safeName))
            safeName = "app";

        // No known version -> stamp the download date instead of a constant.
        //
        // The old fallback was the literal "latest", which is the same string forever.
        // Apps with no App Store catalog entry (delisted ones, where the version can
        // never be looked up) therefore always produced one identical filename, so
        // every re-download collided with the previous one even when it was genuinely
        // a different build. A date keeps those builds apart.
        var version = MakeAsciiSafeName(app.LatestVersion ?? "");
        if (string.IsNullOrEmpty(version))
            version = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Apps resolved by bundle identifier have no store id, and a literal "0" in the
        // name would make every one of them look like the same app. The bundle id is the
        // only identifier they do have, so it stands in.
        var identifier = app.AppStoreId > 0
            ? app.AppStoreId.ToString(CultureInfo.InvariantCulture)
            : MakeAsciiSafeName(app.BundleId ?? "app");

        return Path.Combine(
            string.IsNullOrWhiteSpace(targetFolder) ? _tools.AppsFolder : targetFolder!,
            $"{safeName}_{identifier}_{version}.ipa");
    }

    /// <summary>
    /// Adds " (2)", " (3)", … before the extension until the path is free, so a new
    /// download can land beside an existing file instead of on top of it.
    /// </summary>
    private static string MakeUniquePath(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        // Bounded so a pathological directory can never spin forever; 999 collisions
        // of one app is far past anything real.
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        // Last resort: a timestamp is effectively collision-free.
        return Path.Combine(
            dir,
            $"{name} ({DateTime.Now:yyyyMMdd-HHmmss}){ext}");
    }

    /// <summary>
    /// Asks the UI what to do about an existing file, honouring a previous
    /// "apply to all" answer. Falls back to KeepBoth when no resolver is attached or
    /// the prompt throws, because that is the only outcome that cannot lose data.
    /// </summary>
    private async Task<FileConflictDecision> ResolveConflictAsync(
        AppEntry app, string existingPath, CancellationToken ct)
    {
        if (_stickyConflictDecision is { } sticky) return sticky;

        var resolver = FileConflictResolver;
        if (resolver is null) return FileConflictDecision.KeepBoth;

        try
        {
            var info = new FileInfo(existingPath);
            var request = new FileConflictRequest(
                app.Name,
                existingPath,
                info.Exists ? info.Length : 0,
                info.Exists ? info.LastWriteTime : DateTime.Now);

            var response = await resolver(request, ct).ConfigureAwait(false);
            if (response.ApplyToAll) _stickyConflictDecision = response.Decision;
            return response.Decision;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn($"File conflict dialog failed ({ex.Message}); keeping both files.");
            return FileConflictDecision.KeepBoth;
        }
    }

    /// <summary>
    /// Completes a "replace" decision: the freshly downloaded file takes the place of
    /// the old one. Runs only after a successful transfer.
    ///
    /// If the swap itself fails (file locked by another program, for instance) the new
    /// download is kept under its temporary name and reported as the result — the user
    /// still has a working file rather than an error.
    /// </summary>
    private static DownloadResult FinishReplace(DownloadResult result, string? replaceTarget)
    {
        if (replaceTarget is null || result.IpaPath is null) return result;

        try
        {
            File.Delete(replaceTarget);
            File.Move(result.IpaPath, replaceTarget);
            return DownloadResult.Ok(replaceTarget);
        }
        catch (Exception ex)
        {
            AppLog.Warn(
                $"Could not replace \"{Path.GetFileName(replaceTarget)}\" ({ex.Message}); " +
                $"the new file was kept as \"{Path.GetFileName(result.IpaPath)}\".");
            return result;
        }
    }

    /// <summary>
    /// Live size of a file that another process is currently writing.
    ///
    /// This must NOT use <see cref="FileInfo.Length"/>. On NTFS the size held in the
    /// directory entry is only refreshed when the writer flushes or closes the handle,
    /// so <c>FileInfo.Length</c> — and the <c>FileInfo</c> objects returned by
    /// <c>EnumerateFiles</c>, which are populated from that same cached directory
    /// data — reports a stale value. In practice that means 0 for the entire duration
    /// of the download, then the full size the instant ipatool closes the file.
    ///
    /// That stale read is precisely why the bar sat on "Connecting to the App Store" and
    /// then jumped straight to finished. Opening a handle and querying the file object
    /// itself returns the true current length.
    ///
    /// <see cref="FileShare.ReadWrite"/> is required or the open fails while ipatool
    /// holds the file; <see cref="FileShare.Delete"/> is required so we never block
    /// the writer from renaming or removing it on completion.
    /// </summary>
    private static long LiveLength(string path)
    {
        try
        {
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return fs.Length;
        }
        catch
        {
            // Missing, renamed on completion, or briefly unopenable — try next tick.
            return 0;
        }
    }

    /// <summary>Extensions and names a partially downloaded IPA may carry.</summary>
    private static bool LooksLikePayload(string name) =>
        name.EndsWith(".ipa", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".download", StringComparison.OrdinalIgnoreCase)
        || name.Contains(".ipa.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("ipatool", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Largest plausible in-flight size for this download.
    ///
    /// The temp filename and its location are an ipatool implementation detail that
    /// differs between v2 and v3, so this deliberately assumes neither: it checks the
    /// requested target, any freshly created candidate beside it, and everything in
    /// our staging folder. Staging is wiped before each attempt, so whatever is there
    /// necessarily belongs to the current download.
    /// </summary>
    private static long ProbeSize(string outputPath, string stagingDir, DateTime startedUtc)
    {
        var best = LiveLength(outputPath);

        // Fresh candidates in the destination folder. ipatool does not always honour
        // the requested -o name (ResolveOutputPath exists for exactly that reason), so
        // match on "created since this attempt began" instead of an exact filename.
        var dir = Path.GetDirectoryName(outputPath);
        if (dir is not null && Directory.Exists(dir))
        {
            try
            {
                var cutoff = startedUtc.AddSeconds(-10);
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                {
                    if (!LooksLikePayload(f.Name)) continue;

                    // CreationTimeUtc is dependable here. LastWriteTimeUtc is not, for
                    // the same stale-metadata reason documented on LiveLength.
                    if (f.CreationTimeUtc < cutoff) continue;

                    var len = LiveLength(f.FullName);
                    if (len > best) best = len;
                }
            }
            catch { /* enumeration raced with a rename; next tick */ }
        }

        // Staging folder: cleared before every attempt, so no date filter is applied —
        // timestamps here would be unreliable for the reason above anyway.
        if (Directory.Exists(stagingDir))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
                {
                    var len = LiveLength(f);
                    if (len > best) best = len;
                }
            }
            catch { }
        }

        return best;
    }

    // ---- Learned sizes ---------------------------------------------------------------
    //
    // Apple's catalog answers nothing for delisted apps, and their downloads carry no
    // Content-Length, so neither the lookup below nor ipatool's progress bar can supply a
    // total. The size measured after a successful download is stored here and reused,
    // which is what lets the bar fill on the second and later runs.

    private static readonly object LearnedSizesLock = new();

    // String keys, because an app is remembered both as "<id>" and as "<id>@<version>": the
    // size changes with every release, and a figure left over from an older build made the
    // bar run past its own total. JSON object keys are strings anyway, so tables written by
    // earlier versions (which were keyed by the numeric id) still load unchanged.
    private static Dictionary<string, long>? _learnedSizes;

    /// <summary>Loads the learned-size table, or an empty one if absent/unreadable.</summary>
    private Dictionary<string, long> LoadLearnedSizes()
    {
        lock (LearnedSizesLock)
        {
            if (_learnedSizes is not null) return _learnedSizes;

            try
            {
                if (File.Exists(_tools.LearnedSizesFile))
                {
                    var json = File.ReadAllText(_tools.LearnedSizesFile);
                    _learnedSizes = JsonSerializer.Deserialize<Dictionary<string, long>>(json)
                                    ?? new Dictionary<string, long>();
                }
                else
                {
                    _learnedSizes = new Dictionary<string, long>();
                }
            }
            catch
            {
                // A corrupt file must not break downloading; start over with an empty table.
                _learnedSizes = new Dictionary<string, long>();
            }

            return _learnedSizes;
        }
    }

    /// <summary>Records the exact size of a completed download for future runs.</summary>
    private void RememberSize(AppEntry app, long bytes)
    {
        var identity = LearnedIdentity(app);
        if (identity is null || bytes <= 0) return;

        try
        {
            lock (LearnedSizesLock)
            {
                var table = LoadLearnedSizes();
                var keys = LearnedKeys(identity, app.LatestVersion);

                // Both keys are written: the exact one so a re-download of this build gets
                // the right total, and the bare id so a build we have never seen still
                // starts from something close instead of nothing.
                var changed = false;
                foreach (var key in keys)
                {
                    if (table.TryGetValue(key, out var existing) && existing == bytes) continue;
                    table[key] = bytes;
                    changed = true;
                }
                if (!changed) return;

                Directory.CreateDirectory(_tools.DataFolder);
                File.WriteAllText(_tools.LearnedSizesFile, JsonSerializer.Serialize(table));
            }
        }
        catch
        {
            // Purely an optimisation: failing to persist costs an unknown total next time,
            // nothing more, so it must never surface as a download error.
        }
    }

    /// <summary>
    /// What a learned size is filed under, or null when the app cannot be identified.
    ///
    /// The store id is preferred so the table stays compatible with what earlier versions
    /// wrote, but a bundle identifier is accepted in its place. That second form is the
    /// whole point of this method: repacked and delisted entries ("… (Оригинал)") carry no
    /// store id, so keying on the id alone meant their size was never recorded and every
    /// download of them started over with no total - which is exactly the "всего неизвестно"
    /// the user still saw. Prefixed so a bundle identifier can never collide with an id.
    /// </summary>
    private static string? LearnedIdentity(AppEntry app)
    {
        if (app.AppStoreId > 0) return app.AppStoreId.ToString();
        return string.IsNullOrWhiteSpace(app.BundleId)
            ? null
            : "b:" + app.BundleId.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Keys under which a size is stored: the exact build first, then the app on its own.
    /// Lookups walk them in this order, so a size measured from the very build being
    /// downloaded always wins over one left from an earlier release.
    /// </summary>
    private static string[] LearnedKeys(string identity, string? version)
        => string.IsNullOrWhiteSpace(version)
            ? new[] { identity }
            : new[] { $"{identity}@{version.Trim()}", identity };

    /// <summary>Previously measured size for this build, or 0 if not known yet.</summary>
    private long GetLearnedSize(AppEntry app)
    {
        var identity = LearnedIdentity(app);
        if (identity is null) return 0;

        lock (LearnedSizesLock)
        {
            var table = LoadLearnedSizes();
            foreach (var key in LearnedKeys(identity, app.LatestVersion))
                if (table.TryGetValue(key, out var bytes) && bytes > 0) return bytes;
            return 0;
        }
    }

    /// <summary>
    /// Size of an IPA for this app that is already sitting in the local Apps folder, or 0
    /// when there is none. Returns the file's real length, so it is exact whenever the app
    /// has not been updated since - and a close estimate when it has.
    /// </summary>
    private static long TrySizeOfExistingCopy(AppEntry app)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(app.LocalIpaPath)) return 0;

            var info = new FileInfo(app.LocalIpaPath);
            // Anything under a megabyte is a truncated leftover, not an app.
            return info.Exists && info.Length > 1_048_576 ? info.Length : 0;
        }
        catch
        {
            // Only a seed for the progress bar; an unreadable path is not worth reporting.
            return 0;
        }
    }

    /// <summary>
    /// Looks up an app's IPA size (bytes) from the public iTunes Lookup API by its
    /// App Store id. Returns 0 on any failure. Only used as an early seed — the
    /// progress bar's own total supersedes it as soon as the transfer starts.
    ///
    /// The lookup is tried against several storefronts because the API answers per
    /// country: an app that is not sold in the queried storefront returns zero
    /// results. A plain lookup hits the US storefront, so apps published only in
    /// other regions used to yield no size at all — leaving the transfer with no
    /// total, hence no percentage and a progress bar that never filled.
    /// </summary>
    private async Task<long> TryLookupFileSizeAsync(AppEntry app, CancellationToken ct)
    {
        // A size measured from an earlier download of this exact app beats the catalog:
        // it is the real file, not the generic-device figure, and it is the only source
        // for delisted apps that the catalog does not list at all.
        var learned = GetLearnedSize(app);
        if (learned > 0) return learned;

        // Apple's lookup takes either an id or a bundle identifier. The second form is what
        // makes this work at all for entries that carry no store id, which is every app
        // queued by bundle identifier; without it those downloads had no total to ask for.
        var query = app.AppStoreId > 0
            ? $"id={app.AppStoreId}"
            : string.IsNullOrWhiteSpace(app.BundleId)
                ? null
                : $"bundleId={Uri.EscapeDataString(app.BundleId.Trim())}";
        if (query is null) return 0;

        foreach (var storefront in ItunesStorefront.Candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));

                var url = $"https://itunes.apple.com/lookup?{query}&entity=software"
                          + ItunesStorefront.CountryParam(storefront);
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

                // Reached only when this storefront simply doesn't carry the app;
                // continue with the next one.
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* network/timeout on this storefront — try the next */ }
        }

        // Every storefront came back empty (a delisted app has no catalog entry
        // anywhere). The caller falls back to a bytes-only, indeterminate display.
        return 0;
    }

    /// <summary>
    /// Decides whether an archive already at the destination can stand in for a fresh
    /// download, and returns it when so. Only used in <see cref="ResumeMode.KeepPartialFiles"/>.
    ///
    /// Deliberately conservative — the failure this must never cause is a half-written or
    /// wrongly-licensed file being installed and then not launching, which is far worse
    /// than paying for a download again. An archive qualifies only when it:
    ///   * opens as a valid zip whose licence blobs are present (the check that catches a
    ///     truncated file, since a cut-off zip has no readable central directory), and
    ///   * belongs to the signed-in Apple ID, and
    ///   * matches the size learned from a previous successful download of this build,
    ///     when such a size is known.
    /// Anything else returns null and the normal download path takes over.
    /// </summary>
    private FileInfo? TryReuseExistingArchive(AppEntry app, string outputPath)
    {
        try
        {
            if (!File.Exists(outputPath)) return null;

            var info = new FileInfo(outputPath);
            if (info.Length <= 0) return null;

            // A known size that disagrees means this is a partial file from an interrupted
            // attempt (or a different build), so it cannot be handed back as finished.
            var learned = GetLearnedSize(app);
            if (learned > 0 && info.Length != learned)
            {
                AppLog.Info($"Keeping partial '{info.Name}' ({info.Length / 1048576.0:F1}MB of " +
                            $"{learned / 1048576.0:F1}MB) but re-downloading: it is incomplete.");
                return null;
            }

            if (IpaLicense.BelongsToAnotherAccount(outputPath, _auth.CurrentAccount?.Email, out _))
                return null;

            var license = IpaLicense.Inspect(outputPath);

            // ReadError means the zip directory could not be read, which is exactly what a
            // truncated download looks like. Unlike elsewhere — where an unreadable archive
            // is merely logged — here it must veto reuse: proceeding would install a file
            // that was never finished.
            if (license.ReadError is not null
                || license.IsDefinitelyUnlicensed
                || license.IsPartiallyLicensed)
            {
                AppLog.Info($"Not reusing '{info.Name}': {license.Describe()}");
                return null;
            }

            AppLog.Info($"Reusing the copy of {app.Name} already on disk " +
                        $"({info.Length / 1048576.0:F1}MB) instead of downloading it again.");
            app.FileSizeBytes = info.Length;
            return info;
        }
        catch (Exception ex)
        {
            // Reuse is an optimisation; if anything about the probe fails, download normally.
            AppLog.Warn($"Could not verify the existing copy of {app.Name}, downloading it again: {ex.Message}");
            return null;
        }
    }

    /// <summary>Deletes a stale target file and any leftover partial variants
    /// (name.ipa, name.ipa.part, name.ipa.tmp, …) so a prior attempt can't be
    /// mistaken for live progress. Retries briefly: a just-killed ipatool may
    /// still hold the handle for a moment.</summary>
    private static void TryDeleteStaleFiles(string outputPath)
    {
        DeleteWithRetry(outputPath);

        var dir = Path.GetDirectoryName(outputPath);
        var fileName = Path.GetFileName(outputPath);
        if (dir is null || !Directory.Exists(dir)) return;

        try
        {
            foreach (var f in Directory.GetFiles(dir, fileName + ".*"))
                DeleteWithRetry(f);
        }
        catch { /* best effort */ }
    }

    private static void TryCleanStaging(string stagingDir)
    {
        if (!Directory.Exists(stagingDir)) return;
        try
        {
            foreach (var f in Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories))
                DeleteWithRetry(f);
        }
        catch { /* best effort */ }
    }

    private static void DeleteWithRetry(string path)
    {
        for (var i = 0; i < 4; i++)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.Delete(path);
                return;
            }
            catch
            {
                Thread.Sleep(120);
            }
        }
    }

    /// <summary>Searches the App Store (ipatool search).</summary>
    public async Task<IReadOnlyList<AppEntry>> SearchAsync(string term, int limit = 20, CancellationToken ct = default)
    {
        var result = await _runner.RunAsync(
            _tools.IpatoolPath,
            new[] { "search", term, "-l", limit.ToString(), "--keychain-passphrase", ToolLocator.KeychainPassphrase,
                    "--format", "json" },
            closeStdin: true,
            workingDirectory: _tools.IpatoolWorkingDirectory,
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
            workingDirectory: _tools.IpatoolWorkingDirectory,
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

    // ---- Parsing helpers --------------------------------------------------------

    private static bool TryParseNumber(string raw, out double value) =>
        double.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static long ToBytes(double value, string? unit)
    {
        if (string.IsNullOrEmpty(unit)) return (long)value;
        return unit.ToLowerInvariant() switch
        {
            "b" => (long)value,
            "kb" or "kib" => (long)(value * 1024d),
            "mb" or "mib" => (long)(value * 1024d * 1024d),
            "gb" or "gib" => (long)(value * 1024d * 1024d * 1024d),
            _ => (long)value,
        };
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
        var collapsed = Regex.Replace(sb.ToString(), "_+", "_");
        return collapsed.Trim('_', '.');
    }

    /// <summary>
    /// Recovers the actual .ipa path when it differs from the requested one.
    /// Handles both JSON output (older code path) and plain text.
    /// </summary>
    private static string? ResolveOutputPath(string output, string requestedPath)
    {
        // JSON form: {"output":"..."}
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("output", out var path))
                {
                    var value = path.GetString();
                    if (!string.IsNullOrEmpty(value) && File.Exists(value)) return value;
                }
            }
            catch (JsonException) { }
        }

        // Text form: any absolute .ipa path mentioned in the output.
        foreach (Match m in IpaPathRegex().Matches(output))
        {
            var candidate = m.Groups[1].Value.Trim();
            if (File.Exists(candidate)) return candidate;
        }

        return File.Exists(requestedPath) ? requestedPath : null;
    }

    /// <summary>
    /// Extracts a compact error message. Works for JSON output and for plain text
    /// (text mode is now used for downloads, where the whole log would otherwise be
    /// dumped into the UI).
    /// </summary>
    private static string ExtractError(string output)
    {
        var lines = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0)
            .ToList();

        // JSON form: {"error":"..."}
        foreach (var line in lines)
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("error", out var err))
                    return Trim(err.GetString() ?? line);
            }
            catch (JsonException) { }
        }

        // Text form: prefer lines that actually mention a failure.
        var flagged = lines
            .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)
                     || l.Contains("failed", StringComparison.OrdinalIgnoreCase)
                     || l.Contains("fatal", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (flagged.Count > 0)
            return Trim(string.Join(" · ", flagged.TakeLast(2)));

        if (lines.Count > 0)
            return Trim(string.Join(" · ", lines.TakeLast(2)));

        return "Unknown error";

        static string Trim(string s)
        {
            s = s.Trim();
            return s.Length <= 400 ? s : s[..400] + "…";
        }
    }
}
