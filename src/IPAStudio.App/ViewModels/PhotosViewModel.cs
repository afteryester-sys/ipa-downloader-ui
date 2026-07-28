using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.App.Infrastructure;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Localization;
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
    /// Real album title from the Photos library, when it could be read. Null means the
    /// database was unavailable (Apple restricts it on current iOS), and the UI falls
    /// back to the folder-derived name.
    /// </summary>
    private string? _realAlbumName;

    /// <summary>
    /// The album label shown in the UI and used for filtering.
    ///
    /// When the real title is unknown the item is filed under one "no album" group rather
    /// than its DCIM folder. Those folder numbers (100APPLE, 101APPLE, …) are an internal
    /// storage detail, not albums, so surfacing them produced the meaningless
    /// "Camera (137)" … "Camera (900)" list instead of the user's real albums.
    /// </summary>
    public string DisplayAlbumName => _realAlbumName ?? Loc.Get("L.Photos.NoAlbum");

    /// <summary>Applies a real album title. Call on the UI thread; raises notifications.</summary>
    public void SetRealAlbumName(string? title)
    {
        if (_realAlbumName == title) return;
        _realAlbumName = title;
        OnPropertyChanged(nameof(DisplayAlbumName));
    }

    public bool IsVideo => Item.IsVideo;

    /// <summary>
    /// File size, or an em dash until the device has been asked. Listing skips the
    /// per-file stat so the grid can appear at once, so this shows a placeholder for a
    /// moment instead of claiming a misleading "0 B".
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
        string[] units =
        {
            Loc.Get("L.Unit.B"), Loc.Get("L.Unit.KB"),
            Loc.Get("L.Unit.MB"), Loc.Get("L.Unit.GB"),
        };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}

/// <summary>
/// One album tile: a cover picture, a title and how many items it holds.
///
/// An album is defined by a predicate rather than by a stored list, because most of these
/// are derived from the media itself (videos, screenshots, RAW, Live Photos) exactly as
/// 3uTools presents them. That keeps albums working on devices where the Photos library
/// database cannot be read, which is the common case — without it the album screen would
/// show a single "no album" tile and look broken.
/// </summary>
public sealed partial class PhotoAlbumViewModel : ObservableObject
{
    private readonly Func<PhotoItemViewModel, bool> _match;

    /// <summary>Title shown under the cover.</summary>
    public string Name { get; }

    /// <summary>True for the all-items album, which is always shown even when empty.</summary>
    public bool IsEverything { get; }

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private BitmapImage? _cover;

    /// <summary>Item the cover is taken from; used to fetch that one thumbnail.</summary>
    public PhotoItemViewModel? CoverItem { get; set; }

    /// <summary>True when the cover is a video, so the tile can mark it.</summary>
    public bool CoverIsVideo => CoverItem?.IsVideo == true;

    public PhotoAlbumViewModel(string name, Func<PhotoItemViewModel, bool> match, bool isEverything = false)
    {
        Name = name;
        _match = match;
        IsEverything = isEverything;
    }

    public bool Matches(PhotoItemViewModel item) => _match(item);
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

    /// <summary>
    /// Item lookup by AFC path, so a metadata batch arriving from the background can be
    /// matched to its row without scanning the whole collection per result.
    /// </summary>
    private readonly Dictionary<string, PhotoItemViewModel> _byRemotePath = new();

    /// <summary>Cancels the background size/date pass when the list is rebuilt.</summary>
    private CancellationTokenSource? _metaCts;

    /// <summary>Thumbnail decode width, in pixels. Tiles render at 130 wide.</summary>
    private const int ThumbnailWidth = 160;

    /// <summary>Album cover decode width, in pixels. Album tiles render at 168 wide.</summary>
    private const int AlbumCoverWidth = 200;

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

    /// <summary>
    /// Album tiles shown before the photos themselves, the way a phone gallery opens.
    ///
    /// This replaced a plain album drop-down. Real album titles are usually unreadable on
    /// current iOS, so that list was often a single "no album" entry — technically correct
    /// and useless. Deriving albums from the media itself (videos, screenshots, RAW, Live
    /// Photos) gives the same grouping a phone shows, on every device.
    /// </summary>
    public ObservableCollection<PhotoAlbumViewModel> MediaAlbums { get; } = new();

    /// <summary>Media type filter options.</summary>
    public ObservableCollection<string> MediaTypes { get; } = new();

    [ObservableProperty]
    private string _deviceName = "";

