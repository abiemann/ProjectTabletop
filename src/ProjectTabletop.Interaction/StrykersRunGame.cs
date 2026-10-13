using System.Numerics;

namespace ProjectTabletop.Interaction;

public enum StrykersRunPhase { Ready, Starting, WaitingForInput, Playing, GameOver }
public enum StrykersRunEnemyKind { Runner, Brute, Boss }
public enum StrykersRunGateKind { Add, Subtract, Multiply }
public enum StrykersRunObjectiveKind { Rescue, Reinforcements }
public enum StrykersRunEffectKind { MuzzleFlash, Hit, Defeat, GateBoost, GatePenalty, SoldierLost, AllyUnlocked, AllyAttack, AllyLost, SupplyOpened }

// Value items: a snapshot allocates one array per collection, not an object per actor.
public readonly record struct StrykersRunEnemySnapshot(int Id, float X, float Y, StrykersRunEnemyKind Kind,
    int Health, int MaxHealth, float AnimationPhase);
public readonly record struct StrykersRunGateSnapshot(int Id, float X, float Y, float Width, StrykersRunGateKind Kind, int Value);
public readonly record struct StrykersRunBulletSnapshot(int Id, float X, float Y);
public readonly record struct StrykersRunEffectSnapshot(StrykersRunEffectKind Kind, float X, float Y, float Age, int Seed);
public readonly record struct StrykersRunObjectiveSnapshot(int Id, float X, float Y,
    StrykersRunObjectiveKind Kind, int Health, int MaxHealth);
public readonly record struct StrykersRunAllySnapshot(float X, float Y, int Health, int MaxHealth,
    float AnimationPhase, float AttackSeconds);
public sealed record StrykersRunSnapshot(StrykersRunPhase Phase, float SquadX, int SoldierCount, int Score,
    float Distance, int Wave, float RunSeconds, float GameOverSeconds, bool RecoveringInput,
    string Banner, long Revision, IReadOnlyList<StrykersRunEnemySnapshot> Enemies,
    IReadOnlyList<StrykersRunGateSnapshot> Gates, IReadOnlyList<StrykersRunBulletSnapshot> Bullets,
    IReadOnlyList<StrykersRunEffectSnapshot> Effects)
{
    public float PhaseSeconds { get; init; }
    public IReadOnlyList<StrykersRunObjectiveSnapshot> Objectives { get; init; } = [];
    public StrykersRunAllySnapshot? Ally { get; init; }
    public bool HeroAlive => SoldierCount > 0;
    public int TroopCount => Math.Max(0, SoldierCount - 1);
}

/// <summary>
/// Single-player forward-running shooter. Input supplies an absolute horizontal rail
/// position; a fixed simulation clock owns movement, firing, contacts and presentation.
/// Call from one thread. Missing input pauses the complete run without saving time debt.
/// </summary>
public sealed class StrykersRunGame
{
    public const float RoadLeft = .08f, RoadRight = .92f, SquadY = .78f;
    public const int InitialSoldiers = 1, MaximumSoldiers = 64;
    public const int MaximumEnemies = 72, MaximumBullets = 192, MaximumEffects = 96;
    public const int MaximumObjectives = 2, DefenderMaximumHealth = 24;
    public const float ObjectiveHalfWidth = 110f / 900;
    public const float ObjectiveShootableY = .20f;
    public const float ControlTransitionSeconds = .45f;
    public const float GameOverWallSeconds = 2.7f;
    public const float GameOverPresentationSeconds = GameOverWallSeconds + ControlTransitionSeconds;
    public static readonly TimeSpan InputFreshness = TimeSpan.FromMilliseconds(250);
    private const double StepDuration = 1.0 / 120;
    private const float StepSeconds = 1f / 120;
    private const float BulletSpeed = 1.7f;
    private readonly List<Enemy> _enemies = new(MaximumEnemies);
    private readonly List<Gate> _gates = new(8);
    private readonly List<Bullet> _bullets = new(MaximumBullets);
    private readonly List<Effect> _effects = new(MaximumEffects);
    private readonly List<Objective> _objectives = new(MaximumObjectives);
    private Ally? _ally;
    private StrykersRunPhase _phase;
    private float _squadX = .5f, _targetX = .5f;
    private int _soldiers = InitialSoldiers, _score, _wave = 1;
    private float _distance, _runSeconds, _phaseSeconds;
    private bool _hasInput, _hasPlayed;
    private DateTimeOffset? _inputAt, _lastAdvance;
    private DateTimeOffset _inputResetThrough = DateTimeOffset.MinValue;
    private double _accumulator;
    private float _fireRemaining, _enemyRemaining = .45f, _gateRemaining = 4;
    private float _objectiveRemaining = 2, _rushRemaining = 12;
    private int _objectiveCount, _rushCount;
    private int _nextId = 1, _nextGateRow, _bossWave;
    private uint _random = 0x53545259;
    private StrykersRunSnapshot? _snapshot;

