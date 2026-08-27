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

    // The concurrency limits are applied to the services that own them rather than to a
    // queue: QueueService is per-operation now, so a queue reference here would configure
    // one arbitrary instance and miss every other running operation.
    private readonly DownloadThrottle _throttle;
    private readonly InstallService _install;

    /// <summary>Told about the multitasking switch so the corner circle appears or hides.</summary>
    private readonly OperationService _operations;

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
    private bool _useBetaAppleAuthentication;

    [ObservableProperty]
    private string _betaAuthDiagnostic = "";

    [ObservableProperty]
    private string _appsFolder = "";

    /// <summary>
    /// The iTunes "Mobile Applications" folder, for the iTunes 12.6.5.3 download route.
    /// Left blank on almost every machine: the default locations under Music are probed
    /// automatically, and this only matters when the iTunes library has been moved.
    /// </summary>
    [ObservableProperty]
    private string _itunesLibraryFolder = "";

    [ObservableProperty]
    private int _maxParallelDownloads = 3;

    /// <summary>
    /// Multitasking: each action becomes an operation that can be minimised and returned to.
    ///
    /// Off by default, and off means the old single-queue path rather than the new path
    /// capped at one operation — so turning it off is a real way back if the new path
    /// misbehaves on hardware we cannot test here.
    /// </summary>
    [ObservableProperty]
    private bool _multitaskingEnabled;

    /// <summary>Concurrent installs on one device. Different devices are always parallel.</summary>
    [ObservableProperty]
    private int _maxParallelInstallsPerDevice = 2;

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
            // Checked before anything is attempted: without Bonjour the network query returns
            // an empty list and no error, so a missing mDNS responder would otherwise look
            // exactly like a successful setup that simply finds no device.
            if (!DeviceService.IsBonjourInstalled())
            {
                WifiStatus = Str("L.Settings.Wifi.NoBonjour",
                    "Apple Bonjour is not installed or is disabled, and without it devices on the network cannot be found. Reinstall iTunes from apple.com to restore it.");
                AppLog.Warn("Wi-Fi discovery will find nothing: the Bonjour service is missing or disabled.");
                return;
            }

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

        var win = new Views.DeviceLogWindow(_devices, _auth)
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

    // Interrupted-download behaviour: two RadioButtons bound via the bool helpers below.
    // Written out as ResumeMode.X rather than relying on a static import, because the
    // `using static InstallMode` at the top of the file already owns the bare names.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResumeIsRestartFromScratch))]
    [NotifyPropertyChangedFor(nameof(ResumeIsKeepPartialFiles))]
    private ResumeMode _resumeMode = ResumeMode.KeepPartialFiles;

    public bool ResumeIsRestartFromScratch
    {
        get => ResumeMode == ResumeMode.RestartFromScratch;
        set { if (value) ResumeMode = ResumeMode.RestartFromScratch; }
    }
    public bool ResumeIsKeepPartialFiles
    {
        get => ResumeMode == ResumeMode.KeepPartialFiles;
        set { if (value) ResumeMode = ResumeMode.KeepPartialFiles; }
    }

    // Selection mode, one setting per page. Three pairs of RadioButtons rather than one
    // setting for the whole app, because the pages are used differently enough that a single
    // answer would be wrong somewhere: a camera roll is clicked through, while a batch of
    // apps to download is assembled slowly and is worth protecting from a stray click.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhotosSelectByClick))]
    [NotifyPropertyChangedFor(nameof(PhotosSelectByCheckbox))]
    private TileSelectionMode _photosSelectionMode = TileSelectionMode.Checkbox;

    public bool PhotosSelectByClick
    {
        get => PhotosSelectionMode == TileSelectionMode.Click;
        set { if (value) PhotosSelectionMode = TileSelectionMode.Click; }
    }
    public bool PhotosSelectByCheckbox
    {
        get => PhotosSelectionMode == TileSelectionMode.Checkbox;
        set { if (value) PhotosSelectionMode = TileSelectionMode.Checkbox; }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnDeviceSelectByClick))]
    [NotifyPropertyChangedFor(nameof(OnDeviceSelectByCheckbox))]
    private TileSelectionMode _onDeviceSelectionMode = TileSelectionMode.Checkbox;

    public bool OnDeviceSelectByClick
    {
        get => OnDeviceSelectionMode == TileSelectionMode.Click;
        set { if (value) OnDeviceSelectionMode = TileSelectionMode.Click; }
    }
    public bool OnDeviceSelectByCheckbox
    {
        get => OnDeviceSelectionMode == TileSelectionMode.Checkbox;
        set { if (value) OnDeviceSelectionMode = TileSelectionMode.Checkbox; }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CatalogSelectByClick))]
    [NotifyPropertyChangedFor(nameof(CatalogSelectByCheckbox))]
    private TileSelectionMode _catalogSelectionMode = TileSelectionMode.Checkbox;

    public bool CatalogSelectByClick
    {
        get => CatalogSelectionMode == TileSelectionMode.Click;
        set { if (value) CatalogSelectionMode = TileSelectionMode.Click; }
    }
    public bool CatalogSelectByCheckbox
    {
        get => CatalogSelectionMode == TileSelectionMode.Checkbox;
        set { if (value) CatalogSelectionMode = TileSelectionMode.Checkbox; }
    }

    // Whether Ctrl-click also selects while the page's select mode is off. On by default,
    // since it is the habit from every file manager, but switchable per page: on a camera roll
    // it is the accidental Ctrl-click that loses a carefully built selection.
    [ObservableProperty]
    private bool _photosCtrlSelects = true;

    [ObservableProperty]
    private bool _onDeviceCtrlSelects = true;

    [ObservableProperty]
    private bool _catalogCtrlSelects = true;

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
        SettingsService settings, AuthService auth, DownloadThrottle throttle,
        InstallService install, ToolLocator tools, LocalizationManager localization,
        UpdateService updates, DeviceService devices, OperationService operations)
    {
        _operations = operations;
        _settings = settings;
        _auth = auth;
        _throttle = throttle;
        _install = install;
        _tools = tools;
        _localization = localization;
        _updates = updates;
        _devices = devices;
    }

    // ===== Current load =====
    //
    // Measuring costs something itself, so it only runs while the reading is on screen. The
    // view starts and stops it: this view model is a singleton, and a timer left running here
    // would be exactly the kind of permanent background cost the reading exists to expose.

    private readonly LoadMonitor _loadMonitor = new();
    private System.Windows.Threading.DispatcherTimer? _loadTimer;

    /// <summary>
    /// One second between readings. Faster would make the number jitter too much to read, and
    /// each reading walks the process list.
    /// </summary>
    private static readonly TimeSpan LoadInterval = TimeSpan.FromSeconds(1);

    [ObservableProperty]
    private string _loadCpu = "—";

    [ObservableProperty]
    private string _loadMemory = "—";

    [ObservableProperty]
    private string _loadHelpers = "—";

    /// <summary>
    /// True when the app is working the processor hard enough to be worth flagging. Used to
    /// colour the figure, so a genuine problem is visible without reading the number.
    /// </summary>
    [ObservableProperty]
    private bool _loadIsHigh;

    /// <summary>Begins sampling. Safe to call again; a running timer is left alone.</summary>
    public void StartLoadMonitor()
    {
        if (_loadTimer is not null) return;

        _loadMonitor.Reset();

        _loadTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = LoadInterval,
        };
        _loadTimer.Tick += (_, _) => SampleLoad();
        _loadTimer.Start();
    }

    /// <summary>Stops sampling when the reading is no longer visible.</summary>
    public void StopLoadMonitor()
    {
        if (_loadTimer is null) return;

        _loadTimer.Stop();
        _loadTimer = null;
    }

    private void SampleLoad()
    {
        var sample = _loadMonitor.Sample();

        // Null is the priming call, where there is no window to measure across yet. The
        // previous text is left in place rather than blanked, which would read as a glitch.
        if (sample is null) return;

        LoadCpu = $"{sample.CpuPercent:0.#} %";
        LoadMemory = FormatBytes(sample.MemoryBytes);
        LoadHelpers = sample.HelperCount.ToString();
        LoadIsHigh = sample.CpuPercent >= 40;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "—";

        double value = bytes;
        string[] units = { "Б", "КБ", "МБ", "ГБ" };
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit >= 3 ? $"{value:0.#} {units[unit]}" : $"{value:0} {units[unit]}";
    }

    public void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;
        Language = _settings.Current.Language;
        Theme = _settings.Current.Theme;
        IpatoolVersion = _settings.Current.IpatoolVersion;
        UseBetaAppleAuthentication = _settings.Current.UseBetaAppleAuthentication;
        AppsFolder = _settings.Current.AppsFolder ?? _tools.AppsFolder;
        // Blank means "probe the default locations", which is what the service does anyway.
        ItunesLibraryFolder = _settings.Current.ItunesLibraryFolder ?? "";
        MaxParallelDownloads = _settings.Current.MaxParallelDownloads;
        MultitaskingEnabled = _settings.Current.MultitaskingEnabled;
        MaxParallelInstallsPerDevice = _settings.Current.MaxParallelInstallsPerDevice;
        AccountEmail = _auth.CurrentAccount?.Email ?? "";
        ToolsFolder = _tools.ToolsRoot;
        InstallMode = _settings.Current.InstallMode;
        ResumeMode = _settings.Current.ResumeMode;
        VerboseLogging = _settings.Current.VerboseLogging;
        WifiDeviceConnection = _settings.Current.WifiDeviceConnection;
        PhotosSelectionMode = _settings.Current.PhotosSelectionMode;
        OnDeviceSelectionMode = _settings.Current.OnDeviceSelectionMode;
        CatalogSelectionMode = _settings.Current.CatalogSelectionMode;
        PhotosCtrlSelects = _settings.Current.PhotosCtrlSelects;
        OnDeviceCtrlSelects = _settings.Current.OnDeviceCtrlSelects;
        CatalogCtrlSelects = _settings.Current.CatalogCtrlSelects;

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

            var findings = await TransferTuning.AnalyzeAsync(
                apps, staging, _settings.GetVerifiedDefenderExclusions());

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
        finding.FixFailed = false;
        try
        {
            var apps = _settings.Current.AppsFolder ?? _tools.AppsFolder;
            var staging = System.IO.Path.Combine(apps, ".staging");

            AppLog.Info($"Throughput fix '{finding.Kind}' requested for {apps}");

            var outcome = await TransferTuning.TryAutoFixAsync(finding.Kind, apps, staging);

            AppLog.Info($"Throughput fix '{finding.Kind}' outcome: {outcome}");

            if (outcome == ThroughputFixOutcome.Applied)
            {
                // Remember it: Defender will not show its exclusion list to this
                // unelevated process, so the next scan has no other way to know.
                if (finding.Kind == TransferTuning.KindDefender)
                {
                    _settings.RememberDefenderExclusions(
                        TransferTuning.DefenderExclusionTargets(apps, staging));
                }

                ThroughputFindings.Remove(finding);
                OnPropertyChanged(nameof(HasThroughputFindings));
            }
            else
            {
                finding.FixMessage = Str(outcome switch
                {
                    ThroughputFixOutcome.Cancelled => "L.Settings.Throughput.FixCancelled",
                    ThroughputFixOutcome.Blocked => "L.Settings.Throughput.FixBlocked",
                    _ => "L.Settings.Throughput.FixFailed",
                });
                finding.FixFailed = true;
            }
        }
        catch (Exception ex)
        {
            // Was swallowed silently, which is why the log had nothing to say about a button
            // the user reported as doing nothing at all.
            AppLog.Error($"Throughput fix '{finding.Kind}' threw.", ex);

            finding.FixMessage = Str("L.Settings.Throughput.FixFailed");
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
    private void CheckBetaAuthentication()
    {
        var betaTool = System.IO.File.Exists(_tools.BetaIpatoolPath);
        var helper = System.IO.File.Exists(_tools.SapHelperPath);
        var appleDesktop = new[]
        {
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "iTunes", "iTunes.exe"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "iTunes", "iTunes.exe"),
        }.Any(System.IO.File.Exists);

        BetaAuthDiagnostic = betaTool && helper && appleDesktop
            ? Str("L.Settings.BetaAuth.Ready")
            : string.Format(Str("L.Settings.BetaAuth.Missing"),
                betaTool ? "OK" : "ipatool.exe",
                helper ? "OK" : "ipastudio-sap-helper.exe",
                appleDesktop ? "OK" : "desktop iTunes");
    }

    [RelayCommand]
    private void Save()
    {
        // Detect a theme change before saving — switching the color theme needs a
        // restart because the styles resolve their palette once at startup.
        var themeChanged = !string.Equals(_settings.Current.Theme, Theme, StringComparison.OrdinalIgnoreCase);

        _settings.Current.Language = Language;
        _settings.Current.Theme = Theme;
        _settings.Current.IpatoolVersion = IpatoolVersion;
        _settings.Current.UseBetaAppleAuthentication = UseBetaAppleAuthentication;
        _settings.Current.AppsFolder = string.IsNullOrWhiteSpace(AppsFolder) ? null : AppsFolder;
        _settings.Current.ItunesLibraryFolder =
            string.IsNullOrWhiteSpace(ItunesLibraryFolder) ? null : ItunesLibraryFolder;
        _settings.Current.MaxParallelDownloads = Math.Clamp(MaxParallelDownloads, 1, 6);
        _settings.Current.MultitaskingEnabled = MultitaskingEnabled;
        _settings.Current.MaxParallelInstallsPerDevice = Math.Clamp(MaxParallelInstallsPerDevice, 1, 4);
        _settings.Current.InstallMode = InstallMode;
        _settings.Current.ResumeMode = ResumeMode;
        _settings.Current.PhotosSelectionMode = PhotosSelectionMode;
        _settings.Current.OnDeviceSelectionMode = OnDeviceSelectionMode;
        _settings.Current.CatalogSelectionMode = CatalogSelectionMode;
        _settings.Current.PhotosCtrlSelects = PhotosCtrlSelects;
        _settings.Current.OnDeviceCtrlSelects = OnDeviceCtrlSelects;
        _settings.Current.CatalogCtrlSelects = CatalogCtrlSelects;
        _settings.Current.WifiDeviceConnection = WifiDeviceConnection;
        _settings.Current.VerboseLogging = VerboseLogging;
        _settings.Save();

        // Applied live, without waiting for the current work to end: the throttle can change
        // its limit while transfers are in flight, and the install service rebuilds its
        // per-device limiters on assignment.
        _throttle.Limit = _settings.Current.MaxParallelDownloads;
        _install.MaxParallelInstallsPerDevice = _settings.Current.MaxParallelInstallsPerDevice;
        _operations.NotifyMultitaskingChanged();

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
