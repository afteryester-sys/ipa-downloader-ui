using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.App.Services;
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
    private INavigator? _navigator;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _firmwaresCts;
    private System.Threading.Timer? _scheduleTimer;
    private List<FirmwareDevice> _allDevices = new();
    private List<FirmwareRelease> _deviceFirmwares = new();
    private readonly Dictionary<FirmwareDownloadJob, Operation> _jobOperations = new();
    private readonly Dictionary<FirmwareDownloadJob, Task> _runningJobs = new();
    private readonly string _jobsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IPAStudio", "firmware-jobs.json");
    private bool _startupResumeAsked;

    public static int[] AvailableHours { get; } = Enumerable.Range(0, 24).ToArray();

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
        SettingsService settings, OperationService operations)
    {
        _catalog = catalog;
        _downloads = downloads;
        _settings = settings;
        _operations = operations;
        DestinationFolder = settings.Current.FirmwareFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "IPA Studio", "Firmwares");
        SegmentCount = Math.Clamp(settings.Current.FirmwareDownloadThreads, 1, 16);
        AutoCheckIntervalHours = Math.Clamp(settings.Current.FirmwareCheckIntervalHours, 1, 168);
        IsAutoCheckEnabled = settings.Current.FirmwareAutoCheckEnabled;
        VerifyHash = settings.Current.FirmwareVerifyHash;
        IsScheduleEnabled = settings.Current.FirmwareScheduleEnabled;
        ScheduleStartHour = Math.Clamp(settings.Current.FirmwareScheduleStartHour, 0, 23);
        ScheduleEndHour = Math.Clamp(settings.Current.FirmwareScheduleEndHour, 0, 23);
        Jobs.CollectionChanged += OnJobsChanged;
        RestoreMyDevices();
        LoadPersistedJobs();
        RefreshAutoUpdateTimestamps();
        _scheduleTimer = new System.Threading.Timer(_ => EvaluateSchedule(), null, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15));
    }

    [ObservableProperty] private bool _isSingleDownloadMode;
    [ObservableProperty] private bool _isAutoCheckEnabled = true;
    [ObservableProperty] private bool _isScheduleEnabled;
    [ObservableProperty] private int _scheduleStartHour;
    [ObservableProperty] private int _scheduleEndHour = 6;
    [ObservableProperty] private FirmwareDevice? _selectedSingleDevice;
    [ObservableProperty] private string _directUrlInput = "";
    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private int _settingsTab;
    [ObservableProperty] private bool _verifyHash = true;

    public ObservableCollection<FirmwareRelease> SingleFirmwares { get; } = new();
    public FirmwareDownloadJob? ActiveJob => Jobs.FirstOrDefault(j => j.IsActive) ?? Jobs.LastOrDefault();

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
    [ObservableProperty] private bool _autoUpdateSelected;
    [ObservableProperty] private int _autoCheckIntervalHours = 6;

    // Aggregate state for the right-hand overall panel.
    [ObservableProperty] private double _overallProgress;
    [ObservableProperty] private string _overallSizeText = "";
    [ObservableProperty] private string _overallSpeedText = "";
    [ObservableProperty] private int _activeCount;
    [ObservableProperty] private int _finishedCount;
    [ObservableProperty] private string _lastCheckText = "—";
    [ObservableProperty] private string _lastDownloadText = "—";

    public bool HasJobs => Jobs.Count > 0;
    public bool HasActiveJobs => ActiveCount > 0;

    partial void OnActiveCountChanged(int value) => OnPropertyChanged(nameof(HasActiveJobs));

    partial void OnAutoCheckIntervalHoursChanged(int value)
    {
        _settings.Current.FirmwareCheckIntervalHours = Math.Clamp(value, 1, 168);
        _settings.Save();
    }

    partial void OnSearchTextChanged(string value) => ApplyDeviceFilter();

    partial void OnSignedOnlyChanged(bool value)
    {
        ApplyFirmwareFilter();
        if (SelectedSingleDevice is not null)
            _ = LoadSingleFirmwaresAsync(SelectedSingleDevice);
    }

    partial void OnSelectedSingleDeviceChanged(FirmwareDevice? value)
    {
        _ = LoadSingleFirmwaresAsync(value);
    }

    partial void OnVerifyHashChanged(bool value)
    {
        _settings.Current.FirmwareVerifyHash = value;
        _settings.Save();
    }

    partial void OnIsScheduleEnabledChanged(bool value)
    {
        _settings.Current.FirmwareScheduleEnabled = value;
        _settings.Save();
        EvaluateSchedule();
    }

    partial void OnScheduleStartHourChanged(int value)
    {
        _settings.Current.FirmwareScheduleStartHour = Math.Clamp(value, 0, 23);
        _settings.Save();
        EvaluateSchedule();
    }

    partial void OnScheduleEndHourChanged(int value)
    {
        _settings.Current.FirmwareScheduleEndHour = Math.Clamp(value, 0, 23);
        _settings.Save();
        EvaluateSchedule();
    }

    public bool IsCurrentTimeInSchedule()
    {
        if (!IsScheduleEnabled) return true;
        var now = DateTime.Now.Hour;
        if (ScheduleStartHour == ScheduleEndHour) return true;
        if (ScheduleStartHour < ScheduleEndHour)
            return now >= ScheduleStartHour && now < ScheduleEndHour;
        else
            return now >= ScheduleStartHour || now < ScheduleEndHour;
    }

    private void EvaluateSchedule()
    {
        if (!IsScheduleEnabled) return;
        var inSchedule = IsCurrentTimeInSchedule();
        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            if (!inSchedule)
            {
                foreach (var job in Jobs.Where(j => j.IsActive).ToList())
                {
                    job.Cts?.Cancel();
                    job.State = FirmwareJobState.WaitingSchedule;
                    job.StatusText = string.Format(Loc.Get("L.Firmware.Job.Scheduled"), ScheduleStartHour, ScheduleEndHour);
                    job.BytesPerSecond = 0;
                }
            }
            else
            {
                foreach (var job in Jobs.Where(j => j.IsScheduled).ToList())
                {
                    ResumeJob(job);
                }
            }
            RecomputeAggregate();
        });
    }

    partial void OnSelectedFirmwareChanged(FirmwareRelease? value) => EnqueueDownloadCommand.NotifyCanExecuteChanged();

    [RelayCommand] private void SwitchToDevicesMode() => IsSingleDownloadMode = false;
    [RelayCommand] private void SwitchToSingleMode() => IsSingleDownloadMode = true;

    [RelayCommand]
    private void ToggleAutoCheck()
    {
        IsAutoCheckEnabled = !IsAutoCheckEnabled;
        _settings.Current.FirmwareAutoCheckEnabled = IsAutoCheckEnabled;
        _settings.Save();
    }

    [RelayCommand] private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;
    [RelayCommand] private void OpenSettings() => IsSettingsOpen = true;
    [RelayCommand] private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private void SetThreads(object? arg)
    {
        if (arg is string s && int.TryParse(s, out var t))
            SegmentCount = Math.Clamp(t, 1, 16);
        else if (arg is int i)
            SegmentCount = Math.Clamp(i, 1, 16);
        PersistDownloadSettings();
    }

    [RelayCommand]
    private void CleanCache()
    {
        _downloads.CleanupInvalidTemporaryFiles(DestinationFolder, TimeSpan.Zero);
        StatusText = Loc.Get("L.Firmware.Settings.CleanCache");
    }

    [RelayCommand]
    private void ToggleActiveJobPause()
    {
        var job = ActiveJob;
        if (job is null) return;
        if (job.CanPause) PauseJob(job);
        else if (job.CanResume) ResumeJob(job);
    }

    [RelayCommand]
    private void CancelActiveJob()
    {
        var job = ActiveJob;
        if (job is not null) StopJob(job);
    }

    partial void OnSelectedDeviceChanged(FirmwareDevice? value)
    {
        var subscription = value is null
            ? null
            : _settings.Current.FirmwareSubscriptions.FirstOrDefault(s => s.Identifier == value.Identifier);
        AutoUpdateSelected = subscription is not null;
        RemoveDeviceCommand.NotifyCanExecuteChanged();
        EnqueueDownloadCommand.NotifyCanExecuteChanged();
        RefreshAutoUpdateTimestamps();
        _ = LoadFirmwaresAsync(value);
    }

    partial void OnSelectedCatalogDeviceChanged(FirmwareDevice? value) => AddDeviceCommand.NotifyCanExecuteChanged();

    public async void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;
        if (MyDevices.Count == 0) RestoreMyDevices();
        if (Jobs.Count == 0) LoadPersistedJobs();
        if (_allDevices.Count == 0) await LoadDevicesAsync();
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
        _ = RunJobAsync(job);
    }

    [RelayCommand]
    private void DownloadFirmware(FirmwareRelease? release)
    {
        if (release is null) return;
        var device = SelectedDevice ?? new FirmwareDevice
        {
            Name = !string.IsNullOrWhiteSpace(release.Identifier) ? release.Identifier : "Apple Device",
            Identifier = release.Identifier
        };

        PersistDownloadSettings();
        var destination = Path.Combine(DestinationFolder,
            FirmwareDownloadService.BuildFileName(device.Name, release.Version));

        var existing = Jobs.FirstOrDefault(j =>
            string.Equals(j.DestinationPath, destination, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing.CanResume) ResumeJob(existing);
            return;
        }

        var job = new FirmwareDownloadJob(device, release, destination, PauseJob, ResumeJob, StopJob, isSingleDownload: false);
        Jobs.Add(job);
        _ = RunJobAsync(job);
    }

    [RelayCommand]
    private void DownloadSingleFirmware(FirmwareRelease? release)
    {
        if (release is null) return;
        var device = SelectedSingleDevice ?? new FirmwareDevice
        {
            Name = !string.IsNullOrWhiteSpace(release.Identifier) ? release.Identifier : "Apple Device",
            Identifier = release.Identifier
        };

        PersistDownloadSettings();
        var destination = Path.Combine(DestinationFolder,
            FirmwareDownloadService.BuildFileName(device.Name, release.Version));

        var existing = Jobs.FirstOrDefault(j =>
            string.Equals(j.DestinationPath, destination, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing.CanResume) ResumeJob(existing);
            return;
        }

        var job = new FirmwareDownloadJob(device, release, destination, PauseJob, ResumeJob, StopJob, isSingleDownload: true);
        Jobs.Add(job);
        _ = RunJobAsync(job);
    }

    [RelayCommand]
    private void StartDirectUrlDownload()
    {
        var url = DirectUrlInput?.Trim();
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ErrorText = "Некорректная ссылка на IPSW файл.";
            return;
        }

        var fileName = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".ipsw", StringComparison.OrdinalIgnoreCase))
        {
            fileName = "Apple Firmware.ipsw";
        }

        var device = new FirmwareDevice
        {
            Name = "Direct Download",
            Identifier = "direct-ipsw"
        };
        var release = new FirmwareRelease
        {
            Identifier = "direct",
            Version = fileName.Replace(".ipsw", "", StringComparison.OrdinalIgnoreCase),
            BuildId = "Direct URL",
            Url = url,
            Signed = true
        };

        PersistDownloadSettings();
        var destination = Path.Combine(DestinationFolder, fileName);

        var existing = Jobs.FirstOrDefault(j =>
            string.Equals(j.DestinationPath, destination, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing.CanResume) ResumeJob(existing);
            DirectUrlInput = "";
            return;
        }

        var job = new FirmwareDownloadJob(device, release, destination, PauseJob, ResumeJob, StopJob, isSingleDownload: true);
        Jobs.Add(job);
        DirectUrlInput = "";
        _ = RunJobAsync(job);
    }

    [RelayCommand]
    private void PauseAll()
    {
        foreach (var job in Jobs.Where(j => j.CanPause).ToList()) PauseJob(job);
    }

    [RelayCommand]
    private async Task StopAllAsync()
    {
        foreach (var job in Jobs.ToList()) await StopJobCoreAsync(job);
    }

    [RelayCommand]
    private void ResumeAll()
    {
        foreach (var job in Jobs.Where(j => j.CanResume).ToList()) ResumeJob(job);
    }

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var job in Jobs.Where(j => j.IsFinished).ToList()) Jobs.Remove(job);
        RecomputeAggregate();
    }

    private void PauseJob(FirmwareDownloadJob job)
    {
        job.State = FirmwareJobState.Paused;
        job.StatusText = Loc.Get("L.Firmware.Paused");
        job.BytesPerSecond = 0;
        job.Cts?.Cancel();
        RecomputeAggregate();
    }

    private void ResumeJob(FirmwareDownloadJob job)
    {
        if (job.IsActive) return;
        job.ErrorText = null;
        _ = RunJobAsync(job);
    }

    private void StopJob(FirmwareDownloadJob job) => _ = StopJobCoreAsync(job);

    private async Task StopJobCoreAsync(FirmwareDownloadJob job)
    {
        job.Cts?.Cancel();
        // Wait for the runner to release the part files before deleting them, otherwise
        // the delete races the still-open FileStreams and silently leaves garbage behind.
        for (var i = 0; i < 200 && job.IsActive; i++) await Task.Delay(25);
        _downloads.DeleteTemporaryFiles(job.DestinationPath);
        Jobs.Remove(job);
        if (_jobOperations.Remove(job, out var operation)) operation.Finish(OperationState.Cancelled, Loc.Get("L.Firmware.Stopped"));
        StatusText = Loc.Get("L.Firmware.Stopped");
        RecomputeAggregate();
    }

    private async Task RunJobAsync(FirmwareDownloadJob job)
    {
        if (_runningJobs.TryGetValue(job, out var priorTask) && !priorTask.IsCompleted)
        {
            try { await Task.WhenAny(priorTask, Task.Delay(350)); } catch { }
        }

        job.Cts?.Dispose();
        job.Cts = new CancellationTokenSource();

        if (IsScheduleEnabled && !IsCurrentTimeInSchedule())
        {
            job.State = FirmwareJobState.WaitingSchedule;
            job.StatusText = string.Format(Loc.Get("L.Firmware.Job.Scheduled"), ScheduleStartHour, ScheduleEndHour);
            job.BytesPerSecond = 0;
            RecomputeAggregate();
            return;
        }

        job.State = FirmwareJobState.Running;
        job.StatusText = Loc.Get("L.Firmware.Job.Running");
        job.ErrorText = null;
        RecomputeAggregate();

        var tcs = new TaskCompletionSource<bool>();
        _runningJobs[job] = tcs.Task;

        if (!_jobOperations.TryGetValue(job, out var operation))
        {
            operation = _operations.Start(new Operation(OperationKind.Firmware, Page.Firmware,
                Loc.Get("L.Firmware.Operation"), job.Title, cancel: () => PauseJob(job)));
            _jobOperations[job] = operation;
        }

        var reporter = new Progress<FirmwareDownloadProgress>(p =>
        {
            if (job.State != FirmwareJobState.Running) return;
            job.Downloaded = p.Downloaded;
            job.Total = p.Total;
            job.BytesPerSecond = p.BytesPerSecond;
            job.Progress = p.Percent;
            job.StatusText = job.SizeText;
            operation.Progress = p.Percent;
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
                    savedPath = await _downloads.DownloadToFileAsync(job.Firmware, job.DestinationPath,
                        SegmentCount, reporter, job.Cts.Token);
                    break;
                }
                catch (Exception ex) when ((ex is HttpRequestException or IOException or EndOfStreamException)
                                           && attempt < 12 && !job.Cts.IsCancellationRequested)
                {
                    var wait = Math.Min(30, 1 << Math.Min(attempt, 5));
                    job.State = FirmwareJobState.Reconnecting;
                    job.StatusText = string.Format(Loc.Get("L.Firmware.Reconnecting"), wait);
                    RecomputeAggregate();
                    await Task.Delay(TimeSpan.FromSeconds(wait), job.Cts.Token);
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
        catch (Exception ex) when (job.Cts?.IsCancellationRequested == true ||
                                   ex is OperationCanceledException ||
                                   (ex is AggregateException agg && agg.Flatten().InnerExceptions.All(e => e is OperationCanceledException)))
        {
            job.State = FirmwareJobState.Paused;
            job.BytesPerSecond = 0;
            job.StatusText = Loc.Get("L.Firmware.Paused");
            if (_jobOperations.Remove(job, out var op))
            {
                op.Finish(OperationState.Cancelled, job.StatusText);
            }
        }
        catch (Exception ex)
        {
            job.State = FirmwareJobState.Failed;
            job.BytesPerSecond = 0;
            job.ErrorText = ex.Message;
            job.StatusText = Loc.Get("L.Firmware.Failed");
            if (_jobOperations.Remove(job, out var op))
            {
                op.Finish(OperationState.Failed, ex.Message);
            }
        }
        finally
        {
            tcs.TrySetResult(true);
            RecomputeAggregate();
        }
    }

    /// <summary>
    /// Resumes a download whose device or release is no longer selected, using only the
    /// manifest left on disk. This is what the startup prompt drives.
    /// </summary>
    private async Task RunPendingAsync(FirmwarePendingDownload pending)
    {
        var device = new FirmwareDevice { Name = pending.FileName, Identifier = "" };
        var firmware = new FirmwareRelease { Url = pending.Url, Sha1 = pending.Sha1, FileSize = pending.Total };
        var job = new FirmwareDownloadJob(device, firmware, pending.DestinationPath, PauseJob, ResumeJob, StopJob)
        {
            Title = pending.FileName,
            Subtitle = Loc.Get("L.Firmware.Job.Recovered"),
            Total = pending.Total,
            Downloaded = pending.Downloaded,
            Progress = pending.Percent,
            State = FirmwareJobState.Running,
        };
        Jobs.Add(job);
        job.Cts = new CancellationTokenSource();
        var operation = _operations.Start(new Operation(OperationKind.Firmware, Page.Firmware,
            Loc.Get("L.Firmware.Operation"), job.Title, cancel: () => PauseJob(job)));
        _jobOperations[job] = operation;

        var reporter = new Progress<FirmwareDownloadProgress>(p =>
        {
            job.Downloaded = p.Downloaded;
            job.Total = p.Total;
            job.BytesPerSecond = p.BytesPerSecond;
            job.Progress = p.Percent;
            job.StatusText = job.SizeText;
            operation.Progress = p.Percent;
            RecomputeAggregate();
        });

        try
        {
            await _downloads.ResumeAsync(pending, SegmentCount, reporter, job.Cts.Token);
            job.State = FirmwareJobState.Done;
            job.Progress = 100;
            job.StatusText = Loc.Get("L.Firmware.Done");
            operation.Finish(OperationState.Done, job.StatusText);
            TouchLastDownload();
        }
        catch (Exception ex) when (job.Cts?.IsCancellationRequested == true ||
                                   ex is OperationCanceledException ||
                                   (ex is AggregateException agg && agg.Flatten().InnerExceptions.All(e => e is OperationCanceledException)))
        {
            job.State = FirmwareJobState.Paused;
            job.StatusText = Loc.Get("L.Firmware.Paused");
            operation.Finish(OperationState.Cancelled, job.StatusText);
        }
        catch (Exception ex)
        {
            job.State = FirmwareJobState.Failed;
            job.ErrorText = ex.Message;
            job.StatusText = Loc.Get("L.Firmware.Failed");
            operation.Finish(OperationState.Failed, ex.Message);
        }
        finally
        {
            job.BytesPerSecond = 0;
            _jobOperations.Remove(job);
            RecomputeAggregate();
        }
    }

    public void LoadPersistedJobs()
    {
        try
        {
            var loaded = new List<PersistedFirmwareJob>();
            if (File.Exists(_jobsPath))
            {
                var json = File.ReadAllText(_jobsPath);
                var list = System.Text.Json.JsonSerializer.Deserialize<List<PersistedFirmwareJob>>(json);
                if (list is not null) loaded.AddRange(list);
            }

            var pendingOnDisk = _downloads.FindPendingDownloads(DestinationFolder);
            foreach (var p in pendingOnDisk)
            {
                if (loaded.All(l => !string.Equals(l.DestinationPath, p.DestinationPath, StringComparison.OrdinalIgnoreCase)))
                {
                    loaded.Add(new PersistedFirmwareJob
                    {
                        DeviceName = p.FileName,
                        DeviceIdentifier = "",
                        FirmwareVersion = p.FileName,
                        BuildId = "",
                        Url = p.Url,
                        Sha1 = p.Sha1,
                        DestinationPath = p.DestinationPath,
                        IsSingleDownload = true,
                        Downloaded = p.Downloaded,
                        Total = p.Total,
                        State = (int)FirmwareJobState.Paused
                    });
                }
            }

            foreach (var item in loaded)
            {
                if (Jobs.Any(j => string.Equals(j.DestinationPath, item.DestinationPath, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var device = new FirmwareDevice
                {
                    Name = !string.IsNullOrWhiteSpace(item.DeviceName) ? item.DeviceName : "Apple Device",
                    Identifier = item.DeviceIdentifier
                };
                var release = new FirmwareRelease
                {
                    Identifier = item.DeviceIdentifier,
                    Version = item.FirmwareVersion,
                    BuildId = item.BuildId,
                    Url = item.Url,
                    Sha1 = item.Sha1,
                    FileSize = item.Total,
                    Signed = true
                };

                var isDone = File.Exists(item.DestinationPath) && item.Total > 0 && new FileInfo(item.DestinationPath).Length == item.Total;
                var job = new FirmwareDownloadJob(device, release, item.DestinationPath, PauseJob, ResumeJob, StopJob, item.IsSingleDownload)
                {
                    Downloaded = item.Downloaded,
                    Total = item.Total,
                    Progress = item.Total > 0 ? Math.Clamp(item.Downloaded * 100d / item.Total, 0, 100) : 0,
                    State = isDone ? FirmwareJobState.Done : FirmwareJobState.Paused,
                    StatusText = isDone ? Loc.Get("L.Firmware.Done") : Loc.Get("L.Firmware.Paused")
                };

                Jobs.Add(job);
            }
            RecomputeAggregate();
        }
        catch { }
    }

    public void SavePersistedJobs()
    {
        try
        {
            var list = Jobs.Select(j => new PersistedFirmwareJob
            {
                DeviceName = j.Device.Name,
                DeviceIdentifier = j.Device.Identifier,
                FirmwareVersion = j.Firmware.Version,
                BuildId = j.Firmware.BuildId,
                Url = j.Firmware.Url,
                Sha1 = j.Firmware.Sha1,
                DestinationPath = j.DestinationPath,
                IsSingleDownload = j.IsSingleDownload,
                Downloaded = j.Downloaded,
                Total = j.ExpectedTotal,
                State = (int)j.State
            }).ToList();

            var json = System.Text.Json.JsonSerializer.Serialize(list, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(_jobsPath)!);
            File.WriteAllText(_jobsPath, json);
        }
        catch { }
    }

    /// <summary>Offers to continue interrupted downloads, once per app run.</summary>
    public void OfferPendingResume()
    {
        if (_startupResumeAsked) return;
        _startupResumeAsked = true;
        LoadPersistedJobs();
    }

    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (FirmwareDownloadJob job in e.OldItems) job.PropertyChanged -= OnJobPropertyChanged;
        if (e.NewItems is not null)
            foreach (FirmwareDownloadJob job in e.NewItems) job.PropertyChanged += OnJobPropertyChanged;
        OnPropertyChanged(nameof(HasJobs));
        RecomputeAggregate();
        SavePersistedJobs();
    }

    private void OnJobPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FirmwareDownloadJob.State))
        {
            RecomputeAggregate();
            SavePersistedJobs();
        }
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
        foreach (var job in Jobs)
        {
            var jobTotal = job.ExpectedTotal;
            total += jobTotal;
            downloaded += job.IsFinished && jobTotal > 0 ? jobTotal : Math.Min(job.Downloaded, jobTotal);
            if (job.IsActive) { speed += job.BytesPerSecond; active++; }
            if (job.IsFinished) finished++;
        }
        OverallProgress = total > 0 ? Math.Clamp(downloaded * 100d / total, 0, 100) : 0;
        OverallSizeText = total > 0
            ? $"{downloaded / 1024d / 1024d / 1024d:F2} / {total / 1024d / 1024d / 1024d:F2} GB"
            : "—";
        OverallSpeedText = speed > 0 ? $"{speed / 1024d / 1024d:F1} MB/s" : "";
        ActiveCount = active;
        FinishedCount = finished;
        OnPropertyChanged(nameof(ActiveJob));
        ResumeAllCommand.NotifyCanExecuteChanged();
    }

    // ---------------------------------------------------------------- devices

    public void AddDevices(IEnumerable<FirmwareDevice> devices)
    {
        foreach (var device in devices.Where(candidate => MyDevices.All(d => d.Identifier != candidate.Identifier)))
        {
            _settings.Current.FirmwareSubscriptions.Add(new FirmwareSubscription { Identifier = device.Identifier, DeviceName = device.Name });
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
        if (existing is not null) _settings.Current.FirmwareSubscriptions.Remove(existing);
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
            OnPropertyChanged(nameof(AllDevices));
            RestoreMyDevices();
            ApplyDeviceFilter();
            SelectedSingleDevice ??= _allDevices.FirstOrDefault(d => d.Identifier.StartsWith("iPhone16", StringComparison.OrdinalIgnoreCase))
                ?? _allDevices.FirstOrDefault(d => d.Identifier.StartsWith("iPhone15", StringComparison.OrdinalIgnoreCase))
                ?? _allDevices.FirstOrDefault();
            StatusText = string.Format(Loc.Get("L.Firmware.DevicesLoaded"), _allDevices.Count);
        }
        catch (Exception ex) { ErrorText = ex.Message; }
        finally { IsLoading = false; }
    }

    private async Task LoadSingleFirmwaresAsync(FirmwareDevice? device)
    {
        if (device is null)
        {
            SingleFirmwares.Clear();
            return;
        }

        // Fast path: if cached in memory or disk, display immediately
        var cached = _catalog.GetCachedDevice(device.Identifier);
        if (cached is not null && cached.Firmwares.Count > 0)
        {
            SingleFirmwares.Clear();
            var q = SignedOnly ? cached.Firmwares.Where(f => f.Signed) : cached.Firmwares;
            foreach (var fw in q.OrderByDescending(f => f.ReleaseDate ?? f.UploadDate))
                SingleFirmwares.Add(fw);
        }

        try
        {
            var details = await _catalog.GetDeviceAsync(device.Identifier);
            SingleFirmwares.Clear();
            var query = details.Firmwares.AsEnumerable();
            if (SignedOnly) query = query.Where(f => f.Signed);
            foreach (var fw in query.OrderByDescending(f => f.ReleaseDate ?? f.UploadDate))
            {
                SingleFirmwares.Add(fw);
            }
        }
        catch (Exception ex)
        {
            if (SingleFirmwares.Count == 0) ErrorText = ex.Message;
        }
    }

    private async Task LoadFirmwaresAsync(FirmwareDevice? device)
    {
        if (device is null)
        {
            Firmwares.Clear();
            _deviceFirmwares.Clear();
            SelectedFirmware = null;
            return;
        }

        // Fast path: instantly populate from memory/disk cache without waiting
        var cached = _catalog.GetCachedDevice(device.Identifier);
        if (cached is not null && cached.Firmwares.Count > 0)
        {
            _deviceFirmwares = cached.Firmwares.OrderByDescending(f => f.ReleaseDate ?? f.UploadDate).ToList();
            ApplyFirmwareFilter();
        }

        _firmwaresCts?.Cancel();
        _firmwaresCts = new CancellationTokenSource();
        var ct = _firmwaresCts.Token;

        if (_deviceFirmwares.Count == 0) IsLoading = true;
        try
        {
            var details = await _catalog.GetDeviceAsync(device.Identifier, ct);
            _deviceFirmwares = details.Firmwares.OrderByDescending(f => f.ReleaseDate ?? f.UploadDate).ToList();
            ApplyFirmwareFilter();
            TouchLastCheck(device.Identifier);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_deviceFirmwares.Count == 0) ErrorText = ex.Message;
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
        if (subscription is null) { TouchLastDownload(); return; }
        var oldPath = subscription.LastFilePath;
        subscription.LastBuildId = firmware.BuildId;
        subscription.LastFilePath = path;
        subscription.LastDownloadUtc = DateTimeOffset.UtcNow;
        _settings.Save();
        SavePersistedJobs();
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
        var scoped = SelectedDevice is null
            ? subscriptions
            : subscriptions.Where(s => s.Identifier == SelectedDevice.Identifier).ToList();
        var source = scoped.Count > 0 ? scoped : subscriptions;
        LastCheckText = Format(source.Select(s => s.LastCheckUtc).Where(d => d.HasValue).Max());
        LastDownloadText = Format(source.Select(s => s.LastDownloadUtc).Where(d => d.HasValue).Max());

        static string Format(DateTimeOffset? value) =>
            value is null ? "—" : value.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    }
}
