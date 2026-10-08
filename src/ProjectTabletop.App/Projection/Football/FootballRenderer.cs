using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection.Football;

/// <summary>A device-owned, overhead football pitch. All coordinates are mapped
/// from the same 1.6 × 1 metre simulation rectangle used for input.</summary>
internal sealed partial class FootballRenderer : IDisposable
{
    private readonly CanvasDevice _device;
    private readonly SoccerBallRenderer _ball = new();
    private readonly object _pitchGate = new();
    private Task? _pitchTask;
    private CanvasRenderTarget? _pitch;
    private bool _disposed;

    internal FootballRenderer(CanvasDevice device) => _device = device;

    internal bool PitchReady => Pitch is not null;
    internal bool PitchCompleted { get { lock (_pitchGate) return _pitchTask is { IsCompleted: true }; } }
    internal string? PitchError { get { lock (_pitchGate) return _pitchTask?.Exception?.GetBaseException().Message; } }
    private CanvasRenderTarget? Pitch { get { lock (_pitchGate) return _pitch; } }

    // About 300k antialiased grass strokes are far too slow for a render pass
    // under the compositor lock. Build the cached pitch once on a worker; draw
    // callers show their loading state until it is published.
    internal Task EnsurePitchAsync()
    {
        lock (_pitchGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pitchTask is null)
            {
                _pitchTask = Task.Run(BuildPitch);
                _ = _pitchTask.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
            return _pitchTask;
        }
    }

    internal void DrawThumbnail(CanvasDrawingSession ds, Rect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var pitch = Pitch ?? throw new InvalidOperationException("Football pitch artwork is not ready.");
        var before = ds.Transform;
        float scale = (float)bounds.Height / 400;
        float width = (float)bounds.Width / scale;
        ds.Transform = Matrix3x2.CreateScale(scale) *
            Matrix3x2.CreateTranslation((float)bounds.X, (float)bounds.Y) * before;
        try
        {
            using var clip = ds.CreateLayer(1, new Rect(0, 0, width, 400));
            double cropWidth = 1824, cropHeight = cropWidth * 400 / width;
            if (cropHeight > 1132) { cropHeight = 1132; cropWidth = cropHeight * width / 400; }
            var crop = new Rect((1824 - cropWidth) / 2, (1132 - cropHeight) / 2, cropWidth, cropHeight);
            ds.DrawImage(pitch, new Rect(0, 0, width, 400), crop);
            var center = new Vector2(width * .80f, 178);
            DrawSoftShadow(ds, center + new Vector2(47, 89), 127, 93, 165);
            DrawSoftShadow(ds, new(width * .66f + 12, 320), 68, 42, 115);
            DrawKicker(ds, FootballKickerStyle.Car, new(width * .66f, 300), -.47f, Ink(239, 91, 53), true);
            _ball.Draw(ds, center, 112, Quaternion.CreateFromYawPitchRoll(.47f, -.35f, .28f));
        }
        finally { ds.Transform = before; }
    }

    /// <param name="fieldBounds">The playable rectangle, excluding the 0.1-metre-deep goals.</param>
    internal void Draw(CanvasDrawingSession ds, Rect fieldBounds, FootballSnapshot snapshot)
    {
        if (fieldBounds.Width <= 0 || fieldBounds.Height <= 0) return;
        var pitch = Pitch;
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale((float)fieldBounds.Width / 1600, (float)fieldBounds.Height / 1000) *
            Matrix3x2.CreateTranslation((float)fieldBounds.X, (float)fieldBounds.Y) * previous;
        try
        {
            if (pitch is not null) ds.DrawImage(pitch, -112, -66);
            else ds.FillRectangle(new Rect(0, 0, 1600, 1000), Ink(66, 105, 39));
            // Goal shadows belong to the turf and remain under the moving ball.
            DrawGoal(ds, false, false);
            DrawGoal(ds, true, false);
            foreach (var kicker in snapshot.Kickers)
            {
                var position = FieldPoint(kicker.Position);
                DrawSoftShadow(ds, position + new Vector2(17, 23), 68, 47, kicker.Present ? (byte)132 : (byte)28);
            }
            float lift = Math.Max(0, snapshot.BallHeight - FootballGame.BallRadius) * 1000;
            var ground = FieldPoint(snapshot.BallPosition);
            var ballCenter = ground + new Vector2(-lift * .18f, -lift * .40f);
            float radius = FootballGame.BallRadius * 1000 * (1 + lift * .00065f);
            float spread = 1 + lift * .0035f;
            var shadowCenter = ground + new Vector2(8 + lift * .51f, 10 + lift * .64f);
            byte shadowOpacity = (byte)Math.Clamp(197 - lift * .24f, 130, 197);
            DrawSoftShadow(ds, shadowCenter, radius * .98f * spread, radius * .79f * spread, shadowOpacity);
            if (lift < 8)
                DrawSoftShadow(ds, ground + new Vector2(2, 4), radius * .64f, radius * .38f, (byte)(100 * (1 - lift / 8)));

            // The simulation turns each body toward play at a fixed rate.
            foreach (var kicker in snapshot.Kickers)
                DrawKicker(ds, kicker.Style, FieldPoint(kicker.Position), kicker.Heading,
                    kicker.Index == 0 ? Ink(239, 91, 53) : Ink(52, 161, 224), kicker.Present, !kicker.IsAi);
            _ball.Draw(ds, ballCenter, radius, snapshot.BallRotation);
            // Elevated crossbars occlude a low ball at the goal mouth.
            DrawGoal(ds, false, true);
            DrawGoal(ds, true, true);
        }
        finally { ds.Transform = previous; }
    }

