using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.App.Services;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;
using Microsoft.Win32;

namespace IPAStudio.App.ViewModels;

/// <summary>
/// Direct download: enter a Bundle ID, App Store ID or link, pick a folder, get the IPA signed with the
/// signed-in Apple ID saved straight into that folder.
///
/// Deliberately independent of the queue and of any connected device — the point is
/// "a new app came out, I want the file now", which has nothing to do with installing
/// onto a phone. It therefore does NOT go through <see cref="QueueService"/> (that
/// pipeline requires a target device) and calls <see cref="DownloadService"/> directly.
/// </summary>
public sealed partial class DirectDownloadViewModel : ObservableObject, IPageAware
{
    private readonly CatalogService _catalog;
    private readonly DownloadService _download;
    private readonly AuthService _auth;
    private readonly SettingsService _settings;

    /// <summary>
    /// Connected devices, used only to recover a name and icon the store refused to give.
    /// This page deliberately works without a device; these are consulted when one happens
    /// to be attached, never required.
    /// </summary>
    private readonly DeviceService _devices;
    private readonly InstallService _install;

    /// <summary>
    /// Local .ipa libraries. The third place an app's name and icon can come from, and the only
    /// one that answers with no network and no phone attached.
    /// </summary>
    private readonly IpaCatalogService _ipaCatalogs;

    /// <summary>Where a download registers itself so leaving the page does not hide it.</summary>
    private readonly OperationService _operations;

    /// <summary>
    /// The fallback route through iTunes 12.6.5.3. Independent of <see cref="DownloadService"/>
    /// and of the Apple ID signed in here — that is the entire reason it is worth having.
    /// </summary>
    private readonly ItunesLegacyService _itunes;

    private INavigator? _navigator;

    private CancellationTokenSource? _cts;

    public DirectDownloadViewModel(
        CatalogService catalog,
        DownloadService download,
        AuthService auth,
        SettingsService settings,
        DeviceService devices,
        InstallService install,
        OperationService operations,
        IpaCatalogService ipaCatalogs,
        ItunesLegacyService itunes)
    {
        _itunes = itunes;
        _ipaCatalogs = ipaCatalogs;
        _catalog = catalog;
        _download = download;
        _auth = auth;
        _settings = settings;
        _devices = devices;
        _install = install;
        _operations = operations;

        // Reuse the last folder so a user grabbing several apps in a row picks once.
        DestinationFolder = settings.Current.LastDirectDownloadFolder ?? "";
    }

    // ---- Input ----

    [ObservableProperty]
    private string _bundleId = "";

    [ObservableProperty]
    private string _destinationFolder = "";

    // ---- Resolved app ----

    /// <summary>The app found for <see cref="BundleId"/>, or null before a lookup.</summary>
    [NotifyPropertyChangedFor(nameof(FoundDetails))]
    [ObservableProperty]
    private AppEntry? _foundApp;

    /// <summary>
    /// Bundle id and version on one line, joined only where they exist. An unlisted app has
    /// neither, and the view previously drew the separator regardless, leaving a stray "·"
    /// hanging under the name.
    /// </summary>
    public string? FoundDetails
    {
        get
        {
            if (FoundApp is null) return null;

            var parts = new[] { FoundApp.BundleId, FoundApp.LatestVersion }
                .Where(p => !string.IsNullOrWhiteSpace(p));

            var text = string.Join("  ·  ", parts);
            return string.IsNullOrEmpty(text) ? null : text;
        }
    }

    [ObservableProperty]
    private string? _foundIconUrl;

    /// <summary>
    /// Home-screen artwork read off a connected device, for apps the store has no artwork URL
    /// for. Held as an image rather than a URL because it never had one: the bytes come from
    /// SpringBoard.
    /// </summary>
    [ObservableProperty]
    private ImageSource? _foundIconImage;

    /// <summary>
    /// True when the found app is already in the catalog, so the button reads
    /// "already added" instead of offering a duplicate.
    /// </summary>
    [NotifyPropertyChangedFor(nameof(CanAddToCatalog))]
    [ObservableProperty]
    private bool _isInCatalog;

