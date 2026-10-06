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
    double DroppedSeconds, int DisturbanceCount, double WaveSpeed, string BufferFormat, int DuckCount);

internal sealed record WaterGardenFieldStatistics(double HeightMass, double MinimumHeight,
    double MaximumHeight, double MaximumSpeed, double TotalEnergy, int NonFiniteValues);

internal sealed record WaterGardenFieldProbe(double Height, double Velocity);

#if DEBUG
internal sealed record WaterGardenDuckState(int Index, float SurfaceX, float SurfaceY,
    float VelocityX, float VelocityY, float Height, float VerticalVelocity,
    float SlopeX, float SlopeY, float Yaw, float YawVelocity, float Scale);
#endif

/// <summary>Signed height-field water with a retained pebble substrate. The wave solver and
/// refraction principles adapt Evan Wallace's MIT WebGL Water; see THIRD_PARTY_NOTICES.md.
/// All simulation and visible rendering stay on the GPU. Optical caustics use a bounded
/// local focusing approximation, not the original demo's displaced caustic mesh.</summary>
internal sealed class WaterGardenSimulation : IDisposable
{
    private const double FixedStep = 1.0 / 120.0;
    private const int MaximumStepsPerAdvance = 8;
    private readonly float _aspect;
    private readonly float _waveSpeed;
    private readonly Float2 _fieldSize;
    private readonly Float2 _bedSize;
    private CanvasRenderTarget _field;
    private CanvasRenderTarget _next;
    private CanvasRenderTarget _ducks;
    private CanvasRenderTarget _nextDucks;
    private readonly CanvasRenderTarget _bed;
    private readonly PixelShaderEffect<WaterDisturbanceShader> _disturbanceEffect = new();
    private readonly PixelShaderEffect<WaterStepShader> _stepEffect = new();
    private readonly PixelShaderEffect<WaterSurfaceShader> _surfaceEffect = new();
    private readonly PixelShaderEffect<WaterDuckInitializeShader> _duckInitializeEffect = new();
    private readonly PixelShaderEffect<WaterDuckDynamicsShader> _duckDynamicsEffect = new();
    private readonly Transform2DEffect _surfaceField = new();
    private readonly Transform2DEffect _surfaceDucks = new();
    private double _accumulator;
    private double _simulatedSeconds;
    private double _droppedSeconds;
    private long _steps;
    private int _disturbances;
    private bool _ambientEnabled = true;
    private bool _disposed;

    public WaterGardenSimulation(CanvasDevice device, int fieldWidth, int fieldHeight, double aspect)
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
        _surfaceDucks.TransformMatrix = Matrix3x2.CreateScale(bedWidth / (float)WaterGardenDucks.Count,
            bedHeight / (float)WaterGardenDucks.Rows);
        _surfaceDucks.InterpolationMode = CanvasImageInterpolation.NearestNeighbor;
        _surfaceDucks.BorderMode = EffectBorderMode.Hard;
        _surfaceDucks.BufferPrecision = CanvasBufferPrecision.Precision32Float;
        var allocated = new List<CanvasRenderTarget>();
        try
        {
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
                var target = new CanvasRenderTarget(device, WaterGardenDucks.Count, WaterGardenDucks.Rows,
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
            throw;
        }
    }

    public int FieldWidth { get; }
    public int FieldHeight { get; }
    public long Revision { get; private set; }
    public ICanvasImage Output => _surfaceEffect;

    public WaterGardenDiagnostics GetDiagnostics() => new(FieldWidth, FieldHeight,
        (int)_bedSize.X, (int)_bedSize.Y, _steps, _simulatedSeconds, _droppedSeconds,
        _disturbances, _waveSpeed, "RGBA32Float (height, vertical velocity, reserved, opaque)", WaterGardenDucks.Count);

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
            _stepEffect.ConstantBuffer = new WaterStepShader(_fieldSize, _aspect, (float)FixedStep, _waveSpeed);
            Run(_stepEffect, _next, _field);
            (_field, _next) = (_next, _field);
            _duckDynamicsEffect.ConstantBuffer = new WaterDuckDynamicsShader(_fieldSize, _aspect,
                (float)FixedStep, (float)(_simulatedSeconds + FixedStep), _ambientEnabled ? 1 : 0);
            _duckDynamicsEffect.Sources[1] = _field;
            Run(_duckDynamicsEffect, _nextDucks, _ducks);
            (_ducks, _nextDucks) = (_nextDucks, _ducks);
            _accumulator = Math.Max(0, _accumulator - FixedStep);
            _simulatedSeconds += FixedStep;
            _steps++;
            Revision++;
        }
        BindSurface();
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using (var drawing = _field.CreateDrawingSession()) drawing.Clear(Color.FromArgb(255, 0, 0, 0));
        using (var drawing = _next.CreateDrawingSession()) drawing.Clear(Color.FromArgb(255, 0, 0, 0));
        InitializeDucks();
        _accumulator = _simulatedSeconds = _droppedSeconds = 0;
        _steps = 0;
        _disturbances = 0;
        Revision++;
        BindSurface();
    }

    public void Draw(CanvasDrawingSession drawing, Rect bounds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        var states = new WaterGardenDuckState[WaterGardenDucks.Count];
        for (int index = 0; index < states.Length; index++)
        {
            int horizontal = index * 16;
            int motion = (WaterGardenDucks.Count + index) * 16;
            int vertical = (WaterGardenDucks.Count * 2 + index) * 16;
            int slope = (WaterGardenDucks.Count * 3 + index) * 16;
            states[index] = new(index, Read(pixels, horizontal), Read(pixels, horizontal + 4),
                Read(pixels, motion), Read(pixels, motion + 4),
                Read(pixels, vertical), Read(pixels, vertical + 4),
                Read(pixels, slope), Read(pixels, slope + 4),
                Read(pixels, horizontal + 8), Read(pixels, motion + 8), Read(pixels, vertical + 8));
        }
        return states;
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
                new Rect(0, 0, WaterGardenDucks.Count, WaterGardenDucks.Rows),
                1, CanvasImageInterpolation.NearestNeighbor, CanvasComposite.Copy);
        using (var drawing = _nextDucks.CreateDrawingSession())
            drawing.DrawImage(_duckInitializeEffect, Vector2.Zero,
                new Rect(0, 0, WaterGardenDucks.Count, WaterGardenDucks.Rows),
                1, CanvasImageInterpolation.NearestNeighbor, CanvasComposite.Copy);
    }

    private void BindSurface()
    {
        _surfaceField.Source = _field;
        _surfaceEffect.Sources[0] = _surfaceField;
        _surfaceEffect.Sources[1] = _bed;
        _surfaceDucks.Source = _ducks;
        _surfaceEffect.Sources[2] = _surfaceDucks;
        _surfaceEffect.ConstantBuffer = new WaterSurfaceShader(_fieldSize, _bedSize, _aspect,
            (float)_simulatedSeconds, _ambientEnabled ? 1 : 0,
            new Float4(WaterGardenView.SinTilt, WaterGardenView.CosTilt, WaterGardenView.Distance, WaterGardenView.Zoom),
            new Float2(WaterGardenView.CentreX, WaterGardenView.CentreY), WaterGardenView.RimHeight,
            new Float2(WaterGardenDucks.Count, WaterGardenDucks.Rows));
    }

    private void DisposeEffects()
    {
        _disturbanceEffect.Dispose();
        _stepEffect.Dispose();
        _surfaceEffect.Dispose();
        _surfaceField.Dispose();
        _duckInitializeEffect.Dispose();
        _duckDynamicsEffect.Dispose();
        _surfaceDucks.Dispose();
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
    }
}