    /// <summary>Album whose contents are listed. Null before the first load.</summary>
    [ObservableProperty]
    private PhotoAlbumViewModel? _currentAlbum;

    /// <summary>True while the album tiles are shown instead of the photos.</summary>
    [ObservableProperty]
    private bool _isAlbumMode = true;

    /// <summary>
    /// Free-text filter over the file name and album label of the open album.
    ///
    /// Names are all AFC exposes, so this cannot search by content; it is meant for finding
    /// a known shot (a file name seen elsewhere, a date-like prefix) inside a large roll.
    /// </summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedMediaType = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    private int _selectedCount;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
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

    /// <summary>
    /// True when the Photos library database could not be read, so real album titles are
    /// unavailable and everything lands in one group. Surfaced as a short hint, because
    /// otherwise an album list with a single entry just looks broken.
    /// </summary>
    [ObservableProperty]
    private bool _albumNamesUnavailable;

    public bool IsGridView => !IsListView;

    /// <summary>True while the photos are shown, i.e. an album is open.</summary>
    public bool IsPhotoMode => !IsAlbumMode;

    /// <summary>
    /// Which of the two photo panes is on screen. Exposed as single booleans because the
    /// panes depend on both the mode and the layout, and a binding cannot combine two.
    /// </summary>
    public bool ShowPhotoList => !IsAlbumMode && IsListView;
    public bool ShowPhotoGrid => !IsAlbumMode && !IsListView;

    partial void OnIsListViewChanged(bool value)
    {
        OnPropertyChanged(nameof(IsGridView));
        OnPropertyChanged(nameof(ShowPhotoList));
        OnPropertyChanged(nameof(ShowPhotoGrid));
    }

