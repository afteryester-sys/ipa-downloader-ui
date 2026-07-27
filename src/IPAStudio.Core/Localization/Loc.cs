namespace IPAStudio.Core.Localization;

/// <summary>
/// Bridge that lets Core produce user-facing text in the interface language without
/// referencing WPF. On startup, and on every language switch, the App hands over a
/// snapshot of the active Strings.&lt;lang&gt;.xaml dictionary, so Core text comes from the
/// same resources the views bind to. A snapshot (rather than a live TryFindResource call)
/// because these keys are read on worker threads, and WPF resource dictionaries may only
/// be touched from the thread that owns them.
///
/// Why this exists: services used to build their status and error text inline, in a
/// single hard-coded language. The English UI therefore showed Russian progress lines,
/// and the Russian UI showed raw English tool output — both of which look like bugs.
///
/// <see cref="Fallbacks"/> keeps a neutral English default for every key Core asks for,
/// so a missing resource (or a unit test with no resolver installed) still yields a
/// readable sentence instead of a bare "L.Some.Key".
/// </summary>
public static class Loc
{
    private static Func<string, string?>? _resolver;

    /// <summary>Installs the resource lookup. Pass null to fall back to English.</summary>
    public static void SetResolver(Func<string, string?>? resolver) => _resolver = resolver;

    /// <summary>Localized text for <paramref name="key"/>.</summary>
    public static string Get(string key)
    {
        var resolver = _resolver;
        if (resolver is not null)
        {
            string? value = null;
            try { value = resolver(key); }
            catch { /* a broken resolver must never break a download */ }
            if (!string.IsNullOrEmpty(value)) return value!;
        }
        return Fallbacks.TryGetValue(key, out var fallback) ? fallback : key;
    }

    /// <summary>Localized text for <paramref name="key"/> with <see cref="string.Format(string, object?[])"/> arguments.</summary>
    public static string Format(string key, params object?[] args)
    {
        var format = Get(key);
        try { return string.Format(format, args); }
        catch (FormatException) { return format; }
    }

