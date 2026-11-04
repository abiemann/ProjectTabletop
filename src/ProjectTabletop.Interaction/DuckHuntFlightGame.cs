namespace ProjectTabletop.Interaction;

public enum DuckHuntFlightPhase { Ready, Launching, Flying, Hit, BetweenBirds, Finished }

public readonly record struct DuckHuntFlightSnapshot(DuckHuntFlightPhase Phase, int Score,
    int BirdsLaunched, int Shots, double U, double V, int Direction, double FlapPhase,
    double Scale, bool Visible, long Revision, bool Paused = false)
{
    public BoardAim? Impact { get; init; }
    public double HitElapsed { get; init; }
    public bool PlayVisible => Phase is DuckHuntFlightPhase.Ready or DuckHuntFlightPhase.Finished;
    public bool RoundFinished => Phase == DuckHuntFlightPhase.Finished;
}

/// <summary>Ten sequential flights, started by the standard drawer PLAY control.
/// Shots use the projected pose present in the photo, not the later render tick.
/// The camera timestamp still governs fresh input and the hit animation clock.</summary>
public sealed class DuckHuntFlightGame(Random? random = null)
{
    public const int BirdsPerRound = 10;
    public static readonly TimeSpan LaunchDelay = TimeSpan.FromSeconds(.55);
    public static readonly TimeSpan FlightDuration = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan HitDuration = TimeSpan.FromSeconds(.65);
    public static readonly TimeSpan BetweenBirdsDuration = TimeSpan.FromSeconds(.70);
    private readonly Random _random = random ?? Random.Shared;
    private DateTimeOffset _observedAt, _inputNotBefore, _phaseStartedAt, _birdStartedAt;
    private DateTimeOffset _lastShotAt = DateTimeOffset.MinValue;
    private DateTimeOffset? _pausedAt = DateTimeOffset.MinValue, _hitAt, _hitPoseAt;
    private BoardAim? _impact;
    private bool _ready, _hasBird;
    private int _score, _birdsLaunched, _shots, _direction = 1;
    private double _startU = .5, _exitMargin = .115;
    private TimeSpan _flightDuration = FlightDuration;
    private DuckHuntFlightPhase _phase = DuckHuntFlightPhase.Ready;
    private DuckHuntFlightSnapshot _snapshot = new(DuckHuntFlightPhase.Ready, 0, 0, 0,
        .5, .72, 1, 0, 1, false, 0, Paused: true);

    public DuckHuntFlightSnapshot Snapshot => _snapshot;

    public void Reset(DateTimeOffset now)
    {
        _observedAt = _inputNotBefore = _phaseStartedAt = now;
        _lastShotAt = DateTimeOffset.MinValue;
        _pausedAt = now;
        _hitAt = _hitPoseAt = null;
        _impact = null;
        _ready = _hasBird = false;
        _score = _birdsLaunched = _shots = 0;
        _flightDuration = FlightDuration;
        _phase = DuckHuntFlightPhase.Ready;
        Publish(now);
    }

    public void SetReady(bool ready, DateTimeOffset now)
    {
        if (now < _observedAt || ready == _ready) return;
        Advance(now);
        if (!ready) _pausedAt = now;
        else
        {
            var pause = now - _pausedAt!.Value;
            _phaseStartedAt += pause;
            if (_hasBird) _birdStartedAt += pause;
            if (_hitAt.HasValue) _hitAt += pause;
            if (_hitPoseAt.HasValue) _hitPoseAt += pause;
            _pausedAt = null;
            _inputNotBefore = now;
        }
        _ready = ready;
        Publish(now);
    }

    public void Advance(DateTimeOffset now)
    {
        if (now < _observedAt) return;
        _observedAt = now;
        if (_ready)
        {
            // A sparse rendering tick still completes exactly ten launches.
            while (true)
            {
                TimeSpan duration = _phase switch
                {
                    DuckHuntFlightPhase.Launching => LaunchDelay,
                    DuckHuntFlightPhase.Flying => _flightDuration,
                    DuckHuntFlightPhase.Hit => HitDuration,
                    DuckHuntFlightPhase.BetweenBirds => BetweenBirdsDuration,
                    _ => TimeSpan.MaxValue
                };
                if (duration == TimeSpan.MaxValue || now - _phaseStartedAt < duration) break;
                var boundary = _phaseStartedAt + duration;
                if (_phase is DuckHuntFlightPhase.Launching or DuckHuntFlightPhase.BetweenBirds)
                {
                    if (_birdsLaunched == BirdsPerRound)
                    {
                        _phase = DuckHuntFlightPhase.Finished;
                        _phaseStartedAt = boundary;
                        _impact = null;
                    }
                    else LaunchBird(boundary);
                }
                else
                {
                    _phase = DuckHuntFlightPhase.BetweenBirds;
                    _phaseStartedAt = boundary;
                }
            }
        }
        Publish(_pausedAt ?? now);
    }

