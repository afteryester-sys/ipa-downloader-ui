using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.App.ViewModels;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;

namespace IPAStudio.App.Services;

/// <summary>
/// Owns the list of operations the user can send to the background and return to.
///
/// The two modes are deliberately different code paths rather than the same path with a
/// limit of one:
///
/// - multitasking off: a single operation slot, reused by each new run, exactly mirroring
///   the old behaviour where starting work replaced whatever was there. Nothing new can
///   go wrong, which is what makes the switch a genuine escape hatch.
/// - multitasking on: every run adds an operation, and several run side by side.
/// </summary>
public sealed partial class OperationService : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly IServiceProvider _services;

    /// <summary>
    /// Reads progress off every running queue and republishes it on the operations.
    ///
    /// Polling rather than subscribing to queue events on purpose. The queues raise an event
    /// per progress tick from every parallel install at once, and turning each one into a UI
    /// update is what stopped the window repainting. One slow timer collapses all of that
    /// into a bounded amount of work no matter how many operations run.
    ///
    /// It also has to live here rather than on the queue page: the page detaches when an
    /// operation is minimised, so anything driven from there left the corner circle frozen
    /// for exactly the operations the circle exists to show.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _progressTimer;

    /// <summary>Four updates a second — smooth enough for a progress ring.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Newest first, because that is the one the user just minimised.</summary>
    public ObservableCollection<Operation> Operations { get; } = new();

    [ObservableProperty]
    private double _overallProgress;

    [ObservableProperty]
    private int _runningCount;

    /// <summary>
    /// True when the corner circle should be shown at all.
    ///
    /// Gated on the setting as well as on there being operations: with multitasking off the
    /// single slot still produces an Operation, and showing a circle for work that cannot be
    /// backgrounded would add a control that does nothing.
    /// </summary>
    public bool HasOperations => MultitaskingEnabled && Operations.Count > 0;

    /// <summary>
    /// True while any operation is still working. Replaces the old QueueService.IsRunning
    /// checks: with several queues at once, one queue's flag no longer answers the question.
    /// </summary>
    public bool HasRunning => Operations.Any(o => o.IsRunning);

    public bool MultitaskingEnabled => _settings.Current.MultitaskingEnabled;

    /// <summary>
    /// Re-reads the multitasking setting. Called after saving settings, because the flag lives
    /// in SettingsService and nothing here would otherwise learn that the corner circle should
    /// appear or disappear.
    /// </summary>
    public void NotifyMultitaskingChanged()
    {
        OnPropertyChanged(nameof(MultitaskingEnabled));
        OnPropertyChanged(nameof(HasOperations));
    }

    /// <summary>Raised when an operation asks to be reopened, handled by the shell.</summary>
    public event EventHandler<Operation>? ReturnRequested;

    /// <summary>
    /// Raised when an operation is sent to the background. The shell plays the collapse
    /// animation and navigates away; the work itself is untouched.
    /// </summary>
    public event EventHandler<Operation>? MinimizeRequested;

    public OperationService(SettingsService settings, IServiceProvider services)
    {
        _settings = settings;
        _services = services;

        Operations.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasOperations));
            Recalculate();
        };

        // Background priority: the ring must never compete with painting the window.
        _progressTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = ProgressInterval,
        };
        _progressTimer.Tick += (_, _) => PublishProgress();
        _progressTimer.Start();
    }

    /// <summary>Copies each running queue's progress onto its operation.</summary>
    private void PublishProgress()
    {
        var changed = false;

        foreach (var operation in Operations)
        {
            if (operation.Queue is null || !operation.IsRunning) continue;

            var progress = operation.Queue.OverallProgress;

            // Compared before assigning so an idle queue does not raise a change notification
            // every quarter second for a value nobody moved.
            if (Math.Abs(operation.Progress - progress) < 0.01) continue;

            operation.Progress = progress;
            changed = true;
        }

        if (changed) Recalculate();
    }

    /// <summary>
    /// Registers a new operation, or reuses the single slot when multitasking is off.
    ///
    /// Returns the operation to track progress against. Callers must always use the
    /// returned instance rather than the one they passed in, because in single-slot mode
    /// the passed-in one is discarded.
    /// </summary>
    public Operation Start(Operation operation)
    {
        if (!MultitaskingEnabled)
        {
            // Single-slot mode reproduces the old behaviour, which was per kind rather than
            // global: starting an install replaced the previous install's queue, but never
            // touched a photo export or a download running alongside it. Cancelling those
            // here would break flows that have always worked in parallel.
            foreach (var previous in Operations.Where(o => o.Kind == operation.Kind).ToList())
            {
                previous.Cancel();
                previous.PropertyChanged -= OnOperationPropertyChanged;
                Operations.Remove(previous);
            }
        }

        Operations.Insert(0, operation);
        operation.PropertyChanged += OnOperationPropertyChanged;
        Recalculate();

        AppLog.Info($"operations: started '{operation.Title}' ({operation.Kind}), " +
                    $"multitasking={MultitaskingEnabled}");

        return operation;
    }

    /// <summary>
    /// Creates a queue for an operation. Each operation gets its own instance so two runs
    /// cannot clear each other's items — the single shared QueueService was the reason two
    /// simultaneous runs were impossible before.
    /// </summary>
    public QueueService CreateQueue() =>
        (QueueService)_services.GetService(typeof(QueueService))!;

    /// <summary>
    /// Creates a queue, lets the caller fill it, and registers the resulting operation.
    ///
    /// The build step is a callback rather than something the caller does beforehand,
    /// because the queue has to exist first and only this type knows how to make one.
    /// </summary>
    public Operation StartQueueOperation(
        OperationKind kind,
        Page returnPage,
        string title,
        string subtitle,
        Device? returnDevice,
        Action<QueueService> build)
    {
        var queue = CreateQueue();
        build(queue);

        var operation = new Operation(
            kind, returnPage, title, subtitle,
            returnDevice: returnDevice,
            queue: queue,
            cancel: queue.Cancel);

        return Start(operation);
    }

    public void Remove(Operation operation)
    {
        operation.PropertyChanged -= OnOperationPropertyChanged;
        Operations.Remove(operation);
        Recalculate();
    }

    /// <summary>Drops every finished operation, leaving running ones alone.</summary>
    public void ClearFinished()
    {
        foreach (var op in Operations.Where(o => o.IsFinished).ToList())
            Remove(op);
    }

    public void CancelAll()
    {
        foreach (var op in Operations.Where(o => o.IsRunning).ToList())
            op.Cancel();
    }

    /// <summary>Whether the operations list is showing.</summary>
    [ObservableProperty]
    private bool _isListOpen;

    [RelayCommand]
    private void ToggleList() => IsListOpen = !IsListOpen;

    /// <summary>
    /// Returns to an operation. Closes the list first, so coming back does not leave the
    /// popup hanging over the page the user just asked to see.
    /// </summary>
    [RelayCommand]
    private void Return(Operation operation)
    {
        IsListOpen = false;
        RequestReturn(operation);
    }

    [RelayCommand]
    private void CancelOne(Operation operation) => operation.Cancel();

    [RelayCommand]
    private void ClearFinishedOperations() => ClearFinished();

    /// <summary>Asks the shell to reopen the page this operation belongs to.</summary>
    public void RequestReturn(Operation operation) =>
        ReturnRequested?.Invoke(this, operation);

    /// <summary>Asks the shell to send this operation to the background.</summary>
    public void RequestMinimize(Operation operation) =>
        MinimizeRequested?.Invoke(this, operation);

    /// <summary>Operations that would be interrupted by closing the window.</summary>
    public IReadOnlyList<Operation> Unfinished =>
        Operations.Where(o => o.IsRunning).ToList();

    private void OnOperationPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Operation.Progress) or nameof(Operation.State))
            Recalculate();
    }

    /// <summary>
    /// Recomputes the ring shown in the corner circle: the average across running
    /// operations, so a finished one does not keep the ring pinned at 100.
    /// </summary>
    private void Recalculate()
    {
        var running = Operations.Where(o => o.IsRunning).ToList();

        RunningCount = running.Count;
        OverallProgress = running.Count == 0
            ? 0
            : running.Sum(o => o.Progress) / running.Count;

        OnPropertyChanged(nameof(HasRunning));
        TrimHistory();
    }

    /// <summary>How many finished operations are kept for reference.</summary>
    private const int MaxFinishedKept = 5;

    /// <summary>
    /// Drops the oldest finished operations.
    ///
    /// Needed because a finished operation keeps a queue, its items and a PropertyChanged
    /// subscription alive. Relying on the user pressing "clear finished" would let a long
    /// session accumulate every queue it ever ran.
    /// </summary>
    private bool _trimming;

    private void TrimHistory()
    {
        // Each removal raises CollectionChanged, which calls Recalculate, which lands back
        // here. The guard is what keeps that from recursing.
        if (_trimming) return;

        var finished = Operations.Where(o => !o.IsRunning).ToList();
        if (finished.Count <= MaxFinishedKept) return;

        _trimming = true;
        try
        {
            // Newest are inserted at index 0, so the tail of this list is the oldest.
            foreach (var stale in finished.Skip(MaxFinishedKept))
            {
                stale.PropertyChanged -= OnOperationPropertyChanged;
                Operations.Remove(stale);
            }
        }
        finally
        {
            _trimming = false;
        }
    }
}
