using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.App.Infrastructure;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;
using Microsoft.Win32;

namespace IPAStudio.App.ViewModels;

/// <summary>Selectable wrapper around a Camera Roll media file.</summary>
public sealed partial class PhotoItemViewModel : ObservableObject
{
    public PhotoItem Item { get; }

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Small thumbnail (64 px wide) loaded asynchronously after the list is built.</summary>
    [ObservableProperty]
    private BitmapImage? _thumbnail;

    /// <summary>
    /// True once a thumbnail has been fetched and decoded for this item, whether or not
    /// it produced an image. Stops the loader from re-reading files that yield nothing
    /// (a truncated or unsupported file) every time they scroll back into view. Reset
    /// when a thumbnail is evicted from the cache, so a retry is still possible.
    /// </summary>
    public bool ThumbnailAttempted { get; set; }

    public string FileName => Item.FileName;

    /// <summary>Raw DCIM folder name (e.g. "100APPLE") — used for filtering.</summary>
    public string Album => Item.Album;

    /// <summary>
    /// Human-readable album label shown in the UI.
    /// iOS stores Camera Roll photos in numbered DCIM sub-folders (100APPLE,
    /// 101APPLE, …). We can't read real album names over AFC, so we display
    /// "Камера" (or "Камера 2" for the second folder, etc.).
    /// </summary>
    public string FriendlyAlbumName => MakeFriendlyAlbumNameStatic(Item.Album);

    public static string MakeFriendlyAlbumNameStatic(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return "Камера";
        // DCIM sub-folder convention: "100APPLE", "101APPLE", … or "100CLOUD", etc.
        // iOS uses 100APPLE for the primary Camera Roll; higher numbers are additional
        // rolls (burst, imports, screen recordings that overflowed, etc.). We don't
        // have access to the real album names via AFC, so we show the folder number
        // in a human-friendly way: "Камера" for 100, "Камера (101)" for the rest.
        if (folder.Length >= 3 && int.TryParse(folder[..3], out var num))
        {
            if (num == 100) return "Камера";
            // Show the numeric index so users can distinguish multiple rolls
            // without inventing fake sequential names (39, 40, …).
            return $"Камера ({num})";
        }
        return folder;
    }

    public bool IsVideo => Item.IsVideo;

    /// <summary>
    /// File size, or an em dash until the device has been asked. Listing skips the
    /// per-file stat so the grid can appear at once, so this shows a placeholder for a
    /// moment instead of claiming a misleading "0 Б".
    /// </summary>
    public string SizeText => Item.HasMetadata ? FormatSize(Item.SizeBytes) : "—";

    public string DateText => Item.ModifiedUtc?.LocalDateTime.ToString("dd.MM.yyyy HH:mm") ?? "";

    public PhotoItemViewModel(PhotoItem item) => Item = item;

    /// <summary>
    /// Applies size/date fetched in the background. Must be called on the UI thread:
    /// it writes to the shared item and raises change notifications.
    /// </summary>
    public void ApplyMetadata(long sizeBytes, DateTimeOffset? modifiedUtc)
    {
        Item.SizeBytes = sizeBytes;
        Item.ModifiedUtc = modifiedUtc;
        Item.HasMetadata = true;
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(DateText));
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}

/// <summary>
/// Camera Roll browser for a device: view, multi-select, export to PC and import
/// from PC, grouped by DCIM album folder and filterable by media type.
/// </summary>
public sealed partial class PhotosViewModel : ObservableObject, IPageAware
{
    private readonly PhotoService _photos;
    private INavigator? _navigator;
    private Device? _device;
    private CancellationTokenSource? _cts;
    /// <summary>Maps friendly album label (shown in the picker) to raw DCIM folder name.</summary>
    private readonly Dictionary<string, string> _albumFriendlyToRaw = new();

    /// <summary>
    /// Item lookup by AFC path, so a metadata batch arriving from the background can be
    /// matched to its row without scanning the whole collection per result.
    /// </summary>
    private readonly Dictionary<string, PhotoItemViewModel> _byRemotePath = new();

    /// <summary>Cancels the background size/date pass when the list is rebuilt.</summary>
    private CancellationTokenSource? _metaCts;

