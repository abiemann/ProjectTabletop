using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public enum MarkerSetupStage { NeutralAcquisition, SceneValidation, MovementValidation }

/// <summary>Revision changes for a new target or stage; progress/text updates retain its presentation time.</summary>
public sealed record MarkerSetupPresentation(MarkerSetupStage Stage, string Instruction, double Progress,
    BoardRect Target, long Revision, DateTimeOffset? PresentedAt, BoardRect? RouteEnd = null);

public sealed partial class SceneCompositor
{
    private MarkerSetupPresentation? _markerSetup;
    private long _markerSetupRevision, _markerSetupVisualRevision, _markerSetupRenderedRevision;

    public bool MarkerSetupActive { get { lock (_gate) return _markerSetup is not null; } }
    public bool MarkerSetupReady
    {
        get
        {
            lock (_gate) return MappedBoardInputReady &&
                (_boardSession.Screen != BoardScreen.WaterGarden || WaterGardenEntranceProgress(_waterClock()) >= 1);
        }
    }
    public MarkerSetupPresentation? MarkerSetupState { get { lock (_gate) return _markerSetup; } }

    public long BeginMarkerSetup(MarkerSetupStage stage, string instruction, double progress, BoardRect target,
        BoardRect? routeEnd = null)
    {
        lock (_gate)
        {
            ValidateMarkerSetupTarget(target);
            if (routeEnd is { } end) ValidateMarkerSetupTarget(end);
            _markerSetup = new(stage, instruction, Math.Clamp(progress, 0, 1), target, ++_markerSetupRevision, null, routeEnd);
            InvalidateMarkerSetupPresentation();
            return _markerSetup.Revision;
        }
    }

    public long UpdateMarkerSetup(MarkerSetupStage stage, string instruction, double progress, BoardRect target,
        BoardRect? routeEnd = null)
    {
        lock (_gate)
        {
            ValidateMarkerSetupTarget(target);
            if (routeEnd is { } end) ValidateMarkerSetupTarget(end);
            if (_markerSetup is not { } previous) return BeginMarkerSetup(stage, instruction, progress, target, routeEnd);
            bool targetChanged = previous.Stage != stage || previous.Target != target || previous.RouteEnd != routeEnd;
            var next = new MarkerSetupPresentation(stage, instruction, Math.Clamp(progress, 0, 1), target,
                targetChanged ? ++_markerSetupRevision : previous.Revision, targetChanged ? null : previous.PresentedAt, routeEnd);
            if (next == previous) return previous.Revision;
            _markerSetup = next;
            if (targetChanged) InvalidateMarkerSetupPresentation();
            else _markerSetupVisualRevision++;
            return next.Revision;
        }
    }

    public void EndMarkerSetup()
    {
        lock (_gate)
        {
            if (_markerSetup is null) return;
            _markerSetup = null;
            InvalidateMarkerSetupPresentation();
        }
    }

    /// <summary>Camera-pixel polygon using the same player-facing homography as the projected target.</summary>
    public IReadOnlyList<PixelPoint> GetMarkerSetupCameraRegion(BoardRect region)
    {
        lock (_gate)
        {
            ValidateMarkerSetupTarget(region);
            if (_boardCameraMap is null || _boardSurfaceMap is null) return [];
            PixelPoint Map(double u, double v)
            {
                var point = _boardCameraMap.InverseTransform(_boardSurfaceMap.Transform(new(u, v)));
                return new(point.X, point.Y);
            }
            return [Map(region.X, region.Y), Map(region.X + region.Width, region.Y),
                Map(region.X + region.Width, region.Y + region.Height), Map(region.X, region.Y + region.Height)];
        }
    }

    public IReadOnlyList<PixelPoint> GetMarkerSetupCameraTarget()
    {
        lock (_gate) return _markerSetup is { } state ? GetMarkerSetupCameraRegion(state.Target) : [];
    }

