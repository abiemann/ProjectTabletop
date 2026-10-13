using System.Numerics;

namespace ProjectTabletop.Interaction;

public enum StrykersRunStage { Running, Boss, Intermission, Complete, BossEntrance }
public enum StrykersRunBossKind { BarrelWarden, SwarmCommander, StormRevenant, SkinWalker, Colossus }
public enum StrykersRunHazardKind { Barrel, BarrelBlast, Lightning, Tongue, PoisonGas, Stomp }

public readonly record struct StrykersRunBossSnapshot(StrykersRunBossKind Kind, float X, float Y,
    int Health, int MaxHealth, int Phase, float PhaseSeconds, float AttackSeconds,
    float AnimationPhase, int LeftLegHealth, int RightLegHealth)
{
    public float EntranceProgress { get; init; } = 1;
}
public readonly record struct StrykersRunHazardSnapshot(int Id, StrykersRunHazardKind Kind,
    float X, float Y, float TargetX, float TargetY, float Age, float WarningSeconds, float Duration, float Radius)
{
    public bool IsWarning => Age < WarningSeconds;
}

public sealed partial class StrykersRunGame
{
    public const int LevelCount = 5, MaximumHazards = 12;
    public const float LevelDurationSeconds = 60, LevelIntermissionSeconds = 2;
    public const float BossEntranceSeconds = 3, BossEntranceStartY = .12f;
    public const float BossX = .5f, BossY = .30f;
    public const float BossLeftLegX = .32f, BossRightLegX = .68f, BossLegY = .36f;
    public const float SkinTransformationSeconds = 1.1f;
    private const int LevelTicks = 60 * 120, IntermissionTicks = 2 * 120, BossEntranceTicks = 3 * 120;
    private readonly List<Hazard> _hazards = new(MaximumHazards);
    private StrykersRunStage _stage;
    private int _level = 1, _levelTicks, _stageTicks;
    private Boss? _boss;

    private void ResetCampaign()
    {
        _level = 1;
        _levelTicks = _stageTicks = 0;
        _stage = StrykersRunStage.Running;
        _boss = null;
        _hazards.Clear();
    }

    // Level and attack boundaries use simulation ticks rather than accumulated
    // floats: 7,200 active steps are exactly one minute, independent of rendering.
    private bool StepCampaign()
    {
        _stageTicks++;
        if (_stage == StrykersRunStage.BossEntrance)
        {
            if (_stageTicks >= BossEntranceTicks)
            {
                _stage = StrykersRunStage.Boss;
                _stageTicks = 0;
            }
            // The boss must arrive before either side fires or takes damage.
            // Returning here also keeps the road and every encounter stationary.
            return false;
        }
        if (_stage == StrykersRunStage.Intermission)
        {
            if (_stageTicks >= IntermissionTicks)
            {
                _level++;
                _levelTicks = _stageTicks = 0;
                _stage = StrykersRunStage.Running;
                _enemyRemaining = .75f;
                _gateRemaining = 2;
                _objectiveRemaining = 2;
                _rushRemaining = 12;
                _fireRemaining = 0;
            }
            return false;
        }
        if (_stage == StrykersRunStage.Running)
        {
            _levelTicks++;
            _distance = ((_level - 1) * LevelTicks + _levelTicks) * RoadSpeed / 120f;
            _wave = 1 + (_level - 1) * 2 + _levelTicks / (18 * 120);
            if (_levelTicks >= LevelTicks)
            {
                BeginBoss();
                return false;
            }
        }
        else if (_stage == StrykersRunStage.Boss)
        {
            StepHazards();
            if (_phase == StrykersRunPhase.GameOver) return false;
            StepBoss();
        }
        return true;
    }

    private void BeginBoss()
    {
        _stage = StrykersRunStage.BossEntrance;
        _stageTicks = 0;
        _levelTicks = LevelTicks;
        ClearEncounters();
        // Reinforcements shorten a fight, while a lone survivor can still win.
        // Scaling once at entry prevents losing troops from healing the boss.
        int shooters = Math.Clamp((_soldiers + 3) / 4, 1, 12);
        _boss = new((StrykersRunBossKind)(_level - 1), (90 + _level * 20) * (shooters + 1) / 2);
        _fireRemaining = 0;
    }

    private void ClearEncounters()
    {
        _enemies.Clear();
        _gates.Clear();
        _objectives.Clear();
        _bullets.Clear();
        _hazards.Clear();
    }