    /// <summary>Thumbnail decode width, in pixels. Tiles render at 130 wide.</summary>
    private const int ThumbnailWidth = 160;

    /// <summary>
    /// Upper bound on decoded thumbnails kept in memory. At ~160 px each this caps the
    /// cache in the low tens of MB, so scrolling a 10 000 photo roll can't grow without
    /// limit. Comfortably larger than any viewport, so normal scrolling never evicts
    /// something about to be shown again.
    /// </summary>
    private const int MaxCachedThumbnails = 400;

    /// <summary>Most-recently-seen thumbnails first; the tail is evicted.</summary>
    private readonly LinkedList<PhotoItemViewModel> _cacheOrder = new();
    private readonly Dictionary<PhotoItemViewModel, LinkedListNode<PhotoItemViewModel>> _cacheNodes = new();

    /// <summary>Visible row range last reported by the view, in PhotosView order.</summary>
    private int _visibleFirst;
    private int _visibleLast = -1;

    /// <summary>Wakes the thumbnail loader when the viewport changes.</summary>
    private readonly AsyncAutoResetEvent _viewportChanged = new();

    /// <summary>Consecutive HEIC batches that decoded to nothing.</summary>
    private int _heicFailedBatches;

    /// <summary>
    /// Refilled in one shot via <see cref="BulkObservableCollection{T}.ReplaceAll"/> so a
    /// large roll costs one refresh of <see cref="PhotosView"/> rather than thousands.
    /// </summary>
    public BulkObservableCollection<PhotoItemViewModel> Photos { get; } = new();
    public ICollectionView PhotosView { get; }

    /// <summary>Album folders discovered on the device, plus "" for all.</summary>
    public ObservableCollection<string> Albums { get; } = new();

    /// <summary>Media type filter options.</summary>
    public ObservableCollection<string> MediaTypes { get; } = new() { "Все", "Фото", "Видео" };

    [ObservableProperty]
    private string _deviceName = "";

    [ObservableProperty]
    private string? _selectedAlbum;

