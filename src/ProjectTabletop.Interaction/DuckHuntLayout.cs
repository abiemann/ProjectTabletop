namespace ProjectTabletop.Interaction;

public readonly record struct DuckHuntFootPose(double PivotU, double PivotV, double Angle, double Lift);

/// <summary>Shared photographic duck geometry, in source-image pixels. The source
/// faces left; rendering and hit testing mirror it together when walking right.</summary>
public static class DuckHuntLayout
{
    public static readonly BoardRect ExitBounds = new(.20, .87, .155, .12);
    public const double PhotoPixelWidth = 1312, PhotoPixelHeight = 1199;

    // Follow the visible body/head/bill/tail rather than accepting transparent
    // photo corners or the open space beneath the bill and between the legs.
    private static readonly BoardAim[] BodySilhouette =
    [
        new(90, 338), new(71, 319), new(80, 299), new(205, 207), new(238, 170),
        new(272, 90), new(320, 47), new(380, 23), new(451, 24), new(504, 55),
        new(540, 99), new(564, 164), new(577, 248), new(570, 317), new(550, 372),
        new(654, 360), new(737, 380), new(850, 417), new(967, 474), new(1180, 560),
        new(1260, 607), new(1166, 586), new(1260, 655), new(1167, 665),
        new(1244, 695), new(1302, 709), new(1297, 743), new(1255, 774),
        new(1127, 799), new(1036, 842), new(914, 863), new(839, 889),
        new(728, 907), new(576, 891), new(450, 855), new(345, 806),
        new(270, 738), new(222, 658), new(202, 561), new(215, 479),
        new(257, 414), new(311, 363), new(345, 318), new(353, 286),
        new(336, 268), new(292, 266), new(218, 302), new(132, 332)
    ];
    private static readonly BoardAim[] RearFootSilhouette =
    [
        new(558, 876), new(619, 884), new(607, 965), new(605, 1008),
        new(642, 1047), new(648, 1062), new(630, 1066), new(606, 1052),
        new(604, 1105), new(582, 1132), new(562, 1120), new(441, 1138),
        new(423, 1113), new(362, 1101), new(378, 1080), new(421, 1065),
        new(539, 1041), new(553, 1007), new(560, 959)
    ];
    private static readonly BoardAim[] NearFootSilhouette =
    [
        new(780, 888), new(831, 893), new(826, 969), new(810, 1058),
        new(831, 1090), new(824, 1108), new(804, 1095), new(808, 1137),
        new(783, 1172), new(762, 1180), new(690, 1176), new(613, 1165),
        new(597, 1148), new(619, 1131), new(716, 1111), new(758, 1078),
        new(767, 1000)
    ];

    public static double PhotoWidth(double boardAspect) =>
        Math.Min(.36, .40 / boardAspect * PhotoPixelWidth / PhotoPixelHeight);

    public static BoardRect PhotoBounds(DuckHuntSnapshot duck, double boardAspect)
    {
        double width = PhotoWidth(boardAspect), height = width * boardAspect * PhotoPixelHeight / PhotoPixelWidth;
        return new(duck.U - width / 2, duck.V - height / 2, width, height);
    }

    public static DuckHuntFootPose FootPose(DuckHuntSnapshot duck, bool rearFoot)
    {
        double phase = duck.WalkPhase + (rearFoot ? Math.PI : 0);
        return new((rearFoot ? 587 : 801) / PhotoPixelWidth,
            (rearFoot ? 871 : 890) / PhotoPixelHeight, .20 * Math.Sin(phase),
            -20 * Math.Max(0, Math.Cos(phase)) / PhotoPixelHeight);
    }

    public static bool ContainsDuck(BoardAim point, DuckHuntSnapshot duck, double boardAspect = 1)
    {
        if (!duck.DuckVisible || !double.IsFinite(point.U) || !double.IsFinite(point.V) ||
            !double.IsFinite(boardAspect) || boardAspect <= 0) return false;
        var bounds = PhotoBounds(duck, boardAspect);
        double x = (point.U - bounds.X) / bounds.Width;
        if (duck.Direction > 0) x = 1 - x;
        var pixel = new BoardAim(x * PhotoPixelWidth, (point.V - bounds.Y) / bounds.Height * PhotoPixelHeight);
        return PolygonContains(BodySilhouette, pixel) ||
            FootContains(RearFootSilhouette, pixel, FootPose(duck, rearFoot: true)) ||
            FootContains(NearFootSilhouette, pixel, FootPose(duck, rearFoot: false));
    }

    private static bool FootContains(BoardAim[] outline, BoardAim point, DuckHuntFootPose pose)
    {
        double x = point.U - pose.PivotU * PhotoPixelWidth;
        double y = point.V - (pose.PivotV + pose.Lift) * PhotoPixelHeight;
        double c = Math.Cos(pose.Angle), s = Math.Sin(pose.Angle);
        return PolygonContains(outline, new(x * c + y * s + pose.PivotU * PhotoPixelWidth,
            -x * s + y * c + pose.PivotV * PhotoPixelHeight));
    }

    private static bool PolygonContains(BoardAim[] outline, BoardAim point)
    {
        bool inside = false;
        for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
        {
            var a = outline[i]; var b = outline[j];
            if ((a.V > point.V) != (b.V > point.V) &&
                point.U < (b.U - a.U) * (point.V - a.V) / (b.V - a.V) + a.U) inside = !inside;
        }
        return inside;
    }
}
