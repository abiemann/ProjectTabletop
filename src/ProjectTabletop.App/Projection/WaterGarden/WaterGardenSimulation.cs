using System.Buffers.Binary;
using System.Numerics;
using ComputeSharp;
using ComputeSharp.D2D1;
using ComputeSharp.D2D1.Descriptors;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace ProjectTabletop.App.Projection.WaterGarden;

internal sealed record WaterGardenDiagnostics(int FieldWidth, int FieldHeight,
    int BedWidth, int BedHeight, long SimulationSteps, double SimulatedSeconds,
    double DroppedSeconds, int DisturbanceCount, double WaveSpeed, string BufferFormat, int DuckCount,
    bool FountainEnabled, long FountainImpactCount);

internal sealed record WaterGardenFieldStatistics(double HeightMass, double MinimumHeight,
    double MaximumHeight, double MaximumSpeed, double TotalEnergy, int NonFiniteValues);

internal sealed record WaterGardenFieldProbe(double Height, double Velocity);

#if DEBUG
internal sealed record WaterGardenDuckState(int Index, float SurfaceX, float SurfaceY,
    float VelocityX, float VelocityY, float Height, float VerticalVelocity,
    float SlopeX, float SlopeY, float Yaw, float YawVelocity, float Scale,
    float DropHeight, float FallVelocity, float SplashAge);
#endif

/// <summary>Signed height-field water with a retained pebble substrate. The wave solver and
/// refraction principles adapt Evan Wallace's MIT WebGL Water; see THIRD_PARTY_NOTICES.md.
/// Pond simulation and visible rendering run on the GPU; a bounded CPU particle
/// solver supplies the three-dimensional rock-face flow. Optical caustics use a bounded
/// local focusing approximation, not the original demo's displaced caustic mesh.</summary>
internal sealed class WaterGardenSimulation : IDisposable
{
    private const double FixedStep = 1.0 / 120.0;
    private const int MaximumStepsPerAdvance = 8;
    private const double DuckLaunchSpacing = 1.05;
    private const float DuckDropStartHeight = .85f;
    private readonly float _aspect;
    private readonly float _waveSpeed;
    private readonly Float2 _fieldSize;
    private readonly Float2 _bedSize;
    private readonly WaterFountainFluid _fountainFluid;
    private CanvasRenderTarget _field;
    private CanvasRenderTarget _next;
    private CanvasRenderTarget _ducks;
    private CanvasRenderTarget _nextDucks;
    private readonly CanvasRenderTarget _bed;
    private readonly PixelShaderEffect<WaterDisturbanceShader> _disturbanceEffect = new();
    private readonly PixelShaderEffect<WaterStepShader> _stepEffect = new();
    private readonly PixelShaderEffect<WaterSurfaceShader> _surfaceEffect = new();
    private readonly PixelShaderEffect<WaterDuckInitializeShader> _duckInitializeEffect = new();
    private readonly PixelShaderEffect<WaterDuckSpawnShader> _duckSpawnEffect = new();
    private readonly PixelShaderEffect<WaterDuckStickContactShader> _duckStickContactEffect = new();
    private readonly PixelShaderEffect<WaterDuckDynamicsShader> _duckDynamicsEffect = new();
    private readonly Transform2DEffect _surfaceField = new();
    private readonly Transform2DEffect _surfaceDucks = new();
    private readonly Transform2DEffect _surfaceStone = new();
    private readonly Transform2DEffect _surfaceWetStone = new();
    private readonly Transform2DEffect _surfaceSand = new();
    private readonly Transform2DEffect _surfaceFountainVolume = new();
    private readonly CanvasBitmap? _ownedStoneTexture;
    private readonly CanvasBitmap? _ownedWetStoneTexture;
    private readonly CanvasBitmap? _ownedSandTexture;
    private readonly CanvasBitmap? _fountainAtlas;
    private readonly Vector2[] _fountainLandingPosition = new Vector2[4];
    private readonly float[] _fountainLandingMomentum = new float[4];
    private readonly Vector2[] _duckLandingPosition = new Vector2[4];
    private readonly float[] _duckLandingMomentum = new float[4];
    private readonly Vector2[] _stickWakePosition = new Vector2[WaterGardenDucks.StickWakeCount];
    private readonly Vector2[] _stickWakeDirection = new Vector2[WaterGardenDucks.StickWakeCount];
    private readonly float[] _stickWakeStrength = new float[WaterGardenDucks.StickWakeCount];
    private readonly Queue<int> _pendingDuckLaunches = new();
    private readonly bool[] _duckLandingObserved = new bool[WaterGardenDucks.MaximumCount];
    private Vector2 _duckSplashPosition;
    private float _duckSplashStrength;
    private double _nextDuckLaunchAt;
    private double _accumulator;
    private double _simulatedSeconds;
    private double _droppedSeconds;
    private long _steps;
    private int _disturbances;
    private bool _ambientEnabled = true;
    private bool _fountainEnabled = true;
    // Direct simulation callers use the settled, calibrated view. The board
    // compositor explicitly starts a new session at the establishing view.
    private float _entranceProgress = 1f;
    private long _fountainImpacts;
    private int _duckCount = WaterGardenDucks.InitialCount;
    private int _activeDuckCount = WaterGardenDucks.InitialCount;
    private long _uploadedFountainRevision = -1;
    private bool _disposed;

