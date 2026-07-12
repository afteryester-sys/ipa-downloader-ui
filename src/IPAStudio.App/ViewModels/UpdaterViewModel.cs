using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Services;
using IPAStudio.Core.Tools;

namespace IPAStudio.App.ViewModels;

/// <summary>
/// Compact updater used by the corner "update" flyout that is available on every
/// screen (including the login screen, before the user has signed in). Wraps the
/// shared <see cref="UpdateService"/> and exposes bindable state for the popup.
/// </summary>
public sealed partial class UpdaterViewModel : ObservableObject
{
    private readonly UpdateService _updates;
    private readonly ToolLocator _tools;

    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Human-readable status shown under the "Clear cache" button.</summary>
    [ObservableProperty]
    private string _cacheStatusText = "";

    [ObservableProperty]
    private bool _isClearingCache;

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

    public UpdaterViewModel(UpdateService updates, ToolLocator tools)
    {
        _updates = updates;
        _tools = tools;
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

    /// <summary>
    /// Deletes cached data — downloaded IPA files, cached app icons and the
    /// catalog cache — after confirmation. Leaves settings and the signed-in
    /// session (ipatool keychain) untouched.
    /// </summary>
    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        if (IsClearingCache) return;

        var targets = new (string path, bool isFile)[]
        {
            (_tools.AppsFolder,       false),
            (_tools.IconCacheFolder,  false),
            (_tools.CatalogCacheFile, true),
        };

        // Show how much will be freed and ask for confirmation.
        long total = 0;
        foreach (var (path, isFile) in targets)
            total += isFile ? FileSize(path) : DirSize(path);

        var confirm = MessageBox.Show(
            string.Format(Str("L.Cache.ConfirmBody"), FormatSize(total)),
            Str("L.Cache.ConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        IsClearingCache = true;
        CacheStatusText = Str("L.Cache.Clearing");
        try
        {
            long freed = 0;
            await Task.Run(() =>
            {
                foreach (var (path, isFile) in targets)
                {
                    try
                    {
                        if (isFile)
                        {
                            if (File.Exists(path)) { freed += FileSize(path); File.Delete(path); }
                        }
                        else if (Directory.Exists(path))
                        {
                            freed += DirSize(path);
                            Directory.Delete(path, recursive: true);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"Clear cache: could not remove '{path}': {ex.Message}");
                    }
                }
            });

            // Recreate the empty folders so the app keeps working.
            _tools.EnsureFolders();

            AppLog.Info($"Cache cleared, freed {FormatSize(freed)}.");
            CacheStatusText = string.Format(Str("L.Cache.Done"), FormatSize(freed));
        }
        catch (Exception ex)
        {
            AppLog.Error("Clear cache failed.", ex);
            CacheStatusText = Str("L.Cache.Failed");
        }
        finally
        {
            IsClearingCache = false;
        }
    }

    private static long DirSize(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }

    private static long FileSize(string file)
    {
        try { return File.Exists(file) ? new FileInfo(file).Length : 0; }
        catch { return 0; }
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
}