    public long Revision { get; private set; }
    public StrykersRunSnapshot Snapshot => _snapshot ??= CreateSnapshot();
    // Control and rail transitions need only these values; reading them never builds a snapshot.
    public StrykersRunPhase Phase => _phase;
    public float PhaseSeconds => _phaseSeconds;
    public float GameOverSeconds => _phase == StrykersRunPhase.GameOver ? _phaseSeconds : 0;

    public void Start(DateTimeOffset now) => ResetCore(now, StrykersRunPhase.Starting);
    public void Reset(DateTimeOffset now) => ResetCore(now, StrykersRunPhase.Ready);

    private void ResetCore(DateTimeOffset now, StrykersRunPhase phase)
    {
        if (_lastAdvance is { } previous && now < previous) return;
        _phase = phase;
        _squadX = _targetX = .5f;
        _soldiers = InitialSoldiers;
        _score = 0;
        _wave = 1;
        _distance = _runSeconds = _phaseSeconds = 0;
        _hasInput = _hasPlayed = false;
        _inputAt = null;
        _inputResetThrough = now;
        _lastAdvance = now;
        _accumulator = 0;
        _fireRemaining = 0;
        _enemyRemaining = .45f;
        _gateRemaining = 4;
        _objectiveRemaining = 2;
        _rushRemaining = 12;
        _objectiveCount = _rushCount = 0;
        _ally = null;
        _nextId = 1;
        _nextGateRow = _bossWave = 0;
        _random = 0x53545259;
        _enemies.Clear();
        _gates.Clear();
        _bullets.Clear();
        _effects.Clear();
        _objectives.Clear();
        Change();
    }

    public void SetInput(float? normalizedX, DateTimeOffset frameTime)
    {
        if (_phase is StrykersRunPhase.Ready or StrykersRunPhase.GameOver ||
            frameTime <= _inputResetThrough || _inputAt is { } previous && frameTime <= previous) return;
        _inputAt = frameTime;
        _hasInput = normalizedX is { } x && float.IsFinite(x) && x is >= 0 and <= 1;
        if (_hasInput) _targetX = RoadLeft + normalizedX!.Value * (RoadRight - RoadLeft);
        else PauseForInput();
        Change();
    }

    public void ClearInput(DateTimeOffset now)
    {
        if (now < _inputResetThrough || _inputAt is { } previous && now < previous) return;
        _inputResetThrough = now;
        _inputAt = null;
        _hasInput = false;
        PauseForInput();
        Change();
    }

    private void PauseForInput()
    {
        if (_phase == StrykersRunPhase.Playing)
        {
            _phase = StrykersRunPhase.WaitingForInput;
            _phaseSeconds = 0;
        }
        _accumulator = 0;
    }

