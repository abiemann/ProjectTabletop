using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // Hold buttons activate from covered-caption evidence alone. Their own
    // reference follows only what changes the rendered controls, so activating
    // one (for example, zooming Earth) does not restart the hold.
    private readonly record struct HoldSceneKey(BoardScreen Screen, string Controls, object? CameraMap, object? SurfaceMap);
    private HoldSceneKey? _holdSceneKey;
    private HandAcquisitionSceneImage? _holdExpectedScene;
    private long _holdRevision;

    /// <param name="ButtonIds">Every current button, index-aligned with the scene's control regions.</param>
    public sealed record HoldButtonContext(long Revision, PixelPoint[] SearchPolygon,
        HandAcquisitionSceneImage ExpectedScene, IReadOnlyList<string> ButtonIds)
    {
        // Fresh caption obstruction meeting both 7% floors in this frame.
        public IReadOnlyList<string> HeldButtons(HandAcquisitionPresenceResult? result) =>
            result?.TextPatterns?.Where(pattern => pattern.ConfirmationFrames >= 1 &&
                    pattern.ControlRegion >= 0 && pattern.ControlRegion < ButtonIds.Count)
                .Select(pattern => ButtonIds[pattern.ControlRegion]).ToArray() ?? [];
    }

    public HoldButtonContext? GetHoldButtonContext(DateTimeOffset frameTime)
    {
        lock (_gate)
        {
            var now = HoldClock();
            var buttons = _boardSession.Buttons;
            if (!AcquisitionBoardReady || !buttons.Any(button => button.IsHold && button.Enabled) ||
                HasGlobeDrawerAnimation(_globeClock()) || frameTime > now ||
                now - frameTime > TimeSpan.FromMilliseconds(350)) return null;
            var key = new HoldSceneKey(_boardSession.Screen,
                string.Join('|', buttons.Select(button => $"{button.Id}:{button.Enabled}:{button.Bounds}")),
                _boardCameraMap, _boardSurfaceMap);
            if (_holdSceneKey != key || _holdExpectedScene is null)
            {
                if (CaptureExpectedAcquisitionScene() is not { } scene) return null;
                _holdSceneKey = key;
                _holdExpectedScene = scene;
                _holdRevision++;
            }
            var polygon = new[] { new Point2(.005, .005), new Point2(.995, .005),
                new Point2(.995, .995), new Point2(.005, .995) }
                .Select(point => _boardCameraMap!.InverseTransform(_boardSurfaceMap!.Transform(point)))
                .Select(point => new PixelPoint(point.X, point.Y)).ToArray();
            return new(_holdRevision, polygon, _holdExpectedScene, buttons.Select(button => button.Id).ToArray());
        }
    }

    /// <summary>Applies one camera frame's hold evidence; returns the buttons it activated.</summary>
    public IReadOnlyList<string> ObserveHoldButtons(HoldButtonContext? context, IReadOnlyList<string> heldIds,
        DateTimeOffset frameTime)
    {
        lock (_gate)
        {
            if (context is null || context.Revision != _holdRevision || !AcquisitionBoardReady) return [];
            var activated = _boardSession.ObserveHeldButtons(heldIds, frameTime, HoldClock());
            if (activated.Count > 0) SyncPhotoCopySession();
            return activated;
        }
    }

    private DateTimeOffset HoldClock() => _boardSession.Screen == BoardScreen.Globe ? _globeClock() : _blackjackClock();

    private bool OnHoldButton(PixelPoint camera)
    {
        if (_boardCameraMap is null || _boardSurfaceMap is null) return false;
        var board = _boardSurfaceMap.InverseTransform(_boardCameraMap.Transform(new(camera.X, camera.Y)));
        return _boardSession.Buttons.Any(button => button.IsHold && button.Bounds.Contains(board.X, board.Y));
    }

    // Projected light over a hold button would erase its caption from the
    // camera's view and look like a finger, so lights are clipped around them.
    private CanvasGeometry? HoldButtonLightClip(CanvasDrawingSession ds, Rect output)
    {
        if (_boardSurfaceMap is null) return null;
        var holds = _boardSession.Buttons.Where(button => button.IsHold).ToArray();
        if (holds.Length == 0) return null;
        var area = CanvasGeometry.CreateRectangle(ds.Device, output);
        foreach (var button in holds)
        {
            const double margin = .006;
            var b = button.Bounds;
            var corners = new[] { new Point2(b.X - margin, b.Y - margin), new Point2(b.X + b.Width + margin, b.Y - margin),
                    new Point2(b.X + b.Width + margin, b.Y + b.Height + margin), new Point2(b.X - margin, b.Y + b.Height + margin) }
                .Select(point => _boardSurfaceMap.Transform(point))
                .Select(point => new Vector2((float)(output.X + point.X * output.Width), (float)(output.Y + point.Y * output.Height)))
                .ToArray();
            using var hole = CanvasGeometry.CreatePolygon(ds.Device, corners);
            var remaining = area.CombineWith(hole, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
            area.Dispose();
            area = remaining;
        }
        return area;
    }
}