    public WaterGardenSimulation(CanvasDevice device, int fieldWidth, int fieldHeight, double aspect)
        : this(device, fieldWidth, fieldHeight, aspect, null)
    {
    }

    // Production supplies the asynchronously preloaded, artwork-owned bitmap.
    // The four-argument verification/generator path owns its file-backed copies.
    public WaterGardenSimulation(CanvasDevice device, int fieldWidth, int fieldHeight, double aspect,
        CanvasBitmap? stoneTexture, CanvasBitmap? wetStoneTexture = null, CanvasBitmap? sandTexture = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (fieldWidth is < 32 or > 2048 || fieldHeight is < 32 or > 2048)
            throw new ArgumentOutOfRangeException(nameof(fieldWidth));
        if (!double.IsFinite(aspect) || aspect is < .1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(aspect));
        if (!device.IsPixelFormatSupported(DirectXPixelFormat.R32G32B32A32Float))
            throw new NotSupportedException("The display adapter does not support floating-point water fields.");
        FieldWidth = fieldWidth;
        FieldHeight = fieldHeight;
        _aspect = (float)aspect;
        _fountainFluid = new WaterFountainFluid(_aspect);
        _fieldSize = new(fieldWidth, fieldHeight);
        // The physical domain is aspect by one. Bound c*dt/dx and c*dt/dy so
        // changing quality or board orientation cannot destabilize the wave stencil.
        _waveSpeed = (float)Math.Min(.12, .66 * Math.Min(aspect / fieldWidth, 1.0 / fieldHeight) / FixedStep);
        int bedWidth = aspect >= 1 ? 2048 : Math.Max(256, (int)Math.Round(2048 * aspect));
        int bedHeight = aspect <= 1 ? 2048 : Math.Max(256, (int)Math.Round(2048 / aspect));
        _bedSize = new(bedWidth, bedHeight);
        // Equal surface-input bounds avoid the default D2D multi-input mapper
        // cropping the 2048-pixel bed to the smaller simulation texture.
        _surfaceField.TransformMatrix = Matrix3x2.CreateScale(bedWidth / (float)fieldWidth,
            bedHeight / (float)fieldHeight);
        _surfaceField.InterpolationMode = CanvasImageInterpolation.Linear;
        _surfaceField.BorderMode = EffectBorderMode.Hard;
        _surfaceField.BufferPrecision = CanvasBufferPrecision.Precision32Float;
        _surfaceDucks.TransformMatrix = Matrix3x2.CreateScale(bedWidth / (float)WaterGardenDucks.MaximumCount,
            bedHeight / (float)WaterGardenDucks.Rows);
        _surfaceDucks.InterpolationMode = CanvasImageInterpolation.NearestNeighbor;
        _surfaceDucks.BorderMode = EffectBorderMode.Hard;
        _surfaceDucks.BufferPrecision = CanvasBufferPrecision.Precision32Float;
        var allocated = new List<CanvasRenderTarget>();
        try
        {
            CanvasBitmap stone = stoneTexture ?? (_ownedStoneTexture = CanvasBitmap.LoadAsync(device,
                Path.Combine(AppContext.BaseDirectory, "Assets", "WaterGarden", "warm-limestone.png"),
                96).AsTask().GetAwaiter().GetResult());
            _surfaceStone.Source = stone;
            _surfaceStone.TransformMatrix = Matrix3x2.CreateScale(bedWidth / (float)stone.Size.Width,
                bedHeight / (float)stone.Size.Height);
            _surfaceStone.InterpolationMode = CanvasImageInterpolation.Linear;
            _surfaceStone.BorderMode = EffectBorderMode.Hard;
            CanvasBitmap wetStone = wetStoneTexture ?? (_ownedWetStoneTexture = CanvasBitmap.LoadAsync(device,
                Path.Combine(AppContext.BaseDirectory, "Assets", "WaterGarden", "wet-slate.png"),
                96).AsTask().GetAwaiter().GetResult());
            _surfaceWetStone.Source = wetStone;
            _surfaceWetStone.TransformMatrix = Matrix3x2.CreateScale(bedWidth / (float)wetStone.Size.Width,
                bedHeight / (float)wetStone.Size.Height);
            _surfaceWetStone.InterpolationMode = CanvasImageInterpolation.Linear;
            _surfaceWetStone.BorderMode = EffectBorderMode.Hard;
            CanvasBitmap sand = sandTexture ?? (_ownedSandTexture = CanvasBitmap.LoadAsync(device,
                Path.Combine(AppContext.BaseDirectory, "Assets", "WaterGarden", "sand-ground.png"),
                96).AsTask().GetAwaiter().GetResult());
            _surfaceSand.Source = sand;
            _surfaceSand.TransformMatrix = Matrix3x2.CreateScale(bedWidth / (float)sand.Size.Width,
                bedHeight / (float)sand.Size.Height);
            _surfaceSand.InterpolationMode = CanvasImageInterpolation.Linear;
            _surfaceSand.BorderMode = EffectBorderMode.Hard;
            _fountainAtlas = CanvasBitmap.CreateFromBytes(device, _fountainFluid.BuildDensityAtlas(),
                WaterFountainFluid.AtlasWidth, WaterFountainFluid.AtlasHeight,
                DirectXPixelFormat.R32G32B32A32Float, 96, CanvasAlphaMode.Ignore);
            _surfaceFountainVolume.Source = _fountainAtlas;
            _surfaceFountainVolume.TransformMatrix = Matrix3x2.CreateScale(
                bedWidth / (float)WaterFountainFluid.AtlasWidth,
                bedHeight / (float)WaterFountainFluid.AtlasHeight);
            _surfaceFountainVolume.InterpolationMode = CanvasImageInterpolation.Linear;
            _surfaceFountainVolume.BorderMode = EffectBorderMode.Hard;
            _surfaceFountainVolume.BufferPrecision = CanvasBufferPrecision.Precision32Float;
            _uploadedFountainRevision = _fountainFluid.Revision;
            CanvasRenderTarget Field()
            {
                var target = new CanvasRenderTarget(device, fieldWidth, fieldHeight, 96,
                    DirectXPixelFormat.R32G32B32A32Float, CanvasAlphaMode.Ignore);
                allocated.Add(target);
                using var drawing = target.CreateDrawingSession();
                drawing.Clear(Color.FromArgb(255, 0, 0, 0));
                return target;
            }
            _field = Field();
            _next = Field();
            CanvasRenderTarget DuckState()
            {
                // Signed motion lives in RGB; alpha is consistently opaque so
                // no compositing convention can alter the numerical state.
                var target = new CanvasRenderTarget(device, WaterGardenDucks.MaximumCount, WaterGardenDucks.Rows,
                    96, DirectXPixelFormat.R32G32B32A32Float, CanvasAlphaMode.Ignore);
                allocated.Add(target);
                return target;
            }
            _ducks = DuckState();
            _nextDucks = DuckState();
            InitializeDucks();
            _bed = new CanvasRenderTarget(device, bedWidth, bedHeight, 96);
            allocated.Add(_bed);
            using (var bedEffect = new PixelShaderEffect<PebbleBedShader>())
            using (var drawing = _bed.CreateDrawingSession())
            {
                bedEffect.ConstantBuffer = new PebbleBedShader(_bedSize, _aspect);
                drawing.DrawImage(bedEffect, Vector2.Zero, new Rect(0, 0, bedWidth, bedHeight),
                    1, CanvasImageInterpolation.Linear, CanvasComposite.Copy);
            }
            BindSurface();
            // Realize the surface while constructor failure can still release every target.
            using var probe = new CanvasRenderTarget(device, 8, 8, 96);
            using var probeDrawing = probe.CreateDrawingSession();
            probeDrawing.DrawImage(_surfaceEffect, Vector2.Zero, new Rect(0, 0, 8, 8));
        }
        catch
        {
            foreach (var target in allocated) target.Dispose();
            DisposeEffects();
            _ownedStoneTexture?.Dispose();
            _ownedWetStoneTexture?.Dispose();
            _ownedSandTexture?.Dispose();
            _fountainAtlas?.Dispose();
            throw;
        }
    }

