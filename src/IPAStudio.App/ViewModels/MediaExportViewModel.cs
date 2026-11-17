using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
/// mounted as a file system / MTP, with deep probing of Android folder structures,
/// media type filtering (Photos / Videos / All), and flexible organization (by category,
/// by source folder, by date, or flat).
/// </summary>
public sealed partial class MediaExportViewModel : ObservableObject, IPageAware
{
    private readonly MediaExportService _mediaExport;
    private readonly SettingsService _settings;
    private readonly OperationService _operations;

    private INavigator? _navigator;
    private CancellationTokenSource? _cts;
    private MediaExportScanResult? _rawScanResult;
    private MediaExportScanResult? _scanResult;

    public MediaExportViewModel(MediaExportService mediaExport, SettingsService settings, OperationService operations)
    {
        _mediaExport = mediaExport;
        _settings = settings;
        _operations = operations;

        SourcePath = settings.Current.LastMediaExportSource ?? "";
        DestinationPath = settings.Current.LastMediaExportDestination ?? "";
        _junkThresholdKb = settings.Current.MediaExportJunkThresholdKb;
        _skipJunk = _junkThresholdKb > 0;
        _filterType = (MediaExportFilterType)Math.Clamp(settings.Current.MediaExportFilter, 0, 2);
        _organization = (MediaExportOrganization)Math.Clamp(settings.Current.MediaExportOrganization, 0, 3);
    }

    public void OnNavigatedTo(INavigator navigator) => _navigator = navigator;

    // ---- Input ----

    [ObservableProperty]
    private string _sourcePath = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private string _destinationPath = "";

    // ---- Media Type Filter ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterAll))]
    [NotifyPropertyChangedFor(nameof(FilterPhotosOnly))]
    [NotifyPropertyChangedFor(nameof(FilterVideosOnly))]
    private MediaExportFilterType _filterType = MediaExportFilterType.All;

    public bool FilterAll
    {
        get => FilterType == MediaExportFilterType.All;
        set { if (value) { FilterType = MediaExportFilterType.All; SaveFilterSettings(); UpdateFilteredView(); } }
    }

    public bool FilterPhotosOnly
    {
        get => FilterType == MediaExportFilterType.PhotosOnly;
        set { if (value) { FilterType = MediaExportFilterType.PhotosOnly; SaveFilterSettings(); UpdateFilteredView(); } }
    }

    public bool FilterVideosOnly
    {
        get => FilterType == MediaExportFilterType.VideosOnly;
        set { if (value) { FilterType = MediaExportFilterType.VideosOnly; SaveFilterSettings(); UpdateFilteredView(); } }
    }

    private void SaveFilterSettings()
    {
        _settings.Current.MediaExportFilter = (int)FilterType;
        _settings.Save();
    }

    private void UpdateFilteredView()
    {
        if (_rawScanResult is null) return;

        var filteredFiles = FilterType switch
        {
            MediaExportFilterType.PhotosOnly => _rawScanResult.Files.Where(f => !f.IsVideo).ToList(),
            MediaExportFilterType.VideosOnly => _rawScanResult.Files.Where(f => f.IsVideo).ToList(),
            _ => _rawScanResult.Files.ToList()
        };

        _scanResult = _mediaExport.BuildResultFromFiles(filteredFiles, _rawScanResult.SkippedJunkCount, Loc.Get);
        ShowScan(_scanResult);
    }

    // ---- Folder Organization ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OrgByCategory))]
    [NotifyPropertyChangedFor(nameof(OrgByFolder))]
    [NotifyPropertyChangedFor(nameof(OrgByDate))]
    [NotifyPropertyChangedFor(nameof(OrgSingleFolder))]
    [NotifyPropertyChangedFor(nameof(ByFolder))]
    [NotifyPropertyChangedFor(nameof(SingleFolder))]
    private MediaExportOrganization _organization = MediaExportOrganization.ByCategory;

    public bool OrgByCategory
    {
        get => Organization == MediaExportOrganization.ByCategory;
        set { if (value) SetOrganization(MediaExportOrganization.ByCategory); }
    }

    public bool OrgByFolder
    {
        get => Organization == MediaExportOrganization.ByFolder;
        set { if (value) SetOrganization(MediaExportOrganization.ByFolder); }
    }

    public bool OrgByDate
    {
        get => Organization == MediaExportOrganization.ByDate;
        set { if (value) SetOrganization(MediaExportOrganization.ByDate); }
    }

    public bool OrgSingleFolder
    {
        get => Organization == MediaExportOrganization.SingleFolder;
        set { if (value) SetOrganization(MediaExportOrganization.SingleFolder); }
    }

    /// <summary>Backwards-compatible wrapper for settings and bindings.</summary>
    public bool ByFolder
    {
        get => Organization == MediaExportOrganization.ByFolder;
        set { if (value) SetOrganization(MediaExportOrganization.ByFolder); }
    }

