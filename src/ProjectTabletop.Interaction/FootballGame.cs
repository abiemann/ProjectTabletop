using System.Numerics;

namespace ProjectTabletop.Interaction;

public enum FootballMode { HumanVsAi, TwoHumans }
public enum FootballKickerStyle { Car, Glove, Pan, Boot }
public enum FootballPhase { WaitingForPlayers, Countdown, Playing, Goal, Finished }

/// <summary>Heading is the facing in radians: measured for a bar, otherwise turned at a fixed simulated rate.</summary>
public sealed record FootballKickerSnapshot(int Index, Vector2 Position, Vector2 Velocity,
    bool Present, FootballKickerStyle Style, bool IsAi, float Heading = 0, bool MarkerAnchored = false);

/// <summary>Field coordinates are centered: X goes left to right, Y goes top to bottom.
/// BallHeight is its center above the turf, in the same units as the field.</summary>
public sealed record FootballSnapshot(Vector2 BallPosition, Vector2 BallVelocity, float BallHeight,
    float BallVerticalVelocity, Quaternion BallRotation, IReadOnlyList<FootballKickerSnapshot> Kickers,
    int Score1, int Score2, FootballMode Mode, FootballPhase Phase, string Banner,
    float CountdownSeconds, long Revision)
{
    public bool RecoveringInput { get; init; }
}

/// <summary>
/// A deterministic, fixed-step bumper football game. All four appearances have identical circular
/// colliders. Camera observations supply positions, never ball impulses: bounded bumper movement
/// and continuous relative-motion collision tests produce the kick. Call from one thread.
/// </summary>
public sealed class FootballGame
{
    public const float Width = 1.6f;
    public const float Height = 1f;
    public const float BallRadius = .027f;
    public const float KickerRadius = .068f;
    public const float GoalHalfWidth = .17f;
    public const float GoalHeight = .19f;
    public const float GoalDepth = .10f;
    public const float MaxBallSpeed = 2.8f;
    public const float MaxKickerSpeed = 3.2f;
    public const float AiMaxSpeed = .88f;
    public const int WinningScore = 5;
    public static readonly TimeSpan InputFreshness = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan TrackingRecoveryTimeout = TimeSpan.FromMilliseconds(750);

    private const float StepSeconds = 1f / 240;
    private const double StepDuration = 1.0 / 240;
    private const float PostRadius = .012f;
    private const float Gravity = 2.8f;
    private const float CountdownDuration = 1.5f;
    private const float Restitution = .96f;
    private const float HeadingSpeed = .10f;
    private const float HeadingTurnRate = 8.4f;
    private static readonly Vector2[] GoalPosts =
    [
        new(-Width / 2, -GoalHalfWidth), new(-Width / 2, GoalHalfWidth),
        new(Width / 2, -GoalHalfWidth), new(Width / 2, GoalHalfWidth)
    ];
    private readonly Kicker[] _kickers = [new(0), new(1)];
    private FootballMode _mode = FootballMode.HumanVsAi;
    private FootballPhase _phase = FootballPhase.WaitingForPlayers;
    private bool _recoveringInput;
    private Vector2 _ballPosition;
    private Vector2 _ballVelocity;
    private float _ballHeight = BallRadius;
    private float _ballVerticalVelocity;
    private Quaternion _ballRotation = Quaternion.Identity;
    private float _ballSpin;
    private int _score1;
    private int _score2;
    private int _lastScorer;
    private float _phaseRemaining;
    private bool _servePending = true;
    private DateTimeOffset? _lastAdvance;
    private double _accumulator;
    private float _aiThinkRemaining;
    private Vector2 _aiTarget = new(.52f, 0);
    private int _aiRouteSide;
    private FootballSnapshot? _snapshot;

    public long Revision { get; private set; }
    public FootballSnapshot Snapshot => _snapshot ??= CreateSnapshot();

