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
    private BitmapAssetSet? _duckHuntImages;
    private CanvasImageBrush? _duckHuntPhotoBrush;
    private CanvasGeometry? _duckHuntBodyClip, _duckHuntRearLegClip, _duckHuntNearLegClip;
    private bool _duckHuntArtworkPublished;
    internal bool DuckHuntArtworkReady => _duckHuntArtworkPublished &&
        _duckHuntImages?.Image("mallard-photo.png") is not null;
    private string? DuckHuntArtworkError => _duckHuntImages?.Error ??
        _duckHuntImages?.Errors.GetValueOrDefault("mallard-photo.png");

    private BitmapAssetSet GetDuckHuntImages(CanvasDevice device)
    {
        if (_duckHuntImages is null || _duckHuntImages.Device != device)
        {
            DisposeDuckHuntArtwork();
            _duckHuntImages = new(device, Path.Combine(AppContext.BaseDirectory, "Assets", "DuckHunt"),
                ["mallard-photo.png"]);
        }
        return _duckHuntImages;
    }

    internal async Task EnsureDuckHuntResourcesAsync(CanvasDevice device)
    {
        BitmapAssetSet images;
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); images = GetDuckHuntImages(device); }
        await images.EnsureLoadedAsync().ConfigureAwait(false);
        lock (_gate)
            if (!_disposed && ReferenceEquals(images, _duckHuntImages)) PrepareDuckHuntResources(device);
    }

    private bool PrepareDuckHuntResources(CanvasDevice device)
    {
        var images = GetDuckHuntImages(device);
        if (!images.IsLoaded) return false;
        if (!_duckHuntArtworkPublished)
        {
            PrepareDuckHuntWalkRig(device);
            _duckHuntArtworkPublished = true;
            if (_boardSession.Screen == BoardScreen.DuckHunt) InvalidateBoardArtworkSurface();
            InvalidateMenuPreviewSurface();
        }
        return DuckHuntArtworkReady;
    }

    private void DisposeDuckHuntArtwork()
    {
        DisposeDuckHuntFeathers();
        _duckHuntPhotoBrush?.Dispose();
        _duckHuntBodyClip?.Dispose();
        _duckHuntRearLegClip?.Dispose();
        _duckHuntNearLegClip?.Dispose();
        _duckHuntPhotoBrush = null;
        _duckHuntBodyClip = _duckHuntRearLegClip = _duckHuntNearLegClip = null;
        _duckHuntImages?.Dispose();
        _duckHuntImages = null;
        _duckHuntArtworkPublished = false;
    }
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
        UpdateDuckHuntRound(now);
        var state = _boardSession.DuckHuntState;
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
        DrawBoardAspectText(drawing, $"HITS {state.Hits} / {DuckHuntGame.HitsToWin}",
            new Rect(350, 135, 300, 45), ink, caption);
        DrawBoardAspectText(drawing, DuckHuntAudioHint, new Rect(120, 190, 760, 45), ink, caption);
        DrawDuckHuntWalkingMallard(drawing, state, PaintBoardAspect());
        DrawDuckHuntFeathers(drawing, PaintBoardAspect());
        DrawDuckHuntMeasurement(drawing);
        if (!_boardSession.DuckHuntMicrophoneMenuOpen)
        {
            string status = state.Phase switch
            {
                DuckHuntPhase.PlayerWon => "YOU WIN!",
                DuckHuntPhase.DuckWon => "DUCK WINS!",
                _ => "Hit the duck 10 times before it escapes."
            };
            DrawBoardAspectText(drawing, status, new Rect(120, 730, 760, 55), ink,
                state.RoundFinished ? heading : caption);
            if (state.RoundFinished)
                DrawBoardAspectText(drawing, "Hold PLAY AGAIN for another round.",
                    new Rect(120, 785, 760, 45), ink, caption);
        }
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
        if (!_boardSession.DuckHuntDrawerOpen)
            foreach (var button in buttons)
                if (button.Id == "duck-hunt-replay")
                    DrawDrawerButton(drawing, button, false, PaintBoardAspect(), _holdControlBrightness);
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
            if (!PrepareDuckHuntResources(drawing.Device))
            {
                DrawArtworkLoading(drawing, new Rect(0, 0, width, height));
                return;
            }
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
        if (!PrepareDuckHuntResources(drawing.Device)) return;
        using var clip = CanvasGeometry.CreateRoundedRectangle(drawing.Device, inside, radius, radius);
        using var layer = drawing.CreateLayer(1, clip);
        DrawDuckHuntMallard(drawing,
            new Rect(rect.X + rect.Width * .46, rect.Y + rect.Height * .04, rect.Width * .52, rect.Height * .91),
            PaintBoardAspect());
    }

    private void DrawDuckHuntMallard(CanvasDrawingSession drawing, Rect bounds, double aspect)
    {
        if (_duckHuntImages?.Image("mallard-photo.png") is not { } image) return;
        // Source pixels retain their natural proportions in physical board space.
        double width = Math.Min(bounds.Width, bounds.Height / aspect * image.Size.Width / image.Size.Height);
        double height = width * aspect * image.Size.Height / image.Size.Width;
        var destination = new Rect(bounds.X + (bounds.Width - width) / 2,
            bounds.Y + (bounds.Height - height) / 2, width, height);
        drawing.DrawImage(image, destination, new Rect(0, 0, image.Size.Width, image.Size.Height),
            1, CanvasImageInterpolation.HighQualityCubic);
    }

    private void PrepareDuckHuntWalkRig(CanvasDevice device)
    {
        if (_duckHuntPhotoBrush is not null || _duckHuntImages?.Image("mallard-photo.png") is not { } image) return;
        _duckHuntPhotoBrush = new CanvasImageBrush(device, image)
        {
            Interpolation = CanvasImageInterpolation.HighQualityCubic
        };
        // These source-space contours follow the belly around the two leg roots.
        // The legs tuck behind the original feather edge rather than shearing a
        // rectangular strip through the photograph. The alpha silhouette remains
        // the image's own, and the three reusable geometries never copy pixels.
        _duckHuntBodyClip = CanvasGeometry.CreatePolygon(device,
        [
            new(0, 0), new(1312, 0), new(1312, 1199), new(900, 1199),
            new(900, 930), new(850, 930), new(838, 907), new(802, 900),
            new(770, 899), new(754, 913), new(640, 918), new(621, 895),
            new(584, 889), new(552, 884), new(538, 899), new(0, 1199)
        ]);
        _duckHuntRearLegClip = CanvasGeometry.CreatePolygon(device,
        [
            new(553, 879), new(624, 885), new(655, 1037), new(679, 1074),
            new(610, 1124), new(566, 1154), new(329, 1156), new(328, 1029), new(526, 1006)
        ]);
        _duckHuntNearLegClip = CanvasGeometry.CreatePolygon(device,
        [
            new(767, 892), new(841, 899), new(879, 1199), new(552, 1199),
            new(565, 1141), new(636, 1091), new(729, 1078)
        ]);
        // The photographed feet overlap slightly. Give that small toe seam to
        // the nearer foot so an original pixel cannot move with both legs.
        var rear = _duckHuntRearLegClip;
        _duckHuntRearLegClip = rear.CombineWith(_duckHuntNearLegClip, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        rear.Dispose();
    }

    internal void DrawDuckHuntWalkingMallard(CanvasDrawingSession drawing, DuckHuntSnapshot duck, double aspect)
    {
        if (!duck.DuckVisible || _duckHuntPhotoBrush is not { } brush || _duckHuntBodyClip is null ||
            _duckHuntRearLegClip is null || _duckHuntNearLegClip is null) return;
        var bounds = DuckHuntLayout.PhotoBounds(duck, aspect);
        float width = (float)bounds.Width * BoardSurfaceSize;
        float height = (float)bounds.Height * BoardSurfaceSize;
        // The source faces left. A short eased squeeze through its centre turns
        // it around without moving its horizontal path or shifting the hit pose.
        float facing = (float)duck.Facing;
        float scaleX = -MathF.CopySign(MathF.Max(.10f, MathF.Abs(facing)), facing);
        var previous = drawing.Transform;
        var pose = Matrix3x2.CreateTranslation(-656, -599.5f) *
            Matrix3x2.CreateScale(width / 1312 * scaleX, height / 1199) *
            Matrix3x2.CreateTranslation((float)duck.U * BoardSurfaceSize, (float)duck.V * BoardSurfaceSize) * previous;
        try
        {
            DrawLeg(_duckHuntRearLegClip, true);
            DrawLeg(_duckHuntNearLegClip, false);
            drawing.Transform = pose;
            drawing.FillGeometry(_duckHuntBodyClip, brush);
        }
        finally { drawing.Transform = previous; }

        void DrawLeg(CanvasGeometry geometry, bool rear)
        {
            var foot = DuckHuntLayout.FootPose(duck, rear);
            var pivot = new Vector2((float)foot.PivotU * 1312, (float)foot.PivotV * 1199);
            drawing.Transform = Matrix3x2.CreateRotation((float)foot.Angle, pivot) *
                Matrix3x2.CreateTranslation(0, (float)foot.Lift * 1199) * pose;
            drawing.FillGeometry(geometry, brush);
        }
    }

    internal void DrawDuckHuntMeasurement(CanvasDrawingSession drawing)
    {
        var measurement = DuckHuntMeasurement;
        BoardAim? anchor = measurement.OldDetectedXY ?? measurement.NewDetectedXY ?? measurement.LiveDetectedXY;
        if (anchor is not { } old) return;
        double aspect = PaintBoardAspect();
        // Board units are normalized independently in each axis. Scale the local
        // y axis into the same physical units as x before drawing a true circle.
        // Pixel sizes use the projected board raster, not 1000-unit layout pixels.
        float pixel = BoardSurfaceSize / Math.Max(1, _boardRasterPixels.Width);
        var previous = drawing.Transform;
        using var clip = drawing.CreateLayer(1, new Rect(0, 0, BoardSurfaceSize, BoardSurfaceSize));
        drawing.Transform = Matrix3x2.CreateScale(1, (float)aspect) *
            Matrix3x2.CreateTranslation((float)old.U * BoardSurfaceSize, (float)old.V * BoardSurfaceSize) * previous;
        try
        {
            var black = Color.FromArgb(255, 0, 0, 0);
            if (measurement.HasCompletedPair && measurement.NewDetectedXY is { } current)
            {
                double dx = current.U - old.U, dy = (current.V - old.V) / aspect;
                float radius = (float)Math.Sqrt(dx * dx + dy * dy) * BoardSurfaceSize;
                if (radius > 0) drawing.DrawCircle(Vector2.Zero, radius, black, 5 * pixel);
            }
            drawing.FillCircle(Vector2.Zero, 10 * pixel, black);
        }
        finally { drawing.Transform = previous; }
    }
}
