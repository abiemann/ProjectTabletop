using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private static readonly BoardRect PaintTitleBounds = new(.06, .018, .10, .028);
    private static readonly BoardRect PaintStatusBounds = new(.35, .77, .30, .065);
    // Paint may spread beneath overlays, but marker input excludes the controls.
    private HandTrackingBounds[] PaintIgnoredRegions()
    {
        var regions = BoardControlsForRendering
        .Select(button => button.Bounds).Append(PaintTitleBounds).Append(PaintStatusBounds)
        .Select(bounds =>
        {
            // The floating chrome extends beyond its hit box (6px shadow and
            // hover glow). Include camera blur and roughly two analysis samples
            // of registration tolerance so these known edges cannot seed paint.
            const double margin = .02;
            double left = Math.Max(0, bounds.X - margin), top = Math.Max(0, bounds.Y - margin);
            return new HandTrackingBounds(left, top, Math.Min(1, bounds.X + bounds.Width + margin) - left,
                Math.Min(1, bounds.Y + bounds.Height + margin) - top);
        }).ToList();
        if (PaintControlsInMotion) regions.Add(new(0, .82, 1, .18));
        return regions.ToArray();
    }

    private bool PaintInputReady => !_disposed && _boardSession.Screen == BoardScreen.Paint &&
        !_blackOutput && !_boardSetup && _markerSetup is null && !IsBoardRevealActive && _calibrationTarget < 0 &&
        _boardMediaClip is not null && _boardCameraMap is not null && _boardSurfaceMap is not null;

    public object GetPaintInputDiagnostics()
    {
        lock (_gate)
            return new { active = PaintInputReady, inputMode = "double-bars",
                handPaintingEnabled = false, handDetectionEnabled = false,
                markerActive = PaintMarkerRecentlySeen, markerFrameUtc = _paintMarkerFrameTime,
                markerPendingPathPoints = _paintMarkerPath.Count,
                spotlightsEnabled = false, buttonSpotlightsEnabled = false };
    }

}
