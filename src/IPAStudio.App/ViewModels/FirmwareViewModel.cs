using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.App.Services;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;
using Microsoft.Win32;

namespace IPAStudio.App.ViewModels;

public sealed partial class FirmwareViewModel : ObservableObject, IPageAware
{
    private readonly FirmwareCatalogService _catalog;
    private readonly FirmwareDownloadService _downloads;
    private readonly SettingsService _settings;
    private readonly OperationService _operations;
    private readonly FirmwareAutoUpdateService _autoUpdates;
    private INavigator? _navigator;
    private CancellationTokenSource? _loadCts;
    private List<FirmwareDevice> _allDevices = new();
    private List<FirmwareRelease> _deviceFirmwares = new();
    private readonly Dictionary<FirmwareDownloadJob, Operation> _jobOperations = new();
    private bool _startupResumeAsked;

    public IReadOnlyList<FirmwareDevice> AllDevices => _allDevices;

    public ObservableCollection<FirmwareDevice> CatalogDevices { get; } = new();
    public ObservableCollection<FirmwareDevice> MyDevices { get; } = new();
    public ObservableCollection<FirmwareRelease> Firmwares { get; } = new();

    /// <summary>Live download queue: one row per firmware, each independently controllable.</summary>
    public ObservableCollection<FirmwareDownloadJob> Jobs { get; } = new();

    /// <summary>
    /// Asked by the view when interrupted downloads are found at startup. Returning true
    /// resumes them. Kept as a hook so the ViewModel never talks to MessageBox directly.
    /// </summary>
    public Func<IReadOnlyList<FirmwarePendingDownload>, bool>? ConfirmResumePending { get; set; }