    private void StepBoss()
    {
        if (_boss is not { } boss || _stage != StrykersRunStage.Boss) return;
        boss.PhaseTicks++;
        boss.AttackTicks++;
        if (boss.Kind == StrykersRunBossKind.SkinWalker && boss.Phase == 2 && boss.PhaseTicks < 132) return;
        if (--boss.AttackRemaining > 0) return;
        boss.AttackTicks = 0;
        switch (boss.Kind)
        {
            case StrykersRunBossKind.BarrelWarden:
                // Lock the lane at throw time. The barrel never follows a dodge,
                // and its fuse is measured from this exact simulation step.
                AddHazard(StrykersRunHazardKind.Barrel, _squadX, BossY, _squadX, SquadY, 0, 360, .065f, 5);
                boss.AttackRemaining = 276;
                break;
            case StrykersRunBossKind.SwarmCommander:
                SpawnCommandedWave(boss);
                boss.AttackRemaining = 330;
                break;
            case StrykersRunBossKind.StormRevenant:
                AddHazard(StrykersRunHazardKind.Lightning, BossX, BossY, _squadX, SquadY, 144, 180, .025f, 5);
                boss.AttackRemaining = 306;
                break;
            case StrykersRunBossKind.SkinWalker when boss.Phase == 1:
                AddHazard(StrykersRunHazardKind.Tongue, BossX, BossY, _squadX, SquadY, 90, 126, .018f, 4);
                boss.AttackRemaining = 264;
                break;
            case StrykersRunBossKind.SkinWalker:
                AddHazard(StrykersRunHazardKind.PoisonGas, BossX, BossY, _squadX, SquadY, 96, 540, .09f, 1);
                // Let the previous cloud clear before the next one reaches the
                // squad, so x-only dodges cannot be trapped against a road edge.
                boss.AttackRemaining = 360;
                break;
            case StrykersRunBossKind.Colossus:
                AddHazard(StrykersRunHazardKind.Stomp, _squadX, SquadY, _squadX, SquadY, 138, 186, .12f, 8);
                boss.AttackRemaining = 252;
                break;
        }
    }

    private void SpawnCommandedWave(Boss boss)
    {
        // The commander remains behind his wave. Leave a visibly open lane,
        // alternating it between salvos, so x-only steering can always evade.
        bool left = boss.Waves++ % 2 == 0;
        for (int row = 0; row < 2; row++)
        for (int column = 0; column < 4 && _enemies.Count < MaximumEnemies; column++)
        {
            float x = (left ? .20f : .55f) + column * .065f;
            AddEnemy(column == 1 && row == 1 ? StrykersRunEnemyKind.Brute : StrykersRunEnemyKind.Runner,
                x, BossY + .08f + row * .045f);
        }
    }

    private void AddHazard(StrykersRunHazardKind kind, float x, float y, float targetX, float targetY,
        int warningTicks, int durationTicks, float radius, int damage)
    {
        if (_hazards.Count >= MaximumHazards) return;
        _hazards.Add(new(_nextId++, kind, x, y, targetX, targetY, warningTicks, durationTicks, radius, damage));
    }

    private void StepHazards()
    {
        for (int i = _hazards.Count - 1; i >= 0; i--)
        {
            var hazard = _hazards[i];
            hazard.Ticks++;
            if (hazard.Kind == StrykersRunHazardKind.Barrel)
            {
                float travel = Math.Min(1, hazard.Ticks / 360f);
                hazard.Y = BossY + (hazard.TargetY - BossY) * travel;
                if (hazard.Ticks == 360)
                {
                    hazard.Kind = StrykersRunHazardKind.BarrelBlast;
                    hazard.Ticks = 0;
                    hazard.DurationTicks = 54;
                    hazard.Radius = .14f;
                    DamageHazard(hazard);
                    if (_phase == StrykersRunPhase.GameOver) return;
                }
                continue;
            }
            if (hazard.Kind == StrykersRunHazardKind.PoisonGas)
            {
                float travel = Math.Clamp((hazard.Ticks - hazard.WarningTicks) / 240f, 0, 1);
                hazard.X = BossX + (hazard.TargetX - BossX) * travel;
                hazard.Y = BossY + (hazard.TargetY - BossY) * travel;
                // The cloud poisons only while it visibly overlaps a defender.
                // Leaving it stops damage immediately; each cloud has a bounded tick rate.
                if (hazard.Ticks >= hazard.WarningTicks && --hazard.DamageRemaining <= 0)
                {
                    if (DamageHazard(hazard)) hazard.DamageRemaining = 66;
                    if (_phase == StrykersRunPhase.GameOver) return;
                }
            }
            else if (hazard.Kind != StrykersRunHazardKind.BarrelBlast && hazard.Ticks == hazard.WarningTicks)
            {
                DamageHazard(hazard);
                if (_phase == StrykersRunPhase.GameOver) return;
            }
            if (hazard.Ticks >= hazard.DurationTicks) _hazards.RemoveAt(i);
        }
    }

