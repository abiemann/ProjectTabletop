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
    private BitmapAssetSet? _duckHuntFieldImages;
    private CanvasImageBrush? _duckHuntPhotoBrush;
    private CanvasGeometry? _duckHuntBodyClip, _duckHuntRearLegClip, _duckHuntNearLegClip;
    private bool _duckHuntArtworkPublished;
    private bool _duckHuntFieldPublished;
    internal bool DuckHuntArtworkReady => _duckHuntArtworkPublished &&
        _duckHuntImages?.Image("mallard-photo.png") is not null &&
        _duckHuntImages.Image("arcade-welcome-v1.png") is not null &&
        (_boardSession.Screen != BoardScreen.DuckHunt || _boardSession.CurrentDuckHuntView != DuckHuntView.Hunt ||
            _duckHuntFieldPublished && _duckHuntFieldImages?.Image("hunt-field-v1.png") is not null &&
            _duckHuntFieldImages.Image("flying-mallard-body.png") is not null &&
            _duckHuntFieldImages.Image("flying-mallard-wing.png") is not null);
    private string? DuckHuntArtworkError => _duckHuntImages?.Error ??
        _duckHuntImages?.Errors.Values.FirstOrDefault() ??
        (_boardSession.CurrentDuckHuntView == DuckHuntView.Hunt
            ? _duckHuntFieldImages?.Error ?? _duckHuntFieldImages?.Errors.Values.FirstOrDefault() : null);

    private BitmapAssetSet GetDuckHuntImages(CanvasDevice device)
    {
        if (_duckHuntImages is null || _duckHuntImages.Device != device)
        {
            DisposeDuckHuntArtwork();
            _duckHuntImages = new(device, Path.Combine(AppContext.BaseDirectory, "Assets", "DuckHunt"),
                ["mallard-photo.png", "arcade-welcome-v1.png"]);
        }
        return _duckHuntImages;
    }

    private BitmapAssetSet GetDuckHuntFieldImages(CanvasDevice device)
    {
        if (_duckHuntFieldImages is null || _duckHuntFieldImages.Device != device)
        {
            _duckHuntFieldImages?.Dispose();
            _duckHuntFieldPublished = false;
            _duckHuntFieldImages = new(device, Path.Combine(AppContext.BaseDirectory, "Assets", "DuckHunt"),
                ["hunt-field-v1.png", "flying-mallard-body.png", "flying-mallard-wing.png"]);
        }
        return _duckHuntFieldImages;
    }

    internal async Task EnsureDuckHuntResourcesAsync(CanvasDevice device)
    {
        BitmapAssetSet images;
        BitmapAssetSet? field;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            images = GetDuckHuntImages(device);
            field = _boardSession.Screen == BoardScreen.DuckHunt && _boardSession.CurrentDuckHuntView == DuckHuntView.Hunt
                ? GetDuckHuntFieldImages(device) : null;
        }
        await Task.WhenAll(images.EnsureLoadedAsync(), field?.EnsureLoadedAsync() ?? Task.CompletedTask).ConfigureAwait(false);
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
            PrepareDuckHuntWelcomeTitle(device);
            _duckHuntArtworkPublished = true;
            if (_boardSession.Screen == BoardScreen.DuckHunt) InvalidateBoardArtworkSurface();
            InvalidateMenuPreviewSurface();
        }
        if (_boardSession.Screen == BoardScreen.DuckHunt && _boardSession.CurrentDuckHuntView == DuckHuntView.Hunt)
        {
            // The main menu needs only the arcade preview, never the field.
            var field = GetDuckHuntFieldImages(device);
            if (!field.IsLoaded) return false;
            if (!_duckHuntFieldPublished)
            {
                PrepareDuckHuntFlightResources();
                _duckHuntFieldPublished = true;
                InvalidateBoardArtworkSurface();
            }
        }
        return DuckHuntArtworkReady;
    }

    private void DisposeDuckHuntArtwork()
    {
        DisposeDuckHuntFeathers();
        DisposeDuckHuntWelcomeTitle();
        DisposeDuckHuntFlightResources();
        _duckHuntPhotoBrush?.Dispose();
        _duckHuntBodyClip?.Dispose();
        _duckHuntRearLegClip?.Dispose();
        _duckHuntNearLegClip?.Dispose();
        _duckHuntPhotoBrush = null;
        _duckHuntBodyClip = _duckHuntRearLegClip = _duckHuntNearLegClip = null;
        _duckHuntImages?.Dispose();
        _duckHuntImages = null;
        _duckHuntFieldImages?.Dispose();
        _duckHuntFieldImages = null;
        _duckHuntFieldPublished = false;
        _duckHuntArtworkPublished = false;
    }
    public DuckHuntSnapshot DuckHuntState { get { lock (_gate) return _boardSession.DuckHuntState; } }
    public DuckHuntFlightSnapshot HuntFlightState { get { lock (_gate) return _boardSession.HuntFlightState; } }
    public DuckHuntView CurrentDuckHuntView { get { lock (_gate) return _boardSession.CurrentDuckHuntView; } }

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

    public void ShowDuckHuntTargetPractice()
    {
        lock (_gate)
        {
            CancelBoardReveal();
            _blackOutput = false;
            _boardSession.ShowDuckHuntTargetPractice(_blackjackClock());
            SyncPhotoCopySession();
        }
    }

    public void ShowDuckHuntHunt()
    {
        lock (_gate)
        {
            CancelBoardReveal();
            _blackOutput = false;
            _boardSession.ShowDuckHuntHunt(_blackjackClock());
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
        if (_boardSession.CurrentDuckHuntView != DuckHuntView.TargetPractice)
        {
            DrawDuckHuntSceneView(drawing, now);
            return;
        }
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
        DrawBoardAspectText(drawing, "- DRY FIRE -", new Rect(200, 45, 600, 80), ink, heading);
        DrawBoardAspectText(drawing, $"HITS {state.Hits} / {DuckHuntGame.HitsToWin}",
            new Rect(350, 135, 300, 45), ink, caption);
        DrawDuckHuntWalkingMallard(drawing, state, PaintBoardAspect());
        DrawDuckHuntFeathers(drawing, PaintBoardAspect());
        DrawDuckHuntMeasurement(drawing);
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
        if (_boardSession.CurrentDuckHuntView == DuckHuntView.Menu)
        {
            // These stationary mode captions remain available above the drawer.
            // This method also draws the camera's expected caption reference.
            foreach (var button in buttons)
                if (IsDuckHuntModeButton(button))
                    DrawDrawerButton(drawing, button, false, PaintBoardAspect(), _holdControlBrightness);
            buttons = buttons.Where(button => !IsDuckHuntModeButton(button)).ToArray();
        }
        DrawBoardDrawerControls(drawing, buttons, [], [], _boardSession.DuckHuntDrawerOpen,
            (float)_boardSession.GetDuckHuntDrawerProgress(now), PaintBoardAspect(), _holdControlBrightness);
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
        // Tracking stays live for shot timing, but only a click creates a mark.
        BoardAim? anchor = measurement.OldDetectedXY ?? measurement.NewDetectedXY;
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
