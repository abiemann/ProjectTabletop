namespace ProjectTabletop.App.Camera;

/// <summary>
/// An owned, tightly packed BGRA8 camera frame. The pixel array must be treated as read-only.
/// Camera frames are separate from the 4K projector render path.
/// Timestamp is host UTC after the CPU copy, shared by inference diagnostics and video
/// recording. It is not a measured hardware exposure timestamp.
/// </summary>
public sealed class CameraFrame(
    int Width,
    int Height,
    int Stride,
    byte[] Bgra,
    DateTimeOffset Timestamp)
{
    private CameraFramePool? _pool;
    private int _references;
    private readonly byte[] _bgra = Bgra;

    public int Width { get; } = Width;
    public int Height { get; } = Height;
    public int Stride { get; } = Stride;
    public byte[] Bgra => _pool is not null && Volatile.Read(ref _references) == 0
        ? throw new ObjectDisposedException(nameof(CameraFrame)) : _bgra;
    public DateTimeOffset Timestamp { get; } = Timestamp;

    /// <summary>Host callback arrival, before conversion/copy. Used for optical
    /// latency and reference alignment; Timestamp keeps its post-copy meaning.</summary>
    public DateTimeOffset? ArrivedAt { get; init; }

    /// <summary>Keep this frame's pixels alive across asynchronous work or retained storage.</summary>
    public CameraFrameLease Retain() => TryRetain() ?? throw new ObjectDisposedException(nameof(CameraFrame));

    public CameraFrameLease? TryRetain()
    {
        if (_pool is null) return new(this);
        int references = Volatile.Read(ref _references);
        while (references > 0)
        {
            int previous = Interlocked.CompareExchange(ref _references, references + 1, references);
            if (previous == references) return new(this);
            references = previous;
        }
        return null;
    }

    internal static CameraFrameLease FromPool(CameraFramePool pool, byte[] pixels,
        DateTimeOffset timestamp, DateTimeOffset? arrivedAt)
    {
        var frame = new CameraFrame(pool.Width, pool.Height, pool.Stride, pixels, timestamp)
        { ArrivedAt = arrivedAt, _pool = pool, _references = 1 };
        return new(frame);
    }

    internal void Release()
    {
        if (_pool is { } pool && Interlocked.Decrement(ref _references) == 0)
            pool.Return(_bgra);
    }
}

/// <summary>One explicit owner of immutable frame pixels. Dispose is idempotent.</summary>
public sealed class CameraFrameLease : IDisposable
{
    private CameraFrame? _frame;
    internal CameraFrameLease(CameraFrame frame) => _frame = frame;
    public CameraFrame Frame => Volatile.Read(ref _frame) ?? throw new ObjectDisposedException(nameof(CameraFrameLease));
    public void Dispose() => Interlocked.Exchange(ref _frame, null)?.Release();
}

/// <summary>Owns a retained frame. Acquire a lease before reading pixels; Frame is for metadata only.</summary>
public sealed class CameraFrameSlot : IDisposable
{
    private readonly object _gate = new();
    private CameraFrameLease? _lease;
    public CameraFrame? Frame { get { lock (_gate) return _lease?.Frame; } }
    public CameraFrameLease? Acquire() { lock (_gate) return _lease?.Frame.Retain(); }
    public void Set(CameraFrame? frame)
    {
        var next = frame?.Retain();
        CameraFrameLease? previous;
        lock (_gate) { previous = _lease; _lease = next; }
        previous?.Dispose();
    }
    public void Clear() => Set(null);
    public void Dispose() => Clear();
}
