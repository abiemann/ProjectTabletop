namespace ProjectTabletop.Vision;

/// <summary>Tracks measured football markers without manufacturing motion during a camera
/// gap. A learned pair may continue from one physically validated strip for at most 350 ms
/// after the last complete pair. Prediction only associates measurements and places a search
/// window; it is never published as a controller position.</summary>
public sealed class FootballMarkerTracker
{
    private static readonly TimeSpan PartialLifetime = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan IdentityLifetime = TimeSpan.FromMilliseconds(750);
    private ColorTipObservation? _full, _measured;
    private DateTimeOffset _lastFull, _lastMeasured, _lastFrame;
    private PixelPoint _velocity;
    private bool _confirmed;

    public void Reset()
    {
        _full = _measured = null;
        _lastFull = _lastMeasured = _lastFrame = default;
        _velocity = new(0, 0);
        _confirmed = false;
    }

    /// <summary>A user-selected full marker seeds the normal two-frame acquisition.</summary>
    public void Acquire(ColorTipObservation observation, DateTimeOffset frameTime)
    {
        if (!Valid(observation)) throw new ArgumentException("Invalid black-marker observation.", nameof(observation));
        Reset();
        _full = _measured = observation;
        _lastFull = _lastMeasured = _lastFrame = frameTime;
    }

    public BlackTipSearchHint? GetSearchHint(DateTimeOffset frameTime) =>
        _confirmed && _full is not null && frameTime >= _lastFull && frameTime - _lastFull <= PartialLifetime
            ? new(_full, PredictedCenter(frameTime), _lastFull) : null;

    public FootballTipDecision Update(int player, BlackTipProfile profile, ColorTipDetectionResult detection,
        Func<PixelPoint, PixelPoint?> toField, DateTimeOffset frameTime, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(toField);
        if (!profile.IsValid) throw new ArgumentException("Invalid black-marker profile.", nameof(profile));
        if (player is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(player));
        // Neither stale images nor an unavailable reference refresh the caller's freshness
        // timer. Keep identity briefly so a single delayed frame does not force reacquisition.
        if (frameTime > now + TimeSpan.FromMilliseconds(30) || now - frameTime > TimeSpan.FromMilliseconds(250))
            return Hold("Waiting for a fresh camera image.", "stale-frame");
        if (_lastFrame != default && frameTime <= _lastFrame)
            return Hold("Waiting for a new camera image.", "old-frame");
        _lastFrame = frameTime;
        if (detection.Reason.StartsWith("waiting-for-projected-", StringComparison.Ordinal))
            return Hold("Waiting for a fresh projected-board reference.", "waiting-reference");
        if (_measured is not null && frameTime - _lastMeasured > IdentityLifetime)
        {
            _full = _measured = null;
            _confirmed = false;
            _velocity = new(0, 0);
        }

        bool InHalf(PixelPoint point) => toField(point) is { } field &&
            double.IsFinite(field.X) && double.IsFinite(field.Y) && (field.X < .5 ? 0 : 1) == player;
        var full = detection.Candidates.Where(Valid).Where(item => InHalf(item.Center)).ToArray();
        if (full.Length > 1 || detection.AmbiguousMarkerCenters.Any(InHalf))
        {
            Reset();
            return new(FootballTipAction.Clear, null, null, "Show only one unambiguous black marker in this player's half.")
                { Source = "ambiguous" };
        }
        if (full.Length == 1)
        {
            var selected = full[0];
            bool associated = Associated(selected, frameTime);
            bool confirmed = associated && _measured is not null;
            Remember(selected, frameTime, fullPair: true, resetVelocity: !associated);
            _confirmed = confirmed;
            return confirmed
                ? new(FootballTipAction.Publish, selected, toField(selected.Center), "Black marker tracked.")
                    { Source = detection.Source == "local-contrast" ? "local-contrast" : "full-marker" }
                : Hold("Confirming the black marker.", "confirming");
        }

        if (_confirmed && profile.BarCount == 2 && _full is not null &&
            frameTime - _lastFull <= PartialLifetime)
        {
            var inferred = detection.SupportingBars.Where(Valid)
                .Where(strip => toField(strip.Center) is not null)
                .Select(strip => ContinuePair(strip, profile, frameTime))
                .Where(item => item is not null && InHalf(item.Center)).Cast<ColorTipObservation>().ToArray();
            if (inferred.Length > 1)
            {
                Reset();
                return new(FootballTipAction.Clear, null, null, "The partially visible black marker is ambiguous.")
                    { Source = "ambiguous" };
            }
            if (inferred.Length == 1)
            {
                var selected = inferred[0];
                Remember(selected, frameTime, fullPair: false, resetVelocity: false);
                return new(FootballTipAction.Publish, selected, toField(selected.Center),
                    "Tracking one visible strip briefly; show both bars.") { Source = "single-strip" };
            }
        }
        return Hold($"Looking for a black marker in the {(player == 0 ? "left" : "right")} half.", "missing");
    }

    private static FootballTipDecision Hold(string reason, string source) =>
        new(FootballTipAction.Hold, null, null, reason) { Source = source };

