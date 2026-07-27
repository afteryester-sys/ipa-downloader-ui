using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;
using Microsoft.Win32;

namespace IPAStudio.App.ViewModels;

/// <summary>
/// "Скачать IPA": enter a Bundle ID, pick a folder, get the IPA signed with the
/// signed-in Apple ID saved straight into that folder.
///
/// Deliberately independent of the queue and of any connected device — the point is
/// "a new app came out, I want the file now", which has nothing to do with installing
/// onto a phone. It therefore does NOT go through <see cref="QueueService"/> (that
/// pipeline requires a target device) and calls <see cref="DownloadService"/> directly.
/// </summary>
public sealed partial class DirectDownloadViewModel : ObservableObject, IPageAware
{
    private readonly CatalogService _catalog;
    private readonly DownloadService _download;
    private readonly AuthService _auth;
    private readonly SettingsService _settings;
    private INavigator? _navigator;

    private CancellationTokenSource? _cts;

    public DirectDownloadViewModel(
        CatalogService catalog,
        DownloadService download,
        AuthService auth,
        SettingsService settings)
    {
        _catalog = catalog;
        _download = download;
        _auth = auth;
        _settings = settings;

        // Reuse the last folder so a user grabbing several apps in a row picks once.
        DestinationFolder = settings.Current.LastDirectDownloadFolder ?? "";
    }

    // ---- Input ----

    [ObservableProperty]
    private string _bundleId = "";

    [ObservableProperty]
    private string _destinationFolder = "";

    // ---- Resolved app ----

    /// <summary>The app found for <see cref="BundleId"/>, or null before a lookup.</summary>
    [ObservableProperty]
    private AppEntry? _foundApp;

    [ObservableProperty]
    private string? _foundIconUrl;

    // ---- State ----

    [ObservableProperty]
    private bool _isLookingUp;

    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    [ObservableProperty]
    private bool _isDownloading;

    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    [ObservableProperty]
    private double _progress;

    /// <summary>
    /// True while a transfer is running with no percentage to show. Apps missing from
    /// the App Store catalog have no known size, so ipatool reports bytes only and
    /// <see cref="Progress"/> stays at 0 for the whole download — a bar that just sat
    /// empty and looked broken. An animated bar states honestly that work is happening
    /// but its extent is unknown.
    /// </summary>
    public bool IsProgressIndeterminate => IsDownloading && Progress <= 0;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string? _errorText;

    /// <summary>Path of the finished file; drives the "Открыть папку" button.</summary>
    [ObservableProperty]
    private string? _savedPath;

    public bool IsSignedIn => _auth.IsAuthenticated;

    public string? AccountEmail => _auth.CurrentAccount?.Email;

