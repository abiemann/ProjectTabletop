using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private static readonly Color DuckHuntBackground = Color.FromArgb(255, 218, 223, 213);
    public DuckHuntSnapshot DuckHuntState { get { lock (_gate) return _boardSession.DuckHuntState; } }
    public bool DuckHuntMicrophoneMenuOpen { get { lock (_gate) return _boardSession.DuckHuntMicrophoneMenuOpen; } }
    public event Action<string?>? DuckHuntMicrophoneSelected;
    public event Action? DuckHuntMicrophonesRequested;

    public void SetDuckHuntMicrophones(IReadOnlyList<DuckHuntMicrophoneChoice> choices,
        string? selectedDeviceId, bool loading, string? error = null)
    {
        lock (_gate) _boardSession.SetDuckHuntMicrophones(choices, selectedDeviceId, loading, error, _blackjackClock());
    }

    public void ShowDuckHunt()
    {
        lock (_gate)
        {
            CancelBoardReveal();
            _blackOutput = false;
            _boardSession.ShowDuckHunt(_blackjackClock());
            SyncPhotoCopySession();
        }
    }

    public bool SubmitDuckHuntShot(BoardAim? aim, DateTimeOffset shotTime)
    {
        lock (_gate) return _boardSession.TryDuckHuntShot(aim, shotTime, PaintBoardAspect());
    }

    public bool ActivateDuckHuntAt(double u, double v)
    {
        lock (_gate)
        {
            if (_boardSession.Screen != BoardScreen.DuckHunt) return false;
            var button = _boardSession.Buttons.FirstOrDefault(button => button.Enabled && button.Bounds.Contains(u, v));
            return button is not null && _boardSession.ActivateButton(button.Id, _blackjackClock());
        }
    }

    private void DrawDuckHuntBoard(CanvasDrawingSession drawing, DateTimeOffset now)
    {
        _boardSession.TickDuckHunt(now);
        var duck = _boardSession.DuckHuntState;
        drawing.Clear(DuckHuntBackground);
        using var heading = new CanvasTextFormat
        {
            FontFamily = "Segoe UI", FontWeight = FontWeights.SemiBold, FontSize = 35,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center
        };
        using var caption = new CanvasTextFormat
        {
            FontFamily = "Segoe UI", FontSize = 21,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center
        };
        var ink = Color.FromArgb(255, 39, 60, 51);
        DrawBoardAspectText(drawing, "DUCK HUNT", new Rect(200, 45, 600, 80), ink, heading);
        DrawBoardAspectText(drawing, $"{duck.Hits} HIT{(duck.Hits == 1 ? "" : "S")}",
            new Rect(350, 135, 300, 45), ink, caption);
        DrawBoardAspectText(drawing, DuckHuntAudioHint, new Rect(120, 190, 760, 45), ink, caption);
        if (duck.DuckVisible) DrawDuckHuntMallard(drawing, duck, PaintBoardAspect());
        if (!_boardSession.DuckHuntMicrophoneMenuOpen)
            DrawBoardAspectText(drawing, "Aim the red dot. Click to shoot.", new Rect(200, 745, 600, 55), ink, caption);
        DrawDuckHuntControls(drawing, now);
    }

    private bool HasDuckHuntDrawerAnimation(DateTimeOffset now)
    {
        _boardSession.TickDuckHunt(now);
        return _boardSession.DuckHuntDrawerOpen && _boardSession.GetDuckHuntDrawerProgress(now) < 1;
    }

    private int DuckHuntDrawerVisualFrame(DateTimeOffset now) => _boardSession.DuckHuntDrawerOpen
        ? 1 + (int)Math.Round(_boardSession.GetDuckHuntDrawerProgress(now) * 100000) : 0;

    private void DrawDuckHuntControls(CanvasDrawingSession drawing, DateTimeOffset now)
    {
        SyncHoldControlExposure();
        var buttons = BoardControlsForRendering;
        if (buttons.Count == 0) return;
        using var fade = CreateBoardControlsFadeLayer(drawing);
        if (_boardSession.DuckHuntMicrophoneMenuOpen)
        {
            var ink = Color.FromArgb(255, 39, 60, 51);
            drawing.FillRoundedRectangle(new Rect(350, 306, 605, 545), 18, 18,
                Color.FromArgb(255, 201, 213, 205));
            using var title = new CanvasTextFormat
            {
                FontFamily = "Segoe UI", FontSize = 20, WordWrapping = CanvasWordWrapping.Wrap,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center
            };
            string status = _boardSession.DuckHuntMicrophonesLoading ? "Finding microphones…" :
                _boardSession.DuckHuntMicrophonesError is { } error ? error :
                "CURRENT: " + _boardSession.DuckHuntSelectedMicrophoneLabel;
            DrawBoardAspectText(drawing, status, new Rect(370, 315, 565, 95), ink, title);
            if (_boardSession.DuckHuntMicrophonePageCount > 1)
                DrawBoardAspectText(drawing, $"{_boardSession.DuckHuntMicrophonePage + 1} / {_boardSession.DuckHuntMicrophonePageCount}",
                    new Rect(200, 510, 155, 45), ink, title);
        }
        DrawBoardDrawerControls(drawing, buttons, [], [], _boardSession.DuckHuntDrawerOpen,
            (float)_boardSession.GetDuckHuntDrawerProgress(now), PaintBoardAspect(), _holdControlBrightness);
        if (_boardSession.DuckHuntMicrophoneMenuOpen && _boardSession.GetDuckHuntDrawerProgress(now) == 1)
        {
            var chosen = buttons.FirstOrDefault(button => button.Id == _boardSession.DuckHuntSelectedMicrophoneButtonId);
            if (chosen is not null)
            {
                var b = chosen.Bounds;
                drawing.DrawRoundedRectangle(new Rect(b.X * BoardSurfaceSize + 3, b.Y * BoardSurfaceSize + 3,
                    b.Width * BoardSurfaceSize - 6, b.Height * BoardSurfaceSize - 6), 16, 16,
                    Color.FromArgb(255, 44, 114, 82), 3);
            }
            var mic = DuckHuntLayout.MicrophoneBounds;
            float x = (float)(mic.X + mic.Width / 2) * BoardSurfaceSize, y = (float)mic.Y * BoardSurfaceSize + 20;
            drawing.DrawLine(new(x - 5, y + 3), new(x, y - 2), Color.FromArgb(255, 0, 0, 0), 2);
            drawing.DrawLine(new(x, y - 2), new(x + 5, y + 3), Color.FromArgb(255, 0, 0, 0), 2);
        }
    }

    public void DrawDuckHuntPreview(CanvasDrawingSession drawing, float width, float height)
    {
        drawing.Clear(DuckHuntBackground);
        if (width <= 0 || height <= 0) return;
        lock (_gate)
        {
            if (_disposed || _boardSession.Screen != BoardScreen.DuckHunt) return;
            double aspect = PaintBoardAspect();
            float drawWidth = (float)Math.Min(width, height * aspect), drawHeight = (float)(drawWidth / aspect);
            var previous = drawing.Transform;
            drawing.Transform = Matrix3x2.CreateScale(drawWidth / BoardSurfaceSize, drawHeight / BoardSurfaceSize) *
                Matrix3x2.CreateTranslation((width - drawWidth) / 2, (height - drawHeight) / 2) * previous;
            try { DrawDuckHuntBoard(drawing, _blackjackClock()); }
            finally { drawing.Transform = previous; }
        }
    }

    private void DrawDuckHuntMenuPreview(CanvasDrawingSession drawing, Rect rect, Rect inside, float radius)
    {
        using var clip = CanvasGeometry.CreateRoundedRectangle(drawing.Device, inside, radius, radius);
        using var layer = drawing.CreateLayer(1, clip);
        var previous = drawing.Transform;
        double aspect = PaintBoardAspect();
        float scale = (float)Math.Min(rect.Width * .53 / 380, rect.Height * .75 / (240 * aspect));
        drawing.Transform = Matrix3x2.CreateScale(scale) *
            Matrix3x2.CreateTranslation((float)(rect.X + rect.Width * .73), (float)(rect.Y + rect.Height * .48)) * previous;
        try { DrawDuckHuntMallard(drawing, new(DuckHuntPhase.Aiming, 0, 0, 0, 0, 0, 0), aspect); }
        finally { drawing.Transform = previous; }
    }

    // Geometry is drawn at destination resolution, so the silhouette and fine feather
    // strokes stay sharp on a 4K projector. No red decoration can become a false aim dot.
    private static void DrawDuckHuntMallard(CanvasDrawingSession drawing, DuckHuntSnapshot duck, double aspect)
    {
        var previous = drawing.Transform;
        drawing.Transform = Matrix3x2.CreateRotation((float)duck.Rotation) *
            Matrix3x2.CreateScale(1, (float)aspect) *
            Matrix3x2.CreateTranslation((float)duck.U * BoardSurfaceSize, (float)duck.V * BoardSurfaceSize) * previous;
        try
        {
            DrawDuckHuntTriangle(drawing, DuckHuntLayout.Tail, Color.FromArgb(255, 57, 67, 68));
            FillDuckHuntEllipse(drawing, DuckHuntLayout.Body, Color.FromArgb(255, 224, 230, 221), Color.FromArgb(255, 89, 107, 104));
            FillDuckHuntEllipse(drawing, DuckHuntLayout.Neck, Color.FromArgb(255, 38, 126, 96), Color.FromArgb(255, 13, 55, 46));
            FillDuckHuntEllipse(drawing, DuckHuntLayout.Head, Color.FromArgb(255, 48, 158, 112), Color.FromArgb(255, 13, 62, 49));
            // Narrow white neck ring, kept inside the shared silhouette.
            drawing.DrawLine(new Vector2(45, -14), new Vector2(92, -4), Color.FromArgb(255, 242, 245, 226), 6);
            FillDuckHuntEllipse(drawing, new(-.12, .004, .186, .088),
                Color.FromArgb(255, 140, 157, 151), Color.FromArgb(255, 51, 68, 65));
            using (var wing = new CanvasPathBuilder(drawing.Device))
            {
                wing.BeginFigure(-105, 45);
                wing.AddCubicBezier(new(-54, 7), new(10, 7), new(50, 38));
                wing.AddCubicBezier(new(2, 79), new(-60, 93), new(-105, 45));
                wing.EndFigure(CanvasFigureLoop.Closed);
                using var geometry = CanvasGeometry.CreatePath(wing);
                drawing.FillGeometry(geometry, Color.FromArgb(255, 70, 89, 83));
            }
            // A mallard's blue speculum and layered flight feathers.
            drawing.DrawLine(new(-73, 53), new(20, 39), Color.FromArgb(255, 210, 218, 212), 14);
            drawing.DrawLine(new(-72, 53), new(19, 39), Color.FromArgb(255, 35, 77, 126), 8);
            for (int i = 0; i < 9; i++)
            {
                float x = -91 + i * 13;
                drawing.DrawLine(new(x, 57), new(x + 20, 69 - i * 1.6f), Color.FromArgb(155, 175, 191, 181), 1.2f);
            }
            DrawDuckHuntTriangle(drawing, DuckHuntLayout.Bill, Color.FromArgb(255, 224, 207, 75));
            drawing.DrawLine(new(140, -54), new(180, -55), Color.FromArgb(255, 90, 98, 41), 1.6f);
            drawing.FillCircle(new(151, -58), 1.8f, Color.FromArgb(255, 51, 68, 39));
            drawing.FillCircle(new(113, -77), 5, Color.FromArgb(255, 8, 19, 17));
            drawing.FillCircle(new(114.5f, -78.5f), 1.4f, Color.FromArgb(255, 247, 250, 239));
        }
        finally { drawing.Transform = previous; }
    }

    private static void FillDuckHuntEllipse(CanvasDrawingSession drawing, BoardRect bounds, Color light, Color dark)
    {
        var center = new Vector2((float)(bounds.X + bounds.Width / 2) * BoardSurfaceSize,
            (float)(bounds.Y + bounds.Height / 2) * BoardSurfaceSize);
        float rx = (float)bounds.Width * BoardSurfaceSize / 2, ry = (float)bounds.Height * BoardSurfaceSize / 2;
        using var brush = new CanvasRadialGradientBrush(drawing.Device, light, dark)
        {
            Center = center, OriginOffset = new(-rx * .25f, -ry * .4f), RadiusX = rx * 1.2f, RadiusY = ry * 1.2f
        };
        drawing.FillEllipse(center, rx, ry, brush);
    }

    private static void DrawDuckHuntTriangle(CanvasDrawingSession drawing, BoardAim[] points, Color color)
    {
        using var builder = new CanvasPathBuilder(drawing.Device);
        builder.BeginFigure((float)points[0].U * BoardSurfaceSize, (float)points[0].V * BoardSurfaceSize);
        builder.AddLine((float)points[1].U * BoardSurfaceSize, (float)points[1].V * BoardSurfaceSize);
        builder.AddLine((float)points[2].U * BoardSurfaceSize, (float)points[2].V * BoardSurfaceSize);
        builder.EndFigure(CanvasFigureLoop.Closed);
        using var geometry = CanvasGeometry.CreatePath(builder);
        drawing.FillGeometry(geometry, color);
    }
}