    public int FieldWidth { get; }
    public int FieldHeight { get; }
    public long Revision { get; private set; }
    public ICanvasImage Output => _surfaceEffect;

    public void SetEntranceProgress(float progress)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!float.IsFinite(progress)) throw new ArgumentOutOfRangeException(nameof(progress));
        progress = Math.Clamp(progress, 0, 1);
        if (MathF.Abs(progress - _entranceProgress) < 1e-5f) return;
        _entranceProgress = progress;
        Revision++;
        BindSurface();
    }

    public WaterGardenDiagnostics GetDiagnostics() => new(FieldWidth, FieldHeight,
        (int)_bedSize.X, (int)_bedSize.Y, _steps, _simulatedSeconds, _droppedSeconds,
        _disturbances, _waveSpeed, "RGBA32Float (height, vertical velocity, reserved, opaque)", _duckCount,
        _fountainEnabled, _fountainImpacts);

    public WaterFountainFluidDiagnostics GetFountainDiagnostics() => _fountainFluid.GetDiagnostics();

    /// <summary>Queues a duck to fall into the screen centre without resetting the pond.</summary>
    /// <returns>False when the bounded duck pool is full.</returns>
    public bool AddDuck()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_duckCount >= WaterGardenDucks.MaximumCount) return false;
        _pendingDuckLaunches.Enqueue(_duckCount);
        _duckCount++;
        TryLaunchDuck();
        Revision++;
        BindSurface();
        return true;
    }

    private void TryLaunchDuck()
    {
        if (_pendingDuckLaunches.Count == 0 || _simulatedSeconds + 1e-9 < _nextDuckLaunchAt) return;
        int index = _pendingDuckLaunches.Dequeue();
        if (index != _activeDuckCount)
            throw new InvalidOperationException("Water Garden duck launch order was lost.");
        Vector2 landing = WaterGardenView.ScreenToSurface(new Vector2(.5f, .5f));
        float yaw = index * .71f - 7.1f;
        float scale = (.021f + (index * 3 % 5) * .001f) * MathF.Min(_aspect, 1);
        _duckSpawnEffect.ConstantBuffer = new WaterDuckSpawnShader(index,
            new Float2(landing.X, landing.Y), yaw, scale, DuckDropStartHeight);
        Run(_duckSpawnEffect, _nextDucks, _ducks);
        (_ducks, _nextDucks) = (_nextDucks, _ducks);
        _activeDuckCount++;
        _nextDuckLaunchAt = _simulatedSeconds + DuckLaunchSpacing;
        Revision++;
    }

    private void ObserveDuckLandings()
    {
        bool awaitingLanding = false;
        for (int index = WaterGardenDucks.InitialCount; index < _activeDuckCount; index++)
            awaitingLanding |= !_duckLandingObserved[index];
        if (!awaitingLanding) return;

        // A new duck is rare. Read only the tiny 20x5 state while a drop is in
        // flight; the normal wave and duck rendering loops stay entirely on GPU.
        byte[] state = _ducks.GetPixelBytes();
        for (int index = WaterGardenDucks.InitialCount; index < _activeDuckCount; index++)
        {
            if (_duckLandingObserved[index]) continue;
            int drop = (WaterGardenDucks.MaximumCount * 4 + index) * 16;
            if (BinaryPrimitives.ReadSingleLittleEndian(state.AsSpan(drop + 8, 4)) < 0) continue;
            int positionOffset = index * 16;
            Vector2 position = new(
                BinaryPrimitives.ReadSingleLittleEndian(state.AsSpan(positionOffset, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(state.AsSpan(positionOffset + 4, 4)));
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)) continue;
            _duckLandingObserved[index] = true;
            // The zero-volume depression/rim feeds the existing height-field;
            // its ring then propagates at the pond's physical wave speed.
            _disturbanceEffect.ConstantBuffer = new WaterDisturbanceShader(_fieldSize, _aspect,
                new Float2(position.X, position.Y), .026f, .011f);
            Run(_disturbanceEffect, _next, _field);
            (_field, _next) = (_next, _field);
            _disturbances++;
            _duckSplashPosition = position;
            _duckSplashStrength = .22f;
            Revision++;
        }
    }

    public void SetFountainEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_fountainEnabled == enabled) return;
        _fountainEnabled = enabled;
        Revision++;
        BindSurface();
    }

    public void AddDisturbance(Vector2 center, float radius, float strength)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) ||
            !float.IsFinite(radius) || !float.IsFinite(strength))
            throw new ArgumentException("Water disturbance parameters must be finite.");
        if (strength == 0) return;
        _disturbanceEffect.ConstantBuffer = new WaterDisturbanceShader(_fieldSize, _aspect,
            new Float2(Math.Clamp(center.X, 0, 1), Math.Clamp(center.Y, 0, 1)),
            Math.Clamp(radius, .006f, .15f), Math.Clamp(strength, -.016f, .016f));
        Run(_disturbanceEffect, _next, _field);
        (_field, _next) = (_next, _field);
        _disturbances++;
        Revision++;
        BindSurface();
    }

    /// <summary>
    /// Transfers a confirmed moving stick-tip stroke to nearby floating hulls.
    /// Strength is a bounded 0..1 measure of stroke travel supplied by the
    /// calibrated input path. A stationary observation or a reacquisition does
    /// not call this method, and the wake dissipates when movement stops.
    /// </summary>
    public void AddStickStroke(Vector2 previous, Vector2 current, float strength)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!float.IsFinite(previous.X) || !float.IsFinite(previous.Y) ||
            !float.IsFinite(current.X) || !float.IsFinite(current.Y) ||
            !float.IsFinite(strength))
            throw new ArgumentException("Stick stroke parameters must be finite.");
        Vector2 delta = (current - previous) * new Vector2(_aspect, 1);
        float distance = delta.Length();
        if (distance < .0035f || distance > .18f || strength <= 0) return;
        _duckStickContactEffect.ConstantBuffer = new WaterDuckStickContactShader(_aspect,
            _activeDuckCount, new Float2(previous.X, previous.Y), new Float2(current.X, current.Y),
            Math.Clamp(strength, 0, 1));
        Run(_duckStickContactEffect, _nextDucks, _ducks);
        (_ducks, _nextDucks) = (_nextDucks, _ducks);
        Revision++;
        BindSurface();
        Vector2 midpoint = (previous + current) * .5f;
        Vector2 direction = delta / distance;
        int slot = -1, weakest = 0;
        float weakestStrength = float.MaxValue;
        for (int index = 0; index < _stickWakeStrength.Length; index++)
        {
            float existing = _stickWakeStrength[index];
            if (existing < weakestStrength) { weakest = index; weakestStrength = existing; }
            // Nearby, similarly directed strokes share a wake instead of
            // allowing repeated samples of one physical sweep to pile up.
            Vector2 separation = (midpoint - _stickWakePosition[index]) * new Vector2(_aspect, 1);
            if (existing > .001f && separation.LengthSquared() < .022f * .022f &&
                Vector2.Dot(direction, _stickWakeDirection[index]) > .5f)
            {
                slot = index;
                break;
            }
        }
        bool merged = slot >= 0;
        if (!merged) slot = weakest;
        float added = .075f + .085f * Math.Clamp(strength, 0, 1);
        if (merged)
        {
            float previousStrength = _stickWakeStrength[slot];
            float combined = previousStrength + added;
            _stickWakePosition[slot] = (_stickWakePosition[slot] * previousStrength + midpoint * added) / combined;
            Vector2 blended = _stickWakeDirection[slot] * previousStrength + direction * added;
            _stickWakeDirection[slot] = Vector2.Normalize(blended);
            _stickWakeStrength[slot] = Math.Min(.18f, previousStrength * .65f + added);
        }
        else
        {
            _stickWakePosition[slot] = midpoint;
            _stickWakeDirection[slot] = direction;
            _stickWakeStrength[slot] = added;
        }
    }

    public void Advance(double seconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        double accepted = Math.Min(seconds, FixedStep * MaximumStepsPerAdvance);
        _droppedSeconds += seconds - accepted;
        _accumulator += accepted;
        int steps = Math.Min(MaximumStepsPerAdvance, (int)Math.Floor((_accumulator + 1e-9) / FixedStep));
        for (int i = 0; i < steps; i++)
        {
            _duckSplashStrength *= MathF.Exp(-5f * (float)FixedStep);
            float currentRetention = MathF.Exp(-1.25f * (float)FixedStep);
            for (int bin = 0; bin < _duckLandingMomentum.Length; bin++)
                _duckLandingMomentum[bin] *= currentRetention;
            float wakeRetention = MathF.Exp(-5f * (float)FixedStep);
            for (int wake = 0; wake < _stickWakeStrength.Length; wake++)
                _stickWakeStrength[wake] *= wakeRetention;
            _fountainFluid.Advance(FixedStep, _fountainEnabled);
            foreach (WaterFountainImpact impact in _fountainFluid.DrainImpacts())
            {
                // Only actual parcels crossing the pond surface create waves.
                // Four spatial bins retain distinct landing spots without making
                // every parcel a separate full-field GPU disturbance pass.
                int bin = Math.Clamp((int)((impact.Position.X - .5f) * _aspect /
                    (.42f * MathF.Min(_aspect, 1)) * 4 + 2), 0, 3);
                _fountainLandingPosition[bin] += impact.Position * impact.NormalMomentum;
                _fountainLandingMomentum[bin] += impact.NormalMomentum;
                float retained = _duckLandingMomentum[bin];
                float incoming = impact.NormalMomentum;
                float total = retained + incoming;
                if (total > 0)
                {
                    _duckLandingPosition[bin] = retained > 0
                        ? (_duckLandingPosition[bin] * retained + impact.Position * incoming) / total
                        : impact.Position;
                    _duckLandingMomentum[bin] = total;
                }
                _fountainImpacts += impact.Particles;
            }
            if (_steps % 12 == 11)
            {
                for (int bin = 0; bin < _fountainLandingMomentum.Length; bin++)
                {
                    float momentum = _fountainLandingMomentum[bin];
                    if (momentum > 0)
                    {
                        Vector2 landing = _fountainLandingPosition[bin] / momentum;
                        float pulse = Math.Clamp(momentum * 150f, 0, .0015f);
                        _disturbanceEffect.ConstantBuffer = new WaterDisturbanceShader(_fieldSize, _aspect,
                            new Float2(landing.X, landing.Y), .018f, pulse);
                        Run(_disturbanceEffect, _next, _field);
                        (_field, _next) = (_next, _field);
                    }
                    _fountainLandingPosition[bin] = Vector2.Zero;
                    _fountainLandingMomentum[bin] = 0;
                }
            }
            _stepEffect.ConstantBuffer = new WaterStepShader(_fieldSize, _aspect, (float)FixedStep, _waveSpeed);
            Run(_stepEffect, _next, _field);
            (_field, _next) = (_next, _field);
            Float4 Landing(int bin) => new(_duckLandingPosition[bin].X,
                _duckLandingPosition[bin].Y, Math.Clamp(_duckLandingMomentum[bin] * 2400f, 0, .065f), 0);
            Float4 Stick(int wake) => new(_stickWakePosition[wake].X, _stickWakePosition[wake].Y,
                _stickWakeStrength[wake], MathF.Atan2(_stickWakeDirection[wake].Y,
                    _stickWakeDirection[wake].X));
            _duckDynamicsEffect.ConstantBuffer = new WaterDuckDynamicsShader(_fieldSize, _aspect,
                (float)FixedStep, (float)(_simulatedSeconds + FixedStep), _ambientEnabled ? 1 : 0,
                _activeDuckCount,
                new Float4(_duckSplashPosition.X, _duckSplashPosition.Y, _duckSplashStrength, 0),
                Landing(0), Landing(1), Landing(2), Landing(3),
                Stick(0), Stick(1), Stick(2), Stick(3),
                Stick(4), Stick(5), Stick(6), Stick(7));
            _duckDynamicsEffect.Sources[1] = _field;
            Run(_duckDynamicsEffect, _nextDucks, _ducks);
            (_ducks, _nextDucks) = (_nextDucks, _ducks);
            _accumulator = Math.Max(0, _accumulator - FixedStep);
            _simulatedSeconds += FixedStep;
            _steps++;
            Revision++;
            TryLaunchDuck();
        }
        ObserveDuckLandings();
        BindSurface();
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using (var drawing = _field.CreateDrawingSession()) drawing.Clear(Color.FromArgb(255, 0, 0, 0));
        using (var drawing = _next.CreateDrawingSession()) drawing.Clear(Color.FromArgb(255, 0, 0, 0));
        _duckCount = WaterGardenDucks.InitialCount;
        _activeDuckCount = WaterGardenDucks.InitialCount;
        _pendingDuckLaunches.Clear();
        Array.Clear(_duckLandingObserved);
        _duckSplashPosition = Vector2.Zero;
        _duckSplashStrength = 0;
        _nextDuckLaunchAt = 0;
        InitializeDucks();
        _accumulator = _simulatedSeconds = _droppedSeconds = 0;
        _steps = 0;
        _disturbances = 0;
        _fountainImpacts = 0;
        _fountainFluid.Reset();
        Array.Clear(_fountainLandingPosition);
        Array.Clear(_fountainLandingMomentum);
        Array.Clear(_duckLandingPosition);
        Array.Clear(_duckLandingMomentum);
        Array.Clear(_stickWakePosition);
        Array.Clear(_stickWakeDirection);
        Array.Clear(_stickWakeStrength);
        Revision++;
        BindSurface();
    }

    public void Draw(CanvasDrawingSession drawing, Rect bounds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UpdateFountainAtlas();
        Matrix3x2 original = drawing.Transform;
        try
        {
            drawing.Transform = Matrix3x2.CreateScale((float)bounds.Width / _bedSize.X,
                (float)bounds.Height / _bedSize.Y) *
                Matrix3x2.CreateTranslation((float)bounds.X, (float)bounds.Y) * original;
            drawing.DrawImage(_surfaceEffect, Vector2.Zero, new Rect(0, 0, _bedSize.X, _bedSize.Y));
        }
        finally { drawing.Transform = original; }
    }

