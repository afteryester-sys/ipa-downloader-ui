using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// Routes files intelligently to their native iOS destinations or app sandboxes (iMazing-style):
/// - IPAs -> direct Home Screen installation via installd
/// - Photos & Videos -> direct Camera Roll import via PhotoService into native Photos
/// - Contacts -> vCard parsing and direct transfer for 1-tap phone book import
/// - Books -> Apple Books or reader
/// - Other media & docs -> specialized player or file manager via Apple File Sharing (house_arrest)
/// </summary>
public partial class QuickTransferDialog : Window
{
    private readonly Device _device;
    private readonly FileSharingService _sharing;
    private readonly OperationService _operations;
    private readonly PhotoService? _photos;
    private readonly CancellationTokenSource _cts = new();

    private List<string> _rawFilePaths = new();
    private IReadOnlyList<FileSharingApp> _availableApps = Array.Empty<FileSharingApp>();
    private FileSharingApp? _userSelectedApp;
    private bool _isBusy;
    private bool _isRefreshing;

    public QuickTransferDialog(
        Device device,
        FileSharingService sharing,
        OperationService operations,
        PhotoService? photos = null)
    {
        InitializeComponent();
        _device = device;
        _sharing = sharing;
        _operations = operations;
        _photos = photos ?? App.Services?.GetService(typeof(PhotoService)) as PhotoService;
        DeviceLine.Text = Loc.Format("L.QuickTransfer.Device", device.Name);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        DestinationPicker.IsEnabled = false;
        DestinationHint.Text = Loc.Get("L.QuickTransfer.ScanningApps");
        try
        {
            var scan = await _sharing.GetAvailableAppsAsync(_device.Udid, _cts.Token);
            _availableApps = scan.Apps;

            DestinationPicker.IsEnabled = _availableApps.Count > 0;
            RefreshState();

            if (_availableApps.Count == 0)
            {
                DestinationHint.Text = scan.HasInfrastructureErrors
                    ? Loc.Format("L.QuickTransfer.ScanFailed", FirstScanError(scan))
                    : Loc.Get("L.QuickTransfer.NoFileSharingApps");
            }
            else if (scan.HasInfrastructureErrors)
            {
                DestinationHint.Text = Loc.Format("L.QuickTransfer.ScanPartial", _availableApps.Count,
                    scan.CheckedApps, FirstScanError(scan));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DestinationHint.Text = Loc.Format("L.QuickTransfer.ScanFailed", ex.Message);
        }
    }

    private static string FirstScanError(FileSharingScanResult scan)
    {
        const int maxLength = 180;
        var error = scan.InfrastructureErrors.FirstOrDefault() ?? Loc.Get("L.QuickTransfer.UnknownScanError");
        return error.Length <= maxLength ? error : error[..maxLength] + "…";
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
        if (!_isBusy && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            SetFiles(paths);
        }
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
        if (dialog.ShowDialog(this) == true)
        {
            SetFiles(dialog.FileNames);
        }
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        _rawFilePaths.Clear();
        _userSelectedApp = null;
        RefreshState();
    }

    private void SetFiles(IEnumerable<string> paths)
    {
        _rawFilePaths = FileClassifier.ExpandPaths(paths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _userSelectedApp = null; // Reset to allow smart recommendation for newly dropped set
        RefreshState();
    }

    private void OnDestinationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshing) return;
        if (DestinationPicker.SelectedItem is AppMatchScore score)
        {
            _userSelectedApp = score.App;
            RefreshState();
        }
    }