    /// <summary>An app was found and is not in the catalog yet.</summary>
    /// <summary>
    /// Provisional entries are excluded: the catalog is keyed by store id and holds the
    /// metadata shown in the app list, so saving one would add a permanent row with a bundle
    /// id for a name, no icon and an id of zero — which would then collide with every other
    /// such row.
    /// </summary>
    public bool CanAddToCatalog =>
        FoundApp is { IsProvisional: false, AppStoreId: > 0 } && !IsInCatalog;

    partial void OnFoundAppChanged(AppEntry? value) => OnPropertyChanged(nameof(CanAddToCatalog));

    // ---- State ----

    [ObservableProperty]
    private bool _isLookingUp;

    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    [ObservableProperty]
    private bool _isDownloading;

    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    [ObservableProperty]
    private double _progress;

    /// <summary>
    /// True while a transfer is running with no percentage to show. Apps missing from
    /// the App Store catalog have no known size, so ipatool reports bytes only and
    /// <see cref="Progress"/> stays at 0 for the whole download — a bar that just sat
    /// empty and looked broken. An animated bar states honestly that work is happening
    /// but its extent is unknown.
    /// </summary>
    public bool IsProgressIndeterminate => IsDownloading && Progress <= 0;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string? _errorText;

    /// <summary>Path of the finished file; drives the "Open folder" button.</summary>
    [ObservableProperty]
    private string? _savedPath;

    public bool IsSignedIn => _auth.IsAuthenticated;

    public string? AccountEmail => _auth.CurrentAccount?.Email;

