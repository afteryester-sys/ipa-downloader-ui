using CommunityToolkit.Mvvm.ComponentModel;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;

namespace IPAStudio.App.ViewModels;

/// <summary>What a background operation is doing, which also picks its icon and title.</summary>
public enum OperationKind
{
    /// <summary>Downloading apps from the store and installing them onto a device.</summary>
    Install,

    /// <summary>Copying apps from one device to another.</summary>
    Transfer,

    /// <summary>Saving an IPA into a folder, with no device involved.</summary>
    Download,

    /// <summary>Copying photos off a device.</summary>
    Photos,
}

public enum OperationState
{
    Running,
    Done,
    Failed,
    Cancelled,
}

/// <summary>
/// One unit of work the user can send to the background and come back to.
///
/// Lives in the App layer rather than Core on purpose: an operation is a UI concept — it
/// knows which page to reopen and is bound to directly by the operations list — and Core
/// has no MVVM dependency to build observable objects with.
///
/// The work itself is not owned here. An operation carries the cancellation source and the
/// return target, while the actual pipeline stays in <see cref="QueueService"/> or the page
/// viewmodel that started it. That keeps this type safe to hold on to after the work ends.
/// </summary>
public sealed partial class Operation : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();

    public OperationKind Kind { get; }

    /// <summary>
    /// The queue driving this operation, for <see cref="OperationKind.Install"/> and
    /// <see cref="OperationKind.Transfer"/>. Each such operation gets its own instance, so
    /// two runs cannot clear each other's items. Null for operations a page runs itself.
    /// </summary>
    public QueueService? Queue { get; }

    /// <summary>Page to reopen when returning to this operation.</summary>
    public Page ReturnPage { get; }

    /// <summary>
    /// Device the return page needs, when it needs one. Kept so returning to a transfer
    /// lands on the right device rather than whichever one happens to be selected.
    /// </summary>
    public Device? ReturnDevice { get; }

    [ObservableProperty]
    private string _title = "";

    /// <summary>Device names or folder — what distinguishes two operations of the same kind.</summary>
    [ObservableProperty]
    private string _subtitle = "";

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private OperationState _state = OperationState.Running;

    /// <summary>Short status line, e.g. the current stage or the reason it failed.</summary>
    [ObservableProperty]
    private string _detail = "";

    public bool IsRunning => State == OperationState.Running;

    /// <summary>True once the operation can no longer change on its own.</summary>
    public bool IsFinished => State != OperationState.Running;

    public string StateText => State switch
    {
        OperationState.Done => Loc.Get("L.Ops.State.Done"),
        OperationState.Failed => Loc.Get("L.Ops.State.Failed"),
        OperationState.Cancelled => Loc.Get("L.Ops.State.Cancelled"),
        _ => Loc.Get("L.Ops.State.Running"),
    };

    /// <summary>
    /// Cancels the work. Supplied by whoever started it, because only they know how to stop
    /// it — a queue calls <c>Cancel</c>, a page trips its own token source.
    /// </summary>
    private readonly Action? _cancel;

    public Operation(
        OperationKind kind,
        Page returnPage,
        string title,
        string subtitle,
        Device? returnDevice = null,
        QueueService? queue = null,
        Action? cancel = null)
    {
        Kind = kind;
        ReturnPage = returnPage;
        _title = title;
        _subtitle = subtitle;
        ReturnDevice = returnDevice;
        Queue = queue;
        _cancel = cancel;
    }

    public void Cancel()
    {
        if (IsFinished) return;

        try
        {
            _cancel?.Invoke();
        }
        catch (Exception ex)
        {
            // Cancelling is best-effort: a pipeline that already tore itself down can throw
            // here, and that must not stop the operation being marked cancelled.
            AppLog.Info($"operations: cancel of '{Title}' threw ({ex.Message})");
        }

        Finish(OperationState.Cancelled);
    }

    /// <summary>
    /// Marks the operation terminal. Ignored once already finished, so a cancel that races
    /// with a natural completion cannot rewrite the outcome the user already saw.
    /// </summary>
    public void Finish(OperationState state, string? detail = null)
    {
        if (IsFinished) return;

        State = state;
        if (detail is not null) Detail = detail;
        if (state == OperationState.Done) Progress = 100;
    }

    // State drives three computed properties the UI binds to; the generated OnStateChanged
    // hook is where they have to be raised, because ObservableObject cannot infer them.
    partial void OnStateChanged(OperationState value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(StateText));
    }
}