    public void Advance(DateTimeOffset now)
    {
        if (_lastAdvance is { } previous && now <= previous) return;
        double elapsed = _lastAdvance is { } last ? (now - last).TotalSeconds : 0;
        _lastAdvance = now;
        if (_phase == StrykersRunPhase.Ready) return;
        if (elapsed > .20) { elapsed = 0; _accumulator = 0; }
        bool fresh = _hasInput && _inputAt is { } at && now >= at && now - at <= InputFreshness;
        if (_phase == StrykersRunPhase.Playing && !fresh)
        {
            PauseForInput();
            Change();
            return;
        }
        if (_phase == StrykersRunPhase.WaitingForInput)
        {
            if (!fresh) return;
            _phase = StrykersRunPhase.Playing;
            _phaseSeconds = 0;
            _hasPlayed = true;
            _accumulator = 0;
            Change();
            return;
        }
        if (_phase == StrykersRunPhase.GameOver && _phaseSeconds >= GameOverPresentationSeconds) return;
        _accumulator += elapsed;
        bool changed = false;
        while (_accumulator + 1e-9 >= StepDuration)
        {
            _accumulator -= StepDuration;
            if (_phase == StrykersRunPhase.Starting)
            {
                _phaseSeconds += StepSeconds;
                if (_phaseSeconds + .000001f >= ControlTransitionSeconds)
                {
                    _phase = fresh ? StrykersRunPhase.Playing : StrykersRunPhase.WaitingForInput;
                    _phaseSeconds = 0;
                    _hasPlayed |= fresh;
                }
            }
            else if (_phase == StrykersRunPhase.GameOver)
                _phaseSeconds = Math.Min(GameOverPresentationSeconds, _phaseSeconds + StepSeconds);
            else if (_phase == StrykersRunPhase.Playing) Step();
            changed = true;
        }
        if (changed) Change();
    }

    private void Step()
    {
        _runSeconds += StepSeconds;
        _distance += StepSeconds * 8;
        _wave = 1 + (int)(_runSeconds / 18);
        _squadX += Math.Clamp(_targetX - _squadX, -2.2f * StepSeconds, 2.2f * StepSeconds);
        for (int i = _effects.Count - 1; i >= 0; i--)
        {
            _effects[i].Age += StepSeconds;
            float lifetime = _effects[i].Kind switch
            {
                StrykersRunEffectKind.MuzzleFlash => .10f,
                StrykersRunEffectKind.AllyUnlocked or StrykersRunEffectKind.SupplyOpened => 1.6f,
                StrykersRunEffectKind.AllyLost => 1.1f,
                StrykersRunEffectKind.AllyAttack => .55f,
                _ => .75f
            };
            if (_effects[i].Age > lifetime)
                _effects.RemoveAt(i);
        }
        _enemyRemaining -= StepSeconds;
        if (_enemyRemaining <= 0)
        {
            SpawnEnemy();
            _enemyRemaining += Math.Max(.20f, 1.05f - _wave * .055f);
        }
        if (_wave % 4 == 0 && _bossWave != _wave && _enemies.Count < MaximumEnemies)
        {
            _bossWave = _wave;
            AddEnemy(StrykersRunEnemyKind.Boss, .5f);
        }
        _rushRemaining -= StepSeconds;
        if (_rushRemaining <= 0)
        {
            SpawnRush();
            _rushRemaining += Math.Max(10, 15 - _wave * .35f);
        }
        _objectiveRemaining -= StepSeconds;
        if (_objectiveRemaining <= 0)
        {
            SpawnObjective();
            _objectiveRemaining += 9;
        }
        _gateRemaining -= StepSeconds;
        if (_gateRemaining <= 0)
        {
            SpawnGates();
            _gateRemaining += 7;
        }
        foreach (var enemy in _enemies)
        {
            enemy.Previous = new(enemy.X, enemy.Y);
            float speed = enemy.Kind switch
            {
                StrykersRunEnemyKind.Boss => .065f,
                StrykersRunEnemyKind.Brute => .09f,
                _ => .125f
            };
            enemy.Y += (speed + Math.Min(.07f, _wave * .003f)) * StepSeconds;
            enemy.X = Math.Clamp(enemy.Lane + MathF.Sin(_runSeconds * 2 + enemy.Id) * .018f, RoadLeft, RoadRight);
        }
        foreach (var objective in _objectives)
        {
            objective.Previous = new(objective.X, objective.Y);
            objective.Y += .115f * StepSeconds;
        }
        _fireRemaining -= StepSeconds;
        if (_fireRemaining <= 0)
        {
            Fire();
            _fireRemaining += .18f;
        }
        MoveBullets();
        _objectives.RemoveAll(objective => objective.Y > SquadY + .06f);
        StepAlly();
        MoveGates();
        if (_phase == StrykersRunPhase.GameOver) return;
        var squad = StrykersRunFormation.HorizontalBounds(_squadX, _soldiers);
        for (int i = _enemies.Count - 1; i >= 0; i--)
        {
            var enemy = _enemies[i];
            if (enemy.Health <= 0) { _enemies.RemoveAt(i); continue; }
            if (enemy.Previous.Y < SquadY && enemy.Y >= SquadY &&
                enemy.X + EnemyRadius(enemy.Kind) >= squad.Left && enemy.X - EnemyRadius(enemy.Kind) <= squad.Right)
            {
                int damage = EnemyContactDamage(enemy.Kind);
                // The central protagonist is permanent: casualties consume all
                // recruited troops before the final remaining soldier can die.
                _soldiers = Math.Max(0, _soldiers - damage);
                AddEffect(StrykersRunEffectKind.SoldierLost, enemy.X, SquadY);
                _enemies.RemoveAt(i);
                if (_soldiers == 0) { EndRun(); return; }
                squad = StrykersRunFormation.HorizontalBounds(_squadX, _soldiers);
            }
            else if (enemy.Y > 1.06f) _enemies.RemoveAt(i);
        }
    }