    private static Vector2 FieldPoint(Vector2 point) => new((point.X + .8f) * 1000, (point.Y + .5f) * 1000);

    private void BuildPitch()
    {
        var target = new CanvasRenderTarget(_device, 1824, 1132, 96);
        bool published = false;
        try
        {
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(Ink(24, 45, 23, 0));
                ds.Transform = Matrix3x2.CreateTranslation(112, 66);
                using var shape = CanvasGeometry.CreateRoundedRectangle(_device, new Rect(-110, -64, 1820, 1128), 16, 16);
                using (ds.CreateLayer(1, shape))
                {
                    ds.FillRectangle(new Rect(-112, -66, 1824, 1132), Ink(55, 87, 36));
                    DrawGrass(ds);
                    using var sun = new CanvasLinearGradientBrush(_device,
                    [
                        new() { Position = 0, Color = Ink(228, 219, 114, 38) },
                        new() { Position = .52f, Color = Ink(180, 198, 112, 0) },
                        new() { Position = 1, Color = Ink(5, 24, 19, 35) }
                    ]) { StartPoint = new(0, 0), EndPoint = new(1500, 1050) };
                    ds.FillRectangle(new Rect(-112, -66, 1824, 1132), sun);
                    DrawMarkings(ds);
                }
                ds.DrawRoundedRectangle(new Rect(-110, -64, 1820, 1128), 16, 16, Ink(17, 37, 22, 180), 3);
            }
            lock (_pitchGate)
            {
                if (_disposed) return;
                _pitch = target;
                published = true;
            }
        }
        finally { if (!published) target.Dispose(); }
    }

    private static void DrawGrass(CanvasDrawingSession ds)
    {
        // Original deterministic botanical texture, cached once per CanvasDevice.
        // Blade tufts, cut tips and broad mower directions are separate scales.
        var random = new Random(74183);
        for (int stripe = -1; stripe <= 12; stripe++)
        {
            float x = stripe * (1600f / 12);
            bool towardSun = (stripe & 1) == 0;
            ds.FillRectangle(new Rect(x, -66, 1600f / 12 + .5f, 1132),
                towardSun ? Ink(91, 124, 44) : Ink(66, 105, 39));
        }
        // Very low-frequency variations keep the closely cut turf organic.
        for (int i = 0; i < 2400; i++)
        {
            float x = -110 + (float)random.NextDouble() * 1820;
            float y = -65 + (float)random.NextDouble() * 1130;
            float size = 4 + (float)random.NextDouble() * 17;
            ds.FillEllipse(new(x, y), size * 1.5f, size,
                i % 2 == 0 ? Ink(18, 66, 31, 9) : Ink(199, 192, 86, 8));
        }
        for (int i = 0; i < 235000; i++)
        {
            float x = -110 + (float)random.NextDouble() * 1820;
            float y = -65 + (float)random.NextDouble() * 1130;
            float variety = (float)random.NextDouble();
            float length = .8f + (float)random.NextDouble() * 3.2f;
            float dx = ((float)random.NextDouble() - .55f) * 1.5f;
            float dy = -length;
            int stripe = (int)MathF.Floor(x / (1600f / 12));
            if ((stripe & 1) != 0) { dx += .65f; dy *= -.67f; }
            Color blade = variety switch
            {
                < .15f => Ink(28, 63, 27, 95),
                < .40f => Ink(39, 84, 28, 98),
                < .70f => Ink(132, 155, 62, 83),
                < .93f => Ink(170, 183, 81, 64),
                _ => Ink(218, 199, 112, 77)
            };
            ds.DrawLine(new(x, y), new(x + dx, y + dy), blade, .50f + variety * .54f);
            if (i % 4 == 0)
                ds.DrawLine(new(x + .8f, y), new(x + dx + 1.2f, y + dy * .76f), Ink(21, 61, 27, 58), .55f);
        }
        // Narrow darker grass gutters distinguish the two physical rebounding sidelines.
        ds.FillRectangle(new Rect(-108, -64, 1816, 34), Ink(10, 36, 20, 32));
        ds.FillRectangle(new Rect(-108, 1030, 1816, 34), Ink(10, 36, 20, 44));
    }

    private static void DrawMarkings(CanvasDrawingSession ds)
    {
        var chalk = Ink(244, 245, 218, 230);
        var chalkShadow = Ink(33, 62, 26, 100);
        ds.DrawRectangle(new Rect(.8, .8, 1598.4, 998.4), chalkShadow, 5.5f);
        ds.DrawRectangle(new Rect(1.5, 1.5, 1597, 997), chalk, 3.6f);
        ds.DrawLine(800, 2, 800, 998, chalk, 3.3f);
        ds.DrawCircle(new(800, 500), 139, chalk, 3.2f);
        ds.FillCircle(new(800, 500), 4.5f, chalk);
        for (int side = 0; side < 2; side++)
        {
            float x = side == 0 ? 0 : 1600;
            float direction = side == 0 ? 1 : -1;
            ds.DrawRectangle(new Rect(side == 0 ? 1.5 : 1350, 206, 248.5, 588), chalk, 3.2f);
            ds.DrawRectangle(new Rect(side == 0 ? 1.5 : 1496, 300, 102.5, 400), chalk, 3.2f);
            ds.FillCircle(new(x + direction * 176, 500), 4.5f, chalk);
            using var arcClip = ds.CreateLayer(1, new Rect(side == 0 ? 251 : 1170, 344, 179, 312));
            ds.DrawCircle(new(x + direction * 176, 500), 139, chalk, 3.2f);
        }
        using (ds.CreateLayer(1, new Rect(2, 2, 1596, 996)))
        {
            ds.DrawCircle(new(0, 0), 27, chalk, 2.8f);
            ds.DrawCircle(new(1600, 0), 27, chalk, 2.8f);
            ds.DrawCircle(new(0, 1000), 27, chalk, 2.8f);
            ds.DrawCircle(new(1600, 1000), 27, chalk, 2.8f);
        }
        // Individual paint flecks soften perfectly mathematical line edges.
        var random = new Random(8901);
        for (int i = 0; i < 1900; i++)
        {
            float x = (float)random.NextDouble() * 1600;
            float y = (float)random.NextDouble() * 1000;
            float nearestLine = Math.Min(Math.Min(x, 1600 - x), Math.Min(y, 1000 - y));
            if (nearestLine > 5 && Math.Abs(x - 800) > 3) continue;
            ds.FillCircle(new(x, y), .75f, Ink(222, 225, 191, 135));
        }
    }

    private static void DrawGoal(CanvasDrawingSession ds, bool right, bool foreground)
    {
        float x = right ? 1600 : 0;
        float sign = right ? 1 : -1;
        float depth = FootballGame.GoalDepth * 1000;
        float top = 500 - FootballGame.GoalHalfWidth * 1000;
        float bottom = 500 + FootballGame.GoalHalfWidth * 1000;
        var frontTop = new Vector2(x, top);
        var frontBottom = new Vector2(x, bottom);
        var backTop = new Vector2(x + sign * depth, top + 18);
        var backBottom = new Vector2(x + sign * depth, bottom - 18);
        var lift = new Vector2(-10, -17);
        if (!foreground)
        {
            var shadow = Ink(11, 24, 14, 95);
            ds.DrawLine(frontTop + new Vector2(22, 32), frontBottom + new Vector2(22, 32), shadow, 7);
            ds.DrawLine(frontTop + new Vector2(22, 32), backTop + new Vector2(14, 18), shadow, 5);
            ds.DrawLine(frontBottom + new Vector2(22, 32), backBottom + new Vector2(14, 18), shadow, 5);
            using var netShape = Polygon(ds, frontTop, backTop, backBottom, frontBottom);
            ds.FillGeometry(netShape, Ink(203, 220, 199, 21));
            using (ds.CreateLayer(1, netShape))
            {
                for (float y = top - 20; y < bottom + 20; y += 17)
                {
                    ds.DrawLine(new(x - sign * 10, y), new(x + sign * (depth + 10), y - 6), Ink(27, 48, 29, 100), 2.6f);
                    ds.DrawLine(new(x - sign * 10, y - 1), new(x + sign * (depth + 10), y - 7), Ink(229, 235, 211, 180), 1.05f);
                }
                for (float d = 8; d < depth; d += 15)
                    ds.DrawLine(new(x + sign * d, top), new(x + sign * d, bottom), Ink(240, 240, 217, 150), 1.05f);
            }
            ds.DrawLine(backTop, backBottom, Ink(185, 200, 174), 4);
            ds.DrawLine(frontTop, backTop, Ink(222, 228, 201), 4);
            ds.DrawLine(frontBottom, backBottom, Ink(222, 228, 201), 4);
            return;
        }
        // Narrow upper net roof and rounded tubular frame, with sunlit edges.
        for (float y = top; y <= bottom; y += 17)
        {
            float across = (y - top) / (bottom - top);
            ds.DrawLine(new Vector2(x, y) + lift, Vector2.Lerp(backTop, backBottom, across),
                Ink(237, 239, 220, 120), .9f);
        }
        for (float along = .18f; along < 1; along += .18f)
            ds.DrawLine(Vector2.Lerp(frontTop + lift, backTop, along),
                Vector2.Lerp(frontBottom + lift, backBottom, along), Ink(237, 239, 220, 105), .9f);
        Frame(ds, frontTop, frontTop + lift, 7);
        Frame(ds, frontBottom, frontBottom + lift, 7);
        Frame(ds, frontTop + lift, frontBottom + lift, 7);
        Frame(ds, frontTop + lift, backTop, 4);
        Frame(ds, frontBottom + lift, backBottom, 4);
        ds.FillCircle(frontTop, 5.2f, Ink(225, 230, 207));
        ds.FillCircle(frontBottom, 5.2f, Ink(225, 230, 207));
    }

    private static void Frame(CanvasDrawingSession ds, Vector2 a, Vector2 b, float width)
    {
        ds.DrawLine(a, b, Ink(49, 65, 57), width + 2);
        ds.DrawLine(a, b, Ink(201, 212, 192), width);
        ds.DrawLine(a + new Vector2(-1, -1), b + new Vector2(-1, -1), Ink(255, 255, 238), width * .33f);
    }

    private static void DrawSoftShadow(CanvasDrawingSession ds, Vector2 center, float radiusX, float radiusY, byte opacity)
    {
        using var shadow = new CanvasRadialGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = Ink(7, 20, 12, opacity) },
            new() { Position = .54f, Color = Ink(7, 20, 12, (byte)(opacity * .94f)) },
            new() { Position = .80f, Color = Ink(7, 20, 12, (byte)(opacity * .48f)) },
            new() { Position = 1, Color = Ink(7, 20, 12, 0) }
        ]) { Center = center, RadiusX = radiusX, RadiusY = radiusY };
        ds.FillEllipse(center, radiusX, radiusY, shadow);
    }

    private static CanvasGeometry Polygon(CanvasDrawingSession ds, params Vector2[] points)
    {
        using var path = new CanvasPathBuilder(ds.Device);
        path.BeginFigure(points[0]);
        for (int i = 1; i < points.Length; i++) path.AddLine(points[i]);
        path.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(path);
    }

    private static Color Ink(byte r, byte g, byte b, byte alpha = 255) => Color.FromArgb(alpha, r, g, b);
    public void Dispose()
    {
        lock (_pitchGate)
        {
            _disposed = true;
            _pitch?.Dispose();
            _pitch = null;
        }
    }
}
