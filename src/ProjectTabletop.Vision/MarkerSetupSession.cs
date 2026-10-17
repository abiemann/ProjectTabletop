namespace ProjectTabletop.Vision;

public enum MarkerSetupStage { Learning, Validating, Moving, LiftOff, Ready }

/// <summary>A normalized point within the caller's selected board/player region.</summary>
public sealed record MarkerSetupTarget(string Name, PixelPoint Position, double Radius = .10);

/// <summary>Stages one median marker profile, validates fresh measured pairs across the board,
/// checks continuous movement in both directions, then verifies lift-off. No camera,
/// presentation, persistence or predicted pose is owned here.</summary>
public sealed class MarkerSetupSession
{
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMilliseconds(250);
    private readonly List<BlackTipProfile> _samples = [];
    private readonly BlackTipProfile? _initialProfile;
    private DateTimeOffset _lastFrame, _windowStart;
    private MarkerTipObservation? _learningMarker;
    private PixelPoint? _settledPosition;
    private double _progress;
    private DateTimeOffset _movementStarted, _movementLastPair;
    private PixelPoint? _movementLastPoint;
    private int _movementCoverage, _movementMovingFrames, _movementMissingFrames;
    private static readonly TimeSpan MaximumMovementGap = TimeSpan.FromMilliseconds(150);

    public MarkerSetupSession(IReadOnlyList<MarkerSetupTarget>? targets = null, BlackTipProfile? initialProfile = null)
    {
        targets ??= [new("Centre", new(.5, .5)), new("Left", new(.22, .5)),
            new("Right", new(.78, .5)), new("Near", new(.5, .78)), new("Far", new(.5, .22))];
        if (targets.Count is < 1 or > 9 || targets.Any(target => target is null ||
            string.IsNullOrWhiteSpace(target.Name) || !Normalized(target.Position) ||
            !double.IsFinite(target.Radius) || target.Radius is < .03 or > .25))
            throw new ArgumentException("Supply one to nine finite, named normalized setup targets.", nameof(targets));
        for (int first = 0; first < targets.Count; first++)
        for (int second = first + 1; second < targets.Count; second++)
            if (MarkerTipGeometry.Distance(targets[first].Position, targets[second].Position) < .01)
                throw new ArgumentException("Setup targets must cover distinct positions.", nameof(targets));
        if (initialProfile is not null && (!initialProfile.IsValid || initialProfile.BarCount != 2))
            throw new ArgumentException("Rechecking requires a valid existing double-bar profile.", nameof(initialProfile));
        Targets = Array.AsReadOnly(targets.ToArray());
        int start = 0, end = 0;
        double longest = 0;
        for (int first = 0; first < Targets.Count; first++)
        for (int second = first + 1; second < Targets.Count; second++)
        {
            double distance = MarkerTipGeometry.Distance(Targets[first].Position, Targets[second].Position);
            if (distance > longest + .000001) { longest = distance; start = first; end = second; }
        }
        MovementTargets = start == end ? [] : Array.AsReadOnly(new[] { Targets[start], Targets[end] });
        _initialProfile = initialProfile;
        Reset();
    }

    public MarkerSetupStage Stage { get; private set; } = MarkerSetupStage.Learning;
    public IReadOnlyList<MarkerSetupTarget> Targets { get; }
    public int TargetIndex { get; private set; }
    public IReadOnlyList<MarkerSetupTarget> MovementTargets { get; }
    public int MovementTraversalsCompleted { get; private set; }
    public int MovementTraversalCount => MovementTargets.Count == 2 ? 2 : 0;
    public MarkerSetupTarget? CurrentTarget => Stage switch
    {
        MarkerSetupStage.Validating => Targets[TargetIndex],
        MarkerSetupStage.Moving => MovementTargets[1 - MovementTraversalsCompleted % 2],
        _ => null
    };
    public BlackTipProfile? CandidateProfile { get; private set; }
    public double Progress => _progress;
    public string Reason { get; private set; } = "Place one complete double-bar marker in the target.";
    public int AcceptedFrames { get; private set; }
    public int ObservedFrames { get; private set; }
    public int FullPairFrames { get; private set; }
    public int MissingFrames { get; private set; }
    public TimeSpan MaximumFrameGap { get; private set; }