    public bool TryMapMarkerSetupCameraPoint(Vector2 cameraPoint, out Vector2 boardPoint)
    {
        lock (_gate)
        {
            boardPoint = default;
            if (_boardCameraMap is null || _boardSurfaceMap is null ||
                !float.IsFinite(cameraPoint.X) || !float.IsFinite(cameraPoint.Y)) return false;
            try
            {
                var point = _boardSurfaceMap.InverseTransform(_boardCameraMap.Transform(new(cameraPoint.X, cameraPoint.Y)));
                if (point.X is < 0 or > 1 || point.Y is < 0 or > 1) return false;
                boardPoint = new((float)point.X, (float)point.Y);
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }
    }

    private static void ValidateMarkerSetupTarget(BoardRect target)
    {
        if (!double.IsFinite(target.X) || !double.IsFinite(target.Y) ||
            !double.IsFinite(target.Width) || !double.IsFinite(target.Height) ||
            target.X < 0 || target.Y < 0 || target.Width <= 0 || target.Height <= 0 ||
            target.X + target.Width > 1 || target.Y + target.Height > 1)
            throw new ArgumentOutOfRangeException(nameof(target), "The marker target must be inside the normalized board.");
    }

    private void InvalidateMarkerSetupPresentation()
    {
        ResetPaintMarker();
        _markerSetupVisualRevision++;
        _renderedBoardState = null;
        _waterRenderedFrame = null;
        ClearEyeProjectionHistory();
        ClearAcquisitionLight();
        ClearHandSpotlights();
        _handTips = [];
        _handFrameTime = DateTimeOffset.MinValue;
        _handVisualResetThrough = MonotonicClock.UtcNow;
        _boardSession.ResetHoldCaptionEvidence();
        _holdExpectedScene = null;
        _holdSceneKey = null;
        _holdRevision++;
        _holdNavigationStartedAt = _blackjackClock();
        InvalidateControlSwipeContext(_blackjackClock());
        UpdateFootballAudio(_boardSession.FootballState, _footballClock());
    }

    private void MarkMarkerSetupPresented()
    {
        if (_markerSetup is { PresentedAt: null } state && _markerSetupRenderedRevision == state.Revision)
            _markerSetup = state with { PresentedAt = MonotonicClock.UtcNow };
    }

    // This is baked into the common board texture, so the detector's projection
    // reference contains exactly the same neutral field or real-scene overlay.
    private void DrawMarkerSetupPresentation(CanvasDrawingSession drawing, bool boardTexture = true)
    {
        if (_markerSetup is not { } state) return;
        bool neutral = state.Stage == MarkerSetupStage.NeutralAcquisition;
        if (neutral) drawing.FillRectangle(new Rect(0, 0, BoardSurfaceSize, BoardSurfaceSize),
            Color.FromArgb(255, 230, 230, 224));

        var accent = neutral ? Color.FromArgb(255, 28, 105, 125) : Color.FromArgb(255, 149, 239, 240);
        var target = DrawTarget(state.Target);
        if (state.RouteEnd is { } routeEnd)
        {
            routeEnd = DrawTarget(routeEnd);
            target = new(Math.Min(target.X, routeEnd.X), Math.Min(target.Y, routeEnd.Y),
                Math.Max(target.X + target.Width, routeEnd.X + routeEnd.Width) - Math.Min(target.X, routeEnd.X),
                Math.Max(target.Y + target.Height, routeEnd.Y + routeEnd.Height) - Math.Min(target.Y, routeEnd.Y));
        }

        BoardRect DrawTarget(BoardRect region)
        {
            var center = new Vector2((float)((region.X + region.Width / 2) * BoardSurfaceSize),
                (float)((region.Y + region.Height / 2) * BoardSurfaceSize));
            // Enclose all four ROI corners. Neither the outline nor its dark edging
            // enters the learning area and resembles a black marker bar.
            float radiusX = (float)(region.Width * .707107 + .012) * BoardSurfaceSize;
            float radiusY = (float)(region.Height * .707107 + .012) * BoardSurfaceSize;
            if (!neutral) drawing.DrawEllipse(center, radiusX, radiusY, Color.FromArgb(230, 12, 25, 29), 7);
            drawing.DrawEllipse(center, radiusX, radiusY, accent, 3);
            // Reserve the complete drawn outline, not just the smaller detection
            // rectangle, so the instruction panel cannot cover either ring.
            double halfWidth = (radiusX + (neutral ? 1.5 : 3.5)) / BoardSurfaceSize;
            double halfHeight = (radiusY + (neutral ? 1.5 : 3.5)) / BoardSurfaceSize;
            return new(region.X + region.Width * .5 - halfWidth,
                region.Y + region.Height * .5 - halfHeight, halfWidth * 2, halfHeight * 2);
        }

        double above = target.Y, below = 1 - target.Y - target.Height;
        double available = Math.Max(above, below);
        double height = Math.Min(.18, available - .035);
        if (height >= .075)
        {
            double y = above >= below ? Math.Max(.012, (above - height) / 2) :
                target.Y + target.Height + (below - height) / 2;
            var panel = new Rect(70, y * BoardSurfaceSize, 860, height * BoardSurfaceSize);
            if (!neutral) drawing.FillRoundedRectangle(panel, 14, 14, Color.FromArgb(225, 11, 24, 29));
            var ink = neutral ? Color.FromArgb(255, 23, 49, 53) : Colors.White;
            using var format = new CanvasTextFormat
            {
                FontFamily = "Segoe UI", FontSize = height < .15 ? 22 : 27,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center,
                WordWrapping = CanvasWordWrapping.Wrap
            };
            drawing.DrawText(state.Instruction, new Rect(panel.X + 20, panel.Y + 5,
                panel.Width - 40, panel.Height - 30), ink, format);
            var track = new Rect(panel.X + 90, panel.Y + panel.Height - 17, panel.Width - 180, 4);
            drawing.FillRoundedRectangle(track, 2, 2, neutral ? Color.FromArgb(255, 190, 198, 195) : Color.FromArgb(255, 65, 85, 88));
            if (state.Progress > 0) drawing.FillRoundedRectangle(
                new Rect(track.X, track.Y, track.Width * state.Progress, track.Height), 2, 2, accent);
        }
        if (boardTexture) _markerSetupRenderedRevision = state.Revision;
    }
}
