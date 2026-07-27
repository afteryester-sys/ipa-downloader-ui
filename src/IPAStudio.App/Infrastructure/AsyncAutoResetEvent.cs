namespace IPAStudio.App.Infrastructure;

/// <summary>
/// An awaitable auto-reset event: <see cref="WaitAsync"/> completes once per
/// <see cref="Set"/>, and a Set that happens while nobody is waiting is remembered.
///
/// Used to wake the thumbnail loader when the visible rows change. A plain
/// <see cref="SemaphoreSlim"/> would count every scroll event and make the loader run
/// once per event long after scrolling stopped; here many Sets collapse into a single
/// wake-up, which is what "load whatever is on screen now" wants.
/// </summary>
internal sealed class AsyncAutoResetEvent
{
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _waiter;
    private bool _signalled;

    /// <summary>Releases one waiter, or leaves the event signalled if none is waiting.</summary>
    public void Set()
    {
        TaskCompletionSource<bool>? toRelease = null;

        lock (_gate)
        {
            if (_waiter is not null)
            {
                toRelease = _waiter;
                _waiter = null;
            }
            else
            {
                _signalled = true;
            }
        }

        // Completed outside the lock: continuations may run synchronously here, and
        // holding the lock across them risks a deadlock.
        toRelease?.TrySetResult(true);
    }

    /// <summary>Waits for the next <see cref="Set"/>, consuming a stored signal if present.</summary>
    public Task WaitAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_signalled)
            {
                _signalled = false;
                return Task.CompletedTask;
            }

            // RunContinuationsAsynchronously keeps Set() from running the loader's
            // continuation on the caller's thread — Set() is called from UI scroll
            // handlers, which must not block on background work.
            _waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = _waiter.Task;

            // Only one loader waits on this event, so a single registration is enough.
            return ct.CanBeCanceled
                ? WaitWithCancellationAsync(task, ct)
                : task;
        }
    }

    private static async Task WaitWithCancellationAsync(Task task, CancellationToken ct)
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(static s => ((TaskCompletionSource<bool>)s!).TrySetResult(true), cancelled);

        await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await task.ConfigureAwait(false);
    }
}