    /// <summary>
    /// Supply a fresh world-space marker/fingertip, or null when the player is lost or ambiguous.
    /// A caller may omit samples during a brief miss: the kicker holds its last target and becomes
    /// absent after InputFreshness. A brief absence freezes live play until tracking returns;
    /// TrackingRecoveryTimeout ends that grace period. Invalid/outside samples disarm immediately. Large discontinuities
    /// reacquire without sweeping a bumper through the pitch, so a changed hand identity cannot
    /// produce a phantom kick. A measured heading rigidly anchors the visible kicker to this pose;
    /// its collision movement still uses bounded velocity derived from observation timestamps.
    /// </summary>
    public void SetPlayerInput(int index, Vector2? position, DateTimeOffset observedAt, float? heading = null)
    {
        ValidateIndex(index);
        if (index == 1 && _mode == FootballMode.HumanVsAi) return;
        var kicker = _kickers[index];
        if (kicker.ObservedAt is { } last && (observedAt < last || observedAt == last && position is not null)) return;
        var previousAt = kicker.ObservedAt;
        kicker.ObservedAt = observedAt;
        if (position is not { } point || !Finite(point) || (heading is { } angle && !float.IsFinite(angle)) ||
            MathF.Abs(point.X) > Width / 2 || MathF.Abs(point.Y) > Height / 2)
        {
            kicker.HasInput = kicker.Present = false;
            kicker.Velocity = Vector2.Zero;
            kicker.MeasuredHeading = null;
            kicker.MeasuredSpeed = 0;
            _recoveringInput = false;
            if (_phase is FootballPhase.Playing or FootballPhase.Countdown)
            {
                _phase = FootballPhase.WaitingForPlayers;
                _phaseRemaining = 0;
                _accumulator = 0;
            }
            Change();
            return;
        }

        // A fresh sample may arrive before Advance observes the expired recovery window.
        // Do not let replacing its source timestamp turn a sustained disconnect into a quick resume.
        if (kicker.HasInput && previousAt is { } lastValid && observedAt - lastValid > TrackingRecoveryTimeout &&
            (_phase is FootballPhase.Playing or FootballPhase.Countdown))
        {
            _phase = FootballPhase.WaitingForPlayers;
            _phaseRemaining = 0;
            _accumulator = 0;
            _recoveringInput = false;
        }

        point = ClampKicker(point);
        float distance = Vector2.Distance(kicker.Target, point);
        // A symmetric bar can change its opponent-facing side when it passes side-on.
        // Its anchor then switches across the bar without having travelled that path.
        // Treat the abrupt facing change as reacquisition, even for a short offset.
        bool facingFlip = heading is { } nextAngle && kicker.MeasuredHeading is { } lastAngle &&
            MathF.Abs(MathF.IEEERemainder(nextAngle - lastAngle, MathF.Tau)) > MathF.PI / 2;
        bool discontinuity = facingFlip || (previousAt is { } before && kicker.HasInput &&
            distance > .30f && distance / Math.Max(.001, (observedAt - before).TotalSeconds) > MaxKickerSpeed * 1.8);
        bool reacquired = !kicker.HasInput || previousAt is null ||
            observedAt - previousAt > InputFreshness || discontinuity ||
            heading.HasValue != kicker.MeasuredHeading.HasValue;
        kicker.HasInput = true;
        kicker.Target = point;
        kicker.MeasuredHeading = heading is { } measured ? MathF.IEEERemainder(measured, MathF.Tau) : null;
        if (kicker.MeasuredHeading is { } facing)
        {
            kicker.Heading = facing;
            if (previousAt is { } sampleAt && distance > .000001f)
                kicker.MeasuredSpeed = (float)Math.Min(MaxKickerSpeed,
                    distance / Math.Max(.001, (observedAt - sampleAt).TotalSeconds));
        }
        else kicker.MeasuredSpeed = 0;
        if (reacquired)
        {
            kicker.Position = point;
            kicker.Velocity = Vector2.Zero;
            kicker.MeasuredSpeed = 0;
            kicker.CollisionQuiet = .10f;
        }
        if (discontinuity) kicker.Present = false;
        Change();
    }

