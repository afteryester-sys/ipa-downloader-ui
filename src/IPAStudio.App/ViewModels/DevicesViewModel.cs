using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    /// <summary>
    /// The device's own home screen wallpaper, shown inside a phone-shaped frame on the card so
    /// two phones of the same model are told apart at a glance instead of being two identical
    /// tiles. Null until it has been fetched, and stays null when the device will not give it up
    /// — the card is designed to look finished either way.
    /// </summary>
    [ObservableProperty]
    private BitmapImage? _wallpaper;

    /// <summary>
    /// Icons of apps installed on this device, laid over the wallpaper to make the frame read as
    /// this phone's home screen. Deliberately not a screen capture: capturing the real screen
    /// needs the Developer Disk Image mounted, which is a per-iOS-version download that fails on
    /// a locked phone, whereas the wallpaper and icons come from the SpringBoard service that any
    /// paired device already answers. The order is therefore ours, not the phone's.
    /// </summary>
    public ObservableCollection<BitmapImage> HomeIcons { get; } = new();

    /// <summary>
    /// Why the home screen preview is missing, for the tile to show. Without this the failure is
    /// indistinguishable from a device that simply has a plain wallpaper, which left no way to
    /// tell a broken fetch from a working one without reading the log.
    /// </summary>
    [ObservableProperty]
    private string? _previewNote;

    /// <summary>True once a preview attempt has finished and produced no wallpaper.</summary>
    public bool HasPreviewNote => !string.IsNullOrEmpty(PreviewNote);

    partial void OnPreviewNoteChanged(string? value) => OnPropertyChanged(nameof(HasPreviewNote));

    /// <summary>How many icons the frame has room for.</summary>
    private const int HomeIconCount = 12;

    public string Name => Device.Name;
    public string Model => Device.Model;
    public string OsVersion => Device.OsVersion;
    public string DeviceClass => Device.DeviceClass;

    public DeviceViewModel(Device device)
    {
        Device = device;
        _batteryLevel = device.BatteryLevel;
        _isNetworkLink = device.IsNetworkLink;
    }

    /// <summary>
    /// Fetches the home screen preview - wallpaper plus app icons - in the background.
    /// Fire-and-forget by design: decoration must never delay a device appearing in the list,
    /// and any failure just leaves the card plain with a short note saying why.
    /// </summary>
    public async Task LoadWallpaperAsync(InstallService install)
    {
        try
        {
            // SpringBoard often refuses the very first connection right after attach, while it is
            // still bringing services up. A couple of spaced retries turn the common "no image on
            // a freshly plugged phone" into a preview that simply arrives a moment later. This was
            // the reason the wallpaper looked like it never loaded.
            byte[]? png = null;
            for (var attempt = 0; attempt < 3 && (png is null || png.Length == 0); attempt++)
            {
                if (attempt > 0) await Task.Delay(1200).ConfigureAwait(true);
                png = await install.GetHomeScreenWallpaperAsync(Device.Udid).ConfigureAwait(true);
            }

            if (png is null || png.Length == 0)
            {
                PreviewNote = Loc.Get("L.Devices.Preview.Locked");
                AppLog.Info($"devices: no wallpaper for {Device.Name} after retries");
                return;
            }

            Wallpaper = DecodeCard(png, 320);
            await LoadHomeIconsAsync(install).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PreviewNote = Loc.Get("L.Devices.Preview.Failed");
            AppLog.Info($"devices: no wallpaper for {Device.Name} ({ex.Message})");
        }
    }

    /// <summary>
    /// Fills <see cref="HomeIcons"/> with a page of installed-app icons. Best effort: a device
    /// that lists no apps, or gives up no artwork, just leaves the frame showing bare wallpaper.
    /// </summary>
    private async Task LoadHomeIconsAsync(InstallService install)
    {
        try
        {
            var bundleIds = await install.GetInstalledBundleIdsAsync(Device.Udid).ConfigureAwait(true);
            if (bundleIds.Count == 0) return;

            var page = bundleIds.Take(HomeIconCount).ToList();
            var icons = await install.GetAppIconsAsync(Device.Udid, page).ConfigureAwait(true);

            // Preserve the request order so the grid is stable between reads rather than
            // reshuffling with the dictionary's ordering each time.
            foreach (var id in page)
            {
                if (!icons.TryGetValue(id, out var bytes) || bytes.Length == 0) continue;
                HomeIcons.Add(DecodeCard(bytes, 96));
            }
        }
        catch (Exception ex)
        {
            AppLog.Info($"devices: no home icons for {Device.Name} ({ex.Message})");
        }
    }

    /// <summary>
    /// Decodes PNG bytes to a frozen bitmap at roughly its on-card size, so a full-resolution
    /// wallpaper does not cost several megabytes of bitmap per device. Frozen so it can be
    /// handed to the UI thread from here.
    /// </summary>
    private static BitmapImage DecodeCard(byte[] png, int width)
    {
        using var stream = new MemoryStream(png, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = width;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public void Refresh()
    {
        BatteryLevel = Device.BatteryLevel;
        IsNetworkLink = Device.IsNetworkLink;
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
        DeviceService devices, CatalogService catalog, AuthService auth, InstallService install)
    {
        _devices = devices;
        _catalog = catalog;
        _auth = auth;
        _install = install;

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

            // Started after the card is already on screen, so the wallpaper fades in late
            // rather than holding the device back while SpringBoard is asked for it.
            _ = vm.LoadWallpaperAsync(_install);

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
