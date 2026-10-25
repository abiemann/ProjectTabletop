using System.Numerics;

namespace ProjectTabletop.Interaction;

public enum DuckHuntGalleryPhase
{
    Ready, Opening, RevealWait, Advancing, WaitingForHits, SuccessWait, Departing, Falling, Closing
}

public readonly record struct DuckHuntGalleryPuncture(
    int TargetIndex, double U, double V, DateTimeOffset ShotTime);

public readonly record struct DuckHuntGallerySnapshot(DuckHuntGalleryPhase Phase,
    double FlapAngleDegrees, double ConveyorPosition, double FallProgress,
    int HitMask, int Shots, int Cycle, long Revision, bool Paused = false)
{
    public bool PlayVisible => Phase == DuckHuntGalleryPhase.Ready;
    public bool TargetsVisible => Phase is not (DuckHuntGalleryPhase.Ready or DuckHuntGalleryPhase.Closing);
    public int HitTargets => BitOperations.PopCount((uint)HitMask);
}

/// <summary>A five-paper conveyor gallery. PLAY and papers accept only validated
/// click photos; the stationary paper surface owns each permanent puncture.</summary>
public sealed class DuckHuntGalleryGame
{
    public const int TargetCount = 5;
    public static readonly TimeSpan OpeningDuration = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan RevealWaitDuration = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan AdvanceDuration = TimeSpan.FromSeconds(1.8);
    public static readonly TimeSpan SuccessWaitDuration = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DepartDuration = TimeSpan.FromSeconds(1.2);
    public static readonly TimeSpan FallDuration = TimeSpan.FromSeconds(.7);
    public static readonly TimeSpan ClosingDuration = TimeSpan.FromSeconds(1);

    private readonly List<DuckHuntGalleryPuncture> _punctures = new(32);
    private readonly IReadOnlyList<DuckHuntGalleryPuncture> _readOnlyPunctures;
    private DateTimeOffset _observedAt, _phaseStartedAt, _inputNotBefore;
    private DateTimeOffset _lastShotAt = DateTimeOffset.MinValue;
    private DateTimeOffset? _pausedAt = DateTimeOffset.MinValue;
    private bool _ready;
    private int _hitMask, _shots, _cycle;
    private DuckHuntGalleryPhase _phase = DuckHuntGalleryPhase.Ready;
    private DuckHuntGallerySnapshot _snapshot = new(DuckHuntGalleryPhase.Ready,
        0, 0, 0, 0, 0, 0, 0, Paused: true);

    public DuckHuntGalleryGame() => _readOnlyPunctures = _punctures.AsReadOnly();
    public DuckHuntGallerySnapshot Snapshot => _snapshot;
    public IReadOnlyList<DuckHuntGalleryPuncture> Punctures => _readOnlyPunctures;

    public void Reset(DateTimeOffset now)
    {
        _observedAt = _phaseStartedAt = _inputNotBefore = now;
        _lastShotAt = DateTimeOffset.MinValue;
        _pausedAt = now;
        _ready = false;
        _hitMask = _shots = _cycle = 0;
        _phase = DuckHuntGalleryPhase.Ready;
        _punctures.Clear();
        Publish(now);
    }

    public void SetReady(bool ready, DateTimeOffset now)
    {
        if (now < _observedAt || ready == _ready) return;
        Advance(now);
        if (!ready) _pausedAt = now;
        else
        {
            _phaseStartedAt += now - _pausedAt!.Value;
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
            while (_phase is not (DuckHuntGalleryPhase.Ready or DuckHuntGalleryPhase.WaitingForHits))
            {
                var duration = Duration(_phase);
                if (now - _phaseStartedAt < duration) break;
                _phaseStartedAt += duration;
                _phase = _phase switch
                {
                    DuckHuntGalleryPhase.Opening => DuckHuntGalleryPhase.RevealWait,
                    DuckHuntGalleryPhase.RevealWait => DuckHuntGalleryPhase.Advancing,
                    DuckHuntGalleryPhase.Advancing => DuckHuntGalleryPhase.WaitingForHits,
                    DuckHuntGalleryPhase.SuccessWait => DuckHuntGalleryPhase.Departing,
                    DuckHuntGalleryPhase.Departing => DuckHuntGalleryPhase.Falling,
                    DuckHuntGalleryPhase.Falling => DuckHuntGalleryPhase.Closing,
                    _ => DuckHuntGalleryPhase.Ready
                };
            }
        }
        Publish(_pausedAt ?? now);
    }