    public void ObserveLearning(BlackTipLearningResult sample, DateTimeOffset sourceFrameUtc, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (Stage != MarkerSetupStage.Learning || !AcceptFrame(sourceFrameUtc, now)) return;
        if (sample.Profile is not { IsValid: true, BarCount: 2 } profile || !ValidMarker(sample.Marker))
        {
            MissingFrames = Increment(MissingFrames);
            ResetProgress(); Reason = sample.Reason; return;
        }
        FullPairFrames = Increment(FullPairFrames);
        var marker = sample.Marker!;
        if (_samples.Count > 0 && (!Consistent(profile, _samples[0]) || _learningMarker is { } previous &&
            MarkerTipGeometry.Distance(previous.Center, marker.Center) > Math.Max(6, marker.RadiusPixels * .30)))
            ResetProgress();
        _learningMarker ??= marker;
        if (_samples.Count == 64) _samples.RemoveAt(0);
        _samples.Add(profile);
        Reason = "Keep the marker still while its shape is measured.";
        if (!Settle(sourceFrameUtc, TimeSpan.FromSeconds(1), 12)) return;
        double Median(Func<BlackTipProfile, double> select) => _samples.Select(select).Order().ElementAt(_samples.Count / 2);
        CandidateProfile = new(1, Median(profile => profile.Value), Median(profile => profile.Saturation),
            Median(profile => profile.NormalizedArea), Median(profile => profile.AspectRatio), 2,
            Median(profile => profile.SpacingRatio));
        Stage = MarkerSetupStage.Validating;
        ResetProgress();
        Reason = $"Move the marker to {Targets[0].Name} and hold it steady.";
    }

    /// <summary>Pass the current detector result and its measured, mapped centre. The caller
    /// must not substitute a held, predicted or temporally smoothed tracker position.</summary>
    public void ObserveValidation(MarkerTipDetectionResult detection, PixelPoint? normalizedMarker,
        DateTimeOffset sourceFrameUtc, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(detection);
        if (Stage is not (MarkerSetupStage.Validating or MarkerSetupStage.Moving or MarkerSetupStage.LiftOff) ||
            !AcceptFrame(sourceFrameUtc, now)) return;
        bool pair = detection.Candidates.Count == 1 && detection.AmbiguousMarkerCenters.Count == 0 &&
            detection.Reason == "black-bar-pair-candidate" && ValidMarker(detection.Candidates[0]);
        if (pair) FullPairFrames = Increment(FullPairFrames);
        else MissingFrames = Increment(MissingFrames);
        if (Stage == MarkerSetupStage.Moving)
        {
            ObserveMovement(detection, pair, normalizedMarker, sourceFrameUtc);
            return;
        }
        if (Stage == MarkerSetupStage.LiftOff)
        {
            bool clear = detection.Candidates.Count == 0 && detection.SupportingBars.Count == 0 &&
                detection.AmbiguousMarkerCenters.Count == 0 && detection.Reason == "no-black-bar-pair";
            if (!clear) { ResetProgress(); Reason = "Lift the whole marker off the board."; return; }
            Reason = "Keep the board clear while lift-off is checked.";
            if (Settle(sourceFrameUtc, TimeSpan.FromSeconds(1), 12))
            { Stage = MarkerSetupStage.Ready; Reason = "Marker measured and verified. Ready to save."; }
            return;
        }
        if (!pair || normalizedMarker is not { } point || !Normalized(point))
        {
            ResetProgress();
            Reason = detection.Candidates.Count > 1 || detection.AmbiguousMarkerCenters.Count > 0
                ? "Show only one unambiguous double-bar marker."
                : "Keep both bars visible on the board.";
            return;
        }
        var target = Targets[TargetIndex];
        if (MarkerTipGeometry.Distance(point, target.Position) > target.Radius)
        { ResetProgress(); Reason = $"Move the marker to {target.Name}."; return; }
        // A sweep through the target is not a settled sample, even if it stays
        // inside the circle. Use the actual mapped measurement, never a held pose.
        if (_settledPosition is { } settled && MarkerTipGeometry.Distance(point, settled) > target.Radius * .35)
            ResetProgress();
        _settledPosition ??= point;
        Reason = $"Hold the marker steady at {target.Name}.";
        if (!Settle(sourceFrameUtc, TimeSpan.FromMilliseconds(600), 8)) return;
        TargetIndex++;
        Stage = TargetIndex < Targets.Count ? MarkerSetupStage.Validating :
            MovementTraversalCount > 0 ? MarkerSetupStage.Moving : MarkerSetupStage.LiftOff;
        ResetProgress();
        Reason = Stage switch
        {
            MarkerSetupStage.LiftOff => "Lift the whole marker off the board.",
            MarkerSetupStage.Moving => MovementStartInstruction(),
            _ => $"Move the marker to {Targets[TargetIndex].Name}."
        };
    }

