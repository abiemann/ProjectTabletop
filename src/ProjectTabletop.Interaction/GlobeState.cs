namespace ProjectTabletop.Interaction;

public enum GlobeModel { Clouds, NoClouds, Night }

/// <summary>A time-derived globe presentation. Zoom already includes the entrance animation.</summary>
public sealed record GlobeSnapshot(double TargetZoom, double Zoom, double RotationDegrees,
    double IntroProgress, double ElapsedSeconds, long Revision, double ViewLatitudeDegrees = 0,
    GlobeModel Model = GlobeModel.Clouds);

/// <summary>
/// Keeps globe controls separate from its continuously animated presentation. Sampling a frame
/// never advances an interaction revision or invalidates a hand's selection evidence.
/// </summary>
public sealed class GlobeState
{
    public const double DefaultZoom = .6;
    public const double MinimumZoom = .6;
    public const double MaximumZoom = 3;
    public const double ZoomStep = 1.25;
    public const double RotationStepDegrees = 20;
    public const double RotationDegreesPerSecond = 1;
    // Keep a polar home from turning Earth into a view of the ice cap.
    public const double MaximumViewLatitude = 60;
    public static readonly TimeSpan EntranceDuration = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ControlTransitionDuration = TimeSpan.FromMilliseconds(350);
    public static readonly TimeSpan PitchTransitionDuration = TimeSpan.FromMilliseconds(120);
    public static readonly TimeSpan ZoomRailTransitionDuration = TimeSpan.FromMilliseconds(120);

    private readonly double _homeRotation;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset _transitionAt;
    private DateTimeOffset _lastActionAt;
    private double _zoomFrom = DefaultZoom;
    private double _targetZoom = DefaultZoom;
    private DateTimeOffset _zoomTransitionAt;
    private DateTimeOffset _zoomObservedAt;
    private TimeSpan _zoomTransitionDuration = ControlTransitionDuration;
    private double _rotationFrom;
    private double _targetRotation;
    private DateTimeOffset _pitchTransitionAt;
    private DateTimeOffset _pitchObservedAt;
    private double _pitchFrom;
    private double _targetPitch;
    private DateTimeOffset _modelObservedAt;
    public long Revision { get; private set; }
    public GlobeModel Model { get; private set; } = GlobeModel.Clouds;
    public double HomeRotationDegrees => _homeRotation;
    /// <summary>Latitude held at the disk's centre; north positive.</summary>
    public double HomeLatitudeDegrees { get; }

    /// <param name="homeLongitudeDegrees">Longitude facing the viewer at each launch, east positive.
    /// The surface shader centres longitude -rotation, so it starts at the opposite rotation.</param>
    /// <param name="homeLatitudeDegrees">Latitude tilted to the disk's centre, limited to
    /// ±<see cref="MaximumViewLatitude"/>; spin continues about Earth's axis.</param>
    public GlobeState(double homeLongitudeDegrees = 0, double homeLatitudeDegrees = 0)
    {
        if (!double.IsFinite(homeLongitudeDegrees)) throw new ArgumentOutOfRangeException(nameof(homeLongitudeDegrees));
        if (!double.IsFinite(homeLatitudeDegrees)) throw new ArgumentOutOfRangeException(nameof(homeLatitudeDegrees));
        _homeRotation = ((-homeLongitudeDegrees) % 360 + 360) % 360;
        HomeLatitudeDegrees = Math.Clamp(homeLatitudeDegrees, -MaximumViewLatitude, MaximumViewLatitude);
        _pitchFrom = _targetPitch = HomeLatitudeDegrees;
    }

    public void Start(DateTimeOffset now)
    {
        _startedAt = now;
        _transitionAt = now;
        _lastActionAt = now;
        _zoomFrom = _targetZoom = DefaultZoom;
        _zoomTransitionAt = _zoomObservedAt = now;
        _zoomTransitionDuration = ControlTransitionDuration;
        _rotationFrom = _targetRotation = _homeRotation;
        _pitchTransitionAt = _pitchObservedAt = now;
        _pitchFrom = _targetPitch = HomeLatitudeDegrees;
        _modelObservedAt = now;
        Model = GlobeModel.Clouds;
        Revision++;
    }

