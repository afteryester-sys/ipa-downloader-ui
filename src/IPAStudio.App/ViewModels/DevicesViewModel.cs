using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.App.Services;
using IPAStudio.App.Views;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Localization;
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

    /// <summary>
    /// Whether this device is currently on Wi-Fi rather than a cable.
    ///
    /// Observable rather than read straight off the model, because a device that is
    /// unplugged while also on Wi-Fi stays connected and merely changes transport; the
    /// card has to be able to follow that without the device disappearing and coming back.
    /// </summary>
    [ObservableProperty]
    private bool _isNetworkLink;

    public string Name => Device.Name;
    public string Model => Device.Model;
    public string OsVersion => Device.OsVersion;
    public string DeviceClass => Device.DeviceClass;

    /// <summary>
    /// The shape this device is drawn as, looked up from what it reported about itself. The card
    /// used to draw one fixed rounded rectangle with a pill, so an iPad and an SE both came out
    /// as a modern iPhone; the frame now follows the actual hardware.
    /// </summary>
    private DeviceSilhouette Silhouette => DeviceModels.Silhouette(Device.ProductType, Device.DeviceClass);

    /// <summary>Body outline, with the notch already cut into it where the model has one.</summary>
    public Geometry OutlineBody => Outline.Body;

    /// <summary>Inner screen rectangle, null on edge-to-edge models.</summary>
    public Geometry? OutlineScreen => Outline.Screen;

    /// <summary>Dynamic Island pill, null unless this is an iPhone 14 Pro or later.</summary>
    public Geometry? OutlineIsland => Outline.Island;

    /// <summary>Front-camera dot, null on anything but a tablet.</summary>
    public Geometry? OutlineCamera => Outline.Camera;

    /// <summary>Home button, null on edge-to-edge models.</summary>
    public Geometry? OutlineHomeButton => Outline.HomeButton;

    private ParsedOutline Outline => ParsedOutline.For(Silhouette);

    public DeviceViewModel(Device device)
    {
        Device = device;
        _batteryLevel = device.BatteryLevel;
        _isNetworkLink = device.IsNetworkLink;
    }

    /// <summary>
    /// Re-reads the shape after the device reports more about itself. A locked phone answers with
    /// its class first and its product type a moment later, so the first lookup can only produce
    /// a generic frame — without this the card would keep it for the whole session.
    /// </summary>
    public void RefreshSilhouette()
    {
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(OutlineBody));
        OnPropertyChanged(nameof(OutlineScreen));
        OnPropertyChanged(nameof(OutlineIsland));
        OnPropertyChanged(nameof(OutlineCamera));
        OnPropertyChanged(nameof(OutlineHomeButton));
    }

    public void Refresh()
    {
        BatteryLevel = Device.BatteryLevel;
        IsNetworkLink = Device.IsNetworkLink;
        RefreshSilhouette();
    }
}

/// <summary>
/// One device outline with its path strings already parsed into frozen WPF geometry.
///
/// Cached per shape, not per device: the silhouette table hands out one shared instance per
/// distinct outline, so ten iPhones of the same model parse once between them. Frozen so the
/// same geometry can be shared across cards without WPF cloning it for each one.
/// </summary>
internal sealed record ParsedOutline(
    Geometry Body,
    Geometry? Screen,
    Geometry? Island,
    Geometry? Camera,
    Geometry? HomeButton)
{
    private static readonly ConcurrentDictionary<DeviceSilhouette, ParsedOutline> Cache = new();

    public static ParsedOutline For(DeviceSilhouette silhouette) =>
        Cache.GetOrAdd(silhouette, static s =>
        {
            var g = DeviceOutlines.For(s);
            return new ParsedOutline(Parse(g.Body)!, Parse(g.Screen), Parse(g.Island),
                                     Parse(g.Camera), Parse(g.HomeButton));
        });

    private static Geometry? Parse(string? data)
    {
        if (string.IsNullOrEmpty(data)) return null;

        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
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
    private readonly InstallService _install;

    /// <summary>Copies media onto the device Camera Roll for the Quick Transfer dialog.</summary>
    private readonly PhotoService _photos;

    /// <summary>Registers the Quick Transfer install as a trackable background operation.</summary>
    private readonly OperationService _operations;

    /// <summary>Decides whether Quick Transfer needs a password before it opens.</summary>
    private readonly DeviceGuardService _guard;

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
    private bool _isSignedIn;

    [ObservableProperty]
    private bool _hasDevices;

    public DevicesViewModel(
        DeviceService devices, CatalogService catalog, AuthService auth, InstallService install,
        PhotoService photos, OperationService operations, DeviceGuardService guard)
    {
        _devices = devices;
        _catalog = catalog;
        _auth = auth;
        _install = install;
        _photos = photos;
        _operations = operations;
        _guard = guard;

        _devices.DeviceConnected += OnDeviceConnected;
        _devices.DeviceDisconnected += OnDeviceDisconnected;
        _devices.DeviceUpdated += OnDeviceUpdated;
    }

    public void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;
        IsSignedIn = _auth.IsAuthenticated;
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
            Catalog = _catalog.LoadCatalog().ToList();
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

        // Selecting a device always opens the app picker — no Apple ID required just
        // to browse or to install an IPA from a file. Only the App Store actions
        // (install from the catalog, by Bundle ID, or purchased apps) prompt for
        // sign-in, and they do so on demand from inside the picker.
        _navigator?.GoToAppPicker(device.Device);
    }

    [RelayCommand]
    private void OpenDeviceInfo(DeviceViewModel? device)
    {
        if (device is null) return;
        _navigator?.GoToDeviceInfo(device.Device);
    }

    [RelayCommand]
    private void OpenPhotos(DeviceViewModel? device)
    {
        if (device is null) return;
        _navigator?.GoToPhotos(device.Device);
    }

    [RelayCommand]
    private void OpenOnDevice(DeviceViewModel? device)
    {
        if (device is null) return;
        _navigator?.GoToOnDevice(device.Device);
    }

    [RelayCommand]
    private void OpenQuickTransfer(DeviceViewModel? device)
    {
        if (device is null) return;

        // Gated like every other action that touches this specific device — the dialog
        // itself never runs against an unapproved serial, so there is nothing to unwind
        // if the password prompt is cancelled.
        if (!DeviceGuardPrompt.Allow(_guard, device.Device, "L.Guard.Action.QuickTransfer")) return;

        var owner = Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

        var dialog = new QuickTransferDialog(device.Device, _photos, _operations) { Owner = owner };
        dialog.ShowDialog();
    }

    [RelayCommand]
    private void SignIn() => _navigator?.GoTo(Page.Login);

    [RelayCommand]
    private async Task SignOutAsync()
    {
        await _auth.LogoutAsync();
        IsSignedIn = false;
        AccountEmail = "";
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