    public void SetStyle(int index, FootballKickerStyle style)
    {
        ValidateIndex(index);
        if (!Enum.IsDefined(style)) throw new ArgumentOutOfRangeException(nameof(style));
        if (_kickers[index].Style == style) return;
        _kickers[index].Style = style;
        Change();
    }

    public void SetMode(FootballMode mode, DateTimeOffset now)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (_lastAdvance is { } last && now < last) return;
        if (mode == _mode) return;
        _mode = mode;
        Reset(now);
    }

    /// <summary>Start a new match, preserving selected mode and appearances. Requires fresh input.</summary>
    public void Reset(DateTimeOffset now)
    {
        if (_lastAdvance is { } last && now < last) return;
        _score1 = _score2 = _lastScorer = 0;
        _phase = FootballPhase.WaitingForPlayers;
        _recoveringInput = false;
        _phaseRemaining = 0;
        _servePending = true;
        _lastAdvance = now;
        _accumulator = 0;
        _aiThinkRemaining = 0;
        _aiTarget = new(.52f, 0);
        _aiRouteSide = 0;
        CenterBall();
        foreach (var kicker in _kickers)
        {
            kicker.HasInput = kicker.Present = false;
            kicker.ObservedAt = null;
            kicker.Position = kicker.Target = new(kicker.Index == 0 ? -.52f : .52f, 0);
            kicker.Velocity = Vector2.Zero;
            kicker.Heading = kicker.Index == 0 ? 0 : MathF.PI;
            kicker.MeasuredHeading = null;
            kicker.MeasuredSpeed = 0;
            kicker.CollisionQuiet = .10f;
        }
        Change();
    }

    public void Advance(DateTimeOffset now)
    {
        if (_lastAdvance is { } previous && now <= previous) return;
        double elapsed = _lastAdvance is { } last ? (now - last).TotalSeconds : 0;
        _lastAdvance = now;
        var phase = _phase;
        bool wasRecovering = _recoveringInput;
        bool changed = UpdatePresence(now, out bool ready);
        // A short render hitch discards simulation debt without restarting a match or its
        // countdown. A sustained window suspension still requires a reconnect countdown.
        if (elapsed > .20)
        {
            bool sustained = elapsed > TrackingRecoveryTimeout.TotalSeconds;
            elapsed = 0;
            _accumulator = 0;
            changed |= FreezeKickerMotion();
            if (sustained && (_phase is FootballPhase.Playing or FootballPhase.Countdown))
            {
                _phase = FootballPhase.WaitingForPlayers;
                _phaseRemaining = 0;
                changed = true;
            }
        }
        _recoveringInput = !ready && (_phase is FootballPhase.Playing or FootballPhase.Countdown) &&
            _kickers.All(kicker => IsAi(kicker) || CanRecover(kicker, now));
        if (!ready && !_recoveringInput && (_phase is FootballPhase.Playing or FootballPhase.Countdown))
        {
            _phase = FootballPhase.WaitingForPlayers;
            _phaseRemaining = 0;
            _accumulator = 0;
        }
        changed |= wasRecovering != _recoveringInput;
        if (_recoveringInput || wasRecovering)
        {
            // Freeze both teams and the ball, preserving momentum only for the later resume.
            // Fresh observations may reposition a human visually, but cannot bank a kick.
            elapsed = 0;
            _accumulator = 0;
            changed |= FreezeKickerMotion();
        }
        if (_recoveringInput)
        {
            if (changed) Change();
            return;
        }
        if (ready && _phase == FootballPhase.WaitingForPlayers)
        {
            _phase = FootballPhase.Countdown;
            _phaseRemaining = CountdownDuration;
            _accumulator = 0;
        }
        changed |= _phase != phase;
        if (_phase is FootballPhase.WaitingForPlayers or FootballPhase.Finished)
        {
            // Play is paused: a present player's kicker follows its input directly,
            // without moving the ball or banking velocity for a later kick.
            foreach (var kicker in _kickers)
            {
                if (kicker.Present && !IsAi(kicker) && kicker.Position != kicker.Target)
                {
                    kicker.Position = kicker.Target;
                    changed = true;
                }
                changed |= kicker.Velocity != Vector2.Zero;
                kicker.Velocity = Vector2.Zero;
            }
            // An idle pause keeps its revision, so renderers reuse the last image.
            if (changed) Change();
            return;
        }

        _accumulator += elapsed;
        while (_accumulator + 1e-9 >= StepDuration)
        {
            _accumulator -= StepDuration;
            Step(ready);
            changed = true;
        }
        if (changed) Change();
    }

    /// <summary>Marks stale humans absent and reports whether anything visible changed.</summary>
    private bool UpdatePresence(DateTimeOffset now, out bool ready)
    {
        ready = true;
        bool changed = false;
        foreach (var kicker in _kickers)
        {
            bool present = IsAi(kicker) ||
                kicker.HasInput && kicker.ObservedAt is { } at && now >= at && now - at <= InputFreshness;
            if (!present)
            {
                bool retainPose = (_phase is FootballPhase.Playing or FootballPhase.Countdown) && CanRecover(kicker, now);
                changed |= kicker.Velocity != Vector2.Zero || !retainPose && kicker.MeasuredHeading is not null;
                kicker.Velocity = Vector2.Zero;
                if (!retainPose) kicker.MeasuredHeading = null;
                kicker.MeasuredSpeed = 0;
                kicker.CollisionQuiet = .10f;
            }
            changed |= kicker.Present != present;
            kicker.Present = present;
            ready &= present;
        }
        return changed;
    }

    private static bool CanRecover(Kicker kicker, DateTimeOffset now) => kicker.HasInput &&
        kicker.ObservedAt is { } at && now >= at && now - at <= TrackingRecoveryTimeout;

    private bool FreezeKickerMotion()
    {
        bool changed = false;
        foreach (var kicker in _kickers)
        {
            if (!IsAi(kicker) && kicker.HasInput && kicker.Position != kicker.Target)
            {
                kicker.Position = kicker.Target;
                changed = true;
            }
            changed |= kicker.Velocity != Vector2.Zero;
            kicker.Velocity = Vector2.Zero;
            kicker.MeasuredSpeed = 0;
            kicker.CollisionQuiet = .10f;
        }
        return changed;
    }

    private void Step(bool ready)
    {
        UpdateKickers();
        if (_phase == FootballPhase.Goal)
        {
            _phaseRemaining -= StepSeconds;
            if (_phaseRemaining <= 0)
            {
                CenterBall();
                _servePending = true;
                _phase = ready ? FootballPhase.Countdown : FootballPhase.WaitingForPlayers;
                _phaseRemaining = ready ? CountdownDuration : 0;
            }
            return;
        }
        if (_phase == FootballPhase.Countdown)
        {
            _phaseRemaining -= StepSeconds;
            if (_phaseRemaining <= 0)
            {
                _phase = FootballPhase.Playing;
                _phaseRemaining = 0;
                if (_servePending)
                {
                    float direction = _lastScorer == 0 ? -1 : _lastScorer == 1 ? 1 : -1;
                    _ballVelocity = new(direction * .54f, ((_score1 + _score2) % 2 == 0 ? 1 : -1) * .10f);
                    _ballVerticalVelocity = .30f;
                    _servePending = false;
                }
                // Moving during the countdown can position the bumper, but cannot bank a kick.
                foreach (var kicker in _kickers) kicker.Velocity = Vector2.Zero;
            }
            return;
        }
        if (_phase != FootballPhase.Playing) return;

        var previousPosition = _ballPosition;
        StepVertical();
        MoveBall();
        ResolveBoundary();
        if (_phase != FootballPhase.Playing) return;
        _ballVelocity *= MathF.Exp(-.055f * StepSeconds);
        if (_ballVelocity.LengthSquared() > .0001f && MathF.Abs(_ballSpin) > .01f)
        {
            var across = new Vector2(-_ballVelocity.Y, _ballVelocity.X);
            _ballVelocity += across * (_ballSpin * .008f * StepSeconds);
        }
        _ballVelocity = Limit(_ballVelocity, MaxBallSpeed);
        _ballSpin *= MathF.Exp(-.75f * StepSeconds);
        var delta = _ballPosition - previousPosition;
        float distance = delta.Length();
        if (distance > .000001f)
        {
            var axis = Vector3.Normalize(new Vector3(-delta.Y, delta.X, 0));
            _ballRotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(axis, distance / BallRadius) * _ballRotation);
        }
        if (MathF.Abs(_ballSpin) > .001f)
            _ballRotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, _ballSpin * StepSeconds) * _ballRotation);
    }

    private void UpdateKickers()
    {
        if (_mode == FootballMode.HumanVsAi && _phase == FootballPhase.Playing)
        {
            _aiThinkRemaining -= StepSeconds;
            if (_aiThinkRemaining <= 0)
            {
                // Observe only every 120ms; positioning and strikes use the same
                // bounded physical movement as before.
                _aiThinkRemaining += .12f;
                _aiTarget = ChooseAiTarget();
            }
        }
        foreach (var kicker in _kickers)
        {
            kicker.StepStart = kicker.Position;
            kicker.CollisionQuiet = Math.Max(0, kicker.CollisionQuiet - StepSeconds);
            kicker.HopCooldown = Math.Max(0, kicker.HopCooldown - StepSeconds);
            if (!kicker.Present) { kicker.Velocity = Vector2.Zero; continue; }
            if (IsAi(kicker))
            {
                var desired = _phase == FootballPhase.Playing
                    ? Limit((_aiTarget - kicker.Position) * 7, AiMaxSpeed) : Vector2.Zero;
                kicker.Velocity += Limit(desired - kicker.Velocity, 4f * StepSeconds);
            }
            else if (kicker.MeasuredHeading is not null)
            {
                // The rendered pose is the measured target, while collision follows its sampled
                // segment at the observation speed. Never turn a 30Hz camera displacement into
                // a 240Hz impulse or extrapolate beyond the observed bar position.
                kicker.Velocity = Limit((kicker.Target - kicker.Position) / StepSeconds, kicker.MeasuredSpeed);
            }
            // Approximately 28ms of follow-through smooths camera sampling. Dividing a whole
            // camera-frame displacement by one 240Hz physics step would turn a gentle nudge
            // into a maximum-speed kick every time a fresh camera observation arrived.
            else kicker.Velocity = Limit((kicker.Target - kicker.Position) * 36, MaxKickerSpeed);
            kicker.Position = ClampKicker(kicker.Position + kicker.Velocity * StepSeconds);
            kicker.Velocity = (kicker.Position - kicker.StepStart) / StepSeconds;
            if (kicker.MeasuredHeading is null) TurnToward(kicker);
        }
    }

    private Vector2 ChooseAiTarget()
    {
        var position = _kickers[1].Position;
        var offset = position - _ballPosition;
        float contact = KickerRadius + BallRadius;
        float clearance = contact + .055f;
        float sideLimit = Height / 2 - KickerRadius;

        // Player two shoots left. Proximity alone cannot authorize a strike:
        // when caught on the left of the ball it would drive it into its own goal.
        if (_ballPosition.X > .08f && offset.X >= contact * .6f &&
            MathF.Abs(offset.Y) <= contact * .55f && offset.Length() < .24f)
        {
            _aiRouteSide = 0;
            return ClampAiTarget(new(_ballPosition.X - .14f, _ballPosition.Y));
        }

        float interceptY = _ballPosition.Y;
        if (_ballVelocity.X > .1f)
        {
            float until = Math.Clamp((.52f - _ballPosition.X) / _ballVelocity.X, 0, .6f);
            interceptY += _ballVelocity.Y * until;
        }
        var target = _ballPosition.X > .08f
            ? new Vector2(_ballPosition.X + clearance, _ballPosition.Y)
            : new Vector2(.52f, interceptY);

        // Reach the own-goal side via a clear lateral lane. Driving directly
        // through the ball to a point behind it is itself an own-goal kick.
        // Keep the chosen lane during the manoeuvre to avoid switching sides.
        if (position.X < _ballPosition.X + clearance * .9f && target.X > _ballPosition.X)
        {
            if (_aiRouteSide == 0)
                _aiRouteSide = MathF.Abs(offset.Y) > .035f ? Math.Sign(offset.Y) : (_ballPosition.Y > 0 ? -1 : 1);
            if (MathF.Abs(_ballPosition.Y + _aiRouteSide * clearance) > sideLimit)
                _aiRouteSide = -_aiRouteSide;
            float laneY = _ballPosition.Y + _aiRouteSide * clearance;
            float laneX = _ballPosition.X + clearance;
            if (offset.Y * _aiRouteSide < clearance * .9f)
                laneX = position.X < _ballPosition.X
                    ? Math.Min(position.X, _ballPosition.X - clearance) : position.X;
            return ClampAiTarget(new(laneX, laneY));
        }
        _aiRouteSide = 0;
        return ClampAiTarget(target);
    }

    private static Vector2 ClampAiTarget(Vector2 target) => new(
        Math.Clamp(target.X, .04f, Width / 2 - KickerRadius),
        Math.Clamp(target.Y, -Height / 2 + KickerRadius, Height / 2 - KickerRadius));

    // Cosmetic facing follows play at a fixed rate of simulated time, so every
    // canvas draws the same heading however many frames it renders.
    private static void TurnToward(Kicker kicker)
    {
        if (kicker.Velocity.LengthSquared() <= HeadingSpeed * HeadingSpeed) return;
        float target = MathF.Atan2(kicker.Velocity.Y, kicker.Velocity.X);
        float limit = HeadingTurnRate * StepSeconds;
        float delta = MathF.IEEERemainder(target - kicker.Heading, MathF.Tau);
        kicker.Heading = MathF.IEEERemainder(kicker.Heading + Math.Clamp(delta, -limit, limit), MathF.Tau);
    }

    private void StepVertical()
    {
        if (_ballHeight <= BallRadius && MathF.Abs(_ballVerticalVelocity) < .035f)
        {
            _ballHeight = BallRadius;
            _ballVerticalVelocity = 0;
            return;
        }
        _ballVerticalVelocity -= Gravity * StepSeconds;
        _ballHeight += _ballVerticalVelocity * StepSeconds;
        if (_ballHeight < BallRadius)
        {
            _ballHeight = BallRadius + (BallRadius - _ballHeight) * .86f;
            _ballVerticalVelocity = MathF.Abs(_ballVerticalVelocity) * .86f;
            if (_ballVerticalVelocity < .065f) { _ballHeight = BallRadius; _ballVerticalVelocity = 0; }
        }
    }

    private void MoveBall()
    {
        float remaining = StepSeconds;
        float elapsed = 0;
        for (int collision = 0; collision < 6 && remaining > .000001f; collision++)
        {
            float nearest = remaining + 1;
            Kicker? hitter = null;
            Vector2 hitCenter = default;
            Vector2 hitVelocity = default;
            float hitRadius = 0;
            foreach (var kicker in _kickers)
            {
                if (!kicker.Present || kicker.CollisionQuiet > 0 || _ballHeight > .16f) continue;
                var center = kicker.StepStart + kicker.Velocity * elapsed;
                if (SweepCircle(_ballPosition - center, _ballVelocity - kicker.Velocity,
                    BallRadius + KickerRadius, remaining, out float time) && time < nearest)
                {
                    nearest = time;
                    hitter = kicker;
                    hitCenter = center;
                    hitVelocity = kicker.Velocity;
                    hitRadius = KickerRadius;
                }
            }
            if (_ballHeight - BallRadius < GoalHeight)
            {
                foreach (var center in GoalPosts)
                {
                    if (SweepCircle(_ballPosition - center, _ballVelocity,
                        BallRadius + PostRadius, remaining, out float time) && time < nearest)
                    {
                        nearest = time;
                        hitter = null;
                        hitCenter = center;
                        hitVelocity = Vector2.Zero;
                        hitRadius = PostRadius;
                    }
                }
            }
            if (nearest > remaining) { _ballPosition += _ballVelocity * remaining; break; }
            _ballPosition += _ballVelocity * nearest;
            hitCenter += hitVelocity * nearest;
            var offset = _ballPosition - hitCenter;
            var normal = offset.LengthSquared() > .0000001f
                ? Vector2.Normalize(offset) : NormalOr(-(_ballVelocity - hitVelocity), Vector2.UnitX);
            _ballPosition = hitCenter + normal * (BallRadius + hitRadius + .00005f);
            float closing = Vector2.Dot(_ballVelocity - hitVelocity, normal);
            if (closing < 0)
            {
                _ballVelocity -= normal * ((1 + Restitution) * closing);
                if (hitter is not null)
                {
                    var tangent = new Vector2(-normal.Y, normal.X);
                    float tangentSpeed = Vector2.Dot(hitVelocity, tangent);
                    _ballVelocity += tangent * tangentSpeed * .22f;
                    _ballSpin = Math.Clamp(_ballSpin + tangentSpeed * 6, -16, 16);
                    if (hitter.HopCooldown <= 0)
                    {
                        _ballVerticalVelocity = Math.Max(_ballVerticalVelocity,
                            Math.Clamp(.24f + -closing * .15f, .24f, .80f));
                        hitter.HopCooldown = .10f;
                    }
                }
                _ballVelocity = Limit(_ballVelocity, MaxBallSpeed);
            }
            // Consume a sliver of time even for an existing overlap, avoiding a zero-time loop.
            float consumed = Math.Max(nearest, .00001f);
            remaining -= consumed;
            elapsed += consumed;
        }
    }

    private void ResolveBoundary()
    {
        float side = Height / 2 - BallRadius;
        if (_ballPosition.Y < -side)
        {
            _ballPosition.Y = -side + (-side - _ballPosition.Y);
            _ballVelocity.Y = MathF.Abs(_ballVelocity.Y) * Restitution;
        }
        else if (_ballPosition.Y > side)
        {
            _ballPosition.Y = side - (_ballPosition.Y - side);
            _ballVelocity.Y = -MathF.Abs(_ballVelocity.Y) * Restitution;
        }
        bool inMouth = MathF.Abs(_ballPosition.Y) < GoalHalfWidth - BallRadius - PostRadius &&
            _ballHeight + BallRadius < GoalHeight;
        float end = Width / 2 - BallRadius;
        if (inMouth && MathF.Abs(_ballPosition.X) >= Width / 2 + BallRadius)
        {
            ScoreGoal(_ballPosition.X > 0 ? 1 : 2);
            return;
        }
        if (inMouth) return;
        if (_ballPosition.X < -end)
        {
            _ballPosition.X = -end + MathF.Min(.10f, -end - _ballPosition.X);
            _ballVelocity.X = MathF.Abs(_ballVelocity.X) * Restitution;
        }
        else if (_ballPosition.X > end)
        {
            _ballPosition.X = end - MathF.Min(.10f, _ballPosition.X - end);
            _ballVelocity.X = -MathF.Abs(_ballVelocity.X) * Restitution;
        }
    }

    private void ScoreGoal(int scorer)
    {
        _lastScorer = scorer;
        if (scorer == 1) _score1++; else _score2++;
        _ballVelocity = Vector2.Zero;
        _ballVerticalVelocity = 0;
        _phase = _score1 >= WinningScore || _score2 >= WinningScore ? FootballPhase.Finished : FootballPhase.Goal;
        _phaseRemaining = _phase == FootballPhase.Goal ? 1.25f : 0;
    }

    private void CenterBall()
    {
        _ballPosition = _ballVelocity = Vector2.Zero;
        _ballHeight = BallRadius;
        _ballVerticalVelocity = _ballSpin = 0;
        _ballRotation = Quaternion.Identity;
    }

    private FootballSnapshot CreateSnapshot()
    {
        string player2 = _mode == FootballMode.HumanVsAi ? "Computer" : "Player 2";
        string banner = _phase switch
        {
            FootballPhase.WaitingForPlayers when !_kickers[0].Present && (_mode == FootballMode.HumanVsAi || _kickers[1].Present)
                => "Player 1 · bring your stick or fingertip onto the pitch",
            FootballPhase.WaitingForPlayers when !_kickers[1].Present && _kickers[0].Present
                => "Player 2 · bring your stick or fingertip onto the pitch",
            FootballPhase.WaitingForPlayers => "Bring both sticks or fingertips onto the pitch",
            FootballPhase.Countdown => $"Ready · {Math.Max(1, (int)MathF.Ceiling(_phaseRemaining))}",
            FootballPhase.Goal => _lastScorer == 1 ? "Player 1 scores!" : $"{player2} scores!",
            FootballPhase.Finished => _score1 >= WinningScore ? "Player 1 wins!" : $"{player2} wins!",
            _ => "First to 5"
        };
        var kickers = Array.AsReadOnly(_kickers.Select(kicker => new FootballKickerSnapshot(kicker.Index,
            kicker.MeasuredHeading is not null ? kicker.Target : kicker.Position,
            kicker.Velocity, kicker.Present, kicker.Style, IsAi(kicker), kicker.Heading,
            kicker.MeasuredHeading is not null)).ToArray());
        return new(_ballPosition, _recoveringInput ? Vector2.Zero : _ballVelocity, _ballHeight,
            _recoveringInput ? 0 : _ballVerticalVelocity, _ballRotation, kickers,
            _score1, _score2, _mode, _phase, banner, _phaseRemaining, Revision) { RecoveringInput = _recoveringInput };
    }

    private static bool SweepCircle(Vector2 relative, Vector2 velocity, float radius, float duration, out float time)
    {
        time = 0;
        float c = relative.LengthSquared() - radius * radius;
        float b = Vector2.Dot(relative, velocity);
        if (c <= 0) return b < -.000001f;
        float a = velocity.LengthSquared();
        if (a < .000001f || b >= 0) return false;
        float discriminant = b * b - a * c;
        if (discriminant < 0) return false;
        time = (-b - MathF.Sqrt(discriminant)) / a;
        return time >= 0 && time <= duration;
    }

    private bool IsAi(Kicker kicker) => kicker.Index == 1 && _mode == FootballMode.HumanVsAi;
    private void Change() { Revision++; _snapshot = null; }
    private static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    private static Vector2 ClampKicker(Vector2 value) => new(
        Math.Clamp(value.X, -Width / 2 + KickerRadius, Width / 2 - KickerRadius),
        Math.Clamp(value.Y, -Height / 2 + KickerRadius, Height / 2 - KickerRadius));
    private static Vector2 Limit(Vector2 value, float maximum)
    {
        float length = value.LengthSquared();
        return length > maximum * maximum ? value * (maximum / MathF.Sqrt(length)) : value;
    }
    private static Vector2 NormalOr(Vector2 value, Vector2 fallback) => value.LengthSquared() > .000001f
        ? Vector2.Normalize(value) : fallback;
    private static void ValidateIndex(int index)
    {
        if (index is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(index));
    }

    private sealed class Kicker(int index)
    {
        public readonly int Index = index;
        public Vector2 Position = new(index == 0 ? -.52f : .52f, 0);
        public Vector2 Target = new(index == 0 ? -.52f : .52f, 0);
        public Vector2 StepStart;
        public Vector2 Velocity;
        public bool HasInput;
        public bool Present;
        public DateTimeOffset? ObservedAt;
        public float CollisionQuiet;
        public float HopCooldown;
        public float Heading = index == 0 ? 0 : MathF.PI;
        public float? MeasuredHeading;
        public float MeasuredSpeed;
        public FootballKickerStyle Style = index == 0 ? FootballKickerStyle.Car : FootballKickerStyle.Glove;
    }
}
