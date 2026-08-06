using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.App.Services;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;

namespace IPAStudio.App.ViewModels;

/// <summary>Observable wrapper around a catalog app for the checkbox list.</summary>
public sealed partial class AppItemViewModel : ObservableObject
{
    public AppEntry App { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isDownloaded;

    [ObservableProperty]
    private LicenseState _license;

    [ObservableProperty]
    private bool _isInstalledOnDevice;

    public string Name => App.Name;
    public string? Category => App.Category;
    public string? Developer => App.Developer;
    public string? LatestVersion => App.LatestVersion;
    public string? CachedIconPath => App.CachedIconPath;

    public AppItemViewModel(AppEntry app)
    {
        App = app;
        SyncFromModel();
    }

    public void SyncFromModel()
    {
        IsDownloaded = App.IsDownloaded;
        License = App.License;
        IsInstalledOnDevice = App.IsInstalledOnDevice;
        OnPropertyChanged(nameof(CachedIconPath));
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(LatestVersion));
    }
}

/// <summary>
/// Checkbox-based multi-select of catalog apps for a chosen device, with search,
/// category filter and live status badges (downloaded / licensed / installed).
/// </summary>
public sealed partial class AppPickerViewModel : ObservableObject, IPageAware
{
    private readonly CatalogService _catalog;
    private readonly InstallService _install;
    private readonly OperationService _operations;
    private readonly AuthService _auth;
    private INavigator? _navigator;

    public Device? TargetDevice { get; set; }

    public ObservableCollection<AppItemViewModel> Apps { get; } = new();
    public ICollectionView AppsView { get; }

