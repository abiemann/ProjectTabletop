namespace ProjectTabletop.App;

/// <summary>Serializes reset with publication of results from an earlier asynchronous operation.</summary>
internal sealed class AsyncResultGate
{
    private readonly object _gate = new();
    private long _generation;

    internal long Capture() => Volatile.Read(ref _generation);

    internal void Invalidate(Action? clear = null)
    {
        lock (_gate)
        {
            _generation++;
            clear?.Invoke();
        }
    }

    internal bool TryApply(long generation, Func<bool> apply)
    {
        lock (_gate)
            return generation == _generation && apply();
    }
}
