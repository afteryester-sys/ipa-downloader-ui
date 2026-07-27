using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
        Apps.Clear();
        VisibleApps.Clear();

        try
        {
            var apps = await _install.GetInstalledAppsAsync(TargetDevice.Udid).ConfigureAwait(true);
            var account = _auth.CurrentAccount?.Email;

            // If not a single app carries store metadata, the listing came from the
            // plain-text fallback. Judging store origin from it would mislabel every app
            // as sideloaded and hide every button, so origin is treated as unknown.
            var metadataAvailable = apps.Any(a => a.StoreItemId is not null || a.StoreAccount is not null);

            foreach (var app in apps)
                Apps.Add(new InstalledAppViewModel(app, account, metadataAvailable));

            ApplyFilter();
            AppLog.Info($"On-device list: {apps.Count} apps on {TargetDevice.Name}");
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

            _settings.MarkOwned(entry.AppStoreId);
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
        return byBundle.Count > 0 ? byBundle[0] : null;
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