    /// <summary>English defaults for every key Core requests.</summary>
    private static readonly Dictionary<string, string> Fallbacks = new(StringComparer.Ordinal)
    {
        // Queue stage status lines
        ["L.Queue.Done"] = "Done",
        ["L.Queue.Failed"] = "Failed",
        ["L.Queue.Cancelled"] = "Cancelled",
        ["L.Queue.Status.Checking"] = "Checking local files…",
        ["L.Queue.Status.Connecting"] = "Connecting to the App Store…",
        ["L.Queue.Status.ConnectingElapsed"] = "Connecting to the App Store… {0} s",
        ["L.Queue.Status.Licensing"] = "Obtaining a license…",
        ["L.Queue.Status.Attempt"] = " · attempt {0}",
        ["L.Queue.Status.Finalizing"] = "Packaging and signing… ({0})",
        ["L.Queue.Status.Downloaded"] = "Downloaded {0}",
        ["L.Queue.Status.TotalUnknown"] = "total unknown",
        ["L.Queue.Status.WaitingDevice"] = "Waiting for the device…",
        ["L.Queue.Status.DownloadOnlyDone"] = "Downloaded (install skipped)",

        // Install progress words reported by ideviceinstaller
        ["L.Install.Status.Copying"] = "Copying",
        ["L.Install.Status.Installing"] = "Installing",
        ["L.Install.Status.Complete"] = "Complete",

        // Units
        ["L.Unit.B"] = "B",
        ["L.Unit.KB"] = "KB",
        ["L.Unit.MB"] = "MB",
        ["L.Unit.GB"] = "GB",
        ["L.Unit.PerSecond"] = "/s",
        ["L.Unit.Seconds"] = "s",
        ["L.Unit.Minutes"] = "min",
        ["L.Unit.Hours"] = "h",

        // Generic errors
        ["L.Error.Unknown"] = "Something went wrong. See the log for details.",
        ["L.Error.Network"] = "No connection to Apple. Check your internet connection and try again.",
        ["L.Error.SessionExpired"] = "The Apple ID session is no longer valid. Please sign in again.",
        ["L.Error.DownloadFailed"] = "The download could not be completed.",
        ["L.Error.LicenseFailed"] = "Could not obtain a license for this app on the current Apple ID.",
        ["L.Error.NotInStore"] = "This app is not available in the App Store (it may be delisted or region-limited).",
        ["L.Error.NotPurchased"] = "The signed-in Apple ID does not own this app, and the license could not be obtained automatically.",
        ["L.Error.StoreUnavailable"] = "The App Store did not respond. Please try again in a moment.",
        ["L.Error.ConnectionStalled"] = "The connection stalled; retrying…",
        ["L.Error.FolderUnusable"] = "Could not use the folder “{0}”: {1}",
        ["L.Error.FileExists"] = "Skipped: the file “{0}” already exists.",
        ["L.Error.IpaNotFound"] = "IPA file not found: {0}",
        ["L.Error.ToolFailure"] = "The helper utility could not be started. Reinstall the utilities from Settings.",

        // Install failures (translated ideviceinstaller output)
        ["L.Install.Error.Verification"] = "App verification failed: the IPA is damaged or its signature is invalid.",
        ["L.Install.Error.CertRevoked"] = "The signing certificate has been revoked. Use a different IPA.",
        ["L.Install.Error.Incompatible"] = "This IPA is not compatible with the iOS version on the device.",
        ["L.Install.Error.AlreadyInstalled"] = "This app is already installed on the device.",
        ["L.Install.Error.BundleIdInUse"] = "The bundle ID is already used by another app on the device.",
        ["L.Install.Error.DeviceDisconnected"] = "The device disconnected during installation. Reconnect it and retry.",
        ["L.Install.Error.InstallDaemon"] = "The install service on the device is not responding. Restart the device.",
        ["L.Install.Error.MissingEntitlement"] = "The IPA requires entitlements that need a paid Apple Developer account.",
        ["L.Install.Error.NotPurchased"] = "This app was not purchased with the signed-in Apple ID. Try installing the IPA directly via “Install IPA from file”.",
        ["L.Install.Error.Authenticate"] = "Authentication failed. Unlock the device and check the connection.",
        ["L.Install.Error.Unknown"] = "The app could not be installed. See the log for details.",

        // Sign-in failures
        ["L.Login.Error.BadCredentials"] = "Incorrect Apple ID or password.",
        ["L.Login.Error.WrongCode"] = "That verification code was not accepted. Please try again.",
        ["L.Login.Error.Cancelled"] = "Sign-in was cancelled.",
        ["L.Login.Error.Network"] = "Could not reach Apple. Check your internet connection.",
        ["L.Login.Error.RateLimited"] = "Too many attempts. Wait a few minutes and try again.",
        ["L.Login.Error.AccountLocked"] = "This Apple ID is locked or needs attention at appleid.apple.com.",
        ["L.Login.Error.SessionExpired"] = "The saved session is no longer valid. Please sign in again.",
        ["L.Login.Error.ToolFailure"] = "ipatool could not be started. Reinstall the utilities from Settings.",
        ["L.Login.Error.Unknown"] = "Sign-in failed. See the log for details.",

        // App lookup by Bundle ID / App Store ID / store link
        ["L.Direct.NeedQuery"] = "Enter a Bundle ID, an App Store ID, or a link to the app.",
        ["L.Direct.NotFound"] = "No app found for that Bundle ID, App Store ID or link. Check the spelling.",
        ["L.Direct.LookupFailed"] = "Could not look the app up in the App Store. Check your connection and try again.",

        // Throughput advisor
        ["L.Tuning.Defender.Title"] = "Defender is scanning the download folder",
        ["L.Tuning.Defender.Detail"] = "Every block written into the IPA is checked by the antivirus, and the finished archive is scanned again in full. On multi-gigabyte files this is usually the largest loss of speed. Adding the folder to the exclusion list requires administrator rights.",
        ["L.Tuning.CrossVolume.Title"] = "Temporary folder is on a different drive",
        ["L.Tuning.CrossVolume.Detail"] = "Staging is on {0} while the destination folder is on {1}. The final move becomes a full copy of the archive instead of an instant rename.",
        ["L.Tuning.Compressed.Title"] = "The download folder is NTFS-compressed",
        ["L.Tuning.Compressed.Detail"] = "An IPA is already compressed, so NTFS compression saves no space but turns sequential writes into read-modify-write cycles.",
        ["L.Tuning.LowSpace.Title"] = "Low disk space",
        ["L.Tuning.LowSpace.Detail"] = "{0} GB free. A download needs room for the temporary file and the final archive at the same time.",

        // Battery capacity readout
        ["L.Battery.Error.ToolMissing"] = "the diagnostics tool is not installed",
        ["L.Battery.Error.Timeout"] = "timed out",
        ["L.Battery.Error.NoCapacity"] = "the device does not report capacity",
        ["L.Battery.Error.NoResponse"] = "no response from the device",
        ["L.Battery.Error.ReadFailed"] = "read error",
    };
}
