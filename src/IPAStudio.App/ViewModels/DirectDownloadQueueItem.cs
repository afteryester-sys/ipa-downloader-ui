using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.App.Services;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;

namespace IPAStudio.App.ViewModels;

/// <summary>State of an item inside the direct download queue.</summary>
public enum DirectQueueState
{
    Queued,
    Connecting,
    Transferring,
    Finalizing,
    Done,
    Failed,
    Cancelled,
    Installing
}

/// <summary>
/// Represents a single app undergoing background direct download and device installation.
/// </summary>
public sealed partial class DirectDownloadQueueItem : ObservableObject
{
    private readonly InstallService _install;
    private readonly OperationService _operations;
    private readonly Action<DirectDownloadQueueItem> _onRemove;
    private readonly Action<DirectDownloadQueueItem> _onRetry;

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public AppEntry App { get; }

    public string Name => App.Name;
    public string? BundleId => App.BundleId;
    public string? Version => App.LatestVersion;
    public string? Developer => App.Developer;

    public string DestinationFolder { get; }
    public string? FileNameOverride { get; }

    [ObservableProperty]
    private string? _iconUrl;

    [ObservableProperty]
    private ImageSource? _iconImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQueued))]
    [NotifyPropertyChangedFor(nameof(IsTransferring))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsCancelled))]
    [NotifyPropertyChangedFor(nameof(IsInstalling))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    [NotifyPropertyChangedFor(nameof(CanOpenFolder))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private DirectQueueState _state = DirectQueueState.Queued;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isIndeterminate;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenFolder))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private string? _ipaPath;

    [ObservableProperty]
    private string _sizeInfoText = "";

    [ObservableProperty]
    private string _speedText = "";

    [ObservableProperty]
    private string _remainingText = "";

    [ObservableProperty]
    private bool _isDevicePickerOpen;

    public ObservableCollection<SelectableDevice> TargetDevices { get; } = new();

    public bool HasConnectedDevices => TargetDevices.Count > 0;
    public bool HasSelectedDevices => TargetDevices.Any(d => d.IsSelected);

    public bool IsQueued => State == DirectQueueState.Queued;
    public bool IsTransferring => State == DirectQueueState.Connecting || State == DirectQueueState.Transferring || State == DirectQueueState.Finalizing;
    public bool IsDone => State == DirectQueueState.Done;
    public bool IsFailed => State == DirectQueueState.Failed;
    public bool IsCancelled => State == DirectQueueState.Cancelled;
    public bool IsInstalling => State == DirectQueueState.Installing;
    public bool IsActive => IsTransferring || IsInstalling;

    public bool CanCancel => State == DirectQueueState.Queued || State == DirectQueueState.Connecting || State == DirectQueueState.Transferring || State == DirectQueueState.Finalizing || State == DirectQueueState.Installing;
    public bool CanRetry => State == DirectQueueState.Failed || State == DirectQueueState.Cancelled;
    public bool CanOpenFolder => State == DirectQueueState.Done && !string.IsNullOrEmpty(IpaPath) && File.Exists(IpaPath);
    public bool CanInstall => State == DirectQueueState.Done && !string.IsNullOrEmpty(IpaPath) && File.Exists(IpaPath) && HasConnectedDevices;

    public CancellationTokenSource? Cts { get; set; }
    public Operation? BackgroundOperation { get; set; }

    public DirectDownloadQueueItem(
        AppEntry app,
        string destinationFolder,
        string? fileNameOverride,
        string? iconUrl,
        ImageSource? iconImage,
        InstallService install,
        OperationService operations,
        Action<DirectDownloadQueueItem> onRemove,
        Action<DirectDownloadQueueItem> onRetry)
    {
        App = app;
        DestinationFolder = destinationFolder;
        FileNameOverride = fileNameOverride;
        _iconUrl = iconUrl;
        _iconImage = iconImage;
        _install = install;
        _operations = operations;
        _onRemove = onRemove;
        _onRetry = onRetry;

        StatusText = Loc.Get("L.Direct.Queue.Waiting");
    }

    public void UpdateDevices(IEnumerable<Device> devices)
    {
        var deviceList = devices.ToList();
        var selectedUdids = TargetDevices.Where(d => d.IsSelected).Select(d => d.Udid).ToHashSet();
        TargetDevices.Clear();

        foreach (var dev in deviceList)
        {
            var isSelected = selectedUdids.Contains(dev.Udid) || (selectedUdids.Count == 0 && deviceList.Count == 1);
            TargetDevices.Add(new SelectableDevice(dev, isSelected));
        }

        OnPropertyChanged(nameof(HasConnectedDevices));
        OnPropertyChanged(nameof(HasSelectedDevices));
        OnPropertyChanged(nameof(CanInstall));
    }

    [RelayCommand]
    private void Cancel()
    {
        try
        {
            Cts?.Cancel();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to cancel direct download '{Name}': {ex.Message}");
        }

        if (State == DirectQueueState.Queued)
        {
            State = DirectQueueState.Cancelled;
            StatusText = Loc.Get("L.Direct.Queue.Cancelled");
        }
    }

    [RelayCommand]
    private void Retry()
    {
        if (CanRetry)
        {
            _onRetry(this);
        }
    }

    [RelayCommand]
    private void Remove()
    {
        Cancel();
        _onRemove(this);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (string.IsNullOrEmpty(IpaPath) || !File.Exists(IpaPath)) return;
        try
        {
            Process.Start("explorer.exe", $"/select,\"{IpaPath}\"");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to open folder for {IpaPath}: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ToggleDevicePicker()
    {
        IsDevicePickerOpen = !IsDevicePickerOpen;
    }

    [RelayCommand]
    private void CloseDevicePicker()
    {
        IsDevicePickerOpen = false;
    }

    [RelayCommand]
    private void ToggleDevice(SelectableDevice item)
    {
        if (item is null) return;
        item.IsSelected = !item.IsSelected;
        OnPropertyChanged(nameof(HasSelectedDevices));
        OnPropertyChanged(nameof(CanInstall));
    }

    [RelayCommand]
    private async Task InstallToDeviceAsync()
    {
        var targets = TargetDevices.Where(d => d.IsSelected).Select(d => d.Device).ToList();
        if (targets.Count == 0 || string.IsNullOrEmpty(IpaPath) || !File.Exists(IpaPath))
            return;

        Cts = new CancellationTokenSource();
        State = DirectQueueState.Installing;
        IsIndeterminate = false;
        Progress = 0;
        ErrorText = null;
        StatusText = Loc.Get("L.Direct.Installing");

        var targetNames = string.Join(", ", targets.Select(t => t.Name));
        var operation = _operations.Start(new Operation(
            OperationKind.Install,
            Page.DirectDownload,
            Loc.Get("L.Ops.Kind.Install"),
            $"{Name} → {targetNames}",
            cancel: () => Cts.Cancel()));

        try
        {
            int total = targets.Count;
            int current = 0;
            int successful = 0;
            string? lastError = null;

            foreach (var targetDevice in targets)
            {
                current++;
                Cts.Token.ThrowIfCancellationRequested();

                var prefix = total > 1 ? $"[{current}/{total}] {targetDevice.Name}: " : "";

                var progress = new System.Progress<InstallProgress>(p =>
                {
                    var overallPercent = ((current - 1) * 100.0 + p.Percent) / total;
                    Progress = overallPercent;

                    StatusText = p.Percent > 0
                        ? $"{prefix}{p.Status}: {p.Percent:0}%"
                        : $"{prefix}{p.Status}";

                    operation.Progress = Progress;
                    operation.Detail = StatusText;
                });

                var result = await _install.InstallAsync(
                    udid: targetDevice.Udid,
                    ipaPath: IpaPath,
                    progress: progress,
                    ct: Cts.Token,
                    deviceAppleId: targetDevice.AppleId,
                    bundleId: App.BundleId,
                    allowAccountMismatch: true).ConfigureAwait(true);

                if (result.Success)
                {
                    successful++;
                }
                else
                {
                    lastError = result.Error;
                    AppLog.Warn($"Direct install of '{Name}' to device '{targetDevice.Name}' failed: {result.Error}");
                }
            }

            if (successful == total)
            {
                Progress = 100;
                State = DirectQueueState.Done;
                StatusText = total == 1
                    ? Loc.Format("L.Direct.InstallSuccess", targets[0].Name)
                    : Loc.Format("L.Direct.InstallSuccessMulti", total);
                operation.Finish(OperationState.Done);
                IsDevicePickerOpen = false;
            }
            else if (successful > 0)
            {
                State = DirectQueueState.Done;
                StatusText = Loc.Format("L.Direct.InstallPartial", successful, total);
                operation.Finish(OperationState.Done);
            }
            else
            {
                State = DirectQueueState.Failed;
                ErrorText = lastError ?? Loc.Get("L.Direct.InstallFailed");
                StatusText = ErrorText;
                operation.Finish(OperationState.Failed, ErrorText);
            }
        }
        catch (OperationCanceledException)
        {
            State = DirectQueueState.Done;
            StatusText = Loc.Get("L.Direct.Queue.Cancelled");
            operation.Finish(OperationState.Cancelled);
        }
        catch (Exception ex)
        {
            State = DirectQueueState.Failed;
            ErrorText = ex.Message;
            StatusText = ErrorText;
            operation.Finish(OperationState.Failed, ex.Message);
        }
    }
}
