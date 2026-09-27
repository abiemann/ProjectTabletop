using ProjectTabletop.App.Camera;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Only the single inference worker accesses this image history.
    private readonly HandAcquisitionMotionTracker _handAcquisitionMotion = new();
    private readonly HandAcquisitionPresenceTracker _handAcquisitionPresence = new();
    private long _handAcquisitionContextRevision = -1;
    private int _handAcquisitionSweep;
    private object? _lastHandAcquisitionDetection;

    private sealed record HandAcquisitionQuery(IReadOnlyList<HandAcquisitionHint> Hints,
        IReadOnlyList<HandTrackingBounds> SearchRegions, HandAcquisitionPresenceResult? Presence);

    private HandAcquisitionQuery FindHandAcquisitionHints(CameraFrame frame,
        SceneCompositor.HandAcquisitionContext? context, bool engineReset)
    {
        if (engineReset || context?.Revision != _handAcquisitionContextRevision)
        {
            _handAcquisitionPresence.Reset();
            _handAcquisitionSweep = 0;
        }
        if (engineReset || context?.Revision != _handAcquisitionContextRevision || context?.ObserveMotion != true)
            _handAcquisitionMotion.Reset();
        _handAcquisitionContextRevision = context?.Revision ?? -1;
        if (context is null || !context.ObserveMotion && context.IlluminatedHint is null) return new([], [], null);
        var presence = context.ExpectedScene is { } expected
            ? _handAcquisitionPresence.Update(frame.Width, frame.Height, frame.Stride, frame.Bgra,
                context.SearchPolygon, expected, frame.Timestamp, DateTimeOffset.UtcNow,
                context.IlluminatedHint, context.IlluminationStartedAt) : null;
        IReadOnlyList<HandAcquisitionHint> hints = context.IlluminatedHint is { } illuminated ? [illuminated] :
            presence is { Hints.Count: > 0 } ? presence.Hints :
            _handAcquisitionMotion.Update(frame.Width, frame.Height, frame.Stride, frame.Bgra,
                context.SearchPolygon, frame.Timestamp, DateTimeOffset.UtcNow);
        var regions = new List<HandTrackingBounds>(2);
        if (hints.Count > 0) regions.Add(hints[0].SearchBounds);
        // Search the known controls even when a stationary hand produces no
        // motion or residual hint. These crop hints NEVER create a search light.
        if (context.StationarySearchCenters is { Length: > 0 } centers)
        {
            double width = context.SearchPolygon.Max(point => point.X) - context.SearchPolygon.Min(point => point.X);
            double height = context.SearchPolygon.Max(point => point.Y) - context.SearchPolygon.Min(point => point.Y);
            int side = (int)Math.Clamp(Math.Max(width, height) * .42, 32, Math.Min(frame.Width, frame.Height));
            for (int attempted = 0; attempted < centers.Length && regions.Count < 2; attempted++)
            {
                var center = centers[_handAcquisitionSweep % centers.Length];
                _handAcquisitionSweep = (_handAcquisitionSweep + 1) % centers.Length;
                var region = new HandTrackingBounds(Math.Clamp(Math.Round(center.X - side / 2.0), 0, frame.Width - side),
                    Math.Clamp(Math.Round(center.Y - side / 2.0), 0, frame.Height - side), side, side);
                if (regions.Any(other => Math.Abs(other.X - region.X) < side * .15 &&
                    Math.Abs(other.Y - region.Y) < side * .15)) continue;
                regions.Add(region);
            }
        }
        return new(hints, regions, presence);
    }

    private void DescribeHandAcquisition(CameraFrame frame, SceneCompositor.HandAcquisitionContext? context,
        HandAcquisitionQuery query, HandTrackingDiagnostics? trace, int handCount)
    {
        _lastHandAcquisitionDetection = context is null ? null : new
        {
            frameTime = frame.Timestamp, context.Revision, context.ObserveMotion,
            hints = query.Hints, searchRegions = query.SearchRegions, presence = query.Presence,
            handCount, searches = trace?.Searches,
            selectedSources = trace?.SelectedCandidateIndices.Select(index =>
                trace.Candidates.First(candidate => candidate.Index == index).Source).ToArray()
        };
    }
}
