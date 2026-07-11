using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    [ObservableProperty]
    private bool _isOpen;

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

    public bool IsBusy => IsChecking || IsDownloading;

    public UpdaterViewModel(UpdateService updates)
    {
        _updates = updates;
        var v = _updates.CurrentVersion;
        VersionText = $"{v.Major}.{v.Minor}.{v.Build}";
    }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

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
            else
            {
                StatusText = Str("L.Update.OpenedBrowser");
            }
        }
        catch
        {
            StatusText = Str("L.Update.Failed");
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
}
