namespace ProjectTabletop.App.Camera;

/// <summary>
/// Keeps camera delivery independent of scene locks. One frame is processed and
/// at most one newer frame waits; replacement releases the older pixels at once.
/// Each frame carries the state captured at its delivery. A failed frame is passed
/// to <paramref name="report"/>, which must not throw, and never faults the worker.
/// </summary>
internal sealed class LatestFrameWorker<TState>(Action<CameraFrame, TState> process, Action<Exception> report)
{
    private readonly object _gate = new();
    private CameraFrameLease? _pending;
    private TState _pendingState = default!;
    private Task _task = Task.CompletedTask;
    private bool _running, _stopped;

    public bool Enqueue(CameraFrame frame, TState state)
    {
        lock (_gate)
        {
            if (_stopped || frame.TryRetain() is not { } next) return false;
            _pending?.Dispose();
            _pending = next;
            _pendingState = state;
            if (_running) return true;
            _running = true;
            try { _task = Task.Run(Drain); }
            catch
            {
                _running = false;
                ReleasePendingLocked();
                throw;
            }
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate) ReleasePendingLocked();
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            _stopped = true;
            ReleasePendingLocked();
            return _task;
        }
    }

    private void ReleasePendingLocked()
    {
        _pending?.Dispose();
        _pending = null;
        _pendingState = default!;
    }

    private void Drain()
    {
        while (true)
        {
            CameraFrameLease? frame;
            TState state;
            lock (_gate)
            {
                frame = _pending;
                state = _pendingState;
                _pending = null;
                _pendingState = default!;
                if (frame is null) { _running = false; return; }
            }
            // Shutdown awaits this task before releasing the camera, and a newer
            // frame may already wait. One failed frame must stop neither.
            try { using (frame) process(frame.Frame, state); }
            catch (Exception error) { report(error); }
        }
    }
}
