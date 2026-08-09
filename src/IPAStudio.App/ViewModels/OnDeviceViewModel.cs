using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.App.Controls;
using IPAStudio.App.Services;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;
using Microsoft.Win32;

namespace IPAStudio.App.ViewModels;

/// <summary>
/// One catalog app offered as the store counterpart of an installed app, with the command
/// that downloads the device's app using this entry's store id.
///
/// The command is handed in already bound to its row and entry: the alternative is a
/// two-parameter command in XAML, which needs a converter or a multi-binding to express and
/// is easy to wire to the wrong row inside an item template.
/// </summary>
public sealed class CatalogCandidateViewModel
{
    public CatalogCandidateViewModel(AppEntry entry, IRelayCommand command)
    {
        Entry = entry;
        Command = command;
    }

    public AppEntry Entry { get; }
    public IRelayCommand Command { get; }

    public string Name => Entry.Name;

    /// <summary>The store id, shown so two similar names can be told apart.</summary>
    public string Details => Entry.AppStoreId.ToString();
}

/// <summary>
/// One app installed on the device, plus its own download state.
/// </summary>
public sealed partial class InstalledAppViewModel : ObservableObject, ISelectableTile
{
    public InstalledApp App { get; }

    /// <summary>
    /// Only rows that can actually be fetched, and are not already fetching, are candidates —
    /// the same rule the tick box was already shown under, so click selection cannot assemble
    /// a batch the download command would then have to filter back down.
    /// </summary>
    public bool CanSelect => CanDownload && IsIdle;

    /// <summary>
    /// Apple ID currently signed in, or null when nobody is.
    ///
    /// Not readonly: a download can discover mid-session that Apple has rejected the token,
    /// and every row's button state is derived from this.
    /// </summary>
    private string? _account;

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
    /// Re-reads the signed-in account after it changed underneath the row, so the button and
    /// the reason beside it stop describing a session that is gone.
    /// </summary>
    public void OnSignInStateChanged(string? account)
    {
        _account = account;
        OnPropertyChanged(nameof(AccountMatches));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(BlockedReason));
        OnPropertyChanged(nameof(IsAccountUnverified));

