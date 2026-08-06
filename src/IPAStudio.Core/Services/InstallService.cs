using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using iMobileDevice;
using iMobileDevice.iDevice;
using iMobileDevice.SpringBoardServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Models;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services;

/// <summary>
/// Progress snapshot reported while installing onto a device.
/// </summary>
/// <param name="Percent">
/// 0 while the extent of the current phase is unknown (the upload to the device reports
/// nothing at all), otherwise the real percentage for that phase.
/// </param>
/// <param name="Status">Phase key: Preparing, Copying, Installing or Complete.</param>
/// <param name="TotalBytes">Size of the IPA, so the UI can show what is being moved.</param>
/// <param name="Elapsed">Time spent in the current phase.</param>
public readonly record struct InstallProgress(
    double Percent,
    string Status,
    long TotalBytes = 0,
    TimeSpan Elapsed = default);

/// <summary>Result of an install attempt.</summary>
public sealed class InstallResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }

    /// <summary>
    /// Set when the attempt was refused because the IPA carries no FairPlay licence, or when
    /// the installer reported that it could not pass one on. Kept apart from an ordinary
    /// failure because the remedy differs: nothing is wrong with the device or the cable, the
    /// file itself is incomplete and has to be fetched again.
    /// </summary>
    public bool LicenseMissing { get; init; }

    /// <summary>
    /// Set when the archive is licensed, but to a different Apple ID than the one the target
    /// device uses. Separate from a missing licence because the file is not damaged and
    /// fetching it again changes nothing: it has to be fetched again <em>as the other
    /// account</em>. Carries both addresses so the message can name them.
    /// </summary>
    public bool AccountMismatch { get; init; }

    /// <summary>Apple ID the archive is licensed to. Only set with <see cref="AccountMismatch"/>.</summary>
    public string? IpaAccount { get; init; }

    /// <summary>Apple ID the device uses. Only set with <see cref="AccountMismatch"/>.</summary>
    public string? DeviceAccount { get; init; }

    public static InstallResult Ok() => new() { Success = true };
    public static InstallResult Fail(string error) => new() { Error = error };

    /// <summary>An IPA that would install cleanly and then refuse to launch.</summary>
    public static InstallResult NoLicense(string detail) =>
        new() { Error = detail, LicenseMissing = true };

    /// <summary>An IPA licensed to somebody else, which this device can never decrypt.</summary>
    public static InstallResult WrongAccount(string ipaAccount, string deviceAccount) =>
        new()
        {
            Error = $"IPA is licensed to {ipaAccount}, but the device uses {deviceAccount}",
            AccountMismatch = true,
            IpaAccount = ipaAccount,
            DeviceAccount = deviceAccount,
        };
}

/// <summary>
/// Installs IPA files onto a connected device via ideviceinstaller and lists
/// installed apps for status badges. Installs run one at a time per device, and in
/// parallel across devices.
/// </summary>
public sealed partial class InstallService
{
    private readonly ToolLocator _tools;
    private readonly ProcessRunner _runner;

    /// <summary>
    /// How many installs may run at once on a single device.
    ///
    /// Was effectively 1, enforced by a process-wide semaphore — which also meant a second
    /// connected iPhone gained nothing, since it waited behind the first one's upload.
    /// installd does accept concurrent sessions (installing from this app and from 3uTools
    /// at the same time works), so the limit was self-imposed.
    ///
    /// Kept small on purpose. The bottleneck is the USB link, so more concurrency stops
    /// buying throughput quickly and each extra stream makes the per-app progress noisier.
    /// </summary>
    public int MaxParallelInstallsPerDevice
    {
        get => _maxParallelInstallsPerDevice;
        set
        {
            var clamped = Math.Clamp(value, 1, 4);
            if (clamped == _maxParallelInstallsPerDevice) return;

            _maxParallelInstallsPerDevice = clamped;

            // A SemaphoreSlim's capacity is fixed once constructed, and the limiters below
            // are cached per device forever. Without dropping them, changing the setting
            // would only affect devices connected for the first time afterwards — the
            // device already in the list would keep the old limit for the whole session.
            //
            // Safe because a limiter is only ever taken through DeviceLock: installs already
            // in flight hold the old object and release it correctly, while the next install
            // builds a fresh one at the new size. Worst case an install briefly overlaps the
            // new limit, which installd tolerates.
            _deviceLocks.Clear();
        }
    }

    private int _maxParallelInstallsPerDevice = 2;

