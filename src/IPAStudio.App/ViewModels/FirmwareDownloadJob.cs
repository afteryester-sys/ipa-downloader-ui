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
    Paused,
    Done,
    Failed,
}

/// <summary>
/// One firmware download in the queue. Each job owns its own cancellation source so that
/// pausing or stopping a single row never touches the neighbours, which is what the old
/// single shared token got wrong.
/// </summary>
public sealed partial class FirmwareDownloadJob : ObservableObject
{
    public FirmwareDevice Device { get; }
    public FirmwareRelease Firmware { get; }
    public string DestinationPath { get; }

    /// <summary>Set by the queue runner; the job itself only exposes the request to stop.</summary>
    public CancellationTokenSource? Cts { get; set; }

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

    public bool IsActive => State is FirmwareJobState.Running or FirmwareJobState.Reconnecting;
    public bool CanPause => State is FirmwareJobState.Running or FirmwareJobState.Reconnecting or FirmwareJobState.Queued;
    public bool CanResume => State is FirmwareJobState.Paused or FirmwareJobState.Failed;
    public bool IsFinished => State is FirmwareJobState.Done;

    /// <summary>Total is only known after the first HEAD, so fall back to the catalog size.</summary>
    public long ExpectedTotal => Total > 0 ? Total : Firmware.FileSize;

    public string SizeText => ExpectedTotal <= 0
        ? "—"
        : $"{Downloaded / 1024d / 1024d:F0} / {ExpectedTotal / 1024d / 1024d:F0} MB";

    public string SpeedText => BytesPerSecond <= 0 ? "" : $"{BytesPerSecond / 1024d / 1024d:F1} MB/s";

    partial void OnDownloadedChanged(long value)
    {
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(RemainingText));
    }

    partial void OnTotalChanged(long value) => OnPropertyChanged(nameof(SizeText));

    partial void OnBytesPerSecondChanged(double value)
    {
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(RemainingText));
    }

    public string RemainingText
    {
        get
        {
            if (BytesPerSecond <= 0 || ExpectedTotal <= Downloaded) return "";
            var seconds = (ExpectedTotal - Downloaded) / BytesPerSecond;
            return seconds >= 3600
                ? string.Format(Loc.Get("L.Firmware.Job.LeftHours"), seconds / 3600, seconds % 3600 / 60)
                : string.Format(Loc.Get("L.Firmware.Job.LeftMinutes"), Math.Max(1, seconds / 60));
        }
    }

    [RelayCommand] private void Pause() => _pause(this);
    [RelayCommand] private void Resume() => _resume(this);
    [RelayCommand] private void Stop() => _stop(this);

    public string FileName => Path.GetFileName(DestinationPath);
}