    private void Fire()
    {
        int shooters = Math.Clamp((_soldiers + 3) / 4, 1, 12);
        for (int i = 0; i < shooters && _bullets.Count < MaximumBullets; i++)
        {
            // The hero always fires; representative recruits fire from their
            // actual formation slots instead of a second, unrelated spread.
            var origin = i == 0 ? new Vector2(_squadX, SquadY) :
                StrykersRunFormation.TroopPosition(_squadX, _soldiers, i * (_soldiers - 1) / shooters);
            origin.Y -= .035f;
            _bullets.Add(new(_nextId++, origin.X, origin.Y));
            AddEffect(StrykersRunEffectKind.MuzzleFlash, origin.X, origin.Y);
        }
    }

    private void MoveBullets()
    {
        for (int i = _bullets.Count - 1; i >= 0; i--)
        {
            var bullet = _bullets[i];
            var start = new Vector2(bullet.X, bullet.Y);
            var end = start - new Vector2(0, BulletSpeed * StepSeconds);
            Enemy? hit = null;
            Objective? objectiveHit = null;
            float nearest = float.PositiveInfinity;
            foreach (var enemy in _enemies)
            {
                if (enemy.Health <= 0) continue;
                var relative = start - enemy.Previous;
                var motion = end - start - (new Vector2(enemy.X, enemy.Y) - enemy.Previous);
                if (Sweep(relative, motion, EnemyRadius(enemy.Kind) + .004f, out float contact) && contact < nearest)
                { nearest = contact; hit = enemy; }
            }
            foreach (var objective in _objectives)
            {
                // A numbered lock must enter the visible road below the HUD
                // before it can absorb fire or grant a reward.
                if (objective.Y < ObjectiveShootableY) continue;
                var relative = start - objective.Previous;
                var motion = end - start - (new Vector2(objective.X, objective.Y) - objective.Previous);
                if (Sweep(relative, motion, ObjectiveHalfWidth + .004f, out float contact) && contact < nearest)
                { nearest = contact; objectiveHit = objective; hit = null; }
            }
            if (hit is not null)
            {
                DamageEnemy(hit, 1);
                _bullets.RemoveAt(i);
            }
            else if (objectiveHit is not null)
            {
                if (--objectiveHit.Health == 0) OpenObjective(objectiveHit);
                else AddEffect(StrykersRunEffectKind.Hit, objectiveHit.X, objectiveHit.Y);
                _bullets.RemoveAt(i);
            }
            else
            {
                bullet.Y = end.Y;
                if (bullet.Y < -.06f) _bullets.RemoveAt(i);
            }
        }
    }

    private void DamageEnemy(Enemy enemy, int damage)
    {
        if (enemy.Health <= 0) return;
        enemy.Health = Math.Max(0, enemy.Health - damage);
        AddEffect(enemy.Health == 0 ? StrykersRunEffectKind.Defeat : StrykersRunEffectKind.Hit, enemy.X, enemy.Y);
        if (enemy.Health != 0) return;
        int points = enemy.Kind switch { StrykersRunEnemyKind.Boss => 250, StrykersRunEnemyKind.Brute => 25, _ => 10 };
        _score = (int)Math.Min(int.MaxValue, (long)_score + points);
    }