    public bool SingleFolder
    {
        get => Organization == MediaExportOrganization.SingleFolder;
        set { if (value) SetOrganization(MediaExportOrganization.SingleFolder); }
    }

    private void SetOrganization(MediaExportOrganization org)
    {
        Organization = org;
        _settings.Current.MediaExportOrganization = (int)org;
        _settings.Current.MediaExportByFolder = org == MediaExportOrganization.ByFolder;
        _settings.Save();
        RefreshDisplayedGroups();
    }

    // ---- Junk Options ----

    [ObservableProperty]
    private bool _skipJunk = true;

    [ObservableProperty]
    private int _junkThresholdKb = 20;

    // ---- State ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool _isExporting;

    public bool IsBusy => IsScanning || IsExporting;

    [ObservableProperty]
    private double _exportProgress;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string? _errorText;

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
        _rawScanResult = null;
        _scanResult = null;
        HasScanned = false;
        Groups.Clear();
        TotalFoundText = "";
        SkippedJunkText = "";
        ExportCommand.NotifyCanExecuteChanged();
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
        ExportCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private bool CanScan() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(SourcePath) || !MediaExportService.IsSourceAccessible(SourcePath))
        {
            ErrorText = Loc.Get("L.MediaExport.NeedSource");
            return;
        }

        _settings.Current.MediaExportJunkThresholdKb = SkipJunk ? Math.Max(0, JunkThresholdKb) : 0;
        _settings.Current.MediaExportFilter = (int)FilterType;
        _settings.Current.MediaExportOrganization = (int)Organization;
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

            _rawScanResult = await _mediaExport.ScanAsync(
                SourcePath,
                minBytes,
                MediaExportFilterType.All,
                progress,
                Loc.Get,
                _cts.Token).ConfigureAwait(true);

            UpdateFilteredView();
            operation.Finish(OperationState.Done, TotalFoundText);
            AppLog.Info($"media export: scanned '{SourcePath}', found {_rawScanResult.TotalCount} " +
                        $"({_rawScanResult.TotalPhotos} photos, {_rawScanResult.TotalVideos} videos), " +
                        $"filter: {FilterType}, skipped {_rawScanResult.SkippedJunkCount} as junk.");
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
        RefreshDisplayedGroups();
        HasScanned = true;

        if (scan.TotalCount == 0)
        {
            TotalFoundText = Loc.Get("L.MediaExport.NothingFound");
        }
        else if (FilterType == MediaExportFilterType.PhotosOnly)
        {
            TotalFoundText = Loc.Format("L.MediaExport.FoundPhotosOnly", scan.TotalPhotos, FormatSize(scan.TotalBytes));
        }
        else if (FilterType == MediaExportFilterType.VideosOnly)
        {
            TotalFoundText = Loc.Format("L.MediaExport.FoundVideosOnly", scan.TotalVideos, FormatSize(scan.TotalBytes));
        }
        else
        {
            TotalFoundText = Loc.Format("L.MediaExport.FoundTotal", scan.TotalPhotos, scan.TotalVideos, FormatSize(scan.TotalBytes));
        }

        SkippedJunkText = scan.SkippedJunkCount > 0
            ? Loc.Format("L.MediaExport.SkippedJunk", scan.SkippedJunkCount)
            : "";

        StatusText = null;
        ExportCommand.NotifyCanExecuteChanged();
    }

    private void RefreshDisplayedGroups()
    {
        if (_scanResult is null) return;
        Groups.Clear();

        var sourceList = Organization switch
        {
            MediaExportOrganization.ByCategory => _scanResult.CategoryGroups,
            MediaExportOrganization.ByDate => _scanResult.DateGroups,
            _ => _scanResult.Groups
        };

        foreach (var group in sourceList)
        {
            Groups.Add(new MediaExportGroupRow(
                string.IsNullOrEmpty(group.Name) ? Loc.Get("L.MediaExport.RootGroup") : group.Name,
                group.PhotoCount, group.VideoCount, FormatSize(group.TotalBytes)));
        }
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
                scan,
                DestinationPath,
                Organization,
                Loc.Get("L.MediaExport.RootGroup"),
                progress,
                _cts.Token).ConfigureAwait(true);

            ExportProgress = 100;
            StatusText = scan.TotalVideos > 0
                ? Loc.Format("L.MediaExport.ExportedWithCounts", copied, scan.TotalPhotos, scan.TotalVideos, DestinationPath)
                : Loc.Format("L.MediaExport.Exported", copied, scan.TotalCount, DestinationPath);
            operation.Finish(OperationState.Done, StatusText);
            AppLog.Info($"media export: copied {copied}/{scan.TotalCount} file(s) to '{DestinationPath}' ({Organization}).");
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

/// <summary>One row of the scan breakdown: a category/folder and what was found in it.</summary>
public sealed record MediaExportGroupRow(string Name, int PhotoCount, int VideoCount, string SizeText);
