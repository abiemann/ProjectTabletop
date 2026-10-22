using System.Numerics;
using Microsoft.Graphics.Canvas;
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
            _duckHuntArtworkPublished = true;
            if (_boardSession.Screen == BoardScreen.DuckHunt) InvalidateBoardArtworkSurface();
            InvalidateMenuPreviewSurface();
        }
        return DuckHuntArtworkReady;
    }

    private void DisposeDuckHuntArtwork()
    {
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
        _boardSession.TickDuckHunt(now);
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
        DrawBoardAspectText(drawing, "AIM MEASUREMENT",
            new Rect(350, 135, 300, 45), ink, caption);
        DrawBoardAspectText(drawing, DuckHuntAudioHint, new Rect(120, 190, 760, 45), ink, caption);
        DrawDuckHuntMallard(drawing, new Rect(320, 280, 360, 400), PaintBoardAspect());
        DrawDuckHuntMeasurement(drawing);
        if (!_boardSession.DuckHuntMicrophoneMenuOpen)
            DrawBoardAspectText(drawing, "Move the red dot. Click to compare positions.", new Rect(120, 745, 760, 55), ink, caption);
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

    internal void DrawDuckHuntMeasurement(CanvasDrawingSession drawing)
    {
        var measurement = DuckHuntMeasurement;
        BoardAim? anchor = measurement.HasCompletedPair ? measurement.OldDetectedXY : measurement.LiveDetectedXY;
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