        // A row that can no longer be downloaded must not stay ticked, or the batch button
        // would keep counting it and refuse to run with nothing it is allowed to fetch.
        if (!CanDownload) IsSelected = false;
    }

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

    /// <summary>
    /// Catalog apps offered after the store refused this app's bundle id, for the user to
    /// pick from. Empty at every other time.
    /// </summary>
    public ObservableCollection<CatalogCandidateViewModel> Candidates { get; } = new();

    public bool HasCandidates => Candidates.Count > 0;

    public void ShowCandidates(IEnumerable<CatalogCandidateViewModel> candidates)
    {
        Candidates.Clear();
        foreach (var candidate in candidates) Candidates.Add(candidate);
        OnPropertyChanged(nameof(HasCandidates));
    }

    public void ClearCandidates()
    {
        if (Candidates.Count == 0) return;
        Candidates.Clear();
        OnPropertyChanged(nameof(HasCandidates));
    }
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

    /// <summary>
    /// The application-wide cap on concurrent downloads.
    ///
    /// This screen has to hold it too, not just the queue: several downloads started here now
    /// run side by side, and with the cap enforced only inside <see cref="QueueService"/> a
    /// batch from this page plus a running install queue would open twice the number of
    /// connections the slider promises, which Apple answers by shaping all of them.
    /// </summary>
    private readonly DownloadThrottle _throttle;

    /// <summary>Other attached devices, offered as transfer destinations.</summary>
    private readonly DeviceService _devices;

    /// <summary>
    /// The ordinary install queue. A transfer is built as a normal download-and-install run
    /// against the destination device, so it inherits that pipeline rather than duplicating it.
    /// </summary>
    private readonly OperationService _operations;

    private INavigator? _navigator;

    /// <summary>
    /// Queue of the transfer being assembled, created when the first app is queued.
    ///
    /// Held between calls because a transfer is built app by app: each queued app appends
    /// to the same operation instead of starting a new one, which is what makes the batch
    /// a single transfer the user can minimise as a unit.
    /// </summary>
    private Operation? _pendingTransfer;

    /// <summary>
    /// The device <see cref="_pendingTransfer"/> is sending to.
    ///
    /// Kept so a late-resolved app only joins that batch when it is going to the same place.
    /// The candidate chips capture their destination when they are offered, so without this
    /// check a pick left over from an earlier transfer would land in an operation titled for
    /// a different device.
    /// </summary>
    private Device? _pendingTransferTo;

    public Device? TargetDevice { get; private set; }

    public ObservableCollection<InstalledAppViewModel> Apps { get; } = new();

    /// <summary>
    /// The download run registered with <see cref="OperationService"/>, so saving IPAs from this
    /// page can be sent to the background and counted in the corner circle like every other kind
    /// of work.
    ///
    /// This page used to run its downloads entirely by itself, which is why it was the one place
    /// multitasking did nothing: with no operation registered there was nothing to minimise, no
    /// entry in the operations list, and leaving the page cancelled the transfer outright.
    /// </summary>
    private Operation? _operation;

    /// <summary>
    /// Cancellation for a whole batch, so "cancel" on the operation stops the run rather than
    /// only the row that happens to be transferring.
    /// </summary>
    private CancellationTokenSource? _batchCts;

    /// <summary>Rows in the current batch, for the operation's overall percentage.</summary>
    private List<InstalledAppViewModel> _batchRows = new();

    public OnDeviceViewModel(
        InstallService install,
        DownloadService download,
        CatalogService catalog,
        AuthService auth,
        SettingsService settings,
        DeviceService devices,
        OperationService operations,
        DownloadThrottle throttle)
    {
        _install = install;
        _download = download;
        _catalog = catalog;
        _auth = auth;
        _settings = settings;
        _devices = devices;
        _operations = operations;
        _throttle = throttle;

        DestinationFolder = settings.Current.LastOnDeviceFolder
                            ?? settings.Current.LastDirectDownloadFolder
                            ?? "";

        _isTileView = settings.Current.OnDeviceTileView;
        _tileSize = Math.Clamp(settings.Current.OnDeviceTileSize, MinTileSize, MaxTileSize);
        _selectionMode = settings.Current.OnDeviceSelectionMode;
        _isSelecting = _selectionMode == TileSelectionMode.Click && !settings.Current.OnDeviceCtrlSelects;

        settings.Changed += (_, _) =>
        {
            SelectionMode = _settings.Current.OnDeviceSelectionMode;
            ApplySelectionSetting();
        };
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

    // ─────────────────────── list / tile layout ───────────────────────

    /// <summary>Narrowest tile that still fits an icon, a name and the button under it.</summary>
    public const double MinTileSize = 104;

    /// <summary>Widest tile worth offering; past this the grid is just a list with gaps.</summary>
    public const double MaxTileSize = 208;

    /// <summary>
    /// Whether the apps are shown as tiles instead of rows. Persisted, because this is a
    /// preference about how the user likes to look at their apps, not a per-visit choice.
    /// </summary>
    [ObservableProperty]
    private bool _isTileView;

    /// <summary>Tile edge in device-independent pixels, driven by the size slider.</summary>
    [ObservableProperty]
    private double _tileSize = 132;

    public bool IsListView => !IsTileView;

    /// <summary>
    /// Tile height: the icon square plus a fixed block for the name, the version line and the
    /// button. Fixed rather than measured because <see cref="Controls.VirtualizingWrapPanel"/>
    /// lays out a uniform grid, and a tile that grew with its text would misplace every row
    /// after the first long name.
    /// </summary>
    public double TileHeight => TileSize + 118;

    /// <summary>Artwork size inside a tile, leaving a margin the tile does not look cramped in.</summary>
    public double TileIconSize => Math.Max(48, TileSize - 56);

    /// <summary>Corner radius scaled with the icon, so it keeps iOS's proportions.</summary>
    public double TileIconRadius => TileIconSize * 0.225;

    /// <summary>
    /// How rows and tiles are picked for the batch download, from settings. Mirrored here
    /// rather than read from settings in the XAML so a change reaches the open page.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSelectionCheckboxes))]
    [NotifyPropertyChangedFor(nameof(ShowSelectToggle))]
    private TileSelectionMode _selectionMode;

    /// <summary>True when selecting is done with tick boxes, so the rows draw one.</summary>
    public bool ShowSelectionCheckboxes => SelectionMode == TileSelectionMode.Checkbox;

    /// <summary>
    /// Whether a click picks apps out rather than just focusing a row. The toolbar's toggle;
    /// without it, click mode needed Ctrl to add a second app and nothing said so.
    /// </summary>
    [ObservableProperty]
    private bool _isSelecting;

    /// <summary>
    /// True when the toolbar should offer the select toggle at all. Hidden when Ctrl-select is
    /// off: that setting decides whether the page has two states to switch between, and with it
    /// off clicking is the only way in, so the page selects on click always rather than putting
    /// a press between the user and every batch.
    /// </summary>
    public bool ShowSelectToggle => SelectionMode == TileSelectionMode.Click && CtrlSelects;

    /// <summary>Whether Ctrl-click selects while the select mode is off, from settings.</summary>
    public bool CtrlSelects => _settings.Current.OnDeviceCtrlSelects;

    /// <summary>
    /// Puts the select mode where the current settings say it belongs: always on when clicking
    /// is the only way to select, and otherwise off, waiting for the toggle.
    /// </summary>
    private void ApplySelectionSetting()
    {
        OnPropertyChanged(nameof(CtrlSelects));
        OnPropertyChanged(nameof(ShowSelectToggle));

        // Off outside click mode too, or the page would keep toggling rows on click while
        // showing tick boxes and no visible way to stop.
        IsSelecting = SelectionMode == TileSelectionMode.Click && !CtrlSelects;
    }

    partial void OnIsTileViewChanged(bool value)
    {
        OnPropertyChanged(nameof(IsListView));
        _settings.Current.OnDeviceTileView = value;
        SaveViewPreferences();
    }

    partial void OnTileSizeChanged(double value)
    {
        OnPropertyChanged(nameof(TileHeight));
        OnPropertyChanged(nameof(TileIconSize));
        OnPropertyChanged(nameof(TileIconRadius));

        // Kept in the settings object on every tick but written to disk only when the page is
        // left: a slider drag raises this a few dozen times, and saving each one would rewrite
        // the settings file that often for a value that is still moving.
        _settings.Current.OnDeviceTileSize = value;
    }

    [RelayCommand]
    private void SetListView() => IsTileView = false;

    [RelayCommand]
    private void SetTileView() => IsTileView = true;

    /// <summary>Writes the layout preferences out, tolerating a settings file that will not save.</summary>
    private void SaveViewPreferences()
    {
        try { _settings.Save(); }
        catch (Exception ex) { AppLog.Warn($"Could not save the on-device view preference: {ex.Message}"); }
    }

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

    // ─────────────────── transfer to another device ───────────────────

    /// <summary>
    /// Other devices currently attached, as transfer destinations. The source device is
    /// excluded: the apps are already on it.
    /// </summary>
    public ObservableCollection<Device> TransferTargets { get; } = new();

    /// <summary>At least one destination was found, so the list has something to show.</summary>
    public bool HasTransferTargets => TransferTargets.Count > 0;

    /// <summary>
    /// Whether the transfer button is shown. Tied to the selection alone, deliberately.
    ///
    /// Hiding it until a second device was attached meant that with one phone connected — the
    /// ordinary case — the feature was invisible with nothing to indicate it existed or what it
    /// wanted. Hiding it also depended on the device list being current, so plugging in a second
    /// phone while this screen was already open would not have revealed it. The button is now
    /// always offered and the destination list explains an empty result.
    /// </summary>
    public bool CanTransfer => HasSelection;

    /// <summary>
    /// True when the destination list came back empty, so the menu can say why instead of
    /// opening as an empty box.
    /// </summary>
    public bool NoTransferTargets => TransferTargets.Count == 0;

    /// <summary>"Transfer to another iPhone (3)" — the count belongs in the label.</summary>
    public string TransferSelectedLabel => Loc.Format("L.OnDevice.TransferSelected", SelectedCount);

    /// <summary>Whether the destination list is open.</summary>
    [ObservableProperty]
    private bool _isTransferMenuOpen;

    /// <summary>
    /// Rebuilds the destination list from what is attached right now.
    ///
    /// Recomputed on open rather than kept in sync continuously: a device unplugged while the
    /// list sat closed would otherwise still be offered, and the queue would fail against a
    /// UDID that is no longer there.
    /// </summary>
    [RelayCommand]
    private async Task ToggleTransferMenuAsync()
    {
        if (IsTransferMenuOpen)
        {
            IsTransferMenuOpen = false;
            return;
        }

        // Shown from the cached list first so the menu opens instantly, then re-polled: a phone
        // plugged in moments ago should appear without the user having to leave the screen.
        RefreshTransferTargets();
        IsTransferMenuOpen = true;

        try
        {
            await _devices.PollOnceAsync(quiet: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The cached list is already on screen, so a failed poll costs nothing but freshness.
            AppLog.Warn($"Device poll for transfer menu failed: {ex.Message}");
        }

        if (IsTransferMenuOpen)
            RefreshTransferTargets();
    }

    private void RefreshTransferTargets()
    {
        TransferTargets.Clear();

        foreach (var device in _devices.ConnectedDevices)
        {
            if (TargetDevice is not null
                && string.Equals(device.Udid, TargetDevice.Udid, StringComparison.OrdinalIgnoreCase))
                continue;

            TransferTargets.Add(device);
        }

        OnPropertyChanged(nameof(HasTransferTargets));
        OnPropertyChanged(nameof(NoTransferTargets));
    }

    /// <summary>
    /// Sends the ticked apps to another attached device.
    ///
    /// Deliberately routed through the ordinary install queue rather than copying anything off
    /// this phone: what is installed here is a decrypted, device-signed binary that the other
    /// device will refuse. The App Store copy is the only one that installs, so a "transfer"
    /// is a fresh download plus an install — which is also why the queue's parallelism and its
    /// existing error handling apply unchanged.
    /// </summary>
    [RelayCommand]
    private async Task TransferSelectedAsync(Device? destination)
    {
        if (destination is null) return;

        IsTransferMenuOpen = false;

        var selected = Apps.Where(a => a.IsSelected).ToList();
        if (selected.Count == 0) return;

        // The App Store is the source, so an Apple ID is required. Without this the queue
        // would start and fail every item on the licence step.
        if (!_auth.IsAuthenticated)
        {
            _navigator?.GoToLoginForDevice(destination);
            return;
        }

        // Resolved the same way a single download is, instead of handing the queue the bare
        // bundle id the device reported. That shortcut was the whole of this failure: iOS
        // discloses no store ids any more, so every transferred app arrived with id 0 and the
        // queue could only ask the store by bundle id - the one identifier a delisted app is
        // no longer findable by. The very same apps download from the catalog screen, which
        // asks by catalog id, which is why "it works there but not here".
        var entries = new List<AppEntry>(selected.Count);
        var needChoosing = new List<InstalledAppViewModel>();

        foreach (var row in selected)
        {
            var entry = await ResolveEntryAsync(row.App, CancellationToken.None).ConfigureAwait(true);

            // A store id of 0 means the app could only be addressed by bundle id, which is
            // exactly the request the store refuses for anything delisted. Queueing it anyway
            // is what produced a screenful of errors on apps that download perfectly from the
            // catalog screen — so when the catalog does offer look-alikes, ask first rather
            // than failing first and offering the choice afterwards.
            if ((entry?.AppStoreId ?? 0) <= 0
                && _catalog.FindLocalCandidatesByName(row.Name).Count > 0)
            {
                OfferCatalogCandidates(row, transferTo: destination);
                needChoosing.Add(row);
                continue;
            }

            // ResolveEntryAsync falls back to a bundle-id entry, so a null is not expected;
            // keeping the app in the queue under its own name is still better than dropping it
            // silently, and the queue reports the failure per item.
            entries.Add(entry ?? new AppEntry
            {
                Name = row.Name,
                AppStoreId = row.App.StoreItemId ?? 0,
                BundleId = row.BundleId,
                LatestVersion = row.Version,
            });
        }

        // Nothing could be resolved: stay on this screen with the choices visible, because
        // navigating to an empty queue would look like the button had done nothing.
        if (entries.Count == 0)
        {
            AppLog.Info($"Transfer: all {needChoosing.Count} app(s) need a catalog match chosen first");
            ErrorText = Loc.Get("L.OnDevice.PickCatalogMatch");
            return;
        }

        if (needChoosing.Count > 0)
        {
            AppLog.Info($"Transfer: {needChoosing.Count} app(s) left for the user to match, " +
                        $"{entries.Count} queued");

            // Said out loud, because this is the case that looked like the button had half
            // worked: the transfer starts but the page deliberately stays here, so without a
            // line at the top the only clue was a candidate list somewhere down the list.
            ErrorText = string.Format(
                Loc.Get("L.OnDevice.PartialTransfer"), entries.Count, needChoosing.Count);
        }

        AppLog.Info(
            $"Transferring {entries.Count} app(s) from {DeviceName} to {destination.Name}; " +
            $"{entries.Count(e => e.AppStoreId > 0)} resolved to a store id");

        // One operation for the whole batch, named after both ends: with several transfers
        // running at once, "iPhone → iPad" is the only thing that tells them apart.
        _pendingTransfer = _operations.StartQueueOperation(
            OperationKind.Transfer,
            Page.OnDevice,
            Loc.Get("L.Ops.Kind.Transfer"),
            $"{DeviceName} → {destination.Name}",
            TargetDevice,
            q => q.Build(entries, destination));

        _pendingTransferTo = destination;

        // Only leave for the queue once nothing is waiting on the user here.
        if (needChoosing.Count == 0) _navigator?.GoToOperation(_pendingTransfer);
    }

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
        OnPropertyChanged(nameof(CanMinimize));

        // Coming back to a run that is still going must not re-read the device: LoadAsync
        // replaces every row, which would drop the progress bars and the cancel buttons of
        // downloads that are still writing files, and leave their rows unreachable.
        if (IsBatchRunning || Apps.Any(a => a.IsDownloading))
        {
            AppLog.Info("On-device: reopened with downloads in flight; keeping the current list");
            return;
        }

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
        OnPropertyChanged(nameof(TransferSelectedLabel));

        OnPropertyChanged(nameof(CanTransfer));
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
    /// Downloads the ticked apps, several at a time, as one background operation.
    ///
    /// Parallel now, where this used to await one app before starting the next. The serial
    /// version was justified by ipatool driving a single App Store session, but the queue
    /// screen has been downloading in parallel over the same session all along - only its
    /// authentication handshakes are serialized, and that happens inside
    /// <see cref="DownloadService"/>, which both screens go through. So the one thing the
    /// serial loop actually achieved was making this page the slow way to fetch a batch.
    ///
    /// How many run at once is the user's "parallel downloads" setting, enforced by the
    /// application-wide <see cref="DownloadThrottle"/> inside <see cref="DownloadCoreAsync"/>,
    /// so a batch here and an install queue beside it share one limit rather than each claiming
    /// it in full. Every row is started at once and the throttle decides which may move bytes;
    /// there is deliberately no second gate here, because a local semaphore fixes its capacity
    /// when constructed, and the point of the throttle is that the setting still takes effect
    /// while downloads are already in flight.
    ///
    /// Every await inside stays on the UI thread (<c>ConfigureAwait(true)</c>) - deliberately,
    /// because each row's progress handler writes to bound properties. Handing the bodies to
    /// <c>Parallel.ForEachAsync</c> instead would run them on the thread pool and those writes
    /// would come off the UI thread.
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

        var cts = new CancellationTokenSource();
        _batchCts = cts;
        _batchRows = queue;

        IsBatchRunning = true;
        BatchStatus = Loc.Format("L.OnDevice.BatchRunning", 0, queue.Count);

        // Registered before the first byte moves, so the corner circle counts this run from the
        // start and "send to background" is available for the whole of it.
        var operation = StartOperation(
            Loc.Format("L.OnDevice.BatchSubtitle", queue.Count, DeviceName),
            cts);

        var done = 0;

        try
        {
            var runs = queue.Select(async app =>
            {
                // Said out loud: a row that is ticked, has no progress bar and no button is
                // otherwise indistinguishable from one the batch forgot about. Most rows sit
                // here for a while, since the throttle only lets a few move bytes at a time.
                app.StatusText = Loc.Get("L.OnDevice.Queued");

                await DownloadCoreAsync(app, chosen: null, ct: cts.Token).ConfigureAwait(true);

                // Ticks are cleared only for what succeeded, so a second click retries the
                // failures instead of downloading everything again.
                if (app.ErrorText is null && app.SavedPath is not null) app.IsSelected = false;

                done++;
                BatchStatus = Loc.Format("L.OnDevice.BatchRunning", done, queue.Count);

                // The session is dead, so every app still waiting would fail the same way.
                // Cancelling the rest keeps the run from printing "sign in" once per app and
                // from making that many pointless round trips to Apple. The rows untick
                // themselves, since none can be downloaded until a new sign-in.
                if (!_auth.IsAuthenticated) cts.Cancel();
            }).ToList();

            await Task.WhenAll(runs).ConfigureAwait(true);

            var saved = queue.Count(a => a.SavedPath is not null);
            var failed = queue.Count(a => a.ErrorText is not null);

            BatchStatus = !_auth.IsAuthenticated
                ? Loc.Get("L.OnDevice.NeedLogin")
                : failed == 0
                    ? Loc.Format("L.OnDevice.BatchDone", saved)
                    : Loc.Format("L.OnDevice.BatchDoneWithErrors", saved, failed);

            operation?.Finish(
                cts.IsCancellationRequested ? OperationState.Cancelled
                : failed == 0 ? OperationState.Done
                : OperationState.Failed,
                BatchStatus);
        }
        catch (OperationCanceledException)
        {
            operation?.Finish(OperationState.Cancelled);
        }
        finally
        {
            IsBatchRunning = false;
            _batchCts = null;
            _batchRows = new List<InstalledAppViewModel>();
            cts.Dispose();
            RefreshSelectionState();
            OnPropertyChanged(nameof(CanMinimize));
        }
    }

    // ─────────────────── background operation plumbing ───────────────────

    /// <summary>
    /// Registers this page's work with the operations list and remembers it, so the "send to
    /// background" button has something to hand over.
    ///
    /// Finished operations are left in place rather than cleared here: the list keeps a short
    /// history on purpose, and the user should still find the run they just completed.
    /// </summary>
    private Operation? StartOperation(string subtitle, CancellationTokenSource cts)
    {
        // Only in multitasking mode. With it off OperationService keeps a single slot per kind
        // and cancels whatever held it, so registering here would make a second download on
        // this page kill the first - two rows fetched at once work fine today, and nothing in
        // the off mode has any UI to show an operation in.
        if (!_operations.MultitaskingEnabled) return null;

        _operation = _operations.Start(new Operation(
            OperationKind.Download,
            Page.OnDevice,
            Loc.Get("L.Ops.Kind.Download"),
            subtitle,
            returnDevice: TargetDevice,
            cancel: () =>
            {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { /* already finished */ }
            }));

        OnPropertyChanged(nameof(CanMinimize));
        return _operation;
    }

    /// <summary>
    /// True when there is something to send to the background: work in flight, an operation
    /// registered for it, and multitasking on. With multitasking off nothing can be backgrounded,
    /// so offering the button would be offering a control that does nothing.
    /// </summary>
    public bool CanMinimize =>
        _operations.MultitaskingEnabled
        && _operation is { IsRunning: true }
        && (IsBatchRunning || Apps.Any(a => a.IsDownloading));

    /// <summary>
    /// Leaves the downloads running and returns to where this page was opened from. The shell
    /// does the navigating; the work is untouched.
    /// </summary>
    [RelayCommand]
    private void Minimize()
    {
        if (_operation is not { IsRunning: true } operation) return;
        _operations.RequestMinimize(operation);
    }

    /// <summary>Republishes the operation's percentage from the rows it covers.</summary>
    private void PublishOperationProgress()
    {
        if (_operation is not { IsRunning: true } operation) return;

        var rows = _batchRows.Count > 0
            ? _batchRows
            : Apps.Where(a => a.IsDownloading || a.SavedPath is not null).ToList();

        if (rows.Count == 0) return;

        // Averaged over the whole batch, with a finished row counting as 100: percentages are
        // all the rows report, and file sizes are not known until each download starts.
        operation.Progress = rows.Sum(r => r.SavedPath is not null ? 100 : r.Progress) / rows.Count;
        operation.Detail = BatchStatus ?? rows.FirstOrDefault(r => r.IsDownloading)?.StatusText ?? "";
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
        LeavePage();
        _navigator?.GoBack();
    }

    /// <summary>Straight to the device list, leaving the page the same way <see cref="GoBack"/> does.</summary>
    [RelayCommand]
    private void GoHome()
    {
        LeavePage();
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
    private Task DownloadAsync(InstalledAppViewModel? item)
        => DownloadCoreAsync(item, chosen: null);

    /// <summary>
    /// The download itself. <paramref name="chosen"/> is the catalog entry the user picked
    /// after the store refused the app's own bundle id; null means resolve it as usual.
    /// </summary>
    /// <param name="ct">
    /// The batch's token, when this row is part of one. Null for a single download, which gets
    /// its own source below - a row started on its own must not be stoppable by a batch it is
    /// not in, and vice versa.
    /// </param>
    private async Task DownloadCoreAsync(
        InstalledAppViewModel? item, AppEntry? chosen, CancellationToken? ct = null)
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
        // The offer belongs to the failure that produced it; leaving it on screen through the
        // next attempt would let a stale list be clicked after a success.
        item.ClearCandidates();
        item.IsDownloading = true;
        item.Progress = 0;
        item.StatusText = Loc.Get("L.Queue.Status.Connecting");
        OnPropertyChanged(nameof(CanMinimize));

        // Linked to the batch when there is one, so cancelling the operation stops this row
        // too, while the row's own Cancel button still stops only this row.
        var cts = ct is null
            ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(ct.Value);
        item.Cancellation = cts;

        // A row downloading on its own is an operation in its own right, so it can be
        // backgrounded exactly like a batch. Inside a batch the batch already is the operation.
        var ownOperation = ct is null && !IsBatchRunning
            ? StartOperation(item.Name, cts)
            : null;

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
            var entry = chosen ?? await ResolveEntryAsync(item.App, cts.Token).ConfigureAwait(true);
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

                // The corner circle is driven from here for this page's operations: they have no
                // QueueService for OperationService's timer to read progress off.
                PublishOperationProgress();
            });

            // autoPurchase, like every other download path in the app. This screen was the
            // one caller passing false, and that single flag is why the same app downloads
            // from "Download IPA" but not from here: without it ipatool runs `download`
            // with no licence step, and the store answers "not purchased" (9610) for any
            // app whose licence record this Apple ID does not already hold. With it,
            // `download --purchase` claims the free licence first and proceeds; for an app
            // already owned the purchase is a no-op the tool reports as "already purchased".
            //
            // Nothing is bought silently by this: --purchase only ever obtains a free
            // licence, and Apple refuses it outright for a paid app, which surfaces as the
            // same "not purchased" message as before.
            // Only the byte transfer is inside the throttle. The lookups above are cheap and
            // holding a slot across them would idle a download slot on an HTTP round trip.
            using var slot = await _throttle.AcquireAsync(cts.Token).ConfigureAwait(true);

            item.StatusText = Loc.Get("L.Queue.Status.Connecting");

            var result = await _download.DownloadAsync(
                entry,
                autoPurchase: true,
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

                // Apple has rejected the stored token, so the account shown in the header is
                // no longer real. Dropping it makes the page agree with the message it just
                // printed: asking someone to sign in while their address sits at the top of
                // the same window reads as a bug, and they retry instead of signing in.
                if (result.SessionExpired) HandleSessionExpired();

                if (!string.IsNullOrWhiteSpace(result.Detail))
                    AppLog.Warn($"On-device download failed: {result.Detail}");

                // "Not found" from a bundle-id request is the one failure the catalog can still
                // answer, so the near-name matches are offered instead of stopping here.
                if (!result.SessionExpired && !result.LicenseRequired && entry.AppStoreId <= 0)
                    OfferCatalogCandidates(item);

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

            ownOperation?.Finish(
                item.SavedPath is not null ? OperationState.Done
                : item.ErrorText is not null ? OperationState.Failed
                : OperationState.Cancelled,
                item.ErrorText ?? item.StatusText ?? "");

            PublishOperationProgress();
            OnPropertyChanged(nameof(CanMinimize));
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

    /// <summary>
    /// Clears the session the store just rejected and brings the whole page in line with it.
    ///
    /// Without this the header kept naming a signed-in Apple ID while each row asked the user
    /// to sign in — a contradiction that reads as a bug, so the natural response is to retry
    /// rather than to sign in, and every retry fails the same way.
    /// </summary>
    private void HandleSessionExpired()
    {
        if (!_auth.IsAuthenticated) return;

        _auth.InvalidateSession();

        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(AccountEmail));

        foreach (var row in Apps)
            row.OnSignInStateChanged(null);

        RefreshSelectionState();
    }

    /// <summary>
    /// What happens to work in flight when the user navigates away.
    ///
    /// With multitasking on the downloads are left running: they are registered as an operation,
    /// so the corner circle keeps showing them and "return" comes back to this page - the whole
    /// point of the mode. Cancelling here was what made this screen the exception, where walking
    /// away silently threw a half-finished download away.
    ///
    /// With multitasking off there is no corner circle, so a download left running would be
    /// invisible and unstoppable, and cancelling is still the honest thing to do.
    /// </summary>
    private void LeavePage()
    {
        SaveViewPreferences();

        if (_operations.MultitaskingEnabled && _operation is { IsRunning: true })
        {
            AppLog.Info("On-device: left the page with downloads still running in the background");
            return;
        }

        CancelAll();
    }

    /// <summary>Stops every transfer in flight, including the batch that started them.</summary>
    private void CancelAll()
    {
        try { _batchCts?.Cancel(); }
        catch (ObjectDisposedException) { /* the batch finished on its own */ }

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

    /// <summary>
    /// Queues one resolved app onto the transfer being assembled, starting a new operation
    /// when there is no live one to append to.
    ///
    /// Appending matters: the user resolves ambiguous apps one at a time, and a new
    /// operation per chip would split one transfer into several competing for the same
    /// device instead of the single batch they asked for.
    /// </summary>
    private void QueueTransfer(AppEntry entry, Device destination)
    {
        // The "pick a match" notice has been acted on, so it goes now. ErrorText is otherwise
        // only cleared by a reload, which would leave the banner asking for matches that have
        // already been chosen.
        if (Apps.All(a => !a.HasCandidates)) ErrorText = null;

        // The destination check is what stops apps landing in the wrong batch.
        //
        // When every selected app needs a catalog match, TransferSelected returns before
        // starting an operation, so _pendingTransfer still points at whatever ran last — which
        // may be a transfer to a different device that is still going. The candidate chips
        // captured their own destination when they were offered, so the old test appended to
        // that unrelated batch: the item did go to the right device, but it was filed under an
        // operation titled for another one, where the user had no reason to look for it.
        if (_pendingTransfer is { IsRunning: true, Queue: not null } pending
            && _pendingTransferTo?.Udid == destination.Udid)
        {
            pending.Queue.Add(entry, destination);
            AppLog.Info($"Transfer: appended \"{entry.Name}\" to the pending batch " +
                        $"({pending.Queue.Items.Count} app(s) queued)");
            _navigator?.GoToOperation(pending);
            return;
        }

        _pendingTransfer = _operations.StartQueueOperation(
            OperationKind.Transfer,
            Page.OnDevice,
            Loc.Get("L.Ops.Kind.Transfer"),
            $"{DeviceName} → {destination.Name}",
            TargetDevice,
            q => q.Add(entry, destination));

        _pendingTransferTo = destination;

        _navigator?.GoToOperation(_pendingTransfer);
    }

    /// <summary>
    /// Shows the catalog apps whose names resemble this one, each able to proceed with its own
    /// store id. Does nothing when the catalog has no near match.
    /// </summary>
    /// <param name="transferTo">
    /// When set, picking a candidate queues a transfer to that device; otherwise it downloads
    /// the app to a folder. Without this the chips offered during a transfer would quietly do
    /// something else — save a file — instead of the install the user asked for.
    /// </param>
    private void OfferCatalogCandidates(InstalledAppViewModel item, Device? transferTo = null)
    {
        var candidates = _catalog.FindLocalCandidatesByName(item.Name);
        if (candidates.Count == 0) return;

        item.ShowCandidates(candidates.Select(entry => new CatalogCandidateViewModel(
            entry,
            new AsyncRelayCommand(() =>
            {
                if (transferTo is null)
                    return DownloadCoreAsync(item, CatalogEntryFor(item.App, entry));

                item.ClearCandidates();
                item.ErrorText = null;
                QueueTransfer(CatalogEntryFor(item.App, entry), transferTo);
                return Task.CompletedTask;
            }))));

        item.ErrorText = Loc.Get("L.OnDevice.PickCatalogMatch");
        AppLog.Info($"On-device: offering {candidates.Count} catalog match(es) for \"{item.Name}\" ({item.BundleId})");
    }

    /// <summary>
    /// The download request for an installed app addressed by a catalog entry's store id.
    ///
    /// The catalog's own name is carried, not the device's, so the log and the queue name the
    /// app that was actually requested; the saved file is still named after the device's app
    /// by the caller.
    /// </summary>
    private static AppEntry CatalogEntryFor(InstalledApp app, AppEntry catalogEntry) => new()
    {
        Name = catalogEntry.Name,
        AppStoreId = catalogEntry.AppStoreId,
        // The device's bundle id is deliberately not substituted here: it is the identifier the
        // store has just refused, and the downloader falls back to the bundle id when an id
        // request fails, which would resend the request that already failed.
        BundleId = catalogEntry.BundleId,
        LatestVersion = catalogEntry.LatestVersion ?? app.Version,
    };

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

        // The bundled catalog is a "name: id" list with no bundle identifiers in it, so an app
        // listed there cannot be reached by the only identifier a device provides. That was the
        // whole of this failure: "Сбербанк Онлайн (Оригинал)" downloads on the other screen from
        // its catalog id, while this one asked the store for ru.sberbank.onlineiphone, which the
        // store no longer lists, and reported the app as gone.
        //
        // Any match the catalog can only read one way is taken silently, exact or not: the
        // device shows "Апгрейд" while the catalog says "Альфа-Банк (Апгрейд - Умный помощник)",
        // which is one app either way. A name that fits several entries is offered to the user
        // after the attempt fails instead, because "СберБанк" alone is nine different apps.
        var unique = _catalog.FindLocalUniqueByName(app.Name);
        if (unique is not null)
        {
            AppLog.Info(
                $"On-device: {app.BundleId} is not listed; using catalog id {unique.AppStoreId} " +
                $"for \"{unique.Name}\", the only catalog app named like \"{app.Name}\"");
            return CatalogEntryFor(app, unique);
        }

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
