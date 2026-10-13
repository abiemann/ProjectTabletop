using System.Numerics;

namespace ProjectTabletop.Interaction;

public enum FootballMode { HumanVsAi, TwoHumans }
public enum FootballKickerStyle { Car, Glove, Pan, Boot }
public enum FootballPhase { WaitingForPlayers, Countdown, Playing, Goal, Finished, Ready }

/// <summary>Heading is the facing in radians: measured for a bar, otherwise turned at a fixed simulated rate.</summary>
public sealed record FootballKickerSnapshot(int Index, Vector2 Position, Vector2 Velocity,
    bool Present, FootballKickerStyle Style, bool IsAi, float Heading = 0, bool MarkerAnchored = false);

/// <summary>Field coordinates are centered: X goes left to right, Y goes top to bottom.
/// Height is the center above the turf; Growth scales the ball's radius from zero to full size.</summary>
public sealed record FootballBallSnapshot(int Id, Vector2 Position, Vector2 Velocity, float Height,
    float VerticalVelocity, Quaternion Rotation, float Growth);

public sealed record FootballSnapshot(IReadOnlyList<FootballBallSnapshot> Balls, IReadOnlyList<FootballKickerSnapshot> Kickers,
    int Score1, int Score2, FootballMode Mode, FootballPhase Phase, string Banner,
    float CountdownSeconds, long Revision)
{
    public bool RecoveringInput { get; init; }
    public Vector2 BallPosition => Balls[0].Position;
    public Vector2 BallVelocity => Balls[0].Velocity;
    public float BallHeight => Balls[0].Height;
    public float BallVerticalVelocity => Balls[0].VerticalVelocity;
    public Quaternion BallRotation => Balls[0].Rotation;
}

/// <summary>
/// A deterministic, fixed-step bumper football game. All four appearances have identical circular
/// colliders. Camera observations supply positions, never ball impulses: bounded bumper movement
/// and continuous relative-motion collision tests produce the kick. Call from one thread.
/// </summary>
public sealed class FootballGame
{
    public const float Width = 1.6f;
    public const float Height = 1.18f;
    public const float BallRadius = .027f;
    public const float CenterCircleRadius = .139f;
    public const float BallGrowthDuration = .35f;
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
    private FootballPhase _phase = FootballPhase.Ready;
    private bool _recoveringInput;
    private readonly List<Ball> _balls = [new(0, Vector2.Zero)];
    private int _nextBallId = 1;
    private double _rallySeconds;
    private int _spawnStage;
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
    private int _aiBallId = -1;
    private FootballSnapshot? _snapshot;

    public long Revision { get; private set; }
    public FootballPhase Phase => _phase;
    public FootballSnapshot Snapshot => _snapshot ??= CreateSnapshot();

