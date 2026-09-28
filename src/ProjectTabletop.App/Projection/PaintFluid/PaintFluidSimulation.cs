using System.Numerics;
using System.Buffers.Binary;
using ComputeSharp;
using ComputeSharp.D2D1;
using ComputeSharp.D2D1.Descriptors;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace ProjectTabletop.App.Projection.PaintFluid;

internal sealed record PaintFluidDiagnostics(
    int FieldWidth, int FieldHeight, long SimulationSteps, double SimulatedSeconds,
    double DroppedSeconds, int DropCount, double ActiveSecondsRemaining, string BufferFormat);

internal sealed record PaintFluidFieldStatistics(
    double HeightMass, double PigmentMass, double MaximumHeight,
    double MaximumSpeed, int NonFiniteValues, int NegativeDensityValues);

/// <summary>
/// GPU-only thin-film paint. Each drop joins shared velocity, height and pigment
/// fields rather than becoming a separately rendered animated decal.
/// </summary>
internal sealed class PaintFluidSimulation : IDisposable
{
    private const double FixedStep = 1.0 / 60.0;
    private const int MaximumStepsPerAdvance = 4;
    private const int PressureIterations = 8;
    private readonly float _aspect;
    private readonly Float2 _size;
    private CanvasRenderTarget _flow;
    private CanvasRenderTarget _flowNext;
    private CanvasRenderTarget _pigment;
    private CanvasRenderTarget _pigmentNext;
    private CanvasRenderTarget _metal;
    private CanvasRenderTarget _metalNext;
    private CanvasRenderTarget _pressure;
    private CanvasRenderTarget _pressureNext;
    private readonly CanvasRenderTarget _divergence;
    private readonly PixelShaderEffect<FluidDropShader> _dropEffect = new();
    private readonly PixelShaderEffect<FluidVelocityShader> _velocityEffect = new();
    private readonly PixelShaderEffect<FluidDivergenceShader> _divergenceEffect = new();
    private readonly PixelShaderEffect<FluidPressureShader> _pressureEffect = new();
    private readonly PixelShaderEffect<FluidProjectShader> _projectEffect = new();
    private readonly PixelShaderEffect<FluidHeightShader> _heightEffect = new();
    private readonly PixelShaderEffect<FluidPigmentShader> _pigmentEffect = new();
    private readonly PixelShaderEffect<FluidSurfaceShader> _surfaceEffect = new();
    private double _accumulator;
    private double _activeSecondsRemaining;
    private double _simulatedSeconds;
    private double _droppedSeconds;
    private long _steps;
    private int _dropCount;
    private bool _disposed;

    public PaintFluidSimulation(CanvasDevice device, int fieldWidth, int fieldHeight, double aspect)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (fieldWidth is < 32 or > 2048 || fieldHeight is < 32 or > 2048)
            throw new ArgumentOutOfRangeException(nameof(fieldWidth));
        if (!double.IsFinite(aspect) || aspect is < 0.1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(aspect));
        if (!device.IsPixelFormatSupported(DirectXPixelFormat.R32G32B32A32Float))
            throw new NotSupportedException("The display adapter does not support floating-point paint fields.");
        FieldWidth = fieldWidth;
        FieldHeight = fieldHeight;
        _size = new Float2(fieldWidth, fieldHeight);
        _aspect = (float)aspect;
        var allocated = new List<CanvasRenderTarget>();
        try
        {
            CanvasRenderTarget CreateField()
            {
                var field = new CanvasRenderTarget(device, fieldWidth, fieldHeight, 96,
                    DirectXPixelFormat.R32G32B32A32Float, CanvasAlphaMode.Ignore);
                allocated.Add(field);
                using var ds = field.CreateDrawingSession();
                ds.Clear(Color.FromArgb(255, 0, 0, 0));
                return field;
            }
            _flow = CreateField(); _flowNext = CreateField();
            _pigment = CreateField(); _pigmentNext = CreateField();
            _metal = CreateField(); _metalNext = CreateField();
            _pressure = CreateField(); _pressureNext = CreateField();
            _divergence = CreateField();
            _surfaceEffect.ConstantBuffer = new FluidSurfaceShader(_size, _aspect);
            BindSurface();
            // Force realization while construction can still fail gracefully.
            using var probe = new CanvasRenderTarget(device, 8, 8, 96);
            using var probeSession = probe.CreateDrawingSession();
            probeSession.DrawImage(_surfaceEffect);
        }
        catch
        {
            foreach (var field in allocated) field.Dispose();
            DisposeEffects();
            throw;
        }
    }

    public int FieldWidth { get; }
    public int FieldHeight { get; }
    public long Revision { get; private set; }
    public bool HasPaint => _dropCount > 0;
    public bool IsActive => !_disposed && HasPaint && _activeSecondsRemaining > 0;
    public ICanvasImage Output => _surfaceEffect;

    public PaintFluidDiagnostics GetDiagnostics() => new(FieldWidth, FieldHeight, _steps,
        _simulatedSeconds, _droppedSeconds, _dropCount, _activeSecondsRemaining, "RGBA32Float (opaque data channels)");

