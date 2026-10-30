namespace ProjectTabletop.Interaction;

public enum DuckHuntClayPhase { Ready, Playing, Finished }

public readonly record struct DuckHuntClaySnapshot(DuckHuntClayPhase Phase, int Score,
    int PairsLaunched, int Shots, long Revision, bool Paused)
{
    public bool RoundFinished => Phase == DuckHuntClayPhase.Finished;
}

public readonly record struct DuckHuntClayTarget(int Id, int PairIndex, double U, double V,
    double Scale, double BankAngle, double FaceRatio, double FlightSeconds,
    bool Visible, bool Hit, double HitElapsed);

/// <summary>Twenty timestamped clays, in ten staggered pairs. The complete bounded
/// round history remains available to test a fresh, delayed camera photograph.</summary>
public sealed class DuckHuntClayGame
{
    public const int PairsPerRound = 10;
    public const int TargetsPerRound = PairsPerRound * 2;
    public static readonly TimeSpan LaunchDelay = TimeSpan.FromSeconds(.55);
    public static readonly TimeSpan SecondClayDelay = TimeSpan.FromSeconds(.5);
    public static readonly TimeSpan FlightDuration = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan BetweenPairsDuration = TimeSpan.FromSeconds(.75);
    public static readonly TimeSpan HitDuration = TimeSpan.FromSeconds(1.35);
    public static readonly TimeSpan PairInterval = FlightDuration + SecondClayDelay + BetweenPairsDuration;
    public static readonly TimeSpan RoundDuration = LaunchDelay + PairInterval * (PairsPerRound - 1) +
        SecondClayDelay + FlightDuration;

    private readonly DuckHuntClayTarget[] _targets = new DuckHuntClayTarget[TargetsPerRound];
    private readonly double[] _hitSeconds = new double[TargetsPerRound];
    private readonly IReadOnlyList<DuckHuntClayTarget> _readOnlyTargets;
    private DateTimeOffset _observedAt, _inputNotBefore, _roundStartedAt;
    private DateTimeOffset _lastShotAt = DateTimeOffset.MinValue;
    private DateTimeOffset? _pausedAt = DateTimeOffset.MinValue;
    private bool _ready;
    private int _score, _shots;
    private DuckHuntClayPhase _phase = DuckHuntClayPhase.Ready;
    private DuckHuntClaySnapshot _snapshot = new(DuckHuntClayPhase.Ready, 0, 0, 0, 0, true);

    public DuckHuntClayGame()
    {
        _readOnlyTargets = Array.AsReadOnly(_targets);
        Array.Fill(_hitSeconds, double.NaN);
    }

    public DuckHuntClaySnapshot Snapshot => _snapshot;
    public IReadOnlyList<DuckHuntClayTarget> Targets => _readOnlyTargets;

    public void Reset(DateTimeOffset now)
    {
        _observedAt = _inputNotBefore = _roundStartedAt = now;
        _lastShotAt = DateTimeOffset.MinValue;
        _pausedAt = now;
        _ready = false;
        _score = _shots = 0;
        _phase = DuckHuntClayPhase.Ready;
        Array.Fill(_hitSeconds, double.NaN);
        Publish(now);
    }

    public void SetReady(bool ready, DateTimeOffset now)
    {
        if (now < _observedAt || ready == _ready) return;
        Advance(now);
        if (!ready) _pausedAt = now;
        else
        {
            _roundStartedAt += now - _pausedAt!.Value;
            _pausedAt = null;
            _inputNotBefore = now;
            if (_phase == DuckHuntClayPhase.Ready) _phase = DuckHuntClayPhase.Playing;
        }
        _ready = ready;
        Publish(now);
    }

    public bool Restart(DateTimeOffset now)
    {
        if (!_ready || !_snapshot.RoundFinished || now < _observedAt) return false;
        Reset(now);
        SetReady(true, now);
        return true;
    }

    public void Advance(DateTimeOffset now)
    {
        if (now < _observedAt) return;
        _observedAt = now;
        Publish(_pausedAt ?? now);
    }

