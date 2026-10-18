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
    private readonly record struct HoldSceneKey(BoardScreen Screen, long Navigation, string Controls, double Aspect,
        decimal ControlValue, double ControlBrightness, object? CameraMap, object? SurfaceMap);
    private readonly record struct HoldControlAppearance(string Label, bool Enabled, BoardRect Bounds, decimal Value);
    private HoldSceneKey? _holdSceneKey;
    private Dictionary<string, HoldControlAppearance> _holdControlAppearances = [];
    private HandAcquisitionSceneImage? _holdExpectedScene;
    private long _holdRevision;
    private DateTimeOffset _holdNavigationStartedAt;

    /// <param name="ButtonIds">Every current button, index-aligned with the scene's control regions.</param>
    public sealed record HoldButtonContext(long Revision, PixelPoint[] SearchPolygon,
        HandAcquisitionSceneImage ExpectedScene, IReadOnlyList<string> ButtonIds, IReadOnlySet<string> EnabledHoldIds,
        long NavigationRevision, IReadOnlySet<int>? ActivityGuardRegions = null)
    {
        // Golden rule: a long press requires broken lettering. A colour or
        // reflectance change beneath still-readable letters is never a press.
        // The caption supplies its own damage evidence. Whole-button coverage
        // and acquisition confirmation govern hand searches, never this hold.
        public IReadOnlyList<string> HeldButtons(HandAcquisitionPresenceResult? result) =>
            result?.TextPatterns?.Where(pattern => pattern.CaptionHoldEvidence &&
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

        // Menu paging yields to activity anywhere else on the board. Keep the
        // actual held/clear observations intact: a veto is not proof of release.
        public IReadOnlyList<string> BlockedButtons(HandAcquisitionPresenceResult? result)
        {
            // Roulette's footer lies under the reaching arm. Use the same
            // camera frame's table-caption evidence before either worker can
            // finish a footer hold, even if hand recognition has not completed.
            if (ActivityGuardRegions is not null && result?.TextPatterns?.Any(pattern =>
                    ActivityGuardRegions.Contains(pattern.ControlRegion) &&
                    (pattern.CaptionHoldEvidence || pattern.CaptionReflectanceChanged)) == true)
                return EnabledHoldIds.ToArray();
            if (ExpectedScene.BoardDisturbanceExclusionRegion is null || result is null) return [];
            bool elsewhere = result.OutsideControlDisturbance || result.TextPatterns?.Any(pattern =>
                pattern.CaptionHoldEvidence && pattern.ControlRegion >= 0 && pattern.ControlRegion < ButtonIds.Count &&
                ButtonIds[pattern.ControlRegion] is not ("menu-scroll-down" or "menu-scroll-up")) == true;
            return elsewhere ? ButtonIds.Where(id => id is "menu-scroll-down" or "menu-scroll-up").ToArray() : [];
        }
    }

    public HoldButtonContext? GetHoldButtonContext(DateTimeOffset frameTime)
    {
        lock (_gate)
        {
            RefreshWaterStickPresence();
            var now = HoldClock();
            _boardSession.TickControlVisibility(_blackjackClock());
            var buttons = _boardSession.Buttons;
            if (!AcquisitionBoardReady || HoldControlsSettling(frameTime) || !buttons.Any(button => button.IsHold && button.Enabled) ||
                HasBoardControlDrawerAnimation() || frameTime < _holdNavigationStartedAt || frameTime > now ||
                now - frameTime > TimeSpan.FromMilliseconds(350)) return null;
            var key = new HoldSceneKey(_boardSession.Screen, _boardSession.NavigationRevision,
                string.Join('|', buttons.Select(button => $"{button.Id}:{button.Label}:{button.Enabled}:{button.Bounds}")),
                PaintBoardAspect(), _boardSession.Screen switch
                {
                    BoardScreen.Slots => _boardSession.SlotsState.BuyCost,
                    // The reach veto also compares table captions, so placed
                    // wagers must refresh their reference, not only the footer.
                    BoardScreen.Roulette => _boardSession.RouletteState.Revision,
                    _ => 0
                },
                _holdControlBrightness, _boardCameraMap, _boardSurfaceMap);
            if (_holdSceneKey != key || _holdExpectedScene is null)
            {
                if (CaptureExpectedAcquisitionScene() is not { } scene) return null;
                if (_boardSession.Screen == BoardScreen.Menu)
                {
                    var arrow = BoardSession.MenuScrollButtonBounds;
                    // The finger reaches this bottom-row arrow from the board
                    // edge. Its approach below the plate is part of the press,
                    // not a separate disturbance elsewhere on the menu.
                    scene = scene with { BoardDisturbanceExclusionRegion = new(
                        arrow.X - .015, arrow.Y - .015, arrow.Width + .03, 1 - arrow.Y + .015) };
                }
                // Disabled artwork, changed captions or resized letters cannot
                // arm a later enabled control. Verify this exact appearance
                // uncovered before accepting its next camera-driven press.
                var appearances = buttons.ToDictionary(button => button.Id, button =>
                    new HoldControlAppearance(button.Label, button.Enabled, button.Bounds,
                        button.Id == "slot-buy" ? _boardSession.SlotsState.BuyCost : 0));
                bool sameGeometry = _holdSceneKey is { } previous && previous.Screen == key.Screen && previous.Navigation == key.Navigation &&
                    previous.Aspect == key.Aspect && previous.ControlBrightness == key.ControlBrightness && Equals(previous.CameraMap, key.CameraMap) &&
                    Equals(previous.SurfaceMap, key.SurfaceMap);
                // A sibling changing availability must not interrupt a hold
                // whose own caption and surface are unchanged.
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
                buttons.Where(button => button.IsHold && button.Enabled).Select(button => button.Id).ToHashSet(),
                _boardSession.NavigationRevision,
                _boardSession.Screen == BoardScreen.Roulette
                    ? buttons.Select((button, index) => (button, index)).Where(item => !item.button.IsHold)
                        .Select(item => item.index).ToHashSet() : null);
        }
    }

    /// <summary>Applies one camera frame's hold evidence; returns the buttons it activated.</summary>
    public IReadOnlyList<string> ObserveHoldButtons(HoldButtonContext? context, IReadOnlyList<string> heldIds,
        DateTimeOffset frameTime, IReadOnlyCollection<string>? clearedIds = null,
        IReadOnlyCollection<string>? blockedIds = null)
    {
        lock (_gate)
        {
            RefreshWaterStickPresence();
            if (ControlSwipeTraversalActive(HoldClock()) ||
                frameTime <= _controlSwipeHoldNotBefore ||
                frameTime < _holdNavigationStartedAt || HoldControlsSettling(frameTime) ||
                context is null || !IsCurrentHoldContext(context) ||
                !AcquisitionBoardReady || HasBoardControlDrawerAnimation()) return [];
            if (HoldButtonsBlockedByBoardActivity(HoldClock()))
                blockedIds = _boardSession.Buttons.Where(button => button.IsHold).Select(button => button.Id)
                    .Concat(blockedIds ?? []).Distinct().ToArray();
            var activated = _boardSession.ObserveHeldButtons(heldIds, frameTime, HoldClock(), clearedIds, blockedIds);
            if (activated.Count > 0)
            {
                if (context.ButtonIds.Contains("paint-drawer-close") || context.ButtonIds.Contains("paint-drawer-open"))
                {
                    SyncPaintSession();
                    NotePaintUserActivity();
                    if (activated.Contains("paint-save")) QueuePaintSaveRequest(frameTime);
                }
                QueuePhotoCopyHoldAction(activated, frameTime);
                foreach (var id in activated) RetryGlobeModelAfterAction(id);
                SyncPhotoCopySession();
            }
            return activated;
        }
    }

    // Caption workers publish independently of hand/UI work. Recheck the exact
    // visit and reference after acquiring the scene gate, including a departure
    // and return to a board whose button IDs happen to be identical.
    private bool IsCurrentHoldContext(HoldButtonContext context) =>
        context.Revision == _holdRevision && context.NavigationRevision == _boardSession.NavigationRevision &&
        ReferenceEquals(context.ExpectedScene, _holdExpectedScene) &&
        context.ButtonIds.SequenceEqual(_boardSession.Buttons.Select(button => button.Id));

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
        "globe-drawer-open" or "globe-drawer-close" or "photo-drawer-open" or "photo-drawer-close" or
            "water-drawer-open" or "water-drawer-close" or "paint-drawer-open" or "paint-drawer-close" or
            "menu-scroll-down" or "menu-scroll-up" => 25,
        _ when button.Id.StartsWith("slot-", StringComparison.Ordinal) => 18,
        _ when button.Id.StartsWith("bj-", StringComparison.Ordinal) => 13,
        _ when button.Id.StartsWith("globe-", StringComparison.Ordinal) => 15,
        _ when button.Id.StartsWith("water-garden-", StringComparison.Ordinal) => 15,
        "paint-save" or "paint-reset" => 15,
        _ when button.Id.StartsWith("photo-", StringComparison.Ordinal) || button.Id == "capture-again" ||
            button.Id == "menu" && button.IsHold => 15,
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

    private DateTimeOffset HoldClock() => _boardSession.Screen switch
    {
        BoardScreen.Football => _footballClock(),
        BoardScreen.StrykersRun => _strykersRunClock(),
        BoardScreen.Globe => _globeClock(),
        BoardScreen.WaterGarden => _waterClock(),
        BoardScreen.Paint => _paintClock(),
        _ => _blackjackClock()
    };

    private bool OnHoldButton(PixelPoint camera)
    {
        if (_boardCameraMap is null || _boardSurfaceMap is null) return false;
        var board = _boardSurfaceMap.InverseTransform(_boardCameraMap.Transform(new(camera.X, camera.Y)));
        return _boardSession.Buttons.Any(button => button.IsHold && button.Bounds.Contains(board.X, board.Y));
    }

    private static readonly Vector2[] HoldLightCornerDirections = Enumerable.Range(0, 36)
        .Select(index =>
        {
            double angle = (index / 9 - 1 + index % 9 / 8d) * Math.PI / 2;
            return new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
        }).ToArray();

    // Projected light over a hold button would erase its caption from the
    // camera's view and look like a finger. Follow its rounded surface, including
    // the alignment margin, so the protected area has no dark square corners.
    private CanvasGeometry? HoldButtonLightClip(CanvasDrawingSession ds, Rect output)
    {
        if (_boardSurfaceMap is null) return null;
        var holds = _boardSession.Buttons.Where(button => button.IsHold).ToArray();
        bool globeRails = _boardSession.GlobeDrawerOpen && (_boardSession.ButtonsVisible || BoardControlsAnimating);
        if (holds.Length == 0 && !globeRails) return null;
        var area = CanvasGeometry.CreateRectangle(ds.Device, output);
        foreach (var button in holds)
        {
            const double margin = .006;
            var b = button.Bounds;
            double radius = Math.Min(BoardButtonCornerRadius(button) / BoardSurfaceSize,
                Math.Min(b.Width, b.Height) / 2) + margin;
            ExcludeRounded(new Rect(b.X - margin, b.Y - margin, b.Width + 2 * margin, b.Height + 2 * margin),
                radius, radius);
        }
        if (globeRails)
        {
            // Keep the entire rounded housing dark, including the label and
            // pointer gutter, without restoring square spotlight cutouts.
            const double margin = .006;
            for (int index = 0; index < 2; index++)
            {
                var panel = GlobeRailPanelBounds(zoom: index == 1);
                ExcludeRounded(new Rect(panel.X - margin, panel.Y - margin,
                    panel.Width + 2 * margin, panel.Height + 2 * margin),
                    GlobePitchPanelCornerRadius / BoardSurfaceSize / PaintBoardAspect() + margin,
                    GlobePitchPanelCornerRadius / BoardSurfaceSize + margin);
            }
        }
        return area;

        void ExcludeRounded(Rect bounds, double radiusX, double radiusY)
        {
            var corners = new Vector2[HoldLightCornerDirections.Length];
            for (int index = 0; index < corners.Length; index++)
            {
                int corner = index / 9;
                double x = corner < 2 ? bounds.Right - radiusX : bounds.X + radiusX;
                double y = corner is 0 or 3 ? bounds.Y + radiusY : bounds.Bottom - radiusY;
                var direction = HoldLightCornerDirections[index];
                // Map the curved outline point by point: an affine rounded
                // rectangle would not follow a perspective-calibrated board.
                var point = _boardSurfaceMap.Transform(new Point2(x + direction.X * radiusX, y + direction.Y * radiusY));
                corners[index] = new((float)(output.X + point.X * output.Width),
                    (float)(output.Y + point.Y * output.Height));
            }
            using var hole = CanvasGeometry.CreatePolygon(ds.Device, corners);
            var remaining = area.CombineWith(hole, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
            area.Dispose();
            area = remaining;
        }
    }
}