    public FirmwareViewModel(FirmwareCatalogService catalog, FirmwareDownloadService downloads,
        SettingsService settings, OperationService operations, FirmwareAutoUpdateService autoUpdates)
    {
        _catalog = catalog;
        _downloads = downloads;
        _settings = settings;
        _operations = operations;
        _autoUpdates = autoUpdates;
        DestinationFolder = settings.Current.FirmwareFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "IPA Studio", "Firmwares");
        SegmentCount = Math.Clamp(settings.Current.FirmwareDownloadThreads, 1, 8);
        AutoCheckIntervalHours = Math.Clamp(settings.Current.FirmwareCheckIntervalHours, 1, 168);
        Jobs.CollectionChanged += OnJobsChanged;
        _autoUpdates.StatusChanged += OnAutoUpdateStatusChanged;
        RefreshAutoUpdateTimestamps();
    }

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private FirmwareDevice? _selectedCatalogDevice;
    [ObservableProperty] private FirmwareDevice? _selectedDevice;
    [ObservableProperty] private FirmwareRelease? _selectedFirmware;
    [ObservableProperty] private string _destinationFolder = "";
    [ObservableProperty] private int _segmentCount = 4;
    [ObservableProperty] private bool _signedOnly = true;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _errorText;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    private bool _autoUpdateSelected;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    private bool _isAutoUpdateChecking;
    [ObservableProperty] private string _autoUpdateStatusText = "";
    [ObservableProperty] private string _nextCheckText = "—";
    [ObservableProperty] private string _latestAutoVersionText = "—";
    [ObservableProperty] private string? _autoUpdateErrorText;
    [ObservableProperty] private int _autoCheckIntervalHours = 6;

    // Aggregate state for the right-hand overall panel.
    [ObservableProperty] private double _overallProgress;
    [ObservableProperty] private string _overallSizeText = "";
    [ObservableProperty] private string _overallSpeedText = "";
    [ObservableProperty] private string _overallEtaText = "";
    [ObservableProperty] private int _activeCount;
    [ObservableProperty] private int _finishedCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private string _lastCheckText = "—";
    [ObservableProperty] private string _lastDownloadText = "—";

    public bool HasJobs => Jobs.Count > 0;
    public bool HasActiveJobs => ActiveCount > 0;

    partial void OnActiveCountChanged(int value) => OnPropertyChanged(nameof(HasActiveJobs));

    partial void OnAutoCheckIntervalHoursChanged(int value)
    {
        _settings.Current.FirmwareCheckIntervalHours = Math.Clamp(value, 1, 168);
        _settings.Save();
        _autoUpdates.Reschedule();
    }

    partial void OnSearchTextChanged(string value) => ApplyDeviceFilter();
    partial void OnSignedOnlyChanged(bool value) => ApplyFirmwareFilter();
    partial void OnSelectedFirmwareChanged(FirmwareRelease? value) => EnqueueDownloadCommand.NotifyCanExecuteChanged();

    partial void OnSelectedDeviceChanged(FirmwareDevice? value)
    {
        var subscription = value is null
            ? null
            : _settings.Current.FirmwareSubscriptions.FirstOrDefault(s => s.Identifier == value.Identifier);
        AutoUpdateSelected = subscription?.AutoUpdateEnabled == true;
        IsAutoUpdateChecking = false;
        RemoveDeviceCommand.NotifyCanExecuteChanged();
        EnqueueDownloadCommand.NotifyCanExecuteChanged();
        ToggleAutoUpdateCommand.NotifyCanExecuteChanged();
        CheckNowCommand.NotifyCanExecuteChanged();
        RefreshAutoUpdateTimestamps();
        _ = LoadFirmwaresAsync(value);
    }

    partial void OnSelectedCatalogDeviceChanged(FirmwareDevice? value) => AddDeviceCommand.NotifyCanExecuteChanged();

    public async void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;
        if (_allDevices.Count == 0) await LoadDevicesAsync();
        OfferPendingResume();
    }

    [RelayCommand] private void GoBack() => _navigator?.GoBack();
    [RelayCommand] private void GoHome() => _navigator?.GoHome();

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog { InitialDirectory = Directory.Exists(DestinationFolder) ? DestinationFolder : null };
        if (dialog.ShowDialog() == true) DestinationFolder = dialog.FolderName;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _allDevices.Clear();
        await LoadDevicesAsync();
    }

    [RelayCommand] private void OpenFolder()
    {
        if (Directory.Exists(DestinationFolder))
            Process.Start(new ProcessStartInfo("explorer.exe", DestinationFolder) { UseShellExecute = true });
    }

    private bool CanToggleAutoUpdate() => SelectedDevice is not null;

    [RelayCommand(CanExecute = nameof(CanToggleAutoUpdate))]
    private async Task ToggleAutoUpdateAsync()
    {
        var device = SelectedDevice;
        if (device is null) return;

        var subscription = _settings.Current.FirmwareSubscriptions
            .FirstOrDefault(s => s.Identifier == device.Identifier);
        if (subscription is null) return;

        subscription.AutoUpdateEnabled = !subscription.AutoUpdateEnabled;
        if (!subscription.AutoUpdateEnabled)
        {
            subscription.NextCheckUtc = null;
            subscription.LastError = null;
        }
        _settings.Save();

        AutoUpdateSelected = subscription.AutoUpdateEnabled;
        RefreshAutoUpdateTimestamps();
        _autoUpdates.Reschedule();

        if (subscription.AutoUpdateEnabled)
            await _autoUpdates.CheckNowAsync(subscription.Identifier);
    }

    private bool CanCheckNow() =>
        SelectedDevice is not null && AutoUpdateSelected && !IsAutoUpdateChecking;

    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private Task CheckNowAsync() =>
        SelectedDevice is null
            ? Task.CompletedTask
            : _autoUpdates.CheckNowAsync(SelectedDevice.Identifier);

    private void OnAutoUpdateStatusChanged(object? sender, FirmwareAutoUpdateStatus status)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.BeginInvoke(() =>
        {
            if (!string.Equals(SelectedDevice?.Identifier, status.Identifier, StringComparison.Ordinal)) return;
            IsAutoUpdateChecking = status.IsChecking;
            RefreshAutoUpdateTimestamps();
        });
    }

    // ---------------------------------------------------------------- queue

    private bool CanEnqueueDownload() => SelectedDevice is not null && SelectedFirmware is not null;

    [RelayCommand(CanExecute = nameof(CanEnqueueDownload))]
    private void EnqueueDownload()
    {
        if (SelectedDevice is null || SelectedFirmware is null) return;
        PersistDownloadSettings();
        var destination = Path.Combine(DestinationFolder,
            FirmwareDownloadService.BuildFileName(SelectedDevice.Name, SelectedFirmware.Version));

        // Re-queuing the same file should revive the existing row rather than duplicate it,
        // otherwise two runners would fight over the same manifest and part files.
        var existing = Jobs.FirstOrDefault(j =>
            string.Equals(j.DestinationPath, destination, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing.CanResume) ResumeJob(existing);
            return;
        }

        var job = new FirmwareDownloadJob(SelectedDevice, SelectedFirmware, destination, PauseJob, ResumeJob, StopJob);
        Jobs.Add(job);
        _ = StartJobAsync(job);
    }

    [RelayCommand]
    private Task PauseAllAsync() => Task.WhenAll(
        Jobs.Where(job => job.CanPause).ToList().Select(PauseJobCoreAsync));

    [RelayCommand]
    private Task StopAllAsync() => Task.WhenAll(
        Jobs.ToList().Select(StopJobCoreAsync));

    [RelayCommand]
    private Task ResumeAllAsync() => Task.WhenAll(
        Jobs.Where(job => job.CanResume).ToList().Select(ResumeJobCoreAsync));

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var job in Jobs.Where(job => job.IsFinished).ToList()) Jobs.Remove(job);
        RecomputeAggregate();
    }

    private void PauseJob(FirmwareDownloadJob job) => _ = PauseJobCoreAsync(job);

    private async Task PauseJobCoreAsync(FirmwareDownloadJob job)
    {
        Task? runner;
        lock (job.SyncRoot)
        {
            runner = job.RunnerTask is { IsCompleted: false } ? job.RunnerTask : null;
            if (runner is null)
            {
                if (job.State == FirmwareJobState.Queued)
                {
                    job.PauseRequested = true;
                    job.State = FirmwareJobState.Paused;
                    job.StatusText = Loc.Get("L.Firmware.Paused");
                    RecomputeAggregate();
                }
                return;
            }

            if (!job.CanPause && job.State != FirmwareJobState.Pausing) return;
            job.PauseRequested = true;
            job.StopRequested = false;
            job.State = FirmwareJobState.Pausing;
            job.StatusText = Loc.Get("L.Firmware.Pausing");
            job.Cts?.Cancel();
        }

        try { await runner; }
        catch { }
    }

    private void ResumeJob(FirmwareDownloadJob job) => _ = ResumeJobCoreAsync(job);

    private async Task ResumeJobCoreAsync(FirmwareDownloadJob job)
    {
        Task? previous;
        lock (job.SyncRoot)
            previous = job.RunnerTask is { IsCompleted: false } ? job.RunnerTask : null;

        if (previous is not null)
        {
            try { await previous; }
            catch { }
        }

        if (!Jobs.Contains(job) || !job.CanResume) return;
        job.ErrorText = null;
        await StartJobAsync(job);
    }

    private void StopJob(FirmwareDownloadJob job) => _ = StopJobCoreAsync(job);

    private async Task StopJobCoreAsync(FirmwareDownloadJob job)
    {
        Task? runner;
        lock (job.SyncRoot)
        {
            job.StopRequested = true;
            job.PauseRequested = false;
            runner = job.RunnerTask is { IsCompleted: false } ? job.RunnerTask : null;
            job.Cts?.Cancel();
        }

        if (runner is not null)
        {
            try { await runner; }
            catch { }
        }

        _downloads.DeleteTemporaryFiles(job.DestinationPath);
        Jobs.Remove(job);
        if (_jobOperations.Remove(job, out var operation))
            operation.Finish(OperationState.Cancelled, Loc.Get("L.Firmware.Stopped"));
        StatusText = Loc.Get("L.Firmware.Stopped");
        RecomputeAggregate();
    }

    private Task StartJobAsync(FirmwareDownloadJob job)
    {
        lock (job.SyncRoot)
        {
            if (job.RunnerTask is { IsCompleted: false }) return job.RunnerTask;

            job.Cts?.Dispose();
            var cts = new CancellationTokenSource();
            job.Cts = cts;
            job.PauseRequested = false;
            job.StopRequested = false;
            job.RunnerTask = RunJobAsync(job, cts);
            return job.RunnerTask;
        }
    }

    private async Task RunJobAsync(FirmwareDownloadJob job, CancellationTokenSource cts)
    {
        job.State = FirmwareJobState.Running;
        job.StatusText = Loc.Get("L.Firmware.Job.Running");
        job.ErrorText = null;
        RecomputeAggregate();

        if (!_jobOperations.TryGetValue(job, out var operation))
        {
            operation = _operations.Start(new Operation(OperationKind.Firmware, Page.Firmware,
                Loc.Get("L.Firmware.Operation"), job.Title, cancel: () => PauseJob(job)));
            _jobOperations[job] = operation;
        }

        var reporter = new Progress<FirmwareDownloadProgress>(progress =>
        {
            job.Downloaded = progress.Downloaded;
            job.Total = progress.Total;
            job.BytesPerSecond = progress.BytesPerSecond;
            job.Progress = progress.Percent;
            job.StatusText = job.SizeText;
            operation.Progress = progress.Percent;
            operation.Detail = $"{job.SizeText} · {job.SpeedText}";
            RecomputeAggregate();
        });

        try
        {
            string savedPath;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    savedPath = await _downloads.DownloadToPathAsync(
                        job.Device,
                        job.Firmware,
                        job.DestinationPath,
                        SegmentCount,
                        reporter,
                        cts.Token);
                    break;
                }
                catch (Exception ex) when ((ex is HttpRequestException or IOException)
                                           && attempt < 12 && !cts.IsCancellationRequested)
                {
                    var wait = Math.Min(30, 1 << Math.Min(attempt, 5));
                    job.State = FirmwareJobState.Reconnecting;
                    job.StatusText = string.Format(Loc.Get("L.Firmware.Reconnecting"), wait);
                    RecomputeAggregate();
                    await Task.Delay(TimeSpan.FromSeconds(wait), cts.Token);
                    job.State = FirmwareJobState.Running;
                }
            }

            job.State = FirmwareJobState.Done;
            job.Progress = 100;
            job.BytesPerSecond = 0;
            job.StatusText = Loc.Get("L.Firmware.Done");
            operation.Finish(OperationState.Done, job.StatusText);
            _jobOperations.Remove(job);
            RecordCompletedDownload(job.Device, job.Firmware, savedPath);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            job.State = FirmwareJobState.Paused;
            job.BytesPerSecond = 0;
            job.StatusText = job.StopRequested
                ? Loc.Get("L.Firmware.Stopped")
                : Loc.Get("L.Firmware.Paused");
            operation.Finish(OperationState.Cancelled, job.StatusText);
            _jobOperations.Remove(job);
        }
        catch (Exception ex)
        {
            var userMessage = ex is InvalidDataException or EndOfStreamException
                ? ex.Message
                : Loc.Get("L.Firmware.Failed");
            AppLog.Warn($"Firmware download failed for {job.Device.Identifier} {job.Firmware.BuildId}: {ex.Message}");
            job.State = FirmwareJobState.Failed;
            job.BytesPerSecond = 0;
            job.ErrorText = userMessage;
            job.StatusText = Loc.Get("L.Firmware.Failed");
            operation.Finish(OperationState.Failed, userMessage);
            _jobOperations.Remove(job);
        }
        finally
        {
            lock (job.SyncRoot)
            {
                if (ReferenceEquals(job.Cts, cts)) job.Cts = null;
            }
            cts.Dispose();
            RecomputeAggregate();
        }
    }

    private void RunPending(FirmwarePendingDownload pending)
    {
        var device = new FirmwareDevice
        {
            Identifier = pending.DeviceIdentifier ?? "",
            Name = string.IsNullOrWhiteSpace(pending.DeviceName) ? pending.FileName : pending.DeviceName,
        };
        var firmware = new FirmwareRelease
        {
            Identifier = device.Identifier,
            Version = pending.FirmwareVersion ?? "",
            BuildId = pending.BuildId ?? "",
            Url = pending.Url,
            Sha1 = pending.Sha1,
            Md5 = pending.Md5,
            FileSize = pending.Total,
        };
        var job = new FirmwareDownloadJob(device, firmware, pending.DestinationPath, PauseJob, ResumeJob, StopJob)
        {
            Title = pending.FileName,
            Subtitle = Loc.Get("L.Firmware.Job.Recovered"),
            Total = pending.Total,
            Downloaded = pending.Downloaded,
            Progress = pending.Percent,
        };
        Jobs.Add(job);
        _ = StartJobAsync(job);
    }

    /// <summary>Offers to continue interrupted downloads, once per app run.</summary>
    public void OfferPendingResume()
    {
        var confirm = ConfirmResumePending;
        if (_startupResumeAsked || confirm is null) return;

        var pending = _downloads.FindPendingDownloads(DestinationFolder)
            .Where(p => Jobs.All(j => !string.Equals(j.DestinationPath, p.DestinationPath, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        _startupResumeAsked = true;
        if (pending.Count == 0 || !confirm(pending)) return;

        foreach (var item in pending) RunPending(item);
    }

    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (FirmwareDownloadJob job in e.OldItems) job.PropertyChanged -= OnJobPropertyChanged;
        if (e.NewItems is not null)
            foreach (FirmwareDownloadJob job in e.NewItems) job.PropertyChanged += OnJobPropertyChanged;
        OnPropertyChanged(nameof(HasJobs));
        RecomputeAggregate();
    }

    private void OnJobPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FirmwareDownloadJob.State)) RecomputeAggregate();
    }

    /// <summary>
    /// Overall progress is byte-weighted rather than a mean of percentages, so a small
    /// firmware finishing early cannot make a multi-GB queue look almost done.
    /// </summary>
    private void RecomputeAggregate()
    {
        long downloaded = 0, total = 0;
        double speed = 0;
        var active = 0;
        var finished = 0;
        var failed = 0;

        foreach (var job in Jobs)
        {
            var jobTotal = Math.Max(0, job.ExpectedTotal);
            total += jobTotal;
            downloaded += job.IsFinished && jobTotal > 0
                ? jobTotal
                : Math.Clamp(job.Downloaded, 0, jobTotal);
            if (job.IsActive)
            {
                speed += job.BytesPerSecond;
                active++;
            }
            if (job.IsFinished) finished++;
            if (job.State == FirmwareJobState.Failed) failed++;
        }

        OverallProgress = total > 0 ? Math.Clamp(downloaded * 100d / total, 0, 100) : 0;
        OverallSizeText = total > 0
            ? $"{downloaded / 1024d / 1024d / 1024d:F2} / {total / 1024d / 1024d / 1024d:F2} GB"
            : "—";
        OverallSpeedText = speed > 0 ? $"{speed / 1024d / 1024d:F1} MB/s" : "";
        OverallEtaText = speed > 0 && total > downloaded
            ? FormatOverallEta(TimeSpan.FromSeconds((total - downloaded) / speed))
            : "";
        ActiveCount = active;
        FinishedCount = finished;
        FailedCount = failed;
    }

    private static string FormatOverallEta(TimeSpan remaining) =>
        remaining.TotalHours >= 1
            ? string.Format(Loc.Get("L.Firmware.OverallEtaHours"), (int)remaining.TotalHours, remaining.Minutes)
            : string.Format(Loc.Get("L.Firmware.OverallEtaMinutes"), Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes)));

    // ---------------------------------------------------------------- devices

    public void AddDevices(IEnumerable<FirmwareDevice> devices)
    {
        foreach (var device in devices.Where(candidate => MyDevices.All(d => d.Identifier != candidate.Identifier)))
        {
            _settings.Current.FirmwareSubscriptions.Add(new FirmwareSubscription
            {
                Identifier = device.Identifier,
                DeviceName = device.Name,
                AutoUpdateEnabled = false,
            });
            MyDevices.Add(device);
        }
        _settings.Save();
        SelectedDevice ??= MyDevices.FirstOrDefault();
    }

    private bool CanAddDevice() => SelectedCatalogDevice is not null &&
        MyDevices.All(d => d.Identifier != SelectedCatalogDevice.Identifier);

    [RelayCommand(CanExecute = nameof(CanAddDevice))]
    private void AddDevice()
    {
        if (SelectedCatalogDevice is null) return;
        var device = SelectedCatalogDevice;
        _settings.Current.FirmwareSubscriptions.Add(new FirmwareSubscription
        {
            Identifier = device.Identifier,
            DeviceName = device.Name,
            AutoUpdateEnabled = false,
        });
        _settings.Save();
        MyDevices.Add(device);
        SelectedDevice = device;
        AddDeviceCommand.NotifyCanExecuteChanged();
    }

    private bool CanRemoveDevice() => SelectedDevice is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveDevice))]
    private void RemoveDevice()
    {
        if (SelectedDevice is null) return;
        var device = SelectedDevice;
        var existing = _settings.Current.FirmwareSubscriptions.FirstOrDefault(s => s.Identifier == device.Identifier);
        if (existing is not null)
        {
            // A catalog check may still hold this object while the user removes the device.
            // Mark it disabled before removal so that check cannot start a new transfer.
            existing.AutoUpdateEnabled = false;
            existing.NextCheckUtc = null;
            _settings.Current.FirmwareSubscriptions.Remove(existing);
        }
        _settings.Save();
        MyDevices.Remove(device);
        SelectedDevice = MyDevices.FirstOrDefault();
        AddDeviceCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadDevicesAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        IsLoading = true;
        ErrorText = null;
        try
        {
            _allDevices = (await _catalog.GetDevicesAsync(_loadCts.Token)).ToList();
            RestoreMyDevices();
            ApplyDeviceFilter();
            StatusText = string.Format(Loc.Get("L.Firmware.DevicesLoaded"), _allDevices.Count);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Firmware device catalog failed: {ex.Message}");
            ErrorText = Loc.Get("L.Error.Network");
        }
        finally { IsLoading = false; }
    }

    private async Task LoadFirmwaresAsync(FirmwareDevice? device)
    {
        Firmwares.Clear();
        SelectedFirmware = null;
        if (device is null) return;
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        IsLoading = true;
        try
        {
            var details = await _catalog.GetDeviceAsync(device.Identifier, _loadCts.Token);
            _deviceFirmwares = details.Firmwares.OrderByDescending(f => f.ReleaseDate ?? f.UploadDate).ToList();
            ApplyFirmwareFilter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warn($"Firmware release catalog failed for {device.Identifier}: {ex.Message}");
            ErrorText = Loc.Get("L.Error.Network");
        }
        finally { IsLoading = false; }
    }

    private void RestoreMyDevices()
    {
        var selectedId = SelectedDevice?.Identifier;
        MyDevices.Clear();
        foreach (var subscription in _settings.Current.FirmwareSubscriptions)
        {
            var device = _allDevices.FirstOrDefault(d => d.Identifier == subscription.Identifier) ?? new FirmwareDevice
            {
                Identifier = subscription.Identifier,
                Name = string.IsNullOrWhiteSpace(subscription.DeviceName) ? subscription.Identifier : subscription.DeviceName,
            };
            subscription.DeviceName = device.Name;
            MyDevices.Add(device);
        }
        _settings.Save();
        SelectedDevice = MyDevices.FirstOrDefault(d => d.Identifier == selectedId) ?? MyDevices.FirstOrDefault();
    }

    private void ApplyDeviceFilter()
    {
        var query = SearchText.Trim();
        var filtered = string.IsNullOrEmpty(query) ? Enumerable.Empty<FirmwareDevice>() : _allDevices.Where(d =>
            d.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            d.Identifier.Contains(query, StringComparison.OrdinalIgnoreCase));
        CatalogDevices.Clear();
        foreach (var device in filtered.Take(80)) CatalogDevices.Add(device);
        SelectedCatalogDevice = CatalogDevices.FirstOrDefault();
        AddDeviceCommand.NotifyCanExecuteChanged();
    }

    private void ApplyFirmwareFilter()
    {
        Firmwares.Clear();
        foreach (var firmware in _deviceFirmwares.Where(f => !SignedOnly || f.Signed)) Firmwares.Add(firmware);
        SelectedFirmware = Firmwares.FirstOrDefault();
        EnqueueDownloadCommand.NotifyCanExecuteChanged();
    }

    private void PersistDownloadSettings()
    {
        Directory.CreateDirectory(DestinationFolder);
        _settings.Current.FirmwareFolder = DestinationFolder;
        _settings.Current.FirmwareDownloadThreads = SegmentCount;
        _settings.Save();
    }

    private void RecordCompletedDownload(FirmwareDevice device, FirmwareRelease firmware, string path)
    {
        var subscription = _settings.Current.FirmwareSubscriptions.FirstOrDefault(s => s.Identifier == device.Identifier);
        if (subscription is null) return;
        var oldPath = subscription.LastFilePath;
        subscription.LastBuildId = firmware.BuildId;
        subscription.LastFilePath = path;
        subscription.LastDownloadUtc = DateTimeOffset.UtcNow;
        _settings.Save();
        RefreshAutoUpdateTimestamps();
        if (!string.IsNullOrWhiteSpace(oldPath) && !string.Equals(oldPath, path, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
        {
            try { File.Delete(oldPath); } catch { }
        }
    }

    private void TouchLastCheck(string identifier)
    {
        var subscription = _settings.Current.FirmwareSubscriptions.FirstOrDefault(s => s.Identifier == identifier);
        if (subscription is null) return;
        subscription.LastCheckUtc = DateTimeOffset.UtcNow;
        _settings.Save();
        RefreshAutoUpdateTimestamps();
    }

    private void TouchLastDownload()
    {
        var subscription = SelectedDevice is null
            ? null
            : _settings.Current.FirmwareSubscriptions.FirstOrDefault(s => s.Identifier == SelectedDevice.Identifier);
        if (subscription is null) return;
        subscription.LastDownloadUtc = DateTimeOffset.UtcNow;
        _settings.Save();
        RefreshAutoUpdateTimestamps();
    }

    /// <summary>
    /// Surfaces when the auto-updater last looked and last actually pulled a build, so an
    /// idle schedule is distinguishable from a broken one.
    /// </summary>
    public void RefreshAutoUpdateTimestamps()
    {
        var subscriptions = _settings.Current.FirmwareSubscriptions;
        var selected = SelectedDevice is null
            ? null
            : subscriptions.FirstOrDefault(s => s.Identifier == SelectedDevice.Identifier);
        IEnumerable<FirmwareSubscription> source = selected is null ? subscriptions : new[] { selected };

        AutoUpdateSelected = selected?.AutoUpdateEnabled == true;
        AutoUpdateStatusText = AutoUpdateSelected
            ? Loc.Get("L.Firmware.AutoUpdateEnabled")
            : Loc.Get("L.Firmware.AutoUpdateDisabled");
        LastCheckText = Format(source.Select(s => s.LastCheckUtc).Where(d => d.HasValue).Max());
        LastDownloadText = Format(source.Select(s => s.LastDownloadUtc).Where(d => d.HasValue).Max());
        NextCheckText = Format(selected?.NextCheckUtc);
        LatestAutoVersionText = string.IsNullOrWhiteSpace(selected?.LastFoundVersion)
            ? "—"
            : selected!.LastFoundVersion!;
        AutoUpdateErrorText = string.IsNullOrWhiteSpace(selected?.LastError)
            ? null
            : Loc.Get("L.Firmware.AutoUpdateFailed");

        static string Format(DateTimeOffset? value) =>
            value is null ? "—" : value.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    }
}