    private void SpawnObjective()
    {
        if (_objectives.Count >= MaximumObjectives) return;
        bool rescue = _ally is null && !_objectives.Any(objective => objective.Kind == StrykersRunObjectiveKind.Rescue);
        // The first rescue is directly in the hero's initial firing lane. Later
        // encounters alternate sides, so freeing help remains a steering choice.
        float x = _objectiveCount == 0 ? .5f : _objectiveCount % 2 == 0 ? .28f : .72f;
        _objectiveCount++;
        _objectives.Add(new(_nextId++, x, -.04f,
            rescue ? StrykersRunObjectiveKind.Rescue : StrykersRunObjectiveKind.Reinforcements, rescue ? 8 : 12));
    }

    private void OpenObjective(Objective objective)
    {
        _objectives.Remove(objective);
        if (objective.Kind == StrykersRunObjectiveKind.Rescue)
        {
            // Projectiles hit the containment lock, never the creature inside.
            _ally = new();
            AddEffect(StrykersRunEffectKind.AllyUnlocked, objective.X, objective.Y);
        }
        else
        {
            _soldiers = Math.Min(MaximumSoldiers, _soldiers + 6);
            AddEffect(StrykersRunEffectKind.SupplyOpened, objective.X, objective.Y);
        }
    }

    private void StepAlly()
    {
        if (_ally is not { } ally) return;
        Vector2 position = StrykersRunFormation.DefenderPosition(_squadX);
        ally.AttackSeconds = Math.Min(1, ally.AttackSeconds + StepSeconds);
        ally.AttackRemaining = Math.Max(0, ally.AttackRemaining - StepSeconds);
        if (ally.AttackRemaining <= 0)
        {
            bool attacked = false;
            foreach (var enemy in _enemies)
            {
                if (enemy.Health <= 0 || MathF.Abs(enemy.X - position.X) > .13f + EnemyRadius(enemy.Kind) ||
                    enemy.Y < position.Y - .13f || enemy.Y > position.Y + .025f) continue;
                DamageEnemy(enemy, 4);
                attacked = true;
            }
            if (attacked)
            {
                ally.AttackRemaining = .70f;
                ally.AttackSeconds = 0;
                AddEffect(StrykersRunEffectKind.AllyAttack, position.X, position.Y);
            }
        }
        for (int i = _enemies.Count - 1; i >= 0; i--)
        {
            var enemy = _enemies[i];
            if (enemy.Health <= 0 || enemy.Previous.Y >= position.Y || enemy.Y < position.Y ||
                MathF.Abs(enemy.X - position.X) > StrykersRunFormation.DefenderHalfWidth + EnemyRadius(enemy.Kind)) continue;
            ally.Health = Math.Max(0, ally.Health - EnemyContactDamage(enemy.Kind));
            // Interception consumes the threat, just as a squad contact does;
            // only offensive melee or bullet defeats award points.
            _enemies.RemoveAt(i);
            AddEffect(StrykersRunEffectKind.Defeat, enemy.X, enemy.Y);
            if (ally.Health == 0)
            {
                AddEffect(StrykersRunEffectKind.AllyLost, position.X, position.Y);
                _ally = null;
                return;
            }
        }
    }

    private void SpawnRush()
    {
        int count = Math.Min(8, 4 + _wave / 2);
        float center = (_rushCount++ % 3) switch { 0 => .5f, 1 => .27f, _ => .73f };
        for (int i = 0; i < count && _enemies.Count < MaximumEnemies; i++)
        {
            float x = center + (i - (count - 1) * .5f) * .039f;
            AddEnemy(StrykersRunEnemyKind.Runner, Math.Clamp(x, RoadLeft + .025f, RoadRight - .025f), -.07f - i % 2 * .035f);
        }
    }

