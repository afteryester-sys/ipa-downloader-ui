using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.App.Services;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;
using Microsoft.Win32;

namespace IPAStudio.App.ViewModels;

/// <summary>
/// "Export photos and videos from media": scans a chosen drive, folder, or Android phone
/// mounted as a plain file system, and copies whatever media it found onto the PC.
///
/// Scan and copy are two separate steps on purpose — the user asked for a look before
/// anything is written, so <see cref="ScanAsync"/> only ever reads, and <see cref="ExportAsync"/>
/// works from the list it built rather than walking the source again.
/// </summary>
public sealed partial class MediaExportViewModel : ObservableObject, IPageAware
{
    private readonly MediaExportService _mediaExport;
    private readonly SettingsService _settings;
    private readonly OperationService _operations;

    private INavigator? _navigator;
    private CancellationTokenSource? _cts;
    private MediaExportScanResult? _scanResult;

    public MediaExportViewModel(MediaExportService mediaExport, SettingsService settings, OperationService operations)
    {
        _mediaExport = mediaExport;
        _settings = settings;
        _operations = operations;

        SourcePath = settings.Current.LastMediaExportSource ?? "";
        DestinationPath = settings.Current.LastMediaExportDestination ?? "";
        _byFolder = settings.Current.MediaExportByFolder;
        _junkThresholdKb = settings.Current.MediaExportJunkThresholdKb;
        _skipJunk = _junkThresholdKb > 0;
    }

    public void OnNavigatedTo(INavigator navigator) => _navigator = navigator;

    // ---- Input ----

    [ObservableProperty]
    private string _sourcePath = "";

    [ObservableProperty]
    private string _destinationPath = "";

    /// <summary>True lays copies out one sub-folder per source group; false flattens everything
    /// into <see cref="DestinationPath"/> directly. Two radio buttons bind to this pair the same
    /// way the theme picker does.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SingleFolder))]
    private bool _byFolder;

    public bool SingleFolder
    {
        get => !ByFolder;
        set => ByFolder = !value;
    }

    [ObservableProperty]
    private bool _skipJunk = true;

    [ObservableProperty]
    private int _junkThresholdKb = 20;

    // ---- State ----

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isExporting;

    public bool IsBusy => IsScanning || IsExporting;

    [ObservableProperty]
    private double _exportProgress;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string? _errorText;

    /// <summary>True once a scan has completed, whether or not it found anything — this is
    /// what unlocks the export step, and what tells the "nothing found" message apart from
    /// the page's very first, never-scanned state.</summary>
    [ObservableProperty]
    private bool _hasScanned;

    [ObservableProperty]
    private string _totalFoundText = "";

    [ObservableProperty]
    private string _skippedJunkText = "";

    public ObservableCollection<MediaExportGroupRow> Groups { get; } = new();

    // ---- Commands ----

    [RelayCommand]
    private void GoBack()
    {
        _cts?.Cancel();
        _navigator?.GoBack();
    }

    [RelayCommand]
    private void GoHome()
    {
        _cts?.Cancel();
        _navigator?.GoHome();
    }

