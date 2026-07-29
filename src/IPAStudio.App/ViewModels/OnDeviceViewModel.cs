using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;
using Microsoft.Win32;

namespace IPAStudio.App.ViewModels;

/// <summary>
/// One app installed on the device, plus its own download state.
/// </summary>
public sealed partial class InstalledAppViewModel : ObservableObject
{
    public InstalledApp App { get; }

    /// <summary>Apple ID currently signed in, or null when nobody is.</summary>
    private readonly string? _account;

    /// <summary>
    /// False when the device listing carried no store metadata at all (the plain-text
    /// fallback in <c>InstallService</c>). In that case "not from the App Store" cannot be
    /// concluded from a missing store id, so store origin is treated as unknown.
    /// </summary>
    private readonly bool _storeMetadataAvailable;

    public InstalledAppViewModel(InstalledApp app, string? account, bool storeMetadataAvailable)
    {
        App = app;
        _account = account;
        _storeMetadataAvailable = storeMetadataAvailable;
    }

    public string Name => App.Name;
    public string BundleId => App.BundleId;

    /// <summary>
    /// Home-screen icon, once SpringBoard has handed it over. Null until then, and for apps
    /// it has no artwork for, which is what keeps the letter tile as the fallback.
    /// </summary>
    [ObservableProperty]
    private ImageSource? _icon;
    public string? Version => App.Version;

    /// <summary>"1.2.3 · com.example.app" — one muted line under the name.</summary>
    public string Details => string.IsNullOrEmpty(Version)
        ? BundleId
        : $"{Version} · {BundleId}";

    /// <summary>Apple ID the device says the app was bought with, when it says at all.</summary>
    public string? StoreAccount => App.StoreAccount;

    /// <summary>
    /// Whether the signed-in Apple ID matches the one the app was bought with.
    /// Null means the device did not disclose an account — genuinely unknown, which is
    /// deliberately kept distinct from "different".
    /// </summary>
    public bool? AccountMatches =>
        _account is null || App.StoreAccount is null
            ? null
            : string.Equals(App.StoreAccount.Trim(), _account.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The download button is offered when the Apple ID matches, and also when the device
    /// keeps the account to itself (older iOS versions never report it) — refusing in that
    /// case would hide the button for apps the user does own. A mismatch is the only case
    /// where Apple is certain to refuse, so that is the only case that hides it.
    /// </summary>
    public bool CanDownload =>
        (App.IsFromStore || !_storeMetadataAvailable)
        && _account is not null
        && AccountMatches is not false;

    /// <summary>Reason the button is absent, shown in place of it. Null when it is shown.</summary>
    public string? BlockedReason =>
        CanDownload ? null
        : _account is null ? Loc.Get("L.OnDevice.NeedLogin")
        : !App.IsFromStore && _storeMetadataAvailable ? Loc.Get("L.OnDevice.NotFromStore")
        : Loc.Format("L.OnDevice.OtherAccount", App.StoreAccount ?? "");

    /// <summary>True when the account is unverifiable, so the UI can say so plainly.</summary>
    public bool IsAccountUnverified => CanDownload && AccountMatches is null;

    /// <summary>
    /// Ticked for a batch download.
    ///
    /// Only rows that can actually be downloaded show the tick, so a selection can never
    /// hold an app the download would refuse anyway.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string? _errorText;

    /// <summary>Path of the finished file, enabling "show in folder".</summary>
    [ObservableProperty]
    private string? _savedPath;

    /// <summary>
    /// Cancellation for this row only. Kept per app rather than per page so cancelling one
    /// download cannot abort another that happens to be running beside it.
    /// </summary>
    public CancellationTokenSource? Cancellation { get; set; }

    public bool IsProgressIndeterminate => IsDownloading && Progress <= 0;

    partial void OnIsDownloadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        OnPropertyChanged(nameof(IsIdle));
    }

    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(IsProgressIndeterminate));

    /// <summary>True while nothing is happening, so the row can show the button.</summary>
    public bool IsIdle => !IsDownloading;
}

/// <summary>
/// "On the device": lists the apps actually installed on the connected device and lets an
/// app owned by the signed-in Apple ID be saved as an IPA.
///
/// Deliberately separate from the app picker: the picker shows the catalog (what could be
/// installed), this shows the device (what is installed), and merging the two would make
/// both lists ambiguous.
/// </summary>
public sealed partial class OnDeviceViewModel : ObservableObject, IPageAware
{
    private readonly InstallService _install;
    private readonly DownloadService _download;
    private readonly CatalogService _catalog;
    private readonly AuthService _auth;
    private readonly SettingsService _settings;

