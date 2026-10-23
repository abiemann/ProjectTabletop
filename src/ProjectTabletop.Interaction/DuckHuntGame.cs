namespace ProjectTabletop.Interaction;

public enum DuckHuntPhase { Ready, Walking, Turning, DuckWon, PlayerWon }

public readonly record struct DuckHuntSnapshot(DuckHuntPhase Phase, int Hits, int Shots,
    double U, double V, double Rotation, long Revision, int Direction = -1,
    double Facing = -1, double WalkPhase = 0, bool Paused = false)
{
    public bool DuckVisible => Phase is DuckHuntPhase.Walking or DuckHuntPhase.Turning or DuckHuntPhase.PlayerWon;
    public bool RoundFinished => Phase is DuckHuntPhase.DuckWon or DuckHuntPhase.PlayerWon;
}

/// <summary>A horizontal crossing with a short turn and 10% speed increase after each hit.
/// Motion is evaluated at the camera photo timestamp, independently of render ticks.</summary>
public sealed class DuckHuntGame
{
    public const int HitsToWin = 10;
    public static readonly TimeSpan CrossingDuration = TimeSpan.FromSeconds(22);
    public static readonly TimeSpan TurnDuration = TimeSpan.FromSeconds(.28);
    private const double CentreV = .48;
    private const double EntryMargin = .004;
    private const double StepsPerSecond = 1.7;
    private DateTimeOffset _observedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _segmentStartedAt, _inputNotBefore;
    private DateTimeOffset _lastShotAt = DateTimeOffset.MinValue;
    private DateTimeOffset? _pausedAt;
    private bool _started, _turningSegment, _playerWon;
    private int _direction = -1, _previousDirection = -1, _hits, _shots;
    private double _segmentU, _walkDistanceBeforeSegment, _halfWidth, _baseSpeed, _speed;
    private DuckHuntSnapshot _snapshot = new(DuckHuntPhase.Ready, 0, 0, .5, CentreV, 0, 0);

    public DuckHuntSnapshot Snapshot => _snapshot;
    public DuckHuntFeathers Feathers { get; } = new();

    public void Reset(DateTimeOffset now)
    {
        _observedAt = _inputNotBefore = now;
        _lastShotAt = DateTimeOffset.MinValue;
        _pausedAt = null;
        _started = _turningSegment = _playerWon = false;
        _hits = _shots = 0;
        _walkDistanceBeforeSegment = 0;
        Feathers.Clear();
        _snapshot = new(DuckHuntPhase.Ready, 0, 0, .5, CentreV, 0, _snapshot.Revision + 1);
    }

    /// <summary>Start only after camera, microphone and artwork are ready. The optional
    /// side makes verification deterministic; normal rounds choose either edge randomly.</summary>
    public bool Start(DateTimeOffset now, double boardAspect = 1, bool? enterFromLeft = null)
    {
        if (_started || now < _observedAt || !double.IsFinite(boardAspect) || boardAspect <= 0) return false;
        _direction = _previousDirection = (enterFromLeft ?? Random.Shared.Next(2) == 0) ? 1 : -1;
        _halfWidth = DuckHuntLayout.PhotoWidth(boardAspect) / 2;
        _speed = _baseSpeed = (1 + 2 * _halfWidth + 2 * EntryMargin) / CrossingDuration.TotalSeconds;
        _segmentU = _direction > 0 ? -_halfWidth - EntryMargin : 1 + _halfWidth + EntryMargin;
        _segmentStartedAt = _inputNotBefore = now;
        _started = true;
        Advance(now);
        return true;
    }

    public void Advance(DateTimeOffset now)
    {
        if (now < _observedAt) return;
        _observedAt = now;
        if (!_started) return;
        var animationTime = _pausedAt ?? now;
        bool feathersChanged = Feathers.Advance(animationTime);
        Publish(PoseAt(animationTime) with { Paused = _pausedAt.HasValue }, feathersChanged);
    }