    private static bool Sweep(Vector2 offset, Vector2 motion, float radius, out float contact)
    {
        contact = 0;
        float c = offset.LengthSquared() - radius * radius;
        if (c <= 0) return true;
        float a = motion.LengthSquared(), b = Vector2.Dot(offset, motion);
        if (a < .000000001f || b >= 0) return false;
        float d = b * b - a * c;
        if (d < 0) return false;
        contact = (-b - MathF.Sqrt(d)) / a;
        return contact is >= 0 and <= 1;
    }

    private void SpawnEnemy()
    {
        if (_enemies.Count >= MaximumEnemies) return;
        var kind = _wave > 1 && Random() < .28f ? StrykersRunEnemyKind.Brute : StrykersRunEnemyKind.Runner;
        AddEnemy(kind, RoadLeft + .035f + Random() * (RoadRight - RoadLeft - .07f));
    }

    private void AddEnemy(StrykersRunEnemyKind kind, float x, float y = -.06f)
    {
        int health = kind switch { StrykersRunEnemyKind.Boss => 40 + Math.Min(120, _wave * 5),
            StrykersRunEnemyKind.Brute => 4 + Math.Min(12, _wave / 2), _ => 2 + Math.Min(5, _wave / 4) };
        _enemies.Add(new(_nextId++, x, y, kind, health));
    }

    private void SpawnGates()
    {
        if (_gates.Count > 6) return;
        int row = ++_nextGateRow;
        bool multiply = row % 2 == 0;
        var goodKind = multiply ? StrykersRunGateKind.Multiply : StrykersRunGateKind.Add;
        int goodValue = multiply ? 2 : Math.Min(18, 5 + _wave);
        bool goodLeft = Random() < .5f;
        _gates.Add(new(_nextId++, row, goodLeft ? .28f : .72f, goodKind, goodValue));
        _gates.Add(new(_nextId++, row, goodLeft ? .72f : .28f, StrykersRunGateKind.Subtract, Math.Min(18, 3 + _wave)));
    }

    private void MoveGates()
    {
        foreach (var gate in _gates) gate.Y += .13f * StepSeconds;
        // Both choices share a row. Remove the complete row after its first crossing,
        // so moving sideways cannot claim both gates or apply a penalty twice.
        while (_gates.Count > 0 && _gates[0].Y >= SquadY)
        {
            int row = _gates[0].Row;
            Gate? selected = null;
            foreach (var gate in _gates)
                if (gate.Row == row && MathF.Abs(gate.X - _squadX) <= gate.Width / 2 &&
                    (selected is null || MathF.Abs(gate.X - _squadX) < MathF.Abs(selected.X - _squadX))) selected = gate;
            if (selected is not null)
            {
                long count = selected.Kind switch
                {
                    StrykersRunGateKind.Add => (long)_soldiers + selected.Value,
                    StrykersRunGateKind.Subtract => (long)_soldiers - selected.Value,
                    _ => (long)_soldiers * selected.Value
                };
                _soldiers = (int)Math.Clamp(count, 0, MaximumSoldiers);
                AddEffect(selected.Kind == StrykersRunGateKind.Subtract ? StrykersRunEffectKind.GatePenalty :
                    StrykersRunEffectKind.GateBoost, _squadX, SquadY);
            }
            _gates.RemoveAll(gate => gate.Row == row);
            if (_soldiers == 0) { EndRun(); return; }
        }
    }

    private void AddEffect(StrykersRunEffectKind kind, float x, float y)
    {
        if (_effects.Count == MaximumEffects) _effects.RemoveAt(0);
        _effects.Add(new(kind, x, y, _nextId++));
    }

    private void EndRun()
    {
        _phase = StrykersRunPhase.GameOver;
        _phaseSeconds = 0;
        _hasInput = false;
        _accumulator = 0;
    }

    private float Random()
    {
        _random ^= _random << 13;
        _random ^= _random >> 17;
        _random ^= _random << 5;
        return (_random & 0xffffff) / 16777216f;
    }

    private static float EnemyRadius(StrykersRunEnemyKind kind) => kind switch
    { StrykersRunEnemyKind.Boss => .075f, StrykersRunEnemyKind.Brute => .037f, _ => .023f };
    private static int EnemyContactDamage(StrykersRunEnemyKind kind) => kind switch
    { StrykersRunEnemyKind.Boss => 18, StrykersRunEnemyKind.Brute => 3, _ => 1 };

