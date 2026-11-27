using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

public sealed partial class MediaConverterViewModel : ObservableObject, IPageAware
{
    private readonly MediaConverterService _mediaConverter;
    private readonly VideoTranscoder _videoTranscoder;
    private readonly SettingsService _settings;
    private readonly OperationService _operations;

    private INavigator? _navigator;
    private CancellationTokenSource? _cts;

    public MediaConverterViewModel(
        MediaConverterService mediaConverter,
        VideoTranscoder videoTranscoder,
        SettingsService settings,
        OperationService operations)
    {
        _mediaConverter = mediaConverter;
        _videoTranscoder = videoTranscoder;
        _settings = settings;
        _operations = operations;

        // Wire video transcoder delegate
        _mediaConverter.VideoTranscoder = async (src, dst, prog, ct) =>
            await _videoTranscoder.TranscodeToMp4Async(src, dst, VideoQuality, prog, ct).ConfigureAwait(false);

        // Wire animated GIF transcoder delegate
        _mediaConverter.GifTranscoder = async (src, dst, prog, ct) =>
            await _videoTranscoder.ConvertToGifAsync(src, dst, prog, ct).ConfigureAwait(false);

        SourceFolderPath = settings.Current.LastMediaConverterSource ?? "";
        DestinationFolderPath = settings.Current.LastMediaConverterDestination ?? "";
        SelectedTabIndex = settings.Current.MediaConverterTab;
        JpegQuality = settings.Current.HeicConversionQuality > 0 ? settings.Current.HeicConversionQuality : 92;
    }

