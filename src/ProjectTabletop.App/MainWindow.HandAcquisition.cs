using ProjectTabletop.App.Camera;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Only the single inference worker accesses this image history.
    private readonly HandAcquisitionPresenceTracker _handAcquisitionPresence = new();
    private long _handAcquisitionContextRevision = -1;
    private long _handAcquisitionGeneration = -1;
    private object? _lastHandAcquisitionDetection;
#if DEBUG
    private CameraFrame? _lastHandAcquisitionFrame;
    private SceneCompositor.HandAcquisitionContext? _lastHandAcquisitionContext;
#endif

    private sealed record HandAcquisitionQuery(IReadOnlyList<HandAcquisitionHint> Hints,
        IReadOnlyList<HandTrackingBounds> SearchRegions, HandAcquisitionPresenceResult? Presence, int Width = 0, int Height = 0)
    {
        // Only current measured button obstruction can start a search light.
        public IReadOnlyList<HandAcquisitionHint> LightingHints => Presence?.Hints
            .Select(hint => hint.ConstrainToFrame(Width, Height)).ToArray() ?? [];
    }

    private HandAcquisitionQuery FindHandAcquisitionHints(CameraFrame frame,
        SceneCompositor.HandAcquisitionContext? context, long generation)
    {
        // A landmark-tracking gap invalidates hand identity, not the optics of
        // the unchanged generated labels. Keep that bounded registration cache;
        // presence confirmation independently requires fresh nearby timestamps.
        if (generation != _handAcquisitionGeneration || context?.Revision != _handAcquisitionContextRevision)
            _handAcquisitionPresence.Reset();
        _handAcquisitionGeneration = generation;
        _handAcquisitionContextRevision = context?.Revision ?? -1;
        return CreateHandAcquisitionQuery(frame, context, _handAcquisitionPresence, DateTimeOffset.UtcNow);
    }

    // Hold buttons keep their own detector: it must keep watching while a hand
    // is tracked and must not restart when a hold activation changes the board.
    private readonly HandAcquisitionPresenceTracker _holdPresence = new();
    private long _holdContextRevision = -1;
    private long _holdGeneration = -1;

    private sealed record HoldButtonQuery(IReadOnlyList<string> Held, IReadOnlyList<string> Cleared,
        HandAcquisitionPresenceResult? Presence);

    private HoldButtonQuery FindHeldButtons(CameraFrame frame, SceneCompositor.HoldButtonContext? context,
        long generation)
    {
        if (context is null) return new([], [], null);
        if (context.Revision != _holdContextRevision || generation != _holdGeneration) _holdPresence.Reset();
        _holdContextRevision = context.Revision;
        _holdGeneration = generation;
        var presence = _holdPresence.Update(frame.Width, frame.Height, frame.Stride, frame.Bgra,
            context.SearchPolygon, context.ExpectedScene, frame.Timestamp, DateTimeOffset.UtcNow);
        return new(context.HeldButtons(presence), context.ClearedButtons(presence), presence);
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
        // Fingers on a long-press button feed its hold timer only, never a hand search.
        hints = hints.Where(hint => context.HoldControls?.Any(outline => InsideQuad(hint.Center, outline)) != true)
            .Select(hint => hint.ConstrainToFrame(frame.Width, frame.Height)).ToArray();
        var regions = new List<HandTrackingBounds>(2);
        // Qualified presence already requires two fresh observations and at
        // least 7% of both the control and its label. Try the untouched camera
        // crop first: extra white light can hide an otherwise clear hand pose.
        // A rejected model fit can still request the existing search light.
        regions.AddRange(hints.Take(2).Select(hint => AcquisitionSearchBounds(hint, frame.Width, frame.Height)));
        // A still hand under an unchanged light gives the model the same crop
        // every frame, so one miss repeats until the hand moves: a live Monopoly
        // Exit Game search stayed empty for 5 s. Alternate with closer views.
        if (context.IlluminatedHint is not null && regions.Count > 0 &&
            LitSearchView(regions[0], frame.Timestamp - context.IlluminationStartedAt, frame.Width, frame.Height) is { } view)
            regions[0] = view;
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
        foreach (var lost in context.LostHands ?? [])
        {
            if (regions.Count == 2) break;
            if (LostHandBounds(lost, frame.Width, frame.Height) is { } bounds && !regions.Contains(bounds)) regions.Add(bounds);
        }
        return new(hints, regions, presence, frame.Width, frame.Height);
    }

    // Every other 100 ms of a lit search, one 2/3-size corner of a large crop.
    // Smaller crops already get wider context views from the engine.
    private static HandTrackingBounds? LitSearchView(HandTrackingBounds primary, TimeSpan lit, int width, int height)
    {
        int step = (int)Math.Floor(lit.TotalMilliseconds / 100);
        if (step < 0 || step % 2 == 0 || primary.Width < Math.Min(width, height) * .5) return null;
        int side = (int)Math.Round(primary.Width * 2 / 3), corner = step / 2 % 4;
        return new(Math.Clamp(primary.X + (corner % 2 == 0 ? 0 : primary.Width - side), 0, width - side),
            Math.Clamp(primary.Y + (corner < 2 ? 0 : primary.Height - side), 0, height - side), side, side);
    }

    // The acquisition crop that found these hands was 60% of the frame; keep
    // that context around the last landmarks so a partly lit hand is whole.
    private static HandTrackingBounds? LostHandBounds(HandDetection hand, int width, int height)
    {
        var points = hand.Landmarks.Where(point => double.IsFinite(point.X) && double.IsFinite(point.Y)).ToArray();
        if (points.Length == 0) return null;
        double left = points.Min(point => point.X), right = points.Max(point => point.X);
        double top = points.Min(point => point.Y), bottom = points.Max(point => point.Y);
        int limit = Math.Min(width, height);
        int side = (int)Math.Ceiling(Math.Clamp(Math.Max(right - left, bottom - top) * 2, limit * .3, limit * .6));
        return new(Math.Clamp(Math.Round((left + right - side) / 2), 0, width - side),
            Math.Clamp(Math.Round((top + bottom - side) / 2), 0, height - side), side, side);
    }

    private static HandTrackingBounds AcquisitionSearchBounds(HandAcquisitionHint hint, int width, int height)
    {
        if (hint.ConstrainToFrame(width, height).ValidatedCandidateBounds is not { } candidate)
            return hint.SearchBounds;
        // Keep connected palm context and the measured caption core together.
        // These pixels guide fresh model inference; they do not define a pose.
        double core = hint.RadiusPixels * .64;
        double left = Math.Min(candidate.X, hint.Center.X - core);
        double top = Math.Min(candidate.Y, hint.Center.Y - core);
        double right = Math.Max(candidate.X + candidate.Width, hint.Center.X + core);
        double bottom = Math.Max(candidate.Y + candidate.Height, hint.Center.Y + core);
        int side = (int)Math.Ceiling(Math.Clamp(Math.Max(right - left, bottom - top) * 1.25,
            Math.Min(hint.SearchBounds.Width, Math.Min(width, height) * .60), Math.Min(width, height) * .60));
        return new(Math.Clamp(Math.Round((left + right - side) / 2), 0, width - side),
            Math.Clamp(Math.Round((top + bottom - side) / 2), 0, height - side), side, side);
    }

    private void DescribeHandAcquisition(CameraFrame frame, SceneCompositor.HandAcquisitionContext? context,
        HandAcquisitionQuery query, HandTrackingDiagnostics? trace, int handCount)
    {
#if DEBUG
        // Keep the exact inference input paired with its decision. The latest
        // camera frame may already show a different phase of our search light.
        _lastHandAcquisitionFrame = frame;
        _lastHandAcquisitionContext = context;
#endif
        _lastHandAcquisitionDetection = context is null ? null : new
        {
            frameTime = frame.Timestamp, context.Revision, context.ObserveMotion,
            context.RestrictAcquisitionToSearchRegions, context.AllowsSearchIllumination,
            hints = query.Hints, lightingHints = query.LightingHints,
            searchRegions = query.SearchRegions, presence = query.Presence,
            handCount, searches = trace?.Searches,
            candidates = trace?.Candidates.Select(candidate => new
            {
                candidate.Index, candidate.Source, candidate.PalmScore,
                candidate.HandConfidence, candidate.Result, candidate.NmsResult
            }).ToArray(),
            selectedSources = trace?.SelectedCandidateIndices.Select(index =>
                trace.Candidates.First(candidate => candidate.Index == index).Source).ToArray()
        };
    }
}