    private bool Associated(ColorTipObservation item, DateTimeOffset frameTime)
    {
        if (_measured is null || frameTime - _lastMeasured > IdentityLifetime) return false;
        double length = Length(_measured), age = Math.Max(0, (frameTime - _lastMeasured).TotalSeconds);
        // A fully validated, unique pair can travel several marker lengths during a
        // forward shot. The old fixed 240px/s allowance repeatedly re-confirmed it,
        // resetting velocity on every frame of a fast stroke. Scale the motion
        // allowance with the measured marker, but cap its total reach so a distant
        // replacement still needs confirmation. Center it on the last measurement
        // so a shot's sudden reversal is not penalized by forward prediction. This
        // does not relax lone-strip inference or publish an extrapolated position.
        double reach = Math.Min(Math.Max(32, length * 6),
            Math.Max(32, length * .9) + Math.Min(.25, age) * Math.Max(240, length * 40));
        return item.AreaPixels >= _measured.AreaPixels * .45 && item.AreaPixels <= _measured.AreaPixels * 2.2 &&
            ColorTipDetector.Distance(item.Center, _measured.Center) <= reach;
    }

    private ColorTipObservation? ContinuePair(ColorTipObservation strip, BlackTipProfile profile, DateTimeOffset frameTime)
    {
        if (strip.Bar is not { } bar || _full?.Bar is not { } previous) return null;
        double length = Length(strip), oldLength = Length(_full);
        double width = ColorTipDetector.Distance(bar.Side1, bar.Side2);
        double oldWidth = ColorTipDetector.Distance(previous.Side1, previous.Side2) - oldLength * profile.SpacingRatio;
        if (length < oldLength * .78 || length > oldLength * 1.25 || oldWidth < 1 ||
            width < oldWidth * .55 || width > oldWidth * 1.65) return null;
        double ux = (bar.End2.X - bar.End1.X) / length, uy = (bar.End2.Y - bar.End1.Y) / length;
        double oldUx = (previous.End2.X - previous.End1.X) / oldLength;
        double oldUy = (previous.End2.Y - previous.End1.Y) / oldLength;
        double dot = ux * oldUx + uy * oldUy;
        if (Math.Abs(dot) < .866) return null; // At most 30 degrees without a new full-pair measurement.
        if (dot < 0) { ux = -ux; uy = -uy; }
        double nx = -uy, ny = ux, spacing = profile.SpacingRatio * length;
        var predicted = PredictedCenter(frameTime);
        var minus = new PixelPoint(strip.Center.X - nx * spacing * .5, strip.Center.Y - ny * spacing * .5);
        var plus = new PixelPoint(strip.Center.X + nx * spacing * .5, strip.Center.Y + ny * spacing * .5);
        double minusDistance = ColorTipDetector.Distance(minus, predicted);
        double plusDistance = ColorTipDetector.Distance(plus, predicted);
        if (Math.Abs(minusDistance - plusDistance) < Math.Max(2.5, spacing * .35)) return null;
        var center = minusDistance < plusDistance ? minus : plus;
        double dx = center.X - predicted.X, dy = center.Y - predicted.Y;
        // The side of a lone strip becomes unknowable after a large perpendicular jump.
        if (Math.Abs(dx * nx + dy * ny) > spacing * .42 ||
            Math.Abs(dx * ux + dy * uy) > Math.Max(10, length * .55)) return null;
        double area = strip.AreaPixels * 2;
        return new(center, Math.Sqrt(area / Math.PI), area, strip.Score)
        {
            Bar = new(new(center.X - ux * length * .5, center.Y - uy * length * .5),
                new(center.X + ux * length * .5, center.Y + uy * length * .5),
                new(center.X - nx * (spacing + width) * .5, center.Y - ny * (spacing + width) * .5),
                new(center.X + nx * (spacing + width) * .5, center.Y + ny * (spacing + width) * .5))
        };
    }

    private void Remember(ColorTipObservation item, DateTimeOffset frameTime, bool fullPair, bool resetVelocity)
    {
        double seconds = (frameTime - _lastMeasured).TotalSeconds;
        if (resetVelocity || _measured is null || seconds <= 0 || seconds > .25) _velocity = new(0, 0);
        else
        {
            double vx = (item.Center.X - _measured.Center.X) / seconds;
            double vy = (item.Center.Y - _measured.Center.Y) / seconds;
            double speed = Math.Sqrt(vx * vx + vy * vy), scale = Math.Min(1, 800 / Math.Max(1, speed));
            _velocity = new(_velocity.X * .4 + vx * scale * .6, _velocity.Y * .4 + vy * scale * .6);
        }
        _measured = item;
        _lastMeasured = frameTime;
        if (fullPair) { _full = item; _lastFull = frameTime; }
    }

    private PixelPoint PredictedCenter(DateTimeOffset frameTime)
    {
        if (_measured is null) return new(0, 0);
        double seconds = Math.Clamp((frameTime - _lastMeasured).TotalSeconds, 0, .08);
        double dx = _velocity.X * seconds, dy = _velocity.Y * seconds;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        double scale = Math.Min(1, Length(_measured) * .65 / Math.Max(1, distance));
        return new(_measured.Center.X + dx * scale, _measured.Center.Y + dy * scale);
    }

    private static double Length(ColorTipObservation observation) => observation.Bar is { } bar
        ? ColorTipDetector.Distance(bar.End1, bar.End2) : observation.RadiusPixels * 2;

    private static bool Valid(ColorTipObservation? item) => item?.Bar is { } bar &&
        new[] { item.Center, bar.End1, bar.End2, bar.Side1, bar.Side2 }.All(point =>
            double.IsFinite(point.X) && double.IsFinite(point.Y) && point.X >= 0 && point.Y >= 0) &&
        double.IsFinite(item.AreaPixels) && item.AreaPixels is >= 12 and <= 1000000 &&
        double.IsFinite(item.RadiusPixels) && item.RadiusPixels is >= 1 and <= 1024 &&
        double.IsFinite(item.Score) && item.Score is >= 0 and <= 1 &&
        Length(item) is >= 2 and <= 2048;
}
