using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection.Football;

/// <summary>A device-owned, overhead football pitch. All coordinates are mapped
/// from the same physical simulation rectangle used for input.</summary>
internal sealed partial class FootballRenderer : IDisposable
{
    private const float PitchWidth = (float)FootballFieldGeometry.Width;
    private const float PitchHeight = (float)FootballFieldGeometry.Height;
    private const float PitchBitmapWidth = PitchWidth + 224;
    private const float PitchBitmapHeight = PitchHeight + 132;
    private readonly CanvasDevice _device;
    private readonly SoccerBallRenderer _ball;
    private CanvasRadialGradientBrush? _softShadow;
    private readonly object _pitchGate = new();
    private Task? _pitchTask;
    private CanvasRenderTarget? _pitch;
    private BitmapAssetSet? _kickerImages;
    private ShadowEffect? _carShadow;
    private ShadowEffect? _blueCarShadow;
    private bool _disposed;

    internal FootballRenderer(CanvasDevice device)
    {
        _device = device;
        _ball = new(device);
    }

    internal bool PitchReady
    {
        get { lock (_pitchGate) return _pitch is not null && _carShadow is not null && _blueCarShadow is not null; }
    }
    internal bool PitchCompleted { get { lock (_pitchGate) return _pitchTask is { IsCompleted: true }; } }
    internal string? PitchError { get { lock (_pitchGate) return _pitchTask?.Exception?.GetBaseException().Message; } }
    private CanvasRenderTarget? Pitch { get { lock (_pitchGate) return _pitch; } }

    // About 300k antialiased grass strokes are far too slow for a render pass
    // under the compositor lock. Build the pitch and decode the car once on
    // workers; draw callers show their loading state until both are ready.
    internal Task EnsurePitchAsync()
    {
        lock (_pitchGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pitchTask is null)
            {
                _kickerImages = new(_device, Path.Combine(AppContext.BaseDirectory, "Assets", "Football"),
                    ["car-front.png", "car-front-blue.png"]);
                _pitchTask = BuildResourcesAsync(_kickerImages);
                _ = _pitchTask.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
            return _pitchTask;
        }
    }

    private async Task BuildResourcesAsync(BitmapAssetSet images)
    {
        await Task.WhenAll(Task.Run(BuildPitch), images.EnsureLoadedAsync()).ConfigureAwait(false);
        lock (_pitchGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var car = images.Image("car-front.png") ?? throw new InvalidDataException(
                "Football car artwork: " + images.Errors.GetValueOrDefault("car-front.png", "not loaded"));
            var blueCar = images.Image("car-front-blue.png") ?? throw new InvalidDataException(
                "Football blue car artwork: " + images.Errors.GetValueOrDefault("car-front-blue.png", "not loaded"));
            _carShadow = new ShadowEffect
            {
                Source = car, BlurAmount = 14,
                ShadowColor = Ink(16, 27, 18)
            };
            _blueCarShadow = new ShadowEffect
            {
                Source = blueCar, BlurAmount = 14,
                ShadowColor = Ink(16, 27, 18)
            };
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
            double cropWidth = PitchBitmapWidth, cropHeight = cropWidth * 400 / width;
            if (cropHeight > PitchBitmapHeight) { cropHeight = PitchBitmapHeight; cropWidth = cropHeight * width / 400; }
            var crop = new Rect((PitchBitmapWidth - cropWidth) / 2, (PitchBitmapHeight - cropHeight) / 2, cropWidth, cropHeight);
            ds.DrawImage(pitch, new Rect(0, 0, width, 400), crop);
            var center = new Vector2(width * .80f, 178);
            DrawSoftShadow(ds, center + new Vector2(47, 89), 127, 93, 165);
            var carCenter = new Vector2(width * .65f - 50 / scale, 288);
            var thumbnailTransform = ds.Transform;
            // Give the bonnet more presence on the menu card, including its shadow.
            ds.Transform = Matrix3x2.CreateScale(1.4f, carCenter) * thumbnailTransform;
            DrawKickerShadow(ds, FootballKickerStyle.Car, carCenter, -.47f, 115);
            DrawKicker(ds, FootballKickerStyle.Car, carCenter, -.47f, Ink(239, 91, 53), true, false);
            ds.Transform = thumbnailTransform;
            _ball.Draw(ds, center, 112, Quaternion.CreateFromYawPitchRoll(.47f, -.35f, .28f));
        }
        finally { ds.Transform = before; }
    }

