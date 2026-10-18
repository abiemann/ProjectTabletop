using System.Numerics;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection.GlobeRendering;

/// <summary>Device-owned Earth/sky textures and a native-pixel sphere shader.</summary>
internal sealed class GlobeSurfaceRenderer : IDisposable
{
    private readonly object _gate = new();
    private readonly Task<CanvasBitmap[]> _skyLoading;
    private readonly PixelShaderEffect<EarthSurfaceShader>[] _effects = [new(), new(), new()];
    private CanvasBitmap[]? _sky;
    private ModelTextures? _textures;
    private Task _modelLoading = Task.CompletedTask;
    private GlobeModel _selectedModel = GlobeModel.Clouds;
    private Exception? _modelError;
    private int _modelEpoch, _modelTextureLoadCount;
    private bool _modelRequested, _modelLoadRunning;
    private bool _disposed;

    public GlobeSurfaceRenderer(CanvasDevice device)
    {
        Device = device;
        _skyLoading = LoadSkyAsync(device);
    }

    public CanvasDevice Device { get; }
    public bool IsReady { get { lock (_gate) return !_disposed && _skyLoading.IsCompletedSuccessfully && _textures is not null; } }
    public bool SkyReady { get { lock (_gate) return !_disposed && _skyLoading.IsCompletedSuccessfully; } }
    public bool IsLoading { get { lock (_gate) return !_disposed && (!_skyLoading.IsCompleted || _modelLoadRunning); } }
    public string? Error { get { lock (_gate) return (_skyLoading.Exception?.GetBaseException() ?? _modelError)?.Message; } }
    public GlobeModel SelectedModel { get { lock (_gate) return _selectedModel; } }
    public GlobeModel? LoadedModel { get { lock (_gate) return _textures?.Model; } }
    public int ModelTextureLoadCount { get { lock (_gate) return _modelTextureLoadCount; } }
    public int ResidentModelCount { get { lock (_gate) return _textures is null ? 0 : 1; } }
    public int ResidentModelTextureCount { get { lock (_gate) return _textures is null ? 0 : _textures.Clouds is null ? 1 : 2; } }
    public int SurfaceWidth { get { lock (_gate) return _textures is null ? 0 : (int)_textures.Surface.SizeInPixels.Width; } }
    public int SurfaceHeight { get { lock (_gate) return _textures is null ? 0 : (int)_textures.Surface.SizeInPixels.Height; } }
    public int CloudWidth { get { lock (_gate) return _textures?.Clouds is null ? 0 : (int)_textures.Clouds.SizeInPixels.Width; } }
    public int CloudHeight { get { lock (_gate) return _textures?.Clouds is null ? 0 : (int)_textures.Clouds.SizeInPixels.Height; } }
    public int SkySourceWidth { get { lock (_gate) return _sky is null ? 0 : (int)_sky[0].SizeInPixels.Width * 4; } }
    public int SkySourceHeight { get { lock (_gate) return _sky is null ? 0 : (int)_sky[0].SizeInPixels.Height; } }
    public int SkyTextureWidth => SkySourceWidth;
    public int SkyTextureHeight => SkySourceHeight;
    public int SkyTextureLoadCount { get { lock (_gate) return _sky is null ? 0 : 1; } }