#if DEBUG
    // Verification only: live rendering and camera detection never read the
    // simulation fields back from the GPU.
    public PaintFluidFieldStatistics GetFieldStatistics()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] flow = _flow.GetPixelBytes();
        byte[] pigment = _pigment.GetPixelBytes();
        double heightMass = 0, pigmentMass = 0, maxHeight = 0, maxSpeed = 0;
        int nonFinite = 0, negative = 0;
        static float ReadFloat(byte[] bytes, int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset, 4));
        for (int i = 0; i < flow.Length; i += 16)
        {
            float vx = ReadFloat(flow, i), vy = ReadFloat(flow, i + 4), height = ReadFloat(flow, i + 8);
            float red = ReadFloat(pigment, i), green = ReadFloat(pigment, i + 4), blue = ReadFloat(pigment, i + 8);
            if (!float.IsFinite(vx) || !float.IsFinite(vy) || !float.IsFinite(height)
                || !float.IsFinite(red) || !float.IsFinite(green) || !float.IsFinite(blue)) nonFinite++;
            if (height < -0.00001f || red < -0.00001f || green < -0.00001f || blue < -0.00001f) negative++;
            heightMass += height;
            pigmentMass += red + green + blue;
            maxHeight = Math.Max(maxHeight, height);
            maxSpeed = Math.Max(maxSpeed, Math.Sqrt((double)vx * vx + (double)vy * vy));
        }
        return new(heightMass, pigmentMass, maxHeight, maxSpeed, nonFinite, negative);
    }