    /// <summary>
    /// Concurrency limiter per device, keyed by UDID. Separate devices get separate
    /// limiters and therefore run fully in parallel.
    ///
    /// Entries are never removed: a UDID is a few dozen bytes and a session sees a handful
    /// of devices, which is not worth the race that pruning a limiter somebody is about to
    /// wait on would introduce.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _deviceLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-bundle-id locks, so the same app is never installed twice at once.
    ///
    /// This is the one collision that concurrency really does introduce: two sessions for
    /// one bundle id make installd fail with "coordinated app install already exists".
    /// Serializing by bundle id keeps that impossible while leaving different apps parallel.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _bundleLocks = new(StringComparer.OrdinalIgnoreCase);

    private SemaphoreSlim DeviceLock(string udid) =>
        _deviceLocks.GetOrAdd(udid ?? "", _ => new SemaphoreSlim(
            Math.Max(1, MaxParallelInstallsPerDevice),
            Math.Max(1, MaxParallelInstallsPerDevice)));

    private SemaphoreSlim BundleLock(string udid, string bundleId) =>
        _bundleLocks.GetOrAdd($"{udid}|{bundleId}", _ => new SemaphoreSlim(1, 1));

    /// <summary>How often the copy phase reports, since the tool itself says nothing.</summary>
    private static readonly TimeSpan CopyTick = TimeSpan.FromMilliseconds(500);

    [GeneratedRegex(@"(\d{1,3})\s*%")]
    private static partial Regex PercentRegex();

    [GeneratedRegex("\"([^\"]*)\"")]
    private static partial Regex QuotedRegex();

    public InstallService(ToolLocator tools, ProcessRunner runner)
    {
        _tools = tools;
        _runner = runner;
    }