    public GlobeSnapshot GetSnapshot(DateTimeOffset now)
    {
        double elapsed = _startedAt is { } start ? Math.Max(0, (now - start).TotalSeconds) : 0;
        double intro = Math.Clamp(elapsed / EntranceDuration.TotalSeconds, 0, 1);
        double entranceScale = .04 + .96 * EaseOut(intro);
        double progress = TransitionProgress(now);
        double zoom = ZoomAt(now) * entranceScale;
        double rotation = Lerp(_rotationFrom, _targetRotation, progress) + elapsed * RotationDegreesPerSecond;
        rotation = (rotation % 360 + 360) % 360;
        return new(_targetZoom, zoom, rotation, intro, elapsed, Revision, PitchAt(now), Model);
    }

    public bool HasFinishedEntrance(DateTimeOffset now) =>
        _startedAt is { } started && now >= started + EntranceDuration;

    /// <summary>Sets a bottom-to-top rail position without disturbing zoom, spin or caption evidence.</summary>
    public bool SetPitchPosition(double position, DateTimeOffset now)
    {
        if (_startedAt is null || now < _pitchObservedAt || !double.IsFinite(position) || position is < 0 or > 1)
            return false;
        _pitchObservedAt = now;
        double target = (position * 2 - 1) * MaximumViewLatitude;
        // Repeated stationary observations must not keep restarting the easing.
        if (target == _targetPitch) return true;
        _pitchFrom = PitchAt(now);
        _targetPitch = target;
        _pitchTransitionAt = now;
        return true;
    }

    /// <summary>Sets the rail's absolute zoom, easing independently of pitch and spin.</summary>
    public bool SetZoomPosition(double position, DateTimeOffset now)
    {
        if (_startedAt is null || now < _zoomObservedAt || !double.IsFinite(position) || position is < 0 or > 1)
            return false;
        _zoomObservedAt = now;
        double target = Lerp(MinimumZoom, MaximumZoom, position);
        if (target == _targetZoom) return true;
        _zoomFrom = ZoomAt(now);
        _targetZoom = target;
        _zoomTransitionAt = now;
        _zoomTransitionDuration = ZoomRailTransitionDuration;
        return true;
    }

    /// <summary>Records a requested surface; the application loads only the selected model's textures.</summary>
    public bool SelectModel(GlobeModel model, DateTimeOffset now)
    {
        if (_startedAt is null || now < _modelObservedAt || !Enum.IsDefined(model)) return false;
        _modelObservedAt = now;
        if (Model == model) return true;
        Model = model;
        Revision++;
        return true;
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
        bool changesZoom = id is "globe-zoom-in" or "globe-zoom-out";
        if (changesZoom && now < _zoomObservedAt) return false;
        if (changesZoom) _zoomFrom = ZoomAt(now);
        _rotationFrom = Lerp(_rotationFrom, _targetRotation, progress);
        switch (id)
        {
            case "globe-zoom-in": _targetZoom = Math.Min(MaximumZoom, _targetZoom * ZoomStep); break;
            case "globe-zoom-out": _targetZoom = Math.Max(MinimumZoom, _targetZoom / ZoomStep); break;
            case "globe-rotate-left": _targetRotation -= RotationStepDegrees; break;
            case "globe-rotate-right": _targetRotation += RotationStepDegrees; break;
        }
        _transitionAt = now;
        if (changesZoom)
        {
            _zoomTransitionAt = _zoomObservedAt = now;
            _zoomTransitionDuration = ControlTransitionDuration;
        }
        _lastActionAt = now;
        Revision++;
        return true;
    }

    private double TransitionProgress(DateTimeOffset now) =>
        EaseOut(Math.Clamp((now - _transitionAt).TotalSeconds / ControlTransitionDuration.TotalSeconds, 0, 1));
    private double ZoomAt(DateTimeOffset now)
    {
        if (now >= _zoomTransitionAt + _zoomTransitionDuration) return _targetZoom;
        if (now <= _zoomTransitionAt) return _zoomFrom;
        return Lerp(_zoomFrom, _targetZoom, EaseOut((now - _zoomTransitionAt) / _zoomTransitionDuration));
    }
    private double PitchAt(DateTimeOffset now)
    {
        if (now >= _pitchTransitionAt + PitchTransitionDuration) return _targetPitch;
        if (now <= _pitchTransitionAt) return _pitchFrom;
        return Lerp(_pitchFrom, _targetPitch, EaseOut((now - _pitchTransitionAt) / PitchTransitionDuration));
    }
    private static double EaseOut(double progress) => 1 - Math.Pow(1 - progress, 3);
    private static double Lerp(double first, double last, double amount) => first + (last - first) * amount;
}
