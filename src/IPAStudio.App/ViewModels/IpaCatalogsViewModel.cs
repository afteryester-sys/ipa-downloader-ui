using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
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

/// <summary>One .ipa row inside the selected catalog.</summary>
public sealed partial class CatalogIpaViewModel : ObservableObject
{
    public CatalogIpaViewModel(IpaCatalogItem item)
    {
        Item = item;
        Name = item.Name;
    }

    public IpaCatalogItem Item { get; }

    public string Name { get; }
    public string Path => Item.Path;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private ImageSource? _icon;

    /// <summary>
    /// The name on disk. Shown beside the app name and searched alongside it, because archives
    /// are often saved under something quite different from the app's own title - a bundle id,
    /// or a name the downloader chose - and searching for what is visible in Explorer has to
    /// find the row. Note this deliberately keeps the extension: it is what the folder shows.
    /// </summary>
    public string FileName => System.IO.Path.GetFileName(Item.Path);

    /// <summary>
    /// Hidden when the file is merely the app name plus ".ipa", where repeating it would add a
    /// second copy of the same words and only make the row noisier.
    /// </summary>
    public bool ShowFileName =>
        !string.Equals(System.IO.Path.GetFileNameWithoutExtension(Item.Path), Name,
            StringComparison.CurrentCultureIgnoreCase);

    /// <summary>
    /// True when the archive could not be read. Such a file is still listed — it is visibly
    /// there in the folder — but it cannot be selected, because installing it is a guaranteed
    /// failure and the reason is clearer stated here than in a failed operation.
    /// </summary>
    public bool IsUnreadable => string.IsNullOrWhiteSpace(Item.BundleId);

    /// <summary>Only a readable archive can be picked for installing.</summary>
    public bool CanSelect => !IsUnreadable;

    /// <summary>
    /// Version and size on one line, with the bundle id left out: it repeats for every build of
    /// the same app and is not what the user is scanning the list for.
    /// </summary>
    public string Details
    {
        get
        {
            if (IsUnreadable) return Loc.Get("L.Catalogs.Unreadable");

            var size = FormatSize(Item.SizeBytes);
            return string.IsNullOrWhiteSpace(Item.Version) ? size : $"{Item.Version} · {size}";
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        double value = bytes;
        string[] units = { "Б", "КБ", "МБ", "ГБ" };
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit >= 2 ? $"{value:0.#} {units[unit]}" : $"{value:0} {units[unit]}";
    }
}

/// <summary>One catalog in the left-hand list.</summary>
public sealed partial class CatalogEntryViewModel : ObservableObject
{
    public CatalogEntryViewModel(IpaCatalog catalog)
    {
        Catalog = catalog;
        Refresh();
    }

    public IpaCatalog Catalog { get; }
    public string Id => Catalog.Id;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _summary = "";

    /// <summary>
    /// True when the folder is gone. The entry stays visible: the name is the user's own, and
    /// silently dropping it would look like the app lost their library.
    /// </summary>
    [ObservableProperty]
    private bool _isMissing;

    public void Refresh()
    {
        Name = Catalog.Name;
        IsMissing = !string.IsNullOrWhiteSpace(Catalog.Folder) && !Directory.Exists(Catalog.Folder);

        Summary = IsMissing
            ? Loc.Get("L.Catalogs.FolderMissing")
            : Loc.Format("L.Catalogs.Count", Catalog.Items.Count);
    }
}

/// <summary>
/// The IPA libraries page: named folders of local .ipa files, listed with their real app names
/// and icons, installable onto a connected device without going through the App Store.
/// </summary>
public sealed partial class IpaCatalogsViewModel : ObservableObject, IPageAware
{
    private readonly IpaCatalogService _catalogs;
    private readonly DeviceService _devices;
    private readonly OperationService _operations;

    private INavigator? _navigator;

