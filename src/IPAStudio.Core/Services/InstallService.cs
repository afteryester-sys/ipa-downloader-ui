using System.Runtime.InteropServices;
using iMobileDevice;
using iMobileDevice.iDevice;
using iMobileDevice.SpringBoardServices;
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

        long totalBytes = 0;
        try { totalBytes = new FileInfo(ipaPath).Length; } catch { /* size is cosmetic */ }

        await _deviceLock.WaitAsync(ct).ConfigureAwait(false);

        string installPath = ipaPath;
        string? stagedCopy = null;
        try
        {
            // ---- Phase 1: make sure the tool can open the file at all ----------------
            (installPath, stagedCopy) = await PrepareLocalPathAsync(
                ipaPath, totalBytes, progress, ct).ConfigureAwait(false);

            var failed = false;
            string? errorLine = null;
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
                new[] { "-u", udid, "-i", installPath },
                onSegment: ParseSegment,
                ct: ct).ConfigureAwait(false);

            heartbeatCts.Cancel();
            try { await heartbeat.ConfigureAwait(false); } catch { /* nothing to salvage */ }

            if (result.Success && !failed)
                return InstallResult.Ok();

            return InstallResult.Fail(errorLine ?? Truncate(result.CombinedOutput) ?? "Installation failed");
        }
        finally
        {
            _deviceLock.Release();

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
    /// ideviceinstaller uses libzip, whose zip_open() goes through the narrow (ANSI) CRT
    /// and fails with ZIP_ER_OPEN when the path cannot be expressed in the system code
    /// page. That used to be approximated as "any non-ASCII path", which meant a Russian
    /// Windows — where a Cyrillic path is perfectly representable in CP1251 — copied the
    /// whole IPA before every single install. On a 1 GB app that copy alone was most of
    /// the wait. The code page is now actually consulted, and when a staging file IS
    /// needed it is a hard link (instant, no extra disk space), with a copy only as the
    /// fallback for a different volume.
    /// </summary>
    private static async Task<(string path, string? staged)> PrepareLocalPathAsync(
        string ipaPath, long totalBytes, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (CanOpenWithNarrowApi(ipaPath)) return (ipaPath, null);

        var stageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
            "IPAStudio", "stage");

        try
        {
            Directory.CreateDirectory(stageRoot);
            var dest = Path.Combine(stageRoot, $"{Guid.NewGuid():N}.ipa");

            if (CreateHardLinkW(dest, ipaPath, IntPtr.Zero))
            {
                AppLog.Info($"IPA path is not code-page safe; linked to {dest}");
                return (dest, dest);
            }

            // Different volume, or a filesystem without hard links: copy, and report it,
            // because a silent several-hundred-megabyte copy is exactly the kind of pause
            // that reads as a hang.
            AppLog.Info($"IPA path is not code-page safe and cannot be linked; copying to {dest}");
            await CopyWithProgressAsync(ipaPath, dest, totalBytes, progress, ct).ConfigureAwait(false);
            return (dest, dest);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to stage IPA to a code-page safe path, using original: {ex.Message}");
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
    /// True when the path survives conversion to the system code page, i.e. when the
    /// narrow file APIs that libzip uses can open it.
    /// </summary>
    private static bool CanOpenWithNarrowApi(string path)
    {
        var ascii = true;
        foreach (var c in path)
            if (c > 127) { ascii = false; break; }
        if (ascii) return true;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true;

        try
        {
            const uint cpAcp = 0;
            const uint utf8 = 65001;
            const uint noBestFit = 0x00000400;

            // A UTF-8 code page (Windows 10+ opt-in, and the default in some builds)
            // represents everything, and rejects the no-best-fit flag outright.
            if (GetACP() == utf8) return true;

            var needed = WideCharToMultiByte(cpAcp, noBestFit, path, path.Length, null, 0, IntPtr.Zero, out _);
            if (needed <= 0) return false;

            var buffer = new byte[needed];
            var written = WideCharToMultiByte(
                cpAcp, noBestFit, path, path.Length, buffer, buffer.Length, IntPtr.Zero, out var usedDefault);

            // usedDefault means at least one character was replaced by "?", so the narrow
            // path would point at a file that does not exist.
            return written > 0 && usedDefault == 0;
        }
        catch (Exception ex)
        {
            // Unable to tell: assume the worst and stage, which is always safe.
            AppLog.Debug(() => $"Code page check failed for '{path}': {ex.Message}");
            return false;
        }
    }

    // DllImport rather than LibraryImport: the source-generated variant demands
    // AllowUnsafeBlocks for the whole project, which is a large permission to grant for
    // three tiny calls.
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr securityAttributes);

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    [DllImport("kernel32.dll", EntryPoint = "WideCharToMultiByte", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WideCharToMultiByte(
        uint codePage, uint flags, string wideStr, int wideCount,
        byte[]? multiByteStr, int multiByteCount, IntPtr defaultChar, out int usedDefaultChar);

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
                new[] { "-u", udid, "-l" },
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
                NativeLibraries.Load();

                var idevice = LibiMobileDevice.Instance.iDevice;
                var sb = LibiMobileDevice.Instance.SpringBoardServices;

                if (idevice.idevice_new(out var device, udid) != iDeviceError.Success)
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

    public async Task<IReadOnlyList<InstalledApp>> GetInstalledAppsAsync(
        string udid, CancellationToken ct = default)
    {
        var result = await _runner.RunAsync(
            _tools.IdeviceInstallerPath,
            new[] { "-u", udid, "-l", "-o", "list_user", "-o", "xml" },
            closeStdin: true,
            ct: ct).ConfigureAwait(false);

        var xml = result.StdOut;
        var start = xml.IndexOf("<?xml", StringComparison.Ordinal);
        if (start < 0) start = xml.IndexOf("<plist", StringComparison.Ordinal);

        if (start < 0)
        {
            // Fall back to the plain listing rather than showing an empty device: names
            // are less exact there, but "no apps" would be plainly wrong.
            AppLog.Warn("ideviceinstaller gave no XML listing; falling back to the plain list");
            return ParsePlainListing(
                result.StdOut.Length > 0 ? result.StdOut : await PlainListAsync(udid, ct).ConfigureAwait(false));
        }

        var apps = new List<InstalledApp>();
        try
        {
            var doc = XDocument.Parse(xml[start..]);
            foreach (var dict in doc.Descendants("dict"))
            {
                // Only top-level app dictionaries: nested ones (entitlements, environment
                // variables) carry no bundle identifier of their own.
                var bundleId = PlistString(dict, "CFBundleIdentifier");
                if (string.IsNullOrEmpty(bundleId)) continue;

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
                    StoreItemId = PlistLongDeep(dict,
                                      "ITunesMetadataItemId", "StoreItemIdentifier",
                                      "itemId", "item-id", "storeItemIdentifier"),
                    StoreAccount = PlistStringDeep(dict,
                                       "ITunesMetadataAppleID", "AppleID", "appleId",
                                       "com.apple.iTunesStore.downloadInfo.accountInfo.AppleID"),
                });
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not parse the installed app list: {ex.Message}");
            return ParsePlainListing(result.StdOut);
        }

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

    /// <summary>Plain (non-XML) listing, used when the XML mode is unsupported.</summary>
    private async Task<string> PlainListAsync(string udid, CancellationToken ct)
    {
        var result = await _runner.RunAsync(
            _tools.IdeviceInstallerPath,
            new[] { "-u", udid, "-l" },
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
