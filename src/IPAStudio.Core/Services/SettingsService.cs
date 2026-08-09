using System.Text.Json;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services;

/// <summary>What to do with the bytes already on disk when a download breaks off.</summary>
public enum ResumeMode
{
    /// <summary>
    /// Classic behaviour: throw the partial archive away and start the app from zero on
    /// every attempt. Predictable, and always ends up with a freshly built file.
    /// </summary>
    RestartFromScratch = 0,

    /// <summary>
    /// Keep what has already been fetched. A partial archive survives between attempts
    /// and between runs of the program, and an archive that turns out to be complete and
    /// correctly licensed is reused instead of being fetched again.
    ///
    /// On the limits of this, so the setting is not mistaken for more than it is: the byte
    /// transfer is performed by ipatool, which cannot continue a broken one. The App Store
    /// issues a single-use download URL together with the FairPlay <c>.sinf</c> licence
    /// that has to be packed into the archive, and ipatool exposes neither, so a
    /// half-written file cannot be topped up mid-stream by us or by it. What this mode
    /// does buy is that finished work is never thrown away: closing the program during a
    /// batch, or failing at the install step after the archive was already written, no
    /// longer costs a full re-download.
    /// </summary>
    KeepPartialFiles = 1,
}

/// <summary>What the queue should do after downloading an IPA.</summary>
public enum InstallMode
{
    /// <summary>Download the IPA to disk and then install it on the device (default).</summary>
    DownloadAndInstall = 0,

    /// <summary>Download only; do not install. The IPA stays in the Apps folder.</summary>
    DownloadOnly = 1,

    /// <summary>Skip downloading; install a locally existing IPA file that is already cached.</summary>
    InstallExistingOnly = 2,
}

/// <summary>How a list on a page lets several items be picked out for a batch action.</summary>
public enum TileSelectionMode
{
    /// <summary>
    /// Pick items by clicking them, the way a file manager or a photo gallery does, with
    /// Ctrl to add one and Shift to add a run. The default: it is what the same lists do
    /// everywhere else on the machine, and it leaves the tile itself uncluttered.
    /// </summary>
    Click = 0,

    /// <summary>
    /// Pick items with a tick box drawn on each one. Slower per item, but it states plainly
    /// what is selected and cannot be undone by a stray click, which matters when the batch
    /// took a while to assemble. Kept as a choice rather than removed for that reason.
    /// </summary>
    Checkbox = 1,
}

/// <summary>Persisted user settings.</summary>
public sealed class AppSettings
{

    /// <summary>UI language: "ru" or "en".</summary>
    public string Language { get; set; } = "ru";

    /// <summary>Color theme: "dark" (default) or "light".</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>ipatool major version: 2 (default, no iCloud needed) or 3.</summary>
    public int IpatoolVersion { get; set; } = 2;

    /// <summary>Folder where IPA files are stored.</summary>
    public string? AppsFolder { get; set; }

    /// <summary>
    /// Number of parallel downloads (1-6). Default 3.
    ///
    /// Apple shapes each connection individually, so one stream usually cannot
    /// saturate the line. Several concurrent transfers raise total throughput for a
    /// queue; a single app is unaffected. Safe to raise because the authentication
    /// handshakes are serialized separately from the byte transfers.
    /// </summary>
    public int MaxParallelDownloads { get; set; } = 3;

    /// <summary>
    /// Multitasking mode. When enabled, each action becomes a separate operation that can
    /// be minimised to the background and returned to, and several operations run at once.
    ///
    /// Off by default: with it off the app keeps the single-queue path it has always used,
    /// so an existing install behaves exactly as before an update and switching the mode
    /// off is a real escape hatch rather than the new path capped at one.
    /// </summary>
    public bool MultitaskingEnabled { get; set; }

    /// <summary>
    /// How many apps install at once on one device (1-4). Default 2.
    ///
    /// Different devices always install in parallel; this only caps a single device. Above
    /// 2 rarely helps, because the limit is the USB link rather than the phone.
    /// </summary>
    public int MaxParallelInstallsPerDevice { get; set; } = 2;

    /// <summary>
    /// When true, the app checks on startup for local conditions that throttle
    /// downloads (Defender scanning the download folder, staging on a different
    /// volume, NTFS compression) and surfaces them. Diagnostics only; nothing is
    /// changed without explicit consent.
    /// </summary>
    public bool CheckThroughputIssues { get; set; } = true;

    /// <summary>
    /// Findings the user chose to dismiss, by <c>Kind</c>, so the same advice is not
    /// repeated on every launch.
    /// </summary>
    public List<string> DismissedThroughputFindings { get; set; } = new();

    /// <summary>
    /// Folders an elevated run confirmed as excluded from Defender. Remembered because
    /// Defender does not disclose its exclusion list to an unelevated process, so
    /// without this the app cannot tell a fix it already applied from one still needed.
    /// </summary>
    public List<string> VerifiedDefenderExclusions { get; set; } = new();