    public bool Start(DateTimeOffset now, double boardAspect = 1)
    {
        if (!_ready || now < _observedAt || !double.IsFinite(boardAspect) || boardAspect <= 0) return false;
        Advance(now);
        if (!_snapshot.PlayVisible) return false;
        _score = _birdsLaunched = _shots = 0;
        _lastShotAt = DateTimeOffset.MinValue;
        _flightDuration = FlightDuration;
        _exitMargin = DuckHuntFlightLayout.SpriteBounds(_snapshot with { Scale = 1 }, boardAspect).Width / 2 + .005;
        _hasBird = false;
        _hitAt = _hitPoseAt = null;
        _impact = null;
        _phase = DuckHuntFlightPhase.Launching;
        _phaseStartedAt = _inputNotBefore = now;
        Advance(now);
        return true;
    }

    /// <summary>The trained-audio/camera gate submits shots at flying birds.
    /// A shot never starts or restarts a round.</summary>
    public bool Shoot(BoardAim? aim, DateTimeOffset shotTime, double boardAspect = 1,
        DateTimeOffset? sceneTime = null)
    {
        var poseTime = sceneTime ?? shotTime;
        if (!_ready || aim is not { } point ||
            !double.IsFinite(point.U) || !double.IsFinite(point.V) ||
            point.U is < 0 or > 1 || point.V is < 0 or > 1 ||
            !double.IsFinite(boardAspect) || boardAspect <= 0 ||
            shotTime <= _lastShotAt || shotTime < _inputNotBefore ||
            poseTime > shotTime || poseTime < _inputNotBefore ||
            _observedAt - shotTime > DuckHuntAimGate.Freshness) return false;

        Advance(shotTime > _observedAt ? shotTime : _observedAt);

        // Retain the escaped bird through the inter-bird gap, so confirmation
        // just after escape can still hit what the camera actually photographed.
        if (_hasBird && !_hitAt.HasValue && poseTime >= _birdStartedAt &&
            poseTime < _birdStartedAt + _flightDuration)
        {
            _lastShotAt = shotTime;
            _shots++;
            bool hit = DuckHuntFlightLayout.ContainsBird(point, FlyingPoseAt(poseTime), boardAspect);
            if (hit)
            {
                _score++;
                _impact = point;
                _hitPoseAt = poseTime;
                _hitAt = _phaseStartedAt = shotTime;
                _phase = DuckHuntFlightPhase.Hit;
            }
            Advance(shotTime > _observedAt ? shotTime : _observedAt);
            return hit;
        }

        return false;
    }

    private void LaunchBird(DateTimeOffset now)
    {
        int side = _random.Next(3);
        _startU = side switch { 0 => .15, 1 => .5, _ => .85 };
        _direction = side == 0 ? 1 : side == 2 ? -1 : (_random.Next(2) == 0 ? -1 : 1);
        _birdsLaunched++;
        // Each successive bird flies the same path 10% faster. Calculate once
        // per launch, and share the duration between rendering and shot history.
        _flightDuration = TimeSpan.FromTicks((long)Math.Round(
            FlightDuration.Ticks / Math.Pow(1.1, _birdsLaunched - 1)));
        _birdStartedAt = _phaseStartedAt = now;
        _hasBird = true;
        _hitAt = _hitPoseAt = null;
        _impact = null;
        _phase = DuckHuntFlightPhase.Flying;
    }

    private DuckHuntFlightSnapshot FlyingPoseAt(DateTimeOffset now)
    {
        double elapsed = Math.Clamp((now - _birdStartedAt).TotalSeconds, 0, _flightDuration.TotalSeconds);
        double progress = elapsed / _flightDuration.TotalSeconds;
        double endU = _direction > 0 ? 1 + _exitMargin : -_exitMargin;
        return new(DuckHuntFlightPhase.Flying, _score, _birdsLaunched, _shots,
            _startU + (endU - _startU) * progress, .72 - .53 * progress,
            _direction, elapsed * 3.2 * Math.Tau, 1, true, _snapshot.Revision, !_ready);
    }

    private void Publish(DateTimeOffset now)
    {
        var pose = _hasBird ? FlyingPoseAt(_hitPoseAt ?? now) :
            new DuckHuntFlightSnapshot(_phase, _score, _birdsLaunched, _shots,
                .5, .72, 1, 0, 1, false, _snapshot.Revision);
        bool visible = _phase == DuckHuntFlightPhase.Flying ||
            (_phase == DuckHuntFlightPhase.Hit &&
             (int)(Math.Max(0, (now - _hitAt!.Value).TotalSeconds) / .09) % 2 == 0);
        var next = pose with
        {
            Phase = _phase, Score = _score, BirdsLaunched = _birdsLaunched, Shots = _shots,
            Visible = visible, Paused = !_ready, Impact = _impact,
            HitElapsed = _impact.HasValue ? Math.Max(0, (now - _hitAt!.Value).TotalSeconds) : 0
        };
        if (next != _snapshot) _snapshot = next with { Revision = _snapshot.Revision + 1 };
    }
}