    /// <summary>Freeze the crossing while input is unavailable. On resume, invalidate
    /// earlier photos so a delayed click cannot cross the pause boundary.</summary>
    public void SetPaused(bool paused, DateTimeOffset now)
    {
        if (!_started || now < _observedAt || paused == _pausedAt.HasValue) return;
        Advance(now);
        if (paused) _pausedAt = now;
        else
        {
            var pause = now - _pausedAt!.Value;
            _segmentStartedAt += pause;
            Feathers.Delay(pause);
            _pausedAt = null;
            _inputNotBefore = now;
        }
        Advance(now);
    }

    /// <summary>Missing/invalid aim, duplicate timestamps and photos during a turn
    /// or pause do not count as shots. The audio/camera gate owns freshness.</summary>
    public bool Shoot(BoardAim? aim, DateTimeOffset shotTime, double boardAspect = 1)
    {
        if (!_started || _pausedAt.HasValue || _playerWon || aim is not { } point ||
            !double.IsFinite(point.U) || !double.IsFinite(point.V) ||
            point.U is < 0 or > 1 || point.V is < 0 or > 1 ||
            shotTime <= _lastShotAt || shotTime < _inputNotBefore ||
            !double.IsFinite(boardAspect) || boardAspect <= 0) return false;
        var photographed = PoseAt(shotTime);
        if (photographed.Phase != DuckHuntPhase.Walking) return false;
        _lastShotAt = shotTime;
        _shots++;
        bool hit = DuckHuntLayout.ContainsDuck(point, photographed, boardAspect);
        if (hit)
        {
            Feathers.Spawn(photographed, boardAspect, shotTime);
            _hits++;
            _walkDistanceBeforeSegment = WalkingDistanceAt(shotTime);
            _segmentU = photographed.U;
            _segmentStartedAt = shotTime;
            _speed *= 1.1;
            _playerWon = _hits == HitsToWin;
            _turningSegment = !_playerWon;
            if (!_playerWon)
            {
                _previousDirection = _direction;
                _direction = -_direction;
            }
        }
        // A completed worker may arrive after a render tick, even just after the
        // original escape time. Re-evaluate that tick with the new historical turn.
        Advance(shotTime > _observedAt ? shotTime : _observedAt);
        return hit;
    }

    private double WalkingDistanceAt(DateTimeOffset now)
    {
        double elapsed = Math.Max(0, (now - _segmentStartedAt).TotalSeconds);
        double walking = Math.Max(0, elapsed - (_turningSegment ? TurnDuration.TotalSeconds : 0));
        double exitU = _direction > 0 ? 1 + _halfWidth + EntryMargin : -_halfWidth - EntryMargin;
        return _walkDistanceBeforeSegment + Math.Min(walking * _speed, Math.Abs(exitU - _segmentU));
    }

    private DuckHuntSnapshot PoseAt(DateTimeOffset now)
    {
        double elapsed = Math.Max(0, (now - _segmentStartedAt).TotalSeconds);
        double walkingDistance = _playerWon ? _walkDistanceBeforeSegment : WalkingDistanceAt(now);
        double u = _segmentU + _direction * (walkingDistance - _walkDistanceBeforeSegment);
        bool turning = _turningSegment && elapsed < TurnDuration.TotalSeconds;
        double facing = turning ? _previousDirection * Math.Cos(Math.PI * elapsed / TurnDuration.TotalSeconds) : _direction;
        double exitU = _direction > 0 ? 1 + _halfWidth + EntryMargin : -_halfWidth - EntryMargin;
        bool escaped = !turning && Math.Abs(u - exitU) < 1e-10;
        var phase = _playerWon ? DuckHuntPhase.PlayerWon : escaped ? DuckHuntPhase.DuckWon :
            turning ? DuckHuntPhase.Turning : DuckHuntPhase.Walking;
        return new(phase, _hits, _shots, u, CentreV, 0, _snapshot.Revision,
            // Distance keeps the feet in step with faster travel without a phase jump at a hit.
            _direction, facing, walkingDistance / _baseSpeed * StepsPerSecond * Math.Tau);
    }

    private void Publish(DuckHuntSnapshot next, bool feathersChanged)
    {
        if (feathersChanged || next != _snapshot) _snapshot = next with { Revision = _snapshot.Revision + 1 };
    }
}