    private void RefreshState()
    {
        if (FilesList is null || DestinationPicker is null) return;
        _isRefreshing = true;

        try
        {
            // Evaluate payloads preliminarily with current manual app if any
            var preliminary = _rawFilePaths
                .Select(p => FileClassifier.Describe(p, _userSelectedApp?.Name))
                .ToList();

            // Rank available apps by compatibility with the dropped files
            var ranked = AppRecommendationEngine.RankApps(preliminary, _availableApps);

            // Determine active target app
            FileSharingApp? activeApp = null;
            if (_userSelectedApp != null)
            {
                activeApp = _availableApps.FirstOrDefault(a =>
                    string.Equals(a.BundleId, _userSelectedApp.BundleId, StringComparison.OrdinalIgnoreCase))
                    ?? (ranked.Count > 0 ? ranked[0].App : null);
            }
            else
            {
                activeApp = ranked.Count > 0 ? ranked[0].App : null;
            }

            // Build final payloads with specific target display names
            var finalPayloads = new List<TransferPayload>();
            foreach (var path in _rawFilePaths)
            {
                var category = FileClassifier.Classify(path);
                string targetName;

                if (category == FileCategory.App)
                {
                    targetName = Loc.Get("L.QuickTransfer.TargetInstall");
                }
                else if (FileClassifier.IsCameraRollMedia(path) && _photos != null && _userSelectedApp == null)
                {
                    targetName = Loc.Get("L.QuickTransfer.TargetPhotos");
                }
                else if (category == FileCategory.Contact && _userSelectedApp == null)
                {
                    targetName = activeApp?.Name ?? Loc.Get("L.QuickTransfer.TargetContacts");
                }
                else
                {
                    targetName = activeApp?.Name ?? Loc.Get("L.QuickTransfer.NoDestination");
                }

                finalPayloads.Add(FileClassifier.Describe(path, targetName));
            }

            FilesList.ItemsSource = finalPayloads;
            DestinationPicker.ItemsSource = ranked;

            if (activeApp != null)
            {
                var matchingScore = ranked.FirstOrDefault(s =>
                    string.Equals(s.BundleId, activeApp.BundleId, StringComparison.OrdinalIgnoreCase));
                DestinationPicker.SelectedItem = matchingScore ?? (ranked.Count > 0 ? ranked[0] : null);
            }
            else if (ranked.Count > 0)
            {
                DestinationPicker.SelectedIndex = 0;
            }

            // Update destination hint text
            var hasOnlyCameraRoll = finalPayloads.Count > 0 && finalPayloads.All(p => FileClassifier.IsCameraRollMedia(p.FullPath));
            var hasOnlyIpa = finalPayloads.Count > 0 && finalPayloads.All(p => p.Category == FileCategory.App);
            var hasOnlyContacts = finalPayloads.Count > 0 && finalPayloads.All(p => p.Category == FileCategory.Contact);

            if (hasOnlyIpa)
            {
                DestinationHint.Text = Loc.Get("L.QuickTransfer.TargetInstall") + " — " + Loc.Get("L.QuickTransfer.Supported");
            }
            else if (hasOnlyCameraRoll && _userSelectedApp == null)
            {
                DestinationHint.Text = Loc.Get("L.QuickTransfer.TargetPhotos") + " — " + Loc.Get("L.QuickTransfer.Supported");
            }
            else if (hasOnlyContacts)
            {
                DestinationHint.Text = string.Format(Loc.Get("L.QuickTransfer.RecommendedFor"),
                    Loc.Get("L.QuickTransfer.TypeContact"), activeApp?.Name ?? "iOS");
            }
            else if (DestinationPicker.SelectedItem is AppMatchScore selectedScore)
            {
                if (selectedScore.IsRecommended && !string.IsNullOrWhiteSpace(selectedScore.MatchReason))
                {
                    DestinationHint.Text = selectedScore.MatchReason;
                }
                else
                {
                    DestinationHint.Text = Loc.Get("L.QuickTransfer.FileSharingHint");
                }
            }
            else if (_availableApps.Count == 0)
            {
                DestinationHint.Text = Loc.Get("L.QuickTransfer.NoFileSharingApps");
            }
            else
            {
                DestinationHint.Text = Loc.Get("L.QuickTransfer.FileSharingHint");
            }

            SummaryLine.Text = Loc.Format("L.QuickTransfer.Summary", finalPayloads.Count);
            EmptyState.Visibility = finalPayloads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ResultState.Visibility = finalPayloads.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            var canTransferNative = hasOnlyIpa || (hasOnlyCameraRoll && _photos != null);
            var hasDestination = activeApp != null || DestinationPicker.SelectedItem != null;
            TransferButton.IsEnabled = !_isBusy && finalPayloads.Count > 0 && (canTransferNative || hasDestination);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private async void OnTransferClicked(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        _isBusy = true;
        TransferButton.IsEnabled = false;
        DestinationPicker.IsEnabled = false;
        ResultState.Visibility = Visibility.Collapsed;
        ProgressState.Visibility = Visibility.Visible;

        var ipaFiles = _rawFilePaths.Where(p => FileClassifier.Classify(p) == FileCategory.App).ToList();
        var cameraRollFiles = _rawFilePaths.Where(p =>
            FileClassifier.Classify(p) != FileCategory.App &&
            FileClassifier.IsCameraRollMedia(p) &&
            (_userSelectedApp == null && _photos != null)).ToList();
        var appFiles = _rawFilePaths.Where(p =>
            !ipaFiles.Contains(p) && !cameraRollFiles.Contains(p)).ToList();

        // 1. IPAs to installd
        if (ipaFiles.Count > 0)
        {
            _operations.StartQueueOperation(OperationKind.Install, ViewModels.Page.Devices,
                Loc.Get("L.Ops.Kind.Install"), _device.Name, _device,
                q => q.BuildFromIpaFiles(ipaFiles, _device));
        }

        try
        {
            int photosDone = 0;
            // 2. Photos to Camera Roll
            if (cameraRollFiles.Count > 0 && _photos != null)
            {
                var photoProgress = new Progress<PhotoTransferProgress>(p =>
                {
                    ProgressBarControl.Value = p.Total > 0 ? (double)p.Completed / p.Total * 100 : 0;
                    ProgressLabel.Text = Loc.Format("L.QuickTransfer.ImportingPhotos", p.Completed, p.Total, p.CurrentFile);
                });
                var result = await _photos.ImportAsync(_device.Udid, cameraRollFiles, photoProgress, _cts.Token);
                photosDone = result.Copied;
            }

            int appFilesDone = 0;
            // 3. Document / Media / Contact files to destination app
            if (appFiles.Count > 0)
            {
                var destination = (_userSelectedApp ?? (DestinationPicker.SelectedItem as AppMatchScore)?.App)
                    ?? throw new InvalidOperationException(Loc.Get("L.QuickTransfer.NoFileSharingApps"));

                for (var index = 0; index < appFiles.Count; index++)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    var fileNumber = index + 1;
                    var filePath = appFiles[index];
                    var sw = Stopwatch.StartNew();
                    long lastBytes = 0;
                    long lastTickMs = 0;

                    var progress = new Progress<FileSharingProgress>(p =>
                    {
                        ProgressBarControl.Value = p.Percent;
                        ProgressLabel.Text = Loc.Format("L.QuickTransfer.CopyingFile", fileNumber,
                            appFiles.Count, p.FileName, destination.Name);

                        var elapsedMs = sw.ElapsedMilliseconds;
                        var dtSeconds = (elapsedMs - lastTickMs) / 1000.0;
                        if (dtSeconds >= 0.2 || p.BytesWritten == p.TotalBytes)
                        {
                            var bytesDiff = p.BytesWritten - lastBytes;
                            var speed = dtSeconds > 0 ? bytesDiff / dtSeconds : 0;
                            var speedText = FormatSpeed(speed);
                            var bytesText = $"{FormatBytes(p.BytesWritten)} / {FormatBytes(p.TotalBytes)}";
                            ProgressSpeedLabel.Text = $"{speedText} • {bytesText}";
                            lastBytes = p.BytesWritten;
                            lastTickMs = elapsedMs;
                        }
                    });

                    await _sharing.UploadAsync(_device.Udid, destination, filePath, progress, _cts.Token);
                    appFilesDone++;
                }
            }

            ProgressBarControl.Value = 100;
            ProgressSpeedLabel.Text = string.Empty;
            ProgressLabel.Text = Loc.Format("L.QuickTransfer.VerifiedAll", photosDone, appFilesDone, ipaFiles.Count);
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
            ProgressSpeedLabel.Text = string.Empty;
            ProgressLabel.Text = Loc.Format("L.QuickTransfer.TransferFailed", ex.Message);
            CancelButton.Content = Loc.Get("L.QuickTransfer.CloseAfterError");
            CancelButton.IsEnabled = true;
        }
        finally
        {
            _isBusy = false;
        }
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1_048_576) return $"{bytesPerSec / 1_048_576.0:0.0} MB/s";
        if (bytesPerSec >= 1024) return $"{bytesPerSec / 1024.0:0.0} KB/s";
        return $"{bytesPerSec:0} B/s";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:0.0} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:0.0} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes} B";
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
}