    /// <summary>Discard the current settle window after a presentation/calibration change.
    /// Completed targets and the staged profile remain; old frame timestamps stay rejected.</summary>
    public void ResetProgress()
    {
        _samples.Clear(); _learningMarker = null; _settledPosition = null;
        _windowStart = default; AcceptedFrames = 0;
        _movementStarted = _movementLastPair = default; _movementLastPoint = null;
        _movementCoverage = _movementMovingFrames = _movementMissingFrames = 0;
        _progress = Stage == MarkerSetupStage.Moving ? MovementTraversalsCompleted / 2.0 : 0;
    }

    public void Reset()
    {
        Stage = _initialProfile is null ? MarkerSetupStage.Learning : MarkerSetupStage.Validating;
        TargetIndex = 0; CandidateProfile = _initialProfile;
        MovementTraversalsCompleted = 0;
        _lastFrame = default; ObservedFrames = FullPairFrames = MissingFrames = 0;
        MaximumFrameGap = default; ResetProgress();
        Reason = Stage == MarkerSetupStage.Learning ? "Place one complete double-bar marker in the target." :
            $"Move the marker to {Targets[0].Name} and hold it steady.";
    }

    private string MovementStartInstruction() =>
        $"Start at the centre of {MovementTargets[MovementTraversalsCompleted % 2].Name}, then slide to {CurrentTarget!.Name} at playing speed.";

    private void RetryMovement(string reason)
    {
        ResetProgress();
        Reason = reason + " " + MovementStartInstruction();
    }

    private void ObserveMovement(MarkerTipDetectionResult detection, bool pair, PixelPoint? point,
        DateTimeOffset frame)
    {
        if (detection.Candidates.Count > 1 || detection.AmbiguousMarkerCenters.Count > 0)
        { RetryMovement("Show only one unambiguous double-bar marker."); return; }
        if (!pair || point is not { } position || !Normalized(position))
        {
            if (_movementLastPoint is not null && frame - _movementLastPair <= MaximumMovementGap)
            {
                _movementMissingFrames++;
                Reason = "Keep both bars flat and visible while sliding; tracking briefly dropped.";
            }
            else RetryMovement("Tracking was lost. Keep both bars flat and visible; improve the light if they blur.");
            return;
        }
        var start = MovementTargets[MovementTraversalsCompleted % 2];
        var end = CurrentTarget!;
        double dx = end.Position.X - start.Position.X, dy = end.Position.Y - start.Position.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        double along = ((position.X - start.Position.X) * dx + (position.Y - start.Position.Y) * dy) / (length * length);
        double across = Math.Abs((position.X - start.Position.X) * dy - (position.Y - start.Position.Y) * dx) / length;
        if (_movementLastPoint is not { } previous)
        {
            if (MarkerTipGeometry.Distance(position, start.Position) > Math.Min(start.Radius, length * .15))
            { Reason = MovementStartInstruction(); return; }
            _movementStarted = _movementLastPair = frame;
            _movementLastPoint = position;
            _movementCoverage = 1;
            AcceptedFrames = 1;
            Reason = $"Slide continuously to {end.Name} at playing speed. Keep both bars visible.";
            return;
        }
        double step = MarkerTipGeometry.Distance(previous, position);
        if (frame - _movementLastPair > MaximumMovementGap)
        { RetryMovement("Tracking paused during the movement. Keep both bars visible and retry."); return; }
        if (step > length * .24)
        { RetryMovement("The marker jumped between readings. Slide continuously without lifting it."); return; }
        if (across > Math.Max(start.Radius, end.Radius) + length * .15 || along is < -.20 or > 1.20)
        { RetryMovement("Keep the marker between the two targets."); return; }
        var elapsed = frame - _movementStarted;
        if (elapsed > TimeSpan.FromSeconds(8))
        { RetryMovement("Move between the targets in one continuous sweep at playing speed."); return; }
        _movementLastPair = frame;
        _movementLastPoint = position;
        AcceptedFrames++;
        if (step >= length * .008) _movementMovingFrames++;
        int band = Math.Clamp((int)(along * 5), 0, 4);
        // Require measured coverage of each part of the route in order. A held
        // endpoint, a partial bar or a predicted position cannot fill a gap.
        if (band == 0 || (_movementCoverage & (1 << (band - 1))) != 0)
            _movementCoverage |= 1 << band;
        _progress = (MovementTraversalsCompleted + Math.Clamp(along, 0, 1)) / 2;
        Reason = $"Slide continuously to {end.Name} at playing speed. Keep both bars visible.";
        if (along < .85 || MarkerTipGeometry.Distance(position, end.Position) > end.Radius) return;
        if (elapsed < TimeSpan.FromMilliseconds(700) || AcceptedFrames < 12 ||
            _movementMovingFrames < 8 || _movementCoverage != 31)
        { RetryMovement("The sweep needs continuous readings across the whole route. Take about one to three seconds."); return; }
        if (AcceptedFrames / (double)(AcceptedFrames + _movementMissingFrames) < .85)
        { RetryMovement("Tracking dropped too often. Keep the bars flat and improve the light or camera exposure before retrying."); return; }
        MovementTraversalsCompleted++;
        if (MovementTraversalsCompleted == MovementTraversalCount)
        {
            Stage = MarkerSetupStage.LiftOff;
            ResetProgress();
            Reason = "Movement checked in both directions. Lift the whole marker off the board.";
        }
        else
        {
            ResetProgress();
            Reason = MovementStartInstruction();
        }
    }