    private INavigator? _navigator;

    public Device? TargetDevice { get; private set; }

    public ObservableCollection<InstalledAppViewModel> Apps { get; } = new();

    public OnDeviceViewModel(
        InstallService install,
        DownloadService download,
        CatalogService catalog,
        AuthService auth,
        SettingsService settings)
    {
        _install = install;
        _download = download;
        _catalog = catalog;
        _auth = auth;
        _settings = settings;

        DestinationFolder = settings.Current.LastOnDeviceFolder
                            ?? settings.Current.LastDirectDownloadFolder
                            ?? "";
    }

    [ObservableProperty]
    private string _deviceName = "";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private string _destinationFolder = "";

    [ObservableProperty]
    private string _searchText = "";

    /// <summary>Apps left after the search filter; the list binds to this.</summary>
    public ObservableCollection<InstalledAppViewModel> VisibleApps { get; } = new();

    /// <summary>How many rows are ticked, shown next to the batch button.</summary>
    public int SelectedCount => Apps.Count(a => a.IsSelected);

    public bool HasSelection => SelectedCount > 0;

    /// <summary>
    /// True when every downloadable row is ticked, so the button can offer to clear the
    /// selection instead of re-ticking what is already ticked.
    /// </summary>
    public bool AreAllSelected =>
        VisibleApps.Any(a => a.CanDownload) && VisibleApps.Where(a => a.CanDownload).All(a => a.IsSelected);

    /// <summary>"Download selected (3)" — the count has to be in the text, not beside it.</summary>
    public string DownloadSelectedLabel => Loc.Format("L.OnDevice.DownloadSelected", SelectedCount);

    /// <summary>The tick-all button flips its own wording once everything is ticked.</summary>
    public string SelectAllLabel =>
        AreAllSelected ? Loc.Get("L.OnDevice.ClearSelection") : Loc.Get("L.OnDevice.SelectAll");

    /// <summary>True while the batch is working through the ticked rows.</summary>
    [ObservableProperty]
    private bool _isBatchRunning;

    /// <summary>"3 of 7" while a batch runs; null when none is.</summary>
    [ObservableProperty]
    private string? _batchStatus;

    public bool IsSignedIn => _auth.IsAuthenticated;
    public string? AccountEmail => _auth.CurrentAccount?.Email;