    /// <summary>
    /// Installs an IPA on the device, reporting the three real phases: local preparation,
    /// the upload to the device, and the device-side install.
    ///
    /// Output is read character by character rather than line by line. ideviceinstaller
    /// draws its status with a carriage return and no newline ("\rInstall: Complete (40%)"),
    /// so a line-oriented reader receives NOTHING until the process exits — which is why
    /// the install used to sit on its first message and then jump straight to finished.
    ///
    /// Up to <see cref="MaxParallelInstallsPerDevice"/> installs run at once on one device,
    /// and separate devices are unconstrained; the same app is never installed twice
    /// concurrently.
    /// </summary>
    /// <param name="deviceAppleId">
    /// Apple ID the target device is signed in to, when it is known. Used to refuse an archive
    /// licensed to somebody else before it is pushed. Optional because modern iOS usually hides
    /// this, and a check that cannot be made must not become a check that guesses.
    /// </param>
    /// <param name="bundleId">
    /// Bundle id of the app, used only to keep two installs of the same app from overlapping.
    /// Falls back to the archive path when unknown, which is the same app in practice.
    /// </param>
    public async Task<InstallResult> InstallAsync(
        string udid,
        string ipaPath,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default,
        string? deviceAppleId = null,
        string? bundleId = null)
    {
        if (!File.Exists(ipaPath))
            return InstallResult.Fail($"IPA file not found: {ipaPath}");

        long totalBytes = 0;
        try { totalBytes = new FileInfo(ipaPath).Length; } catch { /* size is cosmetic */ }

        // ---- Phase 0: is this archive even licensed? --------------------------------
        // An App Store IPA is FairPlay-encrypted, and the key material travels beside the
        // bundle rather than inside it (iTunesMetadata.plist plus one SC_Info/*.sinf per
        // encrypted binary). installd verifies only Apple's signature, which is intact
        // either way, so an archive with no licence installs successfully and the app then
        // dies the instant it is launched — the exact "installs fine, will not open"
        // complaint. Reading the zip directory costs milliseconds, so it is checked before
        // several hundred megabytes are pushed over the cable rather than after.
        var license = IpaLicense.Inspect(ipaPath);
        AppLog.Info($"Install licence check: {license.Describe()}");

        if (license.IsDefinitelyUnlicensed)
        {
            // Refused rather than attempted. Installing it would report success and leave an
            // app on the home screen that cannot start, which is a worse outcome than a clear
            // message: the user would have no way to tell that from a broken phone.
            return InstallResult.NoLicense(
                $"IPA has no FairPlay licence ({license.Describe()})");
        }

        // A licence is issued to one Apple ID. An archive fetched as somebody else installs
        // just as cleanly, and then the kernel refuses to decrypt it, the launch dies as an
        // authentication error, and the phone spends minutes asking the store for a licence
        // it will never be given — because it can only ask on behalf of its own account.
        // Refused here rather than diagnosed afterwards from a log.
        if (!string.IsNullOrWhiteSpace(deviceAppleId)
            && !string.IsNullOrWhiteSpace(license.AppleId)
            && !string.Equals(license.AppleId, deviceAppleId, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Warn($"Install refused: IPA is licensed to {license.AppleId}, " +
                        $"device is signed in as {deviceAppleId}");
            return InstallResult.WrongAccount(license.AppleId!, deviceAppleId!);
        }

        if (license.IsPartiallyLicensed)
        {
            // The archive has blobs but not the one the manifest names for the main
            // executable. That should stop it launching, yet it is only logged: the previous
            // version of this check was itself too eager, and refusing an app that turns out
            // to run is worse than a line in the log.
            AppLog.Warn("IPA is missing the blob its manifest names for the main binary; " +
                        $"it may not launch: {license.Describe()}");
        }

        // Two gates, always taken in this order (bundle, then device) everywhere, so no two
        // callers can hold one and wait on the other's — i.e. no deadlock.
        var bundleLock = BundleLock(udid, bundleId ?? ipaPath);
        await bundleLock.WaitAsync(ct).ConfigureAwait(false);

        var deviceLock = DeviceLock(udid);
        try
        {
            await deviceLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Cancelled while queuing: hand back the bundle gate, or this app could never
            // be installed again for the lifetime of the process.
            bundleLock.Release();
            throw;
        }

        string installPath = ipaPath;
        string? stagedCopy = null;
        try
        {
            // ---- Phase 1: make sure the tool can open the file at all ----------------
            (installPath, stagedCopy) = await PrepareLocalPathAsync(
                ipaPath, totalBytes, progress, ct).ConfigureAwait(false);

            var failed = false;
            string? errorLine = null;
            string? licenseWarning = null;
            var copying = true;
            var phaseStart = DateTimeOffset.UtcNow;

            void ParseSegment(string segment)
            {
                var line = segment.Trim();
                if (line.Length == 0) return;

                var match = PercentRegex().Match(line);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var pct))
                {
                    // The device is doing the work now, so the upload is over.
                    copying = false;
                    var status = line.Contains("Complete", StringComparison.OrdinalIgnoreCase)
                        ? "Complete"
                        : "Installing";
                    progress?.Report(new InstallProgress(
                        Math.Clamp(pct, 1, 100), status, totalBytes));
                }
                else if (line.Contains("Installing", StringComparison.OrdinalIgnoreCase))
                {
                    copying = false;
                    progress?.Report(new InstallProgress(0, "Installing", totalBytes));
                }
                else if (line.Contains("DONE", StringComparison.Ordinal))
                {
                    // "Copying '…' to device... DONE." — the upload finished, and the
                    // device has not started reporting yet.
                    copying = false;
                    progress?.Report(new InstallProgress(0, "Installing", totalBytes));
                }
                else if (line.Contains("Complete", StringComparison.OrdinalIgnoreCase))
                {
                    copying = false;
                    progress?.Report(new InstallProgress(100, "Complete", totalBytes));
                }

                // ideviceinstaller announces a missing licence as a WARNING and then carries
                // on to install the app anyway and exit 0:
                //
                //     WARNING: could not locate iTunesMetadata.plist in archive!
                //     WARNING: could not locate Payload/X.app/SC_Info/X.sinf in archive!
                //
                // Only ERROR and "failed" were examined before, so these lines were dropped on
                // the floor and the install was reported as a success. The app on the phone
                // then would not open, and nothing anywhere said why — the tool had in fact
                // said so all along.
                if (line.Contains("WARNING", StringComparison.OrdinalIgnoreCase) &&
                    (line.Contains("sinf", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("iTunesMetadata", StringComparison.OrdinalIgnoreCase)))
                {
                    licenseWarning = line;
                    AppLog.Warn($"ideviceinstaller: {line}");
                    return;
                }

                if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("failed", StringComparison.OrdinalIgnoreCase))
                {
                    failed = true;
                    errorLine = line;
                }
            }

            // ---- Phase 2 heartbeat ---------------------------------------------------
            // ideviceinstaller's first act is to upload the IPA to the device, and it
            // prints absolutely nothing while doing so — on a 500 MB app that is minutes
            // of silence. Report elapsed time and the size being sent, so the screen
            // shows work in progress instead of one frozen message.
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeat = Task.Run(async () =>
            {
                try
                {
                    while (!heartbeatCts.IsCancellationRequested)
                    {
                        await Task.Delay(CopyTick, heartbeatCts.Token).ConfigureAwait(false);
                        if (!copying) break;
                        progress?.Report(new InstallProgress(
                            0, "Copying", totalBytes, DateTimeOffset.UtcNow - phaseStart));
                    }
                }
                catch (OperationCanceledException) { /* install finished */ }
            }, CancellationToken.None);

            progress?.Report(new InstallProgress(0, "Copying", totalBytes));

            // Bundled ideviceinstaller (libimobiledevice 1.x) selects the mode via a
            // flag: -i/--install ARCHIVE (NOT an "install" subcommand). Using a
            // subcommand caused "ERROR: No mode/command was supplied."
            var result = await _runner.RunStreamingAsync(
                _tools.IdeviceInstallerPath,
                DeviceTransport.TargetArgs(udid, "-i", installPath),
                onSegment: ParseSegment,
                ct: ct).ConfigureAwait(false);

            heartbeatCts.Cancel();
            try { await heartbeat.ConfigureAwait(false); } catch { /* nothing to salvage */ }

            if (result.Success && !failed)
            {
                // The install itself worked, but the installer told us it had no licence to
                // hand over, so the app on the home screen will not start. Reporting this as
                // a success is what made the failure invisible; it is surfaced as a licence
                // problem instead, which is what it is.
                if (licenseWarning is not null)
                    return InstallResult.NoLicense(licenseWarning);

                return InstallResult.Ok();
            }

            return InstallResult.Fail(errorLine ?? Truncate(result.CombinedOutput) ?? "Installation failed");
        }
        finally
        {
            deviceLock.Release();
            bundleLock.Release();

            // Remove the temporary copy / link (if we made one).
            if (stagedCopy is not null)
            {
                try { File.Delete(stagedCopy); } catch { /* best effort */ }
            }
        }
    }

    // ─────────────────────────── local preparation ───────────────────────────

    /// <summary>
    /// Returns a path ideviceinstaller can definitely open, plus the throwaway file to
    /// delete afterwards (null when the original path was used directly).
    ///
    /// ideviceinstaller takes the archive path as bytes and hands it to libzip, and the
    /// bundled Windows build does NOT interpret those bytes in the system code page.
    ///
    /// This was previously "safe if the path converts to the ANSI code page", on the theory
    /// that the tool would read it back the same way. A Russian Windows disproves that:
    /// CP1251 encodes Cyrillic exactly, so the check passed, the path was handed over
    /// unchanged, and the install died on a path that is perfectly legal on disk:
    ///
    ///     ERROR: zip_open: C:\Users\User\Desktop\iPa ...\MAX 26.17.3.ipa: 18
    ///
    /// (18 is ZIP_ER_INVAL. That the tool echoed the Cyrillic back as single high bytes is
    /// what shows it received the CP1251 form intact and still could not open it.)
    ///
    /// So the code page cannot be consulted to answer this: what a byte means depends on
    /// the tool's build, not on this machine's settings. Only pure ASCII, where every
    /// encoding agrees, is treated as safe. The reason that check was loosened in the first
    /// place was the cost of copying a 1 GB IPA before every install — but the hard link
    /// below already removes that cost, so being strict here is nearly free.
    /// </summary>
    private static async Task<(string path, string? staged)> PrepareLocalPathAsync(
        string ipaPath, long totalBytes, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (IsEncodingSafePath(ipaPath)) return (ipaPath, null);

        var stageRoot = FindEncodingSafeStageRoot(ipaPath);
        if (stageRoot is null)
        {
            // Nowhere ASCII to put it. Handing over the original path is what we would have
            // done anyway; say so plainly, because the install is about to fail on it.
            AppLog.Warn("No ASCII-safe folder available to stage the IPA; " +
                        "passing the original path, which the installer may refuse.");
            return (ipaPath, null);
        }

        try
        {
            Directory.CreateDirectory(stageRoot);
            var dest = Path.Combine(stageRoot, $"{Guid.NewGuid():N}.ipa");

            if (CreateHardLinkW(dest, ipaPath, IntPtr.Zero))
            {
                AppLog.Info($"IPA path is not ASCII; linked to {dest}");
                return (dest, dest);
            }

            // Different volume, or a filesystem without hard links: copy, and report it,
            // because a silent several-hundred-megabyte copy is exactly the kind of pause
            // that reads as a hang.
            AppLog.Info($"IPA path is not ASCII and cannot be linked; copying to {dest}");
            await CopyWithProgressAsync(ipaPath, dest, totalBytes, progress, ct).ConfigureAwait(false);
            return (dest, dest);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to stage IPA to an ASCII path, using original: {ex.Message}");
            return (ipaPath, null);
        }
    }

    /// <summary>Copies a file in chunks, reporting real percentage as it goes.</summary>
    private static async Task CopyWithProgressAsync(
        string source, string dest, long totalBytes,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        const int bufferSize = 1024 * 1024;
        var started = DateTimeOffset.UtcNow;

        await using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        await using var output = new FileStream(
            dest, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);

        var buffer = new byte[bufferSize];
        long copied = 0;
        var lastReport = DateTimeOffset.MinValue;

        while (true)
        {
            var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;

            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            copied += read;

            var now = DateTimeOffset.UtcNow;
            if (totalBytes > 0 && now - lastReport >= CopyTick)
            {
                lastReport = now;
                progress?.Report(new InstallProgress(
                    copied * 100.0 / totalBytes, "Preparing", totalBytes, now - started));
            }
        }
    }

    /// <summary>
    /// Picks a folder for the staged copy whose own path is ASCII — staging into
    /// "C:\Users\Вася\AppData\Local\Temp" would just relocate the problem.
    ///
    /// Ordered so the first candidate is normally on the system drive, the same volume as
    /// Desktop and Downloads where IPAs actually live, because a hard link only works
    /// within one volume; failing that we still try, and fall back to a copy.
    /// </summary>
    private static string? FindEncodingSafeStageRoot(string sourcePath)
    {
        var candidates = new List<string>(4);

        void Add(string? root, params string[] parts)
        {
            if (string.IsNullOrWhiteSpace(root)) return;
            try { candidates.Add(Path.Combine(new[] { root }.Concat(parts).ToArray())); }
            catch { /* an unusable root is simply not a candidate */ }
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "IPAStudio", "stage");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IPAStudio", "stage");
        // Same volume as the IPA, so the hard link can succeed even for a D: drive.
        Add(Path.GetPathRoot(Path.GetFullPath(sourcePath)), "IPAStudio", "stage");
        Add(Path.GetTempPath(), "IPAStudio-stage");

        foreach (var candidate in candidates)
            if (IsEncodingSafePath(candidate))
                return candidate;

        return null;
    }

    /// <summary>
    /// True when every byte of the path means the same thing to any tool that reads it,
    /// which in practice means pure ASCII. See PrepareLocalPathAsync for why the system
    /// code page deliberately plays no part in this decision.
    /// </summary>
    private static bool IsEncodingSafePath(string path)
    {
        foreach (var c in path)
            if (c > 127) return false;
        return true;
    }

    // DllImport rather than LibraryImport: the source-generated variant demands
    // AllowUnsafeBlocks for the whole project, which is a large permission to grant for
    // one tiny call.
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr securityAttributes);

    // ─────────────────────────── listing installed apps ───────────────────────────

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
                DeviceTransport.TargetArgs(udid, "-l"),
                closeStdin: true,
                quiet: true,
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

    /// <summary>
    /// Lists the user-installed apps on the device with their display names and versions.
    ///
    /// Asks for XML output rather than parsing the plain listing: the plain format is
    /// comma separated with quoted fields, which breaks on the many app names that
    /// contain a comma or a quote. The XML is a plist array of dictionaries.
    /// </summary>
    /// <summary>
    /// Fetches home-screen icons for the given bundle identifiers as PNG bytes.
    ///
    /// The icons come from SpringBoard over its own lockdown service, which is the only
    /// source for them: ideviceinstaller reports names and versions but no artwork, so the
    /// list previously had nothing to show and fell back to a letter tile for every row.
    ///
    /// One service session serves the whole list, because starting it costs a lockdown
    /// handshake and doing that per app would take longer than the listing itself. Bundles
    /// SpringBoard has no icon for are simply absent from the result; the caller keeps its
    /// letter tile for those. Errors are swallowed for the same reason: artwork is a nicety
    /// and must never take the app list down with it.
    /// </summary>
    public Task<Dictionary<string, byte[]>> GetAppIconsAsync(
        string udid, IReadOnlyList<string> bundleIds, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var icons = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (bundleIds.Count == 0) return icons;

            try
            {
                NativeDevice.EnsureLoaded();

                var sb = LibiMobileDevice.Instance.SpringBoardServices;

                if (NativeDevice.Open(udid, out var device) != iDeviceError.Success)
                    return icons;

                using (device)
                {
                    if (sb.sbservices_client_start_service(device, out var client, "IPAStudio")
                        != SpringBoardServicesError.Success)
                    {
                        AppLog.Info("icons: SpringBoard did not accept a connection");
                        return icons;
                    }

                    using (client)
                    {
                        foreach (var bundleId in bundleIds)
                        {
                            ct.ThrowIfCancellationRequested();

                            var data = IntPtr.Zero;
                            ulong size = 0;
                            if (sb.sbservices_get_icon_pngdata(client, bundleId, ref data, ref size)
                                    != SpringBoardServicesError.Success
                                || data == IntPtr.Zero || size == 0)
                                continue;

                            try
                            {
                                var bytes = new byte[size];
                                Marshal.Copy(data, bytes, 0, (int)size);
                                icons[bundleId] = bytes;
                            }
                            finally
                            {
                                // Allocated by the native library on every success, so it is
                                // freed here rather than at the end: on a full device this loop
                                // runs a few hundred times.
                                Marshal.FreeHGlobal(data);
                            }
                        }
                    }
                }

                AppLog.Info($"icons: {icons.Count} of {bundleIds.Count} apps returned artwork");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Info($"icons: could not read app icons ({ex.Message})");
            }

            return icons;
        }, ct);

    /// <summary>
    /// The device's home screen wallpaper as PNG bytes, or null when it cannot be read.
    ///
    /// This is the wallpaper, deliberately not a screenshot: a real screen capture needs the
    /// Developer Disk Image mounted, which is a large download per iOS version and fails
    /// outright on a locked phone. The wallpaper comes from the same lockdown service that
    /// already supplies app icons, so it costs one extra call and works on any paired device
    /// — and it is enough to tell two identical iPhones apart at a glance, which is the point.
    ///
    /// Returns null rather than throwing: an undecorated tile is a fine outcome here, and a
    /// failure to fetch decoration must never keep a device from appearing.
    /// </summary>
    public Task<byte[]?> GetHomeScreenWallpaperAsync(string udid, CancellationToken ct = default)
        => Task.Run<byte[]?>(() =>
        {
            try
            {
                NativeDevice.EnsureLoaded();

                var sb = LibiMobileDevice.Instance.SpringBoardServices;

                if (NativeDevice.Open(udid, out var device) != iDeviceError.Success)
                    return null;

                using (device)
                {
                    if (sb.sbservices_client_start_service(device, out var client, "IPAStudio")
                        != SpringBoardServicesError.Success)
                    {
                        AppLog.Info("wallpaper: SpringBoard did not accept a connection");
                        return null;
                    }

                    using (client)
                    {
                        var data = IntPtr.Zero;
                        ulong size = 0;

                        if (sb.sbservices_get_home_screen_wallpaper_pngdata(client, ref data, ref size)
                                != SpringBoardServicesError.Success
                            || data == IntPtr.Zero || size == 0)
                        {
                            AppLog.Info("wallpaper: the device returned no image");
                            return null;
                        }

                        try
                        {
                            var bytes = new byte[size];
                            Marshal.Copy(data, bytes, 0, (int)size);
                            AppLog.Info($"wallpaper: {bytes.Length} bytes for {udid}");
                            return bytes;
                        }
                        finally
                        {
                            // Allocated by the native library, freed on both paths out.
                            Marshal.FreeHGlobal(data);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Info($"wallpaper: could not read it ({ex.Message})");
                return null;
            }
        }, ct);

    public async Task<IReadOnlyList<InstalledApp>> GetInstalledAppsAsync(
        string udid, CancellationToken ct = default)
    {
        var result = await BrowseAsync(udid, ct).ConfigureAwait(false);
        var xml = ExtractPlist(result.StdOut);

        if (xml is null)
        {
            // Fall back to the plain listing rather than showing an empty device: names
            // are less exact there, but "no apps" would be plainly wrong.
            AppLog.Warn("ideviceinstaller gave no XML listing; falling back to the plain list");
            return ParsePlainListing(
                result.StdOut.Length > 0 ? result.StdOut : await PlainListAsync(udid, ct).ConfigureAwait(false));
        }

        List<InstalledApp> apps;
        try
        {
            apps = ParseListing(xml);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not parse the installed app list: {ex.Message}");
            return ParsePlainListing(result.StdOut).ToList();
        }

        // No second, attribute-filtered browse is attempted for the missing store ids.
        // ideviceinstaller has no option for requesting attributes: its "-a" is the archive
        // mode, so asking added "-a iTunesMetadata" to a listing command and the tool exited
        // with "A mode has already been supplied", printing its whole usage into our log on
        // every device that disclosed no ids - which is every modern one. It never once
        // returned an id. The catalog lookup by name is what recovers those apps instead.
        var withId = apps.Count(a => a.StoreItemId is > 0);
        AppLog.Info($"Device {udid}: {apps.Count} user apps, {withId} with a store id, " +
                    $"{apps.Count(a => a.StoreAccount is not null)} with a purchase account");

        // Without this line a device that discloses no ids is indistinguishable from a
        // parsing mistake on our side, and the download then fails much later with a
        // misleading "not on the App Store".
        if (withId == 0 && apps.Count > 0)
            AppLog.Warn("The device disclosed no store ids; downloads will resolve apps by bundle id");

        return apps
            .GroupBy(a => a.BundleId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// One installation_proxy browse, returning whatever the device reports by default.
    ///
    /// There is no attribute filter to pass: ideviceinstaller exposes no such option, and the
    /// letter that looks like one ("-a") selects the archive mode instead.
    /// </summary>
    private Task<ProcessResult> BrowseAsync(string udid, CancellationToken ct)
    {
        var args = DeviceTransport.TargetArgs(udid, "-l", "-o", "list_user", "-o", "xml");
        return _runner.RunAsync(_tools.IdeviceInstallerPath, args, closeStdin: true, ct: ct);
    }

    /// <summary>Start of the plist inside a tool's chatter, or null if there is none.</summary>
    private static string? ExtractPlist(string stdout)
    {
        var start = stdout.IndexOf("<?xml", StringComparison.Ordinal);
        if (start < 0) start = stdout.IndexOf("<plist", StringComparison.Ordinal);
        return start < 0 ? null : stdout[start..];
    }

    /// <summary>Apps described by an ideviceinstaller XML listing.</summary>
    private static List<InstalledApp> ParseListing(string xml)
    {
        var apps = new List<InstalledApp>();
        var doc = XDocument.Parse(xml);

        foreach (var dict in doc.Descendants("dict"))
        {
            // Only top-level app dictionaries: nested ones (entitlements, environment
            // variables) carry no bundle identifier of their own.
            var bundleId = PlistString(dict, "CFBundleIdentifier");
            if (string.IsNullOrEmpty(bundleId)) continue;

            // The store fields are read from the app's iTunes metadata blob first, since
            // that is where current iOS keeps them, and only then from plain plist keys as
            // older devices wrote them.
            var metadata = StoreMetadata(dict);

            apps.Add(new InstalledApp
            {
                BundleId = bundleId!,
                Name = PlistString(dict, "CFBundleDisplayName")
                       ?? PlistString(dict, "CFBundleName")
                       ?? bundleId!,
                Version = PlistString(dict, "CFBundleShortVersionString")
                          ?? PlistString(dict, "CFBundleVersion"),
                // Only apps that came from the App Store carry store metadata, and
                // that is exactly what makes an app re-downloadable.
                //
                // Searched through the whole app dictionary rather than its direct keys:
                // depending on the iOS version the same numbers arrive either at the top
                // level or nested inside the app's iTunes metadata, and reading only the
                // top level left modern devices reporting no store id at all - which then
                // forced a catalog lookup that fails for every delisted app.
                StoreItemId = BinaryPlist.FindLong(metadata,
                                  "itemId", "item-id", "storeItemIdentifier")
                              ?? PlistLongDeep(dict,
                                     "ITunesMetadataItemId", "StoreItemIdentifier",
                                     "itemId", "item-id", "storeItemIdentifier"),
                StoreAccount = BinaryPlist.FindString(metadata, "AppleID", "apple-id")
                               ?? PlistStringDeep(dict,
                                      "ITunesMetadataAppleID", "AppleID", "appleId",
                                      "com.apple.iTunesStore.downloadInfo.accountInfo.AppleID"),
            });
        }

        return apps;
    }

    /// <summary>
    /// The app's decoded iTunes metadata, or null when it reported none.
    ///
    /// The device hands this over as an opaque blob holding a nested property list, so it
    /// has to be decoded before the store id inside it can be read; treating it as plain
    /// plist elements finds nothing at all.
    /// </summary>
    private static object? StoreMetadata(XElement dict)
    {
        foreach (var value in DeepValues(dict, "iTunesMetadata"))
        {
            if (value.Name.LocalName != "data") continue;

            byte[] raw;
            try
            {
                raw = Convert.FromBase64String(value.Value);
            }
            catch (FormatException)
            {
                continue;
            }

            if (BinaryPlist.LooksBinary(raw))
            {
                var parsed = BinaryPlist.Parse(raw);
                if (parsed is not null) return parsed;
                continue;
            }

            // Some devices store the same metadata as a text plist. It is converted into the
            // same shape the binary reader produces so the fields are read one way only.
            try
            {
                var text = Encoding.UTF8.GetString(raw);
                var inner = ExtractPlist(text);
                if (inner is null) continue;

                var root = XDocument.Parse(inner).Root;
                var converted = root is null ? null : FromXmlPlist(root, 0);
                if (converted is not null) return converted;
            }
            catch (Exception)
            {
                // Not a plist we can read; the plain-key fallback still applies.
            }
        }

        return null;
    }

    /// <summary>
    /// A text plist turned into the dictionaries, lists and scalars that
    /// <see cref="BinaryPlist"/> yields, so both encodings are searched by the same code.
    /// </summary>
    private static object? FromXmlPlist(XElement node, int depth)
    {
        if (depth > 32) return null;

        switch (node.Name.LocalName)
        {
            case "plist":
                var first = node.Elements().FirstOrDefault();
                return first is null ? null : FromXmlPlist(first, depth + 1);

            case "dict":
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var key in node.Elements("key"))
                {
                    var value = key.ElementsAfterSelf().FirstOrDefault();
                    if (value is null) continue;
                    dict[key.Value.Trim()] = FromXmlPlist(value, depth + 1);
                }
                return dict;

            case "array":
                return node.Elements().Select(e => FromXmlPlist(e, depth + 1)).ToList();

            case "integer":
                return long.TryParse(node.Value.Trim(), out var number) ? number : null;

            case "true": return true;
            case "false": return false;

            // Everything else (string, real, date) is only ever read as text here.
            default:
                return node.Value.Trim();
        }
    }

    /// <summary>Plain (non-XML) listing, used when the XML mode is unsupported.</summary>
    private async Task<string> PlainListAsync(string udid, CancellationToken ct)
    {
        var result = await _runner.RunAsync(
            _tools.IdeviceInstallerPath,
            DeviceTransport.TargetArgs(udid, "-l"),
            closeStdin: true,
            ct: ct).ConfigureAwait(false);
        return result.StdOut;
    }

    /// <summary>Reads a &lt;key&gt;name&lt;/key&gt;&lt;string&gt;value&lt;/string&gt; pair.</summary>
    private static string? PlistString(XElement dict, string key)
    {
        var value = dict.Elements("key")
            .FirstOrDefault(k => k.Value == key)?
            .ElementsAfterSelf()
            .FirstOrDefault();
        return value?.Name.LocalName == "string" && !string.IsNullOrWhiteSpace(value.Value)
            ? value.Value.Trim()
            : null;
    }

    /// <summary>
    /// First of the given keys found anywhere inside the app dictionary, read as a positive
    /// number. Accepts a string value too: some iOS versions write the store id quoted.
    /// </summary>
    private static long? PlistLongDeep(XElement dict, params string[] keys)
    {
        foreach (var key in keys)
        {
            foreach (var value in DeepValues(dict, key))
            {
                var name = value.Name.LocalName;
                if (name is not ("integer" or "string")) continue;
                if (long.TryParse(value.Value.Trim(), out var parsed) && parsed > 0) return parsed;
            }
        }
        return null;
    }

    /// <summary>
    /// First of the given keys found anywhere inside the app dictionary, read as a
    /// non-empty string.
    /// </summary>
    private static string? PlistStringDeep(XElement dict, params string[] keys)
    {
        foreach (var key in keys)
        {
            foreach (var value in DeepValues(dict, key))
            {
                if (value.Name.LocalName != "string") continue;
                var text = value.Value.Trim();
                if (text.Length > 0) return text;
            }
        }
        return null;
    }

    /// <summary>
    /// Every value sitting after a &lt;key&gt; of that name, at any depth. Plain iteration
    /// rather than one lookup because the same key can appear both as an empty placeholder
    /// and as the real value, and only the filled one is of any use.
    /// </summary>
    private static IEnumerable<XElement> DeepValues(XElement dict, string key)
    {
        foreach (var element in dict.Descendants("key"))
        {
            if (!string.Equals(element.Value.Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;

            var value = element.ElementsAfterSelf().FirstOrDefault();
            if (value is not null) yield return value;
        }
    }

    /// <summary>Reads an integer plist value.</summary>
    private static long? PlistLong(XElement dict, string key)
    {
        var value = dict.Elements("key")
            .FirstOrDefault(k => k.Value == key)?
            .ElementsAfterSelf()
            .FirstOrDefault();
        return value?.Name.LocalName == "integer" && long.TryParse(value.Value.Trim(), out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Parses the plain «bundleid, "version", "name"» listing used by the fallback path.
    /// </summary>
    private static IReadOnlyList<InstalledApp> ParsePlainListing(string stdout)
    {
        var apps = new List<InstalledApp>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = line.IndexOf(',');
            if (idx <= 0) continue;

            var bundleId = line[..idx].Trim();
            if (!bundleId.Contains('.') || bundleId.Contains(' ')) continue;

            var quoted = QuotedRegex().Matches(line[(idx + 1)..])
                .Select(m => m.Groups[1].Value)
                .ToList();

            apps.Add(new InstalledApp
            {
                BundleId = bundleId,
                Name = quoted.Count > 1 ? quoted[1] : bundleId,
                Version = quoted.Count > 0 ? quoted[0] : null,
            });
        }

        return apps
            .GroupBy(a => a.BundleId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string? Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }
}