    /// <param name="fieldBounds">The playable rectangle, excluding the 0.1-metre-deep goals.</param>
    internal void Draw(CanvasDrawingSession ds, Rect fieldBounds, FootballSnapshot snapshot, double victorySeconds = -1)
    {
        if (fieldBounds.Width <= 0 || fieldBounds.Height <= 0) return;
        var pitch = Pitch;
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale((float)fieldBounds.Width / PitchWidth, (float)fieldBounds.Height / PitchHeight) *
            Matrix3x2.CreateTranslation((float)fieldBounds.X, (float)fieldBounds.Y) * previous;
        try
        {
            if (pitch is not null) ds.DrawImage(pitch, -112, -66);
            else ds.FillRectangle(new Rect(0, 0, PitchWidth, PitchHeight), Ink(66, 105, 39));
            // Goal shadows belong to the turf and remain under the moving ball.
            DrawGoal(ds, false, false);
            DrawGoal(ds, true, false);
            foreach (var kicker in snapshot.Kickers)
            {
                var position = FieldPoint(kicker.Position);
                DrawKickerShadow(ds, kicker.Style, position, kicker.Heading,
                    kicker.Present || snapshot.RecoveringInput || snapshot.Phase == FootballPhase.Finished
                        ? (byte)132 : (byte)28, blueCar: kicker.Index == 1);
            }
            // Seven balls at most: insertion-sort stack indices without allocating
            // or changing the simulation's stable snapshot order. Higher spheres
            // must occlude lower ones when their projected silhouettes cross.
            Span<int> ballOrder = stackalloc int[snapshot.Balls.Count];
            for (int index = 0; index < ballOrder.Length; index++)
            {
                var ball = snapshot.Balls[index];
                int insert = index;
                while (insert > 0)
                {
                    var preceding = snapshot.Balls[ballOrder[insert - 1]];
                    if (preceding.Height < ball.Height ||
                        (preceding.Height == ball.Height && preceding.Id <= ball.Id)) break;
                    ballOrder[insert] = ballOrder[insert - 1];
                    insert--;
                }
                ballOrder[insert] = index;
            }
            foreach (int index in ballOrder)
            {
                var ball = snapshot.Balls[index];
                float growth = BallGrowth(ball.Growth);
                if (growth <= 0) continue;
                float lift = Math.Max(0, ball.Height - FootballGame.BallRadius) * 1000;
                var ground = FieldPoint(ball.Position);
                float radius = FootballGame.BallRadius * 1000 * growth * (1 + lift * .00065f);
                float spread = 1 + lift * .0035f;
                var shadowCenter = ground + new Vector2(8 * growth + lift * .51f, 10 * growth + lift * .64f);
                byte shadowOpacity = (byte)Math.Clamp(197 - lift * .24f, 130, 197);
                DrawSoftShadow(ds, shadowCenter, radius * .98f * spread, radius * .79f * spread, shadowOpacity);
                if (lift < 8)
                    DrawSoftShadow(ds, ground + new Vector2(2, 4) * growth, radius * .64f, radius * .38f,
                        (byte)(100 * (1 - lift / 8)));
            }

            // Bars supply a measured heading; fingers and AI turn toward play.
            foreach (var kicker in snapshot.Kickers)
                DrawKicker(ds, kicker.Style, FieldPoint(kicker.Position), kicker.Heading,
                    kicker.Index == 0 ? Ink(239, 91, 53) : Ink(52, 161, 224),
                    kicker.Present || snapshot.RecoveringInput || snapshot.Phase == FootballPhase.Finished,
                    !kicker.IsAi && !kicker.MarkerAnchored, blueCar: kicker.Index == 1);
            foreach (int index in ballOrder)
            {
                var ball = snapshot.Balls[index];
                float growth = BallGrowth(ball.Growth);
                if (growth <= 0) continue;
                float lift = Math.Max(0, ball.Height - FootballGame.BallRadius) * 1000;
                var center = FieldPoint(ball.Position) + new Vector2(-lift * .18f, -lift * .40f);
                float radius = FootballGame.BallRadius * 1000 * growth * (1 + lift * .00065f);
                _ball.Draw(ds, center, radius, ball.Rotation);
            }
            // Elevated crossbars occlude a low ball at the goal mouth.
            DrawGoal(ds, false, true);
            DrawGoal(ds, true, true);
            DrawVictory(ds, snapshot, victorySeconds);
        }
        finally { ds.Transform = previous; }
    }

    private static Vector2 FieldPoint(Vector2 point) =>
        new((point.X + FootballGame.Width / 2) * 1000, (point.Y + FootballGame.Height / 2) * 1000);

