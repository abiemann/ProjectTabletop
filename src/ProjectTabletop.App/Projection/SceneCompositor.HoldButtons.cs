using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
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
    private readonly record struct HoldSceneKey(BoardScreen Screen, string Controls, double Aspect,
        decimal ControlValue, object? CameraMap, object? SurfaceMap);
    private readonly record struct HoldControlAppearance(string Label, bool Enabled, BoardRect Bounds, decimal Value);
    private HoldSceneKey? _holdSceneKey;
    private Dictionary<string, HoldControlAppearance> _holdControlAppearances = [];
    private HandAcquisitionSceneImage? _holdExpectedScene;
    private long _holdRevision;

    /// <param name="ButtonIds">Every current button, index-aligned with the scene's control regions.</param>
    public sealed record HoldButtonContext(long Revision, PixelPoint[] SearchPolygon,
        HandAcquisitionSceneImage ExpectedScene, IReadOnlyList<string> ButtonIds, IReadOnlySet<string> EnabledHoldIds)
    {
        // Golden rule: a long press requires broken lettering. A colour or
        // reflectance change beneath still-readable letters is never a press.
        // Confirmation also requires both 7% obstruction coverage floors.
        public IReadOnlyList<string> HeldButtons(HandAcquisitionPresenceResult? result) =>
            result?.TextPatterns?.Where(pattern => pattern.ShapeCorrupted && !pattern.LabelIntact &&
                    pattern.ConfirmationFrames >= 1 &&
                    pattern.ControlRegion >= 0 && pattern.ControlRegion < ButtonIds.Count &&
                    EnabledHoldIds.Contains(ButtonIds[pattern.ControlRegion]))
                .Select(pattern => ButtonIds[pattern.ControlRegion]).ToArray() ?? [];

        // A positively recognized, uncovered caption ends a partial press now.
        // No observation or an uncertain optical fit is not proof of release.
        public IReadOnlyList<string> ClearedButtons(HandAcquisitionPresenceResult? result) =>
            result?.TextPatterns?.Where(pattern => pattern.LabelIntact && !pattern.ShapeCorrupted &&
                    pattern.ControlRegion >= 0 && pattern.ControlRegion < ButtonIds.Count &&
                    EnabledHoldIds.Contains(ButtonIds[pattern.ControlRegion]))
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
                string.Join('|', buttons.Select(button => $"{button.Id}:{button.Label}:{button.Enabled}:{button.Bounds}")),
                PaintBoardAspect(), _boardSession.Screen == BoardScreen.Slots ? _boardSession.SlotsState.BuyCost : 0,
                _boardCameraMap, _boardSurfaceMap);
            if (_holdSceneKey != key || _holdExpectedScene is null)
            {
                if (CaptureExpectedAcquisitionScene() is not { } scene) return null;
                // Disabled artwork, changed captions or resized letters cannot
                // arm a later enabled control. Verify this exact appearance
                // uncovered before accepting its next camera-driven press.
                var appearances = buttons.ToDictionary(button => button.Id, button =>
                    new HoldControlAppearance(button.Label, button.Enabled, button.Bounds,
                        button.Id == "slot-buy" ? _boardSession.SlotsState.BuyCost : 0));
                bool sameGeometry = _holdSceneKey is { } previous && previous.Screen == key.Screen &&
                    previous.Aspect == key.Aspect && Equals(previous.CameraMap, key.CameraMap) &&
                    Equals(previous.SurfaceMap, key.SurfaceMap);
                // A sibling becoming enabled must not interrupt a continuous
                // Globe zoom hold whose own caption and surface are unchanged.
                string[]? changedIds = sameGeometry ? appearances.Keys.Union(_holdControlAppearances.Keys)
                    .Where(id => !appearances.TryGetValue(id, out var current) ||
                        !_holdControlAppearances.TryGetValue(id, out var prior) || current != prior).ToArray() : null;
                _boardSession.ResetHoldCaptionEvidence(changedIds);
                _holdControlAppearances = appearances;
                _holdSceneKey = key;
                _holdExpectedScene = scene;
                _holdRevision++;
            }
            var polygon = new[] { new Point2(.005, .005), new Point2(.995, .005),
                new Point2(.995, .995), new Point2(.005, .995) }
                .Select(point => _boardCameraMap!.InverseTransform(_boardSurfaceMap!.Transform(point)))
                .Select(point => new PixelPoint(point.X, point.Y)).ToArray();
            return new(_holdRevision, polygon, _holdExpectedScene, buttons.Select(button => button.Id).ToArray(),
                buttons.Where(button => button.IsHold && button.Enabled).Select(button => button.Id).ToHashSet());
        }
    }

    /// <summary>Applies one camera frame's hold evidence; returns the buttons it activated.</summary>
    public IReadOnlyList<string> ObserveHoldButtons(HoldButtonContext? context, IReadOnlyList<string> heldIds,
        DateTimeOffset frameTime, IReadOnlyCollection<string>? clearedIds = null)
    {
        lock (_gate)
        {
            if (context is null || context.Revision != _holdRevision || !AcquisitionBoardReady) return [];
            var activated = _boardSession.ObserveHeldButtons(heldIds, frameTime, HoldClock(), clearedIds);
            if (activated.Count > 0)
            {
                QueuePhotoCopyHoldAction(activated, frameTime);
                SyncPhotoCopySession();
            }
            return activated;
        }
    }

    // A held long-press button's outside rim warms from amber to hot orange-red
    // as its second fills. Its inner edge follows the actual rounded surface;
    // the caption and its inset camera-reference interior remain untouched.
    private CanvasRenderTarget? _holdFeedbackTarget;
    private string? _holdFeedbackKey;
    private static readonly Windows.UI.Color HoldRimCool = Windows.UI.Color.FromArgb(255, 255, 214, 120);
    private static readonly Windows.UI.Color HoldRimHot = Windows.UI.Color.FromArgb(255, 255, 72, 24);

    public IReadOnlyList<BoardHoldProgress> CurrentHoldProgress
    {
        get { lock (_gate) return _boardSession.HoldProgress(HoldClock()); }
    }

    private CanvasRenderTarget? DrawHoldFeedbackLayer(CanvasDevice device)
    {
        var progress = _boardSession.HoldProgress(HoldClock());
        if (progress.Count == 0) return null;
        if (EnsureBoardRenderTarget(ref _holdFeedbackTarget, device)) _holdFeedbackKey = null;
        string key = string.Join('|', progress.Select(hold => $"{hold.ButtonId}:{(int)(hold.Progress * 60)}"));
        if (_holdFeedbackKey != key)
        {
            using var drawing = _holdFeedbackTarget!.CreateDrawingSession();
            drawing.Transform = BoardRasterTransform(_holdFeedbackTarget);
            drawing.Clear(Colors.Transparent);
            foreach (var hold in progress)
            {
                if (_boardSession.Buttons.FirstOrDefault(button => button.Id == hold.ButtonId) is not { } button) continue;
                float heat = (float)hold.Progress;
                var color = Windows.UI.Color.FromArgb((byte)(90 + 110 * heat),
                    (byte)(HoldRimCool.R + (HoldRimHot.R - HoldRimCool.R) * heat),
                    (byte)(HoldRimCool.G + (HoldRimHot.G - HoldRimCool.G) * heat),
                    (byte)(HoldRimCool.B + (HoldRimHot.B - HoldRimCool.B) * heat));
                // Offset the stroke's centre by half its width, including the
                // corner radius. This makes a continuous outside-only contour
                // touching the button, with no square masks cutting its sides.
                const float width = 3;
                float radius = BoardButtonCornerRadius(button) + width / 2;
                drawing.DrawRoundedRectangle(Pixels(button.Bounds, width / 2), radius, radius, color, width);
            }
            _holdFeedbackKey = key;
        }
        return _holdFeedbackTarget;
    }

    // Shared by the actual button surfaces and their hold outline so both
    // contours keep the same corners on every board and at every raster size.
    private static float BoardButtonCornerRadius(BoardButton button) => button.Id switch
    {
        "globe-drawer-open" or "globe-drawer-close" => 25,
        _ when button.Id.StartsWith("slot-", StringComparison.Ordinal) => 18,
        _ when button.Id.StartsWith("bj-", StringComparison.Ordinal) => 13,
        _ when button.Id.StartsWith("globe-", StringComparison.Ordinal) => 15,
        _ => 19
    };

    private static Rect Pixels(BoardRect bounds, float grow) => new(bounds.X * BoardSurfaceSize - grow,
        bounds.Y * BoardSurfaceSize - grow, bounds.Width * BoardSurfaceSize + 2 * grow, bounds.Height * BoardSurfaceSize + 2 * grow);

    private void DisposeHoldFeedbackLayer()
    {
        _holdFeedbackTarget?.Dispose();
        _holdFeedbackTarget = null;
        _holdFeedbackKey = null;
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