    /// <summary>
    /// Refreshes the sign-in dependent parts of the UI. This viewmodel is a singleton,
    /// so the user may well have signed in on another screen since it was constructed
    /// and the cached values would otherwise be stale.
    /// </summary>
    public void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(AccountEmail));
    }

    // ---- Commands ----

    [RelayCommand]
    private void GoBack()
    {
        // Abort any transfer in flight: leaving the page hides the only progress and
        // cancel UI, so a download left running would be invisible and unstoppable.
        CancelDownload();
        _navigator?.GoBack();
    }

    [RelayCommand]
    private void SignIn() => _navigator?.GoTo(Page.Login);

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Куда сохранить IPA",
            // Reopen where they last saved. Only set it when the folder still exists —
            // pointing at a deleted path makes the dialog open somewhere arbitrary.
            InitialDirectory = Directory.Exists(DestinationFolder) ? DestinationFolder : "",
        };

        if (dialog.ShowDialog() != true) return;

        DestinationFolder = dialog.FolderName;
        _settings.Current.LastDirectDownloadFolder = DestinationFolder;
        _settings.Save();
        ErrorText = null;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        // Select the file inside Explorer rather than just opening the folder, so the
        // user sees exactly which file was produced.
        try
        {
            if (!string.IsNullOrEmpty(SavedPath) && File.Exists(SavedPath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SavedPath}\"") { UseShellExecute = true });
            else if (Directory.Exists(DestinationFolder))
                Process.Start(new ProcessStartInfo(DestinationFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Не удалось открыть папку: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves the Bundle ID to a real App Store entry. Kept separate from the download
    /// so the user can confirm they got the app they meant before spending bandwidth —
    /// a typo'd bundle id is otherwise indistinguishable from a missing app.
    /// </summary>
    [RelayCommand]
    private async Task LookupAsync()
    {
        var id = BundleId.Trim();
        if (id.Length == 0)
        {
            ErrorText = Str("L.Direct.NeedBundleId");
            return;
        }

        IsLookingUp = true;
        ErrorText = null;
        StatusText = Str("L.Direct.Searching");
        FoundApp = null;
        FoundIconUrl = null;
        SavedPath = null;

        try
        {
            var results = await _catalog.SearchByBundleIdAsync(id).ConfigureAwait(true);
            var app = results.FirstOrDefault();

            if (app is null)
            {
                StatusText = null;
                ErrorText = Str("L.Direct.NotFound");
                return;
            }

            FoundApp = app;
            FoundIconUrl = app.IconUrl;
            StatusText = null;
            AppLog.Info($"Direct download: найдено «{app.Name}» ({app.BundleId}) id={app.AppStoreId}");
        }
        catch (Exception ex)
        {
            StatusText = null;
            ErrorText = ex.Message;
            AppLog.Warn($"Direct download lookup failed: {ex.Message}");
        }
        finally
        {
            IsLookingUp = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (FoundApp is null)
        {
            // Let the button work as "find and download" when the user typed an id and
            // pressed download without pressing Найти first.
            await LookupAsync().ConfigureAwait(true);
            if (FoundApp is null) return;
        }

        if (!_auth.IsAuthenticated)
        {
            ErrorText = Str("L.Direct.NeedLogin");
            return;
        }

        if (string.IsNullOrWhiteSpace(DestinationFolder))
        {
            ErrorText = Str("L.Direct.NeedFolder");
            return;
        }

        _cts = new CancellationTokenSource();
        IsDownloading = true;
        ErrorText = null;
        SavedPath = null;
        Progress = 0;
        StatusText = Str("L.Direct.Downloading");

        // Fully qualified: this class also exposes a `Progress` property for the bar,
        // and the bare type name next to it reads as a mistake even where it compiles.
        var progress = new System.Progress<DownloadProgress>(p =>
        {
            Progress = p.Percent;

            if (p.Connecting)
            {
                var seconds = (int)p.Elapsed.TotalSeconds;
                StatusText = seconds >= 2
                    ? $"Соединение с App Store… {seconds} с"
                    : "Соединение с App Store…";
            }
            else if (p.Finalizing)
            {
                StatusText = $"Упаковка и подпись… ({FormatBytes(p.DownloadedBytes)})";
            }
            else if (p.TotalBytes > 0)
            {
                var speed = p.SpeedBps > 0 ? $" · {FormatBytes((long)p.SpeedBps)}/с" : "";
                StatusText = $"{p.Percent:0.0}% · {FormatBytes(p.DownloadedBytes)} / {FormatBytes(p.TotalBytes)}{speed}";
            }
            else
            {
                // Same wording as the queue screen: label the number as bytes-so-far, so
                // it cannot be misread as the (unknown) total sitting next to it.
                var speed = p.SpeedBps > 0 ? $" · {FormatBytes((long)p.SpeedBps)}/с" : "";
                StatusText = $"Скачано {FormatBytes(p.DownloadedBytes)}{speed} · всего неизвестно";
            }
        });

        try
        {
            var result = await _download.DownloadAsync(
                FoundApp!,
                autoPurchase: true,
                progress,
                destinationFolder: DestinationFolder,
                ct: _cts.Token).ConfigureAwait(true);

            if (result.Success && result.IpaPath is not null)
            {
                SavedPath = result.IpaPath;
                Progress = 100;
                StatusText = $"{Str("L.Direct.Done")} {Path.GetFileName(result.IpaPath)}";

                // A successful download proves ownership; persist it so the app picker
                // shows the right badge without another Apple round-trip.
                FoundApp!.License = LicenseState.Owned;
                _settings.MarkOwned(FoundApp!.AppStoreId);
                AppLog.Info($"Direct download OK: {result.IpaPath}");
            }
            else
            {
                StatusText = null;
                ErrorText = result.Error == DownloadService.SessionExpiredMessage
                    ? Str("L.Direct.NeedLogin")
                    : result.Error ?? "Download failed";

                // The session died mid-download: send the user to the login screen
                // instead of leaving a dead-end error on screen.
                if (result.Error == DownloadService.SessionExpiredMessage)
                {
                    OnPropertyChanged(nameof(IsSignedIn));
                    _navigator?.GoTo(Page.Login);
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = null;
            Progress = 0;
        }
        catch (Exception ex)
        {
            StatusText = null;
            ErrorText = ex.Message;
            AppLog.Warn($"Direct download failed: {ex.Message}");
        }
        finally
        {
            IsDownloading = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void CancelDownload() => _cts?.Cancel();

    // ---- Helpers ----

    private static string Str(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as string ?? key;

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}
