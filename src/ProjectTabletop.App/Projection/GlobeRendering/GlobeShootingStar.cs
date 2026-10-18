using System.Numerics;

namespace ProjectTabletop.App.Projection.GlobeRendering;

/// <summary>A single time-derived meteor; physical lengths use the board's short edge as one unit.</summary>
internal readonly record struct GlobeShootingStarSample(
    Vector2 HeadUv, Vector2 Direction, Vector2 PhysicalScale, float TailLength, float Opacity)
{
    public bool Active => Opacity > 0;
}

internal static class GlobeShootingStar
{
    private const double FirstSlotSeconds = 12;
    private const double SlotSeconds = 16;

    // The jitter varies consecutive starts by 10–22 seconds without a timer,
    // mutable random generator, event history, or queued particles.
    internal static double GetStartTime(int eventIndex) =>
        FirstSlotSeconds + eventIndex * SlotSeconds + Random(eventIndex, 1) * 6;

    internal static double GetDuration(int eventIndex) => .8 + Random(eventIndex, 2) * .4;

    internal static GlobeShootingStarSample Sample(double elapsedSeconds, double boardAspect)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < FirstSlotSeconds) return default;
        double slot = Math.Floor((elapsedSeconds - FirstSlotSeconds) / SlotSeconds);
        if (slot > int.MaxValue) return default;
        int index = (int)slot;
        double progress = (elapsedSeconds - GetStartTime(index)) / GetDuration(index);
        if (progress <= 0 || progress >= 1) return default;

        float aspect = double.IsFinite(boardAspect) ? (float)Math.Clamp(boardAspect, .2, 5) : 1;
        var physicalScale = new Vector2(Math.Max(aspect, 1), Math.Max(1 / aspect, 1));
        float angle = (.18f + .42f * Random(index, 3));
        var direction = new Vector2(MathF.Cos(angle) * (Random(index, 4) < .5f ? -1 : 1), MathF.Sin(angle));
        float tailLength = .08f + .04f * Random(index, 5);
        Vector2 travel = direction * (.24f + .18f * Random(index, 6)) / physicalScale;

        // Reserve room for the complete tail on its starting side. Downward
        // paths stay above v=.56, including the glow, and below the title.
        float tailU = tailLength * Math.Abs(direction.X) / physicalScale.X;
        float minimumU = .06f + (direction.X > 0 ? tailU : -travel.X);
        float maximumU = .94f - (direction.X > 0 ? travel.X : tailU);
        var start = new Vector2(minimumU + (maximumU - minimumU) * Random(index, 7),
            .16f + .12f * Random(index, 8));
        float phase = (float)progress;
        float opacity = Smooth(phase / .12f) * Smooth((1 - phase) / .28f);
        return new(start + travel * phase, direction, physicalScale, tailLength, opacity);
    }

    private static float Smooth(float value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * (3 - 2 * value);
    }

    private static float Random(int eventIndex, uint salt)
    {
        uint value = unchecked((uint)eventIndex * 747796405u + salt * 2891336453u + 277803737u);
        value = unchecked((value ^ (value >> 16)) * 0x7feb352du);
        value = unchecked((value ^ (value >> 15)) * 0x846ca68bu);
        value ^= value >> 16;
        return (value >> 8) * (1f / 16777216);
    }
}