    public IpaCatalogsViewModel(
        IpaCatalogService catalogs,
        DeviceService devices,
        OperationService operations)
    {
        _catalogs = catalogs;
        _devices = devices;
        _operations = operations;

        // Model and iOS version arrive after the device is first reported, so the target list
        // would otherwise sit on the bare fallback name for as long as the page stays open.
        // Device carries no change notification of its own, so the row is replaced instead.
        _devices.DeviceUpdated += OnDeviceUpdated;
        _devices.DeviceConnected += OnDeviceListChanged;
        _devices.DeviceDisconnected += OnDeviceListChanged;
    }

    private void OnDeviceListChanged(object? sender, Device device) =>
        Application.Current?.Dispatcher.Invoke(LoadDevices);

    /// <summary>
    /// Label each device was last shown with. The service updates devices in place, so the
    /// object in the list and the one in the event are the same instance - there is nothing to
    /// compare it against without remembering what was displayed.
    /// </summary>
    private readonly Dictionary<string, string> _shownLabels = new();

    private void OnDeviceUpdated(object? sender, Device device)
    {
        // Battery level is polled continuously. Rebuilding on every update would close the
        // dropdown under the user's cursor for a change this list does not even show.
        if (_shownLabels.TryGetValue(device.Udid, out var shown) && shown == device.DisplayLabel)
            return;

        Application.Current?.Dispatcher.Invoke(LoadDevices);
    }

    public ObservableCollection<CatalogEntryViewModel> Catalogs { get; } = new();
    public ObservableCollection<CatalogIpaViewModel> Items { get; } = new();

    /// <summary>Devices to install onto, refreshed each time the page is opened.</summary>
    public ObservableCollection<Device> Devices { get; } = new();

    [ObservableProperty]
    private Device? _selectedDevice;

    [ObservableProperty]
    private CatalogEntryViewModel? _selectedCatalog;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string _searchText = "";

    /// <summary>Whether the inline rename box is showing.</summary>
    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _renameText = "";

    /// <summary>All rows of the selected catalog, before the search box filters them.</summary>
    private readonly List<CatalogIpaViewModel> _allItems = new();

    public bool HasCatalogs => Catalogs.Count > 0;
    public bool IsEmpty => !IsScanning && SelectedCatalog is not null && Items.Count == 0;

    public int SelectedCount => _allItems.Count(i => i.IsSelected);
    public bool HasSelection => SelectedCount > 0;

    public string InstallSelectedLabel => Loc.Format("L.Catalogs.InstallSelected", SelectedCount);

    /// <summary>
    /// The device this page was opened for. Preselected in the target list so arriving from a
    /// device's app list does not ask again which device was meant.
    /// </summary>
    private Device? _arrivedFor;

    public void SetDevice(Device device) => _arrivedFor = device;

    public void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;

        LoadCatalogs();
        LoadDevices();
    }

    /// <summary>
    /// Picks .ipa files straight from disk, without adding their folder as a library — the
    /// one-off case the page has to keep serving.
    /// </summary>
    [RelayCommand]
    private void PickFiles()
    {
        var device = SelectedDevice;
        if (device is null) return;

        var dialog = new OpenFileDialog
        {
            Title = Loc.Get("L.Dialog.PickIpaTitle"),
            Filter = Loc.Get("L.Dialog.IpaFilter"),
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0) return;

        StartInstall(dialog.FileNames, device);
    }

