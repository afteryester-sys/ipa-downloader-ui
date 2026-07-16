using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;

namespace IPAStudio.App.ViewModels;

/// <summary>Observable wrapper around a <see cref="QueueItem"/> for the queue page.</summary>
public sealed partial class QueueItemViewModel : ObservableObject
{
    public QueueItem Item { get; }

    [ObservableProperty]
    private QueueStage _stage;

    [ObservableProperty]
    private double _stageProgress;

    [ObservableProperty]
    private string _statusDetail = "";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _speedText = "";

    public string Name => Item.App.Name;
    public string? CachedIconPath => Item.App.CachedIconPath;

    public bool IsActive => Stage is QueueStage.Checking or QueueStage.Licensing
        or QueueStage.Downloading or QueueStage.Installing;

    /// <summary>
    /// Show a moving (indeterminate) bar for stages where no measurable progress exists.
    /// Checking and Licensing are always indeterminate.
    /// Downloading switches to determinate the moment the first byte lands on disk so
    /// the bar starts filling immediately and never gets stuck showing a spinner.
    /// Installing always uses a determinate bar (starts at >=3%).
    /// </summary>
    public bool IsIndeterminate => Stage switch
    {
        QueueStage.Checking or QueueStage.Licensing => true,
        QueueStage.Downloading => Item.DownloadedBytes <= 0,
        _ => false,
    };
    public bool IsDone => Stage == QueueStage.Done;
    public bool IsFailed => Stage is QueueStage.Failed or QueueStage.Cancelled;
    public bool IsPending => Stage == QueueStage.Pending;

    public QueueItemViewModel(QueueItem item)
    {
        Item = item;
        Sync();
    }

    public void Sync()
    {
        Stage = Item.Stage;
        StageProgress = Item.StageProgress;
        StatusDetail = Item.StatusDetail;
        ErrorMessage = Item.ErrorMessage;

        SpeedText = Item.Stage == QueueStage.Downloading && Item.DownloadSpeedBps > 0
            ? FormatSpeed(Item.DownloadSpeedBps)
            : "";

        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsPending));
    }

    private static string FormatSpeed(double bps)
    {
        static string Fmt(double bytes) => bytes switch
        {
            >= 1 << 30 => $"{bytes / (1 << 30):0.0} GB",
            >= 1 << 20 => $"{bytes / (1 << 20):0.0} MB",
            >= 1 << 10 => $"{bytes / (1 << 10):0.0} KB",
            _ => $"{bytes:0} B",
        };
        return $"{Fmt(bps)}/с";
    }
}

/// <summary>
/// Live install queue: overall progress, per-item stage pipeline with animated
/// progress, cancel and per-item retry.
/// </summary>
public sealed partial class QueueViewModel : ObservableObject, IPageAware
{
    private readonly QueueService _queue;
    private INavigator? _navigator;

    public ObservableCollection<QueueItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private double _overallProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackToAppsCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private int _doneCount;

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private string _deviceName = "";

    /// <summary>Set when the session expires mid-queue; the view shows a "sign in again" banner.</summary>
    [ObservableProperty]
    private bool _sessionExpired;

    public bool IsFinished => !IsRunning && Items.Count > 0;

    public QueueViewModel(QueueService queue)
    {
        _queue = queue;
        _queue.ItemChanged += OnItemChanged;
        _queue.QueueCompleted += OnQueueCompleted;
        _queue.SessionExpired += OnSessionExpired;
    }

    public void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;

        Items.Clear();
        foreach (var item in _queue.Items)
            Items.Add(new QueueItemViewModel(item));

        DeviceName = _queue.Items.FirstOrDefault()?.TargetDevice.Name ?? "";
        RecountAndProgress();

        if (!_queue.IsRunning && Items.Any(i => i.IsPending))
        {
            IsRunning = true;
            _ = _queue.RunAsync();
        }
    }

    private void OnItemChanged(object? sender, QueueItem item)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Items.FirstOrDefault(vm => ReferenceEquals(vm.Item, item))?.Sync();
            RecountAndProgress();
        });
    }

    private void OnQueueCompleted(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsRunning = false;
            RecountAndProgress();
        });
    }

    private void OnSessionExpired(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            SessionExpired = true;
            IsRunning = false;
        });
    }

    private void RecountAndProgress()
    {
        OverallProgress = _queue.OverallProgress;
        DoneCount = Items.Count(i => i.IsDone);
        FailedCount = Items.Count(i => i.IsFailed);
    }

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _queue.Cancel();

    [RelayCommand]
    private async Task RetryItemAsync(QueueItemViewModel item)
    {
        IsRunning = true;
        await _queue.RetryAsync(item.Item);
    }

    private bool CanGoBack() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void BackToApps() => _navigator?.GoTo(Page.AppPicker);

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void BackToDevices() => _navigator?.GoTo(Page.Devices);

    [RelayCommand]
    private void SignInAgain()
    {
        // Reset the flag so the banner disappears if the user navigates back and re-enters the queue.
        SessionExpired = false;
        _navigator?.GoTo(Page.Login);
    }
}