    private bool DamageHazard(Hazard hazard)
    {
        bool beam = hazard.Kind is StrykersRunHazardKind.Lightning or StrykersRunHazardKind.Tongue;
        var origin = new Vector2(hazard.X, hazard.Y);
        var target = new Vector2(hazard.TargetX, hazard.TargetY);
        if (_ally is { } ally && HazardTouches(StrykersRunFormation.DefenderPosition(_squadX),
            StrykersRunFormation.DefenderHalfWidth, origin, target, hazard.Radius, beam))
        {
            ally.Health = Math.Max(0, ally.Health - hazard.Damage);
            var position = StrykersRunFormation.DefenderPosition(_squadX);
            AddEffect(ally.Health == 0 ? StrykersRunEffectKind.AllyLost : StrykersRunEffectKind.Hit, position.X, position.Y);
            if (ally.Health == 0) _ally = null;
            return true;
        }
        bool hit = HazardTouches(new(_squadX, SquadY), StrykersRunFormation.BodyHalfWidth,
            origin, target, hazard.Radius, beam);
        for (int i = 0; !hit && i < _soldiers - 1; i++)
            hit = HazardTouches(StrykersRunFormation.TroopPosition(_squadX, _soldiers, i),
                StrykersRunFormation.BodyHalfWidth, origin, target, hazard.Radius, beam);
        if (!hit) return false;
        _soldiers = Math.Max(0, _soldiers - hazard.Damage);
        AddEffect(StrykersRunEffectKind.SoldierLost, _squadX, SquadY);
        if (_soldiers == 0) EndRun();
        return true;
    }

