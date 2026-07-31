using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.App.Services;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Services;
using IPAStudio.Core.Tools;
using static IPAStudio.Core.Services.InstallMode;

namespace IPAStudio.App.ViewModels;

/// <summary>
/// Settings: UI language, ipatool version, apps folder, parallel downloads and sign out.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IPageAware
{
    private readonly SettingsService _settings;
    private readonly AuthService _auth;
    private readonly QueueService _queue;
    private readonly ToolLocator _tools;
    private readonly LocalizationManager _localization;
    private readonly UpdateService _updates;
    private readonly DeviceService _devices;
    private INavigator? _navigator;

    [ObservableProperty]
    private string _language = "ru";

    // Color theme: two RadioButtons bound via the bool helpers below.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeIsDark))]
    [NotifyPropertyChangedFor(nameof(ThemeIsLight))]
    private string _theme = "dark";

    public bool ThemeIsDark
    {
        get => Theme == "dark";
        set { if (value) Theme = "dark"; }
    }
    public bool ThemeIsLight
    {
        get => Theme == "light";
        set { if (value) Theme = "light"; }
    }

    [ObservableProperty]
    private int _ipatoolVersion = 2;

    [ObservableProperty]
    private string _appsFolder = "";

    [ObservableProperty]
    private int _maxParallelDownloads = 3;

    /// <summary>
    /// Writes routine background detail (device-poll tool calls, media pipeline timings)
    /// to the log. Applied the moment it is toggled rather than on Save, because the
    /// point of the switch is to change what the user is watching in the log right now.
    /// </summary>
    [ObservableProperty]
    private bool _verboseLogging;

    partial void OnVerboseLoggingChanged(bool value)
    {
        // Nothing changed in practice (this fires when the page loads and copies the
        // saved value in), so don't announce anything.
        if (AppLog.Verbose == value) return;

        AppLog.Verbose = value;
        AppLog.Info(value
            ? "Verbose logging enabled."
            : "Verbose logging disabled - routine calls are no longer written.");
    }

    /// <summary>
    /// Look for devices over the local network as well as on the cable.
    ///
    /// Turning this on also asks every currently connected device to start advertising
    /// itself over Wi-Fi. That extra step is what actually makes the feature work on a
    /// phone that has only ever been synced by cable: the device decides whether to
    /// announce itself, that flag is off by default, and it can only be set while a
    /// trusted connection exists. Doing it at the moment the switch is flipped is the one
    /// point where a cable is likely still plugged in.
    /// </summary>
    [ObservableProperty]
    private bool _wifiDeviceConnection;

    /// <summary>Set while the connected devices are being prepared for Wi-Fi.</summary>
    [ObservableProperty]
    private string _wifiStatus = "";

    partial void OnWifiDeviceConnectionChanged(bool value)
    {
        // Fires when the page loads and copies the saved value in, where nothing changed
        // and there is nothing to announce or prepare.
        if (DeviceTransport.WifiEnabled == value) return;

        DeviceTransport.WifiEnabled = value;
        AppLog.Info(value
            ? "Wi-Fi device connection enabled - devices will also be looked for on the network."
            : "Wi-Fi device connection disabled - only cabled devices will be used.");

        if (value) _ = PrepareConnectedForWifiAsync();
        else WifiStatus = "";
    }

    /// <summary>
    /// Asks the connected devices to advertise themselves over the network.
    ///
    /// Failure is reported but not fatal, and deliberately does not switch the setting back
    /// off: a device may well already be enabled from an earlier iTunes sync, in which case
    /// the setting is still useful even though this call had nothing to do.
    /// </summary>
    private async Task PrepareConnectedForWifiAsync()
    {
        try
        {
            WifiStatus = Str("L.Settings.Wifi.Preparing", "Preparing devices…");
            var count = await _devices.EnableWifiSyncOnConnectedAsync().ConfigureAwait(true);

            WifiStatus = count > 0
                ? string.Format(Str("L.Settings.Wifi.Ready", "Wi-Fi enabled on {0} device(s)."), count)
                : Str("L.Settings.Wifi.NoDevices",
                      "No cabled device to prepare. Connect the device by cable once so it can be enabled for Wi-Fi.");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not prepare devices for Wi-Fi: {ex.Message}");
            WifiStatus = Str("L.Settings.Wifi.Failed", "Could not prepare the device for Wi-Fi. See the log.");
        }
    }

    /// <summary>Localized string by key, with a fallback so a missing key cannot blank the UI.</summary>
    private static string Str(string key, string fallback)
        => Application.Current?.TryFindResource(key) as string ?? fallback;

    /// <summary>
    /// Opens the live device log.
    ///
    /// Separate from the application log because it answers a different question: the app
    /// log ends at "install returned success", while a failure to launch is only ever
    /// explained on the phone itself.
    /// </summary>
    [RelayCommand]
    private void ShowDeviceLog()
    {
        // Reuse an open window rather than stacking duplicates, each of which would hold
        // its own syslog connection to the same device.
        foreach (var w in Application.Current.Windows)
        {
            if (w is Views.DeviceLogWindow existing)
            {
                existing.Activate();
                return;
            }
        }

        var win = new Views.DeviceLogWindow(_devices)
        {
            Owner = Application.Current.MainWindow,
        };
        win.Show();
    }

    // Install mode: three RadioButtons bound via bool helpers below.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeIsDownloadAndInstall))]
    [NotifyPropertyChangedFor(nameof(ModeIsDownloadOnly))]
    [NotifyPropertyChangedFor(nameof(ModeIsInstallExistingOnly))]
    private InstallMode _installMode = DownloadAndInstall;

    public bool ModeIsDownloadAndInstall
    {
        get => InstallMode == DownloadAndInstall;
        set { if (value) InstallMode = DownloadAndInstall; }
    }
    public bool ModeIsDownloadOnly
    {
        get => InstallMode == DownloadOnly;
        set { if (value) InstallMode = DownloadOnly; }
    }
    public bool ModeIsInstallExistingOnly
    {
        get => InstallMode == InstallExistingOnly;
        set { if (value) InstallMode = InstallExistingOnly; }
    }

    [ObservableProperty]
    private string _accountEmail = "";

    [ObservableProperty]
    private string _toolsFolder = "";

    // ---- Updates ----
    [ObservableProperty]
    private string _currentVersion = "";

    [ObservableProperty]
    private string _updateStatus = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateBusy))]
    private bool _isCheckingUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateBusy))]
    private bool _isDownloadingUpdate;

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private bool _updateReady;

    [ObservableProperty]
    private double _updateProgress;

    public bool IsUpdateBusy => IsCheckingUpdate || IsDownloadingUpdate;

    // ---- Download throughput diagnostics ----

    /// <summary>Local conditions that are slowing downloads down.</summary>
    public ObservableCollection<ThroughputFindingViewModel> ThroughputFindings { get; } = new();

    [ObservableProperty]
    private bool _isScanningThroughput;

    [ObservableProperty]
    private string _throughputStatus = "";

    public bool HasThroughputFindings => ThroughputFindings.Count > 0;

    public SettingsViewModel(
        SettingsService settings, AuthService auth, QueueService queue,
        ToolLocator tools, LocalizationManager localization, UpdateService updates,
        DeviceService devices)
    {
        _settings = settings;
        _auth = auth;
        _queue = queue;
        _tools = tools;
        _localization = localization;
        _updates = updates;
        _devices = devices;
    }

    public void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;
        Language = _settings.Current.Language;
        Theme = _settings.Current.Theme;
        IpatoolVersion = _settings.Current.IpatoolVersion;
        AppsFolder = _settings.Current.AppsFolder ?? _tools.AppsFolder;
        MaxParallelDownloads = _settings.Current.MaxParallelDownloads;
        AccountEmail = _auth.CurrentAccount?.Email ?? "";
        ToolsFolder = _tools.ToolsRoot;
        InstallMode = _settings.Current.InstallMode;
        VerboseLogging = _settings.Current.VerboseLogging;
        WifiDeviceConnection = _settings.Current.WifiDeviceConnection;

        var v = _updates.CurrentVersion;
        CurrentVersion = $"{v.Major}.{v.Minor}.{v.Build}";

        if (_settings.Current.CheckThroughputIssues)
            _ = ScanThroughputAsync();
    }

    /// <summary>
    /// Looks for local conditions that throttle downloads. Advisory only — nothing is
    /// changed here, and a failure is silent because this is a diagnostic.
    /// </summary>
    [RelayCommand]
    private async Task ScanThroughputAsync()
    {
        if (IsScanningThroughput) return;

        IsScanningThroughput = true;
        ThroughputStatus = Str("L.Settings.Throughput.Scanning");

        try
        {
            var apps = _settings.Current.AppsFolder ?? _tools.AppsFolder;
            var staging = System.IO.Path.Combine(apps, ".staging");

            var findings = await TransferTuning.AnalyzeAsync(apps, staging);

            ThroughputFindings.Clear();
            foreach (var f in findings)
            {
                if (_settings.IsThroughputFindingDismissed(f.Kind)) continue;
                ThroughputFindings.Add(new ThroughputFindingViewModel(f));
            }

            ThroughputStatus = ThroughputFindings.Count == 0
                ? Str("L.Settings.Throughput.Clean")
                : "";
        }
        catch
        {
            ThroughputStatus = "";
        }
        finally
        {
            IsScanningThroughput = false;
            OnPropertyChanged(nameof(HasThroughputFindings));
        }
    }

    /// <summary>
    /// Applies a finding's fix. The Defender exclusion shows a UAC prompt; if the user
    /// declines, the finding stays so it can be retried.
    /// </summary>
    [RelayCommand]
    private async Task FixThroughputAsync(ThroughputFindingViewModel? finding)
    {
        if (finding is null || !finding.CanAutoFix || finding.IsFixing) return;

        finding.IsFixing = true;
        try
        {
            var apps = _settings.Current.AppsFolder ?? _tools.AppsFolder;
            var staging = System.IO.Path.Combine(apps, ".staging");

            var ok = await TransferTuning.TryAutoFixAsync(finding.Kind, apps, staging);
            if (ok)
            {
                ThroughputFindings.Remove(finding);
                OnPropertyChanged(nameof(HasThroughputFindings));
            }
            else
            {
                finding.FixFailed = true;
            }
        }
        catch
        {
            finding.FixFailed = true;
        }
        finally
        {
            finding.IsFixing = false;
        }
    }

    /// <summary>Hides a finding permanently.</summary>
    [RelayCommand]
    private void DismissThroughput(ThroughputFindingViewModel? finding)
    {
        if (finding is null) return;
        _settings.DismissThroughputFinding(finding.Kind);
        ThroughputFindings.Remove(finding);
        OnPropertyChanged(nameof(HasThroughputFindings));
    }

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        if (IsUpdateBusy) return;
        IsCheckingUpdate = true;
        UpdateAvailable = false;
        UpdateReady = false;
        UpdateStatus = Str("L.Update.Checking");
        try
        {
            var hasUpdate = await _updates.CheckForUpdatesAsync();
            if (hasUpdate && _updates.LatestVersion is { } latest)
            {
                UpdateAvailable = true;
                UpdateStatus = string.Format(Str("L.Update.Available"),
                    $"{latest.Major}.{latest.Minor}.{latest.Build}");
            }
            else if (_updates.State == UpdateState.UpToDate)
            {
                UpdateStatus = Str("L.Update.UpToDate");
            }
            else
            {
                UpdateStatus = Str("L.Update.Failed");
            }
        }
        catch
        {
            UpdateStatus = Str("L.Update.Failed");
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        if (IsUpdateBusy) return;
        IsDownloadingUpdate = true;
        UpdateProgress = 0;
        UpdateStatus = Str("L.Update.Downloading");
        try
        {
            var progress = new Progress<double>(f => UpdateProgress = f);
            var ok = await _updates.DownloadUpdateAsync(progress);
            if (ok)
            {
                UpdateReady = true;
                UpdateAvailable = false;
                UpdateStatus = Str("L.Update.Ready");
            }
            else
            {
                // No direct asset — the releases page was opened in the browser.
                UpdateStatus = Str("L.Update.OpenedBrowser");
            }
        }
        catch
        {
            UpdateStatus = Str("L.Update.Failed");
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    [RelayCommand]
    private void InstallUpdate()
    {
        if (_updates.LaunchInstaller())
            Application.Current.Shutdown();
        else
            _updates.OpenReleasesPage();
    }

    private static string Str(string key) =>
        Application.Current.TryFindResource(key) as string ?? key;

    [RelayCommand]
    private void Save()
    {
        // Detect a theme change before saving — switching the color theme needs a
        // restart because the styles resolve their palette once at startup.
        var themeChanged = !string.Equals(_settings.Current.Theme, Theme, StringComparison.OrdinalIgnoreCase);

        _settings.Current.Language = Language;
        _settings.Current.Theme = Theme;
        _settings.Current.IpatoolVersion = IpatoolVersion;
        _settings.Current.AppsFolder = string.IsNullOrWhiteSpace(AppsFolder) ? null : AppsFolder;
        _settings.Current.MaxParallelDownloads = Math.Clamp(MaxParallelDownloads, 1, 6);
        _settings.Current.InstallMode = InstallMode;
        _settings.Current.WifiDeviceConnection = WifiDeviceConnection;
        _settings.Current.VerboseLogging = VerboseLogging;
        _settings.Save();

        _queue.MaxParallelDownloads = _settings.Current.MaxParallelDownloads;
        _localization.Apply(Language);

        if (themeChanged)
        {
            RestartApp();
            return;
        }

        _navigator?.GoTo(Page.Devices);
    }

    /// <summary>
    /// Relaunches the application so the newly-selected color theme takes effect,
    /// then shuts the current instance down.
    /// </summary>
    private static void RestartApp()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath) { UseShellExecute = true });
        }
        catch { /* if relaunch fails, still shut down; user can reopen manually */ }
        Application.Current.Shutdown();
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        await _auth.LogoutAsync();
        _navigator?.GoTo(Page.Login);
    }

    [RelayCommand]
    // GoBack rather than a hard-coded Devices: settings now open from the corner flyout on
    // any page, so returning to Devices would drop a user who came from Login or Setup onto
    // a page they had not reached yet.
    private void GoBack() => _navigator?.GoBack();
}
