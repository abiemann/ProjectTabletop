using System.Numerics;

namespace ProjectTabletop.Interaction;

/// <summary>Shared world-space placement and aggregate contact footprint of the hero and recruits.</summary>
public static class StrykersRunFormation
{
    public const float BodyHalfWidth = 24f / 900;
    public const float DefenderHalfWidth = .072f;

    public static int StompingLeg(int leftHealth, int rightHealth, float targetX) =>
        leftHealth <= 0 ? 1 : rightHealth <= 0 ? 0 : targetX < .5f ? 0 : 1;

    public static float StompFootY(float targetY, float activeSeconds) =>
        targetY - Math.Max(0, activeSeconds - .20f) * .325f;

    public static Vector2 DefenderPosition(float heroX) => new(
        Math.Clamp(heroX, StrykersRunGame.RoadLeft + DefenderHalfWidth,
            StrykersRunGame.RoadRight - DefenderHalfWidth), .66f);

    public static Vector2 TroopPosition(float heroX, int soldierCount, int troopIndex)
    {
        int troops = Math.Clamp(soldierCount, 0, StrykersRunGame.MaximumSoldiers) - 1;
        ArgumentOutOfRangeException.ThrowIfNegative(troopIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(troopIndex, troops);
        var layout = Layout(heroX, troops);
        int row = troopIndex / layout.Columns, column = troopIndex % layout.Columns;
        int inRow = Math.Min(layout.Columns, troops - row * layout.Columns);
        return new(layout.Center + (column - (inRow - 1) * .5f) * (34f / 900),
            StrykersRunGame.SquadY + (31 + row * 21) / 1600f);
    }

    public static (float Left, float Right) HorizontalBounds(float heroX, int soldierCount)
    {
        int troops = Math.Clamp(soldierCount, 0, StrykersRunGame.MaximumSoldiers) - 1;
        float left = heroX - BodyHalfWidth, right = heroX + BodyHalfWidth;
        if (troops > 0)
        {
            var layout = Layout(heroX, troops);
            left = Math.Min(left, layout.Center - layout.HalfWidth - BodyHalfWidth);
            right = Math.Max(right, layout.Center + layout.HalfWidth + BodyHalfWidth);
        }
        return (left, right);
    }

    private static (int Columns, float Center, float HalfWidth) Layout(float heroX, int troops)
    {
        int columns = Math.Min(9, Math.Max(1, (int)Math.Ceiling(Math.Sqrt(troops))));
        float halfWidth = (columns - 1) * (17f / 900);
        float center = Math.Clamp(heroX, StrykersRunGame.RoadLeft + halfWidth + BodyHalfWidth,
            StrykersRunGame.RoadRight - halfWidth - BodyHalfWidth);
        return (columns, center, halfWidth);
    }
}