    private static bool HazardTouches(Vector2 point, float bodyRadius, Vector2 origin, Vector2 target, float radius, bool beam)
    {
        var nearest = origin;
        if (beam)
        {
            var segment = target - origin;
            float along = segment.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(point - origin, segment) / segment.LengthSquared(), 0, 1) : 0;
            nearest += segment * along;
        }
        return Vector2.DistanceSquared(point, nearest) <= (radius + bodyRadius) * (radius + bodyRadius);
    }

    private bool FindBossContact(Vector2 start, Vector2 motion, ref float nearest, out int part)
    {
        part = -1;
        if (_boss is not { } boss || _stage != StrykersRunStage.Boss) return false;
        if (boss.Kind != StrykersRunBossKind.Colossus)
        {
            if (Sweep(start - new Vector2(BossX, BossY), motion, .079f, out float contact) && contact < nearest)
            { nearest = contact; part = 0; }
        }
        else
        {
            if (boss.LeftLegHealth > 0 && Sweep(start - BossPartPosition(boss, 1), motion, .069f, out float left) && left < nearest)
            { nearest = left; part = 1; }
            if (boss.RightLegHealth > 0 && Sweep(start - BossPartPosition(boss, 2), motion, .069f, out float right) && right < nearest)
            { nearest = right; part = 2; }
        }
        return part >= 0;
    }

    private Vector2 BossPartPosition(Boss boss, int part)
    {
        if (part == 0) return new(BossX, BossY);
        for (int i = _hazards.Count - 1; i >= 0; i--)
        {
            var hazard = _hazards[i];
            if (hazard.Kind != StrykersRunHazardKind.Stomp || hazard.Ticks < hazard.WarningTicks ||
                hazard.Ticks >= hazard.DurationTicks ||
                StrykersRunFormation.StompingLeg(boss.LeftLegHealth, boss.RightLegHealth, hazard.TargetX) != part - 1) continue;
            return new(hazard.TargetX, StrykersRunFormation.StompFootY(hazard.TargetY,
                (hazard.Ticks - hazard.WarningTicks) / 120f));
        }
        return new(part == 1 ? BossLeftLegX : BossRightLegX, BossLegY);
    }

    private void DamageBoss(int part)
    {
        if (_boss is not { } boss || _stage != StrykersRunStage.Boss) return;
        if (boss.Kind == StrykersRunBossKind.SkinWalker && boss.Phase == 2 && boss.PhaseTicks < 132) return;
        Vector2 hitPosition = BossPartPosition(boss, part);
        if (boss.Kind == StrykersRunBossKind.Colossus)
        {
            if (part == 1) boss.LeftLegHealth = Math.Max(0, boss.LeftLegHealth - 1);
            else if (part == 2) boss.RightLegHealth = Math.Max(0, boss.RightLegHealth - 1);
            boss.Health = boss.LeftLegHealth + boss.RightLegHealth;
            // Breaking a leg interrupts its pending/active stomp instead of
            // transferring that already locked attack to the surviving leg.
            if (part == 1 && boss.LeftLegHealth == 0 || part == 2 && boss.RightLegHealth == 0)
                _hazards.RemoveAll(hazard => hazard.Kind == StrykersRunHazardKind.Stomp);
        }
        else boss.Health = Math.Max(0, boss.Health - 1);
        AddEffect(StrykersRunEffectKind.Hit, hitPosition.X, hitPosition.Y);
        if (boss.Health == 0)
        {
            AddEffect(StrykersRunEffectKind.Defeat, BossX, BossY);
            _score = (int)Math.Min(int.MaxValue, (long)_score + _level * 500);
            _boss = null;
            ClearEncounters();
            _stageTicks = 0;
            if (_level == LevelCount)
            {
                _stage = StrykersRunStage.Complete;
                _phase = StrykersRunPhase.Victory;
                _phaseSeconds = 0;
                _hasInput = false;
                _accumulator = 0;
            }
            else _stage = StrykersRunStage.Intermission;
        }
        else if (boss.Kind == StrykersRunBossKind.SkinWalker && boss.Phase == 1 && boss.Health * 2 <= boss.MaxHealth)
        {
            boss.Phase = 2;
            boss.PhaseTicks = 0;
            boss.AttackRemaining = 30;
            _hazards.Clear();
        }
    }

    private StrykersRunBossSnapshot? CreateBossSnapshot()
    {
        if (_boss is not { } boss) return null;
        float entrance = _stage == StrykersRunStage.BossEntrance ? _stageTicks / (float)BossEntranceTicks : 1;
        return new(boss.Kind, BossX, BossEntranceStartY + (BossY - BossEntranceStartY) * entrance,
            boss.Health, boss.MaxHealth, boss.Phase, boss.PhaseTicks / 120f, boss.AttackTicks / 120f,
            _stageTicks / 120f % 1, boss.LeftLegHealth, boss.RightLegHealth) { EntranceProgress = entrance };
    }

    private string CampaignBanner() => _stage switch
    {
        StrykersRunStage.BossEntrance or StrykersRunStage.Boss => _boss?.Kind switch
        {
            StrykersRunBossKind.BarrelWarden => "BARREL WARDEN · dodge the burning barrels",
            StrykersRunBossKind.SwarmCommander => "SWARM COMMANDER · break through the minions",
            StrykersRunBossKind.StormRevenant => "STORM REVENANT · dodge the lightning charge",
            StrykersRunBossKind.SkinWalker when _boss.Phase == 2 => "SKIN WALKER · avoid the poison clouds",
            StrykersRunBossKind.SkinWalker => "SKIN WALKER · dodge the tongue",
            _ => "COLOSSUS · shoot both legs, evade the red swirls"
        },
        StrykersRunStage.Intermission => $"LEVEL {_level} CLEAR · survivors advance",
        StrykersRunStage.Complete => "CAMPAIGN COMPLETE",
        _ => $"LEVEL {_level} / {LevelCount}"
    };

    private sealed class Boss(StrykersRunBossKind kind, int health)
    {
        public readonly StrykersRunBossKind Kind = kind;
        public readonly int MaxHealth = health;
        public int Health = health, Phase = 1, PhaseTicks, AttackTicks, AttackRemaining = 144, Waves;
        public int LeftLegHealth = kind == StrykersRunBossKind.Colossus ? health / 2 : 0;
        public int RightLegHealth = kind == StrykersRunBossKind.Colossus ? health - health / 2 : 0;
    }

    private sealed class Hazard(int id, StrykersRunHazardKind kind, float x, float y, float targetX, float targetY,
        int warningTicks, int durationTicks, float radius, int damage)
    {
        public readonly int Id = id, WarningTicks = warningTicks, Damage = damage;
        public readonly float TargetX = targetX, TargetY = targetY;
        public StrykersRunHazardKind Kind = kind;
        public float X = x, Y = y, Radius = radius;
        public int Ticks, DurationTicks = durationTicks, DamageRemaining;
    }
}
