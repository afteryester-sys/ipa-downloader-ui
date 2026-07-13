using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    public string SizeText => FormatSize(Item.SizeBytes);
    public string DateText => Item.ModifiedUtc?.LocalDateTime.ToString("dd.MM.yyyy HH:mm") ?? "";

    public PhotoItemViewModel(PhotoItem item) => Item = item;

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

    public ObservableCollection<PhotoItemViewModel> Photos { get; } = new();
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

        IsBusy = true;
        StatusText = "Чтение медиатеки…";
        Photos.Clear();
        try
        {
            var items = await _photos.ListCameraRollAsync(_device.Udid);
            foreach (var item in items)
            {
                var vm = new PhotoItemViewModel(item);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PhotoItemViewModel.IsSelected))
                        SelectedCount = Photos.Count(p => p.IsSelected);
                };
                Photos.Add(vm);
            }

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

            // Start thumbnail loading in background.
            _ = LoadThumbnailsAsync(_thumbCts.Token);
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

    private async Task LoadPreviewAsync(PhotoItemViewModel? item)
    {
        PreviewImage = null;
        PreviewUnavailable = false;

        if (item is null || _device is null) return;

        // Videos and HEIC can't be decoded by WPF's built-in codecs; show a placeholder.
        var ext = Path.GetExtension(item.FileName).ToLowerInvariant();
        if (item.IsVideo || ext is ".heic" or ".heif")
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
    /// Loads small thumbnails for photos (not videos/HEIC) in the background.
    ///
    /// Strategy: batch files into groups of <c>SessionBatchSize</c>. Each batch opens
    /// ONE AFC session and reads all files in it, avoiding the expensive per-file
    /// USB/lockdown handshake. Thumbnail extraction runs in parallel on the thread
    /// pool. UI updates are dispatched with low priority so the main thread stays
    /// responsive (the dispatcher never blocks between batches).
    /// </summary>
    private async Task LoadThumbnailsAsync(CancellationToken ct)
    {
        const int SessionBatchSize = 20;     // files per single AFC session
        const long ExifHeaderBytes = 65_536; // 64 KB — covers the EXIF block on iPhone JPEGs

        var jpegItems = Photos
            .Where(p => !p.IsVideo
                && Path.GetExtension(p.FileName).ToLowerInvariant() is not ".heic" and not ".heif")
            .ToList();

        if (_device is null) return;

        for (var i = 0; i < jpegItems.Count; i += SessionBatchSize)
        {
            ct.ThrowIfCancellationRequested();

            // Yield to the UI thread between every batch so it can process input
            // and paint incremental thumbnail updates without freezing.
            await Task.Delay(1, ct).ConfigureAwait(false);

            var batch = jpegItems.Skip(i).Take(SessionBatchSize).ToList();
            var paths = batch
                .Where(p => p.Thumbnail is null)
                .Select(p => p.Item.RemotePath)
                .ToList();
            if (paths.Count == 0) continue;

            // Read all EXIF headers in ONE AFC session on a background thread.
            Dictionary<string, byte[]> rawMap;
            try
            {
                rawMap = await _photos.ReadFilesAsync(_device.Udid, paths, ExifHeaderBytes, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch { continue; } // device disconnected; skip batch

            // Decode thumbnails in parallel (CPU-bound, no AFC involved).
            var decoded = await Task.Run(() =>
            {
                var result = new List<(PhotoItemViewModel item, BitmapImage thumb)>();
                foreach (var item in batch)
                {
                    if (ct.IsCancellationRequested) break;
                    if (!rawMap.TryGetValue(item.Item.RemotePath, out var bytes) || bytes is null || bytes.Length == 0) continue;

                    // Prefer the EXIF embedded thumbnail (fast, tiny). Fall back to
                    // down-scaled full-decode only when EXIF thumbnail is absent.
                    BitmapImage? thumb = TryExtractExifThumbnailAsBitmapImage(bytes)
                                      ?? TryDecodeFullJpeg(bytes, 96);
                    if (thumb is not null) result.Add((item, thumb));
                }
                return result;
            }, ct).ConfigureAwait(false);

            // Dispatch thumbnail assignments at Background priority so painting
            // never blocks input events on the UI thread.
            if (decoded.Count > 0)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                    () =>
                    {
                        foreach (var (item, thumb) in decoded)
                            item.Thumbnail = thumb;
                    },
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }
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

    /// <summary>Decodes a full JPEG byte array at a small target width.</summary>
    private static BitmapImage? TryDecodeFullJpeg(byte[] bytes, int decodeWidth)
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

    [RelayCommand]
    private void Back() => _navigator?.GoTo(Page.Devices);
}