    private bool AcceptFrame(DateTimeOffset frame, DateTimeOffset now)
    {
        if (frame == default || now == default || frame > now + TimeSpan.FromMilliseconds(30) || now - frame > MaximumAge)
        { ResetProgress(); Reason = "Waiting for a fresh camera frame."; return false; }
        if (_lastFrame != default && frame <= _lastFrame) return false;
        if (_lastFrame != default)
        {
            var gap = frame - _lastFrame;
            if (gap > MaximumFrameGap) MaximumFrameGap = gap;
            if (gap > MaximumAge) ResetProgress();
        }
        _lastFrame = frame;
        ObservedFrames = Increment(ObservedFrames);
        return true;
    }

    private bool Settle(DateTimeOffset frame, TimeSpan duration, int frames)
    {
        if (_windowStart == default) _windowStart = frame;
        AcceptedFrames = Increment(AcceptedFrames);
        _progress = Math.Clamp(Math.Min(AcceptedFrames / (double)frames,
            (frame - _windowStart).TotalMilliseconds / duration.TotalMilliseconds), 0, 1);
        return _progress >= 1;
    }

    private static bool Consistent(BlackTipProfile sample, BlackTipProfile reference) =>
        Math.Abs(sample.Value - reference.Value) <= .08 &&
        Math.Abs(sample.NormalizedArea / reference.NormalizedArea - 1) <= .20 &&
        Math.Abs(sample.AspectRatio / reference.AspectRatio - 1) <= .15 &&
        Math.Abs(sample.SpacingRatio / reference.SpacingRatio - 1) <= .15;
    private static bool Normalized(PixelPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y) &&
        point.X is >= 0 and <= 1 && point.Y is >= 0 and <= 1;
    private static bool ValidMarker(MarkerTipObservation? marker) => marker?.Bar is { } bar &&
        double.IsFinite(marker.Center.X) && double.IsFinite(marker.Center.Y) &&
        double.IsFinite(marker.AreaPixels) && marker.AreaPixels >= 12 &&
        double.IsFinite(marker.RadiusPixels) && marker.RadiusPixels >= 1 &&
        new[] { bar.End1, bar.End2, bar.Side1, bar.Side2 }.All(point => double.IsFinite(point.X) && double.IsFinite(point.Y));
    private static int Increment(int count) => count < int.MaxValue ? count + 1 : count;
}
