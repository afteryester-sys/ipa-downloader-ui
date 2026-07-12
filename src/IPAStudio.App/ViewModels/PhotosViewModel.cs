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

    public string FileName => Item.FileName;
    public string Album => Item.Album;
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
        if (!string.IsNullOrEmpty(SelectedAlbum) && p.Album != SelectedAlbum) return false;
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
            Albums.Add("");
            foreach (var album in items.Select(i => i.Album).Distinct().OrderBy(a => a))
                Albums.Add(album);

            TotalCount = Photos.Count;
            StatusText = Photos.Count == 0
                ? "Медиафайлы не найдены. Убедитесь, что устройство разблокировано и вы разрешили доступ."
                : $"Найдено медиафайлов: {Photos.Count}";
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
    private void Back() => _navigator?.GoTo(Page.Devices);
}
