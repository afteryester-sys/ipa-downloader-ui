using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.App.Services;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;
using Microsoft.Win32;

namespace IPAStudio.App.ViewModels;

public sealed partial class FirmwareViewModel : ObservableObject, IPageAware
{
    private readonly FirmwareCatalogService _catalog;
    private readonly FirmwareDownloadService _downloads;
    private readonly SettingsService _settings;
    private readonly OperationService _operations;
    private INavigator? _navigator;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _downloadCts;
    private Operation? _operation;
    private List<FirmwareDevice> _allDevices = new();
    public IReadOnlyList<FirmwareDevice> AllDevices => _allDevices;

    public ObservableCollection<FirmwareDevice> CatalogDevices { get; } = new();
    public ObservableCollection<FirmwareDevice> MyDevices { get; } = new();
    public ObservableCollection<FirmwareRelease> Firmwares { get; } = new();

    public FirmwareViewModel(FirmwareCatalogService catalog, FirmwareDownloadService downloads,
        SettingsService settings, OperationService operations)
    {
        _catalog = catalog;
        _downloads = downloads;
        _settings = settings;
        _operations = operations;
        DestinationFolder = settings.Current.FirmwareFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "IPA Studio", "Firmwares");
        SegmentCount = Math.Clamp(settings.Current.FirmwareDownloadThreads, 1, 8);
        AutoCheckIntervalHours = Math.Clamp(settings.Current.FirmwareCheckIntervalHours, 1, 168);
    }

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private FirmwareDevice? _selectedCatalogDevice;
    [ObservableProperty] private FirmwareDevice? _selectedDevice;
    [ObservableProperty] private FirmwareRelease? _selectedFirmware;
    [ObservableProperty] private string _destinationFolder = "";
    [ObservableProperty] private int _segmentCount = 4;
    [ObservableProperty] private bool _signedOnly = true;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private string? _savedPath;
    [ObservableProperty] private bool _autoUpdateSelected;
    [ObservableProperty] private int _autoCheckIntervalHours = 6;

    partial void OnAutoCheckIntervalHoursChanged(int value)
    {
        _settings.Current.FirmwareCheckIntervalHours = Math.Clamp(value, 1, 168);
        _settings.Save();
    }

    partial void OnSearchTextChanged(string value) => ApplyDeviceFilter();
    partial void OnSignedOnlyChanged(bool value) => ApplyFirmwareFilter();
    partial void OnSelectedFirmwareChanged(FirmwareRelease? value) => ToggleDownloadCommand.NotifyCanExecuteChanged();
    partial void OnSelectedDeviceChanged(FirmwareDevice? value)
    {
        AutoUpdateSelected = value is not null;
        RemoveDeviceCommand.NotifyCanExecuteChanged();
        ToggleDownloadCommand.NotifyCanExecuteChanged();
        _ = LoadFirmwaresAsync(value);
    }

    partial void OnIsDownloadingChanged(bool value) => ToggleDownloadCommand.NotifyCanExecuteChanged();

    partial void OnSelectedCatalogDeviceChanged(FirmwareDevice? value) => AddDeviceCommand.NotifyCanExecuteChanged();

    public async void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;
        if (_allDevices.Count == 0) await LoadDevicesAsync();
    }

    [RelayCommand] private void GoBack() => _navigator?.GoBack();
    [RelayCommand] private void GoHome() => _navigator?.GoHome();

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog { InitialDirectory = Directory.Exists(DestinationFolder) ? DestinationFolder : null };
        if (dialog.ShowDialog() == true) DestinationFolder = dialog.FolderName;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _allDevices.Clear();
        await LoadDevicesAsync();
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        if (SelectedDevice is null || SelectedFirmware is null) return;
        Directory.CreateDirectory(DestinationFolder);
        _settings.Current.FirmwareFolder = DestinationFolder;
        _settings.Current.FirmwareDownloadThreads = SegmentCount;
        _settings.Save();

        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        IsPaused = false;
        ErrorText = null;
        SavedPath = null;
        Progress = 0;
        var device = SelectedDevice;
        var firmware = SelectedFirmware;
        _operation = _operations.Start(new Operation(OperationKind.Firmware, Page.Firmware,
            Loc.Get("L.Firmware.Operation"), $"{device.Name} {firmware.Version}", cancel: Pause));

        var reporter = new Progress<FirmwareDownloadProgress>(p =>
        {
            Progress = p.Percent;
            var speed = p.BytesPerSecond / 1024d / 1024d;
            StatusText = $"{p.Downloaded / 1024d / 1024d:F0} / {p.Total / 1024d / 1024d:F0} MB · {speed:F1} MB/s";
            if (_operation is not null) { _operation.Progress = p.Percent; _operation.Detail = StatusText; }
        });

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    SavedPath = await _downloads.DownloadAsync(device, firmware, DestinationFolder, SegmentCount, reporter, _downloadCts.Token);
                    break;
                }
                catch (Exception ex) when ((ex is HttpRequestException or IOException or EndOfStreamException) && attempt < 12 && !_downloadCts.IsCancellationRequested)
                {
                    StatusText = string.Format(Loc.Get("L.Firmware.Reconnecting"), Math.Min(30, 1 << Math.Min(attempt, 5)));
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(attempt, 5))), _downloadCts.Token);
                }
            }
            StatusText = Loc.Get("L.Firmware.Done");
            _operation?.Finish(OperationState.Done, StatusText);
            RecordCompletedAutoUpdate(device, firmware, SavedPath);
        }
        catch (OperationCanceledException)
        {
            IsPaused = true;
            StatusText = Loc.Get("L.Firmware.Paused");
            _operation?.Finish(OperationState.Cancelled, StatusText);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            StatusText = Loc.Get("L.Firmware.Failed");
            _operation?.Finish(OperationState.Failed, ex.Message);
        }
        finally
        {
            IsDownloading = false;
            DownloadCommand.NotifyCanExecuteChanged();
            ToggleDownloadCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanDownload() => SelectedDevice is not null && SelectedFirmware is not null && !IsDownloading;

    private bool CanToggleDownload() => IsDownloading || (SelectedDevice is not null && SelectedFirmware is not null);

    [RelayCommand(CanExecute = nameof(CanToggleDownload))]
    private async Task ToggleDownloadAsync()
    {
        if (IsDownloading) Pause();
        else await DownloadAsync();
    }

    [RelayCommand] private void Pause() => _downloadCts?.Cancel();

    [RelayCommand]
    private async Task StopAndDeleteAsync()
    {
        var device = SelectedDevice;
        var firmware = SelectedFirmware;
        _downloadCts?.Cancel();
        while (IsDownloading) await Task.Delay(50);
        if (device is not null && firmware is not null)
        {
            var destination = Path.Combine(DestinationFolder, FirmwareDownloadService.BuildFileName(device.Name, firmware.Version));
            _downloads.DeleteTemporaryFiles(destination);
        }
        IsPaused = false;
        Progress = 0;
        StatusText = Loc.Get("L.Firmware.Stopped");
    }

    public void AddDevices(IEnumerable<FirmwareDevice> devices)
    {
        foreach (var device in devices.Where(candidate => MyDevices.All(d => d.Identifier != candidate.Identifier)))
        {
            _settings.Current.FirmwareSubscriptions.Add(new FirmwareSubscription { Identifier = device.Identifier, DeviceName = device.Name });
            MyDevices.Add(device);
        }
        _settings.Save();
        SelectedDevice ??= MyDevices.FirstOrDefault();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Directory.Exists(DestinationFolder))
            Process.Start(new ProcessStartInfo("explorer.exe", DestinationFolder) { UseShellExecute = true });
    }

    private bool CanAddDevice() => SelectedCatalogDevice is not null &&
        MyDevices.All(d => d.Identifier != SelectedCatalogDevice.Identifier);

    [RelayCommand(CanExecute = nameof(CanAddDevice))]
    private void AddDevice()
    {
        if (SelectedCatalogDevice is null) return;
        var device = SelectedCatalogDevice;
        _settings.Current.FirmwareSubscriptions.Add(new FirmwareSubscription
        {
            Identifier = device.Identifier,
            DeviceName = device.Name,
        });
        _settings.Save();
        MyDevices.Add(device);
        SelectedDevice = device;
        AddDeviceCommand.NotifyCanExecuteChanged();
    }

    private bool CanRemoveDevice() => SelectedDevice is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveDevice))]
    private void RemoveDevice()
    {
        if (SelectedDevice is null) return;
        var device = SelectedDevice;
        var existing = _settings.Current.FirmwareSubscriptions.FirstOrDefault(s => s.Identifier == device.Identifier);
        if (existing is not null) _settings.Current.FirmwareSubscriptions.Remove(existing);
        _settings.Save();
        MyDevices.Remove(device);
        SelectedDevice = MyDevices.FirstOrDefault();
        AddDeviceCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadDevicesAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        IsLoading = true;
        ErrorText = null;
        try
        {
            _allDevices = (await _catalog.GetDevicesAsync(_loadCts.Token)).ToList();
            RestoreMyDevices();
            ApplyDeviceFilter();
            StatusText = string.Format(Loc.Get("L.Firmware.DevicesLoaded"), _allDevices.Count);
        }
        catch (Exception ex) { ErrorText = ex.Message; }
        finally { IsLoading = false; }
    }

    private async Task LoadFirmwaresAsync(FirmwareDevice? device)
    {
        Firmwares.Clear();
        SelectedFirmware = null;
        if (device is null) return;
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        IsLoading = true;
        try
        {
            var details = await _catalog.GetDeviceAsync(device.Identifier, _loadCts.Token);
            _deviceFirmwares = details.Firmwares.OrderByDescending(f => f.ReleaseDate ?? f.UploadDate).ToList();
            ApplyFirmwareFilter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ErrorText = ex.Message; }
        finally { IsLoading = false; }
    }

    private List<FirmwareRelease> _deviceFirmwares = new();

    private void RestoreMyDevices()
    {
        var selectedId = SelectedDevice?.Identifier;
        MyDevices.Clear();
        foreach (var subscription in _settings.Current.FirmwareSubscriptions)
        {
            var device = _allDevices.FirstOrDefault(d => d.Identifier == subscription.Identifier) ?? new FirmwareDevice
            {
                Identifier = subscription.Identifier,
                Name = string.IsNullOrWhiteSpace(subscription.DeviceName) ? subscription.Identifier : subscription.DeviceName,
            };
            subscription.DeviceName = device.Name;
            MyDevices.Add(device);
        }
        _settings.Save();
        SelectedDevice = MyDevices.FirstOrDefault(d => d.Identifier == selectedId) ?? MyDevices.FirstOrDefault();
    }

    private void ApplyDeviceFilter()
    {
        var query = SearchText.Trim();
        var filtered = string.IsNullOrEmpty(query) ? Enumerable.Empty<FirmwareDevice>() : _allDevices.Where(d =>
            d.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            d.Identifier.Contains(query, StringComparison.OrdinalIgnoreCase));
        CatalogDevices.Clear();
        foreach (var device in filtered.Take(80)) CatalogDevices.Add(device);
        SelectedCatalogDevice = CatalogDevices.FirstOrDefault();
        AddDeviceCommand.NotifyCanExecuteChanged();
    }

    private void ApplyFirmwareFilter()
    {
        Firmwares.Clear();
        foreach (var firmware in _deviceFirmwares.Where(f => !SignedOnly || f.Signed)) Firmwares.Add(firmware);
        SelectedFirmware = Firmwares.FirstOrDefault();
        DownloadCommand.NotifyCanExecuteChanged();
    }

    private void RecordCompletedAutoUpdate(FirmwareDevice device, FirmwareRelease firmware, string path)
    {
        var subscription = _settings.Current.FirmwareSubscriptions.FirstOrDefault(s => s.Identifier == device.Identifier);
        if (subscription is null) return;
        var oldPath = subscription.LastFilePath;
        subscription.LastBuildId = firmware.BuildId;
        subscription.LastFilePath = path;
        _settings.Save();
        if (!string.IsNullOrWhiteSpace(oldPath) && !string.Equals(oldPath, path, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
        {
            try { File.Delete(oldPath); } catch { }
        }
    }
}