#if DEBUG
    public void SetFountainEnabledForVerification(bool enabled) => SetFountainEnabled(enabled);

    public void SetAmbientEnabledForVerification(bool enabled)
    {
        _ambientEnabled = enabled;
        Revision++;
        BindSurface();
    }

    public WaterGardenFieldStatistics GetFieldStatistics()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] pixels = _field.GetPixelBytes();
        double mass = 0, minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;
        double speed = 0, energy = 0;
        int nonFinite = 0;
        double dx = _aspect / FieldWidth, dy = 1.0 / FieldHeight;
        float HeightAt(int x, int y) => Read(pixels,
            (Math.Clamp(y, 0, FieldHeight - 1) * FieldWidth + Math.Clamp(x, 0, FieldWidth - 1)) * 16);
        for (int y = 0; y < FieldHeight; y++)
        for (int x = 0; x < FieldWidth; x++)
        {
            int offset = (y * FieldWidth + x) * 16;
            double h = Read(pixels, offset), v = Read(pixels, offset + 4);
            if (!double.IsFinite(h) || !double.IsFinite(v)) { nonFinite++; continue; }
            mass += h * dx * dy;
            minimum = Math.Min(minimum, h);
            maximum = Math.Max(maximum, h);
            speed = Math.Max(speed, Math.Abs(v));
            double gx = (HeightAt(x + 1, y) - HeightAt(x - 1, y)) / (2 * dx);
            double gy = (HeightAt(x, y + 1) - HeightAt(x, y - 1)) / (2 * dy);
            energy += .5 * (v * v + _waveSpeed * _waveSpeed * (gx * gx + gy * gy)) * dx * dy;
        }
        return new(mass, minimum, maximum, speed, energy, nonFinite);
    }

    public WaterGardenFieldProbe GetFieldProbe(Vector2 point)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) ||
            point.X < 0 || point.X > 1 || point.Y < 0 || point.Y > 1)
            throw new ArgumentOutOfRangeException(nameof(point));
        byte[] pixels = _field.GetPixelBytes(Math.Min(FieldWidth - 1, (int)(point.X * FieldWidth)),
            Math.Min(FieldHeight - 1, (int)(point.Y * FieldHeight)), 1, 1);
        return new(Read(pixels, 0), Read(pixels, 4));
    }

    public IReadOnlyList<WaterGardenDuckState> GetDuckStates()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] pixels = _ducks.GetPixelBytes();
        var states = new WaterGardenDuckState[_activeDuckCount];
        for (int index = 0; index < states.Length; index++)
        {
            int horizontal = index * 16;
            int motion = (WaterGardenDucks.MaximumCount + index) * 16;
            int vertical = (WaterGardenDucks.MaximumCount * 2 + index) * 16;
            int slope = (WaterGardenDucks.MaximumCount * 3 + index) * 16;
            int drop = (WaterGardenDucks.MaximumCount * 4 + index) * 16;
            states[index] = new(index, Read(pixels, horizontal), Read(pixels, horizontal + 4),
                Read(pixels, motion), Read(pixels, motion + 4),
                Read(pixels, vertical), Read(pixels, vertical + 4),
                Read(pixels, slope), Read(pixels, slope + 4),
                Read(pixels, horizontal + 8), Read(pixels, motion + 8), Read(pixels, vertical + 8),
                Read(pixels, drop), Read(pixels, drop + 4), Read(pixels, drop + 8));
        }
        return states;
    }

    internal void PlaceDuckForVerification(int index, Vector2 position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (index < 0 || index >= WaterGardenDucks.InitialCount ||
            !float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
            position.X is < .16f or > .84f || position.Y is < .22f or > .82f)
            throw new ArgumentOutOfRangeException(nameof(position));
        byte[] state = _ducks.GetPixelBytes();
        int horizontal = index * 16;
        int motion = (WaterGardenDucks.MaximumCount + index) * 16;
        BinaryPrimitives.WriteSingleLittleEndian(state.AsSpan(horizontal, 4), position.X);
        BinaryPrimitives.WriteSingleLittleEndian(state.AsSpan(horizontal + 4, 4), position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(state.AsSpan(motion, 4), 0);
        BinaryPrimitives.WriteSingleLittleEndian(state.AsSpan(motion + 4, 4), 0);
        _ducks.SetPixelBytes(state);
        _nextDucks.SetPixelBytes(state);
        Revision++;
        BindSurface();
    }

    private static float Read(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset, 4));