    /// <summary>Last Apple ID used to sign in; pre-filled on the login screen.</summary>
    public string? LastAppleId { get; set; }

    /// <summary>Determines what happens after a download: install, download-only, or install-only.</summary>
    public InstallMode InstallMode { get; set; } = InstallMode.DownloadAndInstall;

    /// <summary>
    /// What happens to a partially downloaded archive when a transfer is interrupted.
    /// Defaults to keeping it, which is the friendlier behaviour on an unreliable link and
    /// cannot produce a bad install: a kept archive is only reused after its size and
    /// FairPlay licence have been verified, and is re-downloaded otherwise.
    /// </summary>
    public ResumeMode ResumeMode { get; set; } = ResumeMode.KeepPartialFiles;

    /// <summary>
    /// Folder last chosen on the direct download screen, so grabbing several apps in a
    /// row only requires picking a destination once. Independent of
    /// <see cref="AppsFolder"/>, which stays the managed location for queue downloads.
    /// </summary>
    public string? LastDirectDownloadFolder { get; set; }

    /// <summary>
    /// Folder last chosen on the "on the device" screen. Kept apart from
    /// <see cref="LastDirectDownloadFolder"/> because saving copies of installed apps and
    /// grabbing a fresh IPA are different errands that usually target different folders.
    /// </summary>
    public string? LastOnDeviceFolder { get; set; }

    /// <summary>
    /// Whether the "on the device" app list is shown as tiles rather than rows. Remembered
    /// because it is a preference about how the user likes to look at their apps, and having to
    /// set it again on every visit would make the tile mode not worth switching to.
    /// </summary>
    public bool OnDeviceTileView { get; set; }

    /// <summary>
    /// Tile edge, in device-independent pixels, for that tile mode. Clamped on load rather than
    /// trusted, so a hand-edited settings file cannot produce a grid of invisible or absurd tiles.
    /// </summary>
    public double OnDeviceTileSize { get; set; } = 132;

    /// <summary>
    /// The same tile-or-rows preference for the .ipa catalog page ("install from file").
    /// Stored separately from the on-device one: the two lists show different things — files
    /// on disk versus apps on a phone — and wanting icons for one does not mean wanting to
    /// lose the file names the other mode shows.
    /// </summary>
    public bool CatalogTileView { get; set; }

    /// <summary>Tile edge for the catalog page, with the same clamp-on-load contract.</summary>
    public double CatalogTileSize { get; set; } = 132;

    /// <summary>
    /// Thumbnail tile edge on the photos page, with the same clamp-on-load contract. Kept
    /// apart from the app-tile sizes: a camera roll is scanned by eye, so the size that
    /// suits picking one shot out of hundreds has nothing to do with the size that suits
    /// reading app names.
    /// </summary>
    public double PhotoTileSize { get; set; } = 130;

    /// <summary>
    /// How items are picked on the photos page. Stored per page rather than once for the
    /// whole app because the pages are used differently: a camera roll is skimmed and
    /// clicked through, whereas a list of apps to download is assembled deliberately and
    /// benefits from tick boxes that a misplaced click cannot clear.
    /// </summary>
    public TileSelectionMode PhotosSelectionMode { get; set; } = TileSelectionMode.Click;

    /// <summary>How items are picked on the "on the device" page.</summary>
    public TileSelectionMode OnDeviceSelectionMode { get; set; } = TileSelectionMode.Click;

    /// <summary>How items are picked on the .ipa catalog page.</summary>
    public TileSelectionMode CatalogSelectionMode { get; set; } = TileSelectionMode.Click;

    /// <summary>
    /// App Store ids the signed-in Apple ID is known to own, learned from successful
    /// downloads. Persisted so the app picker can show ownership immediately on the
    /// next launch instead of paying an Apple round-trip per app.
    /// </summary>
    public List<long> OwnedAppIds { get; set; } = new();

    /// <summary>
    /// Apple ID the <see cref="OwnedAppIds"/> list belongs to. The cache is dropped
    /// when a different account signs in, so one account's licenses are never shown
    /// for another.
    /// </summary>
    public string? OwnedAppIdsAccount { get; set; }

    /// <summary>
    /// When true, routine background detail (device-poll tool invocations, thumbnail
    /// pipeline timings) is written to the log. Off by default: those lines repeat every
    /// few seconds and make the log unreadable when looking for a real problem.
    /// Turn on only when diagnosing an issue.
    /// </summary>
    public bool VerboseLogging { get; set; }

    /// <summary>
    /// When true, devices are also looked for on the local network, so an iPhone that is
    /// not plugged in can be used over Wi-Fi.
    ///
    /// Off by default, and deliberately a setting rather than always-on. Looking on the
    /// network costs an extra discovery query every few seconds, and a device reached over
    /// Wi-Fi is markedly slower and less reliable for the bulk transfers this app does
    /// (installing an IPA, reading the camera roll) than the same device on a cable. It is
    /// also easy to be surprised by: with this on, a phone in another room can appear and
    /// be acted on. So it stays something the user turns on knowingly.
    /// </summary>
    public bool WifiDeviceConnection { get; set; }
}

