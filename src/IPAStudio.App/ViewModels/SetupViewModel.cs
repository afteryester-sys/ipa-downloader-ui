using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.Core.Services;

namespace IPAStudio.App.ViewModels;

/// <summary>
/// First-run environment check: verifies Apple drivers, iTunes, optional iCloud
/// (required by ipatool v3), and the bundled CLI tools.
///
/// When a required component is missing the screen stays visible and shows
/// direct download links instead of silently failing.
/// </summary>
public sealed partial class SetupViewModel : ObservableObject, IPageAware
{
    private const string ITunesUrl = "https://www.apple.com/itunes/download/win64";

    // We intentionally point at the CLASSIC (legacy, non-Store) iCloud for
    // Windows. ipatool v3's anisette reads Apple's ADI libraries from the
    // classic install path; the Microsoft Store version sandboxes those DLLs
    // under WindowsApps where anisette cannot reach them, so the Store build
    // does NOT work for sign-in.
    private const string ICloudWebUrl = "https://support.apple.com/en-us/106383";

    private readonly DependencyService _deps;
    private readonly SettingsService _settings;
    private INavigator? _navigator;
    private bool _checkStarted;

    [ObservableProperty] private DependencyState _driversState;
    [ObservableProperty] private DependencyState _itunesState;
    [ObservableProperty] private DependencyState _toolsState;
    [ObservableProperty] private DependencyState _iCloudState = DependencyState.Unknown;

    /// <summary>True when using ipatool v3 — iCloud for Windows is then required.</summary>
    [ObservableProperty] private bool _needsICloud;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallITunesCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallToolsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RecheckCommand))]
    private bool _isBusy;

    [ObservableProperty] private bool _isChecking;

    /// <summary>True briefly when all checks passed, before auto-continuing.</summary>
    [ObservableProperty] private bool _isReadySuccess;

    /// <summary>0..1 install progress; negative means indeterminate.</summary>
    [ObservableProperty] private double _installProgress = -1;

    [ObservableProperty] private bool _isProgressIndeterminate = true;

    [ObservableProperty] private string? _busyStage;

    [ObservableProperty] private bool _allReady;

    [ObservableProperty] private string? _errorMessage;

    /// <summary>
    /// Whether the Skip button should be shown.
    /// Hidden while the initial check hasn't run yet, or while something is installing.
    /// </summary>
    [ObservableProperty] private bool _canSkip;

    public SetupViewModel(DependencyService deps, SettingsService settings)
    {
        _deps = deps;
        _settings = settings;
        _deps.StatusChanged += OnStatusChanged;
    }

    public async void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;
        NeedsICloud = _settings.Current.IpatoolVersion == 3;
        if (_checkStarted) return;
        _checkStarted = true;
        await RunChecksAsync();
    }

    private void OnStatusChanged()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            DriversState = _deps.Status.AppleDrivers;
            ItunesState  = _deps.Status.ITunes;
            ToolsState   = _deps.Status.CliTools;
            AllReady     = _deps.Status.AllReady;
        });
    }

    private async Task RunChecksAsync()
    {
        IsChecking = true;
        IsReadySuccess = false;
        CanSkip    = false;
        ErrorMessage = null;
        try
        {
            // Keep the checking state visible for a moment so the user actually
            // sees the environment being verified instead of an instant skip.
            var minVisible = Task.Delay(700);
            await _deps.CheckAllAsync();

            // Check iCloud when v3 is active.
            if (NeedsICloud)
                ICloudState = _deps.Status.ICloud;

            // Auto-download CLI tools silently when they are the only thing missing.
            if (_deps.Status.CliTools == DependencyState.Missing &&
                _deps.Status.AppleDrivers == DependencyState.Ok)
            {
                await InstallToolsAsync();
            }

            await minVisible;
        }
        finally
        {
            IsChecking = false;
            CanSkip    = true; // always allow skip after first check
        }

        // Decide whether everything required is in place.
        var ready = _deps.Status.AllReady && (!NeedsICloud || ICloudState == DependencyState.Ok);
        if (ready)
        {
            // Show a visible success state, then continue automatically so the
            // user can confirm the environment was actually checked.
            IsReadySuccess = true;
            await Task.Delay(1200);
            _navigator?.GoTo(Page.Login);
        }
    }

    private bool CanRunInstall() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRunInstall))]
    private async Task InstallITunesAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var progress = new Progress<(double fraction, string stage)>(p =>
            {
                IsProgressIndeterminate = p.fraction < 0;
                InstallProgress = Math.Max(0, p.fraction);
                BusyStage = p.stage;
            });

            var ok = await _deps.InstallITunesAsync(progress);
            if (!ok)
                ErrorMessage = (string)Application.Current.FindResource("L.Setup.ITunesFailed");
            else if (_deps.Status.AllReady)
            {
                await Task.Delay(900);
                _navigator?.GoTo(Page.Login);
            }
        }
        finally
        {
            IsBusy = false;
            BusyStage = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunInstall))]
    private async Task InstallToolsAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var progress = new Progress<(double fraction, string stage)>(p =>
            {
                IsProgressIndeterminate = p.fraction < 0;
                InstallProgress = Math.Max(0, p.fraction);
                BusyStage = p.stage;
            });

            var ok = await _deps.InstallCliToolsAsync(progress);
            if (!ok)
                ErrorMessage = (string)Application.Current.FindResource("L.Setup.ToolsFailed");
        }
        finally
        {
            IsBusy = false;
            BusyStage = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunInstall))]
    private async Task RecheckAsync()
    {
        NeedsICloud = _settings.Current.IpatoolVersion == 3;
        await RunChecksAsync();
    }

    /// <summary>Opens the official iTunes download page in the default browser.</summary>
    [RelayCommand]
    private void OpenITunesPage()
    {
        try { Process.Start(new ProcessStartInfo(ITunesUrl) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Opens the Apple page for the classic (non-Store) iCloud for Windows,
    /// which is the version that works with ipatool v3's anisette.
    /// </summary>
    [RelayCommand]
    private void OpenICloudPage()
    {
        try { Process.Start(new ProcessStartInfo(ICloudWebUrl) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    [RelayCommand]
    private void Skip() => _navigator?.GoTo(Page.Login);
}