    /// <summary>
    /// Refreshes the sign-in dependent parts of the UI. This viewmodel is a singleton,
    /// so the user may well have signed in on another screen since it was constructed
    /// and the cached values would otherwise be stale.
    /// </summary>
    public void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(AccountEmail));
    }

    // ---- Commands ----

    [RelayCommand]
    private void GoBack()
    {
        // Abort any transfer in flight: leaving the page hides the only progress and
        // cancel UI, so a download left running would be invisible and unstoppable.
        CancelDownload();
        _navigator?.GoBack();
    }

    /// <summary>
    /// Straight to the device list. Cancels like <see cref="GoBack"/> does, and for the same
    /// reason: this page owns the only progress and cancel UI a download has.
    /// </summary>
    [RelayCommand]
    private void GoHome()
    {
        CancelDownload();
        _navigator?.GoHome();
    }

    [RelayCommand]
    private void SignIn() => _navigator?.GoTo(Page.Login);

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc.Get("L.Dialog.PickFolderTitle"),
            // Reopen where they last saved. Only set it when the folder still exists —
            // pointing at a deleted path makes the dialog open somewhere arbitrary.
            InitialDirectory = Directory.Exists(DestinationFolder) ? DestinationFolder : "",
        };

        if (dialog.ShowDialog() != true) return;

        DestinationFolder = dialog.FolderName;
        _settings.Current.LastDirectDownloadFolder = DestinationFolder;
        _settings.Save();
        ErrorText = null;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        // Select the file inside Explorer rather than just opening the folder, so the
        // user sees exactly which file was produced.
        try
        {
            if (!string.IsNullOrEmpty(SavedPath) && File.Exists(SavedPath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SavedPath}\"") { UseShellExecute = true });
            else if (Directory.Exists(DestinationFolder))
                Process.Start(new ProcessStartInfo(DestinationFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not open the destination folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves what the user typed — a bundle id, a numeric App Store id, or a store
    /// link pasted from the browser or Share sheet — to a real App Store entry. Kept
    /// separate from the download so the user can confirm they got the app they meant
    /// before spending bandwidth: a typo is otherwise indistinguishable from a missing app.
    /// </summary>
    [RelayCommand]
    private async Task LookupAsync()
    {
        var query = AppQueryParser.Parse(BundleId);
        if (!query.IsValid)
        {
            ErrorText = Loc.Get("L.Direct.NeedQuery");
            return;
        }

        IsLookingUp = true;
        ErrorText = null;
        StatusText = Str("L.Direct.Searching");
        FoundApp = null;
        FoundIconUrl = null;
        FoundIconImage = null;
        IsInCatalog = false;
        SavedPath = null;

        try
        {
            var results = await _catalog.FindAsync(query).ConfigureAwait(true);
            var app = results.FirstOrDefault();

            if (app is null)
            {
                StatusText = null;
                ErrorText = Str("L.Direct.NotFound");
                return;
            }

            // A provisional entry may still be an app sitting on a phone plugged into this
            // machine, which knows its real name and holds its artwork. That is the only
            // remaining source once the store has declined to answer, and it is the source
            // that covers the apps most worth rescuing.
            if (app.IsProvisional)
            {
                var fromDevice = await TryResolveFromDeviceAsync(app).ConfigureAwait(true);
                if (fromDevice is not null)
                {
                    app = fromDevice.Value.Entry;
                    FoundIconImage = fromDevice.Value.Icon;
                }

                // Still nameless: no store listing and no phone that has it. The .ipa libraries
                // already scanned on this machine are consulted last, and for a delisted app
                // with nothing plugged in they are the only source left - which is exactly the
                // case where the page used to show nothing but the number that was typed.
                if (app.IsProvisional && !app.HasLocalMetadata)
                {
                    var fromLibrary = TryResolveFromLibraries(app);
                    if (fromLibrary is not null)
                    {
                        app = fromLibrary.Value.Entry;
                        FoundIconImage ??= fromLibrary.Value.Icon;
                    }
                }
            }

            FoundApp = app;
            FoundIconUrl = app.IconUrl;
            IsInCatalog = _catalog.IsInCatalog(app.AppStoreId);

            // A provisional entry means the public catalog does not list this app, so there is
            // no name, size or version to show — only the identifier the user typed. Saying so
            // explains the bare panel instead of letting it look like a half-failed lookup.
            //
            // Both kinds of identifier get the same message. A previous version singled out
            // bundle ids as near-hopeless, on the theory that ipatool has to resolve them
            // through this very catalog; downloads by bundle id demonstrably keep working, so
            // that warning only talked users out of the one route that still fetches delisted
            // apps. Neither case is predicted here — the store is left to answer.
            //
            // Two wordings, because the old single one claimed the name could not be shown —
            // which now sits directly above the name whenever it was recovered locally, and
            // reads as the screen contradicting itself.
            StatusText = app switch
            {
                { IsProvisional: false } => null,
                { HasLocalMetadata: true } => Str("L.Direct.UnlistedKnown"),
                _ => Str("L.Direct.Unlisted"),
            };

            AppLog.Info(app.IsProvisional
                ? $"Direct download: '{BundleId.Trim()}' is not in the public catalog; will try the store directly"
                : $"Direct download: found '{app.Name}' ({app.BundleId}) id={app.AppStoreId}");
        }
        catch (Exception ex)
        {
            StatusText = null;
            ErrorText = Loc.Get("L.Direct.LookupFailed");
            AppLog.Warn($"Direct download lookup failed: {ex.Message}");
        }
        finally
        {
            IsLookingUp = false;
        }
    }

    /// <summary>
    /// Saves the found app into the catalog so it can be installed later from the app
    /// list without looking it up again. The bundled list is an embedded resource, so
    /// the entry goes to the user catalog file alongside it.
    /// </summary>
    [RelayCommand]
    private async Task AddToCatalogAsync()
    {
        var app = FoundApp;
        if (app is null) return;

        ErrorText = null;
        try
        {
            var added = await _catalog.AddToUserCatalogAsync(app).ConfigureAwait(true);

            // Either way the app is now in the catalog, which is what the flag means.
            IsInCatalog = true;
            StatusText = added
                ? Loc.Format("L.Direct.AddedToCatalog", app.Name)
                : Loc.Get("L.Direct.AlreadyInCatalog");

            if (added) AppLog.Info($"Added to catalog: '{app.Name}' id={app.AppStoreId}");
        }
        catch (Exception ex)
        {
            StatusText = null;
            ErrorText = Loc.Get("L.Direct.AddFailed");
            AppLog.Warn($"Add to catalog failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (FoundApp is null)
        {
            // Let the button work as "find and download" when the user typed something and
            // pressed Download without pressing Find first.
            await LookupAsync().ConfigureAwait(true);
            if (FoundApp is null) return;
        }

        if (!_auth.IsAuthenticated)
        {
            ErrorText = Str("L.Direct.NeedLogin");
            return;
        }

        if (string.IsNullOrWhiteSpace(DestinationFolder))
        {
            ErrorText = Str("L.Direct.NeedFolder");
            return;
        }

        _cts = new CancellationTokenSource();
        IsDownloading = true;
        ErrorText = null;
        SavedPath = null;
        Progress = 0;
        StatusText = Str("L.Direct.Downloading");

        // Registered so leaving this page does not hide the download. No device is involved,
        // so the subtitle names the app instead — that is what distinguishes two downloads.
        var operation = _operations.Start(new Operation(
            OperationKind.Download,
            Page.DirectDownload,
            Loc.Get("L.Ops.Kind.Download"),
            FoundApp!.Name,
            cancel: _cts.Cancel));

        // Fully qualified: this class also exposes a `Progress` property for the bar,
        // and the bare type name next to it reads as a mistake even where it compiles.
        var progress = new System.Progress<DownloadProgress>(p =>
        {
            Progress = p.Percent;

            if (p.Connecting)
            {
                var seconds = (int)p.Elapsed.TotalSeconds;
                StatusText = seconds >= 2
                    ? Loc.Format("L.Queue.Status.ConnectingElapsed", seconds)
                    : Loc.Get("L.Queue.Status.Connecting");
            }
            else if (p.Finalizing)
            {
                StatusText = Loc.Format("L.Queue.Status.Finalizing", FormatBytes(p.DownloadedBytes));
            }
            else if (p.TotalBytes > 0)
            {
                var speed = p.SpeedBps > 0 ? $" · {FormatBytes((long)p.SpeedBps)}{Loc.Get("L.Unit.PerSecond")}" : "";
                StatusText = $"{p.Percent:0.0}% · {FormatBytes(p.DownloadedBytes)} / {FormatBytes(p.TotalBytes)}{speed}";
            }
            else
            {
                // Same wording as the queue screen: label the number as bytes-so-far, so
                // it cannot be misread as the (unknown) total sitting next to it.
                var speed = p.SpeedBps > 0 ? $" · {FormatBytes((long)p.SpeedBps)}{Loc.Get("L.Unit.PerSecond")}" : "";
                StatusText = Loc.Format("L.Queue.Status.Downloaded", FormatBytes(p.DownloadedBytes))
                    + $"{speed} · {Loc.Get("L.Queue.Status.TotalUnknown")}";
            }

            operation.Progress = Progress;
            operation.Detail = StatusText ?? "";
        });

        try
        {
            var result = await _download.DownloadAsync(
                FoundApp!,
                autoPurchase: true,
                progress,
                destinationFolder: DestinationFolder,
                ct: _cts.Token).ConfigureAwait(true);

            if (result.Success && result.IpaPath is not null)
            {
                SavedPath = result.IpaPath;
                Progress = 100;
                StatusText = $"{Str("L.Direct.Done")} {Path.GetFileName(result.IpaPath)}";

                // A successful download proves ownership; persist it so the app picker
                // shows the right badge without another Apple round-trip.
                FoundApp!.License = LicenseState.Owned;
                _settings.MarkOwned(FoundApp!.AppStoreId);
                AppLog.Info($"Direct download OK: {result.IpaPath}");
                operation.Finish(OperationState.Done, Path.GetFileName(result.IpaPath));
            }
            else
            {
                StatusText = null;
                ErrorText = result.SessionExpired
                    ? Str("L.Direct.NeedLogin")
                    : result.Error ?? Loc.Get("L.Error.DownloadFailed");
                operation.Finish(OperationState.Failed, ErrorText);

                if (!string.IsNullOrWhiteSpace(result.Detail))
                    AppLog.Warn($"Direct download failed: {result.Detail}");

                // The session died mid-download: send the user to the login screen
                // instead of leaving a dead-end error on screen.
                if (result.SessionExpired)
                {
                    // Cleared first, or the login screen would find a cached account still
                    // present, decide there is nothing to do and skip straight past itself.
                    _auth.InvalidateSession();
                    OnPropertyChanged(nameof(IsSignedIn));
                    _navigator?.GoTo(Page.Login);
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = null;
            Progress = 0;
            operation.Finish(OperationState.Cancelled);
        }
        catch (Exception ex)
        {
            StatusText = null;
            ErrorText = Loc.Get("L.Error.Unknown");
            AppLog.Error("Direct download threw.", ex);
            operation.Finish(OperationState.Failed, ex.Message);
        }
        finally
        {
            IsDownloading = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void CancelDownload() => _cts?.Cancel();

    // ---- Alternative route: iTunes 12.6.5.3 ----

    /// <summary>
    /// Explains the state of the iTunes route on this machine: not installed, installed but too
    /// new to have an App Store, or ready. Shown next to the button so its refusal to work is
    /// never a mystery.
    /// </summary>
    [ObservableProperty]
    private string? _itunesHint;

    /// <summary>True while the library is being watched for a file iTunes is writing.</summary>
    [ObservableProperty]
    private bool _isItunesRunning;

    /// <summary>
    /// Downloads through iTunes instead of ipatool.
    ///
    /// Apple changed the authentication endpoint ipatool signs into, which is why the normal
    /// route started failing. iTunes 12.6.5.3 has its own store session that still works and
    /// still contains the App Store tab, so it can fetch an app the normal route cannot.
    ///
    /// The sequence is the one described on 4PDA, automated as far as it safely can be: snapshot
    /// the iTunes library, open the app's store page inside iTunes with an itmss:// link, let the
    /// user press Download there, then wait for the new .ipa and copy it into the chosen folder.
    /// The click stays with the user on purpose — iTunes performs its own account and licence
    /// checks, and driving its UI from outside would break the moment Apple moves a button.
    /// </summary>
    [RelayCommand]
    private async Task DownloadViaItunesAsync()
    {
        ItunesHint = null;

        if (FoundApp is null)
        {
            // Same courtesy as the main button: find first if they typed and went straight here.
            await LookupAsync().ConfigureAwait(true);
            if (FoundApp is null) return;
        }

        // iTunes is driven by store id: an itmss:// link needs the numeric id, and a bundle id
        // cannot be turned into one without the very catalog that failed to list the app.
        if (FoundApp.AppStoreId <= 0)
        {
            ItunesHint = Str("L.Itunes.NeedStoreId");
            return;
        }

        if (string.IsNullOrWhiteSpace(DestinationFolder))
        {
            ErrorText = Str("L.Direct.NeedFolder");
            return;
        }

        var installation = _itunes.Detect();
        if (installation is null)
        {
            ItunesHint = Str("L.Itunes.NotFound");
            return;
        }

        if (!installation.SupportsAppStore)
        {
            ItunesHint = Loc.Format("L.Itunes.TooNew", installation.Version.ToString());
            return;
        }

        _cts = new CancellationTokenSource();
        IsItunesRunning = true;
        ErrorText = null;
        SavedPath = null;
        Progress = 0;
        IsDownloading = true;
        StatusText = Str("L.Itunes.Opening");

        var operation = _operations.Start(new Operation(
            OperationKind.Download,
            Page.DirectDownload,
            Loc.Get("L.Itunes.OpKind"),
            FoundApp.Name,
            cancel: _cts.Cancel));

        try
        {
            // Snapshot first. The watcher works by difference, so anything already in the
            // library must be recorded before iTunes is allowed to add to it.
            var before = _itunes.ListLibrary(_settings.Current.ItunesLibraryFolder);

            if (!_itunes.OpenStorePage(FoundApp.AppStoreId))
            {
                ItunesHint = Str("L.Itunes.OpenFailed");
                operation.Finish(OperationState.Failed, ItunesHint);
                return;
            }

            StatusText = Str("L.Itunes.Waiting");
            operation.Detail = StatusText;

            var status = new System.Progress<string>(name =>
            {
                // No percentage is available: iTunes reports nothing to us, so the file name
                // being written is the only honest signal that something is happening.
                StatusText = Loc.Format("L.Itunes.Receiving", name);
                operation.Detail = StatusText ?? "";
            });

            var produced = await _itunes
                .WaitForNewIpaAsync(before, _settings.Current.ItunesLibraryFolder, status, _cts.Token)
                .ConfigureAwait(true);

            if (produced is null)
            {
                StatusText = null;
                ErrorText = Str("L.Itunes.Timeout");
                operation.Finish(OperationState.Failed, ErrorText);
                return;
            }

            StatusText = Str("L.Itunes.Copying");
            var saved = await _itunes
                .CopyOutAsync(produced, DestinationFolder, _cts.Token)
                .ConfigureAwait(true);

            SavedPath = saved;
            Progress = 100;
            StatusText = $"{Str("L.Direct.Done")} {Path.GetFileName(saved)}";
            operation.Finish(OperationState.Done, Path.GetFileName(saved));
            AppLog.Info($"iTunes route finished: {saved}");
        }
        catch (OperationCanceledException)
        {
            StatusText = null;
            Progress = 0;
            operation.Finish(OperationState.Cancelled);
        }
        catch (Exception ex)
        {
            StatusText = null;
            ErrorText = Loc.Get("L.Error.Unknown");
            AppLog.Error("iTunes route threw.", ex);
            operation.Finish(OperationState.Failed, ex.Message);
        }
        finally
        {
            IsItunesRunning = false;
            IsDownloading = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ---- Helpers ----

    /// <summary>
    /// Recovers an unlisted app's real name, bundle id, version and icon from a connected
    /// device that has it installed. Returns null when no device is attached, none of them has
    /// the app, or the device cannot be read.
    ///
    /// Worth the round trip only for a provisional entry: for anything the store described
    /// there is nothing here to add, and listing a device's apps takes seconds.
    /// </summary>
    private async Task<(AppEntry Entry, ImageSource? Icon)?> TryResolveFromDeviceAsync(AppEntry app)
    {
        var devices = _devices.ConnectedDevices.ToList();
        if (devices.Count == 0) return null;

        // Bounded so an unresponsive or sleeping device cannot hold the lookup open with no
        // way out — the button would sit spinning on a page that needs no device at all.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        foreach (var device in devices)
        {
            try
            {
                var installed = await _install
                    .GetInstalledAppsAsync(device.Udid, cts.Token)
                    .ConfigureAwait(true);

                var match = installed.FirstOrDefault(a =>
                    (app.AppStoreId > 0 && a.StoreItemId == app.AppStoreId)
                    || (!string.IsNullOrWhiteSpace(app.BundleId)
                        && string.Equals(a.BundleId, app.BundleId, StringComparison.OrdinalIgnoreCase)));

                if (match is null) continue;

                // Rebuilt rather than patched: Name and AppStoreId are init-only, which is
                // what keeps an entry from being quietly rewritten after it has been handed
                // to the downloader.
                var entry = new AppEntry
                {
                    Name = match.Name,
                    // The device's store id is used only to fill a gap. Overriding an id the
                    // user typed would download a different app from the one they asked for.
                    AppStoreId = app.AppStoreId > 0 ? app.AppStoreId : match.StoreItemId ?? 0,
                    BundleId = app.BundleId ?? match.BundleId,
                    IconUrl = app.IconUrl,
                    IconUrlLarge = app.IconUrlLarge,
                    CachedIconPath = app.CachedIconPath,
                    Developer = app.Developer,
                    LatestVersion = app.LatestVersion ?? match.Version,
                    IsProvisional = true,
                    HasLocalMetadata = true,
                };

                AppLog.Info($"Direct download: '{entry.Name}' identified from {device.Name}");

                var icons = await _install
                    .GetAppIconsAsync(device.Udid, new[] { match.BundleId }, cts.Token)
                    .ConfigureAwait(true);

                return (entry, icons.TryGetValue(match.BundleId, out var png) ? DecodeIcon(png) : null);
            }
            catch (OperationCanceledException)
            {
                // Out of time, or the page was left. Either way the identifier still stands
                // on its own and the download can proceed without a name.
                return null;
            }
            catch (Exception ex)
            {
                // Try the next device; artwork must never break a lookup.
                AppLog.Warn($"Could not read app metadata from {device.Name}: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Recovers an app's name, version and icon from the .ipa libraries scanned on this machine.
    /// Returns null when no library holds this bundle id.
    ///
    /// Matched by bundle id only. The libraries hold no store ids, and matching on a name would
    /// be matching on the very thing that is missing at this point.
    /// </summary>
    private (AppEntry Entry, ImageSource? Icon)? TryResolveFromLibraries(AppEntry app)
    {
        // No bundle id required: an app entered as a bare store number has none, and matching on
        // that number is exactly what is needed here.
        if (string.IsNullOrWhiteSpace(app.BundleId) && app.AppStoreId <= 0) return null;

        IpaCatalogItem? match;
        try
        {
            match = _ipaCatalogs.FindLocal(app.BundleId, app.AppStoreId);
        }
        catch (Exception ex)
        {
            // The libraries are a courtesy here, exactly as the bundled catalog is: a lookup
            // must not fail because a library file is unreadable.
            AppLog.Warn($"Could not search local libraries: {ex.Message}");
            return null;
        }

        if (match is null || string.IsNullOrWhiteSpace(match.Name)) return null;

        AppLog.Info($"Direct download: '{match.Name}' identified from a local library");

        // Rebuilt rather than patched, for the same reason as the device path: Name and
        // AppStoreId are init-only so a downloader can never be handed a rewritten entry.
        var entry = new AppEntry
        {
            Name = match.Name,
            AppStoreId = app.AppStoreId,
            // Filled in from the archive when the query was a bare number: the bundle id is
            // shown under the name, and the archive is the only thing that knows it here.
            BundleId = string.IsNullOrWhiteSpace(app.BundleId)
                ? (string.IsNullOrWhiteSpace(match.BundleId) ? app.BundleId : match.BundleId)
                : app.BundleId,
            IconUrl = app.IconUrl,
            IconUrlLarge = app.IconUrlLarge,
            CachedIconPath = app.CachedIconPath,
            Developer = app.Developer,
            LatestVersion = app.LatestVersion ?? match.Version,
            IsProvisional = true,
            HasLocalMetadata = true,
        };

        return (entry, LoadIconFile(match.IconPath));
    }

    /// <summary>Loads an icon a library scan extracted to disk, or null if unreadable.</summary>
    private static ImageSource? LoadIconFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            // Loaded through a stream, not a UriSource: the latter keeps the file locked for the
            // life of the image, and a rescan of the same library then cannot replace it.
            using (var stream = File.OpenRead(path))
            {
                image.DecodePixelWidth = 128;
                image.StreamSource = stream;
                image.EndInit();
            }
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Decodes SpringBoard's PNG for display, or null if it cannot be read.</summary>
    private static ImageSource? DecodeIcon(byte[] png)
    {
        try
        {
            var image = new BitmapImage();
            using (var stream = new MemoryStream(png))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                // The card draws it at 64 px; the source is up to 180 px square.
                image.DecodePixelWidth = 128;
                image.StreamSource = stream;
                image.EndInit();
            }
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static string Str(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as string ?? key;

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