/// <summary>Loads and saves settings as JSON in the local app data folder.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ToolLocator _tools;
    private readonly object _ownedLock = new();

    public AppSettings Current { get; private set; } = new();

    /// <summary>
    /// Raised after settings are saved, so an open page can pick up a change instead of
    /// showing the old behaviour until it is next opened. Added for the selection mode:
    /// changing it and returning to a page that still selected the old way would read as
    /// the setting not working.
    /// </summary>
    public event EventHandler? Changed;

    public SettingsService(ToolLocator tools)
    {
        _tools = tools;
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_tools.SettingsFile))
            {
                var json = File.ReadAllText(_tools.SettingsFile);
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            Current = new AppSettings();
        }
        Apply();
    }

    public void Save()
    {
        _tools.EnsureFolders();
        File.WriteAllText(_tools.SettingsFile, JsonSerializer.Serialize(Current, JsonOptions));
        Apply();
    }

    /// <summary>
    /// Exposes the current <see cref="InstallMode"/> so callers (e.g. QueueService)
    /// can read it without depending on the full settings file.
    /// </summary>
    public InstallMode InstallMode => Current.InstallMode;

    /// <summary>
    /// Exposes the current <see cref="ResumeMode"/> for the download pipeline, which
    /// consults it before deleting or reusing a partial archive.
    /// </summary>
    public ResumeMode ResumeMode => Current.ResumeMode;

    /// <summary>
    /// Binds the license cache to an account. Called after a successful sign-in;
    /// clears the cache when the account changed.
    /// </summary>
    public void BindOwnedCacheToAccount(string? accountEmail)
    {
        lock (_ownedLock)
        {
            if (string.Equals(Current.OwnedAppIdsAccount, accountEmail, StringComparison.OrdinalIgnoreCase))
                return;

            Current.OwnedAppIdsAccount = accountEmail;
            Current.OwnedAppIds.Clear();
        }
        TrySave();
    }

    /// <summary>True when a previous successful download proved this Apple ID owns the app.</summary>
    public bool IsKnownOwned(long appStoreId)
    {
        if (appStoreId <= 0) return false;
        lock (_ownedLock) return Current.OwnedAppIds.Contains(appStoreId);
    }

    /// <summary>
    /// Records that the signed-in Apple ID owns the app. Called after a successful
    /// download or purchase, so subsequent sessions skip the ownership round-trip.
    /// </summary>
    public void MarkOwned(long appStoreId)
    {
        if (appStoreId <= 0) return;

        lock (_ownedLock)
        {
            if (Current.OwnedAppIds.Contains(appStoreId)) return;
            Current.OwnedAppIds.Add(appStoreId);
        }
        TrySave();
    }

    /// <summary>True when the user has hidden this throughput finding.</summary>
    public bool IsThroughputFindingDismissed(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return false;
        lock (_ownedLock)
            return Current.DismissedThroughputFindings.Contains(kind, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Hides a throughput finding permanently.</summary>
    public void DismissThroughputFinding(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return;

        lock (_ownedLock)
        {
            if (Current.DismissedThroughputFindings.Contains(kind, StringComparer.OrdinalIgnoreCase))
                return;
            Current.DismissedThroughputFindings.Add(kind);
        }
        TrySave();
    }

    /// <summary>Folders a previous elevated run confirmed as excluded from Defender.</summary>
    public IReadOnlyCollection<string> GetVerifiedDefenderExclusions()
    {
        lock (_ownedLock)
            return Current.VerifiedDefenderExclusions.ToList();
    }

    /// <summary>Records folders whose Defender exclusion was confirmed while elevated.</summary>
    public void RememberDefenderExclusions(IEnumerable<string> folders)
    {
        var added = false;

        lock (_ownedLock)
        {
            foreach (var folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                if (Current.VerifiedDefenderExclusions.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    continue;
                Current.VerifiedDefenderExclusions.Add(folder);
                added = true;
            }
        }

        if (added) TrySave();
    }

    /// <summary>
    /// Saves without letting a settings-file problem break a download that already
    /// succeeded — the cache is an optimisation, not critical state.
    /// </summary>
    private void TrySave()
    {
        try { Save(); } catch { /* non-fatal */ }
    }

    /// <summary>Pushes settings into dependent services.</summary>
    private void Apply()
    {
        _tools.IpatoolVersion = Current.IpatoolVersion;
        if (!string.IsNullOrWhiteSpace(Current.AppsFolder))
            _tools.AppsFolder = Current.AppsFolder;

        // Mirrored into DeviceTransport here, on both load and save, so device discovery
        // never has to read settings itself. Doing it in one place means the flag cannot
        // drift out of step with what was saved.
        DeviceTransport.WifiEnabled = Current.WifiDeviceConnection;

        // Last, so a handler sees the mirrored values above already in place.
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
