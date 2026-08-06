using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.App.Services;
using IPAStudio.Core.Localization;
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
    /// Downloading switches to determinate as soon as a real percentage exists, so the
    /// bar starts filling immediately; it stays/returns to indeterminate whenever the
    /// percentage is unknowable — the handshake, the finalizing (repackaging) tail, and
    /// transfers where the total size could never be determined — so the user always
    /// sees motion instead of a bar frozen at 0% or ~99%.
    /// Installing always uses a determinate bar (starts at >=3%).
    /// </summary>
    public bool IsIndeterminate => Stage switch
    {
        QueueStage.Checking or QueueStage.Licensing => true,
        // Connecting = the Apple handshake, before any byte exists. Finalizing =
        // repackaging, where the byte count no longer moves. Both are genuinely
        // unmeasurable, so the bar animates.
        //
        // StageProgress <= 0 is the third unmeasurable case: bytes ARE moving, but
        // no percentage exists because neither the total size nor a reported percent
        // could be obtained (an app delisted from the App Store has no iTunes
        // catalog entry, and ipatool's own progress bar is not always parseable).
        // Without this the bar sat frozen at 0% for the entire transfer, which reads
        // as "stuck" even while the byte counter beside it kept climbing.
        QueueStage.Downloading =>
            Item.IsConnecting || Item.IsFinalizing || Item.StageProgress <= 0,
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

        // Speed is only meaningful while bytes are actually moving. During the
        // handshake and the repackaging tail it is deliberately blank rather than a
        // stale figure left over from the last transferring frame.
        SpeedText = Item.Stage == QueueStage.Downloading
                    && !Item.IsConnecting
                    && !Item.IsFinalizing
                    && Item.DownloadSpeedBps > 0
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
        return $"{Fmt(bps)}{Loc.Get("L.Unit.PerSecond")}";
    }
}

/// <summary>
/// Live install queue: overall progress, per-item stage pipeline with animated
/// progress, cancel and per-item retry.
/// </summary>
public sealed partial class QueueViewModel : ObservableObject, IPageAware
{
    private readonly AuthService _auth;
    private readonly OperationService _operations;
    private INavigator? _navigator;

    /// <summary>
    /// The queue being shown. Attached per operation rather than injected, because a queue
    /// is now created per operation: injecting one would pin this page to a single queue and
    /// make it impossible to look at a second operation.
    /// </summary>
    private QueueService? _queue;

    /// <summary>The operation this page is currently showing, if any.</summary>
    private Operation? _operation;

    public ObservableCollection<QueueItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private double _overallProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    [NotifyPropertyChangedFor(nameof(CanMinimize))]
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

    public QueueViewModel(AuthService auth, OperationService operations)
    {
        _auth = auth;
        _operations = operations;
    }

    /// <summary>
    /// Points the page at an operation's queue. Detaches the previous one first, so the
    /// handlers of an operation left running in the background do not keep writing into
    /// the page while a different operation is on screen.
    /// </summary>
    public void Attach(Operation operation)
    {
        if (ReferenceEquals(_operation, operation)) return;

        Detach();

        _operation = operation;
        _queue = operation.Queue;
        if (_queue is null) return;

        _queue.ItemChanged += OnItemChanged;
        _queue.QueueCompleted += OnQueueCompleted;
        _queue.SessionExpired += OnSessionExpired;

        SessionExpired = false;
        Rebuild();
    }

    /// <summary>
    /// Unsubscribes from the attached queue. Not unsubscribing is the leak that matters
    /// here: a minimised operation runs for minutes, and every event would still fire into
    /// a page showing something else.
    /// </summary>
    public void Detach()
    {
        if (_queue is null) return;

        _queue.ItemChanged -= OnItemChanged;
        _queue.QueueCompleted -= OnQueueCompleted;
        _queue.SessionExpired -= OnSessionExpired;

        _queue = null;
        _operation = null;
    }

    private void Rebuild()
    {
        Items.Clear();
        if (_queue is null) return;

        foreach (var item in _queue.Items)
            Items.Add(new QueueItemViewModel(item));

        DeviceName = _queue.Items.FirstOrDefault()?.TargetDevice.Name ?? "";
        IsRunning = _queue.IsRunning;
        RecountAndProgress();
    }

    public void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;

        if (_queue is null) return;

        Rebuild();

        if (!_queue.IsRunning && Items.Any(i => i.IsPending))
        {
            IsRunning = true;
            _ = RunAsync();
        }
    }

    /// <summary>
    /// Runs the queue and settles the operation afterwards.
    ///
    /// The operation has to be finished here rather than in QueueCompleted, because a
    /// minimised operation may complete while this page is detached and showing something
    /// else — in which case the completion event never reaches the page at all.
    /// </summary>
    private async Task RunAsync()
    {
        var queue = _queue;
        var operation = _operation;
        if (queue is null) return;

        try
        {
            await queue.RunAsync();

            var failed = queue.Items.Count(i => i.Stage is QueueStage.Failed);
            operation?.Finish(
                failed > 0 ? OperationState.Failed : OperationState.Done,
                failed > 0 ? Loc.Get("L.Ops.State.Failed") : Loc.Get("L.Ops.State.Done"));
        }
        catch (OperationCanceledException)
        {
            operation?.Finish(OperationState.Cancelled);
        }
        catch (Exception ex)
        {
            operation?.Finish(OperationState.Failed, ex.Message);
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

            // The banner tells the user to sign in again, so the dead account must go with it.
            // Left in place, the rest of the app still believes it is signed in and the sign-in
            // screen has nothing to do, which is how the banner ended up unactionable.
            _auth.InvalidateSession();
        });
    }

    private void RecountAndProgress()
    {
        if (_queue is null) return;

        OverallProgress = _queue.OverallProgress;
        DoneCount = Items.Count(i => i.IsDone);
        FailedCount = Items.Count(i => i.IsFailed);

        // Feeds the corner circle. Kept in sync here rather than polled, so a minimised
        // operation's ring keeps moving while its page is off screen.
        if (_operation is not null) _operation.Progress = OverallProgress;
    }

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _queue?.Cancel();

    [RelayCommand]
    private async Task RetryItemAsync(QueueItemViewModel item)
    {
        if (_queue is null) return;

        IsRunning = true;
        await _queue.RetryAsync(item.Item);
    }

    /// <summary>Whether the page can be left with the work still going.</summary>
    public bool CanMinimize => _operations.MultitaskingEnabled && IsRunning;

    /// <summary>
    /// Sends this operation to the background: the work keeps running, the page is left,
    /// and the operation stays reachable through the corner circle.
    /// </summary>
    [RelayCommand]
    private void Minimize()
    {
        if (_operation is null) return;

        _operations.RequestMinimize(_operation);
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
