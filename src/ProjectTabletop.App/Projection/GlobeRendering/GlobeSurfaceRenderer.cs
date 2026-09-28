using System.Numerics;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection.GlobeRendering;

/// <summary>Device-owned NASA surface textures and a native-pixel sphere shader.</summary>
internal sealed class GlobeSurfaceRenderer : IDisposable
{
    private readonly object _gate = new();
    private readonly Task<SurfaceTextures> _loading;
    private readonly PixelShaderEffect<EarthSurfaceShader> _effect = new();
    private SurfaceTextures? _textures;
    private bool _disposed;

    public GlobeSurfaceRenderer(CanvasDevice device)
    {
        Device = device;
        _loading = LoadTexturesAsync(device);
    }

    public CanvasDevice Device { get; }
    public bool IsReady => _loading.IsCompletedSuccessfully;
    public string? Error => _loading.Exception?.GetBaseException().Message;
    public int SurfaceWidth => _textures is null ? 0 : (int)_textures.Day.SizeInPixels.Width;
    public int SurfaceHeight => _textures is null ? 0 : (int)_textures.Day.SizeInPixels.Height;
    public int CloudWidth => _textures is null ? 0 : (int)_textures.Clouds.SizeInPixels.Width;
    public int CloudHeight => _textures is null ? 0 : (int)_textures.Clouds.SizeInPixels.Height;

    public async Task EnsureReadyAsync()
    {
        var textures = await _loading.ConfigureAwait(false);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _textures ??= textures;
        }
    }

    public bool Draw(CanvasDrawingSession drawing, float zoom, float rotationDegrees, double aspect)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_loading.IsCompletedSuccessfully) return false;
        _textures ??= _loading.GetAwaiter().GetResult();
        aspect = double.IsFinite(aspect) ? Math.Clamp(aspect, .2, 5) : 1;
        var radius = 335 * Math.Clamp(zoom, .01f, 10);
        var radii = new Float2(aspect >= 1 ? radius / (float)aspect : radius,
            aspect >= 1 ? radius : radius * (float)aspect);
        var transform = drawing.Transform;
        float xScale = new Vector2(transform.M11, transform.M12).Length();
        float yScale = new Vector2(transform.M21, transform.M22).Length();
        float edgeWidth = .75f / Math.Max(1, Math.Min(xScale * radii.X, yScale * radii.Y));
        _effect.Sources[0] = _textures.Day;
        _effect.Sources[1] = _textures.Clouds;
        _effect.ConstantBuffer = new EarthSurfaceShader(
            new Float2((float)_textures.Day.Size.Width, (float)_textures.Day.Size.Height),
            new Float2((float)_textures.Clouds.Size.Width, (float)_textures.Clouds.Size.Height),
            new Float2(500, 438), radii, rotationDegrees * (MathF.PI / 180), edgeWidth);
        // A source rectangle bounds the otherwise texture-sized effect. The
        // drawing transform gives Direct2D its real destination sampling rate.
        drawing.DrawImage(_effect, new Rect(0, 0, 1000, 1000), new Rect(0, 0, 1000, 1000));
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _effect.Dispose();
            _textures?.Dispose();
            if (_textures is null)
                _ = _loading.ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully) task.Result.Dispose();
                    else _ = task.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static async Task<SurfaceTextures> LoadTexturesAsync(CanvasDevice device)
    {
        string assets = Path.Combine(AppContext.BaseDirectory, "GlobeRendering", "Assets");
        CanvasBitmap? day = null, clouds = null;
        try
        {
            day = await CanvasBitmap.LoadAsync(device, Path.Combine(assets, "earth-day-8192.png"), 96).AsTask().ConfigureAwait(false);
            clouds = await CanvasBitmap.LoadAsync(device, Path.Combine(assets, "earth-clouds-2048.jpg"), 96).AsTask().ConfigureAwait(false);
            return new(day, clouds);
        }
        catch
        {
            day?.Dispose(); clouds?.Dispose();
            throw;
        }
    }

    private sealed class SurfaceTextures(CanvasBitmap day, CanvasBitmap clouds) : IDisposable
    {
        private int _disposed;
        public CanvasBitmap Day { get; } = day;
        public CanvasBitmap Clouds { get; } = clouds;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Day.Dispose(); Clouds.Dispose();
        }
    }
}
