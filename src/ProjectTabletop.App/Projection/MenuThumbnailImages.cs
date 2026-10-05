using Microsoft.Graphics.Canvas;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App.Projection;

/// <summary>Pre-rendered menu artwork, decoded away from the rendering thread.</summary>
internal sealed class MenuThumbnailImages : IDisposable
{
    private static readonly (BoardScreen Screen, string File)[] Assets =
    [
        (BoardScreen.HandTracking, "hand-tracking.png"),
        (BoardScreen.PhotoCopy, "photo-copy.png"),
        (BoardScreen.Blackjack, "blackjack.png"),
        (BoardScreen.Paint, "paint.png"),
        (BoardScreen.Monopoly, "crown-deed.png"),
        (BoardScreen.Globe, "globe.png"),
        (BoardScreen.Slots, "dragon-slots.png"),
        (BoardScreen.Roulette, "roulette.png")
    ];

    private readonly object _gate = new();
    private readonly Task _loading;
    private Dictionary<BoardScreen, CanvasBitmap>? _images;
    private bool _disposed;

    public MenuThumbnailImages(CanvasDevice device)
    {
        Device = device;
        // Even the first LoadAsync call and decoder setup stay out of Draw.
        _loading = Task.Run(LoadAsync);
        _ = _loading.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public CanvasDevice Device { get; }
    public bool IsReady => Volatile.Read(ref _images) is not null;
    public int LoadedCount => Volatile.Read(ref _images)?.Count ?? 0;
    public string? Error => _loading.Exception?.GetBaseException().Message;
    public Task EnsureReadyAsync() => _loading;

    public CanvasBitmap? Image(BoardScreen screen) =>
        Volatile.Read(ref _images)?.GetValueOrDefault(screen);

    private async Task LoadAsync()
    {
        var images = new Dictionary<BoardScreen, CanvasBitmap>();
        try
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "Assets", "MenuPreviews");
            foreach (var (screen, file) in Assets)
            {
                var image = await CanvasBitmap.LoadAsync(Device, Path.Combine(directory, file), 96)
                    .AsTask().ConfigureAwait(false);
                images.Add(screen, image);
            }
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                // Publish the complete set together. A menu never receives
                // individual thumbnails halfway through camera acquisition.
                Volatile.Write(ref _images, images);
            }
        }
        catch
        {
            foreach (var image in images.Values) image.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            var images = Interlocked.Exchange(ref _images, null);
            if (images is not null)
                foreach (var image in images.Values) image.Dispose();
            // A pending decoder owns its partial set until LoadAsync sees
            // disposal and releases it. Its failure is observed above.
        }
    }
}
