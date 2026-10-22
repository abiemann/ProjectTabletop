namespace ProjectTabletop.Interaction;

/// <summary>Board coordinates; length is in board-height units and angle zero
/// points up. WidthScale rolls the feather about its shaft as it flutters.</summary>
public readonly record struct DuckHuntFeatherPose(double U, double V, double Length,
    double Angle, double WidthScale, int Tint, bool Settled);

/// <summary>A small, bounded set of feathers retained until the next round.
/// Positions are evaluated from time rather than integrated per render frame.</summary>
public sealed class DuckHuntFeathers
{
    public const int FeathersPerHit = 8;
    public const int MaximumCount = FeathersPerHit * DuckHuntGame.HitsToWin;
    private readonly Feather[] _feathers = new Feather[MaximumCount];
    public int Count { get; private set; }
    public bool IsAnimating { get; private set; }

    private struct Feather
    {
        public DateTimeOffset BornAt;
        public double StartU, StartV, LandU, LandV, Duration, Phase, Drift, Lift;
        public double StartAngle, LandAngle;
        public DuckHuntFeatherPose Pose;
    }

    public DuckHuntFeatherPose GetPose(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return _feathers[index].Pose;
    }

    internal void Clear()
    {
        Count = 0;
        IsAnimating = false;
    }

    internal void Spawn(DuckHuntSnapshot duck, double boardAspect, DateTimeOffset shotTime)
    {
        var bounds = DuckHuntLayout.PhotoBounds(duck, boardAspect);
        int end = Math.Min(Count + FeathersPerHit, MaximumCount);
        for (int i = Count; i < end; i++)
        {
            double length = .022 + .013 * Variation(i, 1);
            double bodyX = .48 + .31 * Variation(i, 2);
            if (duck.Direction > 0) bodyX = 1 - bodyX;
            double u = bounds.X + bounds.Width * bodyX;
            double v = bounds.Y + bounds.Height * (.43 + .24 * Variation(i, 3));
            // Half-length bounds contain every rotated silhouette, including its
            // short shaft, while the falling feather itself remains unmasked.
            double marginU = length * .55 / boardAspect;
            double landingU = Math.Clamp(u + (Variation(i, 4) - .5) * .12 / boardAspect,
                marginU, 1 - marginU);
            double landingV = Math.Min(.70 - length * .55,
                bounds.Y + bounds.Height * .985 + (Variation(i, 5) - .5) * .012);
            double angle = (Variation(i, 6) - .5) * 2.1;
            _feathers[i] = new Feather
            {
                BornAt = shotTime, StartU = u, StartV = v, LandU = landingU, LandV = landingV,
                Duration = 2 + Variation(i, 7), Phase = Math.Tau * Variation(i, 8),
                Drift = (.009 + .012 * Variation(i, 9)) / boardAspect,
                Lift = .009 + .009 * Variation(i, 10), StartAngle = angle,
                LandAngle = (Variation(i, 11) < .5 ? -1 : 1) * Math.PI / 2 +
                    (Variation(i, 12) - .5) * .4,
                Pose = new(u, v, length, angle, 1, i % 3, false)
            };
        }
        Count = end;
        IsAnimating = Count > 0;
    }

    internal bool Advance(DateTimeOffset now)
    {
        bool changed = false, animating = false;
        for (int i = 0; i < Count; i++)
        {
            ref var feather = ref _feathers[i];
            if (feather.Pose.Settled) continue;
            double t = Math.Clamp((now - feather.BornAt).TotalSeconds / feather.Duration, 0, 1);
            double envelope = Math.Sin(Math.PI * t);
            double ease = t * t * (3 - 2 * t);
            bool settled = t >= 1;
            var pose = feather.Pose with
            {
                U = feather.StartU + (feather.LandU - feather.StartU) * ease +
                    feather.Drift * envelope * Math.Sin(feather.Phase + Math.Tau * t * 1.5),
                V = feather.StartV + (feather.LandV - feather.StartV) * Math.Pow(t, 1.35) -
                    feather.Lift * envelope + .003 * envelope * Math.Sin(feather.Phase + Math.Tau * t * 2),
                Angle = feather.StartAngle + (feather.LandAngle - feather.StartAngle) * ease +
                    .7 * envelope * Math.Sin(feather.Phase + Math.Tau * t * 2),
                WidthScale = 1 - .65 * envelope * Math.Abs(Math.Sin(feather.Phase + Math.Tau * t * 2.5)),
                Settled = settled
            };
            changed |= pose != feather.Pose;
            feather.Pose = pose;
            animating |= !settled;
        }
        IsAnimating = animating;
        return changed;
    }

    internal void Delay(TimeSpan pause)
    {
        for (int i = 0; i < Count; i++)
            if (!_feathers[i].Pose.Settled) _feathers[i].BornAt += pause;
    }

    private static double Variation(int index, uint salt)
    {
        uint value = unchecked((uint)(index + 1) * 0x9e3779b9u + salt * 0x85ebca6bu);
        value ^= value >> 16;
        value = unchecked(value * 0x7feb352du);
        value ^= value >> 15;
        return (value & 0xffff) / 65535.0;
    }
}
