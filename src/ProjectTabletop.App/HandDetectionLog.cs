using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace ProjectTabletop.App;

/// <summary>
/// Writes immutable detection metadata to local JSONL files without waiting for
/// serialization or disk access on the caller's thread. Never pass camera pixels.
/// </summary>
internal sealed class HandDetectionLog : IAsyncDisposable
{
    private const string FilePrefix = "hand-detections-";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private static readonly byte[] Newline = [(byte)'\n'];
    private readonly string _directory;
    private readonly long _maximumFileBytes;
    private readonly int _retainedFiles;
    private readonly Channel<object> _entries;
    private readonly Task _worker;
    private readonly string _session = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}";
    private FileStream? _stream;
    private int _fileNumber;
    private long _fileBytes;
    private long _queuedRecords;
    private long _writtenRecords;
    private long _droppedRecords;
    private int _completed;
    private string? _currentPath;
    private string? _error;

    public HandDetectionLog(string directory) : this(directory, 16 * 1024 * 1024, 8, 256) { }

    // Small limits allow deterministic verification of rotation and overload.
    internal HandDetectionLog(string directory, long maximumFileBytes, int retainedFiles, int queueCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFileBytes, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedFiles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);
        _directory = directory;
        _maximumFileBytes = maximumFileBytes;
        _retainedFiles = retainedFiles;
        _entries = Channel.CreateBounded<object>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            // TryWrite returns false on overflow instead of silently evicting
            // records, allowing every discarded entry to be counted.
            FullMode = BoundedChannelFullMode.Wait
        });
        _worker = Task.Run(WriteEntriesAsync);
    }

    public HandDetectionLogStatus Status => new(Volatile.Read(ref _currentPath),
        Interlocked.Read(ref _queuedRecords), Interlocked.Read(ref _writtenRecords),
        Interlocked.Read(ref _droppedRecords), Volatile.Read(ref _error));

    public void Record(object immutableEntry)
    {
        if (immutableEntry is null || Volatile.Read(ref _completed) != 0)
        {
            Drop(immutableEntry is null ? "A null log entry was rejected." : "The detection log is closed.");
            return;
        }
        // Increment before publishing: the worker may take the entry at once.
        Interlocked.Increment(ref _queuedRecords);
        if (_entries.Writer.TryWrite(immutableEntry)) return;
        Interlocked.Decrement(ref _queuedRecords);
        Drop("The detection log queue is full or closed.");
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0) _entries.Writer.TryComplete();
        return new ValueTask(_worker);
    }

    private async Task WriteEntriesAsync()
    {
        try
        {
            await foreach (object entry in _entries.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    byte[] json = JsonSerializer.SerializeToUtf8Bytes(entry, entry.GetType(), JsonOptions);
                    long size = json.LongLength + Newline.Length;
                    if (size > _maximumFileBytes)
                    {
                        Drop("A detection log entry exceeded the file size limit.");
                        continue;
                    }
                    await EnsureFileAsync(size).ConfigureAwait(false);
                    await _stream!.WriteAsync(json).ConfigureAwait(false);
                    await _stream.WriteAsync(Newline).ConfigureAwait(false);
                    // Make complete lines available to a live reader immediately.
                    await _stream.FlushAsync().ConfigureAwait(false);
                    _fileBytes += size;
                    Interlocked.Increment(ref _writtenRecords);
                }
                catch (Exception error)
                {
                    Drop($"Detection log write failed: {error.Message}");
                    // A partial write must never prefix the next JSON record.
                    await CloseFileAsync().ConfigureAwait(false);
                }
                finally { Interlocked.Decrement(ref _queuedRecords); }
            }
        }
        catch (Exception error)
        {
            Volatile.Write(ref _error, $"Detection log stopped: {error.Message}");
            Interlocked.Exchange(ref _completed, 1);
            _entries.Writer.TryComplete();
            while (_entries.Reader.TryRead(out _))
            {
                Interlocked.Decrement(ref _queuedRecords);
                Interlocked.Increment(ref _droppedRecords);
            }
        }
        finally { await CloseFileAsync().ConfigureAwait(false); }
    }

    private async Task EnsureFileAsync(long nextEntryBytes)
    {
        if (_stream is not null && _fileBytes + nextEntryBytes <= _maximumFileBytes) return;
        await CloseFileAsync().ConfigureAwait(false);
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, $"{FilePrefix}{_session}-{++_fileNumber:D6}.jsonl");
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        _fileBytes = 0;
        Volatile.Write(ref _currentPath, path);
        try
        {
            string[] files = Directory.GetFiles(_directory, FilePrefix + "*.jsonl")
                .Order(StringComparer.Ordinal).ToArray();
            int toRemove = files.Length - _retainedFiles;
            foreach (string older in files.Where(file => !string.Equals(file, path, StringComparison.OrdinalIgnoreCase)))
            {
                if (toRemove <= 0) break;
                File.Delete(older);
                toRemove--;
            }
        }
        catch (Exception error)
        {
            // Retention trouble must not prevent current detection diagnostics.
            Volatile.Write(ref _error, $"Detection log retention failed: {error.Message}");
        }
    }

    private async Task CloseFileAsync()
    {
        FileStream? stream = _stream;
        _stream = null;
        if (stream is null) return;
        try { await stream.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { Volatile.Write(ref _error, $"Detection log close failed: {error.Message}"); }
    }

    private void Drop(string error)
    {
        Interlocked.Increment(ref _droppedRecords);
        Volatile.Write(ref _error, error);
    }
}

/// <param name="QueuedRecords">Accepted records still pending, including an in-progress write.</param>
/// <param name="WrittenRecords">Complete records flushed during this logger's lifetime.</param>
/// <param name="DroppedRecords">Rejected or failed records, including calls after disposal.</param>
/// <param name="Error">Most recent diagnostic failure; retained after later successful writes.</param>
internal sealed record HandDetectionLogStatus(string? CurrentPath, long QueuedRecords,
    long WrittenRecords, long DroppedRecords, string? Error);
