using System.Numerics;

namespace ProjectTabletop.Interaction;

public enum DuckHuntGalleryPhase
{
    Ready, Opening, RevealWait, Advancing, WaitingForHits, SuccessWait, Departing, Falling, Closing
}

public enum DuckHuntGalleryUpperPhase
{
    Closed, Raising, WaitingForHits, ShatterWait, Lowering
}

public readonly record struct DuckHuntGalleryPuncture(
    int TargetIndex, double U, double V, DateTimeOffset ShotTime);

public readonly record struct DuckHuntGallerySnapshot(DuckHuntGalleryPhase Phase,
    double FlapAngleDegrees, double ConveyorPosition, double FallProgress,
    int HitMask, int Shots, int Cycle, long Revision, bool Paused = false,
    DuckHuntGalleryUpperPhase UpperPhase = DuckHuntGalleryUpperPhase.Closed,
    double UpperOpenProgress = 0, int SkullHitMask = 0, double AnimationSeconds = 0,
    double Skull0HitSeconds = -1, double Skull1HitSeconds = -1, double Skull2HitSeconds = -1,
    double Skull3HitSeconds = -1, double Skull4HitSeconds = -1)
{
    public bool PlayVisible => Phase == DuckHuntGalleryPhase.Ready && UpperPhase == DuckHuntGalleryUpperPhase.Closed;
    public bool TargetsVisible => Phase is not (DuckHuntGalleryPhase.Ready or DuckHuntGalleryPhase.Closing);
    public bool UpperTargetsVisible => UpperPhase != DuckHuntGalleryUpperPhase.Closed;
    public int HitTargets => BitOperations.PopCount((uint)HitMask);
    public int HitSkulls => BitOperations.PopCount((uint)SkullHitMask);
    public double SkullHitSeconds(int index) => index switch
    {
        0 => Skull0HitSeconds, 1 => Skull1HitSeconds, 2 => Skull2HitSeconds,
        3 => Skull3HitSeconds, 4 => Skull4HitSeconds, _ => -1
    };
}

/// <summary>Parallel paper and crystal-skull galleries share one shot-only PLAY.
/// Each row accepts validated click photos and completes independently.</summary>
public sealed class DuckHuntGalleryGame
{
    public const int TargetCount = 5;
    public const int SkullCount = 5;
    public static readonly TimeSpan OpeningDuration = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan RevealWaitDuration = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan AdvanceDuration = TimeSpan.FromSeconds(1.8);
    public static readonly TimeSpan SuccessWaitDuration = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DepartDuration = TimeSpan.FromSeconds(1.2);
    public static readonly TimeSpan FallDuration = TimeSpan.FromSeconds(.7);
    public static readonly TimeSpan ClosingDuration = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan UpperRaiseDuration = TimeSpan.FromSeconds(1.2);
    public static readonly TimeSpan UpperShatterWaitDuration = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan UpperLowerDuration = TimeSpan.FromSeconds(1.2);

    private readonly List<DuckHuntGalleryPuncture> _punctures = new(32);
    private readonly IReadOnlyList<DuckHuntGalleryPuncture> _readOnlyPunctures;
    private DateTimeOffset _observedAt, _phaseStartedAt, _inputNotBefore;
    private DateTimeOffset _upperPhaseStartedAt, _playStartedAt;
    private DateTimeOffset _lastShotAt = DateTimeOffset.MinValue;
    private DateTimeOffset? _pausedAt = DateTimeOffset.MinValue;
    private bool _ready;
    private int _hitMask, _shots, _cycle;
    private int _skullHitMask;
    private double _skull0HitSeconds = -1, _skull1HitSeconds = -1, _skull2HitSeconds = -1,
        _skull3HitSeconds = -1, _skull4HitSeconds = -1;
    private DuckHuntGalleryPhase _phase = DuckHuntGalleryPhase.Ready;
    private DuckHuntGalleryUpperPhase _upperPhase = DuckHuntGalleryUpperPhase.Closed;
    private DuckHuntGallerySnapshot _snapshot = new(DuckHuntGalleryPhase.Ready,
        0, 0, 0, 0, 0, 0, 0, Paused: true);

    public DuckHuntGalleryGame() => _readOnlyPunctures = _punctures.AsReadOnly();
    public DuckHuntGallerySnapshot Snapshot => _snapshot;
    public IReadOnlyList<DuckHuntGalleryPuncture> Punctures => _readOnlyPunctures;

