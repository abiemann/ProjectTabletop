namespace ProjectTabletop.App;

/// <summary>
/// An atomic, process-visible lease represented by the lifetime of a named kernel object.
/// It does not take mutex ownership, so disposal may happen on any async continuation.
/// </summary>
internal sealed class ExclusiveProcessLease : IDisposable
{
    private Mutex? _handle;

    private ExclusiveProcessLease(Mutex handle) => _handle = handle;

    internal static ExclusiveProcessLease? TryAcquire(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var handle = new Mutex(initiallyOwned: false, name, out bool createdNew);
        if (createdNew) return new(handle);
        handle.Dispose();
        return null;
    }

    public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();
}
