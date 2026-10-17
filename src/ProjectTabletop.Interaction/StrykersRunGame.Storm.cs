namespace ProjectTabletop.Interaction;

public readonly record struct StrykersRunCraterSnapshot(int Id, float X, float Y, float Age);

public sealed partial class StrykersRunGame
{
    public const float StormCloudStartY = BossY + .08f, StormCloudEndY = 1.04f;
    public const float StormCloudWarningSeconds = .75f, StormCloudTravelSeconds = 3.6f;
    public const float StormSpiderDelaySeconds = .35f, StormReturnFlightSeconds = .65f, StormReturnSeconds = .90f;
    public const float StormCloudPulseSeconds = .4f;
    public const float StormCraterScrollScale = .0125f;
    // The renderer can raise smoke 228 native pixels above the ground anchor.
    // At 1.06 the entire effect is below the .90 road/rail edge, with 28 pixels
    // to spare. Keep this shared cutoff beyond the full smoke, not just the hole.
    public const float StormCraterCullY = 1.06f;
    private const int StormCloudWarningTicks = 90, StormCloudTravelTicks = 432;
    private const int StormCloudDurationTicks = StormCloudWarningTicks + StormCloudTravelTicks;
    private const int StormSpiderDelayTicks = 42, StormReturnFlightTicks = 78, StormReturnTicks = 108;
    private const int StormCloudPulseTicks = 48, StormAttackIntervalTicks = 432;
    // A returned strike takes about one eighth of this encounter's entry HP;
    // only a handful of spiders can die before their finite boss battle ends.
    private readonly List<StormCrater> _stormCraters = new(10);

    private static float StormLane(int wave) => (wave % 3) switch { 0 => .24f, 1 => .76f, _ => .5f };

    private void StepStormCloud(Hazard cloud)
    {
        if (cloud.Ticks < cloud.WarningTicks) return;
        float progress = Math.Clamp((cloud.Ticks - cloud.WarningTicks) / (float)StormCloudTravelTicks, 0, 1);
        cloud.Y = StormCloudStartY + (StormCloudEndY - StormCloudStartY) * progress;
        // A spider arrives only after the rain starts, in another lane so the
        // player can shoot it without standing beneath this cloud's lightning.
        if (cloud.Ticks == cloud.WarningTicks + StormSpiderDelayTicks)
        {
            float spiderLane = cloud.X < .4f ? .76f : cloud.X > .6f ? .5f : .24f;
            AddEnemy(StrykersRunEnemyKind.ElectricSpider, spiderLane, StormCloudStartY);
        }
        if ((cloud.Ticks - cloud.WarningTicks) % StormCloudPulseTicks == 0)
            DamageHazard(cloud);
    }

    private void ReleaseStormReturn(Enemy spider)
    {
        if (_stage != StrykersRunStage.Boss || _boss is not { Kind: StrykersRunBossKind.StormRevenant } boss) return;
        _stormCraters.Add(new(spider.Id, spider.X, spider.Y, _runSeconds, _distance));
        // Only a damage defeat reaches here. Touching the squad/Bulwark or
        // walking offstage consumes a spider without awarding a return strike.
        // One bounded hazard represents the visible cluster of small bolts.
        AddHazard(StrykersRunHazardKind.ReturnLightning, spider.X, spider.Y, BossX, BossY,
            StormReturnFlightTicks, StormReturnTicks, 0, Math.Max(1, boss.MaxHealth / 8));
    }

    private void CullStormCraters()
    {
        for (int i = _stormCraters.Count - 1; i >= 0; i--)
            if (CraterY(_stormCraters[i]) > StormCraterCullY) _stormCraters.RemoveAt(i);
    }

    internal void ClearStormCraters()
    {
        if (_stormCraters.Count == 0) return;
        _stormCraters.Clear();
        Change();
    }

    private float CraterY(StormCrater crater) => crater.Y + (_distance - crater.Distance) * StormCraterScrollScale;
    private readonly record struct StormCrater(int Id, float X, float Y, float CreatedAt, float Distance);
}
