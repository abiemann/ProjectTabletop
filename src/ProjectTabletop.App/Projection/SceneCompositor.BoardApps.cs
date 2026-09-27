using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Text;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

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

    private readonly record struct BoardSurfaceState(BoardScreen Screen, int HoverMask, int HandStatus);

    public BoardScreen CurrentBoardScreen
    {
        get { lock (_gate) return _boardSession.Screen; }
    }

    public string CurrentBoardTitle
    {
        get { lock (_gate) return _boardSession.Title; }
    }

    public void ShowBoardMenu()
    {
        lock (_gate)
        {
            _blackOutput = false;
            _boardSession.ShowMenu();
        }
    }

    public void ShowHandTrackingTest()
    {
        lock (_gate)
        {
            _blackOutput = false;
            _boardSession.ShowHandTrackingTest();
        }
    }

    private void DrawBoardApplication(CanvasDrawingSession ds, Rect output)
    {
        if (_boardSurfaceMap is null) return;
        if (_boardApplicationTarget is null || _boardApplicationTarget.Device != ds.Device)
        {
            _boardApplicationTarget?.Dispose();
            _boardApplicationTarget = new CanvasRenderTarget(ds.Device,
                BoardSurfaceSize, BoardSurfaceSize, 96);
            _renderedBoardState = null;
        }

        var now = DateTimeOffset.UtcNow;
        var handsFresh = _handFrameTime <= now &&
            now - _handFrameTime <= TimeSpan.FromMilliseconds(350);
        var executing = handsFresh && _handTips.Any(cursor => now < cursor.ExecuteUntil);
        var hoverMask = 0;
        for (var index = 0; index < _boardSession.Buttons.Count; index++)
            if (handsFresh && _boardSession.HoveredButtonIds.Contains(_boardSession.Buttons[index].Id))
                hoverMask |= 1 << index;
        var state = new BoardSurfaceState(_boardSession.Screen, hoverMask,
            executing ? 2 : handsFresh && _handTips.Length > 0 ? 1 : 0);
        // Cursor motion is drawn separately. Reuse the UI texture until its
        // screen, hovered button, or gesture status changes on either canvas.
        if (_renderedBoardState != state)
        {
            using var surface = _boardApplicationTarget.CreateDrawingSession();
            surface.Clear(_boardSession.Screen == BoardScreen.HandTracking
                ? Colors.Transparent : Color.FromArgb(255, 8, 14, 24));
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

            var muted = Color.FromArgb(255, 183, 201, 218);
            if (_boardSession.Screen == BoardScreen.Menu)
            {
                surface.DrawText("PROJECT TABLETOP", 80, 57, Colors.Cyan, small);
                surface.DrawText("Choose an app", 76, 97, Colors.White, heading);
                surface.DrawText("Point at a button. Pinch to select.", 80, 186, muted, body);
                foreach (var button in _boardSession.Buttons)
                {
                    var hovered = handsFresh && _boardSession.HoveredButtonIds.Contains(button.Id);
                    DrawBoardButton(surface, button, hovered, label, small,
                        button.Destination == BoardScreen.HandTracking ? "Gesture test" : "Coming soon");
                }
                surface.DrawLine(80, 862, 920, 862, Color.FromArgb(255, 44, 65, 83), 2);
                surface.DrawText("Blue circle = fingertip     Red circle = pinch", 80, 890, muted, small);
                surface.DrawText("Release your fingers before selecting again.", 80, 931, muted, small);
            }
            else
            {
                foreach (var button in _boardSession.Buttons)
                    DrawBoardButton(surface, button,
                        handsFresh && _boardSession.HoveredButtonIds.Contains(button.Id), label, small);

                if (_boardSession.Screen == BoardScreen.HandTracking)
                {
                    surface.FillRoundedRectangle(new Rect(390, 55, 550, 105), 18, 18,
                        Color.FromArgb(255, 20, 33, 49));
                    surface.DrawText("Hand-Tracking", 414, 68, Colors.White, body);
                    var statusColor = executing ? Colors.Red : Colors.Cyan;
                    var status = executing ? "PINCH DETECTED" : handsFresh && _handTips.Length > 0
                        ? "Pinch: red circle for one second" : "Waiting for a hand";
                    surface.DrawText(status, 414, 113, statusColor, small);
                }
                else
                {
                    surface.DrawText("PROJECT TABLETOP", 390, 83, Colors.Cyan, small);
                    surface.DrawText(_boardSession.Title, 76, 264, Colors.White, heading);
                    surface.DrawText("Coming soon", 80, 372, Colors.Cyan, label);
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

    private static void DrawBoardButton(CanvasDrawingSession ds, BoardButton button,
        bool hovered, CanvasTextFormat label, CanvasTextFormat small, string? description = null)
    {
        var bounds = button.Bounds;
        var rect = new Rect(bounds.X * BoardSurfaceSize, bounds.Y * BoardSurfaceSize,
            bounds.Width * BoardSurfaceSize, bounds.Height * BoardSurfaceSize);
        ds.FillRoundedRectangle(rect, 18, 18, hovered
            ? Color.FromArgb(255, 20, 78, 92) : Color.FromArgb(255, 27, 43, 62));
        ds.DrawRoundedRectangle(rect, 18, 18,
            hovered ? Colors.Cyan : Color.FromArgb(255, 87, 115, 140), hovered ? 5 : 2);
        var x = (float)rect.X + 26;
        var y = (float)rect.Y + (description is null ? 27 : 33);
        ds.DrawText(button.Label, x, y, Colors.White, label);
        if (description is not null)
            ds.DrawText(description, x, (float)rect.Y + 100,
                hovered ? Colors.Cyan : Color.FromArgb(255, 183, 201, 218), small);
    }

}
