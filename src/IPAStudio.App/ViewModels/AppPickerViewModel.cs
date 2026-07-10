using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;
using Microsoft.Extensions.DependencyInjection;

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

    public AppPickerViewModel(CatalogService catalog, InstallService install, QueueService queue)
    {
        _catalog = catalog;
        _install = install;
        _queue = queue;

        AppsView = CollectionViewSource.GetDefaultView(Apps);
        AppsView.Filter = FilterApp;
    }

    partial void OnSearchTextChanged(string value) => AppsView.Refresh();
    partial void OnSelectedCategoryChanged(string? value) => AppsView.Refresh();

    public void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;
        DeviceName = TargetDevice?.Name ?? "";
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        // Reuse the catalog already loaded by DevicesViewModel.
        var devicesVm = App.Services.GetRequiredService<DevicesViewModel>();
        var catalog = devicesVm.Catalog;

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

        await RefreshStatusesAsync();
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

    [RelayCommand]
    private void GoBack() => _navigator?.GoTo(Page.Devices);
}