    /// <summary>
    /// Supply a fresh world-space marker/fingertip, or null when the player is lost or ambiguous.
    /// A caller may omit samples during a brief miss: the kicker holds its last target and becomes
    /// absent after InputFreshness. A brief absence freezes live play until tracking returns;
    /// TrackingRecoveryTimeout ends that grace period. Invalid/outside samples disarm immediately. Large discontinuities
    /// reacquire without sweeping a bumper through the pitch, so a changed hand identity cannot
    /// produce a phantom kick. A measured heading rigidly anchors the visible kicker to this pose;
    /// its collision movement still uses bounded velocity derived from observation timestamps.
    /// Finished matches ignore input until Start begins the next match or Reset returns to the preview.
    /// </summary>
    public void SetPlayerInput(int index, Vector2? position, DateTimeOffset observedAt, float? heading = null)
    {
        ValidateIndex(index);
        if (_phase == FootballPhase.Finished || index == 1 && _mode == FootballMode.HumanVsAi) return;
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

    /// <summary>Return to the idle preview, preserving selected mode and appearances. Requires Start to play.</summary>
    public void Reset(DateTimeOffset now) => ResetMatch(now, FootballPhase.Ready);

    /// <summary>Explicitly start a new match, preserving selected mode and appearances. Requires fresh input.</summary>
    public void Start(DateTimeOffset now) => ResetMatch(now, FootballPhase.WaitingForPlayers);

    private void ResetMatch(DateTimeOffset now, FootballPhase phase)
    {
        if (_lastAdvance is { } last && now < last) return;
        _score1 = _score2 = _lastScorer = 0;
        _phase = phase;
        _recoveringInput = false;
        _phaseRemaining = 0;
        _servePending = true;
        _lastAdvance = now;
        _accumulator = 0;
        _aiThinkRemaining = 0;
        _aiTarget = new(.52f, 0);
        _aiRouteSide = 0;
        _aiBallId = -1;
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
        if (_phase == FootballPhase.Finished) return;
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
        if (_phase is FootballPhase.Ready or FootballPhase.WaitingForPlayers)
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
                    _balls[0].Velocity = new(direction * .54f, ((_score1 + _score2) % 2 == 0 ? 1 : -1) * .10f);
                    _balls[0].VerticalVelocity = .30f;
                    _servePending = false;
                }
                // Moving during the countdown can position the bumper, but cannot bank a kick.
                foreach (var kicker in _kickers) kicker.Velocity = Vector2.Zero;
            }
            return;
        }
        if (_phase != FootballPhase.Playing) return;

        foreach (var ball in _balls)
        {
            ball.StepStart = ball.Position;
            if (ball.Growth < 1)
            {
                ball.Growth = Math.Min(1, ball.Growth + StepSeconds / BallGrowthDuration);
                ball.Height = BallRadius * ball.Growth;
            }
            else StepVertical(ball);
        }
        SeparateGrowingBalls();
        MoveBalls();
        foreach (var ball in _balls)
        {
            if (ball.Growth < 1) continue;
            ResolveBoundary(ball);
            if (_phase != FootballPhase.Playing) return;
            ball.Velocity *= MathF.Exp(-.055f * StepSeconds);
            if (ball.Velocity.LengthSquared() > .0001f && MathF.Abs(ball.Spin) > .01f)
            {
                var across = new Vector2(-ball.Velocity.Y, ball.Velocity.X);
                ball.Velocity += across * (ball.Spin * .008f * StepSeconds);
            }
            ball.Velocity = Limit(ball.Velocity, MaxBallSpeed);
            ball.Spin *= MathF.Exp(-.75f * StepSeconds);
            var delta = ball.Position - ball.StepStart;
            float distance = delta.Length();
            if (distance > .000001f)
            {
                var axis = Vector3.Normalize(new Vector3(-delta.Y, delta.X, 0));
                ball.Rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(axis, distance / BallRadius) * ball.Rotation);
            }
            if (MathF.Abs(ball.Spin) > .001f)
                ball.Rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, ball.Spin * StepSeconds) * ball.Rotation);
        }
        _rallySeconds += StepDuration;
        if (_spawnStage == 0 && _rallySeconds + 1e-9 >= 15)
        {
            SpawnSideBalls();
            _spawnStage = 1;
        }
        else if (_spawnStage == 1 && _rallySeconds + 1e-9 >= 30)
        {
            SpawnSideBalls();
            SpawnBall(CenterCircleRadius + BallRadius);
            SpawnBall(-CenterCircleRadius - BallRadius);
            _spawnStage = 2;
        }
    }

    private void SpawnSideBalls()
    {
        SpawnBall(Height / 2 - BallRadius);
        SpawnBall(-Height / 2 + BallRadius);
    }

    private void SpawnBall(float y) => _balls.Add(new(_nextBallId++, new(0, y), 0));

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
        var ball = ChooseAiBall();
        if (ball is null) return new(.52f, 0);
        if (_aiBallId != ball.Id) { _aiBallId = ball.Id; _aiRouteSide = 0; }
        var position = _kickers[1].Position;
        var offset = position - ball.Position;
        float contact = KickerRadius + BallRadius;
        float clearance = contact + .055f;
        float sideLimit = Height / 2 - KickerRadius;

        // Player two shoots left. Proximity alone cannot authorize a strike:
        // when caught on the left of the ball it would drive it into its own goal.
        if (ball.Position.X > .08f && offset.X >= contact * .6f &&
            MathF.Abs(offset.Y) <= contact * .55f && offset.Length() < .24f)
        {
            _aiRouteSide = 0;
            return ClampAiTarget(new(ball.Position.X - .14f, ball.Position.Y));
        }

        float interceptY = ball.Position.Y;
        if (ball.Velocity.X > .1f)
        {
            float until = Math.Clamp((.52f - ball.Position.X) / ball.Velocity.X, 0, .6f);
            interceptY += ball.Velocity.Y * until;
        }
        var target = ball.Position.X > .08f
            ? new Vector2(ball.Position.X + clearance, ball.Position.Y)
            : new Vector2(.52f, interceptY);

        // Reach the own-goal side via a clear lateral lane. Driving directly
        // through the ball to a point behind it is itself an own-goal kick.
        // Keep the chosen lane during the manoeuvre to avoid switching sides.
        if (position.X < ball.Position.X + clearance * .9f && target.X > ball.Position.X)
        {
            if (_aiRouteSide == 0)
                _aiRouteSide = MathF.Abs(offset.Y) > .035f ? Math.Sign(offset.Y) : (ball.Position.Y > 0 ? -1 : 1);
            if (MathF.Abs(ball.Position.Y + _aiRouteSide * clearance) > sideLimit)
                _aiRouteSide = -_aiRouteSide;
            float laneY = ball.Position.Y + _aiRouteSide * clearance;
            float laneX = ball.Position.X + clearance;
            if (offset.Y * _aiRouteSide < clearance * .9f)
                laneX = position.X < ball.Position.X
                    ? Math.Min(position.X, ball.Position.X - clearance) : position.X;
            return ClampAiTarget(new(laneX, laneY));
        }
        _aiRouteSide = 0;
        return ClampAiTarget(target);
    }

    private Ball? ChooseAiBall()
    {
        Ball? selected = null;
        float best = float.NegativeInfinity;
        foreach (var ball in _balls)
        {
            if (ball.Growth < 1 || ball.Height > .16f) continue;
            // Prioritize an approaching shot on our goal, then nearby balls in our
            // half. A ball still growing or sailing above the bumper is not playable.
            float priority = ball.Position.X - Vector2.Distance(_kickers[1].Position, ball.Position) * .35f;
            if (ball.Velocity.X > .1f)
            {
                float until = (Width / 2 - ball.Position.X) / ball.Velocity.X;
                float goalY = ball.Position.Y + ball.Velocity.Y * Math.Max(0, until);
                if (until >= 0 && until < 1.5f && MathF.Abs(goalY) < GoalHalfWidth)
                    priority += 2 + 1.5f - until;
            }
            if (priority > best) { best = priority; selected = ball; }
        }
        return selected;
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

    private static void StepVertical(Ball ball)
    {
        if (ball.Height <= BallRadius && MathF.Abs(ball.VerticalVelocity) < .035f)
        {
            ball.Height = BallRadius;
            ball.VerticalVelocity = 0;
            return;
        }
        ball.VerticalVelocity -= Gravity * StepSeconds;
        ball.Height += ball.VerticalVelocity * StepSeconds;
        if (ball.Height < BallRadius)
        {
            ball.Height = BallRadius + (BallRadius - ball.Height) * .86f;
            ball.VerticalVelocity = MathF.Abs(ball.VerticalVelocity) * .86f;
            if (ball.VerticalVelocity < .065f) { ball.Height = BallRadius; ball.VerticalVelocity = 0; }
        }
    }

    private void MoveBalls()
    {
        float remaining = StepSeconds;
        float elapsed = 0;
        // Every ball shares the same collision clock. Advancing each ball independently
        // would make a ball hit another at its old position and make results order-dependent.
        for (int collision = 0; collision < 6 * _balls.Count && remaining > .000001f; collision++)
        {
            float nearest = remaining + 1;
            Ball? ball = null;
            Ball? other = null;
            Kicker? hitter = null;
            Vector2 hitCenter = default;
            Vector2 hitVelocity = default;
            float hitRadius = 0;
            for (int i = 0; i < _balls.Count; i++)
            {
                var candidate = _balls[i];
                if (candidate.Growth < 1) continue;
                foreach (var kicker in _kickers)
                {
                    if (!kicker.Present || kicker.CollisionQuiet > 0 || candidate.Height > .16f) continue;
                    var center = kicker.StepStart + kicker.Velocity * elapsed;
                    if (SweepCircle(candidate.Position - center, candidate.Velocity - kicker.Velocity,
                        BallRadius + KickerRadius, remaining, out float time) && time < nearest)
                    {
                        nearest = time;
                        ball = candidate;
                        other = null;
                        hitter = kicker;
                        hitCenter = center;
                        hitVelocity = kicker.Velocity;
                        hitRadius = KickerRadius;
                    }
                }
                if (candidate.Height - BallRadius < GoalHeight)
                {
                    foreach (var center in GoalPosts)
                    {
                        if (SweepCircle(candidate.Position - center, candidate.Velocity,
                            BallRadius + PostRadius, remaining, out float time) && time < nearest)
                        {
                            nearest = time;
                            ball = candidate;
                            other = null;
                            hitter = null;
                            hitCenter = center;
                            hitVelocity = Vector2.Zero;
                            hitRadius = PostRadius;
                        }
                    }
                }
                for (int j = i + 1; j < _balls.Count; j++)
                {
                    var second = _balls[j];
                    if (second.Growth < 1) continue;
                    float height = candidate.Height - second.Height;
                    float radiusSquared = 4 * BallRadius * BallRadius - height * height;
                    if (radiusSquared <= 0) continue;
                    var offset = candidate.Position - second.Position;
                    bool overlap = offset.LengthSquared() < radiusSquared - .0000001f;
                    if ((SweepCircle(offset, candidate.Velocity - second.Velocity,
                        MathF.Sqrt(radiusSquared), remaining, out float time) || overlap) && time < nearest)
                    {
                        nearest = overlap ? 0 : time;
                        ball = candidate;
                        other = second;
                        hitter = null;
                    }
                }
            }
            if (ball is null) { MoveAll(remaining); return; }
            MoveAll(nearest);
            if (other is not null)
            {
                ResolveBallContact(ball, other);
            }
            else
            {
                hitCenter += hitVelocity * nearest;
                var offset = ball.Position - hitCenter;
                var normal = offset.LengthSquared() > .0000001f
                    ? Vector2.Normalize(offset) : NormalOr(-(ball.Velocity - hitVelocity), Vector2.UnitX);
                ball.Position = hitCenter + normal * (BallRadius + hitRadius + .00005f);
                float closing = Vector2.Dot(ball.Velocity - hitVelocity, normal);
                if (closing < 0)
                {
                    ball.Velocity -= normal * ((1 + Restitution) * closing);
                    if (hitter is not null)
                    {
                        var tangent = new Vector2(-normal.Y, normal.X);
                        float tangentSpeed = Vector2.Dot(hitVelocity, tangent);
                        ball.Velocity += tangent * tangentSpeed * .22f;
                        ball.Spin = Math.Clamp(ball.Spin + tangentSpeed * 6, -16, 16);
                        if (hitter.HopCooldown <= 0)
                        {
                            ball.VerticalVelocity = Math.Max(ball.VerticalVelocity,
                                Math.Clamp(.24f + -closing * .15f, .24f, .80f));
                            hitter.HopCooldown = .10f;
                        }
                    }
                    ball.Velocity = Limit(ball.Velocity, MaxBallSpeed);
                }
            }
            // Consume a sliver of time even for an existing overlap, avoiding a zero-time loop.
            float consumed = Math.Max(nearest, .00001f);
            remaining -= consumed;
            elapsed += consumed;
        }
        // Dense simultaneous contacts are bounded; do not make unrelated balls lose
        // the rest of their movement when that contact budget is exhausted.
        if (remaining > 0) MoveAll(remaining);
    }

    private void MoveAll(float seconds)
    {
        foreach (var ball in _balls)
            if (ball.Growth >= 1) ball.Position += ball.Velocity * seconds;
    }

    private static void ResolveBallContact(Ball first, Ball second)
    {
        float height = first.Height - second.Height;
        float radius = MathF.Sqrt(Math.Max(0, 4 * BallRadius * BallRadius - height * height));
        var offset = first.Position - second.Position;
        var direction = NormalOr(offset, Vector2.UnitX);
        var correction = direction * Math.Max(0, radius + .00005f - offset.Length()) * .5f;
        first.Position += correction;
        second.Position -= correction;
        // Equal-mass sphere impact: airborne balls may pass over one another, and
        // glancing height differences can transfer momentum vertically.
        var normal = Vector3.Normalize(new Vector3(direction * radius, height));
        var relative = new Vector3(first.Velocity - second.Velocity,
            first.VerticalVelocity - second.VerticalVelocity);
        float closing = Vector3.Dot(relative, normal);
        if (closing >= 0) return;
        var impulse = normal * (-(1 + Restitution) * closing * .5f);
        var horizontal = new Vector2(impulse.X, impulse.Y);
        first.Velocity = Limit(first.Velocity + horizontal, MaxBallSpeed);
        second.Velocity = Limit(second.Velocity - horizontal, MaxBallSpeed);
        first.VerticalVelocity += impulse.Z;
        second.VerticalVelocity -= impulse.Z;
    }

    private void SeparateGrowingBalls()
    {
        // The second wave uses the same sideline spots. If an earlier ball is still
        // there, gradually make room as the new sphere grows, without a launch impulse.
        for (int i = 0; i < _balls.Count; i++)
        for (int j = i + 1; j < _balls.Count; j++)
        {
            var first = _balls[i];
            var second = _balls[j];
            if (first.Growth >= 1 && second.Growth >= 1) continue;
            float radius = BallRadius * (first.Growth + second.Growth);
            float height = first.Height - second.Height;
            float horizontalSquared = radius * radius - height * height;
            var offset = first.Position - second.Position;
            if (horizontalSquared <= 0 || offset.LengthSquared() >= horizontalSquared) continue;
            var correction = NormalOr(offset, Vector2.UnitX) *
                ((MathF.Sqrt(horizontalSquared) - offset.Length()) * .5f);
            first.Position = ClampBall(first.Position + correction, BallRadius * first.Growth);
            second.Position = ClampBall(second.Position - correction, BallRadius * second.Growth);
        }
    }

    private static Vector2 ClampBall(Vector2 position, float radius) => new(
        Math.Clamp(position.X, -Width / 2 + radius, Width / 2 - radius),
        Math.Clamp(position.Y, -Height / 2 + radius, Height / 2 - radius));

    private void ResolveBoundary(Ball ball)
    {
        float side = Height / 2 - BallRadius;
        if (ball.Position.Y < -side)
        {
            ball.Position.Y = -side + (-side - ball.Position.Y);
            ball.Velocity.Y = MathF.Abs(ball.Velocity.Y) * Restitution;
        }
        else if (ball.Position.Y > side)
        {
            ball.Position.Y = side - (ball.Position.Y - side);
            ball.Velocity.Y = -MathF.Abs(ball.Velocity.Y) * Restitution;
        }
        bool inMouth = MathF.Abs(ball.Position.Y) < GoalHalfWidth - BallRadius - PostRadius &&
            ball.Height + BallRadius < GoalHeight;
        float end = Width / 2 - BallRadius;
        if (inMouth && MathF.Abs(ball.Position.X) >= Width / 2 + BallRadius)
        {
            ScoreGoal(ball, ball.Position.X > 0 ? 1 : 2);
            return;
        }
        if (inMouth) return;
        if (ball.Position.X < -end)
        {
            ball.Position.X = -end + MathF.Min(.10f, -end - ball.Position.X);
            ball.Velocity.X = MathF.Abs(ball.Velocity.X) * Restitution;
        }
        else if (ball.Position.X > end)
        {
            ball.Position.X = end - MathF.Min(.10f, ball.Position.X - end);
            ball.Velocity.X = -MathF.Abs(ball.Velocity.X) * Restitution;
        }
    }

    private void ScoreGoal(Ball ball, int scorer)
    {
        _lastScorer = scorer;
        if (scorer == 1) _score1++; else _score2++;
        ball.Velocity = Vector2.Zero;
        ball.VerticalVelocity = 0;
        _balls.Clear();
        _balls.Add(ball);
        _rallySeconds = 0;
        _spawnStage = 0;
        _phase = _score1 >= WinningScore || _score2 >= WinningScore ? FootballPhase.Finished : FootballPhase.Goal;
        _phaseRemaining = _phase == FootballPhase.Goal ? 1.25f : 0;
    }

    private void CenterBall()
    {
        _balls.Clear();
        _balls.Add(new(_nextBallId++, Vector2.Zero));
        _rallySeconds = 0;
        _spawnStage = 0;
    }

    private FootballSnapshot CreateSnapshot()
    {
        string player2 = _mode == FootballMode.HumanVsAi ? "Computer" : "Player 2";
        string banner = _phase switch
        {
            FootballPhase.Ready => "Hold PLAY to start",
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
        var balls = Array.AsReadOnly(_balls.Select(ball => new FootballBallSnapshot(ball.Id, ball.Position,
            _recoveringInput ? Vector2.Zero : ball.Velocity, ball.Height,
            _recoveringInput ? 0 : ball.VerticalVelocity, ball.Rotation, ball.Growth)).ToArray());
        return new(balls, kickers,
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

    private sealed class Ball(int id, Vector2 position, float growth = 1)
    {
        public readonly int Id = id;
        public Vector2 Position = position;
        public Vector2 Velocity;
        public Vector2 StepStart;
        public float Height = BallRadius * growth;
        public float VerticalVelocity;
        public Quaternion Rotation = Quaternion.Identity;
        public float Spin;
        public float Growth = growth;
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
        public FootballKickerStyle Style = FootballKickerStyle.Car;
    }
}
