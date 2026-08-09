using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.App.Services;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Services;

namespace IPAStudio.App.ViewModels;

/// <summary>
/// Compact updater used by the corner "update" flyout that is available on every
/// screen (including the login screen, before the user has signed in). Wraps the
/// shared <see cref="UpdateService"/> and exposes bindable state for the popup.
/// </summary>
public sealed partial class UpdaterViewModel : ObservableObject
{
    private readonly UpdateService _updates;
    private readonly SettingsService _settings;

    // Asked whether any work is in flight, which is a question about all operations rather
    // than one queue: with multitasking on there can be several queues at once, and the old
    // single QueueService reference would only have seen one of them.
    private readonly OperationService _operations;
    private readonly CleanupService _cleanup;

    /// <summary>
    /// Last measurement. Reused by "Clear cache" so the amount shown in the
    /// confirmation is exactly what gets deleted, and dropped afterwards.
    /// </summary>
    private CleanupReport? _lastScan;

    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Human-readable status shown under the "Clear cache" button.</summary>
    [ObservableProperty]
    private string _cacheStatusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCacheBusy))]
    private bool _isClearingCache;

    /// <summary>True while measuring. Drives an indeterminate bar: a scan has no total.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCacheBusy))]
    private bool _isScanningCache;

    /// <summary>Delete progress, 0..1. Meaningless while scanning.</summary>
    [ObservableProperty]
    private double _cacheProgress;

    /// <summary>Per-group breakdown from the last check; empty until one has run.</summary>
    public ObservableCollection<CacheGroupRow> CacheGroups { get; } = new();

    /// <summary>Total from the last check, e.g. "2.7 GB in 143 files".</summary>
    [ObservableProperty]
    private string _cacheTotalText = "";

    public bool IsCacheBusy => IsClearingCache || IsScanningCache;

    [ObservableProperty]
    private string _versionText = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isChecking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isDownloading;

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private bool _updateReady;

    [ObservableProperty]
    private double _progress;

    public bool IsBusy => IsChecking || IsDownloading || IsRollingBack;

    // ---- Rollback (password-gated) -----------------------------------------
    // Hidden behind a password so it isn't a stray button next to "Check for updates":
    // rolling back reinstalls an older build over the current one, which is not something
    // to trigger by accident.

    private const string RollbackPassword = "SEREGA";

    [ObservableProperty]
    private bool _rollbackUnlocked;

    [ObservableProperty]
    private string _rollbackPasswordInput = "";

    [ObservableProperty]
    private string _rollbackPasswordError = "";

    [ObservableProperty]
    private bool _isLoadingReleases;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isRollingBack;

    [ObservableProperty]
    private string _rollbackStatusText = "";

    [ObservableProperty]
    private ReleaseSummary? _selectedRollbackRelease;

    public ObservableCollection<ReleaseSummary> RollbackReleases { get; } = new();

    public bool HasRollbackReleases => RollbackReleases.Count > 0;

    public UpdaterViewModel(UpdateService updates, SettingsService settings,
                            OperationService operations, CleanupService cleanup)
    {
        _updates = updates;
        _settings = settings;
        _operations = operations;
        _cleanup = cleanup;
        var v = _updates.CurrentVersion;
        VersionText = $"{v.Major}.{v.Minor}.{v.Build}";

        RollbackReleases.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRollbackReleases));
    }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    // ---- Automatic checking -------------------------------------------------
    // Nothing checked on its own before: the dot beside the gear only appeared after the
    // user opened the menu and pressed "Check", so a release could sit unnoticed for as
    // long as nobody thought to look. The timer runs the same check quietly instead.

    private System.Windows.Threading.DispatcherTimer? _autoCheckTimer;

    /// <summary>Once an hour. Releases appear a few times a day at most.</summary>
    private static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// Delay before the first look. Startup is already busy probing tools, polling for
    /// devices and loading the catalog; adding a network call to that makes the window
    /// slower to become usable and buys nothing, since the answer then keeps for an hour.
    /// </summary>
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Starts checking in the background. Safe to call again — a running timer is left
    /// alone, which matters because the window re-hooks its view model on every
    /// DataContext change.
    /// </summary>
    public void StartAutoCheck()
    {
        if (_autoCheckTimer is not null) return;

        // Background priority: this must never take a slice from rendering or input.
        _autoCheckTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = FirstCheckDelay,
        };
        _autoCheckTimer.Tick += async (_, _) =>
        {
            // The first tick uses the short startup delay; every one after it is hourly.
            if (_autoCheckTimer is { } timer && timer.Interval != AutoCheckInterval)
                timer.Interval = AutoCheckInterval;

            await AutoCheckAsync().ConfigureAwait(true);
        };
        _autoCheckTimer.Start();
    }

    /// <summary>
    /// The quiet counterpart of <see cref="CheckAsync"/>: it may only ever turn the dot on.
    ///
    /// Deliberately does not touch <see cref="StatusText"/> on failure and does not clear
    /// <see cref="UpdateAvailable"/> the way the manual check does. An hourly background
    /// task that reports "no connection" into the menu, or blanks the dot for the duration
    /// of its own request, would be reporting on itself rather than on updates.
    /// </summary>
    private async Task AutoCheckAsync()
    {
        // Anything the user started owns the status text; an already-downloaded update is
        // waiting to be installed, and the dot has nothing left to say.
        if (IsBusy || UpdateReady || UpdateAvailable) return;

        // Not while the menu is open: text changing under the pointer reads as a glitch,
        // and the Check button is right there for anyone who wants an answer now.
        if (IsOpen) return;

        try
        {
            var hasUpdate = await _updates.CheckForUpdatesAsync().ConfigureAwait(true);
            if (!hasUpdate || _updates.LatestVersion is not { } latest) return;

            UpdateAvailable = true;
            StatusText = string.Format(Str("L.Update.Available"),
                $"{latest.Major}.{latest.Minor}.{latest.Build}");
            AppLog.Info($"Automatic check found update {latest.Major}.{latest.Minor}.{latest.Build}.");
        }
        catch (Exception ex)
        {
            // Logged, never shown: an unreachable network is the normal case here, and the
            // next attempt is an hour away.
            AppLog.Info($"Automatic update check failed, will retry later ({ex.Message}).");
        }
    }

    // ---- Appearance (color theme) ------------------------------------------
    // Two RadioButtons in the menu popup bind to these. Selecting a different
    // theme saves the setting and restarts the app so the palette re-applies.

    public bool ThemeIsDark
    {
        get => !string.Equals(_settings.Current.Theme, "light", StringComparison.OrdinalIgnoreCase);
        set { if (value) ApplyTheme("dark"); }
    }

    public bool ThemeIsLight
    {
        get => string.Equals(_settings.Current.Theme, "light", StringComparison.OrdinalIgnoreCase);
        set { if (value) ApplyTheme("light"); }
    }

    private void ApplyTheme(string theme)
    {
        if (string.Equals(_settings.Current.Theme, theme, StringComparison.OrdinalIgnoreCase))
            return;

        _settings.Current.Theme = theme;
        _settings.Save();
        OnPropertyChanged(nameof(ThemeIsDark));
        OnPropertyChanged(nameof(ThemeIsLight));

        // The palette is resolved once at startup, so a restart is required.
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Theme change relaunch failed: {ex.Message}");
        }
        Application.Current.Shutdown();
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (IsBusy) return;
        IsChecking = true;
        UpdateAvailable = false;
        UpdateReady = false;
        StatusText = Str("L.Update.Checking");
        try
        {
            var hasUpdate = await _updates.CheckForUpdatesAsync();
            if (hasUpdate && _updates.LatestVersion is { } latest)
            {
                UpdateAvailable = true;
                StatusText = string.Format(Str("L.Update.Available"),
                    $"{latest.Major}.{latest.Minor}.{latest.Build}");
            }
            else if (_updates.State == UpdateState.UpToDate)
            {
                var v = _updates.CurrentVersion;
                StatusText = string.Format(Str("L.Update.UpToDateVer"),
                    $"{v.Major}.{v.Minor}.{v.Build}");
            }
            else
            {
                // Precise, actionable reason instead of a generic message.
                StatusText = _updates.FailureReason switch
                {
                    UpdateFailureReason.NoReleases  => Str("L.Update.NoReleases"),
                    UpdateFailureReason.Network     => Str("L.Update.NoConnection"),
                    UpdateFailureReason.Timeout     => Str("L.Update.Timeout"),
                    UpdateFailureReason.ServerError => string.Format(Str("L.Update.ServerError"),
                                                          _updates.LastErrorDetail),
                    UpdateFailureReason.BadResponse => Str("L.Update.BadResponse"),
                    _                               => Str("L.Update.Failed"),
                };
            }
        }
        catch
        {
            StatusText = Str("L.Update.Failed");
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (IsBusy) return;
        IsDownloading = true;
        Progress = 0;
        StatusText = Str("L.Update.Downloading");
        try
        {
            var progress = new Progress<double>(f => Progress = f);
            var ok = await _updates.DownloadUpdateAsync(progress);
            if (ok)
            {
                UpdateReady = true;
                UpdateAvailable = false;
                StatusText = Str("L.Update.Ready");
            }
            else if (_updates.State == UpdateState.Failed)
            {
                // Map FailureReason to a user-readable string (same as CheckAsync).
                StatusText = _updates.FailureReason switch
                {
                    UpdateFailureReason.Timeout     => Str("L.Update.Timeout"),
                    UpdateFailureReason.Network     => Str("L.Update.NoConnection"),
                    UpdateFailureReason.ServerError => string.Format(Str("L.Update.ServerError"),
                                                          _updates.LastErrorDetail),
                    _                               => Str("L.Update.Failed"),
                };
                AppLog.Error($"Update download failed: {_updates.FailureReason} — {_updates.LastErrorDetail}");
            }
            else
            {
                // DownloadUrl was absent — the releases page was opened instead.
                StatusText = Str("L.Update.OpenedBrowser");
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = Str("L.Update.Timeout");
        }
        catch (Exception ex)
        {
            StatusText = Str("L.Update.Failed");
            AppLog.Error($"DownloadAsync unhandled exception: {ex.Message}", ex);
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void Install()
    {
        if (_updates.LaunchInstaller())
            Application.Current.Shutdown();
        else
            _updates.OpenReleasesPage();
    }

    /// <summary>
    /// Measures what can be freed — downloaded IPA files including half-finished ones,
    /// cached icons, the catalog cache, leftover temporary files and logs from previous
    /// days — and lists it per group. Nothing is deleted here, so this is safe to press
    /// out of curiosity; the numbers are what "Clear cache" would remove.
    /// </summary>
    [RelayCommand]
    private async Task CheckCacheAsync()
    {
        if (IsCacheBusy) return;

        IsScanningCache = true;
        CacheProgress = 0;
        CacheGroups.Clear();
        CacheTotalText = "";
        CacheStatusText = Str("L.Cache.Scanning");
        try
        {
            // Name the group being walked: on a large Apps folder this is the only
            // sign that the scan is moving rather than stuck.
            var onGroup = new Progress<string>(key =>
                CacheStatusText = string.Format(Str("L.Cache.ScanningGroup"), Str(key)));

            _lastScan = await _cleanup.ScanAsync(onGroup);
            ShowScan(_lastScan);
        }
        catch (Exception ex)
        {
            AppLog.Error("Cache scan failed.", ex);
            _lastScan = null;
            CacheStatusText = Str("L.Cache.ScanFailed");
        }
        finally
        {
            IsScanningCache = false;
        }
    }

    /// <summary>
    /// Deletes the cached data after confirmation, reporting real progress: clearing tens
    /// of gigabytes of IPA files takes long enough that a frozen window looks like a hang.
    /// Leaves settings, the signed-in session and the user's own catalog entries untouched.
    /// </summary>
    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        if (IsCacheBusy) return;

        // Refuse while transfers are in flight: the queue writes into AppsFolder, and
        // deleting a file mid-download would fail the transfer with a confusing I/O
        // error rather than anything that points back to this button.
        if (_operations.HasRunning)
        {
            CacheStatusText = Str("L.Cache.BusyDownloading");
            return;
        }

        // Measure first if the user went straight for the button, so the figure in the
        // confirmation is a real one rather than a guess.
        var report = _lastScan;
        if (report is null)
        {
            await CheckCacheAsync();
            report = _lastScan;
            if (report is null) return;
        }

        if (report.IsEmpty)
        {
            CacheStatusText = Str("L.Cache.AlreadyEmpty");
            return;
        }

        var confirm = MessageBox.Show(
            string.Format(Str("L.Cache.ConfirmBody"), FormatSize(report.TotalBytes)),
            Str("L.Cache.ConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        IsClearingCache = true;
        CacheProgress = 0;
        CacheStatusText = Str("L.Cache.Clearing");
        try
        {
            var progress = new Progress<CleanupProgress>(p =>
            {
                CacheProgress = p.Fraction;
                CacheStatusText = string.Format(Str("L.Cache.ClearingProgress"),
                                                FormatSize(p.BytesDone),
                                                FormatSize(p.BytesTotal));
            });

            var result = await _cleanup.CleanAsync(report, progress);

            CacheProgress = 1;
            CacheGroups.Clear();
            CacheTotalText = "";
            _lastScan = null;

            CacheStatusText = result.SkippedFiles > 0
                ? string.Format(Str("L.Cache.DonePartial"), FormatSize(result.FreedBytes), result.SkippedFiles)
                : string.Format(Str("L.Cache.Done"), FormatSize(result.FreedBytes));
        }
        catch (Exception ex)
        {
            AppLog.Error("Clear cache failed.", ex);
            _lastScan = null;
            CacheStatusText = Str("L.Cache.Failed");
        }
        finally
        {
            IsClearingCache = false;
        }
    }

    private void ShowScan(CleanupReport report)
    {
        CacheGroups.Clear();
        foreach (var group in report.NonEmptyGroups)
            CacheGroups.Add(new CacheGroupRow(Str(group.Key), FormatSize(group.Bytes), group.Path));

        if (report.IsEmpty)
        {
            CacheTotalText = "";
            CacheStatusText = Str("L.Cache.AlreadyEmpty");
            return;
        }

        CacheTotalText = string.Format(Str("L.Cache.ScanTotal"),
                                       FormatSize(report.TotalBytes), report.TotalFiles);
        CacheStatusText = Str("L.Cache.ScanDone");
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var u = 0;
        while (size >= 1024 && u < units.Length - 1) { size /= 1024; u++; }
        return $"{size:0.#} {units[u]}";
    }

    /// <summary>Opens the detailed log viewer (used to copy errors for support).</summary>
    [RelayCommand]
    private void ShowLogs()
    {
        IsOpen = false;

        // Reuse an already-open log window instead of stacking duplicates.
        foreach (var w in Application.Current.Windows)
        {
            if (w is Views.LogWindow existing)
            {
                existing.Activate();
                return;
            }
        }

        var win = new Views.LogWindow
        {
            Owner = Application.Current.MainWindow,
        };
        win.Show();
    }

    private static string Str(string key) =>
        Application.Current.TryFindResource(key) as string ?? key;

    // ---- Rollback (password-gated) -----------------------------------------

    /// <summary>Checks the typed password and, if correct, loads the release list.</summary>
    [RelayCommand]
    private async Task UnlockRollbackAsync()
    {
        if (RollbackPasswordInput != RollbackPassword)
        {
            RollbackPasswordError = Str("L.Rollback.WrongPassword");
            return;
        }

        RollbackPasswordError = "";
        RollbackUnlocked = true;
        RollbackPasswordInput = "";
        await LoadRollbackReleasesAsync();
    }

    /// <summary>Re-locks the tool and drops whatever was loaded, so leaving the menu open
    /// unattended doesn't leave rollback armed for the next person to use the PC.</summary>
    [RelayCommand]
    private void LockRollback()
    {
        RollbackUnlocked = false;
        RollbackPasswordInput = "";
        RollbackPasswordError = "";
        RollbackStatusText = "";
        SelectedRollbackRelease = null;
        RollbackReleases.Clear();
    }

    private async Task LoadRollbackReleasesAsync()
    {
        IsLoadingReleases = true;
        RollbackStatusText = Str("L.Rollback.Loading");
        try
        {
            var releases = await _updates.ListReleasesAsync();
            RollbackReleases.Clear();
            // The current build has nothing to roll back to itself, so it is excluded —
            // whatever remains is strictly older or, for a pre-release channel, unreleased.
            foreach (var r in releases.Where(r => r.Version is null || r.Version != _updates.CurrentVersion))
                RollbackReleases.Add(r);

            RollbackStatusText = RollbackReleases.Count > 0
                ? "" : Str("L.Rollback.NoReleases");
        }
        catch (Exception ex)
        {
            AppLog.Error("LoadRollbackReleasesAsync failed.", ex);
            RollbackStatusText = Str("L.Rollback.LoadFailed");
        }
        finally
        {
            IsLoadingReleases = false;
        }
    }

    /// <summary>Downloads the selected release's installer and hands off to it, exactly like
    /// a normal update install — the app closes so the installer can replace its files.</summary>
    [RelayCommand]
    private async Task RollbackAsync()
    {
        if (IsBusy || SelectedRollbackRelease is not { } release) return;

        var confirm = MessageBox.Show(
            string.Format(Str("L.Rollback.ConfirmBody"), release.DisplayVersion),
            Str("L.Rollback.ConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        IsRollingBack = true;
        Progress = 0;
        RollbackStatusText = Str("L.Rollback.Downloading");
        try
        {
            var progress = new Progress<double>(f => Progress = f);
            var ok = await _updates.DownloadReleaseAsync(release.Tag, progress);
            if (!ok)
            {
                RollbackStatusText = Str("L.Rollback.Failed");
                AppLog.Error($"Rollback download failed for tag '{release.Tag}'.");
                return;
            }

            AppLog.Info($"Rollback: launching installer for '{release.Tag}'.");
            if (_updates.LaunchInstaller())
                Application.Current.Shutdown();
            else
                RollbackStatusText = Str("L.Rollback.Failed");
        }
        catch (Exception ex)
        {
            AppLog.Error("RollbackAsync failed.", ex);
            RollbackStatusText = Str("L.Rollback.Failed");
        }
        finally
        {
            IsRollingBack = false;
        }
    }
}

/// <summary>One line of the cache breakdown: what it is, how big, and where it lives.</summary>
public sealed record CacheGroupRow(string Label, string SizeText, string Path);