    [ObservableProperty]
    private string _selectedMediaType = "Все";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportSelectedCommand))]
    private int _selectedCount;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isTransferring;

    [ObservableProperty]
    private double _transferProgress;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private PhotoItemViewModel? _selectedPhoto;

    [ObservableProperty]
    private BitmapImage? _previewImage;

    [ObservableProperty]
    private bool _previewUnavailable;

    /// <summary>True = list layout; false = tile/grid layout.</summary>
    [ObservableProperty]
    private bool _isListView = true;

    /// <summary>
    /// True when HEIC files can't be decoded on this PC, i.e. the OS HEIF codec is
    /// missing. Drives a one-line hint with an install link — without it HEIC tiles
    /// would just stay blank with no explanation.
    /// </summary>
    [ObservableProperty]
    private bool _isHeicCodecMissing;

    public bool IsGridView => !IsListView;

    partial void OnIsListViewChanged(bool value) => OnPropertyChanged(nameof(IsGridView));

    private CancellationTokenSource? _thumbCts;

    public PhotosViewModel(PhotoService photos)
    {
        _photos = photos;
        PhotosView = CollectionViewSource.GetDefaultView(Photos);
        PhotosView.Filter = Filter;
    }

    public void SetDevice(Device device) => _device = device;

    /// <summary>
    /// Keeps <see cref="SelectedCount"/> up to date by adjusting it in place.
    ///
    /// Previously this recounted the entire collection on every toggle, which made
    /// "select all" quadratic — on a few thousand photos that meant millions of
    /// comparisons and a visibly frozen window for a single click.
    /// </summary>
    private void OnPhotoItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PhotoItemViewModel.IsSelected)) return;
        if (sender is not PhotoItemViewModel item) return;
        SelectedCount += item.IsSelected ? 1 : -1;
    }

    public void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;
        DeviceName = _device?.Name ?? "";
        _ = LoadAsync();
    }

    partial void OnSelectedAlbumChanged(string? value) => PhotosView.Refresh();
    partial void OnSelectedMediaTypeChanged(string value) => PhotosView.Refresh();

    partial void OnSelectedPhotoChanged(PhotoItemViewModel? value) => _ = LoadPreviewAsync(value);

    private bool Filter(object obj)
    {
        if (obj is not PhotoItemViewModel p) return false;
        // "Все альбомы" (or empty) means show all.
        if (!string.IsNullOrEmpty(SelectedAlbum) && SelectedAlbum != "Все альбомы")
        {
            // Resolve friendly name back to raw DCIM folder name for comparison.
            var rawFolder = _albumFriendlyToRaw.TryGetValue(SelectedAlbum, out var raw)
                ? raw : SelectedAlbum;
            if (p.Album != rawFolder) return false;
        }
        return SelectedMediaType switch
        {
            "Фото" => !p.IsVideo,
            "Видео" => p.IsVideo,
            _ => true,
        };
    }

    private async Task LoadAsync()
    {
        if (_device is null) return;

        // Cancel any running thumbnail loader before rebuilding the list.
        _thumbCts?.Cancel();
        _thumbCts?.Dispose();
        _thumbCts = new CancellationTokenSource();

        // Same for the size/date pass, otherwise a previous run would keep writing
        // into rows that no longer exist.
        _metaCts?.Cancel();
        _metaCts?.Dispose();
        _metaCts = new CancellationTokenSource();

        IsBusy = true;
        StatusText = "Чтение медиатеки…";

        foreach (var old in Photos) old.PropertyChanged -= OnPhotoItemPropertyChanged;
        Photos.Clear();
        _byRemotePath.Clear();
        // The cache holds strong references to the old rows; without this a reload would
        // pin every previous item (and its decoded bitmap) in memory.
        _cacheOrder.Clear();
        _cacheNodes.Clear();
        _heicFailedBatches = 0;
        // Selection is tracked incrementally now, so it has to be reset explicitly
        // when the rows behind it disappear.
        SelectedCount = 0;
        try
        {
            var items = await _photos.ListCameraRollAsync(_device.Udid);

            // Build the rows off to the side, then publish them in a single Reset. Doing
            // this inside PhotosView.DeferRefresh() instead would throw: each Add makes
            // the view re-check its Current position, which is forbidden while a refresh
            // is deferred. One Reset avoids both the exception and the per-item churn.
            var rows = new List<PhotoItemViewModel>(items.Count);
            foreach (var item in items)
            {
                var vm = new PhotoItemViewModel(item);
                vm.PropertyChanged += OnPhotoItemPropertyChanged;
                rows.Add(vm);
                _byRemotePath[item.RemotePath] = vm;
            }
            Photos.ReplaceAll(rows);

            Albums.Clear();
            Albums.Add("Все альбомы");
            // Show friendly names in the picker but keep raw folder name as the
            // value for filtering (both happen to be the same string here —
            // the filter compares p.Album which is the raw folder name).
            foreach (var album in items.Select(i => i.Album).Distinct().OrderBy(a => a))
            {
                var friendly = PhotoItemViewModel.MakeFriendlyAlbumNameStatic(album);
                Albums.Add(friendly == album ? album : friendly);
            }
            // Rebuild album map so the filter can resolve friendly → raw folder.
            _albumFriendlyToRaw.Clear();
            foreach (var vm in Photos)
            {
                var friendly = vm.FriendlyAlbumName;
                if (!_albumFriendlyToRaw.ContainsKey(friendly))
                    _albumFriendlyToRaw[friendly] = vm.Album;
            }
            SelectedAlbum = "Все альбомы";

            TotalCount = Photos.Count;
            StatusText = Photos.Count == 0
                ? "Медиафайлы не найдены. Убедитесь, что устройство разблокировано и вы разрешили доступ."
                : $"Найдено медиафайлов: {Photos.Count}";

            // Start the thumbnail loader. It waits for the view to report which rows are
            // visible; nudge it once here so the first screenful loads even if no scroll
            // or resize event follows (the common case on a fresh open).
            _ = LoadThumbnailsAsync(_thumbCts.Token);
            _viewportChanged.Set();

            // Sizes and dates come in afterwards, so the grid is usable immediately
            // instead of waiting on one AFC round-trip per file.
            _ = FillMetadataAsync(items, _metaCts.Token);
        }
        catch (Exception ex)
        {
            StatusText = $"Не удалось прочитать медиатеку: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Fills in sizes and dates after the grid is already on screen, then puts the list
    /// into true newest-first order.
    ///
    /// The initial order is by file name, which tracks capture order within one DCIM
    /// folder but interleaves wrongly when a device has several (100APPLE, 101APPLE, …).
    /// So the view is re-sorted by real date once — a single reshuffle at the end rather
    /// than items hopping around while results stream in.
    /// </summary>
    private async Task FillMetadataAsync(IReadOnlyList<PhotoItem> items, CancellationToken ct)
    {
        // Constructed on the UI thread, so Report() marshals back to it for us.
        var progress = new Progress<IReadOnlyList<PhotoMetadata>>(batch =>
        {
            foreach (var meta in batch)
            {
                if (_byRemotePath.TryGetValue(meta.Item.RemotePath, out var vm))
                    vm.ApplyMetadata(meta.SizeBytes, meta.ModifiedUtc);
            }
        });

        try
        {
            await _photos.FillMetadataAsync(_device!.Udid, items, progress, ct);

            ct.ThrowIfCancellationRequested();

            // Assigning CustomSort refreshes the view itself. Sorting the view rather
            // than the backing collection also leaves the thumbnail loader's snapshot
            // of Photos untouched.
            if (PhotosView is ListCollectionView list)
            {
                list.CustomSort = PhotoDateComparer.Instance;
            }
            else
            {
                PhotosView.Refresh();
            }
        }
        catch (OperationCanceledException)
        {
            // List was rebuilt or the page was left; nothing to report.
        }
        catch
        {
            // Sizes and dates are cosmetic — a mid-pass disconnect leaves the
            // placeholders in place rather than tearing down a working grid.
        }
    }

    /// <summary>Newest first, falling back to file name so the order stays stable.</summary>
    private sealed class PhotoDateComparer : System.Collections.IComparer
    {
        public static readonly PhotoDateComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (x is not PhotoItemViewModel a || y is not PhotoItemViewModel b) return 0;
            var da = a.Item.ModifiedUtc ?? DateTimeOffset.MinValue;
            var db = b.Item.ModifiedUtc ?? DateTimeOffset.MinValue;
            var byDate = db.CompareTo(da);
            return byDate != 0
                ? byDate
                : string.Compare(b.FileName, a.FileName, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task LoadPreviewAsync(PhotoItemViewModel? item)
    {
        PreviewImage = null;
        PreviewUnavailable = false;

        if (item is null || _device is null) return;

        // Video has no still frame to show. HEIC is attempted: WIC decodes it when the
        // OS HEIF codec is present, and simply fails into the placeholder when not.
        if (item.IsVideo || (IsHeicName(item.FileName) && IsHeicCodecMissing))
        {
            PreviewUnavailable = true;
            return;
        }

        try
        {
            var bytes = await _photos.ReadFileAsync(_device.Udid, item.Item.RemotePath, 0);
            if (bytes is null || bytes.Length == 0) { PreviewUnavailable = true; return; }

            var image = new BitmapImage();
            using (var ms = new MemoryStream(bytes))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 720; // downscale for the preview pane
                image.StreamSource = ms;
                image.EndInit();
            }
            image.Freeze();
            PreviewImage = image;
        }
        catch
        {
            PreviewUnavailable = true;

            // A HEIC that downloaded fine but won't decode means the OS HEIF codec is
            // missing. Raise the hint here too: clicking a photo is often the first
            // thing a user does, and without this they'd get a bare "no preview" with
            // no way to find out why.
            if (IsHeicName(item.FileName)) IsHeicCodecMissing = true;
        }
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        foreach (var p in PhotosView.Cast<PhotoItemViewModel>())
            p.IsSelected = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var p in Photos)
            p.IsSelected = false;
    }

    [RelayCommand]
    private async Task Refresh() => await LoadAsync();

    private bool CanExport() => SelectedCount > 0 && !IsTransferring;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportSelected()
    {
        if (_device is null) return;

        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку для сохранения",
        };
        if (dialog.ShowDialog() != true) return;

        var selected = Photos.Where(p => p.IsSelected).Select(p => p.Item).ToList();
        await RunTransferAsync(async (progress, ct) =>
        {
            var count = await _photos.ExportAsync(_device.Udid, selected, dialog.FolderName, progress, ct);
            StatusText = $"Скопировано на компьютер: {count} из {selected.Count}";
        });
    }

    [RelayCommand]
    private async Task Import()
    {
        if (_device is null) return;

        var dialog = new OpenFileDialog
        {
            Title = "Выберите фото или видео для переноса",
            Multiselect = true,
            Filter = "Медиафайлы|*.jpg;*.jpeg;*.png;*.heic;*.heif;*.mov;*.mp4;*.m4v|Все файлы|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        var files = dialog.FileNames.ToList();
        await RunTransferAsync(async (progress, ct) =>
        {
            var count = await _photos.ImportAsync(_device.Udid, files, progress, ct);
            StatusText = $"Перенесено на устройство: {count} из {files.Count}";
            await LoadAsync();
        });
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private async Task RunTransferAsync(Func<IProgress<PhotoTransferProgress>, CancellationToken, Task> work)
    {
        _cts = new CancellationTokenSource();
        IsTransferring = true;
        TransferProgress = 0;
        try
        {
            var progress = new Progress<PhotoTransferProgress>(p =>
            {
                if (p.Total > 0) TransferProgress = 100.0 * p.Completed / p.Total;
                StatusText = string.IsNullOrEmpty(p.CurrentFile)
                    ? StatusText
                    : $"{p.Completed}/{p.Total}: {p.CurrentFile}";
            });
            await work(progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Операция отменена.";
        }
        catch (Exception ex)
        {
            StatusText = $"Ошибка переноса: {ex.Message}";
        }
        finally
        {
            IsTransferring = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void SetListView() => IsListView = true;

    [RelayCommand]
    private void SetGridView() => IsListView = false;

    /// <summary>
    /// Called by the view whenever the visible rows change (scroll, resize, view
    /// switch). Indices refer to <see cref="PhotosView"/> order.
    ///
    /// Only records the range and wakes the loader — it must stay cheap because it
    /// fires on every scroll event.
    /// </summary>
    public void SetVisibleRange(int firstIndex, int lastIndex)
    {
        _visibleFirst = firstIndex;
        _visibleLast = lastIndex;
        _viewportChanged.Set();
    }

    /// <summary>
    /// Loads thumbnails for the rows that are actually on screen, and keeps loading as
    /// the user scrolls.
    ///
    /// Only visible items are fetched. The previous version walked the entire roll up
    /// front, which for HEIC would mean pulling every full-size file off the device —
    /// gigabytes of reads for photos the user may never scroll to. HEIC cannot be
    /// decoded from a partial read (its thumbnail is HEVC-coded and described by boxes
    /// that may sit anywhere in the file), so those are read whole and therefore only
    /// ever on demand; JPEG still needs just a 64 KB header.
    /// </summary>
    private async Task LoadThumbnailsAsync(CancellationToken ct)
    {
        // Small batches keep the loader responsive to scrolling: after each batch it
        // re-reads the viewport, so flinging the list doesn't first drain a long queue
        // of thumbnails the user has already scrolled past.
        const int JpegBatchSize = 12;
        const int HeicBatchSize = 4; // whole multi-MB files; keep peak memory modest
        const long ExifHeaderBytes = 65_536; // 64 KB — covers the EXIF block on iPhone JPEGs

        while (!ct.IsCancellationRequested)
        {
            // Wait until the viewport is known/changed, then service it.
            await _viewportChanged.WaitAsync(ct).ConfigureAwait(false);
            if (_device is null) continue;

            // Keep servicing the current viewport until nothing is left to load, then
            // go back to waiting. Re-reading the range each pass is what makes
            // scrolling feel immediate.
            while (!ct.IsCancellationRequested)
            {
                var batch = await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                    NextThumbnailBatch).Task.ConfigureAwait(false);

                if (batch.Count == 0) break;

                var isHeic = IsHeicName(batch[0].FileName);
                if (batch.Count > (isHeic ? HeicBatchSize : JpegBatchSize))
                    batch = batch.GetRange(0, isHeic ? HeicBatchSize : JpegBatchSize);

                var paths = batch.Select(p => p.Item.RemotePath).ToList();

                Dictionary<string, byte[]> rawMap;
                try
                {
                    // maxBytes 0 = whole file, required for HEIC.
                    rawMap = await _photos
                        .ReadFilesAsync(_device.Udid, paths, isHeic ? 0 : ExifHeaderBytes, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch { break; } // device disconnected; wait for the next viewport change

                var decoded = await Task.Run(() =>
                {
                    var result = new List<(PhotoItemViewModel item, BitmapImage thumb)>();
                    foreach (var item in batch)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (!rawMap.TryGetValue(item.Item.RemotePath, out var bytes) || bytes is null || bytes.Length == 0) continue;

                        // For JPEG the EXIF thumbnail is tiny and near-instant; the
                        // full decode is only a fallback. For HEIC the platform HEIF
                        // codec does the work, so there is nothing cheaper to try.
                        var thumb = isHeic
                            ? TryDecodeThumbnail(bytes, ThumbnailWidth)
                            : TryExtractExifThumbnailAsBitmapImage(bytes)
                              ?? TryDecodeThumbnail(bytes, ThumbnailWidth);
                        if (thumb is not null) result.Add((item, thumb));
                    }
                    return result;
                }, ct).ConfigureAwait(false);

                // HEIC decoding depends on an OS component that may be absent. If a
                // whole batch of readable HEIC files decoded to nothing, the codec is
                // the only plausible cause — surface the hint instead of leaving the
                // user staring at permanently blank tiles.
                if (isHeic && rawMap.Count > 0)
                    NoteHeicDecodeOutcome(succeeded: decoded.Count > 0);

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                    () =>
                    {
                        foreach (var (item, thumb) in decoded)
                        {
                            item.Thumbnail = thumb;
                            TouchCache(item);
                        }
                        // Items whose bytes arrived but produced no image are recorded
                        // as attempted, so the loader doesn't retry them forever.
                        foreach (var item in batch)
                            if (rawMap.ContainsKey(item.Item.RemotePath))
                                item.ThumbnailAttempted = true;

                        TrimCache();
                    },
                    System.Windows.Threading.DispatcherPriority.Background).Task.ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Picks the next items to load: those in (or just around) the viewport that have
    /// no thumbnail yet. Runs on the UI thread because it reads the collection view.
    ///
    /// Returns items of a single kind (all HEIC or all JPEG) so the caller can use one
    /// read size per AFC session.
    /// </summary>
    private List<PhotoItemViewModel> NextThumbnailBatch()
    {
        var result = new List<PhotoItemViewModel>();
        if (_visibleLast < _visibleFirst) return result;

        // A small look-ahead margin means thumbnails are usually ready by the time a
        // row scrolls into view, without fetching far-away files.
        const int Margin = 8;

        var first = Math.Max(0, _visibleFirst - Margin);
        var last = _visibleLast + Margin;

        // Walk only the window, stopping at its end: materialising the whole view here
        // would be O(total photos) on every batch.
        bool? wantHeic = null;
        var index = -1;
        foreach (PhotoItemViewModel item in PhotosView)
        {
            index++;
            if (index < first) continue;
            if (index > last) break;

            if (item.IsVideo || item.Thumbnail is not null || item.ThumbnailAttempted) continue;

            var heic = IsHeicName(item.FileName);
            // Don't spend reads on HEIC once we know the OS can't decode it.
            if (heic && IsHeicCodecMissing) continue;

            wantHeic ??= heic;
            if (heic != wantHeic) continue;

            result.Add(item);
        }

        return result;
    }

    private static bool IsHeicName(string fileName)
        => Path.GetExtension(fileName).ToLowerInvariant() is ".heic" or ".heif";

    /// <summary>
    /// Tracks whether HEIC decoding works, and flips the hint on only after several
    /// consecutive all-failed batches. One failure can just be a corrupt file; a
    /// missing codec fails everything.
    /// </summary>
    private void NoteHeicDecodeOutcome(bool succeeded)
    {
        if (succeeded)
        {
            _heicFailedBatches = 0;
            return;
        }

        if (++_heicFailedBatches >= 2)
            IsHeicCodecMissing = true;
    }

    /// <summary>
    /// Marks an item as most-recently-used in the thumbnail cache.
    /// Must run on the UI thread.
    /// </summary>
    private void TouchCache(PhotoItemViewModel item)
    {
        if (_cacheNodes.TryGetValue(item, out var node))
            _cacheOrder.Remove(node);

        _cacheNodes[item] = _cacheOrder.AddFirst(item);
    }

    /// <summary>
    /// Drops the least-recently-seen thumbnails once the cache exceeds
    /// <see cref="MaxCachedThumbnails"/>, so browsing a large roll cannot grow memory
    /// without bound. Items still on screen are kept — evicting those would make them
    /// reload immediately, and flicker. Must run on the UI thread.
    /// </summary>
    private void TrimCache()
    {
        if (_cacheOrder.Count <= MaxCachedThumbnails) return;

        // Snapshot the visible items once. Resolving "is this on screen?" per candidate
        // would rescan the collection view for every eviction.
        var visible = VisibleItems();

        var node = _cacheOrder.Last;
        while (_cacheOrder.Count > MaxCachedThumbnails && node is not null)
        {
            var prev = node.Previous;
            var item = node.Value;

            if (!visible.Contains(item))
            {
                item.Thumbnail = null;
                // Allow a reload if the user scrolls back to it.
                item.ThumbnailAttempted = false;
                _cacheOrder.Remove(node);
                _cacheNodes.Remove(item);
            }

            node = prev;
        }
    }

    /// <summary>Items currently inside the reported viewport. UI thread only.</summary>
    private HashSet<PhotoItemViewModel> VisibleItems()
    {
        var set = new HashSet<PhotoItemViewModel>();
        if (_visibleLast < _visibleFirst) return set;

        var i = 0;
        foreach (PhotoItemViewModel item in PhotosView)
        {
            if (i > _visibleLast) break;
            if (i >= _visibleFirst) set.Add(item);
            i++;
        }
        return set;
    }

    /// <summary>
    /// Extracts the EXIF embedded thumbnail from a partial JPEG byte header and
    /// returns it as a frozen <see cref="BitmapImage"/> ready for data binding.
    /// Returns null when no thumbnail is present in the header.
    /// </summary>
    private static BitmapImage? TryExtractExifThumbnailAsBitmapImage(byte[] header)
    {
        try
        {
            using var ms = new MemoryStream(header);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                ms,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

            // Prefer the dedicated EXIF thumbnail; fall back to the first frame's
            // thumbnail metadata when present.
            BitmapSource? thumb = decoder.Thumbnail;
            if (thumb is null && decoder.Frames.Count > 0)
                thumb = decoder.Frames[0].Thumbnail;

            if (thumb is null) return null;

            // Re-encode to BitmapImage so the binding type is consistent.
            // Use JPEG (not PNG) for speed — thumbnails are already lossy.
            var img = new BitmapImage();
            var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(thumb));
            using var outMs = new MemoryStream();
            encoder.Save(outMs);
            outMs.Position = 0;
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 96;
            img.StreamSource = outMs;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    /// <summary>
    /// Decodes an image at a small target width, using whatever WIC codec the OS has
    /// for the format. This is also the HEIC path: decoding happens in the platform
    /// HEIF codec, so it works when Windows can open the file and returns null when it
    /// can't. <c>DecodePixelWidth</c> lets WIC scale during decode rather than after,
    /// which keeps a full-size photo from being materialised at full resolution.
    /// </summary>
    private static BitmapImage? TryDecodeThumbnail(byte[] bytes, int decodeWidth)
    {
        try
        {
            var img = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = decodeWidth;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    /// <summary>
    /// Opens the Microsoft Store page for the HEIF codec. Uses the ms-windows-store URI
    /// so the Store app opens directly, falling back to the web listing on machines
    /// where the Store isn't available.
    /// </summary>
    [RelayCommand]
    private void OpenHeicHelp()
    {
        const string StoreUri = "ms-windows-store://pdp/?ProductId=9pmmsr1cgpwg";
        const string WebUri = "https://apps.microsoft.com/detail/9pmmsr1cgpwg";

        try
        {
            Process.Start(new ProcessStartInfo(StoreUri) { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo(WebUri) { UseShellExecute = true }); }
            catch { /* nothing further we can do from here */ }
        }
    }

    [RelayCommand]
    private void Back() => _navigator?.GoTo(Page.Devices);
}
