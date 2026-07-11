using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.Core.Services;

namespace IPAStudio.App.ViewModels;

/// <summary>
/// First-run environment check: verifies Apple drivers, iTunes and the CLI
/// tools, and installs whatever is missing — fully autonomous. Automatically
/// continues to the login page when everything is ready.
/// </summary>
public sealed partial class SetupViewModel : ObservableObject, IPageAware
{
    private readonly DependencyService _deps;
    private INavigator? _navigator;
    private bool _checkStarted;

    [ObservableProperty] private DependencyState _driversState;
    [ObservableProperty] private DependencyState _itunesState;
    [ObservableProperty] private DependencyState _toolsState;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallITunesCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallToolsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RecheckCommand))]
    private bool _isBusy;

    [ObservableProperty] private bool _isChecking;

    /// <summary>0..1 install progress; negative means indeterminate.</summary>
    [ObservableProperty] private double _installProgress = -1;

    [ObservableProperty] private bool _isProgressIndeterminate = true;

    [ObservableProperty] private string? _busyStage;

    [ObservableProperty] private bool _allReady;

    [ObservableProperty] private string? _errorMessage;

    public SetupViewModel(DependencyService deps)
    {
        _deps = deps;
        _deps.StatusChanged += OnStatusChanged;
    }

    public async void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;
        if (_checkStarted) return;
        _checkStarted = true;
        await RunChecksAsync();
    }

    private void OnStatusChanged()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            DriversState = _deps.Status.AppleDrivers;
            ItunesState = _deps.Status.ITunes;
            ToolsState = _deps.Status.CliTools;
            AllReady = _deps.Status.AllReady;
        });
    }

    private async Task RunChecksAsync()
    {
        IsChecking = true;
        ErrorMessage = null;
        try
        {
            await _deps.CheckAllAsync();

            // Auto-download CLI tools silently — no user action needed.
            if (_deps.Status.CliTools == DependencyState.Missing)
                await InstallToolsAsync();

            // Everything present? Move on after a short beat so the user
            // sees the green checkmarks.
            if (_deps.Status.AllReady)
            {
                await Task.Delay(900);
                _navigator?.GoTo(Page.Login);
            }
        }
        finally
        {
            IsChecking = false;
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
    private async Task RecheckAsync() => await RunChecksAsync();

    [RelayCommand]
    private void Skip() => _navigator?.GoTo(Page.Login);
}