    // Match the simulation radius so growing balls retain natural contact edges.
    private static float BallGrowth(float progress) => Math.Clamp(progress, 0, 1);

    private void BuildPitch()
    {
        var target = new CanvasRenderTarget(_device, PitchBitmapWidth, PitchBitmapHeight, 96);
        bool published = false;
        try
        {
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(Ink(24, 45, 23, 0));
                ds.Transform = Matrix3x2.CreateTranslation(112, 66);
                var turf = new Rect(FootballFieldGeometry.TurfLeft, FootballFieldGeometry.TurfTop,
                    FootballFieldGeometry.TurfWidth, FootballFieldGeometry.TurfHeight);
                float corner = (float)FootballFieldGeometry.TurfCornerRadius;
                using var shape = CanvasGeometry.CreateRoundedRectangle(_device, turf, corner, corner);
                using (ds.CreateLayer(1, shape))
                {
                    ds.FillRectangle(new Rect(-112, -66, PitchBitmapWidth, PitchBitmapHeight), Ink(55, 87, 36));
                    DrawGrass(ds);
                    using var sun = new CanvasLinearGradientBrush(_device,
                    [
                        new() { Position = 0, Color = Ink(228, 219, 114, 38) },
                        new() { Position = .52f, Color = Ink(180, 198, 112, 0) },
                        new() { Position = 1, Color = Ink(5, 24, 19, 35) }
                    ]) { StartPoint = new(0, 0), EndPoint = new(PitchWidth - 100, PitchHeight + 50) };
                    ds.FillRectangle(new Rect(-112, -66, PitchBitmapWidth, PitchBitmapHeight), sun);
                    DrawMarkings(ds);
                }
                ds.DrawRoundedRectangle(turf, corner, corner, Ink(17, 37, 22, 180), 3);
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
            float x = stripe * (PitchWidth / 12);
            bool towardSun = (stripe & 1) == 0;
            ds.FillRectangle(new Rect(x, -66, PitchWidth / 12 + .5f, PitchBitmapHeight),
                towardSun ? Ink(91, 124, 44) : Ink(66, 105, 39));
        }
        // Very low-frequency variations keep the closely cut turf organic.
        for (int i = 0; i < (int)(2400 * FootballGame.Height); i++)
        {
            float x = -110 + (float)random.NextDouble() * (PitchWidth + 220);
            float y = -65 + (float)random.NextDouble() * (PitchHeight + 130);
            float size = 4 + (float)random.NextDouble() * 17;
            ds.FillEllipse(new(x, y), size * 1.5f, size,
                i % 2 == 0 ? Ink(18, 66, 31, 9) : Ink(199, 192, 86, 8));
        }
        for (int i = 0; i < (int)(235000 * FootballGame.Height); i++)
        {
            float x = -110 + (float)random.NextDouble() * (PitchWidth + 220);
            float y = -65 + (float)random.NextDouble() * (PitchHeight + 130);
            float variety = (float)random.NextDouble();
            float length = .8f + (float)random.NextDouble() * 3.2f;
            float dx = ((float)random.NextDouble() - .55f) * 1.5f;
            float dy = -length;
            int stripe = (int)MathF.Floor(x / (PitchWidth / 12));
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
        ds.FillRectangle(new Rect(-108, -64, PitchWidth + 216, 34), Ink(10, 36, 20, 32));
        ds.FillRectangle(new Rect(-108, PitchHeight + 30, PitchWidth + 216, 34), Ink(10, 36, 20, 44));
    }

    private static void DrawMarkings(CanvasDrawingSession ds)
    {
        var chalk = Ink(244, 245, 218, 230);
        var chalkShadow = Ink(33, 62, 26, 100);
        ds.DrawRectangle(new Rect(.8, .8, PitchWidth - 1.6, PitchHeight - 1.6), chalkShadow, 5.5f);
        ds.DrawRectangle(new Rect(1.5, 1.5, PitchWidth - 3, PitchHeight - 3), chalk, 3.6f);
        ds.DrawLine(PitchWidth / 2, 2, PitchWidth / 2, PitchHeight - 2, chalk, 3.3f);
        ds.DrawCircle(new(PitchWidth / 2, PitchHeight / 2), FootballGame.CenterCircleRadius * 1000, chalk, 3.2f);
        ds.FillCircle(new(PitchWidth / 2, PitchHeight / 2), 4.5f, chalk);
        for (int side = 0; side < 2; side++)
        {
            float x = side == 0 ? 0 : PitchWidth;
            float direction = side == 0 ? 1 : -1;
            ds.DrawRectangle(new Rect(side == 0 ? 1.5 : PitchWidth - 250, PitchHeight / 2 - 294, 248.5, 588), chalk, 3.2f);
            ds.DrawRectangle(new Rect(side == 0 ? 1.5 : PitchWidth - 104, PitchHeight / 2 - 200, 102.5, 400), chalk, 3.2f);
            ds.FillCircle(new(x + direction * 176, PitchHeight / 2), 4.5f, chalk);
            using var arcClip = ds.CreateLayer(1, new Rect(side == 0 ? 251 : PitchWidth - 430, PitchHeight / 2 - 156, 179, 312));
            ds.DrawCircle(new(x + direction * 176, PitchHeight / 2), 139, chalk, 3.2f);
        }
        using (ds.CreateLayer(1, new Rect(2, 2, PitchWidth - 4, PitchHeight - 4)))
        {
            ds.DrawCircle(new(0, 0), 27, chalk, 2.8f);
            ds.DrawCircle(new(PitchWidth, 0), 27, chalk, 2.8f);
            ds.DrawCircle(new(0, PitchHeight), 27, chalk, 2.8f);
            ds.DrawCircle(new(PitchWidth, PitchHeight), 27, chalk, 2.8f);
        }
        // Individual paint flecks soften perfectly mathematical line edges.
        var random = new Random(8901);
        for (int i = 0; i < 1900; i++)
        {
            float x = (float)random.NextDouble() * PitchWidth;
            float y = (float)random.NextDouble() * PitchHeight;
            float nearestLine = Math.Min(Math.Min(x, PitchWidth - x), Math.Min(y, PitchHeight - y));
            if (nearestLine > 5 && Math.Abs(x - PitchWidth / 2) > 3) continue;
            ds.FillCircle(new(x, y), .75f, Ink(222, 225, 191, 135));
        }
    }

    private static void DrawGoal(CanvasDrawingSession ds, bool right, bool foreground)
    {
        float x = right ? PitchWidth : 0;
        float sign = right ? 1 : -1;
        float depth = FootballGame.GoalDepth * 1000;
        float top = PitchHeight / 2 - FootballGame.GoalHalfWidth * 1000;
        float bottom = PitchHeight / 2 + FootballGame.GoalHalfWidth * 1000;
        var frontTop = new Vector2(x, top);
        var frontBottom = new Vector2(x, bottom);
        var backTop = new Vector2(x + sign * depth, top + 18);
        var backBottom = new Vector2(x + sign * depth, bottom - 18);
        var lift = new Vector2(-10, -17);
        if (!foreground)
        {
            var shadow = Ink(11, 24, 14, 95);
            // Keep both goal shadows behind their own line, including when
            // board orientation puts the logical left goal on the user's right.
            var frontShadow = new Vector2(sign * 22, 32);
            var backShadow = new Vector2(sign * 14, 18);
            ds.DrawLine(frontTop + frontShadow, frontBottom + frontShadow, shadow, 7);
            ds.DrawLine(frontTop + frontShadow, backTop + backShadow, shadow, 5);
            ds.DrawLine(frontBottom + frontShadow, backBottom + backShadow, shadow, 5);
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

    private void DrawSoftShadow(CanvasDrawingSession ds, Vector2 center, float radiusX, float radiusY, byte opacity)
    {
        // Every ball and kicker shares the same material; placement and opacity
        // change sequentially without allocating a brush for each shadow.
        var shadow = _softShadow ??= new CanvasRadialGradientBrush(_device,
        [
            new() { Position = 0, Color = Ink(7, 20, 12, 255) },
            new() { Position = .54f, Color = Ink(7, 20, 12, 240) },
            new() { Position = .80f, Color = Ink(7, 20, 12, 122) },
            new() { Position = 1, Color = Ink(7, 20, 12, 0) }
        ]);
        shadow.Center = center;
        shadow.RadiusX = radiusX;
        shadow.RadiusY = radiusY;
        shadow.Opacity = opacity / 255f;
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
            _ball.Dispose();
            _softShadow?.Dispose();
            _softShadow = null;
            _confettiPaper?.Dispose();
            _confettiPaper = null;
            _carShadow?.Dispose();
            _carShadow = null;
            _blueCarShadow?.Dispose();
            _blueCarShadow = null;
            _kickerImages?.Dispose();
            _kickerImages = null;
            _pitch?.Dispose();
            _pitch = null;
        }
    }
}
