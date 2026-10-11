namespace ProjectTabletop.App.Camera;

/// <summary>
/// Keeps at most one latest-state UI refresh queued. The callback reads current
/// state when it runs, so intermediate frames need neither a callback nor a queue.
/// </summary>
internal sealed class CoalescedDispatch
{
    private int _pending;
    public bool IsPending => Volatile.Read(ref _pending) != 0;

    public bool TryPost(Func<Action, bool> dispatch, Action refresh)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(refresh);
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return false;
        bool accepted = false;
        int released = 0;
        void Release()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) Volatile.Write(ref _pending, 0);
        }
        try
        {
            accepted = dispatch(() =>
            {
                try { refresh(); }
                finally { Release(); }
            });
            return accepted;
        }
        finally
        {
            if (!accepted) Release();
        }
    }
}
