using System.Numerics;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection.GlobeRendering;

/// <summary>Device-owned Earth/sky textures and a native-pixel sphere shader.</summary>
internal sealed class GlobeSurfaceRenderer : IDisposable
{
    private readonly object _gate = new();
    private readonly Task<SurfaceTextures> _loading;
    private readonly PixelShaderEffect<EarthSurfaceShader>[] _effects = [new(), new(), new()];
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
    public int SkySourceWidth => _textures is null ? 0 : (int)_textures.Sky[0].SizeInPixels.Width * 4;
    public int SkySourceHeight => _textures is null ? 0 : (int)_textures.Sky[0].SizeInPixels.Height;
    public int SkyTextureWidth => SkySourceWidth;
    public int SkyTextureHeight => SkySourceHeight;
    public int SkyTextureLoadCount => _textures is null ? 0 : 1;

    public async Task EnsureReadyAsync()
    {
        var textures = await _loading.ConfigureAwait(false);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _textures ??= textures;
        }
    }

    public bool Draw(CanvasDrawingSession drawing, float zoom, float rotationDegrees, double aspect,
        float viewLatitudeDegrees = 0, double elapsedSeconds = 0)
        => DrawFrame(drawing, zoom, rotationDegrees, aspect, viewLatitudeDegrees, elapsedSeconds,
            GetSkyRotationRadians(elapsedSeconds));

    // Keep the double-precision clock bounded before sending it to the GPU.
    // 0.03 degrees per second: one turn in three hours and twenty minutes.
    public static float GetSkyRotationRadians(double elapsedSeconds) =>
        (float)((Math.Max(0, double.IsFinite(elapsedSeconds) ? elapsedSeconds : 0) % 12000)
            * (Math.PI * 2 / 12000));

    internal bool DrawFrame(CanvasDrawingSession drawing, float zoom, float rotationDegrees, double aspect,
        float viewLatitudeDegrees, double elapsedSeconds, float skyRotationRadians)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_loading.IsCompletedSuccessfully) return false;
        _textures ??= _loading.GetAwaiter().GetResult();
        aspect = double.IsFinite(aspect) ? Math.Clamp(aspect, .2, 5) : 1;
        var radius = 335 * Math.Clamp(zoom, .01f, 10);
        var radii = new Float2(aspect >= 1 ? radius / (float)aspect : radius,
            aspect >= 1 ? radius : radius * (float)aspect);
        var transform = drawing.Transform;
        float xScale = Math.Max(.001f, new Vector2(transform.M11, transform.M12).Length());
        float yScale = Math.Max(.001f, new Vector2(transform.M21, transform.M22).Length());
        float edgeWidth = .75f / Math.Max(1, Math.Min(xScale * radii.X, yScale * radii.Y));
        var shootingStar = GlobeShootingStar.Sample(elapsedSeconds, aspect);
        var blend = drawing.Blend;
        try
        {
            for (int layer = 0; layer < _effects.Length; layer++)
            {
                var effect = _effects[layer];
                effect.Sources[0] = _textures.Day;
                effect.Sources[1] = _textures.Clouds;
                for (int i = 0; i < _textures.Sky.Length; i++)
                    effect.Sources[i + 2] = _textures.Sky[i];
                effect.ConstantBuffer = new EarthSurfaceShader(
                    new Float2((float)_textures.Day.Size.Width, (float)_textures.Day.Size.Height),
                    new Float2((float)_textures.Clouds.Size.Width, (float)_textures.Clouds.Size.Height),
                    new Float2(SkyTextureWidth, SkyTextureHeight),
                    new Float2((float)Math.Min(1, aspect), (float)Math.Min(1, 1 / aspect)),
                    layer == 0 ? new Float2(1, 1) : new Float2(1 / xScale, 1 / yScale),
                    new Float2(.25f / xScale, .25f / yScale), layer,
                    skyRotationRadians,
                    new Float2(500, 500), radii, rotationDegrees * (MathF.PI / 180), edgeWidth,
                    viewLatitudeDegrees * (MathF.PI / 180),
                    new Float2(shootingStar.HeadUv.X, shootingStar.HeadUv.Y),
                    new Float2(shootingStar.Direction.X, shootingStar.Direction.Y),
                    new Float2(shootingStar.PhysicalScale.X, shootingStar.PhysicalScale.Y),
                    shootingStar.TailLength, shootingStar.Opacity);
                if (layer == 0)
                {
                    // Preserve the approved moving sky and comet rasterization;
                    // changing its sampling footprint can disturb swipe detection.
                    drawing.Blend = CanvasBlend.SourceOver;
                    drawing.DrawImage(effect, new Rect(0, 0, 1000, 1000), new Rect(0, 0, 1000, 1000));
                }
                else
                {
                    // Evaluate Earth at destination resolution, then add its
                    // atmospheric scattering without blurring the background.
                    drawing.Transform = Matrix3x2.CreateScale(1 / xScale, 1 / yScale) * transform;
                    drawing.Blend = layer == 1 ? CanvasBlend.SourceOver : CanvasBlend.Add;
                    drawing.DrawImage(effect, Vector2.Zero, new Rect(0, 0, 1000 * xScale, 1000 * yScale));
                }
            }
        }
        finally
        {
            drawing.Transform = transform;
            drawing.Blend = blend;
        }
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var effect in _effects) effect.Dispose();
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
        var sky = new CanvasBitmap[4];
        try
        {
            day = await CanvasBitmap.LoadAsync(device, Path.Combine(assets, "earth-day-8192.png"), 96).AsTask().ConfigureAwait(false);
            clouds = await CanvasBitmap.LoadAsync(device, Path.Combine(assets, "earth-clouds-2048.jpg"), 96).AsTask().ConfigureAwait(false);
            // Lossless longitude tiles retain the 16K source while staying
            // within an 8192-pixel bitmap limit and normal repository limits.
            for (int i = 0; i < sky.Length; i++)
                sky[i] = await CanvasBitmap.LoadAsync(device,
                    Path.Combine(assets, $"milky-way-sky-{i}.png"), 96).AsTask().ConfigureAwait(false);
            return new(day, clouds, sky);
        }
        catch
        {
            day?.Dispose(); clouds?.Dispose();
            foreach (var tile in sky) tile?.Dispose();
            throw;
        }
    }

    private sealed class SurfaceTextures(CanvasBitmap day, CanvasBitmap clouds, CanvasBitmap[] sky) : IDisposable
    {
        private int _disposed;
        public CanvasBitmap Day { get; } = day;
        public CanvasBitmap Clouds { get; } = clouds;
        public CanvasBitmap[] Sky { get; } = sky;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Day.Dispose(); Clouds.Dispose();
            foreach (var tile in Sky) tile.Dispose();
        }
    }
}
