using System.Diagnostics;

namespace ProjectTabletop.App.Camera;

/// <summary>
/// One independent detector's work slot. Busy arrivals are dropped, never queued.
/// Detector-specific state, reset generations and result freshness stay with the caller.
/// </summary>
internal sealed class FrameWorker
{
    private readonly object _gate = new();
    private Task _workerTask = Task.CompletedTask;
    private int _busy;
    private bool _hasStarted;
    private long _lastStarted;

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    // Shutdown may await background work without deadlocking the UI thread on
    // a publication already dispatched to that same thread.
    public Task WorkerTask { get { lock (_gate) return _workerTask; } }

    // Cheap advisory check before preparing detector context. TryRun checks
    // again atomically when reserving the slot; this does not consume cadence.
    public bool CanRun(long tick, TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);
        if (IsBusy) return false;
        lock (_gate) return ReadyLocked(tick, interval);
    }

    public void ResetCadence()
    {
        lock (_gate) _hasStarted = false;
    }

    public bool TryRun(long tick, TimeSpan interval, Action<FrameWork> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return TryStart(tick, interval, work, static (callback, lease) => Task.Run(() =>
        {
            try { callback(lease); }
            finally { lease.Complete(); }
        }));
    }

    public bool TryRun(long tick, TimeSpan interval, Func<FrameWork, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return TryStart(tick, interval, work, static (callback, lease) => Task.Run(async () =>
        {
            try { await callback(lease).ConfigureAwait(false); }
            finally { lease.Complete(); }
        }));
    }

    private bool TryStart<TWork>(long tick, TimeSpan interval, TWork work, Func<TWork, FrameWork, Task> start)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);
        if (IsBusy) return false;
        lock (_gate)
        {
            if (!ReadyLocked(tick, interval)) return false;
            var lease = new FrameWork(this);
            Volatile.Write(ref _busy, 1);
            try
            {
                _workerTask = start(work, lease);
                _lastStarted = tick;
                _hasStarted = true;
                return true;
            }
            catch
            {
                Volatile.Write(ref _busy, 0);
                throw;
            }
        }
    }

    private bool ReadyLocked(long tick, TimeSpan interval) =>
        !IsBusy && (!_hasStarted || Stopwatch.GetElapsedTime(_lastStarted, tick) >= interval);

    internal void Release()
    {
        lock (_gate) Volatile.Write(ref _busy, 0);
    }
}

/// <summary>Retains the slot through any dispatched result publications.</summary>
internal sealed class FrameWork(FrameWorker owner)
{
    private int _owners = 1;

    /// <summary>
    /// Call during the worker to dispatch a result without awaiting the UI.
    /// The callback must still check the detector's reset generation and freshness.
    /// A false return means the dispatcher rejected the callback.
    /// </summary>
    public bool TryPost(Func<Action, bool> dispatch, Action publication)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(publication);
        Retain();
        bool accepted = false;
        int released = 0;
        void ReleasePublication()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) Complete();
        }
        try
        {
            accepted = dispatch(() =>
            {
                try { publication(); }
                finally { ReleasePublication(); }
            });
            return accepted;
        }
        finally
        {
            if (!accepted) ReleasePublication();
        }
    }

    private void Retain()
    {
        int owners = Volatile.Read(ref _owners);
        do
        {
            if (owners == 0) throw new InvalidOperationException("The frame work has already completed.");
            int observed = Interlocked.CompareExchange(ref _owners, owners + 1, owners);
            if (observed == owners) return;
            owners = observed;
        } while (true);
    }

    internal void Complete()
    {
        if (Interlocked.Decrement(ref _owners) == 0) owner.Release();
    }
}