    public ObservableCollection<string> Categories { get; } = new();

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string? _selectedCategory;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallSelectedCommand))]
    private int _selectedCount;

    [ObservableProperty]
    private bool _isRefreshingStatuses;

    [ObservableProperty]
    private string _deviceName = "";

    // ---- Apple ID mismatch banner ----

    /// <summary>
    /// True when the signed-in Apple ID differs from the Apple ID on the device.
    /// Shows an inline warning banner. The user can dismiss it (and proceed) or go back.
    /// </summary>
    [ObservableProperty]
    private bool _showAppleIdMismatch;

    [ObservableProperty]
    private string _deviceAppleId = "";

    [ObservableProperty]
    private string _accountAppleId = "";

    [RelayCommand]
    private void DismissMismatchWarning() => ShowAppleIdMismatch = false;

    public AppPickerViewModel(CatalogService catalog, InstallService install,
                              OperationService operations, AuthService auth)
    {
        _catalog = catalog;
        _install = install;
        _operations = operations;
        _auth = auth;

        AppsView = CollectionViewSource.GetDefaultView(Apps);
        AppsView.Filter = FilterApp;

        // Refresh icon paths and metadata on the list items as the background
        // metadata loader fills them in (runs once per session after catalog load).
        _catalog.MetadataUpdated += OnCatalogMetadataUpdated;
    }

    private void OnCatalogMetadataUpdated(object? sender, IReadOnlyList<AppEntry> updated)
    {
        // Build a lookup from the updated entries so we can patch only the affected items.
        var updatedIds = updated.Select(e => e.AppStoreId).ToHashSet();
        var affected = Apps.Where(a => updatedIds.Contains(a.App.AppStoreId)).ToList();
        if (affected.Count == 0) return;

        RunOnUi(() =>
        {
            foreach (var item in affected)
                item.SyncFromModel();
        });
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.InvokeAsync(action);
    }

    partial void OnSearchTextChanged(string value) => AppsView.Refresh();
    partial void OnSelectedCategoryChanged(string? value) => AppsView.Refresh();

    public void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;
        DeviceName = TargetDevice?.Name ?? "";

        // Check whether the signed-in Apple ID matches the device's Apple ID.
        // If they differ, show an inline warning banner so the user can decide
        // whether to proceed or abort (they might be installing on the wrong account).
        ShowAppleIdMismatch = false;
        if (TargetDevice is not null
            && !string.IsNullOrWhiteSpace(TargetDevice.AppleId)
            && _auth.CurrentAccount is not null
            && !string.Equals(
                TargetDevice.AppleId.Trim(),
                _auth.CurrentAccount.Email.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            DeviceAppleId = TargetDevice.AppleId;
            AccountAppleId = _auth.CurrentAccount.Email;
            ShowAppleIdMismatch = true;
        }

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        // 1. Load bare catalog (name + id only) — instant.
        var catalog = _catalog.LoadCatalog().ToList();

        // 2. Apply on-disk metadata cache so icons/categories appear immediately.
        await _catalog.ApplyCachedMetadataAsync(catalog).ConfigureAwait(false);

        // 3. Populate the observable list on the UI thread.
        await RunOnUiAsync(() =>
        {
            Apps.Clear();
            foreach (var entry in catalog)
            {
                var item = new AppItemViewModel(entry);
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(AppItemViewModel.IsSelected))
                        SelectedCount = Apps.Count(a => a.IsSelected);
                };
                Apps.Add(item);
            }

            Categories.Clear();
            Categories.Add("");
            foreach (var category in catalog
                         .Select(c => c.Category)
                         .Where(c => !string.IsNullOrEmpty(c))
                         .Distinct()
                         .OrderBy(c => c))
                Categories.Add(category!);
        });

        // 4. Refresh download/install badges.
        await RefreshStatusesAsync().ConfigureAwait(false);

        // 5. Background metadata refresh from iTunes API (fills missing icons and
        //    categories; hooks MetadataUpdated to push icon paths to the live list).
        _catalog.MetadataUpdated -= OnMetadataUpdated;
        _catalog.MetadataUpdated += OnMetadataUpdated;
        _ = _catalog.RefreshMetadataAsync(catalog).ConfigureAwait(false);
    }

    /// <summary>
    /// Pushes newly-loaded icon paths from the background metadata refresh back into
    /// the live AppItemViewModel list so icons appear as they are downloaded.
    /// Called from a background thread; dispatches to UI.
    /// </summary>
    private void OnMetadataUpdated(object? sender, IReadOnlyList<AppEntry> updated)
    {
        _ = RunOnUiAsync(() =>
        {
            // Build fast lookup: appStoreId -> item (cheap on 570 entries).
            var map = Apps.ToDictionary(a => a.App.AppStoreId);
            foreach (var entry in updated)
            {
                if (!map.TryGetValue(entry.AppStoreId, out var item)) continue;
                item.SyncFromModel();
            }
        });
    }

    /// <summary>Refreshes "downloaded" and "installed on device" badges.</summary>
    private async Task RefreshStatusesAsync()
    {
        if (TargetDevice is null) return;
        IsRefreshingStatuses = true;
        try
        {
            var entries = Apps.Select(a => a.App).ToList();
            _catalog.RefreshDownloadedFlags(entries);

            var installed = await _install.GetInstalledBundleIdsAsync(TargetDevice.Udid);
            foreach (var app in Apps)
            {
                app.App.IsInstalledOnDevice =
                    app.App.BundleId is not null && installed.Contains(app.App.BundleId);
                app.SyncFromModel();
            }
        }
        finally
        {
            IsRefreshingStatuses = false;
        }
    }

    private bool FilterApp(object obj)
    {
        if (obj is not AppItemViewModel app) return false;

        if (!string.IsNullOrEmpty(SelectedCategory) && app.Category != SelectedCategory)
            return false;

        if (!string.IsNullOrWhiteSpace(SearchText) &&
            !app.Name.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase))
            return false;

        return true;
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        foreach (var item in AppsView.Cast<AppItemViewModel>())
            item.IsSelected = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var item in Apps)
            item.IsSelected = false;
    }

    private bool CanInstallSelected() => SelectedCount > 0 && TargetDevice is not null;

    [RelayCommand(CanExecute = nameof(CanInstallSelected))]
    private void InstallSelected()
    {
        if (TargetDevice is null) return;

        // Installing from the catalog needs a licensed Apple ID. If the user skipped
        // sign-in, send them to the login screen now (returns to this device afterwards).
        if (!RequireSignIn()) return;

        var selected = Apps.Where(a => a.IsSelected).Select(a => a.App).ToList();
        StartInstall(q => q.Build(selected, TargetDevice));
    }

    /// <summary>
    /// Registers an install operation and opens it.
    ///
    /// Every install path goes through here so they all get an operation, which is what
    /// makes them minimisable. The device name is the subtitle because that is what tells
    /// two simultaneous installs apart in the operations list.
    /// </summary>
    private void StartInstall(Action<QueueService> build)
    {
        if (TargetDevice is null) return;

        var operation = _operations.StartQueueOperation(
            OperationKind.Install,
            Page.AppPicker,
            Loc.Get("L.Ops.Kind.Install"),
            TargetDevice.Name,
            TargetDevice,
            build);

        _navigator?.GoToOperation(operation);
    }

    /// <summary>
    /// Ensures an Apple ID is signed in before an App Store action. Returns true when
    /// already authenticated; otherwise redirects to the login screen (pre-filled for
    /// the current device) and returns false. Direct IPA installs never call this.
    /// </summary>
    private bool RequireSignIn()
    {
        if (_auth.IsAuthenticated) return true;
        if (TargetDevice is not null)
            _navigator?.GoToLoginForDevice(TargetDevice);
        else
            _navigator?.GoTo(Page.Login);
        return false;
    }

    /// <summary>
    /// IPA install mode: opens the install-from-file page, where .ipa files can be picked
    /// directly or chosen from a named library folder. Works regardless of which Apple ID is
    /// signed in, or whether one is signed in at all.
    ///
    /// A page rather than the file picker it used to open: picking files by hand meant
    /// recognising apps by file name, which for App Store archives is usually the bundle id.
    /// The page reads the real names and icons out of the archives instead.
    /// </summary>
    [RelayCommand]
    private void InstallFromIpa()
    {
        if (TargetDevice is null) return;
        _navigator?.GoToIpaCatalogs(TargetDevice);
    }

    // ---- Install by Bundle ID ----

    [ObservableProperty]
    private bool _isBundleIdPanelVisible;

    [RelayCommand]
    private void ToggleBundleIdPanel()
    {
        IsBundleIdPanelVisible = !IsBundleIdPanelVisible;
        BundleIdError = "";
    }

    [ObservableProperty]
    private string _bundleIdInput = "";

    [ObservableProperty]
    private string _bundleIdError = "";

    [ObservableProperty]
    private bool _isBundleIdBusy;

    [RelayCommand]
    private async Task InstallByBundleIdAsync()
    {
        if (TargetDevice is null) return;

        // App Store download by Bundle ID requires a licensed Apple ID.
        if (!RequireSignIn()) return;

        var raw = BundleIdInput.Trim();
        var query = AppQueryParser.Parse(raw);
        if (!query.IsValid)
        {
            BundleIdError = Loc.Get("L.Picker.BundleIdEmpty");
            return;
        }

        BundleIdError = "";
        IsBundleIdBusy = true;
        try
        {
            // 1. Try to find the app in the already-loaded catalog list (no network required).
            //    Works for both input forms: a bundle id matches on BundleId, a pasted
            //    store link or numeric id matches on AppStoreId.
            var fromCatalog = Apps
                .Select(a => a.App)
                .FirstOrDefault(e => query.Kind == AppQueryKind.BundleId
                    ? string.Equals(e.BundleId, query.BundleId, StringComparison.OrdinalIgnoreCase)
                    : e.AppStoreId == query.AppStoreId);

            if (fromCatalog is not null)
            {
                StartInstall(q => q.Build(new[] { fromCatalog }, TargetDevice));
                return;
            }

            // 2. Not in catalog: look the app up in the App Store via the iTunes Lookup API.
            var results = await _catalog.FindAsync(query).ConfigureAwait(false);
            if (results is null || results.Count == 0)
            {
                BundleIdError = Loc.Format("L.Picker.BundleIdNotFound", raw);
                return;
            }

            var app = results[0];

            // A bundle id missing from the public catalog is queued like any other. An earlier
            // version refused it here, reasoning that ipatool resolves "-b" through that same
            // catalog and so the job was doomed. That reasoning was wrong in practice: these
            // downloads do succeed, because ipatool queries the storefront tied to the signed-in
            // account, which still answers for apps the anonymous sweep above cannot see.
            //
            // Refusing up front removed the only way to fetch a delisted app, which is precisely
            // what this tool is for. The store is the authority on what it will hand over, so the
            // attempt goes through and the store's own answer decides.
            StartInstall(q => q.Build(new[] { app }, TargetDevice));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Bundle ID lookup failed for '{raw}': {ex.Message}");
            BundleIdError = Loc.Get("L.Picker.BundleIdLookupFailed");
        }
        finally
        {
            IsBundleIdBusy = false;
        }
    }

    private static Task RunOnUiAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) { action(); return Task.CompletedTask; }
        return dispatcher.InvokeAsync(action).Task;
    }

    // Goes back through the shell's history rather than jumping straight to the device list.
    // A forward navigation to Devices also pushed a history entry, so the button both ignored
    // where the user came from and left a step behind that Back would later have to unwind.
    [RelayCommand]
    private void GoBack() => _navigator?.GoBack();

    /// <summary>Straight to the device list, skipping the whole path taken to get here.</summary>
    [RelayCommand]
    private void GoHome() => _navigator?.GoHome();

    /// <summary>
    /// Opens the list of apps already installed on the target device.
    ///
    /// Guarded inside the body rather than gated by CanExecute: the device is assigned
    /// during navigation, and a CanExecute that is never re-queried afterwards would risk
    /// leaving the button permanently dead.
    /// </summary>
    [RelayCommand]
    private void OpenOnDevice()
    {
        if (TargetDevice is not null)
            _navigator?.GoToOnDevice(TargetDevice);
    }
}