    partial void OnIsAlbumModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPhotoMode));
        OnPropertyChanged(nameof(ShowPhotoList));
        OnPropertyChanged(nameof(ShowPhotoGrid));
        // Leaving the tiles reveals a different set of rows, so the loader must re-check.
        if (!value) _viewportChanged.Set();
    }

    private CancellationTokenSource? _thumbCts;

    public PhotosViewModel(PhotoService photos)
    {
        _photos = photos;

        // Built here rather than in a field initializer because the labels come from the
        // active language dictionary, and the filter compares the selection against the
        // same strings.
        MediaTypes.Add(Loc.Get("L.Photos.Media.All"));
        MediaTypes.Add(Loc.Get("L.Photos.Media.Photos"));
        MediaTypes.Add(Loc.Get("L.Photos.Media.Videos"));

        PhotosView = CollectionViewSource.GetDefaultView(Photos);
        PhotosView.Filter = Filter;

        // After PhotosView exists: the setter notifies, and the handler refreshes the view.
        SelectedMediaType = MediaTypes[0];
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

    partial void OnCurrentAlbumChanged(PhotoAlbumViewModel? value)
    {
        PhotosView.Refresh();
        // The visible rows are now different ones, so the loader has to look again.
        _viewportChanged.Set();
    }
    partial void OnSelectedMediaTypeChanged(string value) => PhotosView.Refresh();

    partial void OnSelectedPhotoChanged(PhotoItemViewModel? value) => _ = LoadPreviewAsync(value);

    private bool Filter(object obj)
    {
        if (obj is not PhotoItemViewModel p) return false;

        // The all-items album matches everything, so it needs no test.
        if (CurrentAlbum is { IsEverything: false } album && !album.Matches(p)) return false;
        if (SelectedMediaType == Loc.Get("L.Photos.Media.Photos") && p.IsVideo) return false;
        if (SelectedMediaType == Loc.Get("L.Photos.Media.Videos") && !p.IsVideo) return false;

        if (SearchText.Length > 0
            && !p.FileName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            && !p.DisplayAlbumName.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    partial void OnSearchTextChanged(string value)
    {
        PhotosView.Refresh();
        // Different rows are visible now, so thumbnails for them have to be fetched.
        _viewportChanged.Set();
    }

    /// <summary>
    /// Rebuilds the album tiles from the media currently loaded, keeping the open album
    /// where possible. Called after loading and again if real album names arrive later.
    /// </summary>
    /// <param name="keepSelection">
    /// True when relabelling an already-loaded list, so the album the user opened survives.
    /// False for a freshly loaded roll, where carrying a selection over from the previous
    /// device would silently filter the new list if a name happened to coincide.
    /// </param>
    private void BuildMediaAlbums(bool keepSelection)
    {
        var previousName = keepSelection ? CurrentAlbum?.Name : null;

        // Computed once and captured, instead of re-derived inside the predicate: pairing
        // is a property of the whole roll, not of one file.
        var livePhotoKeys = FindLivePhotoKeys();

        var built = new List<PhotoAlbumViewModel>
        {
            new(Loc.Get("L.Photos.Albums.All"), static _ => true, isEverything: true),
            new(Loc.Get("L.Photos.Albums.Videos"), static p => p.IsVideo),
            new(Loc.Get("L.Photos.Albums.Screenshots"), static p => IsScreenshotName(p.FileName)),
            new(Loc.Get("L.Photos.Albums.Raw"), static p => IsRawName(p.FileName)),
            new(Loc.Get("L.Photos.Albums.Live"), p => livePhotoKeys.Contains(LivePhotoKey(p))),
        };

        // Real Photos-library albums when iOS allowed reading them. The catch-all "no
        // album" group is left out: everything is already reachable through the all-items
        // tile, so it would only duplicate it.
        var noAlbum = Loc.Get("L.Photos.NoAlbum");
        var hidden = Loc.Get("L.Photos.Hidden");
        foreach (var name in Photos.Select(p => p.DisplayAlbumName)
                                   .Distinct(StringComparer.Ordinal)
                                   .Where(n => !string.Equals(n, noAlbum, StringComparison.Ordinal))
                                   .OrderBy(n => n == hidden ? 1 : 0)
                                   .ThenBy(n => n, StringComparer.CurrentCulture))
        {
            var albumName = name; // captured per iteration, not shared by every predicate
            built.Add(new PhotoAlbumViewModel(
                albumName,
                p => string.Equals(p.DisplayAlbumName, albumName, StringComparison.Ordinal)));
        }

        // One pass per album fills both the count and the cover, so the tiles can show how
        // much is inside before anything is opened.
        foreach (var album in built)
        {
            var count = 0;
            foreach (var photo in Photos)
            {
                if (!album.Matches(photo)) continue;
                count++;
                album.CoverItem ??= photo;
            }
            album.Count = count;
        }

        MediaAlbums.Clear();
        // Empty derived albums are dropped: a phone with no RAW shots should not be shown
        // an empty RAW album. The all-items tile stays even at zero, so the screen is never
        // completely blank.
        foreach (var album in built.Where(a => a.IsEverything || a.Count > 0)) MediaAlbums.Add(album);

        var restored = previousName is null
            ? null
            : MediaAlbums.FirstOrDefault(a => string.Equals(a.Name, previousName, StringComparison.Ordinal));

        var next = restored ?? MediaAlbums.FirstOrDefault();
        if (ReferenceEquals(CurrentAlbum, next))
        {
            // Assigning the same instance raises no notification, so the filter would keep
            // matching on stale labels after a relabel. Refresh explicitly.
            PhotosView.Refresh();
        }
        else
        {
            CurrentAlbum = next; // OnCurrentAlbumChanged refreshes the view
        }

        if (!keepSelection) IsAlbumMode = true;

        _ = LoadAlbumCoversAsync(_thumbCts?.Token ?? CancellationToken.None);
    }

    /// <summary>Screenshots are the PNGs in the Camera Roll; the camera only writes HEIC/JPEG.</summary>
    private static bool IsScreenshotName(string fileName)
        => fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    private static bool IsRawName(string fileName)
        => fileName.EndsWith(".dng", StringComparison.OrdinalIgnoreCase);

    /// <summary>Folder + name without extension, which is what pairs a Live Photo's two files.</summary>
    private static string LivePhotoKey(PhotoItemViewModel item)
        => $"{item.Album}/{Path.GetFileNameWithoutExtension(item.FileName)}";

    /// <summary>
    /// Finds the Live Photos: a still and a short movie saved side by side under one name
    /// (IMG_0001.HEIC + IMG_0001.MOV). Nothing in the file list marks them otherwise, so
    /// the pairing is the only signal available over AFC.
    /// </summary>
    private HashSet<string> FindLivePhotoKeys()
    {
        var stills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var movies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var photo in Photos)
        {
            var key = LivePhotoKey(photo);
            if (photo.IsVideo) movies.Add(key);
            else stills.Add(key);
        }

        movies.IntersectWith(stills);
        return movies;
    }

    /// <summary>
    /// Fetches the cover thumbnails for the album tiles.
    ///
    /// Only the device-rendered thumbnails are used — a few KB each. Reading the originals
    /// would mean pulling one multi-megabyte file per album before the screen can be drawn.
    /// </summary>
    private async Task LoadAlbumCoversAsync(CancellationToken ct)
    {
        if (_device is null) return;

        // A cover already decoded for the photo grid is reused as-is.
        foreach (var album in MediaAlbums)
        {
            if (album.Cover is null && album.CoverItem?.Thumbnail is not null)
                album.Cover = album.CoverItem.Thumbnail;
        }

        var pending = MediaAlbums.Where(a => a.Cover is null && a.CoverItem is not null).ToList();
        if (pending.Count == 0) return;

        Dictionary<string, byte[]> thumbMap;
        try
        {
            thumbMap = await _photos
                .ReadIosThumbnailsAsync(_device.Udid, pending.Select(a => a.CoverItem!.Item).ToList(), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch { return; } // device went away; tiles keep their placeholder glyph

        var decoded = await Task.Run(() =>
        {
            var result = new List<(PhotoAlbumViewModel album, BitmapImage cover)>();
            foreach (var album in pending)
            {
                if (ct.IsCancellationRequested) break;
                if (!thumbMap.TryGetValue(album.CoverItem!.Item.RemotePath, out var bytes)) continue;

                var cover = TryDecodeThumbnail(bytes, AlbumCoverWidth);
                if (cover is not null) result.Add((album, cover));
            }
            return result;
        }, ct).ConfigureAwait(false);

        if (ct.IsCancellationRequested) return;

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (var (album, cover) in decoded) album.Cover = cover;
        }).Task.ConfigureAwait(false);
    }

    [RelayCommand]
    private void OpenAlbum(PhotoAlbumViewModel? album)
    {
        if (album is null) return;
        CurrentAlbum = album;
        IsAlbumMode = false;
    }

    [RelayCommand]
    private void ShowAlbums()
    {
        IsAlbumMode = true;
        // Covers may have been missing when the tiles were built (nothing loaded yet).
        _ = LoadAlbumCoversAsync(_thumbCts?.Token ?? CancellationToken.None);
    }

    /// <summary>
    /// Attempts to replace the folder-derived album names with the real ones from the
    /// device Photos library. Silent no-op when the database can't be read, which is the
    /// normal outcome on current iOS — the folder-based names simply stay.
    /// </summary>
    private async Task ApplyRealAlbumNamesAsync(CancellationToken ct)
    {
        if (_device is null) return;

        Dictionary<string, string>? map;
        try
        {
            map = await _photos.TryReadAlbumNamesAsync(_device.Udid, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch { return; }

        if (ct.IsCancellationRequested) return;

        if (map is null || map.Count == 0)
        {
            // iOS does not always allow reading Photos.sqlite over AFC. Say so instead of
            // silently showing one unnamed group.
            AppLog.Warn("Album titles unavailable: Photos library database could not be read");
            await System.Windows.Application.Current.Dispatcher
                .InvokeAsync(() => AlbumNamesUnavailable = true).Task.ConfigureAwait(false);
            return;
        }

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested) return;

            var matched = 0;
            foreach (var row in Photos)
            {
                if (map.TryGetValue(row.FileName, out var title))
                {
                    row.SetRealAlbumName(title);
                    matched++;
                }
            }

            // The database was readable but describes other assets: nothing to relabel.
            if (matched == 0)
            {
                AlbumNamesUnavailable = true;
                return;
            }

            AlbumNamesUnavailable = false;
            BuildMediaAlbums(keepSelection: true);
        }).Task.ConfigureAwait(false);
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
        StatusText = Loc.Get("L.Photos.Reading");

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

            // Also returns to the album tiles for the newly loaded list.
            BuildMediaAlbums(keepSelection: false);

            TotalCount = Photos.Count;
            StatusText = Photos.Count == 0
                ? Loc.Get("L.Photos.Empty")
                : Loc.Format("L.Photos.Found", Photos.Count);

            // Start the thumbnail loader. It waits for the view to report which rows are
            // visible; nudge it once here so the first screenful loads even if no scroll
            // or resize event follows (the common case on a fresh open).
            _ = LoadThumbnailsAsync(_thumbCts.Token);
            _viewportChanged.Set();

            // Sizes and dates come in afterwards, so the grid is usable immediately
            // instead of waiting on one AFC round-trip per file.
            _ = FillMetadataAsync(items, _metaCts.Token);

            // Real album names need the Photos library database copied off the device,
            // which is slow and often blocked. Run it in the background: the folder-based
            // names are already showing, and the picker is relabelled only if it succeeds.
            _ = ApplyRealAlbumNamesAsync(_metaCts.Token);
        }
        catch (Exception ex)
        {
            StatusText = Loc.Format("L.Photos.ReadFailed", ex.Message);
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
            Title = Loc.Get("L.Photos.PickExportFolder"),
        };
        if (dialog.ShowDialog() != true) return;

        var selected = Photos.Where(p => p.IsSelected).Select(p => p.Item).ToList();
        await RunTransferAsync(async (progress, ct) =>
        {
            var count = await _photos.ExportAsync(_device.Udid, selected, dialog.FolderName, progress, ct);
            StatusText = Loc.Format("L.Photos.Exported", count, selected.Count);
        });
    }

    private bool CanDelete() => SelectedCount > 0 && !IsTransferring;

    /// <summary>
    /// Removes the selected items from the device after an explicit confirmation.
    ///
    /// The count is spelled out in the prompt and the dialog defaults to "No", because AFC
    /// deletion skips "Recently Deleted": there is nothing to restore from afterwards.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteSelected()
    {
        if (_device is null) return;

        var selected = Photos.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0) return;

        var confirm = MessageBox.Show(
            Loc.Format("L.Photos.ConfirmDeleteBody", selected.Count),
            Loc.Get("L.Photos.ConfirmDeleteTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        var items = selected.Select(p => p.Item).ToList();
        await RunTransferAsync(async (progress, ct) =>
        {
            var count = await _photos.DeleteAsync(_device.Udid, items, progress, ct);

            // Dropping the rows locally instead of reloading the whole roll: a reload would
            // discard every thumbnail already decoded, and the device list is the same minus
            // exactly what went away. Only the confirmed deletions are removed.
            var goneNames = new HashSet<string>(
                items.Take(count).Select(i => i.RemotePath), StringComparer.OrdinalIgnoreCase);

            foreach (var row in selected.Where(p => goneNames.Contains(p.Item.RemotePath)))
            {
                row.PropertyChanged -= OnPhotoItemPropertyChanged;
                if (row.IsSelected) SelectedCount--;
                Photos.Remove(row);
            }

            // Counts and covers on the tiles refer to rows that no longer exist.
            BuildMediaAlbums(keepSelection: true);

            StatusText = Loc.Format("L.Photos.Deleted", count, items.Count);
        });
    }

    [RelayCommand]
    private async Task Import()
    {
        if (_device is null) return;

        var dialog = new OpenFileDialog
        {
            Title = Loc.Get("L.Photos.PickImportFiles"),
            Multiselect = true,
            Filter = Loc.Get("L.Photos.MediaFilter"),
        };
        if (dialog.ShowDialog() != true) return;

        var files = dialog.FileNames.ToList();
        await RunTransferAsync(async (progress, ct) =>
        {
            ImportNeedsRestart = false;

            var result = await _photos.ImportAsync(_device.Udid, files, progress, ct);

            // Say what actually happened rather than just a count: copying files into DCIM and
            // having them show up in Photos are different outcomes, and reporting the first as
            // the second is what made a failed import look successful.
            StatusText = result.Copied == 0
                ? Loc.Get("L.Photos.ImportNothingCopied")
                : result.AppearedInLibrary
                    ? Loc.Format("L.Photos.Imported", result.Copied, result.Total)
                    : Loc.Format("L.Photos.ImportedNotInLibrary", result.Copied, result.Total);

            // The banner offers the reboot, so it appears only when the files are on the
            // device but the library has not picked them up.
            ImportNeedsRestart = result.Copied > 0 && !result.AppearedInLibrary;

            await LoadAsync();
        });
    }

    /// <summary>
    /// True while imported files are on the device but not in the Camera Roll, which is the
    /// only situation where offering a reboot makes sense.
    /// </summary>
    [ObservableProperty]
    private bool _importNeedsRestart;

    [RelayCommand]
    private void DismissImportHint() => ImportNeedsRestart = false;

    /// <summary>
    /// Reboots the device so Photos re-scans DCIM on the way up. Confirmed first: this
    /// interrupts whatever the user is doing on the phone.
    /// </summary>
    [RelayCommand]
    private async Task RestartDevice()
    {
        if (_device is null) return;

        var confirm = MessageBox.Show(
            Loc.Get("L.Photos.RestartConfirm"),
            Loc.Get("L.Photos.RestartDevice"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        var restarted = await _photos.RestartDeviceAsync(_device.Udid);
        StatusText = Loc.Get(restarted ? "L.Photos.Restarting" : "L.Photos.RestartFailed");

        // The device is going away; keeping the banner would invite a second reboot.
        if (restarted) ImportNeedsRestart = false;
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
            StatusText = Loc.Get("L.Photos.Cancelled");
        }
        catch (Exception ex)
        {
            StatusText = Loc.Format("L.Photos.TransferFailed", ex.Message);
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

                var isHeic = batch.Any(p => !p.IsVideo && IsHeicName(p.FileName));
                if (batch.Count > (isHeic ? HeicBatchSize : JpegBatchSize))
                    batch = batch.GetRange(0, isHeic ? HeicBatchSize : JpegBatchSize);

                // First ask the device for the thumbnails iOS already rendered. These are a
                // few KB each, against multi-MB HEIC and ~25 MB DNG source files, which is
                // what made a screenful of tiles take many seconds to appear. They also
                // cover formats Windows cannot decode at all (DNG, or HEIC with no codec),
                // so those tiles stop coming up blank.
                Dictionary<string, byte[]> thumbMap;
                try
                {
                    thumbMap = await _photos
                        .ReadIosThumbnailsAsync(_device.Udid, batch.Select(p => p.Item).ToList(), ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch { thumbMap = new Dictionary<string, byte[]>(); }

                // Only items with no device thumbnail need the source file read, and videos are
                // excluded: WPF cannot decode MOV/MP4, so pulling those bytes could never
                // produce a picture. It only cost time and left the tile blank anyway; the
                // tile now shows a film icon instead.
                var needSource = batch
                    .Where(p => !thumbMap.ContainsKey(p.Item.RemotePath) && !p.IsVideo)
                    .ToList();

                Dictionary<string, byte[]> rawMap;
                try
                {
                    // maxBytes 0 = whole file, required for HEIC.
                    rawMap = needSource.Count == 0
                        ? new Dictionary<string, byte[]>()
                        : await _photos
                            .ReadFilesAsync(
                                _device.Udid,
                                needSource.Select(p => p.Item.RemotePath).ToList(),
                                isHeic ? 0 : ExifHeaderBytes,
                                ct)
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

                        // Device thumbnail: a small JPEG, so one cheap decode and done.
                        if (thumbMap.TryGetValue(item.Item.RemotePath, out var thumbBytes)
                            && thumbBytes is { Length: > 0 })
                        {
                            var fromDevice = TryDecodeThumbnail(thumbBytes, ThumbnailWidth);
                            if (fromDevice is not null)
                            {
                                result.Add((item, fromDevice));
                                continue;
                            }
                        }

                        // A video with no device-rendered thumbnail has nothing left to try.
                        if (item.IsVideo) continue;

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
                // Judge the codec only on files we actually had to decode ourselves.
                // Device thumbnails are plain JPEG and succeed with or without the HEIF
                // codec, so counting them here would mask a genuinely missing codec.
                if (isHeic && rawMap.Count > 0)
                {
                    var decodedFromSource = decoded.Count(d => rawMap.ContainsKey(d.item.Item.RemotePath)
                                                               && !thumbMap.ContainsKey(d.item.Item.RemotePath));
                    NoteHeicDecodeOutcome(succeeded: decodedFromSource > 0);
                }

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
                        // Videos count as attempted either way: there is no second thing
                        // to try for them, and leaving them unmarked would make the loader
                        // hand back the same batch forever and never idle.
                        foreach (var item in batch)
                            if (item.IsVideo
                                || rawMap.ContainsKey(item.Item.RemotePath)
                                || thumbMap.ContainsKey(item.Item.RemotePath))
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

            if (item.Thumbnail is not null || item.ThumbnailAttempted) continue;

            // Videos join any batch instead of being skipped. They used to be excluded
            // altogether, which is why every video tile stayed empty: the device already
            // holds a rendered JPEG thumbnail for them, and it was never asked for. They
            // sit outside the HEIC/JPEG grouping below because that only decides how much
            // of the original file to read, and a video original is never read.
            if (item.IsVideo)
            {
                result.Add(item);
                continue;
            }

            var heic = IsHeicName(item.FileName);
            // HEIC is no longer skipped when the OS codec is missing: the device's own
            // thumbnail is a plain JPEG, so those tiles can be filled anyway. The hint
            // stays visible because full-size preview and export still need the codec.

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