    public bool RetryFailedModel(GlobeModel model)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_selectedModel != model || _modelError is null || _modelLoadRunning) return false;
            _modelRequested = false;
            RequestModel(model);
            return true;
        }
    }

    public async Task EnsureReadyAsync(GlobeModel model = GlobeModel.Clouds)
    {
        Task loading;
        lock (_gate)
        {
            RequestModel(model);
            loading = _modelLoading;
        }
        await _skyLoading.ConfigureAwait(false);
        while (true)
        {
            await loading.ConfigureAwait(false);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_modelLoadRunning)
                {
                    if (_modelError is not null) throw new InvalidOperationException("Earth imagery could not load.", _modelError);
                    _sky ??= _skyLoading.GetAwaiter().GetResult();
                    return;
                }
                // A newer user choice may have started while this await resumed.
                // Follow the current load; never re-request the obsolete choice.
                loading = _modelLoading;
            }
        }
    }

    public bool Draw(CanvasDrawingSession drawing, float zoom, float rotationDegrees, double aspect,
        float viewLatitudeDegrees = 0, double elapsedSeconds = 0, GlobeModel model = GlobeModel.Clouds)
        => DrawFrame(drawing, zoom, rotationDegrees, aspect, viewLatitudeDegrees, elapsedSeconds,
            GetSkyRotationRadians(elapsedSeconds), model);

    // Keep the double-precision clock bounded before sending it to the GPU.
    // 0.03 degrees per second: one turn in three hours and twenty minutes.
    public static float GetSkyRotationRadians(double elapsedSeconds) =>
        (float)((Math.Max(0, double.IsFinite(elapsedSeconds) ? elapsedSeconds : 0) % 12000)
            * (Math.PI * 2 / 12000));

    internal bool DrawFrame(CanvasDrawingSession drawing, float zoom, float rotationDegrees, double aspect,
        float viewLatitudeDegrees, double elapsedSeconds, float skyRotationRadians, GlobeModel model = GlobeModel.Clouds)
    {
        lock (_gate)
        {
            RequestModel(model);
            if (!_skyLoading.IsCompletedSuccessfully) return false;
            _sky ??= _skyLoading.GetAwaiter().GetResult();
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
                // Keep the camera's star reference moving normally while an
                // Earth choice loads. Only the selected surface is unloaded;
                // there is no full-board black flash during model changes.
                int layers = _textures is null ? 1 : _effects.Length;
                var surface = _textures?.Surface ?? _sky[0];
                for (int layer = 0; layer < layers; layer++)
                {
                    var effect = _effects[layer];
                    effect.Sources[0] = surface;
                    // An unused shader input still needs a valid image. Reuse the
                    // selected surface instead of retaining an invisible cloud map.
                    effect.Sources[1] = _textures?.Clouds ?? surface;
                    for (int i = 0; i < _sky.Length; i++)
                        effect.Sources[i + 2] = _sky[i];
                    effect.ConstantBuffer = new EarthSurfaceShader(
                        new Float2((float)surface.Size.Width, (float)surface.Size.Height),
                        _textures?.Clouds is { } clouds
                            ? new Float2((float)clouds.Size.Width, (float)clouds.Size.Height) : new Float2(1, 1),
                        new Float2(SkyTextureWidth, SkyTextureHeight),
                        new Float2((float)Math.Min(1, aspect), (float)Math.Min(1, 1 / aspect)),
                        layer == 0 ? new Float2(1, 1) : new Float2(1 / xScale, 1 / yScale),
                        new Float2(.25f / xScale, .25f / yScale), layer, (int)_selectedModel,
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
            return _textures is not null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var effect in _effects) effect.Dispose();
            _textures?.Dispose();
            _textures = null;
            if (_sky is not null)
            {
                foreach (var tile in _sky) tile.Dispose();
                _sky = null;
            }
            else
                _ = _skyLoading.ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully)
                        foreach (var tile in task.Result) tile.Dispose();
                    else _ = task.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    // Called under _gate. Old model resources are released before decoding the
    // next model. There is one loader, with the latest choice replacing pending
    // choices rather than allowing parallel uploads or an unbounded task queue.
    private void RequestModel(GlobeModel model)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enum.IsDefined(model)) throw new ArgumentOutOfRangeException(nameof(model));
        if (_modelRequested && _selectedModel == model) return;
        _selectedModel = model;
        _modelRequested = true;
        _modelError = null;
        _modelEpoch++;
        foreach (var effect in _effects)
        {
            effect.Sources[0] = null;
            effect.Sources[1] = null;
        }
        _textures?.Dispose();
        _textures = null;
        if (_modelLoadRunning) return;
        _modelLoadRunning = true;
        _modelLoading = Task.Run(LoadRequestedModelAsync);
    }

    private async Task LoadRequestedModelAsync()
    {
        while (true)
        {
            GlobeModel model;
            int epoch;
            lock (_gate)
            {
                if (_disposed) { _modelLoadRunning = false; return; }
                model = _selectedModel;
                epoch = _modelEpoch;
            }
            ModelTextures? textures = null;
            Exception? error = null;
            try { textures = await LoadModelAsync(model, epoch).ConfigureAwait(false); }
            catch (Exception ex) { error = ex; }
            lock (_gate)
            {
                if (_disposed)
                {
                    textures?.Dispose();
                    _modelLoadRunning = false;
                    return;
                }
                if (epoch != _modelEpoch)
                {
                    textures?.Dispose();
                    continue;
                }
                _textures = textures;
                _modelError = error;
                _modelLoadRunning = false;
                return;
            }
        }
    }

    private async Task<ModelTextures?> LoadModelAsync(GlobeModel model, int epoch)
    {
        string assets = Path.Combine(AppContext.BaseDirectory, "GlobeRendering", "Assets");
        CanvasBitmap? surface = null, clouds = null;
        try
        {
            string file = model == GlobeModel.Night ? "earth-night-8192.png" : "earth-day-8192.png";
            surface = await CanvasBitmap.LoadAsync(Device, Path.Combine(assets, file), 96).AsTask().ConfigureAwait(false);
            lock (_gate)
            {
                _modelTextureLoadCount++;
                // An abandoned selection must not start its optional cloud load.
                if (_disposed || epoch != _modelEpoch) return null;
            }
            if (model == GlobeModel.Clouds)
            {
                clouds = await CanvasBitmap.LoadAsync(Device, Path.Combine(assets, "earth-clouds-2048.jpg"), 96)
                    .AsTask().ConfigureAwait(false);
                lock (_gate) _modelTextureLoadCount++;
            }
            var textures = new ModelTextures(model, surface, clouds);
            surface = clouds = null;
            return textures;
        }
        finally
        {
            surface?.Dispose();
            clouds?.Dispose();
        }
    }

    private static async Task<CanvasBitmap[]> LoadSkyAsync(CanvasDevice device)
    {
        string assets = Path.Combine(AppContext.BaseDirectory, "GlobeRendering", "Assets");
        var sky = new CanvasBitmap[4];
        try
        {
            // Lossless longitude tiles retain the 16K source while staying
            // within an 8192-pixel bitmap limit and normal repository limits.
            for (int i = 0; i < sky.Length; i++)
                sky[i] = await CanvasBitmap.LoadAsync(device,
                    Path.Combine(assets, $"milky-way-sky-{i}.png"), 96).AsTask().ConfigureAwait(false);
            return sky;
        }
        catch
        {
            foreach (var tile in sky) tile?.Dispose();
            throw;
        }
    }

    private sealed class ModelTextures(GlobeModel model, CanvasBitmap surface, CanvasBitmap? clouds) : IDisposable
    {
        private int _disposed;
        public GlobeModel Model { get; } = model;
        public CanvasBitmap Surface { get; } = surface;
        public CanvasBitmap? Clouds { get; } = clouds;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Surface.Dispose(); Clouds?.Dispose();
        }
    }
}
