using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly QueueService _queue;
    private readonly AuthService _auth;
    private readonly DownloadService _download;
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

    public AppPickerViewModel(CatalogService catalog, InstallService install, QueueService queue, AuthService auth, DownloadService download)
    {
        _catalog = catalog;
        _install = install;
        _queue = queue;
        _auth = auth;
        _download = download;

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
        var catalog = _catalog.LoadBundledCatalog().ToList();

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

        var selected = Apps.Where(a => a.IsSelected).Select(a => a.App).ToList();
        _queue.Build(selected, TargetDevice);
        _navigator?.GoTo(Page.Queue);
    }

    /// <summary>
    /// IPA install mode: open a Windows file picker, select one or more .ipa files
    /// and install them directly onto the device without touching the App Store.
    /// Works regardless of which Apple ID is signed in (or whether one is signed in at all).
    /// </summary>
    [RelayCommand]
    private void InstallFromIpa()
    {
        if (TargetDevice is null) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите IPA для установки",
            Filter = "Файлы IPA|*.ipa|Все файлы|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0) return;

        _queue.BuildFromIpaFiles(dialog.FileNames, TargetDevice);
        _navigator?.GoTo(Page.Queue);
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

        var bundleId = BundleIdInput.Trim();
        if (string.IsNullOrEmpty(bundleId))
        {
            BundleIdError = "Введите Bundle ID, например: com.apple.mobilemail";
            return;
        }

        BundleIdError = "";
        IsBundleIdBusy = true;
        try
        {
            // 1. Try to find the app in the already-loaded catalog list (no network required).
            var fromCatalog = Apps
                .Select(a => a.App)
                .FirstOrDefault(e => string.Equals(e.BundleId, bundleId, StringComparison.OrdinalIgnoreCase));

            if (fromCatalog is not null)
            {
                _queue.Build(new[] { fromCatalog }, TargetDevice);
                _navigator?.GoTo(Page.Queue);
                return;
            }

            // 2. Not in catalog: search the App Store by bundle ID via iTunes Lookup API.
            var results = await _catalog.SearchByBundleIdAsync(bundleId).ConfigureAwait(false);
            if (results is null || results.Count == 0)
            {
                BundleIdError = $"Приложение с Bundle ID «{bundleId}» не найдено в App Store.";
                return;
            }

            var app = results[0];
            _queue.Build(new[] { app }, TargetDevice);
            _navigator?.GoTo(Page.Queue);
        }
        catch (Exception ex)
        {
            BundleIdError = $"Ошибка поиска: {ex.Message}";
        }
        finally
        {
            IsBundleIdBusy = false;
        }
    }

    // ---- Load Purchased / Downloaded apps from Apple ID ----

    [ObservableProperty]
    private bool _isLoadingPurchased;

    [ObservableProperty]
    private string _purchasedStatusMessage = "";

    /// <summary>
    /// Fetches all apps purchased or previously downloaded under the signed-in
    /// Apple ID via ipatool (purchase-history / library command). Replaces the
    /// current list and marks the "Licensed" state so users can queue them for
    /// re-download/re-install in one click.
    /// </summary>
    [RelayCommand]
    private async Task LoadPurchasedAppsAsync()
    {
        if (IsLoadingPurchased) return;

        if (!_auth.IsAuthenticated)
        {
            PurchasedStatusMessage = "Войдите в Apple ID чтобы просмотреть купленные приложения.";
            return;
        }

        IsLoadingPurchased = true;
        PurchasedStatusMessage = "Загружаем список купленных приложений…";

        try
        {
            var purchased = await _download.ListPurchasedAsync().ConfigureAwait(false);

            await RunOnUiAsync(() =>
            {
                Apps.Clear();
                Categories.Clear();

                if (purchased.Count == 0)
                {
                    PurchasedStatusMessage = "Купленные приложения не найдены (или команда не поддерживается текущей версией ipatool).";
                    return;
                }

                var cats = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var app in purchased)
                {
                    // Mark every app as owned since we got it from the purchase history.
                    app.License = LicenseState.Owned;
                    Apps.Add(new AppItemViewModel(app));
                    if (!string.IsNullOrEmpty(app.Category)) cats.Add(app.Category!);
                }
                foreach (var c in cats) Categories.Add(c);

                PurchasedStatusMessage = $"Найдено купленных приложений: {purchased.Count}";
            });
        }
        catch (Exception ex)
        {
            PurchasedStatusMessage = $"Ошибка загрузки: {ex.Message}";
        }
        finally
        {
            IsLoadingPurchased = false;
        }
    }

    private static Task RunOnUiAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) { action(); return Task.CompletedTask; }
        return dispatcher.InvokeAsync(action).Task;
    }

    [RelayCommand]
    private void GoBack() => _navigator?.GoTo(Page.Devices);
}