    [RelayCommand]
    private void BrowseSource()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc.Get("L.MediaExport.PickSourceTitle"),
            InitialDirectory = Directory.Exists(SourcePath) ? SourcePath : "",
        };
        if (dialog.ShowDialog() != true) return;

        SourcePath = dialog.FolderName;
        _settings.Current.LastMediaExportSource = SourcePath;
        _settings.Save();
        ErrorText = null;

        // A new source invalidates whatever the last scan found.
        _scanResult = null;
        HasScanned = false;
        Groups.Clear();
        TotalFoundText = "";
        SkippedJunkText = "";
    }

    [RelayCommand]
    private void BrowseDestination()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc.Get("L.MediaExport.PickDestinationTitle"),
            InitialDirectory = Directory.Exists(DestinationPath) ? DestinationPath : "",
        };
        if (dialog.ShowDialog() != true) return;

        DestinationPath = dialog.FolderName;
        _settings.Current.LastMediaExportDestination = DestinationPath;
        _settings.Save();
        ErrorText = null;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private bool CanScan() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(SourcePath) || !Directory.Exists(SourcePath))
        {
            ErrorText = Loc.Get("L.MediaExport.NeedSource");
            return;
        }

        _settings.Current.MediaExportJunkThresholdKb = SkipJunk ? Math.Max(0, JunkThresholdKb) : 0;
        _settings.Save();

        _cts = new CancellationTokenSource();
        IsScanning = true;
        ErrorText = null;
        HasScanned = false;
        Groups.Clear();
        TotalFoundText = "";
        SkippedJunkText = "";
        StatusText = Loc.Get("L.MediaExport.Scanning");

        var operation = _operations.Start(new Operation(
            OperationKind.MediaExport,
            Page.MediaExport,
            Loc.Get("L.Ops.Kind.MediaExport"),
            SourcePath,
            cancel: _cts.Cancel));

        try
        {
            var minBytes = SkipJunk ? (long)Math.Max(0, JunkThresholdKb) * 1024 : 0;
            var progress = new Progress<string>(group =>
            {
                StatusText = string.IsNullOrEmpty(group)
                    ? Loc.Get("L.MediaExport.Scanning")
                    : Loc.Format("L.MediaExport.ScanningGroup", group);
                operation.Detail = StatusText ?? "";
            });

            _scanResult = await _mediaExport.ScanAsync(SourcePath, minBytes, progress, _cts.Token).ConfigureAwait(true);
            ShowScan(_scanResult);
            operation.Finish(OperationState.Done, TotalFoundText);
            AppLog.Info($"media export: scanned '{SourcePath}', found {_scanResult.TotalCount} " +
                        $"({_scanResult.TotalPhotos} photos, {_scanResult.TotalVideos} videos), " +
                        $"skipped {_scanResult.SkippedJunkCount} as junk.");
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc.Get("L.MediaExport.Cancelled");
            operation.Finish(OperationState.Cancelled);
        }
        catch (Exception ex)
        {
            ErrorText = Loc.Format("L.MediaExport.ScanFailed", ex.Message);
            StatusText = null;
            operation.Finish(OperationState.Failed, ex.Message);
            AppLog.Error("media export: scan failed.", ex);
        }
        finally
        {
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void ShowScan(MediaExportScanResult scan)
    {
        Groups.Clear();
        foreach (var group in scan.Groups)
            Groups.Add(new MediaExportGroupRow(
                string.IsNullOrEmpty(group.Name) ? Loc.Get("L.MediaExport.RootGroup") : group.Name,
                group.PhotoCount, group.VideoCount, FormatSize(group.TotalBytes)));

        HasScanned = true;
        TotalFoundText = scan.TotalCount == 0
            ? Loc.Get("L.MediaExport.NothingFound")
            : Loc.Format("L.MediaExport.FoundTotal", scan.TotalPhotos, scan.TotalVideos, FormatSize(scan.TotalBytes));

        SkippedJunkText = scan.SkippedJunkCount > 0
            ? Loc.Format("L.MediaExport.SkippedJunk", scan.SkippedJunkCount)
            : "";

        StatusText = null;
    }

    private bool CanExport() => !IsBusy && _scanResult is { TotalCount: > 0 } && !string.IsNullOrWhiteSpace(DestinationPath);

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (_scanResult is not { TotalCount: > 0 } scan) return;

        if (string.IsNullOrWhiteSpace(DestinationPath))
        {
            ErrorText = Loc.Get("L.MediaExport.NeedDestination");
            return;
        }

        _cts = new CancellationTokenSource();
        IsExporting = true;
        ExportProgress = 0;
        ErrorText = null;
        StatusText = Loc.Get("L.MediaExport.Exporting");

        var operation = _operations.Start(new Operation(
            OperationKind.MediaExport,
            Page.MediaExport,
            Loc.Get("L.Ops.Kind.MediaExport"),
            DestinationPath,
            cancel: _cts.Cancel));

        try
        {
            var mode = ByFolder ? MediaExportCopyMode.ByFolder : MediaExportCopyMode.SingleFolder;
            var progress = new Progress<MediaExportProgress>(p =>
            {
                if (p.Total > 0) ExportProgress = 100.0 * p.Completed / p.Total;
                StatusText = string.IsNullOrEmpty(p.CurrentItem)
                    ? StatusText
                    : $"{p.Completed}/{p.Total}: {p.CurrentItem}";

                operation.Progress = ExportProgress;
                operation.Detail = StatusText ?? "";
            });

            var copied = await _mediaExport.CopyAsync(
                scan, DestinationPath, mode, Loc.Get("L.MediaExport.RootGroup"),
                progress, _cts.Token).ConfigureAwait(true);

            ExportProgress = 100;
            StatusText = Loc.Format("L.MediaExport.Exported", copied, scan.TotalCount, DestinationPath);
            operation.Finish(OperationState.Done, StatusText);
            AppLog.Info($"media export: copied {copied}/{scan.TotalCount} file(s) to '{DestinationPath}' ({mode}).");
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc.Get("L.MediaExport.Cancelled");
            operation.Finish(OperationState.Cancelled);
        }
        catch (Exception ex)
        {
            ErrorText = Loc.Format("L.MediaExport.ExportFailed", ex.Message);
            StatusText = null;
            operation.Finish(OperationState.Failed, ex.Message);
            AppLog.Error("media export: copy failed.", ex);
        }
        finally
        {
            IsExporting = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void OpenDestination()
    {
        try
        {
            if (Directory.Exists(DestinationPath))
                Process.Start(new ProcessStartInfo(DestinationPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"media export: could not open the destination folder: {ex.Message}");
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var u = 0;
        while (size >= 1024 && u < units.Length - 1) { size /= 1024; u++; }
        return $"{size:0.#} {units[u]}";
    }
}

/// <summary>One row of the scan breakdown: a source folder and what was found in it.</summary>
public sealed record MediaExportGroupRow(string Name, int PhotoCount, int VideoCount, string SizeText);
