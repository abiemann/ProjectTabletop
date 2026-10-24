namespace ProjectTabletop.Interaction;

public enum DuckHuntFlightPhase { Ready, Launching, Flying, Hit, BetweenBirds, Finished }

public readonly record struct DuckHuntFlightSnapshot(DuckHuntFlightPhase Phase, int Score,
    int BirdsLaunched, int Shots, double U, double V, int Direction, double FlapPhase,
    double Scale, bool Visible, long Revision, bool Paused = false)
{
    public bool StartVisible => Phase is DuckHuntFlightPhase.Ready or DuckHuntFlightPhase.Finished;
    public bool RoundFinished => Phase == DuckHuntFlightPhase.Finished;
}

/// <summary>Ten sequential flights, started only by a valid click photo on START.
/// Shots use the pose present at the photo timestamp, not the later render tick.</summary>
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
    private DateTimeOffset? _pausedAt = DateTimeOffset.MinValue, _hitAt;
    private bool _ready, _hasBird;
    private int _score, _birdsLaunched, _shots, _direction = 1;
    private double _startU = .5, _exitMargin = .115;
    private DuckHuntFlightPhase _phase = DuckHuntFlightPhase.Ready;
    private DuckHuntFlightSnapshot _snapshot = new(DuckHuntFlightPhase.Ready, 0, 0, 0,
        .5, .72, 1, 0, 1, false, 0, Paused: true);

    public DuckHuntFlightSnapshot Snapshot => _snapshot;

    public void Reset(DateTimeOffset now)
    {
        _observedAt = _inputNotBefore = _phaseStartedAt = now;
        _lastShotAt = DateTimeOffset.MinValue;
        _pausedAt = now;
        _hitAt = null;
        _ready = _hasBird = false;
        _score = _birdsLaunched = _shots = 0;
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
                    DuckHuntFlightPhase.Flying => FlightDuration,
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

    /// <summary>The existing trained-audio/camera gate is the caller. START is
    /// deliberately absent from BoardButton, hand holds and ordinary tap routes.</summary>
    public bool Shoot(BoardAim? aim, DateTimeOffset shotTime, double boardAspect = 1)
    {
        if (!_ready || aim is not { } point ||
            !double.IsFinite(point.U) || !double.IsFinite(point.V) ||
            point.U is < 0 or > 1 || point.V is < 0 or > 1 ||
            !double.IsFinite(boardAspect) || boardAspect <= 0 ||
            shotTime <= _lastShotAt || shotTime < _inputNotBefore ||
            _observedAt - shotTime > DuckHuntAimGate.Freshness) return false;

        Advance(shotTime > _observedAt ? shotTime : _observedAt);

        // Retain the escaped bird through the inter-bird gap, so confirmation
        // just after escape can still hit what the camera actually photographed.
        if (_hasBird && !_hitAt.HasValue && shotTime >= _birdStartedAt &&
            shotTime < _birdStartedAt + FlightDuration)
        {
            _lastShotAt = shotTime;
            _shots++;
            bool hit = DuckHuntFlightLayout.ContainsBird(point, FlyingPoseAt(shotTime), boardAspect);
            if (hit)
            {
                _score++;
                _hitAt = _phaseStartedAt = shotTime;
                _phase = DuckHuntFlightPhase.Hit;
            }
            Advance(shotTime > _observedAt ? shotTime : _observedAt);
            return hit;
        }

        if (_phase is not (DuckHuntFlightPhase.Ready or DuckHuntFlightPhase.Finished) ||
            shotTime < _phaseStartedAt) return false;
        _lastShotAt = shotTime;
        if (!DuckHuntFlightLayout.StartBounds.Contains(point.U, point.V)) return false;
        _score = _birdsLaunched = _shots = 0;
        _exitMargin = DuckHuntFlightLayout.SpriteBounds(_snapshot with { Scale = 1 }, boardAspect).Width / 2 + .005;
        _hasBird = false;
        _hitAt = null;
        _phase = DuckHuntFlightPhase.Launching;
        _phaseStartedAt = _inputNotBefore = shotTime;
        Advance(shotTime > _observedAt ? shotTime : _observedAt);
        return true;
    }

    private void LaunchBird(DateTimeOffset now)
    {
        int side = _random.Next(3);
        _startU = side switch { 0 => .15, 1 => .5, _ => .85 };
        _direction = side == 0 ? 1 : side == 2 ? -1 : (_random.Next(2) == 0 ? -1 : 1);
        _birdsLaunched++;
        _birdStartedAt = _phaseStartedAt = now;
        _hasBird = true;
        _hitAt = null;
        _phase = DuckHuntFlightPhase.Flying;
    }

    private DuckHuntFlightSnapshot FlyingPoseAt(DateTimeOffset now)
    {
        double elapsed = Math.Clamp((now - _birdStartedAt).TotalSeconds, 0, FlightDuration.TotalSeconds);
        double progress = elapsed / FlightDuration.TotalSeconds;
        double endU = _direction > 0 ? 1 + _exitMargin : -_exitMargin;
        return new(DuckHuntFlightPhase.Flying, _score, _birdsLaunched, _shots,
            _startU + (endU - _startU) * progress, .72 - .53 * progress,
            _direction, elapsed * 3.2 * Math.Tau, 1, true, _snapshot.Revision, !_ready);
    }

    private void Publish(DateTimeOffset now)
    {
        var pose = _hasBird ? FlyingPoseAt(_hitAt ?? now) :
            new DuckHuntFlightSnapshot(_phase, _score, _birdsLaunched, _shots,
                .5, .72, 1, 0, 1, false, _snapshot.Revision);
        bool visible = _phase == DuckHuntFlightPhase.Flying ||
            (_phase == DuckHuntFlightPhase.Hit &&
             (int)(Math.Max(0, (now - _hitAt!.Value).TotalSeconds) / .09) % 2 == 0);
        var next = pose with
        {
            Phase = _phase, Score = _score, BirdsLaunched = _birdsLaunched, Shots = _shots,
            Visible = visible, Paused = !_ready
        };
        if (next != _snapshot) _snapshot = next with { Revision = _snapshot.Revision + 1 };
    }
}