    public void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;
    }

    // ---- Navigation ----

    [RelayCommand]
    private void Back() => _navigator?.GoTo(Page.Devices);

    // ---- Tabs / Modes ----

    [ObservableProperty]
    private int _selectedTabIndex = 0;

    partial void OnSelectedTabIndexChanged(int value)
    {
        _settings.Current.MediaConverterTab = value;
        _settings.Save();
        FilterVisibleItems();
    }

    // ---- Items ----

    private readonly List<MediaConverterItem> _allItems = new();

    public ObservableCollection<MediaConverterItem> VisibleItems { get; } = new();

    [ObservableProperty]
    private bool _hasItems;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _heicCount;

    [ObservableProperty]
    private int _videoCount;

    [ObservableProperty]
    private int _livePhotoCount;

    [ObservableProperty]
    private int _otherCount;

    // ---- Paths ----

    [ObservableProperty]
    private string _sourceFolderPath = "";

    [ObservableProperty]
    private string _destinationFolderPath = "";

    [ObservableProperty]
    private bool _saveInSourceFolder = true;

    [ObservableProperty]
    private bool _includeSubdirectories = true;

    [ObservableProperty]
    private bool _preserveSubdirectories = true;

    [ObservableProperty]
    private bool _deleteOriginals = false;

    // ---- Format Options ----

    [ObservableProperty]
    private string _photoFormat = "jpeg";

    [ObservableProperty]
    private int _jpegQuality = 92;

    [ObservableProperty]
    private string _videoQuality = "auto";

    [ObservableProperty]
    private LivePhotoConversionMode _livePhotoMode = LivePhotoConversionMode.UniversalJpegAndMp4;

    // ---- Conversion State ----

    [ObservableProperty]
    private bool _isConverting;

    [ObservableProperty]
    private double _conversionProgress;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string? _currentItemName;

    [ObservableProperty]
    private string? _summaryText;

    [ObservableProperty]
    private bool _hasCompleted;

    [ObservableProperty]
    private int _succeededCount;

    [ObservableProperty]
    private int _failedCount;

    // ---- Commands ----

    [RelayCommand]
    private void BrowseSourceFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc.Get("L.MediaConverter.PickSourceFolderTitle")
        };

        if (!string.IsNullOrEmpty(SourceFolderPath) && Directory.Exists(SourceFolderPath))
        {
            dialog.InitialDirectory = SourceFolderPath;
        }

        if (dialog.ShowDialog() == true)
        {
            SourceFolderPath = dialog.FolderName;
            _settings.Current.LastMediaConverterSource = dialog.FolderName;
            _settings.Save();
            ScanAndAddPaths(new[] { dialog.FolderName });
        }
    }

    [RelayCommand]
    private void BrowseSourceFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.Get("L.MediaConverter.PickSourceFilesTitle"),
            Multiselect = true,
            Filter = "Media Files|*.heic;*.heif;*.jpg;*.jpeg;*.png;*.mov;*.mp4;*.avi;*.mkv;*.wmv;*.webp|All Files|*.*"
        };

        if (dialog.ShowDialog() == true && dialog.FileNames.Length > 0)
        {
            ScanAndAddPaths(dialog.FileNames);
        }
    }

    [RelayCommand]
    private void BrowseDestinationFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc.Get("L.MediaConverter.PickDestFolderTitle")
        };

        if (!string.IsNullOrEmpty(DestinationFolderPath) && Directory.Exists(DestinationFolderPath))
        {
            dialog.InitialDirectory = DestinationFolderPath;
        }

        if (dialog.ShowDialog() == true)
        {
            DestinationFolderPath = dialog.FolderName;
            SaveInSourceFolder = false;
            _settings.Current.LastMediaConverterDestination = dialog.FolderName;
            _settings.Save();
        }
    }

    [RelayCommand]
    private void ClearItems()
    {
        if (IsConverting) return;
        _allItems.Clear();
        VisibleItems.Clear();
        UpdateItemCounts();
        HasCompleted = false;
        StatusText = null;
        SummaryText = null;
    }

    [RelayCommand]
    private void RemoveItem(MediaConverterItem? item)
    {
        if (item is null || IsConverting) return;
        _allItems.Remove(item);
        VisibleItems.Remove(item);
        UpdateItemCounts();
    }

    public void AddDroppedPaths(string[] paths)
    {
        if (IsConverting || paths == null || paths.Length == 0) return;
        if (string.IsNullOrEmpty(SourceFolderPath) && paths.Length == 1 && Directory.Exists(paths[0]))
        {
            SourceFolderPath = paths[0];
            _settings.Current.LastMediaConverterSource = paths[0];
            _settings.Save();
        }
        ScanAndAddPaths(paths);
    }

    private void ScanAndAddPaths(IEnumerable<string> paths)
    {
        var found = _mediaConverter.ScanItems(paths, IncludeSubdirectories);
        foreach (var item in found)
        {
            if (!_allItems.Any(i => i.SourcePath.Equals(item.SourcePath, StringComparison.OrdinalIgnoreCase)))
            {
                _allItems.Add(item);
            }
        }

        UpdateItemCounts();
        FilterVisibleItems();
        HasCompleted = false;
        StatusText = Loc.Format("L.MediaConverter.LoadedItems", _allItems.Count);
    }

    private void FilterVisibleItems()
    {
        VisibleItems.Clear();
        IEnumerable<MediaConverterItem> items = _allItems;

        switch (SelectedTabIndex)
        {
            case 0: // Photos (HEIC / HEIF / Stills, including Live Photo still component)
                items = _allItems.Where(i => i.Kind is MediaItemKind.HeicPhoto or MediaItemKind.OtherPhoto or MediaItemKind.LivePhotoPair);
                break;
            case 1: // Videos (standalone videos and Live Photo video clips)
                items = _allItems.Where(i => i.Kind is MediaItemKind.Video or MediaItemKind.LivePhotoPair);
                break;
            case 2: // Live Photos
                items = _allItems.Where(i => i.Kind == MediaItemKind.LivePhotoPair);
                break;
            case 3: // All files
            default:
                items = _allItems;
                break;
        }

        foreach (var it in items)
        {
            VisibleItems.Add(it);
        }
    }

    private void UpdateItemCounts()
    {
        TotalCount = _allItems.Count;
        HeicCount = _allItems.Count(i => i.Kind == MediaItemKind.HeicPhoto || (i.Kind == MediaItemKind.LivePhotoPair && MediaConverterService.IsHeic(i.Extension)));
        VideoCount = _allItems.Count(i => i.Kind == MediaItemKind.Video || i.Kind == MediaItemKind.LivePhotoPair);
        LivePhotoCount = _allItems.Count(i => i.Kind == MediaItemKind.LivePhotoPair);
        OtherCount = _allItems.Count(i => i.Kind == MediaItemKind.OtherPhoto);
        HasItems = TotalCount > 0;
    }

    [RelayCommand]
    private async Task ConvertAsync()
    {
        if (IsConverting || VisibleItems.Count == 0) return;

        var itemsToConvert = VisibleItems.ToList();
        if (itemsToConvert.Count == 0) return;

        IsConverting = true;
        HasCompleted = false;
        ConversionProgress = 0;
        SummaryText = null;
        StatusText = Loc.Get("L.MediaConverter.Starting");

        _cts = new CancellationTokenSource();
        var cts = _cts;

        var operation = _operations.Start(new Operation(
            OperationKind.MediaConverter,
            Page.MediaConverter,
            Loc.Get("L.MediaConverter.OpTitle"),
            Loc.Format("L.MediaConverter.OpDetail", itemsToConvert.Count),
            cancel: cts.Cancel));

        try
        {
            var effectiveLivePhotoMode = SelectedTabIndex switch
            {
                0 => LivePhotoConversionMode.PhotoOnlyJpeg,
                1 => LivePhotoConversionMode.VideoOnlyMp4,
                _ => LivePhotoMode
            };

            var options = new MediaConverterOptions
            {
                OutputFolder = SaveInSourceFolder ? null : DestinationFolderPath,
                SaveInSourceFolder = SaveInSourceFolder,
                PhotoFormat = PhotoFormat,
                JpegQuality = JpegQuality,
                VideoQuality = VideoQuality,
                LivePhotoMode = effectiveLivePhotoMode,
                DeleteOriginals = DeleteOriginals,
                IncludeSubdirectories = IncludeSubdirectories,
                PreserveSubdirectories = PreserveSubdirectories,
                PreserveTimestamps = true
            };

            var progress = new Progress<MediaConverterProgress>(p =>
            {
                ConversionProgress = p.OverallProgressPercent;
                CurrentItemName = p.CurrentItemName;
                SucceededCount = p.SucceededCount;
                FailedCount = p.FailedCount;
                StatusText = $"{p.CompletedCount}/{p.TotalCount}: {p.CurrentItemName}";

                operation.Progress = ConversionProgress;
                operation.Detail = StatusText;
            });

            var succeeded = await _mediaConverter.ConvertBatchAsync(
                itemsToConvert,
                options,
                SourceFolderPath,
                progress,
                cts.Token).ConfigureAwait(true);

            ConversionProgress = 100;
            HasCompleted = true;
            SummaryText = Loc.Format("L.MediaConverter.CompletedSummary", succeeded, itemsToConvert.Count);
            StatusText = SummaryText;

            // Finishing the operation dispatches Operations.OperationCompleted in ShellViewModel,
            // which handles the centralized desktop/in-app toast notification.
            operation.Finish(OperationState.Done, SummaryText);
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc.Get("L.MediaConverter.Cancelled");
            SummaryText = StatusText;
            operation.Finish(OperationState.Cancelled);
        }
        catch (Exception ex)
        {
            StatusText = Loc.Format("L.MediaConverter.Failed", ex.Message);
            SummaryText = StatusText;
            operation.Finish(OperationState.Failed, ex.Message);
            AppLog.Error("media converter batch error", ex);
        }
        finally
        {
            IsConverting = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private void OpenDestination()
    {
        try
        {
            string targetPath = !SaveInSourceFolder && !string.IsNullOrEmpty(DestinationFolderPath) && Directory.Exists(DestinationFolderPath)
                ? DestinationFolderPath
                : (!string.IsNullOrEmpty(SourceFolderPath) && Directory.Exists(SourceFolderPath)
                    ? SourceFolderPath
                    : (VisibleItems.FirstOrDefault()?.OutputPath != null && File.Exists(VisibleItems.First().OutputPath)
                        ? Path.GetDirectoryName(VisibleItems.First().OutputPath)!
                        : ""));

            if (!string.IsNullOrEmpty(targetPath) && Directory.Exists(targetPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetPath,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"media converter: cannot open destination: {ex.Message}");
        }
    }
}
