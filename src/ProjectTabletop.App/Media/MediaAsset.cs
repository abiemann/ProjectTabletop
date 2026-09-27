using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace ProjectTabletop.App.Media;

/// <summary>
/// A local still image or looping Windows-decodable video. Video frames are copied into
/// one reusable Direct3D surface; 4K frames never pass through a managed pixel array.
/// </summary>
public sealed class MediaAsset : IDisposable
{
    private readonly object _gate = new();
    private CanvasBitmap? _still;
    private readonly byte[]? _stillBytes;
    private readonly int _stillWidth;
    private readonly int _stillHeight;
    private readonly DirectXPixelFormat _stillFormat;
    private readonly CanvasAlphaMode _stillAlpha;
    private readonly MediaPlayer? _player;
    private CanvasRenderTarget? _videoSurface;
    private long _availableFrame;
    private long _copiedFrame = -1;
    private long _surfaceCopyCount;
    private bool _disposed;

    private MediaAsset(string path, CanvasBitmap? still, MediaPlayer? player)
    {
        Path = path;
        _still = still;
        if (still is not null)
        {
            // A one-time copy lets a static image recover if Win2D recreates its GPU device.
            _stillBytes = still.GetPixelBytes();
            _stillWidth = (int)still.SizeInPixels.Width;
            _stillHeight = (int)still.SizeInPixels.Height;
            _stillFormat = still.Format;
            _stillAlpha = still.AlphaMode;
        }
        _player = player;
        if (_player is not null)
        {
            _player.VideoFrameAvailable += OnVideoFrameAvailable;
            _player.MediaFailed += OnMediaFailed;
        }
    }

    public string Path { get; }
    public bool IsVideo => _player is not null;
    // Notifications can be coalesced before the next draw, and a device reset can
    // cause another surface copy of the same frame. Neither count measures drops.
    public (long FrameReadyEvents, long SurfaceCopies) PlaybackCounts =>
        (Interlocked.Read(ref _availableFrame), Interlocked.Read(ref _surfaceCopyCount));
    public string? Error { get; private set; }
    public (int Width, int Height)? NaturalSize
    {
        get
        {
            if (_still is not null) return ((int)_still.SizeInPixels.Width, (int)_still.SizeInPixels.Height);
            if (_player is null) return null;
            var session = _player.PlaybackSession;
            return session.NaturalVideoWidth > 0 && session.NaturalVideoHeight > 0
                ? ((int)session.NaturalVideoWidth, (int)session.NaturalVideoHeight)
                : null;
        }
    }

    public static async Task<MediaAsset> OpenAsync(CanvasDevice device, string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Media file not found.", path);
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff")
        {
            var bitmap = await CanvasBitmap.LoadAsync(device, path);
            return new MediaAsset(path, bitmap, null);
        }

        var file = await StorageFile.GetFileFromPathAsync(path);
        var player = new MediaPlayer
        {
            IsVideoFrameServerEnabled = true,
            IsLoopingEnabled = true,
            IsMuted = true,
            Source = MediaSource.CreateFromStorageFile(file)
        };
        var asset = new MediaAsset(path, null, player);
        player.Play();
        return asset;
    }

    public CanvasBitmap? GetFrame(CanvasDevice device)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (_still is not null)
            {
                if (!Equals(_still.Device, device))
                {
                    var replacement = CanvasBitmap.CreateFromBytes(device, _stillBytes!, _stillWidth,
                        _stillHeight, _stillFormat, 96, _stillAlpha);
                    _still.Dispose();
                    _still = replacement;
                }
                return _still;
            }
            if (_player is null) return null;

            var size = NaturalSize;
            if (size is null) return null;
            var (width, height) = size.Value;
            var deviceChanged = _videoSurface is not null && !Equals(_videoSurface.Device, device);
            if (_videoSurface is null || deviceChanged ||
                _videoSurface.SizeInPixels.Width != width || _videoSurface.SizeInPixels.Height != height)
            {
                _videoSurface?.Dispose();
                _videoSurface = new CanvasRenderTarget(device, width, height, 96,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Ignore);
                _copiedFrame = -1;
                if (deviceChanged && Error?.StartsWith("Could not copy decoded video", StringComparison.Ordinal) == true)
                    Error = null;
            }
            if (Error is not null) return null;

            var frame = Volatile.Read(ref _availableFrame);
            if (frame > 0 && frame != _copiedFrame)
            {
                try
                {
                    _player.CopyFrameToVideoSurface(_videoSurface);
                    _copiedFrame = frame;
                    Interlocked.Increment(ref _surfaceCopyCount);
                }
                catch (Exception ex)
                {
                    Error = "Could not copy decoded video to the GPU surface: " + ex.Message;
                    return null;
                }
            }
            return _copiedFrame >= 0 ? _videoSurface : null;
        }
    }

    private void OnVideoFrameAvailable(MediaPlayer sender, object args) => Interlocked.Increment(ref _availableFrame);

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        Error = "Video decoder failed: " + args.ErrorMessage;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_player is not null)
            {
                _player.VideoFrameAvailable -= OnVideoFrameAvailable;
                _player.MediaFailed -= OnMediaFailed;
                _player.Dispose();
            }
            _videoSurface?.Dispose();
            _still?.Dispose();
        }
    }
}
