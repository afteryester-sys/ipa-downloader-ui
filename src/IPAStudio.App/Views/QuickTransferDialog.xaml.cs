using System;
using System.Collections.Generic;
using System.IO;
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
/// "Quick Transfer": drop anything onto a device and it lands wherever it belongs — photos and
/// videos into the Camera Roll, .ipa archives installed straight onto the device — without the
/// user having to know which page of the app handles which file type.
///
/// Deliberately two destinations rather than one funnel: the app already has two completely
/// different pipes for "files onto a phone" (AFC into DCIM for media, installd for archives),
/// and pretending they are one operation would either drop one of them or make this dialog
/// reimplement both from scratch. Instead it sorts, shows what it found, and hands each group
/// to the exact same service the dedicated pages already use.
/// </summary>
public partial class QuickTransferDialog : Window
{
    private readonly Device _device;
    private readonly PhotoService _photos;
    private readonly OperationService _operations;

    /// <summary>Media files recognised from the last drop or browse.</summary>
    private List<string> _mediaFiles = new();

    /// <summary>.ipa archives recognised from the last drop or browse.</summary>
    private List<string> _ipaFiles = new();

    private bool _isBusy;

    public QuickTransferDialog(Device device, PhotoService photos, OperationService operations)
    {
        InitializeComponent();

        _device = device;
        _photos = photos;
        _operations = operations;

        DeviceLine.Text = Loc.Format("L.QuickTransfer.Device", device.Name);
    }

    // ===================== Drag and drop =====================

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var accepted = !_isBusy && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.BorderBrush = accepted
            ? (Brush)FindResource("Brush.Accent")
            : (Brush)FindResource("Brush.Border");

        // Without this WPF keeps walking up looking for a handler and shows the wrong
        // cursor even though this window would in fact take the files.
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = (Brush)FindResource("Brush.Border");
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = (Brush)FindResource("Brush.Border");
        e.Handled = true;

        if (_isBusy) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        Classify(ExpandPaths(paths));
    }

    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        var dialog = new OpenFileDialog
        {
            Title = Loc.Get("L.QuickTransfer.Browse"),
            Multiselect = true,
            Filter = Loc.Get("L.QuickTransfer.Filter"),
        };
        if (dialog.ShowDialog(this) != true) return;

        Classify(dialog.FileNames);
    }

    /// <summary>
    /// Folders are expanded rather than rejected, the same as the Photos page drop handler:
    /// dragging a whole export folder is the obvious thing to try, and refusing it would look
    /// like the drop silently failed.
    /// </summary>
    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    yield return file;
            }
            else if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    // ===================== Classification =====================

    /// <summary>
    /// Sorts dropped files into the two destinations this dialog knows, and switches from the
    /// empty drop well to the result view — or back to it, unchanged, if nothing recognisable
    /// came through. Anything else (a PDF, a text file) is silently left out, matching iMazing:
    /// a file this app has nowhere to put is not an error, just not shown.
    /// </summary>
    private void Classify(IEnumerable<string> files)
    {
        _mediaFiles = new List<string>();
        _ipaFiles = new List<string>();

        foreach (var file in files)
        {
            if (PhotoService.IsMediaFile(file)) _mediaFiles.Add(file);
            else if (string.Equals(Path.GetExtension(file), ".ipa", StringComparison.OrdinalIgnoreCase))
                _ipaFiles.Add(file);
        }

        var total = _mediaFiles.Count + _ipaFiles.Count;
        if (total == 0)
        {
            TransferButton.IsEnabled = false;
            return;
        }

        BuildSummary(total);
        EmptyState.Visibility = Visibility.Collapsed;
        ResultState.Visibility = Visibility.Visible;
        TransferButton.IsEnabled = true;
    }

    private void BuildSummary(int total)
    {
        SummaryLine.Text = Loc.Format("L.QuickTransfer.Summary", total);

        DestinationTiles.Children.Clear();
        if (_mediaFiles.Count > 0)
            DestinationTiles.Children.Add(BuildTile("\uE8B9", "L.QuickTransfer.Photos", _mediaFiles.Count));
        if (_ipaFiles.Count > 0)
            DestinationTiles.Children.Add(BuildTile("\uE7BA", "L.QuickTransfer.Apps", _ipaFiles.Count));
    }

    /// <summary>
    /// One destination tile: glyph, name, file count — the same three pieces of information
    /// iMazing's own "will go to X" tile shows, so a user who already knows that dialog
    /// recognises this one immediately.
    /// </summary>
    private Border BuildTile(string glyph, string nameKey, int count)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 32,
            Foreground = (Brush)FindResource("Brush.Accent"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = Loc.Get(nameKey),
            Style = (Style)FindResource("Text.Body"),
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        });
        stack.Children.Add(new TextBlock
        {
            Text = Loc.Format("L.QuickTransfer.FileCount", count),
            Style = (Style)FindResource("Text.Caption"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        });

        return new Border
        {
            Style = (Style)FindResource("Card"),
            Width = 140,
            Margin = new Thickness(0, 0, 12, 12),
            Child = stack,
        };
    }

    // ===================== Transfer =====================

    private async void OnTransferClicked(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        _isBusy = true;

        TransferButton.IsEnabled = false;
        CancelButton.IsEnabled = false;

        // The install is queued first and left to run in the background: it goes through the
        // same operation the App Store and local-folder install paths use, so it is tracked
        // from the corner circle and does not need this window to stay open. Only the photo
        // copy blocks here, because AFC has no queue of its own for this dialog to hand off to.
        if (_ipaFiles.Count > 0)
        {
            var operation = _operations.StartQueueOperation(
                OperationKind.Install,
                ViewModels.Page.Devices,
                Loc.Get("L.Ops.Kind.Install"),
                _device.Name,
                _device,
                q => q.BuildFromIpaFiles(_ipaFiles, _device));

            // Left running; this dialog does not navigate to it, since Quick Transfer is meant
            // to stay a drop-and-go action rather than a detour through the queue screen.
            _ = operation;
        }

        if (_mediaFiles.Count > 0)
        {
            ResultState.Visibility = Visibility.Collapsed;
            ProgressState.Visibility = Visibility.Visible;
            ProgressLabel.Text = Loc.Get("L.QuickTransfer.Importing");

            var progress = new Progress<PhotoTransferProgress>(p =>
            {
                ProgressBarControl.Value = p.Total == 0 ? 0 : (double)p.Completed / p.Total * 100;
                ProgressLabel.Text = Loc.Format("L.QuickTransfer.ImportingFile", p.Completed, p.Total);
            });

            try
            {
                var result = await _photos.ImportAsync(_device.Udid, _mediaFiles, progress);
                ProgressLabel.Text = result.Copied == 0
                    ? Loc.Get("L.Photos.ImportNothingCopied")
                    : result.AppearedInLibrary
                        ? Loc.Format("L.Photos.Imported", result.Copied, result.Total)
                        : Loc.Format("L.Photos.ImportedNotInLibrary", result.Copied, result.Total);
            }
            catch (Exception ex)
            {
                ProgressLabel.Text = ex.Message;
                ProgressBarControl.Value = 0;
                _isBusy = false;
                CancelButton.IsEnabled = true;
                CancelButton.Content = Loc.Get("L.QuickTransfer.CloseAfterError");
                return;
            }
        }

        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