    private StrykersRunSnapshot CreateSnapshot() => new(_phase, _squadX, _soldiers, _score, _distance, _wave,
        _runSeconds, _phase == StrykersRunPhase.GameOver ? _phaseSeconds : 0,
        _phase == StrykersRunPhase.WaitingForInput && _hasPlayed,
        _phase switch
        {
            StrykersRunPhase.Ready => "Hold PLAY to begin",
            StrykersRunPhase.Starting => "Move along the control rail",
            StrykersRunPhase.WaitingForInput => _hasPlayed ? "Paused · return to the control rail" : "Touch the control rail to run",
            StrykersRunPhase.GameOver => "GAME OVER",
            _ => "Stryker's Run"
        }, Revision,
        Array.AsReadOnly(_enemies.Where(enemy => enemy.Health > 0).Select(enemy => new StrykersRunEnemySnapshot(
            enemy.Id, enemy.X, enemy.Y, enemy.Kind, enemy.Health, enemy.MaxHealth,
            (_runSeconds * (enemy.Kind == StrykersRunEnemyKind.Boss ? 1.2f : 2.5f) + enemy.Id * .173f) % 1)).ToArray()),
        Array.AsReadOnly(_gates.Select(gate => new StrykersRunGateSnapshot(gate.Id, gate.X, gate.Y, gate.Width, gate.Kind, gate.Value)).ToArray()),
        Array.AsReadOnly(_bullets.Select(bullet => new StrykersRunBulletSnapshot(bullet.Id, bullet.X, bullet.Y)).ToArray()),
        Array.AsReadOnly(_effects.Select(effect => new StrykersRunEffectSnapshot(effect.Kind, effect.X, effect.Y, effect.Age, effect.Seed)).ToArray()))
        {
            PhaseSeconds = _phaseSeconds,
            Objectives = Array.AsReadOnly(_objectives.Select(objective => new StrykersRunObjectiveSnapshot(
                objective.Id, objective.X, objective.Y, objective.Kind, objective.Health, objective.MaxHealth)).ToArray()),
            Ally = _ally is { } ally ? new StrykersRunAllySnapshot(
                StrykersRunFormation.DefenderPosition(_squadX).X, StrykersRunFormation.DefenderPosition(_squadX).Y,
                ally.Health, DefenderMaximumHealth, _runSeconds * 2.8f % 1, ally.AttackSeconds) : null
        };

    private void Change() { Revision++; _snapshot = null; }

    private sealed class Enemy(int id, float x, float y, StrykersRunEnemyKind kind, int health)
    {
        public readonly int Id = id, MaxHealth = health;
        public readonly StrykersRunEnemyKind Kind = kind;
        public readonly float Lane = x;
        public float X = x, Y = y;
        public Vector2 Previous = new(x, y);
        public int Health = health;
    }
    private sealed class Gate(int id, int row, float x, StrykersRunGateKind kind, int value)
    {
        public readonly int Id = id, Row = row, Value = value;
        public readonly StrykersRunGateKind Kind = kind;
        public readonly float X = x, Width = .40f;
        public float Y = -.04f;
    }
    private sealed class Bullet(int id, float x, float y)
    {
        public readonly int Id = id;
        public readonly float X = x;
        public float Y = y;
    }
    private sealed class Objective(int id, float x, float y, StrykersRunObjectiveKind kind, int health)
    {
        public readonly int Id = id, MaxHealth = health;
        public readonly StrykersRunObjectiveKind Kind = kind;
        public readonly float X = x;
        public float Y = y;
        public Vector2 Previous = new(x, y);
        public int Health = health;
    }
    private sealed class Ally
    {
        public int Health = DefenderMaximumHealth;
        // Seconds since the last strike; one means no strike animation is active.
        public float AttackSeconds = 1, AttackRemaining;
    }
    private sealed class Effect(StrykersRunEffectKind kind, float x, float y, int seed)
    {
        public readonly StrykersRunEffectKind Kind = kind;
        public readonly float X = x, Y = y;
        public readonly int Seed = seed;
        public float Age;
    }
}
