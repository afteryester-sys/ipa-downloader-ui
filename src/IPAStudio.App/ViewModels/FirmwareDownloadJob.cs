using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;

namespace IPAStudio.App.ViewModels;

public enum FirmwareJobState
{
    Queued,
    Running,
    Reconnecting,
    Pausing,
    Paused,
    Done,
    Failed,
}

public sealed partial class FirmwareDownloadJob : ObservableObject
{
    public FirmwareDevice Device { get; }
    public FirmwareRelease Firmware { get; }
    public string DestinationPath { get; }

    internal object SyncRoot { get; } = new();
    internal CancellationTokenSource? Cts { get; set; }
    internal Task? RunnerTask { get; set; }
    internal bool PauseRequested { get; set; }
    internal bool StopRequested { get; set; }

    private readonly Action<FirmwareDownloadJob> _pause;
    private readonly Action<FirmwareDownloadJob> _resume;
    private readonly Action<FirmwareDownloadJob> _stop;

    public FirmwareDownloadJob(
        FirmwareDevice device,
        FirmwareRelease firmware,
        string destinationPath,
        Action<FirmwareDownloadJob> pause,
        Action<FirmwareDownloadJob> resume,
        Action<FirmwareDownloadJob> stop)
    {
        Device = device;
        Firmware = firmware;
        DestinationPath = destinationPath;
        _pause = pause;
        _resume = resume;
        _stop = stop;
        Title = $"{device.Name} · iOS {firmware.Version}";
        Subtitle = $"{firmware.BuildId} · {device.Identifier}";
        StatusText = Loc.Get("L.Firmware.Job.Queued");
    }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private long _downloaded;
    [ObservableProperty] private long _total;
    [ObservableProperty] private double _bytesPerSecond;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(CanPause))]
    [NotifyPropertyChangedFor(nameof(CanResume))]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    private FirmwareJobState _state = FirmwareJobState.Queued;

    public bool IsActive => State is FirmwareJobState.Running
        or FirmwareJobState.Reconnecting
        or FirmwareJobState.Pausing;
    public bool CanPause => State is FirmwareJobState.Running
        or FirmwareJobState.Reconnecting
        or FirmwareJobState.Queued;
    public bool CanResume => State is FirmwareJobState.Paused or FirmwareJobState.Failed;
    public bool IsFinished => State is FirmwareJobState.Done;

    public long ExpectedTotal => Total > 0 ? Total : Firmware.FileSize;

    public string SizeText => ExpectedTotal <= 0
        ? "—"
        : $"{Downloaded / 1024d / 1024d:F0} / {ExpectedTotal / 1024d / 1024d:F0} MB";

    public string SpeedText => BytesPerSecond <= 0 ? "" : $"{BytesPerSecond / 1024d / 1024d:F1} MB/s";

    public string RemainingText
    {
        get
        {
            if (BytesPerSecond <= 0 || ExpectedTotal <= Downloaded) return "";
            var remaining = TimeSpan.FromSeconds((ExpectedTotal - Downloaded) / BytesPerSecond);
            return remaining.TotalHours >= 1
                ? string.Format(Loc.Get("L.Firmware.Job.LeftHours"), (int)remaining.TotalHours, remaining.Minutes)
                : string.Format(Loc.Get("L.Firmware.Job.LeftMinutes"), Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes)));
        }
    }

    partial void OnDownloadedChanged(long value)
    {
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(RemainingText));
    }

    partial void OnTotalChanged(long value)
    {
        OnPropertyChanged(nameof(ExpectedTotal));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(RemainingText));
    }

    partial void OnBytesPerSecondChanged(double value)
    {
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(RemainingText));
    }

    [RelayCommand] private void Pause() => _pause(this);
    [RelayCommand] private void Resume() => _resume(this);
    [RelayCommand] private void Stop() => _stop(this);

    public string FileName => Path.GetFileName(DestinationPath);
}
