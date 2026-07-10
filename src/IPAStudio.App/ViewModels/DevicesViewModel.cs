using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;

namespace IPAStudio.App.ViewModels;

/// <summary>Observable wrapper around a connected device for animated cards.</summary>
public sealed partial class DeviceViewModel : ObservableObject
{
    public Device Device { get; }

    [ObservableProperty]
    private int _batteryLevel;

    /// <summary>Set briefly after connection so the card can play its pulse animation.</summary>
    [ObservableProperty]
    private bool _justConnected;

    public string Name => Device.Name;
    public string Model => Device.Model;
    public string OsVersion => Device.OsVersion;
    public string DeviceClass => Device.DeviceClass;

    public DeviceViewModel(Device device)
    {
        Device = device;
        _batteryLevel = device.BatteryLevel;
    }

    public void Refresh()
    {
        BatteryLevel = Device.BatteryLevel;
    }
}

/// <summary>
/// Main screen after login: live-discovered devices on the left and catalog
/// preload status. Selecting a device drills into the app picker.
/// </summary>
public sealed partial class DevicesViewModel : ObservableObject, IPageAware
{
    private readonly DeviceService _devices;
    private readonly CatalogService _catalog;
    private readonly AuthService _auth;
    private INavigator? _navigator;
    private bool _initialized;

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();

    /// <summary>Shared catalog list; also consumed by AppPickerViewModel.</summary>
    public List<AppEntry> Catalog { get; private set; } = new();

    [ObservableProperty]
    private int _catalogCount;

    [ObservableProperty]
    private double _catalogLoadProgress;

    [ObservableProperty]
    private bool _isCatalogLoading;

    [ObservableProperty]
    private string _accountEmail = "";

    [ObservableProperty]
    private bool _hasDevices;

    public DevicesViewModel(DeviceService devices, CatalogService catalog, AuthService auth)
    {
        _devices = devices;
        _catalog = catalog;
        _auth = auth;

        _devices.DeviceConnected += OnDeviceConnected;
        _devices.DeviceDisconnected += OnDeviceDisconnected;
        _devices.DeviceUpdated += OnDeviceUpdated;
    }

    public void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;
        AccountEmail = _auth.CurrentAccount?.Email ?? "";

        if (_initialized) return;
        _initialized = true;

        _devices.StartMonitoring();
        _ = LoadCatalogAsync();
    }

    private async Task LoadCatalogAsync()
    {
        IsCatalogLoading = true;
        try
        {
            Catalog = _catalog.LoadBundledCatalog().ToList();
            CatalogCount = Catalog.Count;

            var hadCache = await _catalog.ApplyCachedMetadataAsync(Catalog);
            _catalog.RefreshDownloadedFlags(Catalog);

            // Refresh metadata + icons in the background (instant when cached).
            var progress = new Progress<double>(p => CatalogLoadProgress = p);
            await _catalog.RefreshMetadataAsync(Catalog, progress);
        }
        catch
        {
            // Offline: bundled names/IDs still work, icons appear next time.
        }
        finally
        {
            IsCatalogLoading = false;
            CatalogLoadProgress = 100;
        }
    }

    [RelayCommand]
    private void SelectDevice(DeviceViewModel? device)
    {
        if (device is null) return;
        _navigator?.GoToAppPicker(device.Device);
    }

    [RelayCommand]
    private void OpenSettings() => _navigator?.GoTo(Page.Settings);

    [RelayCommand]
    private async Task SignOutAsync()
    {
        await _auth.LogoutAsync();
        _navigator?.GoTo(Page.Login);
    }

    private void OnDeviceConnected(object? sender, Device device)
    {
        RunOnUi(() =>
        {
            var vm = new DeviceViewModel(device) { JustConnected = true };
            Devices.Add(vm);
            HasDevices = Devices.Count > 0;

            // Clear the "just connected" flag after the entry animation window.
            _ = Task.Delay(2500).ContinueWith(_ =>
                RunOnUi(() => vm.JustConnected = false));
        });
    }

    private void OnDeviceDisconnected(object? sender, Device device)
    {
        RunOnUi(() =>
        {
            var vm = Devices.FirstOrDefault(d => d.Device.Udid == device.Udid);
            if (vm is not null) Devices.Remove(vm);
            HasDevices = Devices.Count > 0;
        });
    }

    private void OnDeviceUpdated(object? sender, Device device)
    {
        RunOnUi(() =>
        {
            Devices.FirstOrDefault(d => d.Device.Udid == device.Udid)?.Refresh();
        });
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}