    public bool Shoot(BoardAim? aim, DateTimeOffset shotTime, double boardAspect = 1)
    {
        if (!_ready || aim is not { } point || !double.IsFinite(point.U) || !double.IsFinite(point.V) ||
            point.U is < 0 or > 1 || point.V is < 0 or > 1 ||
            !double.IsFinite(boardAspect) || boardAspect <= 0 || shotTime <= _lastShotAt ||
            shotTime < _inputNotBefore || _observedAt - shotTime > DuckHuntAimGate.Freshness) return false;

        Advance(shotTime > _observedAt ? shotTime : _observedAt);
        if (shotTime < _phaseStartedAt) return false;
        if (_phase == DuckHuntGalleryPhase.Ready)
        {
            _lastShotAt = shotTime;
            if (!DuckHuntGalleryLayout.PlayBounds.Contains(point.U, point.V)) return false;
            _punctures.Clear();
            _hitMask = _shots = 0;
            _cycle++;
            _phase = DuckHuntGalleryPhase.Opening;
            _phaseStartedAt = _inputNotBefore = shotTime;
            Advance(shotTime > _observedAt ? shotTime : _observedAt);
            return true;
        }
        if (_phase != DuckHuntGalleryPhase.WaitingForHits) return false;
        _lastShotAt = shotTime;
        _shots++;
        bool hit = false;
        for (int index = 0; index < TargetCount; index++)
        {
            var paper = DuckHuntGalleryLayout.TargetQuad(index, _snapshot, boardAspect);
            if (!paper.TryMapToLocal(point, out var local) || !DuckHuntGalleryLayout.ContainsPaper(local)) continue;
            _punctures.Add(new(index, local.U, local.V, shotTime));
            _hitMask |= 1 << index;
            hit = true;
            if (_hitMask == (1 << TargetCount) - 1)
            {
                _phase = DuckHuntGalleryPhase.SuccessWait;
                _phaseStartedAt = shotTime;
            }
            break;
        }
        Advance(shotTime > _observedAt ? shotTime : _observedAt);
        return hit;
    }

    private static TimeSpan Duration(DuckHuntGalleryPhase phase) => phase switch
    {
        DuckHuntGalleryPhase.Opening => OpeningDuration,
        DuckHuntGalleryPhase.RevealWait => RevealWaitDuration,
        DuckHuntGalleryPhase.Advancing => AdvanceDuration,
        DuckHuntGalleryPhase.SuccessWait => SuccessWaitDuration,
        DuckHuntGalleryPhase.Departing => DepartDuration,
        DuckHuntGalleryPhase.Falling => FallDuration,
        DuckHuntGalleryPhase.Closing => ClosingDuration,
        _ => TimeSpan.Zero
    };

    private void Publish(DateTimeOffset now)
    {
        double elapsed = Math.Max(0, (now - _phaseStartedAt).TotalSeconds);
        double progress = _phase is DuckHuntGalleryPhase.Ready or DuckHuntGalleryPhase.WaitingForHits
            ? 0 : Math.Clamp(elapsed / Duration(_phase).TotalSeconds, 0, 1);
        double angle = _phase switch
        {
            DuckHuntGalleryPhase.Ready => 0,
            DuckHuntGalleryPhase.Opening => 95 * Smooth(progress),
            DuckHuntGalleryPhase.Closing => 95 * (1 - Smooth(progress)),
            _ => 95
        };
        double belt = _phase switch
        {
            DuckHuntGalleryPhase.Advancing => progress,
            DuckHuntGalleryPhase.WaitingForHits or DuckHuntGalleryPhase.SuccessWait => 1,
            DuckHuntGalleryPhase.Departing => 1 + progress,
            DuckHuntGalleryPhase.Falling or DuckHuntGalleryPhase.Closing => 2,
            _ => 0
        };
        double fall = _phase == DuckHuntGalleryPhase.Falling ? progress :
            _phase == DuckHuntGalleryPhase.Closing ? 1 : 0;
        var next = new DuckHuntGallerySnapshot(_phase, angle, belt, fall,
            _hitMask, _shots, _cycle, _snapshot.Revision, !_ready);
        if (next != _snapshot) _snapshot = next with { Revision = _snapshot.Revision + 1 };
    }

    private static double Smooth(double value) => value * value * (3 - 2 * value);
}
