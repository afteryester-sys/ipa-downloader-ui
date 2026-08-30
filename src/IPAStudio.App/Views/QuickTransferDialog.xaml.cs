using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IPAStudio.App.Services;
using IPAStudio.App.ViewModels;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;
using Microsoft.Win32;

namespace IPAStudio.App.Views;

/// <summary>
/// Routes IPA archives to installd and every other file to an installed app that exposes
/// Apple File Sharing. System Photos/Music libraries are intentionally not promised: copying
/// bytes into their private storage is not an import on current iOS.
/// </summary>
public partial class QuickTransferDialog : Window
{
    private readonly Device _device;
    private readonly FileSharingService _sharing;
    private readonly OperationService _operations;
    private readonly CancellationTokenSource _cts = new();
    private List<string> _files = new();
    private IReadOnlyList<FileSharingApp> _destinations = Array.Empty<FileSharingApp>();
    private bool _isBusy;

    public QuickTransferDialog(Device device, FileSharingService sharing, OperationService operations)
    {
        InitializeComponent();
        _device = device;
        _sharing = sharing;
        _operations = operations;
        DeviceLine.Text = Loc.Format("L.QuickTransfer.Device", device.Name);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        DestinationPicker.IsEnabled = false;
        DestinationHint.Text = Loc.Get("L.QuickTransfer.ScanningApps");
        try
        {
            _destinations = await _sharing.GetAvailableAppsAsync(_device.Udid, _cts.Token);
            DestinationPicker.ItemsSource = _destinations;
            if (_destinations.Count > 0) DestinationPicker.SelectedIndex = 0;
            DestinationHint.Text = _destinations.Count > 0
                ? Loc.Get("L.QuickTransfer.FileSharingHint")
                : Loc.Get("L.QuickTransfer.NoFileSharingApps");
            DestinationPicker.IsEnabled = _destinations.Count > 0;
            RefreshState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DestinationHint.Text = Loc.Format("L.QuickTransfer.ScanFailed", ex.Message);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var accepted = !_isBusy && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.BorderBrush = accepted ? (Brush)FindResource("Brush.Accent") : (Brush)FindResource("Brush.Border");
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) =>
        DropZone.BorderBrush = (Brush)FindResource("Brush.Border");

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = (Brush)FindResource("Brush.Border");
        e.Handled = true;
        if (!_isBusy && e.Data.GetData(DataFormats.FileDrop) is string[] paths) SetFiles(ExpandPaths(paths));
    }

    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var dialog = new OpenFileDialog
        {
            Title = Loc.Get("L.QuickTransfer.Browse"),
            Multiselect = true,
            Filter = Loc.Get("L.QuickTransfer.FilterAll"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true) SetFiles(dialog.FileNames);
    }

    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var file in files) yield return file;
            }
            else if (File.Exists(path)) yield return path;
        }
    }

    private void SetFiles(IEnumerable<string> files)
    {
        _files = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        RefreshState();
    }

    private void OnDestinationChanged(object sender, SelectionChangedEventArgs e) => RefreshState();

    private void RefreshState()
    {
        if (FilesList is null) return;
        var destination = DestinationPicker.SelectedItem as FileSharingApp;
        FilesList.ItemsSource = _files.Select(path => Describe(path, destination?.Name)).ToList();
        SummaryLine.Text = Loc.Format("L.QuickTransfer.Summary", _files.Count);
        EmptyState.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultState.Visibility = _files.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var onlyIpa = _files.Count > 0 && _files.All(IsIpa);
        TransferButton.IsEnabled = !_isBusy && _files.Count > 0 && (onlyIpa || destination is not null);
    }

    private static TransferFileRow Describe(string path, string? appName)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var (type, glyph) = extension switch
        {
            ".ipa" => (Loc.Get("L.QuickTransfer.TypeApp"), "\uE7BA"),
            ".jpg" or ".jpeg" or ".png" or ".heic" or ".heif" or ".gif" or ".webp" or ".tif" or ".tiff" or ".bmp" or ".dng" or ".cr2" or ".nef" or ".arw" or ".aae" => (Loc.Get("L.QuickTransfer.TypeImage"), "\uEB9F"),
            ".mov" or ".mp4" or ".m4v" or ".avi" or ".3gp" or ".mkv" or ".webm" => (Loc.Get("L.QuickTransfer.TypeVideo"), "\uE714"),
            ".mp3" or ".m4a" or ".aac" or ".wav" or ".aiff" or ".aif" or ".flac" or ".ogg" or ".opus" or ".m4r" => (Loc.Get("L.QuickTransfer.TypeAudio"), "\uE8D6"),
            ".pdf" or ".epub" or ".mobi" or ".azw" or ".azw3" => (Loc.Get("L.QuickTransfer.TypeBook"), "\uE82D"),
            ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".pages" or ".numbers" or ".key" or ".txt" or ".rtf" or ".md" or ".csv" or ".json" or ".xml" => (Loc.Get("L.QuickTransfer.TypeDocument"), "\uE8A5"),
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".bz2" or ".xz" => (Loc.Get("L.QuickTransfer.TypeArchive"), "\uF012"),
            _ => (Loc.Get("L.QuickTransfer.TypeOther"), "\uE8A5"),
        };
        return new TransferFileRow(Path.GetFileName(path), type, glyph,
            IsIpa(path) ? Loc.Get("L.QuickTransfer.Apps") : appName ?? Loc.Get("L.QuickTransfer.NoDestination"));
    }

    private async void OnTransferClicked(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        _isBusy = true;
        TransferButton.IsEnabled = false;
        DestinationPicker.IsEnabled = false;
        ResultState.Visibility = Visibility.Collapsed;
        ProgressState.Visibility = Visibility.Visible;

        var ipaFiles = _files.Where(IsIpa).ToList();
        var documentFiles = _files.Where(path => !IsIpa(path)).ToList();
        if (ipaFiles.Count > 0)
        {
            _operations.StartQueueOperation(OperationKind.Install, ViewModels.Page.Devices,
                Loc.Get("L.Ops.Kind.Install"), _device.Name, _device,
                q => q.BuildFromIpaFiles(ipaFiles, _device));
        }

        try
        {
            if (documentFiles.Count > 0)
            {
                var destination = DestinationPicker.SelectedItem as FileSharingApp
                    ?? throw new InvalidOperationException(Loc.Get("L.QuickTransfer.NoFileSharingApps"));
                for (var index = 0; index < documentFiles.Count; index++)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    var fileNumber = index + 1;
                    var progress = new Progress<FileSharingProgress>(p =>
                    {
                        ProgressBarControl.Value = p.Percent;
                        ProgressLabel.Text = Loc.Format("L.QuickTransfer.CopyingFile", fileNumber,
                            documentFiles.Count, p.FileName, destination.Name);
                    });
                    await _sharing.UploadAsync(_device.Udid, destination, documentFiles[index], progress, _cts.Token);
                }
            }

            ProgressBarControl.Value = 100;
            ProgressLabel.Text = Loc.Format("L.QuickTransfer.Verified", documentFiles.Count, ipaFiles.Count);
            CancelButton.Content = Loc.Get("L.QuickTransfer.CloseAfterError");
            CancelButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            Close();
        }
        catch (Exception ex)
        {
            ProgressBarControl.Value = 0;
            ProgressLabel.Text = Loc.Format("L.QuickTransfer.TransferFailed", ex.Message);
            CancelButton.Content = Loc.Get("L.QuickTransfer.CloseAfterError");
            CancelButton.IsEnabled = true;
        }
        finally
        {
            _isBusy = false;
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        _cts.Dispose();
        base.OnClosed(e);
    }

    private static bool IsIpa(string path) =>
        string.Equals(Path.GetExtension(path), ".ipa", StringComparison.OrdinalIgnoreCase);

    private sealed record TransferFileRow(string Name, string Type, string Glyph, string Destination);
}