    /// <summary>True when the device is connected but reported no user apps at all.</summary>
    public bool IsEmpty => !IsLoading && Apps.Count == 0 && ErrorText is null;

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(IsEmpty));

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public void SetDevice(Device device)
    {
        TargetDevice = device;
        DeviceName = device.Name;
    }

    public void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(AccountEmail));
        _ = LoadAsync();
    }

    // ─────────────────────────── loading ───────────────────────────

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (TargetDevice is null) return;

        IsLoading = true;
        ErrorText = null;

        // Detach first: a row left subscribed would keep reporting ticks into a list it is
        // no longer part of, and keep this page alive through the handler.
        foreach (var stale in Apps) stale.PropertyChanged -= OnRowPropertyChanged;

        Apps.Clear();
        VisibleApps.Clear();
        RefreshSelectionState();

        try
        {
            var apps = await _install.GetInstalledAppsAsync(TargetDevice.Udid).ConfigureAwait(true);
            var account = _auth.CurrentAccount?.Email;

            // If not a single app carries store metadata, the listing came from the
            // plain-text fallback. Judging store origin from it would mislabel every app
            // as sideloaded and hide every button, so origin is treated as unknown.
            var metadataAvailable = apps.Any(a => a.StoreItemId is not null || a.StoreAccount is not null);

            foreach (var app in apps)
            {
                var row = new InstalledAppViewModel(app, account, metadataAvailable);

                // The batch button counts ticks, so it has to hear about every one.
                row.PropertyChanged += OnRowPropertyChanged;
                Apps.Add(row);
            }

            ApplyFilter();
            AppLog.Info($"On-device list: {apps.Count} apps on {TargetDevice.Name}");

            // After the list is on screen, not before: the icons need a separate SpringBoard
            // session and a few hundred round-trips, and holding the list back for artwork
            // would make the page feel slower than it did without it.
            await LoadIconsAsync(TargetDevice.Udid).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { /* left the page */ }
        catch (Exception ex)
        {
            // Say the request failed. An empty list here would read as "no apps installed",
            // which is never true of a real device.
            ErrorText = Loc.Get("L.OnDevice.LoadFailed");
            AppLog.Warn($"Could not list installed apps: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>Recomputes what the batch controls show. Called whenever a tick changes.</summary>
    private void RefreshSelectionState()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(AreAllSelected));
        OnPropertyChanged(nameof(DownloadSelectedLabel));
        OnPropertyChanged(nameof(SelectAllLabel));
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstalledAppViewModel.IsSelected)) RefreshSelectionState();
    }

    /// <summary>
    /// Ticks every downloadable row on screen, or clears them when they are all ticked.
    ///
    /// Deliberately limited to the filtered rows: after a search, "select all" that also
    /// caught the hidden ones would queue downloads the user cannot see.
    /// </summary>
    [RelayCommand]
    private void ToggleSelectAll()
    {
        var select = !AreAllSelected;
        foreach (var app in VisibleApps)
            if (app.CanDownload) app.IsSelected = select;

        RefreshSelectionState();
    }

    /// <summary>
    /// Downloads the ticked apps one after another.
    ///
    /// Sequential on purpose: ipatool drives one App Store session, and parallel downloads
    /// on the same account get throttled or refused outright, which would look like random
    /// failures across the batch.
    /// </summary>
    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        if (IsBatchRunning) return;

        var queue = Apps.Where(a => a.IsSelected && a.CanDownload && !a.IsDownloading).ToList();
        if (queue.Count == 0) return;

        if (!_auth.IsAuthenticated)
        {
            ErrorText = Loc.Get("L.OnDevice.NeedLogin");
            return;
        }

        // Asked once for the whole batch rather than per app: prompting between downloads
        // would interrupt a run the user has already walked away from.
        if (string.IsNullOrWhiteSpace(DestinationFolder))
        {
            BrowseFolder();
            if (string.IsNullOrWhiteSpace(DestinationFolder)) return;
        }

        IsBatchRunning = true;
        try
        {
            var done = 0;
            foreach (var app in queue)
            {
                BatchStatus = Loc.Format("L.OnDevice.BatchProgress", done + 1, queue.Count);

                await DownloadAsync(app).ConfigureAwait(true);

                // Ticks are cleared only for what succeeded, so a second click retries the
                // failures instead of downloading everything again.
                if (app.ErrorText is null) app.IsSelected = false;

                done++;

                // A cancelled row means the user pressed Cancel: stopping the batch there is
                // the only reading of that click that does not fight the user.
                if (app.SavedPath is null && app.ErrorText is null) break;
            }

            var failed = queue.Count(a => a.ErrorText is not null);
            BatchStatus = failed == 0
                ? Loc.Format("L.OnDevice.BatchDone", queue.Count - failed)
                : Loc.Format("L.OnDevice.BatchDoneWithErrors", queue.Count - failed, failed);
        }
        finally
        {
            IsBatchRunning = false;
            RefreshSelectionState();
        }
    }

    private void ApplyFilter()
    {
        VisibleApps.Clear();
        var needle = SearchText.Trim();

        foreach (var app in Apps)
        {
            if (needle.Length == 0 ||
                app.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase) ||
                app.BundleId.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                VisibleApps.Add(app);
            }
        }

        // "All" is defined over the visible rows, so it changes with the filter.
        OnPropertyChanged(nameof(AreAllSelected));
        OnPropertyChanged(nameof(SelectAllLabel));
    }

    // ─────────────────────────── commands ───────────────────────────

    [RelayCommand]
    private void GoBack()
    {
        // Leaving the page hides the only progress and cancel UI, so anything still running
        // would be invisible and unstoppable.
        CancelAll();
        _navigator?.GoBack();
    }

    [RelayCommand]
    private void SignIn() => _navigator?.GoTo(Page.Login);

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc.Get("L.Dialog.PickFolderTitle"),
            InitialDirectory = Directory.Exists(DestinationFolder) ? DestinationFolder : "",
        };
        if (dialog.ShowDialog() != true) return;

        DestinationFolder = dialog.FolderName;
        _settings.Current.LastOnDeviceFolder = DestinationFolder;
        _settings.Save();
    }

    [RelayCommand]
    private void OpenSavedFolder(InstalledAppViewModel? item)
    {
        if (item?.SavedPath is null) return;
        try
        {
            if (File.Exists(item.SavedPath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.SavedPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not reveal {item.SavedPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Downloads one installed app as an IPA into the chosen folder, named
    /// "App name - apple.id@example.com.ipa".
    ///
    /// The transfer itself still lands on an ASCII path inside the managed folder, because
    /// ipatool's native pieces (Go + nlohmann/json + libzip) mangle non-ASCII paths; the
    /// file is renamed to the requested name only once it is complete. Naming it up front
    /// would break the download for every non-Latin app name.
    /// </summary>
    [RelayCommand]
    private async Task DownloadAsync(InstalledAppViewModel? item)
    {
        if (item is null || item.IsDownloading) return;

        if (!_auth.IsAuthenticated)
        {
            item.ErrorText = Loc.Get("L.OnDevice.NeedLogin");
            return;
        }

        if (string.IsNullOrWhiteSpace(DestinationFolder))
        {
            BrowseFolder();
            if (string.IsNullOrWhiteSpace(DestinationFolder)) return;
        }

        item.ErrorText = null;
        item.SavedPath = null;
        item.IsDownloading = true;
        item.Progress = 0;
        item.StatusText = Loc.Get("L.Queue.Status.Connecting");

        var cts = new CancellationTokenSource();
        item.Cancellation = cts;

        try
        {
            // Every download on this screen goes out to Apple, even though the app is sitting
            // right there on the device. That looks like a missed shortcut and keeps getting
            // raised as one, so, having checked: there is no way to copy it off the device.
            //
            //  - The app bundle lives under /var/containers/Bundle/Application, which no
            //    host-side service exposes. AFC is limited to /var/mobile/Media, and
            //    house_arrest hands back a chosen app's Documents/Library container, never
            //    the .app itself.
            //  - instproxy's archive command did exactly this once, and the bundled
            //    ideviceinstaller still carries the verbs (archive / list-archives / restore
            //    are present in the binary). Apple dropped the underlying support back in
            //    iOS 7, so on anything current it answers UnknownCommand.
            //  - Even given the bundle, App Store binaries are FairPlay encrypted; getting a
            //    usable one means dumping from memory on a jailbroken device.
            //
            // So the store really is the only source, and an app Apple no longer serves
            // cannot be fetched at all - which is what the verdict below has to say.
            var entry = await ResolveEntryAsync(item.App, cts.Token).ConfigureAwait(true);
            if (entry is null)
            {
                item.ErrorText = Loc.Get("L.OnDevice.NotInStore");
                return;
            }

            var progress = new Progress<DownloadProgress>(p =>
            {
                item.Progress = p.Percent;
                item.StatusText = p.TotalBytes > 0
                    ? $"{p.Percent:0.0}% · {FormatBytes(p.DownloadedBytes)} / {FormatBytes(p.TotalBytes)}"
                    : Loc.Format("L.Queue.Status.Downloaded", FormatBytes(p.DownloadedBytes));
            });

            var result = await _download.DownloadAsync(
                entry,
                autoPurchase: false,
                progress,
                destinationFolder: null,
                ct: cts.Token).ConfigureAwait(true);

            if (!result.Success || result.IpaPath is null)
            {
                item.StatusText = null;
                item.ErrorText = result.SessionExpired
                    ? Loc.Get("L.OnDevice.NeedLogin")
                    : result.LicenseRequired
                        ? Loc.Get("L.OnDevice.NotOwned")
                        : result.Error ?? Loc.Get("L.Error.DownloadFailed");

                if (!string.IsNullOrWhiteSpace(result.Detail))
                    AppLog.Warn($"On-device download failed: {result.Detail}");
                return;
            }

            var finalPath = MoveToRequestedName(result.IpaPath, item.Name, AccountEmail);
            item.SavedPath = finalPath;
            item.Progress = 100;
            item.StatusText = Path.GetFileName(finalPath);

            // Ownership is keyed by store id, so a bundle-id download has nothing to record.
            if (entry.AppStoreId > 0) _settings.MarkOwned(entry.AppStoreId);
            AppLog.Info($"On-device download OK: {finalPath}");
        }
        catch (OperationCanceledException)
        {
            item.StatusText = null;
            item.Progress = 0;
        }
        catch (Exception ex)
        {
            item.StatusText = null;
            item.ErrorText = Loc.Get("L.Error.Unknown");
            AppLog.Error("On-device download threw.", ex);
        }
        finally
        {
            item.IsDownloading = false;
            item.Cancellation = null;
            cts.Dispose();
        }
    }

    [RelayCommand]
    private void Cancel(InstalledAppViewModel? item)
    {
        try
        {
            item?.Cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The download finished between the click and the cancel: nothing left to stop.
        }
    }

    /// <summary>Stops every transfer in flight, used when the page is left.</summary>
    private void CancelAll()
    {
        foreach (var app in Apps)
            Cancel(app);
    }

    // ─────────────────────────── helpers ───────────────────────────

    /// <summary>
    /// Turns an installed app into a catalog entry the downloader can use.
    ///
    /// Prefers the store id the device itself reported: it identifies the exact app, while
    /// a bundle-id lookup can come back empty for apps that were renamed or pulled from
    /// the store in the user's region.
    /// </summary>
    /// <summary>
    /// Fills in the home-screen icons for the rows already on screen.
    ///
    /// Decoding happens here rather than in the service because a BitmapImage belongs to the
    /// UI layer; each one is frozen so the list can scroll without re-decoding it.
    /// </summary>
    private async Task LoadIconsAsync(string udid)
    {
        var rows = Apps.ToList();
        if (rows.Count == 0) return;

        var icons = await _install
            .GetAppIconsAsync(udid, rows.Select(r => r.BundleId).ToList())
            .ConfigureAwait(true);

        foreach (var row in rows)
        {
            if (!icons.TryGetValue(row.BundleId, out var png)) continue;

            try
            {
                var image = new BitmapImage();
                using (var stream = new MemoryStream(png))
                {
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    // Decoded at tile size: the artwork is up to 180 px square and the row
                    // shows it at 40, so full-size bitmaps would be wasted memory per app.
                    image.DecodePixelWidth = 80;
                    image.StreamSource = stream;
                    image.EndInit();
                }
                image.Freeze();
                row.Icon = image;
            }
            catch
            {
                // A single unreadable icon leaves that row on its letter tile.
            }
        }
    }

    private async Task<AppEntry?> ResolveEntryAsync(InstalledApp app, CancellationToken ct)
    {
        if (app.StoreItemId is > 0)
        {
            var byId = await _catalog.LookupByAppStoreIdAsync(app.StoreItemId.Value, ct).ConfigureAwait(true);
            if (byId.Count > 0) return byId[0];

            // Delisted in this region: the id is still valid for a download that only needs
            // the numeric id, so fall back to a minimal entry rather than giving up.
            return new AppEntry
            {
                Name = app.Name,
                AppStoreId = app.StoreItemId.Value,
                BundleId = app.BundleId,
                LatestVersion = app.Version,
            };
        }

        var byBundle = await _catalog.SearchByBundleIdAsync(app.BundleId, ct).ConfigureAwait(true);
        if (byBundle.Count > 0) return byBundle[0];

        // Nothing in any storefront and no id from the device. The App Store can still hand
        // over an app the account owns when asked by bundle identifier, so the attempt is
        // made instead of refusing on the strength of a catalog that is merely incomplete:
        // the lookup API hides apps pulled from sale, apps restricted by region and apps
        // that were never listed publicly, all of which stay downloadable for their owner.
        AppLog.Info($"On-device: {app.BundleId} is not in the catalog; trying the bundle id directly");
        return new AppEntry
        {
            Name = app.Name,
            AppStoreId = 0,
            BundleId = app.BundleId,
            LatestVersion = app.Version,
        };
    }

    /// <summary>
    /// Renames the finished IPA to "App name - account.ipa" in the chosen folder.
    ///
    /// On failure the original file is kept and its path returned: a rename problem must
    /// not lose a download that already succeeded.
    /// </summary>
    private string MoveToRequestedName(string downloadedPath, string appName, string? account)
    {
        try
        {
            var baseName = string.IsNullOrWhiteSpace(account)
                ? appName
                : $"{appName} - {account}";

            // Created before the uniqueness check so that check runs against the real folder.
            Directory.CreateDirectory(DestinationFolder);

            var target = Path.Combine(DestinationFolder, SanitizeFileName(baseName) + ".ipa");
            target = MakeUnique(target);

            // Move, not copy: File.Move across volumes is handled by the runtime, and a
            // copy would leave a duplicate of a multi-gigabyte file behind.
            File.Move(downloadedPath, target);
            return target;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not rename to the requested name, keeping {downloadedPath}: {ex.Message}");
            return downloadedPath;
        }
    }

    /// <summary>
    /// Strips only what a filename cannot contain, keeping letters of any alphabet and the
    /// "@" of the Apple ID, so the name stays the readable one the user asked for.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name
            .Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)
            .ToArray();

        var cleaned = new string(chars).Trim();

        // Windows also rejects a trailing dot or space.
        cleaned = cleaned.TrimEnd('.', ' ');

        // Leave room for the extension, the folder and the " (2)" suffix.
        if (cleaned.Length > 150) cleaned = cleaned[..150].TrimEnd('.', ' ');

        return cleaned.Length == 0 ? "app" : cleaned;
    }

    private static string MakeUnique(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return $"0 {Loc.Get("L.Unit.B")}";
        string[] units = { Loc.Get("L.Unit.B"), Loc.Get("L.Unit.KB"), Loc.Get("L.Unit.MB"), Loc.Get("L.Unit.GB") };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}
