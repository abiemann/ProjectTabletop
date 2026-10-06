using Microsoft.Graphics.Canvas;

namespace ProjectTabletop.App.Projection;

// A complete, device-owned set of file-backed artwork. Decoding starts on a
// worker; Draw only borrows already-published images and never waits on I/O.
internal sealed class BitmapAssetSet : IDisposable
{
    private readonly object _gate = new();
    private readonly Task _loading;
    private IReadOnlyDictionary<string, CanvasBitmap>? _images;
    private IReadOnlyDictionary<string, string> _errors = new Dictionary<string, string>();
    private bool _disposed;

    internal BitmapAssetSet(CanvasDevice device, string directory, IReadOnlyList<string> files)
    {
        Device = device;
        string[] requestedFiles = files.ToArray();
        _loading = Task.Run(() => LoadAsync(directory, requestedFiles));
        _ = _loading.ContinueWith(task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    internal CanvasDevice Device { get; }
    internal bool IsLoaded => _loading.IsCompletedSuccessfully;
    internal string? Error => _loading.Exception?.GetBaseException().Message;
    internal IReadOnlyDictionary<string, string> Errors => Volatile.Read(ref _errors);
    internal CanvasBitmap? Image(string file) =>
        Volatile.Read(ref _images)?.GetValueOrDefault(file);
    internal Task EnsureLoadedAsync() => _loading;

    private async Task LoadAsync(string directory, IReadOnlyList<string> files)
    {
        var images = new Dictionary<string, CanvasBitmap>(StringComparer.Ordinal);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (string file in files)
            {
                lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
                try
                {
                    images.Add(file, await CanvasBitmap.LoadAsync(Device,
                        Path.Combine(directory, file), 96).AsTask().ConfigureAwait(false));
                }
                catch (Exception error) when (!Device.IsDeviceLost(error.HResult))
                {
                    errors.Add(file, error.Message);
                }
            }
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                Volatile.Write(ref _errors, errors);
                Volatile.Write(ref _images, images);
                images = null!; // Published images are now owned by Dispose.
            }
        }
        finally
        {
            if (images is not null)
                foreach (var image in images.Values) image.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_images is { } images)
                foreach (var image in images.Values) image.Dispose();
            _images = null;
        }
    }
}
