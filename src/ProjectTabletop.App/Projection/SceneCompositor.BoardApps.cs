using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Text;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // A square logical surface is fitted to the physical board by the very same
    // homography used in reverse for hit testing. Buttons never occupy clipped
    // corners of the board's projector-space bounding rectangle.
    private const float BoardSurfaceSize = 1000;
    private readonly BoardSession _boardSession = new();
    private CanvasRenderTarget? _boardApplicationTarget;
    private BoardSurfaceState? _renderedBoardState;

    private readonly record struct BoardSurfaceState(BoardScreen Screen, int HoverMask, int HandStatus,
        int PhotoStampCount, string? PhotoStatus, long PhotoRevision);

    public BoardScreen CurrentBoardScreen
    {
        get { lock (_gate) return _boardSession.Screen; }
    }

    public string CurrentBoardTitle
    {
        get { lock (_gate) return _boardSession.Title; }
    }

    public IReadOnlyList<string> HoveredBoardButtons
    {
        get
        {
            lock (_gate)
                return DateTimeOffset.UtcNow - _handFrameTime <= TimeSpan.FromMilliseconds(350)
                    ? _boardSession.HoveredButtonIds.ToArray() : Array.Empty<string>();
        }
    }

    public void ShowBoardMenu()
    {
        lock (_gate)
        {
            _blackOutput = false;
            _boardSession.ShowMenu();
            SyncPhotoCopySession();
        }
    }

    public void ShowHandTrackingTest()
    {
        lock (_gate)
        {
            _blackOutput = false;
            _boardSession.ShowHandTrackingTest();
            SyncPhotoCopySession();
        }
    }

    private void DrawBoardApplication(CanvasDrawingSession ds, Rect output, bool preview)
    {
        try { DrawBoardApplicationCore(ds, output, preview); }
        catch (Exception error) when (_boardSession.Screen == BoardScreen.PhotoCopy &&
            !ds.Device.IsDeviceLost(error.HResult))
        {
            // EndDraw on the cached drawing session may report deferred GPU
            // errors after individual stamp calls have already returned.
            HandlePhotoCopyRenderFailure(error);
        }
    }

    private void DrawBoardApplicationCore(CanvasDrawingSession ds, Rect output, bool preview)
    {
        if (_boardSurfaceMap is null) return;
        SyncPhotoCopySession();
        if (_boardApplicationTarget is null || _boardApplicationTarget.Device != ds.Device)
        {
            _boardApplicationTarget?.Dispose();
            _boardApplicationTarget = new CanvasRenderTarget(ds.Device,
                BoardSurfaceSize, BoardSurfaceSize, 96);
            _renderedBoardState = null;
        }

        var now = DateTimeOffset.UtcNow;
        var photoCopy = _boardSession.Screen == BoardScreen.PhotoCopy;
        if (photoCopy && !preview && PhotoCopyCaptureAllowed &&
            _photoCopyWhiteShownAt == DateTimeOffset.MinValue)
            _photoCopyWhiteShownAt = now;
        var handsFresh = _handFrameTime <= now &&
            now - _handFrameTime <= TimeSpan.FromMilliseconds(350);
        var executing = handsFresh && _handTips.Any(cursor => now < cursor.ExecuteUntil);
        var hoverMask = 0;
        for (var index = 0; index < _boardSession.Buttons.Count; index++)
            if (handsFresh && _boardSession.HoveredButtonIds.Contains(_boardSession.Buttons[index].Id))
                hoverMask |= 1 << index;
        var state = new BoardSurfaceState(_boardSession.Screen, hoverMask,
            executing ? 2 : handsFresh && _handTips.Length > 0 ? 1 : 0,
            photoCopy ? PhotoCopyStampCount(now) : 0,
            photoCopy ? PhotoCopyDisplayStatus(now) : null,
            photoCopy ? _photoCopyRevision : 0);
        // Cursor motion is drawn separately. Reuse the UI texture until its
        // screen, hovered button, or gesture status changes on either canvas.
        if (_renderedBoardState != state)
        {
            using var surface = _boardApplicationTarget.CreateDrawingSession();
            surface.Clear(photoCopy ? Colors.White : _boardSession.Screen == BoardScreen.HandTracking
                ? Colors.Transparent : AppPalette.Background);
            if (photoCopy)
            {
                DrawPhotoCopyStamps(surface, state.PhotoStampCount);
            }
            using var heading = new CanvasTextFormat
            {
                FontFamily = "Segoe UI",
                FontSize = 52,
                FontWeight = FontWeights.SemiBold,
                WordWrapping = CanvasWordWrapping.NoWrap
            };
            using var label = new CanvasTextFormat
            {
                FontFamily = "Segoe UI",
                FontSize = 34,
                FontWeight = FontWeights.SemiBold,
                WordWrapping = CanvasWordWrapping.NoWrap
            };
            using var body = new CanvasTextFormat
            {
                FontFamily = "Segoe UI",
                FontSize = 24,
                WordWrapping = CanvasWordWrapping.NoWrap
            };
            using var small = new CanvasTextFormat
            {
                FontFamily = "Segoe UI",
                FontSize = 21,
                WordWrapping = CanvasWordWrapping.NoWrap
            };

            var muted = AppPalette.MutedText;
            if (_boardSession.Screen == BoardScreen.Menu)
            {
                // Keep neutral illumination on the targets instead of the whole
                // board, with sparse text and no bright bands across the hand.
                var menuText = AppPalette.Text;
                var menuMuted = AppPalette.MutedText;
                surface.DrawText("PROJECT TABLETOP", 80, 57, menuMuted, small);
                surface.DrawText("Choose a board", 76, 97, menuText, heading);
                surface.DrawText("Point until the side light comes on. Then pinch.", 80, 186, menuMuted, body);
                foreach (var button in _boardSession.Buttons)
                {
                    var hovered = handsFresh && _boardSession.HoveredButtonIds.Contains(button.Id);
                    DrawMenuButton(surface, button, hovered, label);
                }
                surface.DrawText("White spotlight = hand     Side light = selected button", 80, 890, menuMuted, small);
                surface.DrawText("Release your fingers before selecting again.", 80, 931, menuMuted, small);
            }
            else
            {
                foreach (var button in _boardSession.Buttons)
                    DrawBoardButton(surface, button,
                        handsFresh && _boardSession.HoveredButtonIds.Contains(button.Id), label, small);

                if (photoCopy)
                {
                    // Keep the controls and instructions legible over the copies,
                    // while letting the pattern reach the top edge between them.
                    surface.FillRoundedRectangle(new Rect(380, 70, 240, 75), 12, 12, AppPalette.Surface);
                    surface.DrawText("Photo Copy", 394, 87, AppPalette.Text, label);
                    using var photoStatus = new CanvasTextFormat
                    {
                        FontFamily = "Segoe UI",
                        FontSize = 18,
                        WordWrapping = CanvasWordWrapping.Wrap
                    };
                    surface.FillRoundedRectangle(new Rect(50, 165, 900, 50), 10, 10, AppPalette.Surface);
                    surface.DrawText(state.PhotoStatus ?? PhotoCopyReadyMessage,
                        new Rect(60, 170, 880, 44), AppPalette.Text, photoStatus);
                }
                else if (_boardSession.Screen == BoardScreen.HandTracking)
                {
                    surface.FillRoundedRectangle(new Rect(390, 55, 550, 105), 18, 18,
                        AppPalette.Surface);
                    surface.DrawText("Hand-Tracking", 414, 68, AppPalette.Text, body);
                    var statusColor = executing ? Colors.Red : AppPalette.Text;
                    var status = executing ? "PINCH DETECTED" : handsFresh && _handTips.Length > 0
                        ? "Pinch: red circle for one second" : "Waiting for a hand";
                    surface.DrawText(status, 414, 113, statusColor, small);
                }
                else
                {
                    surface.DrawText("PROJECT TABLETOP", 390, 83, AppPalette.MutedText, small);
                    surface.DrawText(_boardSession.Title, 76, 264, AppPalette.Text, heading);
                    surface.DrawText("Coming soon", 80, 372, AppPalette.IndicatorOn, label);
                    surface.DrawText("This app is not connected yet.", 80, 444, muted, body);
                    surface.DrawText("Point and pinch Back to choose another app.", 80, 543, muted, body);
                }
            }
            _renderedBoardState = state;
        }

        var h = _boardSurfaceMap.ToMatrix();
        // System.Numerics uses row vectors. Include the source-pixel → UV scale,
        // the projective denominator, and the preview's fitted output rectangle.
        // Dividing the resulting XY by W exactly matches Homography.Transform.
        var matrix = new Matrix4x4(
            (float)((output.Width * h[0] + output.X * h[6]) / BoardSurfaceSize),
            (float)((output.Height * h[3] + output.Y * h[6]) / BoardSurfaceSize), 0,
            (float)(h[6] / BoardSurfaceSize),
            (float)((output.Width * h[1] + output.X * h[7]) / BoardSurfaceSize),
            (float)((output.Height * h[4] + output.Y * h[7]) / BoardSurfaceSize), 0,
            (float)(h[7] / BoardSurfaceSize),
            0, 0, 1, 0,
            (float)(output.Width * h[2] + output.X * h[8]),
            (float)(output.Height * h[5] + output.Y * h[8]), 0, (float)h[8]);
        using var perspective = new Transform3DEffect
        {
            Source = _boardApplicationTarget,
            TransformMatrix = matrix,
            InterpolationMode = CanvasImageInterpolation.Linear,
            BorderMode = EffectBorderMode.Soft
        };
        ds.DrawImage(perspective);
    }

    private static void DrawMenuButton(CanvasDrawingSession ds, BoardButton button,
        bool hovered, CanvasTextFormat label)
    {
        var bounds = button.Bounds;
        var rect = new Rect(bounds.X * BoardSurfaceSize, bounds.Y * BoardSurfaceSize,
            bounds.Width * BoardSurfaceSize, bounds.Height * BoardSurfaceSize);
        DrawButtonSurface(ds, rect, hovered);
        ds.DrawText(button.Label, (float)rect.X + 34, (float)rect.Y + 58,
            AppPalette.ButtonText, label);
    }

    private static void DrawButtonSurface(CanvasDrawingSession ds, Rect rect, bool hovered)
    {
        ds.FillRoundedRectangle(rect, 18, 18, AppPalette.Button);
        var indicator = new Vector2((float)rect.X + 16, (float)(rect.Y + rect.Height / 2));
        if (hovered)
            ds.FillCircle(indicator, 10, AppPalette.IndicatorGlow);
        ds.FillCircle(indicator, 6, hovered
            ? AppPalette.IndicatorOn : AppPalette.IndicatorOff);
    }

    private static void DrawBoardButton(CanvasDrawingSession ds, BoardButton button,
        bool hovered, CanvasTextFormat label, CanvasTextFormat small, string? description = null)
    {
        var bounds = button.Bounds;
        var rect = new Rect(bounds.X * BoardSurfaceSize, bounds.Y * BoardSurfaceSize,
            bounds.Width * BoardSurfaceSize, bounds.Height * BoardSurfaceSize);
        DrawButtonSurface(ds, rect, hovered);
        var x = (float)rect.X + 34;
        var y = (float)rect.Y + (description is null ? 27 : 33);
        ds.DrawText(button.Label, x, y, AppPalette.ButtonText, label);
        if (description is not null)
            ds.DrawText(description, x, (float)rect.Y + 100,
                AppPalette.ButtonText, small);
    }

}
