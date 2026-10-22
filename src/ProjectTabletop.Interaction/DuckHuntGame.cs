namespace ProjectTabletop.Interaction;

public enum DuckHuntPhase { Aiming, Falling, Waiting }

public readonly record struct DuckHuntSnapshot(DuckHuntPhase Phase, int Hits, int Shots,
    double U, double V, double Rotation, long Revision)
{
    public bool DuckVisible => Phase != DuckHuntPhase.Waiting;
}

/// <summary>A stationary target, a short gravity fall, then a fresh target. Input timestamps
/// are supplied by the caller; the camera/audio layer owns observation freshness.</summary>
public sealed class DuckHuntGame
{
    public static readonly TimeSpan FallDuration = TimeSpan.FromSeconds(1.15);
    public static readonly TimeSpan RespawnDelay = TimeSpan.FromSeconds(.45);
    private DateTimeOffset _observedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _targetAppearedAt = DateTimeOffset.MinValue;
    private DateTimeOffset? _hitAt;
    private DateTimeOffset _lastShotAt = DateTimeOffset.MinValue;
    private DuckHuntSnapshot _snapshot = new(DuckHuntPhase.Aiming, 0, 0, .5, .48, 0, 0);

    public DuckHuntSnapshot Snapshot => _snapshot;

    public void Reset(DateTimeOffset now)
    {
        _observedAt = now;
        _targetAppearedAt = now;
        _lastShotAt = DateTimeOffset.MinValue;
        _hitAt = null;
        _snapshot = new(DuckHuntPhase.Aiming, 0, 0, .5, .48, 0, _snapshot.Revision + 1);
    }

    public void Advance(DateTimeOffset now)
    {
        if (now < _observedAt) return;
        _observedAt = now;
        if (_hitAt is not { } hitAt) return;
        double seconds = (now - hitAt).TotalSeconds;
        var phase = seconds < FallDuration.TotalSeconds ? DuckHuntPhase.Falling : DuckHuntPhase.Waiting;
        if (seconds >= (FallDuration + RespawnDelay).TotalSeconds)
        {
            _hitAt = null;
            _targetAppearedAt = hitAt + FallDuration + RespawnDelay;
            SetPose(DuckHuntPhase.Aiming, .5, .48, 0);
        }
        else
            SetPose(phase, .5 + .055 * seconds, .48 - .08 * seconds + .70 * seconds * seconds,
                seconds * 2.1);
    }

    /// <summary>Returns true only for a hit on the visible silhouette. Missing/invalid aim
    /// and repeated audio timestamps are ignored rather than counted as shots.</summary>
    public bool Shoot(BoardAim? aim, DateTimeOffset shotTime, double boardAspect = 1)
    {
        if (aim is not { } point || !double.IsFinite(point.U) || !double.IsFinite(point.V) ||
            point.U is < 0 or > 1 || point.V is < 0 or > 1 ||
            shotTime <= _lastShotAt || shotTime < _targetAppearedAt ||
            !double.IsFinite(boardAspect) || boardAspect <= 0) return false;
        Advance(shotTime);
        if (_snapshot.Phase != DuckHuntPhase.Aiming) return false;
        _lastShotAt = shotTime;
        bool hit = DuckHuntLayout.ContainsDuck(point, _snapshot, boardAspect);
        _snapshot = _snapshot with
        {
            Shots = _snapshot.Shots + 1,
            Hits = _snapshot.Hits + (hit ? 1 : 0),
            Phase = hit ? DuckHuntPhase.Falling : DuckHuntPhase.Aiming,
            Revision = _snapshot.Revision + 1
        };
        if (hit) _hitAt = shotTime;
        return hit;
    }

    private void SetPose(DuckHuntPhase phase, double u, double v, double rotation)
    {
        if (_snapshot.Phase == phase && _snapshot.U == u && _snapshot.V == v && _snapshot.Rotation == rotation) return;
        _snapshot = _snapshot with { Phase = phase, U = u, V = v, Rotation = rotation,
            Revision = _snapshot.Revision + 1 };
    }
}