    partial void OnSelectedDeviceChanged(Device? value)
    {
        InstallSelectedCommand.NotifyCanExecuteChanged();
        PickFilesCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasDevice));
    }

    /// <summary>Whether a device is available to install onto at all.</summary>
    public bool HasDevice => SelectedDevice is not null;

    private void LoadCatalogs()
    {
        var previous = SelectedCatalog?.Id;

        Catalogs.Clear();
        foreach (var catalog in _catalogs.Catalogs)
            Catalogs.Add(new CatalogEntryViewModel(catalog));

        OnPropertyChanged(nameof(HasCatalogs));

        // The previous selection is restored so returning from an install does not silently
        // reset the page to the first library.
        SelectedCatalog = Catalogs.FirstOrDefault(c => c.Id == previous) ?? Catalogs.FirstOrDefault();
    }

    private void LoadDevices()
    {
        var previous = SelectedDevice?.Udid;

        Devices.Clear();
        _shownLabels.Clear();
        foreach (var device in _devices.ConnectedDevices)
        {
            Devices.Add(device);
            _shownLabels[device.Udid] = device.DisplayLabel;
        }

        // The device the page was opened for wins, then whatever was chosen last, then the
        // first one connected.
        SelectedDevice =
            Devices.FirstOrDefault(d => d.Udid == _arrivedFor?.Udid)
            ?? Devices.FirstOrDefault(d => d.Udid == previous)
            ?? Devices.FirstOrDefault();
    }

    /// <summary>
    /// Switching library shows what the last scan found, without touching the disk. Rescanning
    /// on every click would make the list flicker and re-read hundreds of archives for nothing.
    /// </summary>
    partial void OnSelectedCatalogChanged(CatalogEntryViewModel? value)
    {
        ShowItems(value?.Catalog);
        StatusText = value is null ? null : ScannedLine(value.Catalog);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ShowItems(IpaCatalog? catalog)
    {
        foreach (var row in _allItems)
            row.PropertyChanged -= OnRowChanged;

        _allItems.Clear();

        if (catalog is not null)
        {
            foreach (var item in catalog.Items)
            {
                var row = new CatalogIpaViewModel(item);
                row.PropertyChanged += OnRowChanged;
                _allItems.Add(row);
            }
        }

        ApplyFilter();
        _ = LoadIconsAsync(_allItems.ToList());
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CatalogIpaViewModel.IsSelected)) return;

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(InstallSelectedLabel));
        InstallSelectedCommand.NotifyCanExecuteChanged();
    }

    private void ApplyFilter()
    {
        var query = SearchText?.Trim() ?? "";

        Items.Clear();
        foreach (var row in _allItems)
        {
            // The file name is searched too: an archive is frequently stored under a name that
            // has nothing to do with the app's title, and typing what the folder shows must
            // find it. CurrentCulture for the two human-readable names so Russian matches
            // case-insensitively; Ordinal for the bundle id, which is ASCII by definition.
            if (query.Length > 0 &&
                !row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) &&
                !row.FileName.Contains(query, StringComparison.CurrentCultureIgnoreCase) &&
                !row.Item.BundleId.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            Items.Add(row);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Decodes the cached icons off the UI thread and hands each one over frozen, so a library
    /// of several hundred archives does not stall the list while it appears.
    /// </summary>
    private async Task LoadIconsAsync(List<CatalogIpaViewModel> rows)
    {
        foreach (var row in rows)
        {
            var path = row.Item.IconPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;

            var image = await Task.Run(() =>
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    // Shown at 40 px; decoding at 80 keeps it crisp on a scaled display without
                    // holding a full 180 px bitmap per row.
                    bitmap.DecodePixelWidth = 80;
                    bitmap.UriSource = new Uri(path);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return (ImageSource)bitmap;
                }
                catch
                {
                    // An unreadable icon leaves the row on its letter tile.
                    return null;
                }
            }).ConfigureAwait(true);

            if (image is not null) row.Icon = image;
        }
    }

    private static string ScannedLine(IpaCatalog catalog) =>
        catalog.ScannedAt is null
            ? Loc.Get("L.Catalogs.NeverScanned")
            : Loc.Format("L.Catalogs.ScannedAt", catalog.ScannedAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm"));

    // ───────────────────────────────── commands ─────────────────────────────────

    /// <summary>
    /// Adds a folder as a library. The name is asked for in the inline panel afterwards rather
    /// than in a second dialog, matching how the rest of the app takes text input.
    /// </summary>
    [RelayCommand]
    private void AddCatalog()
    {
        var dialog = new OpenFolderDialog { Title = Loc.Get("L.Catalogs.PickFolder") };
        if (dialog.ShowDialog() != true) return;

        // Empty name: the service falls back to the folder's own name, so adding a library is
        // one click and naming it stays optional.
        var catalog = _catalogs.Add("", dialog.FolderName);

        LoadCatalogs();
        SelectedCatalog = Catalogs.FirstOrDefault(c => c.Id == catalog.Id);

        // Scanned straight away: a library that looks empty until the user finds Refresh
        // reads as broken.
        _ = ScanAsync();
    }

    /// <summary>Opens the rename panel, pre-filled with the current name.</summary>
    [RelayCommand]
    private void StartRename()
    {
        if (SelectedCatalog is null) return;

        RenameText = SelectedCatalog.Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename()
    {
        IsRenaming = false;
        RenameText = "";
    }

    [RelayCommand]
    private void CommitRename()
    {
        if (SelectedCatalog is null || string.IsNullOrWhiteSpace(RenameText))
        {
            IsRenaming = false;
            return;
        }

        _catalogs.Rename(SelectedCatalog.Id, RenameText);
        SelectedCatalog.Refresh();

        IsRenaming = false;
        RenameText = "";
    }

    /// <summary>
    /// Forgets a library. Only the entry goes: the folder and the .ipa files in it are the
    /// user's own, and the confirmation says so, because "Remove" on a page full of files is
    /// otherwise a fair thing to hesitate over.
    /// </summary>
    [RelayCommand]
    private void RemoveCatalog()
    {
        if (SelectedCatalog is null) return;

        var confirm = MessageBox.Show(
            Loc.Format("L.Catalogs.RemoveBody", SelectedCatalog.Name),
            Loc.Get("L.Catalogs.RemoveTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        _catalogs.Remove(SelectedCatalog.Id);
        LoadCatalogs();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var folder = SelectedCatalog?.Catalog.Folder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not open '{folder}': {ex.Message}");
        }
    }

    /// <summary>Rereads the selected folder.</summary>
    [RelayCommand]
    private async Task ScanAsync()
    {
        if (SelectedCatalog is null || IsScanning) return;

        IsScanning = true;
        OnPropertyChanged(nameof(IsEmpty));
        StatusText = Loc.Get("L.Catalogs.Scanning");

        try
        {
            var catalog = await _catalogs.ScanAsync(SelectedCatalog.Id).ConfigureAwait(true);

            SelectedCatalog.Refresh();

            if (catalog is not null)
            {
                ShowItems(catalog);
                StatusText = ScannedLine(catalog);
            }
        }
        finally
        {
            IsScanning = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        // Unreadable archives are skipped: ticking them would only queue certain failures.
        var selectable = Items.Where(i => i.CanSelect).ToList();
        if (selectable.Count == 0) return;

        var target = selectable.Any(i => !i.IsSelected);
        foreach (var row in selectable)
            row.IsSelected = target;
    }

    private bool CanInstall() => HasSelection && SelectedDevice is not null;

    /// <summary>
    /// Installs the ticked archives onto the chosen device, through the same queue every other
    /// install path uses — so it is minimisable and verified on the device afterwards.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private void InstallSelected()
    {
        var device = SelectedDevice;
        if (device is null) return;

        var paths = _allItems.Where(i => i.IsSelected).Select(i => i.Path).ToList();
        if (paths.Count == 0) return;

        StartInstall(paths, device);
    }

    /// <summary>
    /// Registers the install as an operation and opens it, so installs started here are
    /// minimisable and listed exactly like the ones started from the App Store list.
    /// </summary>
    private void StartInstall(IReadOnlyList<string> paths, Device device)
    {
        var operation = _operations.StartQueueOperation(
            OperationKind.Install,
            Page.IpaCatalogs,
            Loc.Get("L.Ops.Kind.Install"),
            device.Name,
            device,
            q => q.BuildFromIpaFiles(paths, device));

        _navigator?.GoToOperation(operation);
    }

    [RelayCommand]
    private void GoBack() => _navigator?.GoBack();

    [RelayCommand]
    private void GoHome() => _navigator?.GoHome();
}
