namespace ProjectTabletop.App.Camera;

public sealed record CameraFramePoolDiagnostics(int Capacity, int Available, int Outstanding,
    long AllocatedBytes, long Rentals, long ExhaustedDrops, bool Retired);

/// <summary>Preallocated, bounded capture storage. Exhaustion drops arrivals instead of allocating or waiting.</summary>
public sealed class CameraFramePool : IDisposable
{
    public const int DefaultCapacity = 24;
    // Keep all 24 slots even at the largest supported 4K mode: the recorder's
    // idle prebuffer and retained detector references must leave capture headroom.
    public const long MaximumAllocatedBytes = 768L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Queue<byte[]> _available;
    private long _rentals, _exhaustedDrops;
    private int _outstanding;
    private bool _retired;

    public CameraFramePool(int width, int height, int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Width = width; Height = height; Stride = checked(width * 4);
        int bytes = checked(Stride * height);
        if (bytes > MaximumAllocatedBytes) throw new ArgumentOutOfRangeException(nameof(width));
        Capacity = Math.Min(capacity, (int)(MaximumAllocatedBytes / bytes));
        AllocatedBytes = (long)Capacity * bytes;
        _available = new(Capacity);
        for (int index = 0; index < Capacity; index++)
        {
            var buffer = GC.AllocateUninitializedArray<byte>(bytes);
            // Commit/touch the pages before starting the reader, so the first
            // captured frames do not pay the pool's initial page-fault cost.
            buffer.AsSpan().Clear();
            _available.Enqueue(buffer);
        }
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public int Capacity { get; }
    public long AllocatedBytes { get; }

    public CameraFrameBuffer? TryRent()
    {
        lock (_gate)
        {
            if (_retired) return null;
            if (!_available.TryDequeue(out var buffer)) { _exhaustedDrops++; return null; }
            _rentals++; _outstanding++;
            return new(this, buffer);
        }
    }

    internal void Return(byte[] buffer)
    {
        lock (_gate)
        {
            _outstanding--;
            if (!_retired) _available.Enqueue(buffer);
        }
    }

    public CameraFramePoolDiagnostics GetDiagnostics()
    {
        lock (_gate) return new(Capacity, _available.Count, _outstanding, AllocatedBytes,
            _rentals, _exhaustedDrops, _retired);
    }

    public void Dispose()
    {
        lock (_gate) { _retired = true; _available.Clear(); }
    }
}

/// <summary>Exclusive writable buffer before publication; disposal returns an unpublished buffer.</summary>
public sealed class CameraFrameBuffer : IDisposable
{
    private CameraFramePool? _pool;
    private readonly byte[] _bytes;
    internal CameraFrameBuffer(CameraFramePool pool, byte[] bytes) { _pool = pool; _bytes = bytes; }
    public byte[] Bytes => Volatile.Read(ref _pool) is not null ? _bytes : throw new ObjectDisposedException(nameof(CameraFrameBuffer));

    public CameraFrameLease Publish(DateTimeOffset timestamp, DateTimeOffset? arrivedAt = null)
    {
        var pool = Interlocked.Exchange(ref _pool, null) ?? throw new ObjectDisposedException(nameof(CameraFrameBuffer));
        try { return CameraFrame.FromPool(pool, _bytes, timestamp, arrivedAt); }
        catch { pool.Return(_bytes); throw; }
    }

    public void Dispose() => Interlocked.Exchange(ref _pool, null)?.Return(_bytes);
}