    public void Reset(DateTimeOffset now)
    {
        _observedAt = _phaseStartedAt = _inputNotBefore = now;
        _upperPhaseStartedAt = _playStartedAt = now;
        _lastShotAt = DateTimeOffset.MinValue;
        _pausedAt = now;
        _ready = false;
        _hitMask = _shots = _cycle = 0;
        _phase = DuckHuntGalleryPhase.Ready;
        _upperPhase = DuckHuntGalleryUpperPhase.Closed;
        ClearSkulls();
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
            var pause = now - _pausedAt!.Value;
            _phaseStartedAt += pause;
            _upperPhaseStartedAt += pause;
            _playStartedAt += pause;
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
            while (_upperPhase is not (DuckHuntGalleryUpperPhase.Closed or DuckHuntGalleryUpperPhase.WaitingForHits))
            {
                var duration = UpperDuration(_upperPhase);
                if (now - _upperPhaseStartedAt < duration) break;
                _upperPhaseStartedAt += duration;
                _upperPhase = _upperPhase switch
                {
                    DuckHuntGalleryUpperPhase.Raising => DuckHuntGalleryUpperPhase.WaitingForHits,
                    DuckHuntGalleryUpperPhase.ShatterWait => DuckHuntGalleryUpperPhase.Lowering,
                    _ => DuckHuntGalleryUpperPhase.Closed
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
        if (_snapshot.PlayVisible)
        {
            if (shotTime < _phaseStartedAt || shotTime < _upperPhaseStartedAt) return false;
            _lastShotAt = shotTime;
            if (!DuckHuntGalleryLayout.PlayBounds.Contains(point.U, point.V)) return false;
            _punctures.Clear();
            _hitMask = _shots = 0;
            _cycle++;
            _phase = DuckHuntGalleryPhase.Opening;
            _phaseStartedAt = _inputNotBefore = shotTime;
            _upperPhase = DuckHuntGalleryUpperPhase.Raising;
            _upperPhaseStartedAt = _playStartedAt = shotTime;
            ClearSkulls();
            Advance(shotTime > _observedAt ? shotTime : _observedAt);
            return true;
        }
        bool paperReady = _phase == DuckHuntGalleryPhase.WaitingForHits && shotTime >= _phaseStartedAt;
        bool skullReady = _upperPhase == DuckHuntGalleryUpperPhase.WaitingForHits && shotTime >= _upperPhaseStartedAt;
        if (!paperReady && !skullReady) return false;
        _lastShotAt = shotTime;
        _shots++;
        bool hit = false;
        for (int index = 0; paperReady && index < TargetCount; index++)
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
        for (int index = 0; !hit && skullReady && index < SkullCount; index++)
        {
            if ((_skullHitMask & (1 << index)) != 0 ||
                !DuckHuntGalleryLayout.ContainsSkull(index, point, boardAspect)) continue;
            _skullHitMask |= 1 << index;
            double seconds = (shotTime - _playStartedAt).TotalSeconds;
            switch (index)
            {
                case 0: _skull0HitSeconds = seconds; break;
                case 1: _skull1HitSeconds = seconds; break;
                case 2: _skull2HitSeconds = seconds; break;
                case 3: _skull3HitSeconds = seconds; break;
                case 4: _skull4HitSeconds = seconds; break;
            }
            hit = true;
            if (_skullHitMask == (1 << SkullCount) - 1)
            {
                _upperPhase = DuckHuntGalleryUpperPhase.ShatterWait;
                _upperPhaseStartedAt = shotTime;
            }
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

    private static TimeSpan UpperDuration(DuckHuntGalleryUpperPhase phase) => phase switch
    {
        DuckHuntGalleryUpperPhase.Raising => UpperRaiseDuration,
        DuckHuntGalleryUpperPhase.ShatterWait => UpperShatterWaitDuration,
        DuckHuntGalleryUpperPhase.Lowering => UpperLowerDuration,
        _ => TimeSpan.Zero
    };

    private void ClearSkulls()
    {
        _skullHitMask = 0;
        _skull0HitSeconds = _skull1HitSeconds = _skull2HitSeconds = _skull3HitSeconds = _skull4HitSeconds = -1;
    }

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
        double upperProgress = _upperPhase is DuckHuntGalleryUpperPhase.Closed or DuckHuntGalleryUpperPhase.WaitingForHits
            ? 0 : Math.Clamp((now - _upperPhaseStartedAt).TotalSeconds / UpperDuration(_upperPhase).TotalSeconds, 0, 1);
        double upperOpen = _upperPhase switch
        {
            DuckHuntGalleryUpperPhase.Closed => 0,
            DuckHuntGalleryUpperPhase.Raising => Smooth(upperProgress),
            DuckHuntGalleryUpperPhase.Lowering => 1 - Smooth(upperProgress),
            _ => 1
        };
        var animationAt = _upperPhase == DuckHuntGalleryUpperPhase.Closed ? _upperPhaseStartedAt : now;
        double animation = _cycle == 0 ? 0 : Math.Floor(Math.Max(0, (animationAt - _playStartedAt).TotalSeconds) * 60) / 60;
        var next = new DuckHuntGallerySnapshot(_phase, angle, belt, fall,
            _hitMask, _shots, _cycle, _snapshot.Revision, !_ready, _upperPhase, upperOpen, _skullHitMask, animation,
            _skull0HitSeconds, _skull1HitSeconds, _skull2HitSeconds, _skull3HitSeconds, _skull4HitSeconds);
        if (next != _snapshot) _snapshot = next with { Revision = _snapshot.Revision + 1 };
    }

    private static double Smooth(double value) => value * value * (3 - 2 * value);
}
