using ProjectTabletop.App.Camera;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Only the single inference worker accesses this image history.
    private readonly HandAcquisitionPresenceTracker _handAcquisitionPresence = new();
    private long _handAcquisitionContextRevision = -1;
    private object? _lastHandAcquisitionDetection;

    private sealed record HandAcquisitionQuery(IReadOnlyList<HandAcquisitionHint> Hints,
        IReadOnlyList<HandTrackingBounds> SearchRegions, HandAcquisitionPresenceResult? Presence)
    {
        // Only current measured button obstruction can start a search light.
        public IReadOnlyList<HandAcquisitionHint> LightingHints => Presence?.Hints ?? [];
    }

    private HandAcquisitionQuery FindHandAcquisitionHints(CameraFrame frame,
        SceneCompositor.HandAcquisitionContext? context, bool engineReset)
    {
        if (engineReset || context?.Revision != _handAcquisitionContextRevision)
            _handAcquisitionPresence.Reset();
        _handAcquisitionContextRevision = context?.Revision ?? -1;
        return CreateHandAcquisitionQuery(frame, context, _handAcquisitionPresence, DateTimeOffset.UtcNow);
    }

    private static HandAcquisitionQuery CreateHandAcquisitionQuery(CameraFrame frame,
        SceneCompositor.HandAcquisitionContext? context, HandAcquisitionPresenceTracker tracker, DateTimeOffset now)
    {
        if (context is null) return new([], [], null);
        var presence = (context.ObserveMotion || context.IlluminatedHint is not null) && context.ExpectedScene is { } expected
            ? tracker.Update(frame.Width, frame.Height, frame.Stride, frame.Bgra,
                context.SearchPolygon, expected, frame.Timestamp, now,
                context.IlluminatedHint, context.IlluminationStartedAt) : null;
        IReadOnlyList<HandAcquisitionHint> hints = context.IlluminatedHint is { } illuminated ? [illuminated] :
            presence?.Hints ?? [];
        var regions = new List<HandTrackingBounds>(2);
        // A newly obstructed button first lights up. Acquisition uses its crop
        // on subsequent frames, when the camera can see the illumination.
        bool waitingForLight = context.RequiresSearchIllumination && context.IlluminatedHint is null && hints.Count > 0;
        if (!waitingForLight)
        {
            regions.AddRange(hints.Take(2).Select(hint => hint.SearchBounds));
            // Photo Copy's object field is also a point of interest. Keep its
            // separate capture gesture without searching the whole webcam.
            if (context.ContinuousSearchPolygon is { Length: >= 3 } polygon)
            {
                double left = polygon.Min(point => point.X), top = polygon.Min(point => point.Y);
                double width = polygon.Max(point => point.X) - left, height = polygon.Max(point => point.Y) - top;
                int side = (int)Math.Clamp(Math.Ceiling(Math.Max(Math.Min(width, height), Math.Max(width, height) / 2)),
                    32, Math.Min(frame.Width, frame.Height));
                // Two overlapping squares cover the rectangular field without
                // stretching it into the palm model's square input.
                foreach (double position in new[] { 0.0, 1.0 })
                {
                    double x = width >= height ? left + position * Math.Max(0, width - side) : left + (width - side) / 2;
                    double y = height > width ? top + position * Math.Max(0, height - side) : top + (height - side) / 2;
                    var region = new HandTrackingBounds(Math.Clamp(Math.Round(x), 0, frame.Width - side),
                        Math.Clamp(Math.Round(y), 0, frame.Height - side), side, side);
                    if (regions.Contains(region)) continue;
                    if (regions.Count == 2) break;
                    regions.Add(region);
                }
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
            context.RestrictAcquisitionToSearchRegions, context.RequiresSearchIllumination,
            hints = query.Hints, lightingHints = query.LightingHints,
            searchRegions = query.SearchRegions, presence = query.Presence,
            handCount, searches = trace?.Searches,
            selectedSources = trace?.SelectedCandidateIndices.Select(index =>
                trace.Candidates.First(candidate => candidate.Index == index).Source).ToArray()
        };
    }
}
