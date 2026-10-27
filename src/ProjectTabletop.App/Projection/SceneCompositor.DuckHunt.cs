using System.Numerics;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private static readonly Color DuckHuntBackground = Color.FromArgb(255, 218, 223, 213);
    private BitmapAssetSet? _duckHuntImages;
    private BitmapAssetSet? _duckHuntFieldImages;
    private BitmapAssetSet? _duckHuntGalleryImages;
    private bool _duckHuntArtworkPublished;
    private bool _duckHuntFieldPublished;
    private bool _duckHuntGalleryPublished;
    internal bool DuckHuntArtworkReady => _duckHuntArtworkPublished &&
        _duckHuntImages?.Image("arcade-welcome-v1.png") is not null &&
        (_boardSession.Screen != BoardScreen.DuckHunt || _boardSession.CurrentDuckHuntView != DuckHuntView.Hunt ||
            _duckHuntFieldPublished && _duckHuntFieldImages?.Image("hunt-field-v1.png") is not null &&
            _duckHuntFieldImages.Image("flying-mallard-body.png") is not null &&
            _duckHuntFieldImages.Image("flying-mallard-wing.png") is not null) &&
        (_boardSession.Screen != BoardScreen.DuckHunt || _boardSession.CurrentDuckHuntView != DuckHuntView.TargetPractice ||
            _duckHuntGalleryPublished && _duckHuntGalleryImages?.Image("target-practice-saloon-v1.png") is not null &&
            _duckHuntGalleryImages.Image("gallery-golden-ceiling-v3.png") is not null &&
            _duckHuntGalleryImages.Image("gallery-steampunk-target.png") is not null &&
            _duckHuntGalleryImages.Image("gallery-paper-puncture.png") is not null &&
            _duckHuntGalleryImages.Image("gallery-crystal-skull.png") is not null &&
            _duckHuntGalleryImages.Image("gallery-upper-caravan.png") is not null &&
            _duckHuntGalleryImages.Image("gallery-unlit-lamps.png") is not null &&
            _duckHuntGalleryImages.Image("gallery-candle-flames.png") is not null);
    private string? DuckHuntArtworkError => _duckHuntImages?.Error ??
        _duckHuntImages?.Errors.Values.FirstOrDefault() ??
        (_boardSession.CurrentDuckHuntView == DuckHuntView.Hunt
            ? _duckHuntFieldImages?.Error ?? _duckHuntFieldImages?.Errors.Values.FirstOrDefault() :
         _boardSession.CurrentDuckHuntView == DuckHuntView.TargetPractice
            ? _duckHuntGalleryImages?.Error ?? _duckHuntGalleryImages?.Errors.Values.FirstOrDefault() : null);

    private BitmapAssetSet GetDuckHuntImages(CanvasDevice device)
    {
        if (_duckHuntImages is null || _duckHuntImages.Device != device)
        {
            DisposeDuckHuntArtwork();
            _duckHuntImages = new(device, Path.Combine(AppContext.BaseDirectory, "Assets", "DuckHunt"),
                ["arcade-welcome-v1.png"]);
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

    private BitmapAssetSet GetDuckHuntGalleryImages(CanvasDevice device)
    {
        if (_duckHuntGalleryImages is null || _duckHuntGalleryImages.Device != device)
        {
            _duckHuntGalleryImages?.Dispose();
            DisposeDuckHuntGalleryResources();
            _duckHuntGalleryPublished = false;
            _duckHuntGalleryImages = new(device, Path.Combine(AppContext.BaseDirectory, "Assets", "DuckHunt"),
                ["target-practice-saloon-v1.png", "gallery-golden-ceiling-v3.png", "gallery-steampunk-target.png", "gallery-paper-puncture.png",
                    "gallery-crystal-skull.png", "gallery-upper-caravan.png", "gallery-unlit-lamps.png", "gallery-candle-flames.png"]);
        }
        return _duckHuntGalleryImages;
    }

    internal async Task EnsureDuckHuntResourcesAsync(CanvasDevice device)
    {
        BitmapAssetSet images;
        BitmapAssetSet? field;
        BitmapAssetSet? gallery;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            images = GetDuckHuntImages(device);
            field = _boardSession.Screen == BoardScreen.DuckHunt && _boardSession.CurrentDuckHuntView == DuckHuntView.Hunt
                ? GetDuckHuntFieldImages(device) : null;
            gallery = _boardSession.Screen == BoardScreen.DuckHunt && _boardSession.CurrentDuckHuntView == DuckHuntView.TargetPractice
                ? GetDuckHuntGalleryImages(device) : null;
        }
        await Task.WhenAll(images.EnsureLoadedAsync(), field?.EnsureLoadedAsync() ?? Task.CompletedTask,
            gallery?.EnsureLoadedAsync() ?? Task.CompletedTask).ConfigureAwait(false);
        lock (_gate)
            if (!_disposed && ReferenceEquals(images, _duckHuntImages)) PrepareDuckHuntResources(device);
    }

    private bool PrepareDuckHuntResources(CanvasDevice device)
    {
        var images = GetDuckHuntImages(device);
        if (!images.IsLoaded) return false;
        if (!_duckHuntArtworkPublished)
        {
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
        if (_boardSession.Screen == BoardScreen.DuckHunt && _boardSession.CurrentDuckHuntView == DuckHuntView.TargetPractice)
        {
            var gallery = GetDuckHuntGalleryImages(device);
            if (!gallery.IsLoaded) return false;
            if (!_duckHuntGalleryPublished)
            {
                PrepareDuckHuntGalleryResources(device);
                _duckHuntGalleryPublished = true;
                InvalidateBoardArtworkSurface();
            }
        }
        return DuckHuntArtworkReady;
    }

    private void DisposeDuckHuntArtwork()
    {
        DisposeDuckHuntGalleryResources();
        DisposeDuckHuntWelcomeTitle();
        DisposeDuckHuntFlightResources();
        _duckHuntImages?.Dispose();
        _duckHuntImages = null;
        _duckHuntFieldImages?.Dispose();
        _duckHuntFieldImages = null;
        _duckHuntFieldPublished = false;
        _duckHuntGalleryImages?.Dispose();
        _duckHuntGalleryImages = null;
        _duckHuntGalleryPublished = false;
        _duckHuntArtworkPublished = false;
    }
    public DuckHuntGallerySnapshot GalleryState { get { lock (_gate) return _boardSession.GalleryState; } }
    public IReadOnlyList<DuckHuntGalleryPuncture> GalleryPunctures
    {
        get { lock (_gate) return _boardSession.GalleryPunctures.ToArray(); }
    }
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
            var now = _blackjackClock();
            if (_boardSession.CurrentDuckHuntView == DuckHuntView.TargetPractice)
                UpdateDuckHuntRound(now);
            var button = _boardSession.Buttons.FirstOrDefault(button => button.Enabled && button.Bounds.Contains(u, v));
            return button is not null && _boardSession.ActivateButton(button.Id, now);
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
        DrawDuckHuntGallery(drawing, _boardSession.GalleryState, PaintBoardAspect());
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

}