#endif

    public void AddDrop(Vector2 boardUv, float radiusUv, Vector3 pigment, float amount, int seed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!float.IsFinite(boardUv.X) || !float.IsFinite(boardUv.Y)
            || !float.IsFinite(radiusUv) || !float.IsFinite(amount)
            || !float.IsFinite(pigment.X) || !float.IsFinite(pigment.Y) || !float.IsFinite(pigment.Z))
            throw new ArgumentException("Paint drop parameters must be finite.");
        var center = new Float2(Math.Clamp(boardUv.X, 0, 1), Math.Clamp(boardUv.Y, 0, 1));
        float radius = Math.Clamp(radiusUv, 0.009f, 0.18f);
        float volume = Math.Clamp(amount, 0.05f, 1.5f);
        // Three-channel Beer-Lambert absorption preserves supplied colours
        // while mixtures remain subtractive rather than averaging RGB light.
        var color = new Float3(-MathF.Log(Math.Clamp(pigment.X, 0.008f, 1)),
            -MathF.Log(Math.Clamp(pigment.Y, 0.008f, 1)), -MathF.Log(Math.Clamp(pigment.Z, 0.008f, 1)));
        _dropEffect.ConstantBuffer = new FluidDropShader(_size, center, radius, volume, color, _aspect, seed, 0);
        Run(_dropEffect, _flowNext, _flow); Swap(ref _flow, ref _flowNext);
        _dropEffect.ConstantBuffer = new FluidDropShader(_size, center, radius, volume, color, _aspect, seed, 1);
        Run(_dropEffect, _pigmentNext, _pigment); Swap(ref _pigment, ref _pigmentNext);
        _dropEffect.ConstantBuffer = new FluidDropShader(_size, center, radius, volume, color, _aspect, seed, 2);
        Run(_dropEffect, _metalNext, _metal); Swap(ref _metal, ref _metalNext);
        _dropCount++;
        _activeSecondsRemaining = 20;
        Revision++;
        BindSurface();
    }

    public void Advance(double seconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(seconds) || seconds <= 0 || !IsActive) return;
        double accepted = Math.Min(seconds, FixedStep * MaximumStepsPerAdvance);
        _droppedSeconds += seconds - accepted;
        _accumulator += accepted;
        int steps = Math.Min(MaximumStepsPerAdvance, (int)Math.Floor((_accumulator + 1e-9) / FixedStep));
        for (int i = 0; i < steps && IsActive; i++)
        {
            Step();
            _accumulator = Math.Max(0, _accumulator - FixedStep);
            _activeSecondsRemaining = Math.Max(0, _activeSecondsRemaining - FixedStep);
            _simulatedSeconds += FixedStep;
            _steps++;
            Revision++;
        }
        BindSurface();
    }

    public void Draw(CanvasDrawingSession ds, Rect logicalBoardBounds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Matrix3x2 original = ds.Transform;
        try
        {
            ds.Transform = Matrix3x2.CreateScale((float)logicalBoardBounds.Width / FieldWidth,
                (float)logicalBoardBounds.Height / FieldHeight)
                * Matrix3x2.CreateTranslation((float)logicalBoardBounds.X, (float)logicalBoardBounds.Y) * original;
            ds.DrawImage(_surfaceEffect);
        }
        finally { ds.Transform = original; }
    }

    private void Step()
    {
        _velocityEffect.ConstantBuffer = new FluidVelocityShader(_size, (float)FixedStep);
        Run(_velocityEffect, _flowNext, _flow); Swap(ref _flow, ref _flowNext);
        _divergenceEffect.ConstantBuffer = new FluidDivergenceShader(_size);
        Run(_divergenceEffect, _divergence, _flow);
        using (var ds = _pressure.CreateDrawingSession()) ds.Clear(Color.FromArgb(255, 0, 0, 0));
        _pressureEffect.ConstantBuffer = new FluidPressureShader(_size);
        for (int i = 0; i < PressureIterations; i++)
        {
            Run(_pressureEffect, _pressureNext, _pressure, _divergence);
            Swap(ref _pressure, ref _pressureNext);
        }
        _projectEffect.ConstantBuffer = new FluidProjectShader(_size);
        Run(_projectEffect, _flowNext, _flow, _pressure); Swap(ref _flow, ref _flowNext);
        _heightEffect.ConstantBuffer = new FluidHeightShader(_size, (float)FixedStep);
        Run(_heightEffect, _flowNext, _flow); Swap(ref _flow, ref _flowNext);
        _pigmentEffect.ConstantBuffer = new FluidPigmentShader(_size, (float)FixedStep, 3);
        Run(_pigmentEffect, _pigmentNext, _pigment, _flow); Swap(ref _pigment, ref _pigmentNext);
        _pigmentEffect.ConstantBuffer = new FluidPigmentShader(_size, (float)FixedStep, 0.05f);
        Run(_pigmentEffect, _metalNext, _metal, _flow); Swap(ref _metal, ref _metalNext);
    }

    private static void Swap(ref CanvasRenderTarget first, ref CanvasRenderTarget second) => (first, second) = (second, first);

    private static void Run<T>(PixelShaderEffect<T> effect, CanvasRenderTarget target, params ICanvasImage[] sources)
        where T : unmanaged, ID2D1PixelShader, ID2D1PixelShaderDescriptor<T>
    {
        for (int i = 0; i < sources.Length; i++) effect.Sources[i] = sources[i];
        using var ds = target.CreateDrawingSession();
        ds.DrawImage(effect, Vector2.Zero, new Rect(0, 0, target.Size.Width, target.Size.Height), 1,
            CanvasImageInterpolation.Linear, CanvasComposite.Copy);
    }

    private void BindSurface()
    {
        _surfaceEffect.Sources[0] = _flow;
        _surfaceEffect.Sources[1] = _pigment;
        _surfaceEffect.Sources[2] = _metal;
    }

    private void DisposeEffects()
    {
        _dropEffect.Dispose(); _velocityEffect.Dispose(); _divergenceEffect.Dispose();
        _pressureEffect.Dispose(); _projectEffect.Dispose(); _heightEffect.Dispose(); _pigmentEffect.Dispose(); _surfaceEffect.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeEffects();
        _flow.Dispose(); _flowNext.Dispose(); _pigment.Dispose(); _pigmentNext.Dispose();
        _metal.Dispose(); _metalNext.Dispose(); _pressure.Dispose(); _pressureNext.Dispose(); _divergence.Dispose();
    }
}