    /// <summary>The trained-audio/camera gate has already checked clock ordering.
    /// Test only identities that had launched when this photograph was captured.</summary>
    public bool Shoot(BoardAim? aim, DateTimeOffset shotTime, double boardAspect = 1)
    {
        if (!_ready || aim is not { } point ||
            !double.IsFinite(point.U) || !double.IsFinite(point.V) ||
            point.U is < 0 or > 1 || point.V is < 0 or > 1 ||
            !double.IsFinite(boardAspect) || boardAspect <= 0 ||
            shotTime <= _lastShotAt || shotTime < _inputNotBefore ||
            _observedAt - shotTime > DuckHuntAimGate.Freshness) return false;

        Advance(shotTime > _observedAt ? shotTime : _observedAt);
        double seconds = (shotTime - _roundStartedAt).TotalSeconds;
        if (_phase == DuckHuntClayPhase.Ready || seconds < LaunchDelay.TotalSeconds ||
            seconds >= RoundDuration.TotalSeconds) return false;
        _lastShotAt = shotTime;
        _shots++;
        bool hit = false;
        // Later launches are nearer the player, and take precedence if two
        // silhouettes happen to overlap. One click shatters at most one disc.
        for (int id = TargetsPerRound - 1; id >= 0; id--)
        {
            if (!double.IsNaN(_hitSeconds[id])) continue;
            var target = FlyingPoseAt(id, seconds);
            if (!DuckHuntClayLayout.ContainsClay(point, target, boardAspect)) continue;
            _hitSeconds[id] = seconds;
            _score++;
            hit = true;
            break;
        }
        Publish(_observedAt);
        return hit;
    }

    private static double LaunchSeconds(int id) => LaunchDelay.TotalSeconds +
        (id / 2) * PairInterval.TotalSeconds + (id % 2) * SecondClayDelay.TotalSeconds;

    private static DuckHuntClayTarget FlyingPoseAt(int id, double seconds)
    {
        double launch = LaunchSeconds(id);
        double age = seconds - launch;
        double elapsed = Math.Clamp(age, 0, FlightDuration.TotalSeconds);
        double progress = elapsed / FlightDuration.TotalSeconds;
        int pair = id / 2;
        int direction = ((id + pair) % 2) == 0 ? -1 : 1;
        double startU = .5 - direction * (.025 + .012 * (pair % 3));
        double endU = direction < 0 ? -.12 : 1.12;
        // A steep foreground ascent relaxes into a broad ballistic arc as the
        // clay recedes over the valley; it leaves through a side of the image.
        double v = 1.10 - 2.55 * progress + 1.86 * progress * progress;
        double bank = direction * (.18 + .24 * Math.Sin(elapsed * 1.3 + pair * .65));
        double face = .48 + .13 * Math.Sin(elapsed * 1.8 + pair * .4);
        return new(id, pair, startU + (endU - startU) * progress, v,
            1 / (1 + 2.5 * progress), bank, face, elapsed,
            seconds >= launch && seconds < launch + FlightDuration.TotalSeconds, false, 0);
    }

    private void Publish(DateTimeOffset now)
    {
        double seconds = Math.Max(0, (now - _roundStartedAt).TotalSeconds);
        bool changed = false;
        double finishAt = RoundDuration.TotalSeconds;
        for (int id = 0; id < TargetsPerRound; id++)
        {
            bool hit = !double.IsNaN(_hitSeconds[id]);
            var target = FlyingPoseAt(id, hit ? _hitSeconds[id] : seconds);
            if (hit)
            {
                double elapsed = Math.Clamp(seconds - _hitSeconds[id], 0, HitDuration.TotalSeconds);
                target = target with { Hit = true, HitElapsed = elapsed, Visible = elapsed < HitDuration.TotalSeconds };
                finishAt = Math.Max(finishAt, _hitSeconds[id] + HitDuration.TotalSeconds);
            }
            if (_phase == DuckHuntClayPhase.Ready) target = target with { Visible = false };
            changed |= _targets[id] != target;
            _targets[id] = target;
        }
        if (_phase == DuckHuntClayPhase.Playing && seconds >= finishAt)
            _phase = DuckHuntClayPhase.Finished;
        int launched = _phase == DuckHuntClayPhase.Ready || seconds < LaunchDelay.TotalSeconds ? 0 :
            Math.Min(PairsPerRound, 1 + (int)((seconds - LaunchDelay.TotalSeconds) / PairInterval.TotalSeconds));
        var next = new DuckHuntClaySnapshot(_phase, _score, launched, _shots, _snapshot.Revision, !_ready);
        if (changed || next != _snapshot) _snapshot = next with { Revision = _snapshot.Revision + 1 };
    }
}