#endif

    private static void Run<T>(PixelShaderEffect<T> effect, CanvasRenderTarget target, ICanvasImage source)
        where T : unmanaged, ID2D1PixelShader, ID2D1PixelShaderDescriptor<T>
    {
        effect.Sources[0] = source;
        using var drawing = target.CreateDrawingSession();
        drawing.DrawImage(effect, Vector2.Zero, new Rect(0, 0, target.Size.Width, target.Size.Height),
            1, CanvasImageInterpolation.Linear, CanvasComposite.Copy);
    }

    private void InitializeDucks()
    {
        _duckInitializeEffect.ConstantBuffer = new WaterDuckInitializeShader(_aspect);
        using (var drawing = _ducks.CreateDrawingSession())
            drawing.DrawImage(_duckInitializeEffect, Vector2.Zero,
                new Rect(0, 0, WaterGardenDucks.MaximumCount, WaterGardenDucks.Rows),
                1, CanvasImageInterpolation.NearestNeighbor, CanvasComposite.Copy);
        using (var drawing = _nextDucks.CreateDrawingSession())
            drawing.DrawImage(_duckInitializeEffect, Vector2.Zero,
                new Rect(0, 0, WaterGardenDucks.MaximumCount, WaterGardenDucks.Rows),
                1, CanvasImageInterpolation.NearestNeighbor, CanvasComposite.Copy);
    }

    private void BindSurface()
    {
        _surfaceField.Source = _field;
        _surfaceEffect.Sources[0] = _surfaceField;
        _surfaceEffect.Sources[1] = _bed;
        _surfaceDucks.Source = _ducks;
        _surfaceEffect.Sources[2] = _surfaceDucks;
        _surfaceEffect.Sources[3] = _surfaceStone;
        _surfaceEffect.Sources[4] = _surfaceWetStone;
        _surfaceEffect.Sources[5] = _surfaceFountainVolume;
        _surfaceEffect.Sources[6] = _surfaceSand;
        Vector3 fountainMinimum = _fountainFluid.WorldMinimum;
        Vector3 fountainSize = _fountainFluid.WorldSize;
        WaterGardenCamera camera = WaterGardenView.CameraAt(_entranceProgress);
        _surfaceEffect.ConstantBuffer = new WaterSurfaceShader(_fieldSize, _bedSize, _aspect,
            (float)_simulatedSeconds, _ambientEnabled ? 1 : 0,
            new Float4(camera.SinTilt, camera.CosTilt, camera.Distance, camera.Zoom),
            new Float2(camera.CentreX, camera.CentreY), WaterGardenView.RimHeight,
            new Float2(WaterGardenDucks.MaximumCount, WaterGardenDucks.Rows), _activeDuckCount,
            new Float2(_duckSplashPosition.X, _duckSplashPosition.Y),
            _fountainFluid.ParticleCount > 0 ? 1f : 0f,
            new Float3(fountainMinimum.X, fountainMinimum.Y, fountainMinimum.Z),
            new Float3(fountainSize.X, fountainSize.Y, fountainSize.Z),
            new Float3(WaterFountainFluid.GridX, WaterFountainFluid.GridY, WaterFountainFluid.GridZ),
            new Float2(WaterFountainFluid.AtlasTiles, WaterFountainFluid.AtlasTiles));
    }

    private void UpdateFountainAtlas()
    {
        if (_uploadedFountainRevision == _fountainFluid.Revision) return;
        _fountainAtlas!.SetPixelBytes(_fountainFluid.BuildDensityAtlas());
        _uploadedFountainRevision = _fountainFluid.Revision;
    }

    private void DisposeEffects()
    {
        _disturbanceEffect.Dispose();
        _stepEffect.Dispose();
        _surfaceEffect.Dispose();
        _surfaceField.Dispose();
        _duckInitializeEffect.Dispose();
        _duckSpawnEffect.Dispose();
        _duckStickContactEffect.Dispose();
        _duckDynamicsEffect.Dispose();
        _surfaceDucks.Dispose();
        _surfaceStone.Dispose();
        _surfaceWetStone.Dispose();
        _surfaceSand.Dispose();
        _surfaceFountainVolume.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeEffects();
        _field.Dispose();
        _next.Dispose();
        _bed.Dispose();
        _ducks.Dispose();
        _nextDucks.Dispose();
        _ownedStoneTexture?.Dispose();
        _ownedWetStoneTexture?.Dispose();
        _ownedSandTexture?.Dispose();
        _fountainAtlas?.Dispose();
    }
}
