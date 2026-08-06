namespace IPAStudio.Core.Services;

/// <summary>
/// Caps how many downloads run at once across the whole application.
///
/// The per-queue <c>Parallel.ForEachAsync</c> only limits one queue, so once several
/// operations can run side by side the slider would stop meaning anything: five operations
/// allowed three downloads each would open fifteen connections, and Apple starts shaping
/// them well before that. This is the single place that count is enforced.
///
/// Deliberately not a <see cref="SemaphoreSlim"/>: the user can move the slider while
/// downloads are in flight, and a semaphore's capacity is fixed once constructed. Here the
/// limit is just an integer compared against the number of holders, so lowering it takes
/// effect as running downloads finish, and raising it releases waiters immediately.
/// </summary>
public sealed class DownloadThrottle
{
    private readonly object _sync = new();

    /// <summary>Waiters in arrival order, so a queue cannot be starved by newer ones.</summary>
    private readonly Queue<TaskCompletionSource<bool>> _waiters = new();

    private int _limit = 3;
    private int _held;

    /// <summary>
    /// Maximum concurrent downloads. Changing this while downloads are running is safe and
    /// is the reason this type exists.
    /// </summary>
    public int Limit
    {
        get { lock (_sync) return _limit; }
        set
        {
            var release = new List<TaskCompletionSource<bool>>();

            lock (_sync)
            {
                var clamped = Math.Clamp(value, 1, 6);
                if (clamped == _limit) return;

                _limit = clamped;

                // Raising the limit must wake the waiters that the old, lower limit was
                // holding back; otherwise they would sit there until an unrelated download
                // happened to finish.
                while (_held < _limit && _waiters.Count > 0)
                {
                    release.Add(_waiters.Dequeue());
                    _held++;
                }
            }

            foreach (var w in release) w.TrySetResult(true);
        }
    }

    /// <summary>Downloads running right now. Diagnostics only.</summary>
    public int Active { get { lock (_sync) return _held; } }

    /// <summary>
    /// Waits for a free slot and returns a handle that frees it on dispose.
    ///
    /// Wrap only the byte transfer in this. Wrapping a whole queue item would put the
    /// install stage under the download limit too, which would serialize installs across
    /// devices and undo the parallel-install work.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        TaskCompletionSource<bool> waiter;

        lock (_sync)
        {
            if (_held < _limit)
            {
                _held++;
                return new Slot(this);
            }

            waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(waiter);
        }

        // Cancellation while queued must not leave a dead waiter holding a slot: the
        // registration below completes the waiter as cancelled, and the catch removes it
        // from the queue so a later Release cannot hand a slot to nobody.
        await using var reg = ct.Register(() => waiter.TrySetCanceled(ct)).ConfigureAwait(false);

        try
        {
            await waiter.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TaskCompletionSource<bool>? next = null;

            lock (_sync)
            {
                // If Release already granted this waiter a slot, the cancellation lost the
                // race and the slot is genuinely ours to hand on to the next in line.
                if (waiter.Task.IsCompletedSuccessfully)
                {
                    next = ReleaseLocked();
                }
                else
                {
                    var kept = new Queue<TaskCompletionSource<bool>>(_waiters.Count);
                    while (_waiters.Count > 0)
                    {
                        var w = _waiters.Dequeue();
                        if (!ReferenceEquals(w, waiter)) kept.Enqueue(w);
                    }
                    while (kept.Count > 0) _waiters.Enqueue(kept.Dequeue());
                }
            }

            // Completed outside the lock, and never dropped: losing this wake-up would
            // leave a slot permanently unusable.
            next?.TrySetResult(true);
            throw;
        }

        return new Slot(this);
    }

    private void Release()
    {
        TaskCompletionSource<bool>? next = null;

        lock (_sync)
        {
            next = ReleaseLocked();
        }

        next?.TrySetResult(true);
    }

    /// <summary>
    /// Gives up one slot and, if the limit allows, hands it straight to the next waiter.
    /// Must be called under <see cref="_sync"/>; returns the waiter to complete outside the
    /// lock, because completing a continuation while holding it invites deadlocks.
    /// </summary>
    private TaskCompletionSource<bool>? ReleaseLocked()
    {
        // A lowered limit can leave more downloads running than allowed. In that case the
        // slot is retired rather than passed on, so the queue drains down to the new limit.
        while (_waiters.Count > 0 && _held <= _limit)
        {
            var w = _waiters.Dequeue();
            if (w.Task.IsCompleted) continue; // cancelled while queued
            return w;                          // slot transfers, _held stays the same
        }

        _held = Math.Max(0, _held - 1);
        return null;
    }

    private sealed class Slot : IDisposable
    {
        private DownloadThrottle? _owner;

        public Slot(DownloadThrottle owner) => _owner = owner;

        public void Dispose()
        {
            // Interlocked so a double dispose cannot release two slots and let the limit
            // drift upward over a long session.
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release();
        }
    }
}
