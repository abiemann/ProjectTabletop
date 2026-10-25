namespace ProjectTabletop.Interaction;

public readonly record struct DuckHuntFlightWingPose(
    double PivotU, double PivotV, double Angle, double Length);

/// <summary>One physically square flight rig shared by drawing and shot testing.
/// Source artwork faces right; the complete rig is mirrored for leftward flight.</summary>
public static class DuckHuntFlightLayout
{
    public static readonly BoardRect StartBounds = new(.37, .43, .26, .14);
    public static readonly BoardRect BodyArtworkBounds = new(.04, .5245, .92, .251);
    public static readonly BoardRect BodySourceBounds = new(58, 178, 1870, 510);
    public const double WingRootU = 820, WingRootV = 1435, WingTopV = 27;
    public const double BodyCenterU = .50, BodyCenterV = .65;

    // Follow the alpha silhouettes rather than accepting the empty rectangular
    // corners or the sky beneath the neck. Coordinates are native asset pixels.
    private static readonly BoardAim[] BodySilhouette =
    [
        new(58, 489), new(155, 465), new(279, 450), new(488, 355), new(694, 288),
        new(808, 265), new(937, 257), new(1042, 270), new(1180, 306), new(1286, 307),
        new(1363, 268), new(1444, 220), new(1529, 181), new(1624, 181), new(1694, 207),
        new(1734, 263), new(1746, 286), new(1803, 329), new(1892, 366), new(1928, 388),
        new(1924, 405), new(1860, 403), new(1740, 379), new(1674, 366), new(1535, 370),
        new(1474, 390), new(1449, 440), new(1412, 484), new(1319, 547), new(1222, 588),
        new(1093, 636), new(930, 671), new(781, 688), new(664, 678), new(539, 649),
        new(439, 623), new(410, 642), new(337, 628), new(255, 621), new(217, 597),
        new(224, 578), new(142, 586), new(104, 563), new(80, 534), new(98, 508), new(58, 496)
    ];
    private static readonly BoardAim[] WingSilhouette =
    [
        new(392, 27), new(467, 115), new(556, 265), new(643, 404), new(725, 518),
        new(791, 658), new(849, 793), new(906, 937), new(947, 1090), new(962, 1245),
        new(972, 1327), new(994, 1387), new(922, 1400), new(826, 1440), new(733, 1475),
        new(635, 1493), new(570, 1496), new(609, 1463), new(497, 1468), new(320, 1497),
        new(352, 1413), new(330, 1371), new(345, 1327), new(322, 1319), new(305, 1254),
        new(284, 1190), new(285, 1158), new(269, 1115), new(268, 1090), new(251, 1044),
        new(250, 1020), new(267, 978), new(239, 978), new(239, 958), new(252, 930),
        new(225, 910), new(224, 884), new(200, 852), new(199, 825), new(222, 805),
        new(186, 783), new(182, 746), new(198, 726), new(166, 698), new(160, 660),
        new(170, 646), new(180, 640), new(156, 612), new(144, 576), new(145, 558),
        new(178, 549), new(151, 524), new(138, 486), new(136, 452), new(149, 441),
        new(188, 458), new(156, 414), new(131, 365), new(134, 347), new(152, 346),
        new(204, 374), new(168, 306), new(150, 265), new(148, 244), new(165, 244),
        new(245, 288), new(192, 208), new(183, 175), new(187, 167), new(216, 181),
        new(298, 244), new(246, 151), new(237, 114), new(242, 106), new(269, 124),
        new(347, 195), new(303, 104), new(297, 70), new(302, 59), new(333, 79),
        new(413, 162), new(395, 70), new(391, 43)
    ];

    public static BoardRect SpriteBounds(DuckHuntFlightSnapshot bird, double boardAspect)
    {
        double width = Math.Min(.22, .25 / boardAspect) * bird.Scale;
        double height = width * boardAspect;
        return new(bird.U - width / 2, bird.V - height / 2, width, height);
    }

    // Angle zero points upward. A struck bird retains its exact flap phase.
    public static DuckHuntFlightWingPose NearWingPose(DuckHuntFlightSnapshot bird) =>
        new(.50, .60, -.30 - 1.10 * (1 - Math.Sin(bird.FlapPhase)), .53);

    public static DuckHuntFlightWingPose FarWingPose(DuckHuntFlightSnapshot bird) =>
        new(.54, .60, -.30 - 1.10 * (1 - Math.Sin(bird.FlapPhase + .38)),
            .53 * .78);

    public static bool ContainsBird(BoardAim point, DuckHuntFlightSnapshot bird, double boardAspect = 1)
    {
        if (bird.Phase != DuckHuntFlightPhase.Flying || !bird.Visible ||
            !double.IsFinite(point.U) || !double.IsFinite(point.V) ||
            !double.IsFinite(boardAspect) || boardAspect <= 0) return false;
        var bounds = SpriteBounds(bird, boardAspect);
        double u = (point.U - bounds.X) / bounds.Width;
        double v = (point.V - bounds.Y) / bounds.Height;
        if (bird.Direction < 0) u = 1 - u;
        var body = BodyArtworkBounds;
        var source = BodySourceBounds;
        if (BoardPolygon.Contains(BodySilhouette, new(
            source.X + (u - body.X) / body.Width * source.Width,
            source.Y + (v - body.Y) / body.Height * source.Height))) return true;
        return ContainsWing(u, v, NearWingPose(bird)) || ContainsWing(u, v, FarWingPose(bird));
    }

    private static bool ContainsWing(double u, double v, DuckHuntFlightWingPose wing)
    {
        double x = u - wing.PivotU, y = v - wing.PivotV;
        double s = Math.Sin(wing.Angle), c = Math.Cos(wing.Angle);
        double scale = wing.Length / (WingRootV - WingTopV);
        return BoardPolygon.Contains(WingSilhouette, new(WingRootU + (x * c + y * s) / scale,
            WingRootV + (-x * s + y * c) / scale));
    }
}
