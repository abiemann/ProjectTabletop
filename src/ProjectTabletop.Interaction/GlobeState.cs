namespace ProjectTabletop.Interaction;

/// <summary>A time-derived globe presentation. Zoom already includes the entrance animation.</summary>
public sealed record GlobeSnapshot(double TargetZoom, double Zoom, double RotationDegrees,
    double IntroProgress, double ElapsedSeconds, long Revision);

/// <summary>
/// Keeps globe controls separate from its continuously animated presentation. Sampling a frame
/// never advances an interaction revision or invalidates a hand's selection evidence.
/// </summary>
public sealed class GlobeState
{
    public const double DefaultZoom = 1.5;
    public const double MinimumZoom = .6;
    public const double MaximumZoom = 3;
    public const double ZoomStep = 1.25;
    public const double RotationStepDegrees = 20;
    public const double RotationDegreesPerSecond = 1;
    public static readonly TimeSpan EntranceDuration = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ControlTransitionDuration = TimeSpan.FromMilliseconds(350);

    private readonly double _homeRotation;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset _transitionAt;
    private DateTimeOffset _lastActionAt;
    private double _zoomFrom = DefaultZoom;
    private double _targetZoom = DefaultZoom;
    private double _rotationFrom;
    private double _targetRotation;
    public long Revision { get; private set; }
    public double HomeRotationDegrees => _homeRotation;

    /// <param name="homeLongitudeDegrees">Longitude facing the viewer at each launch, east positive.
    /// The surface shader centres longitude -rotation, so it starts at the opposite rotation.</param>
    public GlobeState(double homeLongitudeDegrees = 0)
    {
        if (!double.IsFinite(homeLongitudeDegrees)) throw new ArgumentOutOfRangeException(nameof(homeLongitudeDegrees));
        _homeRotation = ((-homeLongitudeDegrees) % 360 + 360) % 360;
    }

    public void Start(DateTimeOffset now)
    {
        _startedAt = now;
        _transitionAt = now;
        _lastActionAt = now;
        _zoomFrom = _targetZoom = DefaultZoom;
        _rotationFrom = _targetRotation = _homeRotation;
        Revision++;
    }

    public GlobeSnapshot GetSnapshot(DateTimeOffset now)
    {
        double elapsed = _startedAt is { } start ? Math.Max(0, (now - start).TotalSeconds) : 0;
        double intro = Math.Clamp(elapsed / EntranceDuration.TotalSeconds, 0, 1);
        double entranceScale = .04 + .96 * EaseOut(intro);
        double progress = TransitionProgress(now);
        double zoom = Lerp(_zoomFrom, _targetZoom, progress) * entranceScale;
        double rotation = Lerp(_rotationFrom, _targetRotation, progress) + elapsed * RotationDegreesPerSecond;
        rotation = (rotation % 360 + 360) % 360;
        return new(_targetZoom, zoom, rotation, intro, elapsed, Revision);
    }

    public bool CanHandleAction(string id) => _startedAt is not null && (id switch
    {
        "globe-zoom-in" => _targetZoom < MaximumZoom,
        "globe-zoom-out" => _targetZoom > MinimumZoom,
        "globe-rotate-left" or "globe-rotate-right" => true,
        _ => false
    });

    public bool HandleAction(string id, DateTimeOffset now)
    {
        if (now < _lastActionAt || !CanHandleAction(id)) return false;
        double progress = TransitionProgress(now);
        _zoomFrom = Lerp(_zoomFrom, _targetZoom, progress);
        _rotationFrom = Lerp(_rotationFrom, _targetRotation, progress);
        switch (id)
        {
            case "globe-zoom-in": _targetZoom = Math.Min(MaximumZoom, _targetZoom * ZoomStep); break;
            case "globe-zoom-out": _targetZoom = Math.Max(MinimumZoom, _targetZoom / ZoomStep); break;
            case "globe-rotate-left": _targetRotation -= RotationStepDegrees; break;
            case "globe-rotate-right": _targetRotation += RotationStepDegrees; break;
        }
        _transitionAt = now;
        _lastActionAt = now;
        Revision++;
        return true;
    }

    private double TransitionProgress(DateTimeOffset now) =>
        EaseOut(Math.Clamp((now - _transitionAt).TotalSeconds / ControlTransitionDuration.TotalSeconds, 0, 1));
    private static double EaseOut(double progress) => 1 - Math.Pow(1 - progress, 3);
    private static double Lerp(double first, double last, double amount) => first + (last - first) * amount;
}
