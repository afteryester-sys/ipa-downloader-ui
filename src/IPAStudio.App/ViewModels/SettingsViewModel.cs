using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.App.Services;
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

    public SettingsViewModel(
        SettingsService settings, AuthService auth, QueueService queue,
        ToolLocator tools, LocalizationManager localization, UpdateService updates)
    {
        _settings = settings;
        _auth = auth;
        _queue = queue;
        _tools = tools;
        _localization = localization;
        _updates = updates;
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

        var v = _updates.CurrentVersion;
        CurrentVersion = $"{v.Major}.{v.Minor}.{v.Build}";
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
        _settings.Current.MaxParallelDownloads = Math.Clamp(MaxParallelDownloads, 1, 5);
        _settings.Current.InstallMode = InstallMode;
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
    private void GoBack() => _navigator?.GoTo(Page.Devices);
}
